'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  ExternalLink,
  Eye,
  Info,
  KeyRound,
  Loader2,
  Lock,
  PlugZap,
  RefreshCw,
  Search,
  SlidersHorizontal,
  Trash2,
  XCircle,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select, type SelectOption } from '@/components/ui/Select'
import { roleHeaders } from '@/lib/role'
import { useAccess } from '@/lib/useAccess'
import { expireSessionOn401 } from '@/lib/auth'
import { useSettingsDirty } from './SettingsDirtyContext'
import { EMBEDDING_KEYS, EmbeddingsSection } from '@/components/ai-learning/EmbeddingsSection'
import { Advanced } from '@/components/ui/Advanced'
import { AiProviderLogo } from '@/components/ui/AiProviderLogo'
import { useT, type MessageKey, type TFunction } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/* ---------- types (mirror VoiceMessagingEndpoints.AiProvidersPayload) ---------- */

type FieldKind = 'secret' | 'select' | 'text' | 'int' | 'model' | 'order'

interface FieldMeta {
  key: string
  label: string
  /** 'providers' (who answers) | 'provider' (one provider's own fields — drawn as tiles) | 'tunables' | … */
  group: string
  kind: FieldKind
  min?: number
  help?: string
}

interface ModelGroup {
  provider: string
  label: string
  models: string[]
}

/** One chat provider, as the catalog (LlmProviderCatalog) describes it — never its key. */
interface ProviderInfo {
  id: string
  label: string
  group: 'global' | 'asia' | 'self'
  key_field: string
  key_masked: string
  key_source: string
  key_optional: boolean
  key_url: string
  base_url_field: string | null
  base_url: string
  default_base_url: string
  endpoints: { label: string; url: string }[]
  model_field: string
  model: string
  default_model: string
  models: string[]
  vision: boolean
  configured: boolean
}

type Payload = {
  fields: FieldMeta[]
  chat_provider_options: string[]
  chat_model_options: ModelGroup[]
  providers: ProviderInfo[]
  source: Record<string, string>
  ai_available: boolean
  env_only_note: string
} & Record<string, unknown>

interface TestResult {
  ok: boolean
  model: string
  reply: string
  error: string
  ms: number
}

async function api<T>(path = '', init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/settings/ai-providers${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** The schema's field labels / help, translated by field key (the API's own
 *  English is the fallback for a field this client does not know). */
const FIELD_TEXT: Record<string, { label: MessageKey; help?: MessageKey }> = {
  elevenlabs_api_key: {
    label: 'settings.aiProviders.fields.elevenlabsKey',
    help: 'settings.aiProviders.fields.elevenlabsKeyHelp',
  },
  chat_provider_pref: { label: 'settings.aiProviders.fields.chatProvider' },
  chat_provider_order: { label: 'settings.aiProviders.fields.order', help: 'settings.aiProviders.fields.orderHelp' },
  chat_model: { label: 'settings.aiProviders.fields.chatModel', help: 'settings.aiProviders.fields.chatModelHelp' },
  ai_reviewer_model: {
    label: 'settings.aiProviders.fields.reviewerModel',
    help: 'settings.aiProviders.fields.reviewerModelHelp',
  },
  email_subject: { label: 'settings.aiProviders.fields.emailSubject' },
  flows_tick_seconds: { label: 'settings.aiProviders.fields.flowsTick' },
  whatsapp_health_interval: { label: 'settings.aiProviders.fields.healthInterval' },
  aicore_summary_interval: {
    label: 'settings.aiProviders.fields.summaryInterval',
    help: 'settings.aiProviders.fields.summaryIntervalHelp',
  },
  aicore_summary_idle_min: { label: 'settings.aiProviders.fields.summaryIdle' },
}

/** One line under each provider's name. */
const PROVIDER_BLURB: Record<string, MessageKey> = {
  openrouter: 'settings.aiProviders.catalog.openrouter',
  openai: 'settings.aiProviders.catalog.openai',
  anthropic: 'settings.aiProviders.catalog.anthropic',
  google: 'settings.aiProviders.catalog.google',
  mistral: 'settings.aiProviders.catalog.mistral',
  xai: 'settings.aiProviders.catalog.xai',
  groq: 'settings.aiProviders.catalog.groq',
  deepseek: 'settings.aiProviders.catalog.deepseek',
  qwen: 'settings.aiProviders.catalog.qwen',
  moonshot: 'settings.aiProviders.catalog.moonshot',
  zhipu: 'settings.aiProviders.catalog.zhipu',
  ilmu: 'settings.aiProviders.catalog.ilmu',
  local: 'settings.aiProviders.catalog.local',
}

const GROUPS: { id: ProviderInfo['group']; lbl: MessageKey }[] = [
  { id: 'global', lbl: 'settings.aiProviders.sections.global' },
  { id: 'asia', lbl: 'settings.aiProviders.sections.asia' },
  { id: 'self', lbl: 'settings.aiProviders.sections.self' },
]

/** Answer-mode fields kept up front; the rest of the schema's non-provider
 *  fields sit under "Advanced settings" with the embeddings. */
const ANSWER_KEYS = ['chat_provider_pref', 'chat_model', 'chat_provider_order']

const fieldLabel = (t: TFunction, f: FieldMeta) => (FIELD_TEXT[f.key] ? t(FIELD_TEXT[f.key].label) : f.label)
const fieldHelp = (t: TFunction, f: FieldMeta) => {
  const k = FIELD_TEXT[f.key]?.help
  return k ? t(k) : f.help
}

/** Where a value comes from — a dashboard row, the server's .env, or nothing. */
function SourceChip({ src, tour }: { src?: string; tour?: boolean }) {
  const t = useT()
  const map: Record<string, { lbl: MessageKey; cls: string }> = {
    settings: { lbl: 'settings.aiProviders.source.settings', cls: 'bg-green/15 text-[var(--green-ink)]' },
    env: { lbl: 'settings.aiProviders.source.env', cls: 'bg-amber/15 text-[var(--amber-ink)]' },
    '': { lbl: 'settings.aiProviders.source.none', cls: 'bg-fill text-text-muted' },
  }
  const s = map[src || ''] || map['']
  return (
    <span
      data-tour={tour ? 'aiproviders-source' : undefined}
      className={`shrink-0 rounded-full px-2 py-px text-[10px] font-bold ${s.cls}`}
    >
      {t(s.lbl)}
    </span>
  )
}

const CUSTOM = '__custom__'

/**
 * Model picker: a dropdown grouped by provider plus "Custom…" (any model id).
 * Empty = the provider's default, named in `emptyLabel`.
 */
function ModelPicker({
  value,
  catalog,
  disabled,
  emptyLabel,
  ariaLabel,
  onChange,
}: {
  value: string
  catalog: ModelGroup[]
  disabled: boolean
  emptyLabel: string
  ariaLabel: string
  onChange: (v: string) => void
}) {
  const t = useT()
  const known = useMemo(() => catalog.flatMap((g) => g.models), [catalog])
  const [forceCustom, setForceCustom] = useState(false)
  const isCustom = forceCustom || (value !== '' && !known.includes(value))
  const grouped = catalog.length > 1

  return (
    <>
      <Select
        value={isCustom ? CUSTOM : value}
        disabled={disabled}
        onChange={(v) => {
          if (v === CUSTOM) setForceCustom(true)
          else {
            setForceCustom(false)
            onChange(v)
          }
        }}
        options={[
          { value: '', label: emptyLabel },
          ...catalog.flatMap((g): SelectOption[] =>
            g.models.map((m) => ({ value: m, label: m, group: grouped ? g.label : undefined })),
          ),
          { value: CUSTOM, label: t('settings.aiProviders.custom'), group: grouped ? t('settings.aiProviders.other') : undefined },
        ]}
        size="md"
        searchable
        className="w-full"
        ariaLabel={ariaLabel}
      />
      {isCustom && (
        <input
          value={value}
          readOnly={disabled}
          autoComplete="off"
          placeholder={t('settings.aiProviders.customPlaceholder')}
          onChange={(e) => onChange(e.target.value)}
          className={`${inputCls} mt-1.5 ${disabled ? 'cursor-default bg-fill-subtle' : ''}`}
        />
      )}
    </>
  )
}

/**
 * Fallback order of the CONNECTED providers (up/down). The stored list keeps
 * every provider; the ones without a key ride at the end, out of sight.
 */
function OrderList({
  value,
  connected,
  labels,
  disabled,
  onChange,
}: {
  value: string
  connected: string[]
  labels: Record<string, string>
  disabled: boolean
  onChange: (v: string) => void
}) {
  const t = useT()
  let stored: string[] = []
  try {
    const parsed = JSON.parse(value || '[]')
    if (Array.isArray(parsed)) stored = parsed.filter((x): x is string => typeof x === 'string')
  } catch {
    stored = []
  }
  const live = [...stored.filter((p) => connected.includes(p)), ...connected.filter((p) => !stored.includes(p))]

  const move = (i: number, dir: -1 | 1) => {
    const j = i + dir
    if (j < 0 || j >= live.length) return
    const next = [...live]
    ;[next[i], next[j]] = [next[j], next[i]]
    onChange(JSON.stringify([...next, ...stored.filter((p) => !next.includes(p))]))
  }

  if (live.length === 0)
    return <p className="text-[11.5px] text-text-muted">{t('settings.aiProviders.orderEmpty')}</p>
  return (
    <ol className="flex flex-col gap-1">
      {live.map((p, i) => (
        <li key={p} className="flex items-center gap-2 rounded-lg border border-line bg-fill px-2.5 py-1.5">
          <span className="grid h-5 w-5 shrink-0 place-items-center rounded-md bg-card text-[10px] font-bold text-text-muted">
            {i + 1}
          </span>
          <span className="flex-1 text-[12px] font-semibold text-text-high">{labels[p] || p}</span>
          <button
            type="button"
            disabled={disabled || i === 0}
            onClick={() => move(i, -1)}
            title={t('admin.tags.editor.moveUp')}
            aria-label={t('settings.aiProviders.moveUpAria', { provider: labels[p] || p })}
            className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-default disabled:opacity-30"
          >
            <ChevronUp size={14} aria-hidden />
          </button>
          <button
            type="button"
            disabled={disabled || i === live.length - 1}
            onClick={() => move(i, 1)}
            title={t('admin.tags.editor.moveDown')}
            aria-label={t('settings.aiProviders.moveDownAria', { provider: labels[p] || p })}
            className="grid h-6 w-6 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-default disabled:opacity-30"
          >
            <ChevronDown size={14} aria-hidden />
          </button>
        </li>
      ))}
    </ol>
  )
}

/** The provider's logo on a neutral tile (a hairline keeps dark marks visible). */
function ProviderMark({ p }: { p: ProviderInfo }) {
  return (
    <span
      className={`grid h-9 w-9 shrink-0 place-items-center rounded-lg border bg-card ${
        p.configured ? 'border-line-strong' : 'border-line opacity-80'
      }`}
    >
      <AiProviderLogo id={p.id} label={p.label} />
    </span>
  )
}

/** One provider in the grid: name, what it is, whether it is connected, its model. */
function ProviderTile({ p, inUse, onOpen }: { p: ProviderInfo; inUse: boolean; onOpen: () => void }) {
  const t = useT()
  return (
    <button
      type="button"
      onClick={onOpen}
      className={`flex w-full cursor-pointer flex-col gap-2 rounded-xl border bg-card p-3 text-left transition-colors hover:border-brand-400 focus-visible:border-brand-400 focus-visible:outline-none ${
        p.configured ? 'border-line-strong' : 'border-line'
      }`}
    >
      <span className="flex items-start gap-2.5">
        <ProviderMark p={p} />
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[13px] font-bold text-text-high">{p.label}</span>
          <span className="line-clamp-2 block text-[11px] leading-snug text-text-muted">
            {PROVIDER_BLURB[p.id] ? t(PROVIDER_BLURB[p.id]) : ''}
          </span>
        </span>
      </span>
      <span className="flex flex-wrap items-center gap-1.5">
        {p.configured ? (
          <span className="inline-flex items-center gap-1 rounded-full bg-green/15 px-2 py-px text-[10px] font-bold text-[var(--green-ink)]">
            <CheckCircle2 size={10} aria-hidden />
            {p.key_source === 'env' ? t('settings.aiProviders.tile.fromEnv') : t('settings.aiProviders.tile.connected')}
          </span>
        ) : (
          <span className="rounded-full bg-fill px-2 py-px text-[10px] font-bold text-text-muted">
            {t('settings.aiProviders.tile.notSet')}
          </span>
        )}
        {inUse && (
          <span className="rounded-full bg-brand-400/15 px-2 py-px text-[10px] font-bold text-[var(--brand-ink)]">
            {t('settings.aiProviders.tile.answering')}
          </span>
        )}
        {p.vision && (
          <span
            className="inline-flex items-center gap-1 rounded-full bg-fill px-2 py-px text-[10px] font-semibold text-text-muted"
            title={t('settings.aiProviders.tile.visionHint')}
          >
            <Eye size={10} aria-hidden /> {t('settings.aiProviders.tile.vision')}
          </span>
        )}
      </span>
      {p.configured && (
        <span className="truncate font-mono text-[10.5px] text-text-muted">{p.model || p.default_model}</span>
      )}
    </button>
  )
}

/**
 * The panel behind a tile: key, endpoint (region / base URL), default model,
 * Test connection and Save — saved on its own (PUT of just these fields), so it
 * never mixes with the page's staged edits.
 */
function ProviderPanel({
  p,
  editable,
  onClose,
  onSaved,
}: {
  p: ProviderInfo
  editable: boolean
  onClose: () => void
  onSaved: (next: Payload) => void
}) {
  const t = useT()
  const [key, setKey] = useState('')
  const [baseUrl, setBaseUrl] = useState(p.base_url)
  const [customUrl, setCustomUrl] = useState(
    !!p.base_url && !p.endpoints.some((e) => e.url === p.base_url) && p.endpoints.length > 0,
  )
  const [model, setModel] = useState(p.model)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<TestResult | null>(null)

  const dirty = key.trim() !== '' || baseUrl !== p.base_url || model !== p.model
  const needsUrl = p.key_optional && !baseUrl.trim()

  const fields = (): Record<string, string> => {
    const body: Record<string, string> = {}
    if (key.trim()) body[p.key_field] = key.trim()
    if (p.base_url_field && baseUrl !== p.base_url) body[p.base_url_field] = baseUrl.trim()
    if (model !== p.model) body[p.model_field] = model.trim()
    return body
  }

  const save = async () => {
    setBusy('save')
    setError(null)
    try {
      const next = await api<Payload>('', { method: 'PUT', body: JSON.stringify(fields()) })
      onSaved(next)
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const remove = async () => {
    setBusy('remove')
    setError(null)
    try {
      const body: Record<string, string> = { [p.key_field]: '' }
      if (p.key_optional && p.base_url_field) body[p.base_url_field] = ''
      const next = await api<Payload>('', { method: 'PUT', body: JSON.stringify(body) })
      onSaved(next)
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const runTest = async () => {
    setBusy('test')
    setError(null)
    setTest(null)
    try {
      setTest(
        await api<TestResult>('/test', {
          method: 'POST',
          body: JSON.stringify({ provider: p.id, api_key: key.trim(), base_url: baseUrl.trim(), model: model.trim() }),
        }),
      )
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const endpointOptions: SelectOption[] = [
    ...p.endpoints.map((e) => ({ value: e.url, label: e.label })),
    { value: CUSTOM, label: t('settings.aiProviders.panel.customEndpoint') },
  ]
  const endpointValue = customUrl ? CUSTOM : baseUrl || p.default_base_url || p.endpoints[0]?.url || CUSTOM

  return (
    <Modal
      open
      onClose={onClose}
      title={
        <span className="flex items-center gap-2.5">
          <ProviderMark p={p} />
          {p.label}
        </span>
      }
      subtitle={PROVIDER_BLURB[p.id] ? t(PROVIDER_BLURB[p.id]) : undefined}
      footer={
        <div className="flex w-full flex-wrap items-center gap-2">
          {editable && p.configured && p.key_source === 'settings' && (
            <Button variant="ghost" size="sm" disabled={busy != null} onClick={remove}>
              {busy === 'remove' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Trash2 size={13} aria-hidden />}
              {t('settings.aiProviders.panel.remove')}
            </Button>
          )}
          <span className="flex-1" />
          <Button variant="secondary" size="sm" disabled={busy != null || needsUrl} onClick={runTest}>
            {busy === 'test' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <PlugZap size={13} aria-hidden />}
            {busy === 'test' ? t('settings.aiProviders.panel.testing') : t('settings.aiProviders.panel.test')}
          </Button>
          {editable && (
            <Button size="sm" disabled={!dirty || busy != null} onClick={save}>
              {busy === 'save' && <Loader2 size={13} className="animate-spin" aria-hidden />}
              {t('common.actions.save')}
            </Button>
          )}
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/20 bg-red/10 px-3 py-2 text-[12px] text-[var(--red-ink)]">
            <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
          </div>
        )}
        {!editable && (
          <p className="flex items-center gap-1.5 text-[11.5px] text-text-muted">
            <Lock size={11} aria-hidden /> {t('settings.aiProviders.readOnly')}
          </p>
        )}

        <label className="block">
          <span className="mb-1 flex items-center justify-between gap-2">
            <span className="text-[11.5px] font-bold text-text-high">
              {p.key_optional ? t('settings.aiProviders.panel.keyOptional') : t('settings.aiProviders.panel.key')}
            </span>
            {p.key_url && (
              <a
                href={p.key_url}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-1 text-[11px] font-semibold text-[var(--brand-ink)] hover:underline"
              >
                {t('settings.aiProviders.panel.getKey')} <ExternalLink size={11} aria-hidden />
              </a>
            )}
          </span>
          <input
            type="password"
            value={key}
            readOnly={!editable}
            autoComplete="off"
            onChange={(e) => setKey(e.target.value)}
            placeholder={
              p.key_masked
                ? t('settings.aiProviders.panel.keyKeep', { masked: p.key_masked })
                : t('settings.aiProviders.panel.keyNew')
            }
            className={`${inputCls} ${editable ? '' : 'cursor-default bg-fill-subtle'}`}
          />
          {p.key_source === 'env' && (
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              {t('settings.aiProviders.panel.fromEnv')}
            </span>
          )}
        </label>

        {p.base_url_field && (
          <div>
            <span className="mb-1 block text-[11.5px] font-bold text-text-high">
              {p.endpoints.length > 0 && !p.key_optional
                ? t('settings.aiProviders.panel.endpoint')
                : t('settings.aiProviders.panel.baseUrl')}
            </span>
            {p.endpoints.length > 0 && (
              <Select
                value={endpointValue}
                disabled={!editable}
                onChange={(v) => {
                  if (v === CUSTOM) setCustomUrl(true)
                  else {
                    setCustomUrl(false)
                    // the built-in endpoint needs no row
                    setBaseUrl(v === p.default_base_url ? '' : v)
                  }
                }}
                options={endpointOptions}
                size="md"
                className="w-full"
                ariaLabel={t('settings.aiProviders.panel.endpoint')}
              />
            )}
            {(customUrl || p.endpoints.length === 0) && (
              <input
                value={baseUrl}
                readOnly={!editable}
                autoComplete="off"
                placeholder={p.default_base_url || 'http://localhost:11434/v1'}
                onChange={(e) => setBaseUrl(e.target.value)}
                className={`${inputCls} ${p.endpoints.length > 0 ? 'mt-1.5' : ''} ${editable ? '' : 'cursor-default bg-fill-subtle'}`}
              />
            )}
            {p.key_optional && (
              <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
                {t('settings.aiProviders.panel.localHelp')}
              </span>
            )}
          </div>
        )}

        <div>
          <span className="mb-1 block text-[11.5px] font-bold text-text-high">{t('settings.aiProviders.panel.model')}</span>
          <ModelPicker
            value={model}
            catalog={[{ provider: p.id, label: p.label, models: p.models }]}
            disabled={!editable}
            emptyLabel={t('settings.aiProviders.panel.modelDefault', { model: p.default_model })}
            ariaLabel={t('settings.aiProviders.panel.model')}
            onChange={setModel}
          />
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
            {t('settings.aiProviders.panel.modelHelp')}
          </span>
        </div>

        {test && (
          <div
            role="status"
            className={`flex items-start gap-2 rounded-lg px-3 py-2 text-[12px] ${
              test.ok ? 'bg-green/10 text-[var(--green-ink)]' : 'bg-red/10 text-[var(--red-ink)]'
            }`}
          >
            {test.ok ? (
              <CheckCircle2 size={14} className="mt-0.5 shrink-0" aria-hidden />
            ) : (
              <XCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
            )}
            <span className="min-w-0 break-words">
              {test.ok
                ? t('settings.aiProviders.panel.testOk', { model: test.model, ms: test.ms })
                : t('settings.aiProviders.panel.testFail', { error: test.error })}
            </span>
          </div>
        )}
      </div>
    </Modal>
  )
}

/**
 * Settings → AI keys & models.
 *
 * - "Who answers": the pin (or Automatic + the fallback order of the connected
 *   providers) and an optional model override — staged, saved with the page's
 *   Save / Discard (SettingsDirtyContext).
 * - "AI providers": every provider of the catalog as a tile (search, Connected /
 *   Not connected filter, grouped Global · Asia · Self-hosted); a tile opens
 *   ProviderPanel, which saves that provider on its own and can test it.
 * - Advanced: the AI reviewer model, ElevenLabs, embeddings and the tunables.
 *
 * Editing needs settings.ai_keys.manage; others see everything read-only.
 */
export function AiProvidersSettings() {
  const t = useT()
  const { ready, can } = useAccess()
  const isSuper = can('settings.ai_keys.manage')

  const [meta, setMeta] = useState<FieldMeta[] | null>(null)
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const [source, setSource] = useState<Record<string, string>>({})
  const [aiAvailable, setAiAvailable] = useState(false)
  const [values, setValues] = useState<Record<string, string>>({})
  const [initial, setInitial] = useState<Record<string, string>>({})
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<'all' | 'connected' | 'off'>('all')
  const [openId, setOpenId] = useState<string | null>(null)
  const [note, setNote] = useState('')

  const { setDirty, register } = useSettingsDirty()

  const valuesRef = useRef(values)
  valuesRef.current = values
  const initialRef = useRef(initial)
  initialRef.current = initial
  const metaRef = useRef<FieldMeta[] | null>(meta)
  metaRef.current = meta

  /** The page's own (non-provider) fields as strings — what Save sends. */
  const pageFields = useCallback((fields: FieldMeta[]) => fields.filter((f) => f.group !== 'provider'), [])

  const seed = useCallback(
    (p: Payload) => {
      const rec = p as Record<string, unknown>
      const vals: Record<string, string> = {}
      for (const f of pageFields(p.fields)) {
        let raw: unknown
        if (f.kind === 'secret') raw = rec[`${f.key}_masked`]
        else if (f.kind === 'order') raw = JSON.stringify(rec[f.key] ?? [])
        else raw = rec[f.key]
        vals[f.key] = raw == null ? '' : String(raw)
      }
      for (const k of EMBEDDING_KEYS) if (!(k in vals)) vals[k] = rec[k] == null ? '' : String(rec[k])
      setMeta(p.fields)
      setProviders(p.providers || [])
      setSource(p.source || {})
      setNote(p.env_only_note || '')
      setAiAvailable(!!p.ai_available)
      setValues(vals)
      setInitial(vals)
    },
    [pageFields],
  )

  /** A provider panel saved: refresh the tiles + status, keep the page's staged edits. */
  const providerSaved = useCallback((p: Payload) => {
    setProviders(p.providers || [])
    setSource(p.source || {})
    setAiAvailable(!!p.ai_available)
  }, [])

  const load = useCallback(async () => {
    try {
      seed(await api<Payload>())
      setLoadError(null)
    } catch (e) {
      setMeta(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [seed])

  useEffect(() => {
    load()
  }, [load])

  const save = useCallback(async () => {
    const fields = pageFields(metaRef.current || [])
    const cur = valuesRef.current
    const init = initialRef.current
    const body: Record<string, string> = {}
    for (const k of EMBEDDING_KEYS) {
      if (!fields.some((f) => f.key === k) && (cur[k] ?? '') !== (init[k] ?? '')) body[k] = cur[k] ?? ''
    }
    for (const f of fields) {
      const v = cur[f.key] ?? ''
      if (v === (init[f.key] ?? '')) continue
      if (f.kind === 'secret' && v.includes('••')) continue
      if (f.kind === 'int' && v.trim() === '') continue
      body[f.key] = v
    }
    if (Object.keys(body).length === 0) return
    setError(null)
    try {
      seed(await api<Payload>('', { method: 'PUT', body: JSON.stringify(body) }))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      throw e
    }
  }, [seed, pageFields])

  useEffect(() => {
    register({ save, discard: () => void load() })
    return () => register(null)
  }, [register, save, load])

  const edit = (key: string, v: string) => {
    if (!isSuper) return
    setValues((prev) => ({ ...prev, [key]: v }))
    setDirty(true)
  }

  const labels = useMemo(() => Object.fromEntries(providers.map((p) => [p.id, p.label])), [providers])
  const connected = useMemo(() => providers.filter((p) => p.configured).map((p) => p.id), [providers])
  const pin = values['chat_provider_pref'] ?? ''

  // which provider answers first right now (the pin, else the first connected in the order)
  const answering = useMemo(() => {
    if (pin && connected.includes(pin)) return pin
    let order: string[] = []
    try {
      order = JSON.parse(values['chat_provider_order'] || '[]')
    } catch {
      order = []
    }
    return [...order, ...connected].find((p) => connected.includes(p)) ?? ''
  }, [pin, connected, values])

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return providers.filter(
      (p) =>
        (filter === 'all' || (filter === 'connected') === p.configured) &&
        (!q ||
          p.label.toLowerCase().includes(q) ||
          p.id.includes(q) ||
          (PROVIDER_BLURB[p.id] && t(PROVIDER_BLURB[p.id]).toLowerCase().includes(q))),
    )
  }, [providers, query, filter, t])

  if (!ready || (meta === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('settings.aiProviders.loading')}
      </div>
    )
  }

  if (meta === null) {
    return (
      <div className="py-6 text-[12px]">
        <div className="flex items-start gap-2 rounded-lg border border-red/20 bg-red/10 px-3 py-2 text-[var(--red-ink)]">
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>{t('settings.aiProviders.loadError', { error: loadError ?? '' })}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
        </Button>
      </div>
    )
  }

  const visible = pageFields(meta).filter((f) => !EMBEDDING_KEYS.includes(f.key))
  const advanced = visible.filter((f) => !ANSWER_KEYS.includes(f.key))
  const advGroups: string[] = []
  for (const f of advanced) if (!advGroups.includes(f.group)) advGroups.push(f.group)
  const customised =
    advanced.filter((f) => !!source[f.key]).length + EMBEDDING_KEYS.filter((k) => (values[k] ?? '') !== '').length

  // the model override offers the pinned provider's models, else every connected provider's
  const modelCatalog: ModelGroup[] = providers
    .filter((p) => (pin ? p.id === pin : p.configured))
    .map((p) => ({ provider: p.id, label: p.label, models: p.models }))

  const openProvider = providers.find((p) => p.id === openId) ?? null

  function renderField(f: FieldMeta) {
    const val = values[f.key] ?? ''
    const masked = f.kind === 'secret' && val.includes('••')
    return (
      <label key={f.key} className="block">
        <span className="mb-1 flex items-center justify-between gap-2">
          <span className="text-[11px] font-bold text-text-high">{fieldLabel(t, f)}</span>
          <SourceChip src={source[f.key]} />
        </span>
        <input
          value={val}
          readOnly={!isSuper}
          autoComplete="off"
          type={f.kind === 'secret' ? (masked ? 'text' : 'password') : f.kind === 'int' ? 'number' : 'text'}
          min={f.kind === 'int' ? f.min : undefined}
          onChange={(e) => edit(f.key, e.target.value)}
          className={`${inputCls} ${isSuper ? '' : 'cursor-default bg-fill-subtle'}`}
        />
        {fieldHelp(t, f) && (
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{fieldHelp(t, f)}</span>
        )}
      </label>
    )
  }

  const filterBtn = (id: typeof filter, lbl: string) => (
    <button
      key={id}
      type="button"
      onClick={() => setFilter(id)}
      aria-pressed={filter === id}
      className={`rounded-full px-2.5 py-1 text-[11px] font-semibold transition-colors ${
        filter === id ? 'bg-primary text-on-primary' : 'bg-fill text-text-mid hover:bg-fill-hover'
      }`}
    >
      {lbl}
    </button>
  )

  return (
    <div className="flex flex-col gap-5 pt-3.5">
      <div data-tour="aiproviders-status" className="flex flex-wrap items-center gap-2">
        <span
          className={`rounded-full px-2.5 py-1 text-[11px] font-bold ${
            aiAvailable ? 'bg-green/15 text-[var(--green-ink)]' : 'bg-fill text-text-muted'
          }`}
        >
          {aiAvailable ? t('settings.appKeys.aiReady') : t('settings.appKeys.aiOff')}
        </span>
        <span className="text-[11.5px] text-text-muted">
          {t('settings.aiProviders.connectedCount', { count: connected.length })}
          {answering && ` · ${t('settings.aiProviders.answeringNow', { provider: labels[answering] || answering })}`}
        </span>
        {!isSuper && (
          <span className="flex items-center gap-1 text-[10.5px] font-semibold text-text-muted">
            <Lock size={11} aria-hidden /> {t('settings.aiProviders.readOnly')}
          </span>
        )}
      </div>

      {error && (
        <div className="flex items-start gap-2 rounded-lg border border-red/20 bg-red/10 px-3 py-2 text-[12px] text-[var(--red-ink)]">
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      {/* ---- the providers ---- */}
      <section data-tour="aiproviders-providers" className="rounded-xl border border-line bg-card p-4">
        <div className="flex items-center gap-2">
          <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill text-[var(--brand-ink)]">
            <KeyRound size={14} aria-hidden />
          </span>
          <span className="text-[12.5px] font-bold text-text-high">{t('settings.aiProviders.providersTitle')}</span>
        </div>
        <p className="mb-3 mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">
          {t('settings.aiProviders.providersBlurb')}
        </p>
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <label className="relative min-w-[200px] flex-1 sm:max-w-[280px]">
            <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t('settings.aiProviders.search')}
              aria-label={t('settings.aiProviders.search')}
              className={`${inputCls} pl-8`}
            />
          </label>
          <span className="flex flex-wrap gap-1">
            {filterBtn('all', t('settings.aiProviders.filter.all', { count: providers.length }))}
            {filterBtn('connected', t('settings.aiProviders.filter.connected', { count: connected.length }))}
            {filterBtn('off', t('settings.aiProviders.filter.off'))}
          </span>
        </div>
        {shown.length === 0 ? (
          <p className="py-4 text-[12px] text-text-muted">{t('settings.aiProviders.noMatch')}</p>
        ) : (
          <div className="flex flex-col gap-4">
            {GROUPS.map((g) => {
              const list = shown.filter((p) => p.group === g.id)
              if (list.length === 0) return null
              return (
                <div key={g.id}>
                  <div className="eyebrow mb-1.5">{t(g.lbl)}</div>
                  <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {list.map((p) => (
                      <ProviderTile key={p.id} p={p} inUse={p.id === answering} onOpen={() => setOpenId(p.id)} />
                    ))}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>

      {/* ---- who answers ---- */}
      <section className="rounded-xl border border-line bg-card p-4">
        <div className="flex items-center gap-2">
          <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill text-[var(--brand-ink)]">
            <SlidersHorizontal size={14} aria-hidden />
          </span>
          <span className="text-[12.5px] font-bold text-text-high">{t('settings.aiProviders.answersTitle')}</span>
        </div>
        <p className="mb-3 mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">
          {t('settings.aiProviders.answersBlurb')}
        </p>
        <div className="grid gap-3.5 md:grid-cols-2">
          <label className="block">
            <span className="mb-1 flex items-center justify-between gap-2">
              <span className="text-[11px] font-bold text-text-high">{t('settings.aiProviders.fields.chatProvider')}</span>
              <SourceChip src={source['chat_provider_pref']} tour />
            </span>
            <Select
              value={pin}
              disabled={!isSuper}
              onChange={(v) => edit('chat_provider_pref', v)}
              options={[
                { value: '', label: t('settings.aiProviders.automatic') },
                ...providers.map((p) => ({
                  value: p.id,
                  label: p.configured ? p.label : t('settings.aiProviders.noKeyOption', { provider: p.label }),
                  disabled: !p.configured && p.id !== pin,
                })),
              ]}
              size="md"
              searchable
              ariaLabel={t('settings.aiProviders.fields.chatProvider')}
              className="w-full"
            />
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              {pin ? t('settings.aiProviders.pinHelp') : t('settings.aiProviders.autoHelp')}
            </span>
          </label>
          <label className="block">
            <span className="mb-1 flex items-center justify-between gap-2">
              <span className="text-[11px] font-bold text-text-high">{t('settings.aiProviders.fields.chatModel')}</span>
              <SourceChip src={source['chat_model']} />
            </span>
            <ModelPicker
              value={values['chat_model'] ?? ''}
              catalog={modelCatalog}
              disabled={!isSuper}
              emptyLabel={t('settings.aiProviders.providerDefault')}
              ariaLabel={t('settings.aiProviders.fields.chatModel')}
              onChange={(v) => edit('chat_model', v)}
            />
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              {t('settings.aiProviders.fields.chatModelHelp')}
            </span>
          </label>
          {!pin && (
            <div className="md:col-span-2">
              <span className="mb-1 flex items-center justify-between gap-2">
                <span className="text-[11px] font-bold text-text-high">{t('settings.aiProviders.fields.order')}</span>
                <SourceChip src={source['chat_provider_order']} />
              </span>
              <OrderList
                value={values['chat_provider_order'] ?? ''}
                connected={connected}
                labels={labels}
                disabled={!isSuper}
                onChange={(v) => edit('chat_provider_order', v)}
              />
              <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
                {t('settings.aiProviders.fields.orderHelp')}
              </span>
            </div>
          )}
        </div>
      </section>

      <Advanced data-tour="aiproviders-advanced" variant="card" count={customised} forceOpen={customised > 0}>
        {note && (
          <div className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2.5 text-[11.5px] leading-relaxed text-text-mid">
            <Info size={14} className="mt-0.5 shrink-0 text-[var(--brand-ink)]" aria-hidden />
            <span>{t('settings.aiProviders.envNote')}</span>
          </div>
        )}
        {advGroups.map((g) => (
          <div key={g} className="grid gap-3.5 md:grid-cols-2">
            {advanced.filter((f) => f.group === g).map(renderField)}
          </div>
        ))}
        <EmbeddingsSection
          provider={values.embedding_provider ?? ''}
          model={values.embedding_model ?? ''}
          disabled={!isSuper}
          onChange={edit}
        />
      </Advanced>

      {openProvider && (
        <ProviderPanel
          key={openProvider.id}
          p={openProvider}
          editable={isSuper}
          onClose={() => setOpenId(null)}
          onSaved={providerSaved}
        />
      )}
    </div>
  )
}
