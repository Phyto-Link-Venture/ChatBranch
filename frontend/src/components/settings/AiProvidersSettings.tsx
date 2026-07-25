'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Info, KeyRound, Loader2, Lock, RefreshCw, SlidersHorizontal } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { expireSessionOn401 } from '@/lib/auth'
import { useSettingsDirty } from './SettingsDirtyContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/* ---------- types (mirror core/settings_api.py `_payload`) ---------- */

type FieldKind = 'secret' | 'select' | 'text' | 'int'

interface FieldMeta {
  key: string
  label: string
  group: string // 'providers' | 'tunables'
  kind: FieldKind
  min?: number
  help?: string
}

// The GET payload is self-describing: `fields` schema + `source` provenance +
// `chat_provider_options`, plus per-field VALUES carried as `${key}_masked`
// (secrets) or `${key}` (everything else). We keep the loose index so the value
// lookups stay data-driven from the schema — no hardcoded field names.
type Payload = {
  fields: FieldMeta[]
  chat_provider_options: string[]
  source: Record<string, string>
  ai_available: boolean
  env_only_note: string
} & Record<string, unknown>

async function api<T>(init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/settings/ai-providers`, {
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

const GROUP_META: Record<string, { title: string; blurb: string; Icon: typeof KeyRound }> = {
  providers: {
    title: 'Provider keys, chat provider & model',
    blurb:
      'Keys, the chat AI provider pin and the model that power text auto-replies and summaries. A dashboard value wins over backend/.env; clear one to fall back to the env default.',
    Icon: KeyRound,
  },
  tunables: {
    title: 'Background & ticker tunables',
    blurb:
      'Timings for the flow engine, channel-health checker and the idle-chat summariser. Some are read when a thread starts, so a change applies on the next restart.',
    Icon: SlidersHorizontal,
  },
}

function providerLabel(o: string): string {
  if (!o) return 'Automatic (priority order)'
  const names: Record<string, string> = {
    openai: 'OpenAI',
    openrouter: 'OpenRouter',
    google: 'Google Gemini',
    ilmu: 'ILMU',
  }
  return names[o] || o
}

/** Where the current value comes from — mirrors app_settings.sources(). */
function SourceChip({ src }: { src?: string }) {
  const map: Record<string, { lbl: string; bg: string; fg: string }> = {
    settings: { lbl: 'from Settings', bg: 'rgba(16,185,129,0.12)', fg: 'var(--green-ink)' },
    env: { lbl: 'from .env', bg: 'rgba(245,158,11,0.12)', fg: 'var(--amber-ink)' },
    '': { lbl: 'not set', bg: 'var(--fill)', fg: 'var(--text-muted)' },
  }
  const s = map[src || ''] || map['']
  return (
    <span
      className="shrink-0 rounded-full px-2 py-px text-[10px] font-bold"
      style={{ background: s.bg, color: s.fg }}
    >
      {s.lbl}
    </span>
  )
}

/**
 * Settings → AI & Providers. Renders ENTIRELY from the endpoint's `fields`
 * schema (`GET /api/settings/ai-providers`): secrets as masked password inputs,
 * the chat provider as a dropdown from `chat_provider_options`, ints as number
 * inputs honouring `min`, each with its `help` and a provenance chip.
 *
 * GET works for any bearer role; PUT is superadmin-only, so non-superadmins see
 * every value read-only. Saves go through the shared header Save/Discard cluster
 * (SettingsDirtyContext) and send ONLY changed fields — untouched masked secrets
 * still carry the "••••" placeholder so the backend treats them as unchanged,
 * and unchanged env-sourced fields are never re-written (which would flip their
 * source from .env to Settings).
 */
export function AiProvidersSettings() {
  const { role, ready } = useRole()
  const isSuper = role === 'superadmin'

  const [meta, setMeta] = useState<FieldMeta[] | null>(null)
  const [options, setOptions] = useState<string[]>([])
  const [source, setSource] = useState<Record<string, string>>({})
  const [note, setNote] = useState('')
  const [aiAvailable, setAiAvailable] = useState(false)
  const [values, setValues] = useState<Record<string, string>>({})
  const [initial, setInitial] = useState<Record<string, string>>({})
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { setDirty, register } = useSettingsDirty()

  // refs so the registered save() closure always sees the latest edits
  const valuesRef = useRef(values)
  valuesRef.current = values
  const initialRef = useRef(initial)
  initialRef.current = initial
  const metaRef = useRef<FieldMeta[] | null>(meta)
  metaRef.current = meta

  const seed = useCallback((p: Payload) => {
    const rec = p as Record<string, unknown>
    const vals: Record<string, string> = {}
    for (const f of p.fields) {
      const raw = f.kind === 'secret' ? rec[`${f.key}_masked`] : rec[f.key]
      vals[f.key] = raw == null ? '' : String(raw)
    }
    setMeta(p.fields)
    setOptions(p.chat_provider_options || [])
    setSource(p.source || {})
    setNote(p.env_only_note || '')
    setAiAvailable(!!p.ai_available)
    setValues(vals)
    setInitial(vals)
  }, [])

  const load = useCallback(async () => {
    try {
      const p = await api<Payload>()
      seed(p)
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
    const fields = metaRef.current || []
    const cur = valuesRef.current
    const init = initialRef.current
    const body: Record<string, string> = {}
    for (const f of fields) {
      const v = cur[f.key] ?? ''
      if (v === (init[f.key] ?? '')) continue // unchanged
      if (f.kind === 'secret' && v.includes('••')) continue // masked placeholder, untouched
      if (f.kind === 'int' && v.trim() === '') continue // empty = no change
      body[f.key] = v
    }
    if (Object.keys(body).length === 0) return
    setError(null)
    try {
      const next = await api<Payload>({ method: 'PUT', body: JSON.stringify(body) })
      seed(next)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      throw e // keep the dirty state so the operator can retry
    }
  }, [seed])

  useEffect(() => {
    register({ save, discard: () => void load() })
    return () => register(null)
  }, [register, save, load])

  const edit = (key: string, v: string) => {
    if (!isSuper) return
    setValues((prev) => ({ ...prev, [key]: v }))
    setDirty(true)
  }

  if (!ready || (meta === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading AI &amp; provider settings…
      </div>
    )
  }

  if (meta === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load AI &amp; provider settings — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  // group order preserved from the schema (providers, then tunables)
  const groups: string[] = []
  for (const f of meta) if (!groups.includes(f.group)) groups.push(f.group)

  return (
    <div className="flex flex-col gap-5 pt-3.5">
      <div className="flex flex-wrap items-center gap-2">
        <span
          className={`rounded-full px-2.5 py-1 text-[11px] font-bold ${
            aiAvailable ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
          }`}
        >
          AI replies {aiAvailable ? 'ready' : 'off — no provider key'}
        </span>
        {!isSuper && (
          <span className="flex items-center gap-1 text-[10.5px] font-semibold text-text-muted">
            <Lock size={11} aria-hidden /> superadmin only to edit
          </span>
        )}
      </div>

      {/* Honest env boundary — the endpoint hands us this note verbatim. */}
      {note && (
        <div className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2.5 text-[11.5px] leading-relaxed text-text-mid">
          <Info size={14} className="mt-0.5 shrink-0" color="#3B82F6" aria-hidden />
          <span>{note}</span>
        </div>
      )}

      {error && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}

      {groups.map((g) => {
        const gm = GROUP_META[g] || { title: g, blurb: '', Icon: KeyRound }
        const Icon = gm.Icon
        const fields = meta.filter((f) => f.group === g)
        return (
          <div key={g} className="rounded-xl border border-line bg-card p-4">
            <div className="flex items-center gap-2">
              <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill">
                <Icon size={14} color="#3B82F6" aria-hidden />
              </span>
              <span className="text-[12.5px] font-bold text-text-high">{gm.title}</span>
            </div>
            {gm.blurb && (
              <p className="mb-3 mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">{gm.blurb}</p>
            )}

            <div className="grid gap-3.5 md:grid-cols-2">
              {fields.map((f) => {
                const val = values[f.key] ?? ''
                const masked = f.kind === 'secret' && val.includes('••')
                return (
                  <label key={f.key} className="block">
                    <span className="mb-1 flex items-center justify-between gap-2">
                      <span className="text-[11px] font-bold text-text-high">{f.label}</span>
                      <SourceChip src={source[f.key]} />
                    </span>

                    {f.kind === 'select' ? (
                      <select
                        value={val}
                        disabled={!isSuper}
                        onChange={(e) => edit(f.key, e.target.value)}
                        className={`${inputCls} ${isSuper ? '' : 'cursor-default bg-fill-subtle'}`}
                      >
                        {options.map((o) => (
                          <option key={o || 'auto'} value={o}>
                            {providerLabel(o)}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <input
                        value={val}
                        readOnly={!isSuper}
                        autoComplete="off"
                        // secret + still-masked => text so the "••••1234" hint is visible;
                        // secret + being edited => password; int => number honouring `min`.
                        type={f.kind === 'secret' ? (masked ? 'text' : 'password') : f.kind === 'int' ? 'number' : 'text'}
                        min={f.kind === 'int' ? f.min : undefined}
                        onChange={(e) => edit(f.key, e.target.value)}
                        className={`${inputCls} ${isSuper ? '' : 'cursor-default bg-fill-subtle'}`}
                      />
                    )}

                    {f.help && (
                      <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{f.help}</span>
                    )}
                  </label>
                )
              })}
            </div>
          </div>
        )
      })}
    </div>
  )
}
