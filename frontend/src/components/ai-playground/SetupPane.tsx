'use client'

/**
 * The playground's Setup pane: which outlet the AI speaks for, the channel,
 * who the customer is (a made-up profile or an existing contact), an optional
 * real conversation to start from — and, for people who may change the AI's
 * behaviour, "Try changes": the model, an unsaved persona draft and the parts
 * of the prompt, per set-up (A, and B when comparing).
 */

import Link from 'next/link'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { ExternalLink, GitCompare, History, Loader2, RotateCcw, Save, Search, UserRound, X } from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { ChannelLogo, channelLabelFor } from '@/components/ui/ChannelLogo'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { Toggle } from '@/components/ui/Toggle'
import { useTags } from '@/lib/tags'
import { aiApi } from '@/lib/aiMonitor'
import {
  isOverride,
  profileLanguage,
  type PlaygroundField,
  type PlaygroundOptions,
  type Setup,
  type Variant,
} from '@/lib/aiPlayground'
import { useI18n, useT, type MessageKey } from '@/i18n'

export const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const MODE_LABEL: Record<string, MessageKey> = {
  auto: 'ai.monitor.mode.auto',
  review_flagged: 'ai.monitor.mode.reviewFlagged',
  review_all: 'ai.monitor.mode.reviewAll',
  shadow: 'ai.monitor.mode.shadow',
}

const LANG_LABEL: Record<string, MessageKey> = {
  en: 'playground.setup.languages.en',
  ms: 'playground.setup.languages.ms',
  zh: 'playground.setup.languages.zh',
}

function Label({ children, htmlFor }: { children: ReactNode; htmlFor?: string }) {
  return (
    <label htmlFor={htmlFor} className="mb-1 block text-[12px] font-semibold text-text-high">
      {children}
    </label>
  )
}

function Section({ title, children, aside }: { title: string; children: ReactNode; aside?: ReactNode }) {
  return (
    <section className="border-t border-line pt-3 first:border-t-0 first:pt-0">
      <div className="mb-2 flex items-center justify-between gap-2">
        <h2 className="eyebrow">{title}</h2>
        {aside}
      </div>
      <div className="flex flex-col gap-3">{children}</div>
    </section>
  )
}

interface ContactHit {
  id: number
  name: string
  phone?: string
  email?: string
}

/** Pick an existing contact — searched server-side, scoped like the Contacts page. */
function ContactPicker({
  entity,
  value,
  onChange,
}: {
  entity: string | null
  value: Setup['contact']
  onChange: (c: Setup['contact']) => void
}) {
  const t = useT()
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<ContactHit[] | null>(null)
  const [denied, setDenied] = useState(false)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (value) return
    const term = q.trim()
    const timer = window.setTimeout(() => {
      const p = new URLSearchParams({ limit: '8' })
      if (term) p.set('q', term)
      if (entity) p.set('entity', entity)
      setLoading(true)
      aiApi<{ contacts: ContactHit[] }>(`contacts?${p}`)
        .then((d) => {
          setHits(Array.isArray(d.contacts) ? d.contacts.slice(0, 8) : [])
          setDenied(false)
        })
        .catch((e: { status?: number }) => {
          setHits([])
          setDenied(e?.status === 403)
        })
        .finally(() => setLoading(false))
    }, 250)
    return () => window.clearTimeout(timer)
  }, [q, entity, value])

  if (value)
    return (
      <div className="flex items-center gap-2 rounded-lg border border-line-strong bg-card px-3 py-2">
        <UserRound size={14} className="shrink-0 text-text-muted" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-text-high">{value.name}</span>
        <Button size="sm" variant="ghost" onClick={() => onChange(null)}>
          {t('playground.setup.change')}
        </Button>
      </div>
    )
  if (denied) return <p className="text-[12px] text-text-muted">{t('playground.setup.contactsUnavailable')}</p>
  return (
    <div>
      <div className="relative">
        <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
        <input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder={t('playground.setup.contactSearch')}
          aria-label={t('playground.setup.contactSearch')}
          className={`${fieldCls} pl-8`}
        />
      </div>
      <ul className="mt-1.5 flex flex-col gap-0.5" aria-busy={loading}>
        {(hits ?? []).map((c) => (
          <li key={c.id}>
            <button
              type="button"
              onClick={() => onChange({ id: c.id, name: c.name })}
              className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-[12.5px] hover:bg-fill-hover"
            >
              <span className="min-w-0 flex-1 truncate font-semibold text-text-high">{c.name}</span>
              <span className="shrink-0 truncate text-[11.5px] text-text-muted">{c.phone || c.email}</span>
            </button>
          </li>
        ))}
        {hits && hits.length === 0 && !loading && (
          <li className="px-2 py-1.5 text-[12px] text-text-muted">{t('playground.setup.noContacts')}</li>
        )}
      </ul>
    </div>
  )
}

/** One custom field of the made-up profile, rendered by its kind. */
function FieldInput({
  f,
  value,
  onChange,
}: {
  f: PlaygroundField
  value: string | number | boolean | string[] | undefined
  onChange: (v: string | number | boolean | string[]) => void
}) {
  const t = useT()
  const id = `pg-field-${f.key}`
  if (f.kind === 'boolean')
    return (
      <div className="flex items-center justify-between gap-3">
        <span className="text-[12.5px] text-text-high">{f.label}</span>
        <Toggle on={value === true} onChange={(v) => onChange(v)} label={f.label} />
      </div>
    )
  if (f.kind === 'select')
    return (
      <div>
        <Label>{f.label}</Label>
        <Select
          value={typeof value === 'string' ? value : ''}
          onChange={(v) => onChange(v)}
          options={f.options.map((o) => ({ value: o, label: o }))}
          clearable
          ariaLabel={f.label}
          size="md"
        />
      </div>
    )
  if (f.kind === 'multiselect')
    return (
      <div>
        <Label>{f.label}</Label>
        <MultiSelect
          value={Array.isArray(value) ? value : []}
          onChange={(v) => onChange(v)}
          options={f.options.map((o) => ({ value: o, label: o }))}
          ariaLabel={f.label}
          size="md"
        />
      </div>
    )
  return (
    <div>
      <Label htmlFor={id}>{f.label}</Label>
      <input
        id={id}
        type={f.kind === 'number' ? 'number' : 'text'}
        value={value == null || Array.isArray(value) || typeof value === 'boolean' ? '' : String(value)}
        onChange={(e) =>
          onChange(f.kind === 'number' && e.target.value !== '' ? Number(e.target.value) : e.target.value)
        }
        className={fieldCls}
        aria-label={t('playground.setup.fields') + ' · ' + f.label}
      />
    </div>
  )
}

/** Model, persona draft and prompt parts for one set-up. */
export function VariantEditor({
  options,
  variant,
  onChange,
  idPrefix,
  entity,
  onPersonaSaved,
}: {
  options: PlaygroundOptions
  variant: Variant
  onChange: (v: Variant) => void
  idPrefix: string
  /** The outlet the set-up speaks for (null = the organization) — where "Save as persona" writes. */
  entity?: string | null
  /** The draft was saved as the stored persona. */
  onPersonaSaved?: (persona: string) => void
}) {
  const t = useT()
  const [saving, setSaving] = useState(false)
  const [saveMsg, setSaveMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const savePersona = async () => {
    if (variant.persona == null) return
    setSaving(true)
    setSaveMsg(null)
    try {
      await aiApi('aicore/persona', {
        method: 'PUT',
        body: JSON.stringify({ persona: variant.persona, entity: entity || null }),
      })
      onPersonaSaved?.(variant.persona)
      onChange({ ...variant, persona: null })
      setSaveMsg({ ok: true, text: t('playground.changes.personaSavedOk') })
    } catch (e) {
      setSaveMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(false)
    }
  }
  const route = options.default_route
  const providerOptions = useMemo<SelectOption[]>(
    () => [
      {
        value: '',
        label: route
          ? t('playground.changes.defaultRoute', { provider: route.label, model: route.model })
          : t('playground.changes.defaultSandbox'),
      },
      ...options.providers.map((p) => ({ value: p.id, label: p.label, hint: p.default_model })),
    ],
    [options.providers, route, t],
  )
  const provider = options.providers.find((p) => p.id === variant.provider)
  const personaText = variant.persona ?? options.persona ?? ''
  const set = (patch: Partial<Variant>) => onChange({ ...variant, ...patch })
  const parts: { key: 'knowledge' | 'lessons' | 'skills' | 'memory'; label: MessageKey }[] = [
    { key: 'knowledge', label: 'playground.changes.knowledge' },
    { key: 'lessons', label: 'playground.changes.lessons' },
    { key: 'skills', label: 'playground.changes.skills' },
    { key: 'memory', label: 'playground.changes.memory' },
  ]

  return (
    <div className="flex flex-col gap-3">
      <div>
        <Label>{t('playground.changes.model')}</Label>
        <Select
          value={variant.provider}
          onChange={(v) => set({ provider: v, model: '' })}
          options={providerOptions}
          ariaLabel={t('playground.changes.model')}
          size="md"
        />
        {provider && (
          <input
            value={variant.model}
            onChange={(e) => set({ model: e.target.value })}
            placeholder={t('playground.changes.modelPlaceholder', { model: provider.default_model })}
            aria-label={t('playground.changes.modelName')}
            className={`${fieldCls} mt-1.5`}
          />
        )}
      </div>

      <div>
        <div className="mb-1 flex items-center justify-between gap-2">
          <label htmlFor={`${idPrefix}-persona`} className="text-[12px] font-semibold text-text-high">
            {t('playground.changes.persona')}
          </label>
          {variant.persona == null && (
            <Link
              href="/ai-persona"
              className="inline-flex items-center gap-1 text-[11.5px] font-semibold text-text-muted hover:text-text-high"
            >
              <ExternalLink size={11} aria-hidden /> {t('playground.changes.personaOpenEditor')}
            </Link>
          )}
          {variant.persona != null && (
            <button
              type="button"
              onClick={() => set({ persona: null })}
              className="inline-flex items-center gap-1 text-[11.5px] font-semibold text-text-muted hover:text-text-high"
            >
              <RotateCcw size={11} aria-hidden /> {t('playground.changes.personaReset')}
            </button>
          )}
        </div>
        <textarea
          id={`${idPrefix}-persona`}
          rows={5}
          value={personaText}
          maxLength={options.limits.persona}
          onChange={(e) => set({ persona: e.target.value })}
          className={`${fieldCls} resize-y font-mono text-[12px]`}
        />
        <p className={`mt-1 text-[11.5px] ${variant.persona != null ? 'text-amber-300' : 'text-text-muted'}`}>
          {variant.persona != null ? t('playground.changes.personaUnsaved') : t('playground.changes.personaSavedHint')}
        </p>
        {variant.persona != null && (
          <Button size="sm" variant="secondary" className="mt-1.5" onClick={savePersona} disabled={saving}>
            {saving ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <Save size={12} aria-hidden />}
            {t('playground.changes.personaSave')}
          </Button>
        )}
        {saveMsg && (
          <p role={saveMsg.ok ? 'status' : 'alert'} className={`mt-1 text-[11.5px] ${saveMsg.ok ? 'text-[var(--green-ink)]' : 'text-[var(--red-ink)]'}`}>
            {saveMsg.text}
          </p>
        )}
      </div>

      <div>
        <div className="mb-1.5 text-[12px] font-semibold text-text-high">{t('playground.changes.parts')}</div>
        <div className="flex flex-col gap-2">
          {parts.map((p) => (
            <div key={p.key} className="flex items-center justify-between gap-3">
              <span className="text-[12.5px] text-text-mid">{t(p.label)}</span>
              <Toggle on={variant[p.key]} onChange={(v) => set({ [p.key]: v } as Partial<Variant>)} label={t(p.label)} />
            </div>
          ))}
          <div className="flex items-center justify-between gap-3">
            <span className="text-[12.5px] text-text-mid">{t('playground.changes.showPrompt')}</span>
            <Toggle
              on={variant.showPrompt}
              onChange={(v) => set({ showPrompt: v })}
              label={t('playground.changes.showPrompt')}
            />
          </div>
        </div>
      </div>
    </div>
  )
}

export function SetupPane({
  options,
  setup,
  onSetup,
  variants,
  onVariants,
  onImport,
  onPersonaSaved,
  failed = false,
}: {
  options: PlaygroundOptions | null
  setup: Setup
  onSetup: (s: Setup) => void
  variants: Variant[]
  onVariants: (v: Variant[]) => void
  onImport: () => void
  /** A persona draft was saved as the stored persona of the set-up's outlet. */
  onPersonaSaved?: (persona: string) => void
  /** The options could not be loaded (the page shows why). */
  failed?: boolean
}) {
  const t = useT()
  const { locale } = useI18n()
  const { tags: registry } = useTags('contact', setup.entity)
  const set = (patch: Partial<Setup>) => onSetup({ ...setup, ...patch })
  const setProfile = (patch: Partial<Setup['profile']>) => set({ profile: { ...setup.profile, ...patch } })
  const suggestions = registry
    .map((x) => x.name)
    .filter((n) => !setup.profile.tags.includes(n))
    .slice(0, 8)
  const channels = options?.channels ?? ['whatsapp']
  const channelOptions = channels.map((c) => ({ value: c, label: channelLabelFor(c, t) }))
  const languages = options?.languages ?? ['en', 'ms', 'zh']
  const comparing = variants.length > 1
  // The made-up customer's extras (language, tags, custom fields) sit under
  // Advanced; opened whenever one of them is set.
  const profileExtras =
    (setup.profile.language ? 1 : 0) +
    (setup.profile.tags.length > 0 ? 1 : 0) +
    Object.values(setup.profile.fields).filter((v) => (Array.isArray(v) ? v.length > 0 : v !== '' && v != null && v !== false))
      .length
  const changesActive = comparing || variants.some(isOverride)

  return (
    <div className="flex flex-col gap-4">
      <Section title={t('playground.setup.title')}>
        <div>
          <Label>{t('playground.setup.outlet')}</Label>
          <EntityPicker value={setup.entity} onChange={(slug) => set({ entity: slug, contact: null })} />
          <p className="mt-1 text-[11.5px] text-text-muted">{t('playground.setup.outletHint')}</p>
        </div>
        <div>
          <Label>{t('playground.setup.channel')}</Label>
          <div className="flex items-center gap-2">
            <ChannelLogo channel={setup.channel} size={18} title="" />
            <Select
              value={setup.channel}
              onChange={(v) => set({ channel: v })}
              options={channelOptions}
              ariaLabel={t('playground.setup.channel')}
              size="md"
              className="min-w-0 flex-1"
            />
          </div>
        </div>
        {options && (
          <div className="rounded-lg bg-well px-3 py-2 text-[11.5px] leading-relaxed text-text-mid">
            <div className="font-semibold text-text-high">{t('playground.setup.production')}</div>
            <div>
              {t('playground.setup.policy', {
                mode: MODE_LABEL[options.policy.review_mode] ? t(MODE_LABEL[options.policy.review_mode]) : options.policy.review_mode,
              })}
            </div>
            {/* the AI controls' own reason ("On: …" / "Off: …"), as the chat header shows it */}
            <div className={options.ai.enabled ? '' : 'text-amber-300'}>{options.ai.reason}</div>
          </div>
        )}
      </Section>

      <Section title={t('playground.setup.customer')}>
        <div className="inline-flex w-fit rounded-lg border border-line-strong bg-card p-0.5" role="radiogroup"
          aria-label={t('playground.setup.customer')}>
          {(
            [
              { id: 'made_up', label: t('playground.setup.madeUp') },
              { id: 'contact', label: t('playground.setup.existing') },
            ] as const
          ).map((m) => (
            <button
              key={m.id}
              type="button"
              role="radio"
              aria-checked={setup.mode === m.id}
              onClick={() => set({ mode: m.id })}
              className={`rounded-md px-3 py-1 text-[12.5px] font-semibold transition-colors ${
                setup.mode === m.id ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
              }`}
            >
              {m.label}
            </button>
          ))}
        </div>

        {setup.mode === 'contact' ? (
          <ContactPicker entity={setup.entity} value={setup.contact} onChange={(c) => set({ contact: c })} />
        ) : (
          <>
            <div>
              <Label htmlFor="pg-name">{t('playground.setup.name')}</Label>
              <input
                id="pg-name"
                value={setup.profile.name}
                onChange={(e) => setProfile({ name: e.target.value })}
                placeholder={t('playground.setup.namePlaceholder')}
                maxLength={120}
                className={fieldCls}
              />
            </div>
            <Advanced count={profileExtras} forceOpen={profileExtras > 0}>
              <div>
                <Label>{t('playground.setup.language')}</Label>
                <Select
                  value={profileLanguage(setup, locale)}
                  onChange={(v) => setProfile({ language: v })}
                  options={languages.map((l) => ({ value: l, label: LANG_LABEL[l] ? t(LANG_LABEL[l]) : l }))}
                  ariaLabel={t('playground.setup.language')}
                  size="md"
                />
              </div>
              <div>
                <Label>{t('playground.setup.tags')}</Label>
                <TagListInput
                  value={setup.profile.tags}
                  onChange={(tags) => setProfile({ tags: tags.slice(0, 30) })}
                  placeholder={t('playground.setup.tagsPlaceholder')}
                />
                {suggestions.length > 0 && (
                  <div className="mt-1.5 flex flex-wrap items-center gap-1">
                    <span className="text-[11px] text-text-muted">{t('playground.setup.tagSuggestions')}</span>
                    {suggestions.map((s) => (
                      <button
                        key={s}
                        type="button"
                        onClick={() => setProfile({ tags: [...setup.profile.tags, s] })}
                        className="rounded-full border border-line-strong px-2 py-0.5 text-[11px] font-semibold text-text-mid hover:bg-fill-hover"
                      >
                        + {s}
                      </button>
                    ))}
                  </div>
                )}
              </div>
              {options && options.custom_fields.length > 0 && (
                <div className="flex flex-col gap-2.5">
                  <div className="text-[12px] font-semibold text-text-high">{t('playground.setup.fields')}</div>
                  {options.custom_fields.slice(0, 6).map((f) => (
                    <FieldInput
                      key={f.key}
                      f={f}
                      value={setup.profile.fields[f.key]}
                      onChange={(v) => setProfile({ fields: { ...setup.profile.fields, [f.key]: v } })}
                    />
                  ))}
                </div>
              )}
            </Advanced>
          </>
        )}

        {options?.can_import && (
          <div className="flex flex-col gap-1.5">
            {setup.imported ? (
              <div className="flex items-center gap-2 rounded-lg border border-line-strong bg-card px-3 py-2">
                <History size={14} className="shrink-0 text-text-muted" aria-hidden />
                <span className="min-w-0 flex-1 truncate text-[12.5px] text-text-high">
                  {t('playground.setup.importedFrom', { name: setup.imported.name })}
                </span>
                <button
                  type="button"
                  onClick={() => set({ imported: null })}
                  aria-label={t('playground.setup.stopUsing')}
                  title={t('playground.setup.stopUsing')}
                  className="grid h-6 w-6 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
                >
                  <X size={13} aria-hidden />
                </button>
              </div>
            ) : null}
            <Button size="sm" variant="secondary" onClick={onImport} className="w-fit">
              <History size={13} aria-hidden /> {t('playground.setup.real')}
            </Button>
            <p className="text-[11.5px] text-text-muted">{t('playground.setup.realHint')}</p>
          </div>
        )}
      </Section>

      {options?.can_override ? (
        <section className="border-t border-line pt-3">
          <Advanced label={t('playground.changes.title')} hint={t('playground.changes.hint')} forceOpen={changesActive}>
            <Button
              size="sm"
              variant="secondary"
              className="w-full"
              onClick={() => onVariants(comparing ? variants.slice(0, 1) : [variants[0], { ...variants[0] }])}
            >
              <GitCompare size={13} aria-hidden />
              <span className="truncate">
                {comparing ? t('playground.changes.stopCompare') : t('playground.changes.compare')}
              </span>
            </Button>
            {variants.map((v, i) => (
              <div key={i} className={comparing ? 'rounded-xl border border-line p-3' : ''}>
                {comparing && (
                  <div className="eyebrow mb-2">{i === 0 ? t('playground.changes.setupA') : t('playground.changes.setupB')}</div>
                )}
                <VariantEditor
                  options={options}
                  variant={v}
                  idPrefix={`pg-v${i}`}
                  entity={setup.entity}
                  onPersonaSaved={onPersonaSaved}
                  onChange={(next) => onVariants(variants.map((x, j) => (j === i ? next : x)))}
                />
              </div>
            ))}
          </Advanced>
        </section>
      ) : options ? (
        <p className="border-t border-line pt-3 text-[11.5px] text-text-muted">{t('playground.changes.adminOnly')}</p>
      ) : failed ? null : (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}
    </div>
  )
}
