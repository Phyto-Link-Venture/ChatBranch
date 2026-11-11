'use client'

/**
 * Pairing a WhatsApp number through WAHA — the operator's OWN self-hosted WhatsApp HTTP API
 * (github.com/devlikeapro/waha). Both ways WhatsApp offers, as WAHA exposes them:
 *
 *  * **Scan a QR code** — `POST …/waha/start` creates or updates the session with this
 *    inbox's signed webhook and starts it; the panel then polls `…/waha/status`, which
 *    carries a fresh base64 QR (WAHA's codes expire: 60 s for the first, 20 s after).
 *  * **Use my phone number** — nothing is asked for until the number is typed, because
 *    WhatsApp rate-limits pairing codes per number. WAHA answers with the 8 characters to
 *    type under WhatsApp → Linked devices → "Link with phone number".
 *
 * The status word shown is WAHA's own (STARTING · SCAN_QR_CODE · PASSKEY_REQUIRED ·
 * PASSKEY_CONFIRMATION_REQUIRED · WORKING · FAILED · STOPPED). **The pairing states are
 * progress, never a hard error** — the backend marks them `transient`, and this panel keeps
 * waiting on them rather than telling someone to go and fix something that is not broken.
 * Only an unreachable WAHA, a refused API key or a FAILED session is an error here, and each
 * says what actually went wrong.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertTriangle, CheckCircle, Copy, Loader2, RefreshCw, Server, Smartphone } from 'lucide-react'
import { API, type WahaState } from '@/components/channels/inboxes'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { Button } from '@/components/ui/Button'
import { PhoneInput } from '@/components/ui/PhoneInput'
import { Segmented } from '@/components/chats/Segmented'
import { useT, type TFunction } from '@/i18n'

type PairMethod = 'qr' | 'phone'

/** One pairing attempt the poller follows; `n` makes a retry a new attempt. */
interface Attempt {
  method: PairMethod
  /** E.164 with the "+", for a phone-number attempt ("" for a QR one). */
  phone: string
  n: number
}

async function wahaApi<T>(path: string, t: TFunction, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error(t('common.errors.sessionExpired'))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export function WahaPairPanel({ accountId, onConnected }: { accountId: string; onConnected?: () => void }) {
  const t = useT()
  const [method, setMethod] = useState<PairMethod>('qr')
  const [phone, setPhone] = useState('')
  // null = nothing asked for yet (phone mode, waiting for the number)
  const [attempt, setAttempt] = useState<Attempt | null>({ method: 'qr', phone: '', n: 0 })
  const [state, setState] = useState<WahaState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const connectedRef = useRef(false)
  /** This attempt showed a QR at some point — so a FAILED after it is "nobody scanned", not a ban. */
  const sawQrRef = useRef(false)

  const begin = (m: PairMethod, e164 = '') => {
    setError(null)
    setState(null)
    setAttempt((prev) => ({ method: m, phone: e164, n: (prev?.n ?? 0) + 1 }))
  }

  const chooseMethod = (m: PairMethod) => {
    if (m === method) return
    setMethod(m)
    setError(null)
    setState(null)
    // a QR session can start straight away; a code has to be asked for per number
    if (m === 'qr') begin('qr')
    else setAttempt(null)
  }

  const askForCode = () => {
    if (phone.replace(/\D/g, '').length < 7) {
      setError(t('channels.waha.pairPhoneNeeded'))
      return
    }
    begin('phone', phone)
  }

  useEffect(() => {
    if (!attempt) return
    let cancelled = false
    let timer: ReturnType<typeof setInterval> | null = null
    const stop = () => {
      if (timer) clearInterval(timer)
      timer = null
    }
    setError(null)
    setState(null)
    connectedRef.current = false
    sawQrRef.current = false

    const read = (s: WahaState) => {
      setState(s)
      if (s.status === 'SCAN_QR_CODE') sawQrRef.current = true
      // WAHA itself unreachable, or it refused us: a real error, and the message says which
      if (!s.reachable) {
        setError(s.detail || t('channels.waha.unreachable'))
        stop()
        return
      }
      // FAILED is the one session state that is a fault — but a FAILED that follows a QR
      // nobody scanned is just a timed-out code, not a number in trouble
      if (s.status === 'FAILED') {
        setError(sawQrRef.current ? t('channels.waha.qrExpired') : s.detail || t('channels.waha.sessionFailed'))
        stop()
        return
      }
      setError(null)
      if (s.working && !connectedRef.current) {
        connectedRef.current = true
        stop()
        onConnected?.()
      }
    }

    const fail = (e: unknown) => {
      if (cancelled) return
      setError(e instanceof Error ? e.message : String(e))
      stop()
    }

    // Look BEFORE touching anything: WAHA's session update stops and restarts the session,
    // so a paired, working number must not be disturbed by someone opening this drawer.
    wahaApi<WahaState>(`accounts/${accountId}/waha/status`, t)
      .then((first) => {
        if (cancelled) return
        read(first)
        if (first.working) return
        return wahaApi<WahaState>(`accounts/${accountId}/waha/start`, t, {
          method: 'POST',
          // a QR attempt sends nothing; a phone attempt names the number
          body: attempt.method === 'phone' ? JSON.stringify({ phone: attempt.phone }) : JSON.stringify({}),
        }).then((s) => {
          if (!cancelled) read(s)
        })
      })
      .catch(fail)

    const tick = async () => {
      try {
        const s = await wahaApi<WahaState>(`accounts/${accountId}/waha/status`, t)
        if (!cancelled) read(s)
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : String(e))
          stop()
        }
      }
    }
    // WAHA's QR codes expire in ~20s, so a slow poll would show a dead one
    timer = setInterval(tick, 3000)
    return () => {
      cancelled = true
      stop()
    }
    // onConnected is a notification only — re-running for a new callback identity
    // would restart the pairing session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId, attempt])

  const connected = !!state?.working
  const code = state?.pairing_code || ''
  const confirmCode = state?.confirm_code || ''
  const codeFor = state?.pairing_phone || attempt?.phone.replace(/\D/g, '') || ''

  const copyCode = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked — the code is selectable */
    }
  }, [code])

  // Retry goes back where the person can act: the form when a number is needed.
  const retry = () => {
    if (method === 'phone' && !phone) setAttempt(null)
    else begin(method, phone)
  }

  /** What is happening, in WAHA's own words, while we wait. */
  const waiting = () => {
    switch (state?.status) {
      case 'PASSKEY_REQUIRED':
      case 'PASSKEY_CONFIRMATION_REQUIRED':
        return t('channels.waha.passkey')
      case 'STOPPED':
      case 'STARTING':
        return t('channels.waha.starting')
      default:
        return method === 'phone' ? t('channels.waha.pairCodeAsking') : t('channels.waha.contacting')
    }
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-line bg-card px-4 py-5">
      {!connected && (
        <Segmented<PairMethod>
          value={method}
          onChange={chooseMethod}
          label={t('channels.waha.pairMethod')}
          options={[
            { value: 'qr', label: t('channels.waha.pairByQr') },
            { value: 'phone', label: t('channels.waha.pairByPhone') },
          ]}
        />
      )}
      <div className="flex flex-col items-center gap-3 text-center">
        {error ? (
          <>
            <AlertTriangle size={22} aria-hidden style={{ color: 'var(--red-ink)' }} />
            <div className="text-[12.5px] font-semibold" style={{ color: 'var(--red-ink)' }}>
              {error}
            </div>
            {/* the remedy matches the cause: WAHA is the operator's own server, not a sidecar of ours */}
            <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
              {state && !state.reachable ? t('channels.waha.unreachableHelp') : t('channels.waha.failedHelp')}
            </p>
            <Button variant="secondary" size="sm" onClick={retry}>
              <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
            </Button>
          </>
        ) : connected ? (
          <>
            <CheckCircle size={36} className="text-green-300" aria-hidden />
            <div className="text-[14px] font-bold text-text-heading">
              {state?.me
                ? t('channels.waha.connectedAs', { number: `+${state.me}` })
                : t('common.status.connected')}
            </div>
            <p className="text-[12px] text-text-muted">{t('channels.waha.connectedBody')}</p>
          </>
        ) : confirmCode ? (
          <>
            <div
              className="select-all font-mono text-[26px] font-bold tracking-[0.25em] text-text-heading"
              aria-label={t('channels.waha.confirmCodeAria')}
            >
              {confirmCode}
            </div>
            <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.waha.confirmCodeHelp')}
            </p>
            <span className="flex items-center gap-2 text-[12px] text-text-faint">
              <Loader2 size={13} className="animate-spin" aria-hidden />
              {t('channels.waha.pairCodeWaiting')}
            </span>
          </>
        ) : code ? (
          <>
            <div
              className="select-all font-mono text-[26px] font-bold tracking-[0.25em] text-text-heading"
              aria-label={t('channels.waha.pairCodeAria')}
            >
              {code}
            </div>
            <Button variant="secondary" size="sm" onClick={copyCode}>
              <Copy size={13} aria-hidden /> {copied ? t('common.actions.copied') : t('common.actions.copy')}
            </Button>
            <p className="max-w-[340px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.waha.pairCodeHelp', { number: codeFor ? `+${codeFor}` : '' })}
            </p>
            <span className="flex items-center gap-2 text-[12px] text-text-faint">
              <Loader2 size={13} className="animate-spin" aria-hidden />
              {t('channels.waha.pairCodeWaiting')}
            </span>
          </>
        ) : method === 'phone' && !attempt ? (
          <>
            <div className="w-full max-w-[330px] text-left">
              <span className="mb-1 block text-[12px] font-semibold text-text-mid">
                {t('channels.waha.pairPhoneLabel')}
              </span>
              <PhoneInput value={phone} onChange={setPhone} ariaLabel={t('channels.waha.pairPhoneLabel')} />
            </div>
            <p className="max-w-[330px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.waha.pairPhoneHelp')}
            </p>
            <Button size="sm" onClick={askForCode}>
              <Smartphone size={13} aria-hidden /> {t('channels.waha.pairPhoneSend')}
            </Button>
          </>
        ) : state?.qr && state.status === 'SCAN_QR_CODE' ? (
          <>
            {/* base64 PNG from WAHA — white box for scan contrast in every theme */}
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src={state.qr} alt={t('channels.waha.qrAlt')} className="h-56 w-56 rounded-lg bg-white p-2" />
            <p className="max-w-[320px] text-[12px] leading-relaxed text-text-muted">
              {t('channels.waha.scanHelp')}
            </p>
          </>
        ) : (
          <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden />
            {waiting()}
          </div>
        )}
      </div>
      <span className="flex items-center justify-center gap-1 text-[11px] text-text-faint">
        <Server size={11} aria-hidden />{' '}
        {t('channels.waha.sessionScope', {
          session: state?.session || 'default',
          engine: state?.engine || t('channels.waha.engineUnknown'),
        })}
      </span>
      {state?.reachable && !state.webhook_configured && (
        <span className="text-center text-[11px]" style={{ color: 'var(--amber-ink)' }}>
          {t('channels.waha.noWebhookKey')}
        </span>
      )}
    </div>
  )
}
