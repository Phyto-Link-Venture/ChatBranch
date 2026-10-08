'use client'

/**
 * WhatsApp-specific pieces of the Inboxes setup (/channels): the per-account
 * "Delivery & AI" editor (quiet hours, send pacing, voice-call LLM/STT), the
 * in-drawer QR pairing panel for WhatsApp-by-QR numbers, the Meta webhook
 * rows and the contacts .vcf export. The account list itself is rendered by
 * ChannelsSettings as inbox cards; these are the parts only WhatsApp has.
 *
 * Linking walkthrough: backend/whatsapp/SETUP.md.
 */

import { useEffect, useState, type ReactNode } from 'react'
import { CheckCircle, Contact, Copy, Loader2, QrCode, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { LLM_PROVIDER_LABELS, STT_PROVIDER_LABELS } from '@/lib/voiceProviders'
import { API, API_ORIGIN, EMPTY_DELIVERY, type AccountDelivery } from '@/components/channels/inboxes'

// ── shared bits ──────────────────────────────────────────────────────────────

export const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

/** A read-only value with a copy button (webhook URLs, verify tokens, snippets). */
export function CopyRow({ label, value, multiline = false }: { label: string; value: string; multiline?: boolean }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked — the value is selectable */
    }
  }
  return (
    <div className="flex items-start gap-2 rounded-lg border border-line bg-well px-3 py-2">
      <div className="min-w-0 flex-1">
        <div className="eyebrow !text-[10px]">{label}</div>
        <div
          className={`mt-0.5 select-all font-mono text-[11.5px] text-text-high ${
            multiline ? 'whitespace-pre-wrap break-all' : 'truncate'
          }`}
        >
          {value || '—'}
        </div>
      </div>
      <button
        type="button"
        onClick={copy}
        disabled={!value}
        title={`Copy ${label}`}
        aria-label={`Copy ${label}`}
        className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
      >
        {copied ? <CheckCircle size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
      </button>
    </div>
  )
}

/** One webhook serves every Cloud number (routed by the receiving number). */
export function MetaWebhookRows({ verifyToken, path = '/api/whatsapp/webhook' }: { verifyToken: string; path?: string }) {
  return (
    <div className="grid grid-cols-1 gap-2">
      <CopyRow label="Callback URL" value={`${API_ORIGIN}${path}`} />
      <CopyRow label="Verify token" value={verifyToken} />
    </div>
  )
}

/** contacts.vcf for the paired phone — address-book numbers bypass WhatsApp's 463 first-contact lock. */
export async function exportContactsVcf(): Promise<void> {
  const res = await fetch(`${API}/messaging/contacts/vcard`, { headers: authHeaders() })
  if (expireSessionOn401(res)) return
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = 'contacts.vcf'
  a.click()
  URL.revokeObjectURL(url)
}

export function VcfTip() {
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-well px-3 py-2.5">
      <div className="flex items-start gap-2 text-[12px] leading-relaxed text-text-mid">
        <Contact size={14} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
        <span>
          <b className="text-text-high">Messaging new numbers?</b> WhatsApp limits first-contact messages from
          QR-paired numbers (error 463). Import your contacts into the paired phone — numbers in its address book
          are exempt.
        </span>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() =>
            exportContactsVcf()
              .then(() => setMsg({ ok: true, text: 'contacts.vcf downloaded — import it on the phone.' }))
              .catch((e) => setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) }))
          }
        >
          <Contact size={13} aria-hidden /> Export contacts (.vcf)
        </Button>
        {msg && (
          <span className="text-[11.5px] font-semibold" style={{ color: msg.ok ? 'var(--green-ink)' : 'var(--red-ink)' }}>
            {msg.text}
          </span>
        )}
      </div>
    </div>
  )
}

// ── QR pairing (WhatsApp by QR) ──────────────────────────────────────────────

interface BaileysState {
  state: 'stopped' | 'starting' | 'qr' | 'connected' | 'disconnected' | 'logged_out' | 'error'
  qr: string | null
  me: string | null
  error: string | null
}

async function waApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/**
 * Inline QR pairing for a WhatsApp-by-QR account. Starts the gateway session
 * on mount and polls its status every 2s until the phone scans the code; the
 * poller stops on a terminal state (connected, or a gateway error such as no
 * sidecar running) and on unmount — so closing the drawer stops it.
 */
export function BaileysPairPanel({ accountId, onConnected }: { accountId: string; onConnected?: () => void }) {
  const [state, setState] = useState<BaileysState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false
    let t: ReturnType<typeof setInterval> | null = null
    const stop = () => {
      if (t) clearInterval(t)
      t = null
    }
    setError(null)
    setState(null)
    waApi(`accounts/${accountId}/baileys/start`, { method: 'POST' }).catch((e) => {
      if (!cancelled) setError(e instanceof Error ? e.message : String(e))
    })
    const tick = async () => {
      try {
        const s = await waApi<BaileysState>(`accounts/${accountId}/baileys/status`)
        if (cancelled) return
        setState(s)
        const gatewayError = s.error || (s.state === 'error' ? 'The WhatsApp gateway reported an error.' : null)
        if (gatewayError && s.state !== 'qr' && s.state !== 'connected') {
          setError(gatewayError)
          stop()
          return
        }
        setError(null)
        if (s.state === 'connected') {
          stop()
          onConnected?.()
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : String(e))
          stop()
        }
      }
    }
    tick()
    t = setInterval(tick, 2000)
    return () => {
      cancelled = true
      stop()
    }
    // onConnected is a notification only — re-running the effect for a new
    // callback identity would restart the pairing session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId, attempt])

  return (
    <div className="flex flex-col items-center gap-3 rounded-xl border border-line bg-card px-4 py-5 text-center">
      {error ? (
        <>
          <div className="text-[12.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
            {error}
          </div>
          <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
            QR pairing runs in the WhatsApp gateway sidecar (<code className="rounded bg-fill px-1">npm run dev:baileys</code>).
            Start it and retry — or use WhatsApp Business (Cloud API), which needs no gateway.
          </p>
          <Button variant="secondary" size="sm" onClick={() => setAttempt((n) => n + 1)}>
            <RefreshCw size={13} aria-hidden /> Try again
          </Button>
        </>
      ) : state?.state === 'connected' ? (
        <>
          <CheckCircle size={36} className="text-green-300" aria-hidden />
          <div className="text-[14px] font-bold text-text-heading">Connected{state.me ? ` as +${state.me}` : ''}</div>
          <p className="text-[12px] text-text-muted">This number now sends and receives through the inbox.</p>
        </>
      ) : state?.qr && state.state === 'qr' ? (
        <>
          {/* data-URL QR from the gateway — white box for scan contrast in every theme */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={state.qr} alt="WhatsApp pairing QR code" className="h-56 w-56 rounded-lg bg-white p-2" />
          <p className="max-w-[320px] text-[12px] leading-relaxed text-text-muted">
            On the phone: WhatsApp → Settings → Linked devices → Link a device, then scan. The code refreshes on its
            own.
          </p>
        </>
      ) : (
        <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden />
          {state?.state === 'logged_out' ? 'Session was logged out — generating a fresh QR…' : 'Contacting the WhatsApp gateway…'}
        </div>
      )}
      <span className="flex items-center gap-1 text-[11px] text-text-faint">
        <QrCode size={11} aria-hidden /> Pairing session for this inbox only
      </span>
    </div>
  )
}

// ── Delivery & AI (per account) ──────────────────────────────────────────────

/** Weekdays, Monday first — mirrors AccountDelivery.Days on the server. */
const DAYS = [
  { key: 'mon', label: 'Monday' },
  { key: 'tue', label: 'Tuesday' },
  { key: 'wed', label: 'Wednesday' },
  { key: 'thu', label: 'Thursday' },
  { key: 'fri', label: 'Friday' },
  { key: 'sat', label: 'Saturday' },
  { key: 'sun', label: 'Sunday' },
] as const

const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/

interface QuietDay {
  on: boolean
  start: string
  end: string
}

/** Working copy of the editor: the flat keys plus the per-day schedule. */
export interface DeliveryState {
  delivery: AccountDelivery
  perDay: boolean
  days: Record<string, QuietDay>
}

const emptyQuietDays = (): Record<string, QuietDay> =>
  Object.fromEntries(DAYS.map((d) => [d.key, { on: false, start: '21:00', end: '09:00' }]))

/** Stored schedule → working copy; null when nothing usable is stored. */
function parseQuietSchedule(raw: string): Record<string, QuietDay> | null {
  if (!raw.trim()) return null
  try {
    const parsed = JSON.parse(raw) as Record<string, Partial<QuietDay>>
    const days = emptyQuietDays()
    let any = false
    for (const d of DAYS) {
      const r = parsed?.[d.key]
      if (!r) continue
      days[d.key] = {
        on: r.on !== false,
        start: typeof r.start === 'string' ? r.start : days[d.key].start,
        end: typeof r.end === 'string' ? r.end : days[d.key].end,
      }
      any = true
    }
    return any ? days : null
  } catch {
    return null // malformed = fall back to the single window
  }
}

export function deliveryStateFrom(d?: Partial<AccountDelivery> | null): DeliveryState {
  const delivery = { ...EMPTY_DELIVERY, ...(d ?? {}) }
  const parsed = parseQuietSchedule(delivery.quiet_schedule)
  return { delivery, perDay: parsed !== null, days: parsed ?? emptyQuietDays() }
}

/** Validate and flatten into the account write keys ("" = inherit). Throws a readable message. */
export function deliveryPatch(s: DeliveryState): Record<string, string> {
  const d = s.delivery
  const lo = d.queue_min_gap_s.trim()
  const hi = d.queue_max_gap_s.trim()
  if ((lo && !/^\d+$/.test(lo)) || (hi && !/^\d+$/.test(hi)))
    throw new Error('Send pacing gaps must be whole seconds (0 or more).')
  if (lo && hi && Number(lo) > Number(hi)) throw new Error('The minimum send gap cannot exceed the maximum.')
  for (const k of ['quiet_start', 'quiet_end'] as const) {
    const v = d[k].trim()
    if (!s.perDay && v && !HHMM.test(v)) throw new Error('Quiet hours must be HH:MM (24h), or blank to inherit.')
  }
  const patch: Record<string, string> = {}
  for (const [k, v] of Object.entries(d)) patch[k] = v.trim()
  if (s.perDay) {
    for (const day of DAYS) {
      const r = s.days[day.key]
      if (r.on && (!HHMM.test(r.start) || !HHMM.test(r.end)))
        throw new Error(`${day.label} quiet hours must be HH:MM (24h).`)
    }
    patch.quiet_schedule = JSON.stringify(Object.fromEntries(DAYS.map((d2) => [d2.key, s.days[d2.key]])))
  } else {
    patch.quiet_schedule = ''
  }
  return patch
}

/** Organization-wide fallbacks, shown as "inherit" placeholders. */
interface DeliveryDefaults {
  quiet_start: string
  quiet_end: string
  queue_min_gap_s: string
  queue_max_gap_s: string
  llm_provider: string
  stt_provider: string
}

function useDeliveryDefaults(): DeliveryDefaults | null {
  const [defaults, setDefaults] = useState<DeliveryDefaults | null>(null)
  useEffect(() => {
    const get = (p: string) =>
      fetch(`${API}/${p}`, { headers: authHeaders() }).then((r) => (r.ok ? r.json() : Promise.reject()))
    Promise.allSettled([
      get('whatsapp/settings'),
      get('whatsapp/routing-rules'),
      get('voice/llm-settings'),
      get('voice/stt-settings'),
    ]).then(([q, r, l, t]) =>
      setDefaults({
        quiet_start: (q.status === 'fulfilled' && q.value?.quiet_start) || '',
        quiet_end: (q.status === 'fulfilled' && q.value?.quiet_end) || '',
        queue_min_gap_s:
          r.status === 'fulfilled' && r.value?.queue_min_gap_s != null ? String(r.value.queue_min_gap_s) : '10',
        queue_max_gap_s:
          r.status === 'fulfilled' && r.value?.queue_max_gap_s != null ? String(r.value.queue_max_gap_s) : '30',
        llm_provider: (l.status === 'fulfilled' && l.value?.llm_provider) || 'openai',
        stt_provider: (t.status === 'fulfilled' && t.value?.stt_provider) || 'elevenlabs',
      }),
    )
  }, [])
  return defaults
}

function FieldLabel({ children }: { children: ReactNode }) {
  return <span className="mb-1 block text-[12px] font-semibold text-text-high">{children}</span>
}

function Hint({ children }: { children: ReactNode }) {
  return <span className="mt-1 block text-[11.5px] leading-relaxed text-text-muted">{children}</span>
}

/**
 * Per-account Delivery & AI: quiet hours (one window or per weekday), send
 * pacing and the voice-call LLM + STT. Blank = inherit the organization-wide
 * value, shown as the placeholder.
 */
export function DeliveryEditor({
  value,
  onChange,
  disabled,
}: {
  value: DeliveryState
  onChange: (next: DeliveryState) => void
  disabled?: boolean
}) {
  const defaults = useDeliveryDefaults()
  const d = value.delivery
  const set = (patch: Partial<AccountDelivery>) => onChange({ ...value, delivery: { ...d, ...patch } })
  const setDay = (key: string, patch: Partial<QuietDay>) =>
    onChange({ ...value, days: { ...value.days, [key]: { ...value.days[key], ...patch } } })

  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
          <span className="text-[12px] font-semibold text-text-high">Quiet hours</span>
          <label className="flex cursor-pointer items-center gap-1.5 text-[11.5px] text-text-muted">
            <input
              type="checkbox"
              checked={value.perDay}
              disabled={disabled}
              onChange={(e) => onChange({ ...value, perDay: e.target.checked })}
              className="h-3.5 w-3.5"
              style={{ accentColor: 'var(--primary)' }}
            />
            Different hours per day
          </label>
        </div>
        {!value.perDay ? (
          <>
            <div className="flex items-center gap-1.5">
              <input
                className={`${inputCls} max-w-[110px] text-center`}
                value={d.quiet_start}
                disabled={disabled}
                onChange={(e) => set({ quiet_start: e.target.value })}
                placeholder={defaults?.quiet_start || '21:00'}
                aria-label="Quiet hours start"
              />
              <span className="text-[12px] text-text-muted">to</span>
              <input
                className={`${inputCls} max-w-[110px] text-center`}
                value={d.quiet_end}
                disabled={disabled}
                onChange={(e) => set({ quiet_end: e.target.value })}
                placeholder={defaults?.quiet_end || '09:00'}
                aria-label="Quiet hours end"
              />
            </div>
            <Hint>
              Sends from this number wait out this window (HH:MM, server time). A window ending before it starts runs
              past midnight. Blank inherits{defaults && !defaults.quiet_start && !defaults.quiet_end ? ' (none set)' : ''}.
            </Hint>
          </>
        ) : (
          <>
            <div className="flex flex-col gap-1.5">
              {DAYS.map((day) => {
                const r = value.days[day.key]
                return (
                  <div key={day.key} className="flex flex-wrap items-center gap-2">
                    <label className="flex w-[104px] shrink-0 cursor-pointer items-center gap-1.5">
                      <input
                        type="checkbox"
                        checked={r.on}
                        disabled={disabled}
                        onChange={(e) => setDay(day.key, { on: e.target.checked })}
                        className="h-3.5 w-3.5"
                        style={{ accentColor: 'var(--primary)' }}
                        aria-label={`Quiet hours on ${day.label}`}
                      />
                      <span className="text-[12px] font-semibold text-text-high">{day.label}</span>
                    </label>
                    <input
                      className={`${inputCls} !w-[84px] text-center`}
                      value={r.start}
                      disabled={disabled || !r.on}
                      onChange={(e) => setDay(day.key, { start: e.target.value })}
                      placeholder="21:00"
                      aria-label={`${day.label} quiet start`}
                    />
                    <span className="text-[12px] text-text-muted">to</span>
                    <input
                      className={`${inputCls} !w-[84px] text-center`}
                      value={r.end}
                      disabled={disabled || !r.on}
                      onChange={(e) => setDay(day.key, { end: e.target.value })}
                      placeholder="09:00"
                      aria-label={`${day.label} quiet end`}
                    />
                  </div>
                )
              })}
            </div>
            <Hint>Unticked days never go quiet. The same start and end means the whole day is quiet.</Hint>
          </>
        )}
      </div>

      <div>
        <FieldLabel>Send pacing (seconds between sends)</FieldLabel>
        <div className="flex items-center gap-1.5">
          <input
            className={`${inputCls} max-w-[110px] text-center`}
            inputMode="numeric"
            value={d.queue_min_gap_s}
            disabled={disabled}
            onChange={(e) => set({ queue_min_gap_s: e.target.value })}
            placeholder={defaults?.queue_min_gap_s || '10'}
            aria-label="Send pacing minimum gap"
          />
          <span className="text-[12px] text-text-muted">to</span>
          <input
            className={`${inputCls} max-w-[110px] text-center`}
            inputMode="numeric"
            value={d.queue_max_gap_s}
            disabled={disabled}
            onChange={(e) => set({ queue_max_gap_s: e.target.value })}
            placeholder={defaults?.queue_max_gap_s || '30'}
            aria-label="Send pacing maximum gap"
          />
        </div>
        <Hint>A jittered gap between queued sends — keeps personal numbers human-paced.</Hint>
      </div>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="block">
          <FieldLabel>Voice call brain (LLM)</FieldLabel>
          <Select
            value={d.llm_provider}
            onChange={(v) => set({ llm_provider: v })}
            size="md"
            disabled={disabled}
            ariaLabel="Voice call LLM provider"
            options={[
              {
                value: '',
                label: `Inherit${defaults ? ` (${LLM_PROVIDER_LABELS[defaults.llm_provider] ?? defaults.llm_provider})` : ''}`,
              },
              ...Object.entries(LLM_PROVIDER_LABELS).map(([v, label]) => ({ value: v, label })),
            ]}
          />
        </label>
        <label className="block">
          <FieldLabel>Call speech-to-text</FieldLabel>
          <Select
            value={d.stt_provider}
            onChange={(v) => set({ stt_provider: v })}
            size="md"
            disabled={disabled}
            ariaLabel="Voice call STT provider"
            options={[
              {
                value: '',
                label: `Inherit${defaults ? ` (${STT_PROVIDER_LABELS[defaults.stt_provider] ?? defaults.stt_provider})` : ''}`,
              },
              ...Object.entries(STT_PROVIDER_LABELS).map(([v, label]) => ({ value: v, label })),
            ]}
          />
        </label>
      </div>
      <Hint>The LLM and STT apply to ElevenLabs-voice calls placed from this number, from the next call on.</Hint>
    </div>
  )
}
