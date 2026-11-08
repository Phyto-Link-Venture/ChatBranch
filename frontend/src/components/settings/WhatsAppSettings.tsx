'use client'

/**
 * WhatsApp-specific pieces of the Inboxes setup (/channels): the per-account
 * "Delivery & AI" editor (quiet hours, send pacing, voice-call LLM/STT), the
 * in-drawer pairing panel for WhatsApp-by-QR numbers (scan a QR, or link with
 * a phone number), the Meta webhook
 * rows and the contacts .vcf export. The account list itself is rendered by
 * ChannelsSettings as inbox cards; these are the parts only WhatsApp has.
 *
 * Linking walkthrough: backend/whatsapp/SETUP.md.
 */

import { useEffect, useState, type ReactNode } from 'react'
import { CheckCircle, Contact, Copy, Loader2, QrCode, RefreshCw, Smartphone } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { PhoneInput } from '@/components/ui/PhoneInput'
import { Segmented } from '@/components/chats/Segmented'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { LLM_PROVIDER_LABELS, STT_PROVIDER_LABELS } from '@/lib/voiceProviders'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'
import { API, EMPTY_DELIVERY, type AccountDelivery } from '@/components/channels/inboxes'
import { absoluteApiResource } from '@/lib/apiBase'

// ── shared bits ──────────────────────────────────────────────────────────────

export const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

/** A read-only value with a copy button (webhook URLs, verify tokens, snippets). */
export function CopyRow({ label, value, multiline = false }: { label: string; value: string; multiline?: boolean }) {
  const t = useT()
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
        title={t('channels.whatsapp.copyLabel', { label })}
        aria-label={t('channels.whatsapp.copyLabel', { label })}
        className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
      >
        {copied ? <CheckCircle size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
      </button>
    </div>
  )
}

/** One webhook serves every Cloud number (routed by the receiving number). */
export function MetaWebhookRows({ verifyToken, path = '/api/whatsapp/webhook' }: { verifyToken: string; path?: string }) {
  const t = useT()
  return (
    <div className="grid grid-cols-1 gap-2">
      <CopyRow label={t('channels.whatsapp.callbackUrl')} value={absoluteApiResource(path)} />
      <CopyRow label={t('channels.whatsapp.verifyToken')} value={verifyToken} />
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
  const t = useT()
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-well px-3 py-2.5">
      <div className="flex items-start gap-2 text-[12px] leading-relaxed text-text-mid">
        <Contact size={14} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
        <span>
          <Trans
            k="channels.whatsapp.vcfTip"
            values={{ b: <b className="text-text-high">{t('channels.whatsapp.vcfTipTitle')}</b> }}
          />
        </span>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() =>
            exportContactsVcf()
              .then(() => setMsg({ ok: true, text: t('channels.whatsapp.vcfDownloaded') }))
              .catch((e) => setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) }))
          }
        >
          <Contact size={13} aria-hidden /> {t('channels.whatsapp.vcfExport')}
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

// ── Pairing a QR number (scan a code, or link with a phone number) ──────────

/** Which way the person is linking the number. */
type PairMethod = 'qr' | 'phone'

interface BaileysState {
  state:
    | 'stopped'
    | 'starting'
    | 'qr'
    | 'qr_expired'
    | 'pairing_code'
    | 'connected'
    | 'disconnected'
    | 'logged_out'
    | 'error'
  qr: string | null
  /** state "pairing_code": the 8 characters to type on the phone. */
  pairing_code?: string | null
  /** the number that code was issued for (digits, no "+"). */
  pairing_phone?: string | null
  me: string | null
  error: string | null
}

/** One pairing attempt the poller follows. `n` makes a retry a new attempt. */
interface PairSession {
  method: PairMethod
  /** E.164 with the "+", for a phone-number attempt ("" for a QR one). */
  phone: string
  n: number
}

async function waApi<T>(path: string, t: TFunction, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error(t('common.errors.sessionExpired'))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/**
 * Inline pairing for a WhatsApp-by-QR account, both ways WhatsApp offers:
 *
 *  * <b>Scan a QR code</b> — the session starts at once and the gateway's QR is
 *    polled until the phone scans it (what this panel always did).
 *  * <b>Use my phone number</b> — nothing is requested until the number is typed
 *    and the button pressed, because WhatsApp rate-limits pairing codes per
 *    number. The 8 characters it answers with are typed on the phone under
 *    Linked devices → "Link with phone number"; no camera is involved, which is
 *    the only way in when the phone cannot point at this screen.
 *
 * Either way the status is polled every 2s until the phone joins; the poller
 * stops on a terminal state (connected, or a gateway error such as no sidecar
 * running) and on unmount — so closing the drawer stops it.
 */
export function BaileysPairPanel({ accountId, onConnected }: { accountId: string; onConnected?: () => void }) {
  const t = useT()
  const [method, setMethod] = useState<PairMethod>('qr')
  const [phone, setPhone] = useState('')
  // null = nothing asked for yet (phone mode, waiting for the number)
  const [session, setSession] = useState<PairSession | null>({ method: 'qr', phone: '', n: 0 })
  const [state, setState] = useState<BaileysState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const begin = (m: PairMethod, e164 = '') => {
    setError(null)
    setState(null)
    setSession((prev) => ({ method: m, phone: e164, n: (prev?.n ?? 0) + 1 }))
  }

  const chooseMethod = (m: PairMethod) => {
    if (m === method) return
    setMethod(m)
    setError(null)
    setState(null)
    // QR pairing can start straight away; a code has to be asked for per number
    if (m === 'qr') begin('qr')
    else setSession(null)
  }

  const askForCode = () => {
    if (phone.replace(/\D/g, '').length < 7) {
      setError(t('channels.whatsapp.pairPhoneNeeded'))
      return
    }
    begin('phone', phone)
  }

  useEffect(() => {
    if (!session) return
    let cancelled = false
    let timer: ReturnType<typeof setInterval> | null = null
    const stop = () => {
      if (timer) clearInterval(timer)
      timer = null
    }
    setError(null)
    setState(null)
    waApi(`accounts/${accountId}/baileys/start`, t, {
      method: 'POST',
      // a QR attempt sends nothing; a phone attempt names the number
      body: session.method === 'phone' ? JSON.stringify({ phone: session.phone }) : undefined,
    }).catch((e) => {
      if (!cancelled) setError(e instanceof Error ? e.message : String(e))
    })
    const tick = async () => {
      try {
        const s = await waApi<BaileysState>(`accounts/${accountId}/baileys/status`, t)
        if (cancelled) return
        setState(s)
        if (s.state === 'qr_expired') {
          setError(t('channels.whatsapp.qrExpired'))
          stop()
          return
        }
        const gatewayError = s.error || (s.state === 'error' ? t('channels.whatsapp.gatewayError') : null)
        if (gatewayError && s.state !== 'qr' && s.state !== 'pairing_code' && s.state !== 'connected') {
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
    timer = setInterval(tick, 2000)
    return () => {
      cancelled = true
      stop()
    }
    // onConnected is a notification only — re-running the effect for a new
    // callback identity would restart the pairing session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId, session])

  const connected = state?.state === 'connected'
  const code = state?.state === 'pairing_code' ? state.pairing_code || '' : ''
  const codeFor = state?.pairing_phone || session?.phone.replace(/\D/g, '') || ''

  const copyCode = async () => {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked — the code is selectable */
    }
  }

  // Retry goes back where the person can act: the form when a number is needed.
  const retry = () => {
    if (method === 'phone' && !phone) setSession(null)
    else begin(method, phone)
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-line bg-card px-4 py-5">
      {!connected && (
        <Segmented<PairMethod>
          value={method}
          onChange={chooseMethod}
          label={t('channels.whatsapp.pairMethod')}
          options={[
            { value: 'qr', label: t('channels.whatsapp.pairByQr') },
            { value: 'phone', label: t('channels.whatsapp.pairByPhone') },
          ]}
        />
      )}
      <div className="flex flex-col items-center gap-3 text-center">
        {error ? (
          <>
            <div className="text-[12.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
              {error}
            </div>
            {state?.state !== 'qr_expired' && (
              <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
                <Trans
                  k="channels.whatsapp.pairNeedsGateway"
                  values={{ cmd: <code className="rounded bg-fill px-1">npm run dev:baileys</code> }}
                />
              </p>
            )}
            <Button variant="secondary" size="sm" onClick={retry}>
              <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
            </Button>
          </>
        ) : connected ? (
          <>
            <CheckCircle size={36} className="text-green-300" aria-hidden />
            <div className="text-[14px] font-bold text-text-heading">
              {state.me ? t('channels.whatsapp.connectedAs', { number: `+${state.me}` }) : t('common.status.connected')}
            </div>
            <p className="text-[12px] text-text-muted">{t('channels.whatsapp.connectedBody')}</p>
          </>
        ) : code ? (
          <>
            <div
              className="select-all font-mono text-[26px] font-bold tracking-[0.25em] text-text-heading"
              aria-label={t('channels.whatsapp.pairCodeAria')}
            >
              {code.length === 8 ? `${code.slice(0, 4)} ${code.slice(4)}` : code}
            </div>
            <Button variant="secondary" size="sm" onClick={copyCode}>
              <Copy size={13} aria-hidden /> {copied ? t('common.actions.copied') : t('common.actions.copy')}
            </Button>
            <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.whatsapp.pairCodeHelp', { number: codeFor ? `+${codeFor}` : '' })}
            </p>
            <span className="flex items-center gap-2 text-[12px] text-text-faint">
              <Loader2 size={13} className="animate-spin" aria-hidden />
              {t('channels.whatsapp.pairCodeWaiting')}
            </span>
          </>
        ) : method === 'phone' && !session ? (
          <>
            <div className="w-full max-w-[330px] text-left">
              <span className="mb-1 block text-[12px] font-semibold text-text-mid">
                {t('channels.whatsapp.pairPhoneLabel')}
              </span>
              <PhoneInput value={phone} onChange={setPhone} ariaLabel={t('channels.whatsapp.pairPhoneLabel')} />
            </div>
            <p className="max-w-[330px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.whatsapp.pairPhoneHelp')}
            </p>
            <Button size="sm" onClick={askForCode}>
              <Smartphone size={13} aria-hidden /> {t('channels.whatsapp.pairPhoneSend')}
            </Button>
          </>
        ) : state?.qr && state.state === 'qr' ? (
          <>
            {/* data-URL QR from the gateway — white box for scan contrast in every theme */}
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src={state.qr} alt={t('channels.whatsapp.qrAlt')} className="h-56 w-56 rounded-lg bg-white p-2" />
            <p className="max-w-[320px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.whatsapp.scanHelp')}
            </p>
          </>
        ) : (
          <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden />
            {state?.state === 'logged_out'
              ? t('channels.whatsapp.loggedOut')
              : method === 'phone'
                ? t('channels.whatsapp.pairCodeAsking')
                : t('channels.whatsapp.contacting')}
          </div>
        )}
      </div>
      <span className="flex items-center justify-center gap-1 text-[11px] text-text-faint">
        <QrCode size={11} aria-hidden /> {t('channels.whatsapp.pairingScope')}
      </span>
    </div>
  )
}

// ── Delivery & AI (per account) ──────────────────────────────────────────────

/** Weekdays, Monday first — mirrors AccountDelivery.Days on the server. */
const DAYS: readonly { key: string; label: MessageKey }[] = [
  { key: 'mon', label: 'channels.whatsapp.days.mon' },
  { key: 'tue', label: 'channels.whatsapp.days.tue' },
  { key: 'wed', label: 'channels.whatsapp.days.wed' },
  { key: 'thu', label: 'channels.whatsapp.days.thu' },
  { key: 'fri', label: 'channels.whatsapp.days.fri' },
  { key: 'sat', label: 'channels.whatsapp.days.sat' },
  { key: 'sun', label: 'channels.whatsapp.days.sun' },
]

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
export function deliveryPatch(s: DeliveryState, t: TFunction): Record<string, string> {
  const d = s.delivery
  const lo = d.queue_min_gap_s.trim()
  const hi = d.queue_max_gap_s.trim()
  if ((lo && !/^\d+$/.test(lo)) || (hi && !/^\d+$/.test(hi)))
    throw new Error(t('channels.whatsapp.errors.pacingWhole'))
  if (lo && hi && Number(lo) > Number(hi)) throw new Error(t('channels.whatsapp.errors.pacingOrder'))
  for (const k of ['quiet_start', 'quiet_end'] as const) {
    const v = d[k].trim()
    if (!s.perDay && v && !HHMM.test(v)) throw new Error(t('channels.whatsapp.errors.quietFormat'))
  }
  const patch: Record<string, string> = {}
  for (const [k, v] of Object.entries(d)) patch[k] = v.trim()
  if (s.perDay) {
    for (const day of DAYS) {
      const r = s.days[day.key]
      if (r.on && (!HHMM.test(r.start) || !HHMM.test(r.end)))
        throw new Error(t('channels.whatsapp.errors.dayFormat', { day: t(day.label) }))
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
  const t = useT()
  const defaults = useDeliveryDefaults()
  const d = value.delivery
  const set = (patch: Partial<AccountDelivery>) => onChange({ ...value, delivery: { ...d, ...patch } })
  const setDay = (key: string, patch: Partial<QuietDay>) =>
    onChange({ ...value, days: { ...value.days, [key]: { ...value.days[key], ...patch } } })

  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
          <span className="text-[12px] font-semibold text-text-high">{t('channels.whatsapp.quietHours')}</span>
          <label className="flex cursor-pointer items-center gap-1.5 text-[11.5px] text-text-muted">
            <input
              type="checkbox"
              checked={value.perDay}
              disabled={disabled}
              onChange={(e) => onChange({ ...value, perDay: e.target.checked })}
              className="h-3.5 w-3.5"
              style={{ accentColor: 'var(--primary)' }}
            />
            {t('channels.whatsapp.perDay')}
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
                aria-label={t('channels.whatsapp.quietStart')}
              />
              <span className="text-[12px] text-text-muted">{t('channels.whatsapp.to')}</span>
              <input
                className={`${inputCls} max-w-[110px] text-center`}
                value={d.quiet_end}
                disabled={disabled}
                onChange={(e) => set({ quiet_end: e.target.value })}
                placeholder={defaults?.quiet_end || '09:00'}
                aria-label={t('channels.whatsapp.quietEnd')}
              />
            </div>
            <Hint>
              {defaults && !defaults.quiet_start && !defaults.quiet_end
                ? t('channels.whatsapp.quietHintNone')
                : t('channels.whatsapp.quietHint')}
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
                        aria-label={t('channels.whatsapp.quietOnDay', { day: t(day.label) })}
                      />
                      <span className="text-[12px] font-semibold text-text-high">{t(day.label)}</span>
                    </label>
                    <input
                      className={`${inputCls} !w-[84px] text-center`}
                      value={r.start}
                      disabled={disabled || !r.on}
                      onChange={(e) => setDay(day.key, { start: e.target.value })}
                      placeholder="21:00"
                      aria-label={t('channels.whatsapp.dayStart', { day: t(day.label) })}
                    />
                    <span className="text-[12px] text-text-muted">{t('channels.whatsapp.to')}</span>
                    <input
                      className={`${inputCls} !w-[84px] text-center`}
                      value={r.end}
                      disabled={disabled || !r.on}
                      onChange={(e) => setDay(day.key, { end: e.target.value })}
                      placeholder="09:00"
                      aria-label={t('channels.whatsapp.dayEnd', { day: t(day.label) })}
                    />
                  </div>
                )
              })}
            </div>
            <Hint>{t('channels.whatsapp.perDayHint')}</Hint>
          </>
        )}
      </div>

      <div>
        <FieldLabel>{t('channels.whatsapp.pacing')}</FieldLabel>
        <div className="flex items-center gap-1.5">
          <input
            className={`${inputCls} max-w-[110px] text-center`}
            inputMode="numeric"
            value={d.queue_min_gap_s}
            disabled={disabled}
            onChange={(e) => set({ queue_min_gap_s: e.target.value })}
            placeholder={defaults?.queue_min_gap_s || '10'}
            aria-label={t('channels.whatsapp.pacingMin')}
          />
          <span className="text-[12px] text-text-muted">{t('channels.whatsapp.to')}</span>
          <input
            className={`${inputCls} max-w-[110px] text-center`}
            inputMode="numeric"
            value={d.queue_max_gap_s}
            disabled={disabled}
            onChange={(e) => set({ queue_max_gap_s: e.target.value })}
            placeholder={defaults?.queue_max_gap_s || '30'}
            aria-label={t('channels.whatsapp.pacingMax')}
          />
        </div>
        <Hint>{t('channels.whatsapp.pacingHint')}</Hint>
      </div>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="block">
          <FieldLabel>{t('channels.whatsapp.llm')}</FieldLabel>
          <Select
            value={d.llm_provider}
            onChange={(v) => set({ llm_provider: v })}
            size="md"
            disabled={disabled}
            ariaLabel={t('channels.whatsapp.llmAria')}
            options={[
              {
                value: '',
                label: defaults
                  ? t('channels.whatsapp.inheritValue', {
                      value: LLM_PROVIDER_LABELS[defaults.llm_provider] ?? defaults.llm_provider,
                    })
                  : t('channels.whatsapp.inherit'),
              },
              ...Object.entries(LLM_PROVIDER_LABELS).map(([v, label]) => ({ value: v, label })),
            ]}
          />
        </label>
        <label className="block">
          <FieldLabel>{t('channels.whatsapp.stt')}</FieldLabel>
          <Select
            value={d.stt_provider}
            onChange={(v) => set({ stt_provider: v })}
            size="md"
            disabled={disabled}
            ariaLabel={t('channels.whatsapp.sttAria')}
            options={[
              {
                value: '',
                label: defaults
                  ? t('channels.whatsapp.inheritValue', {
                      value: STT_PROVIDER_LABELS[defaults.stt_provider] ?? defaults.stt_provider,
                    })
                  : t('channels.whatsapp.inherit'),
              },
              ...Object.entries(STT_PROVIDER_LABELS).map(([v, label]) => ({ value: v, label })),
            ]}
          />
        </label>
      </div>
      <Hint>{t('channels.whatsapp.voiceHint')}</Hint>
    </div>
  )
}
