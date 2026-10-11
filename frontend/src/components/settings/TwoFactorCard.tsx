'use client'

import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { AlertCircle, Check, Copy, Download, KeyRound, Loader2, ShieldCheck, Smartphone } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import {
  minutesFrom,
  qrDataUrl,
  SecurityError,
  twoFactor,
  type TwoFactorSetup,
  type TwoFactorStatus,
} from '@/lib/security'
import { useFormat, useT } from '@/i18n'

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

type Mode = 'idle' | 'setup' | 'reauth-device' | 'codes' | 'off'

function Notice({ kind, children }: { kind: 'ok' | 'error'; children: string }) {
  return kind === 'ok' ? (
    <div role="status" className="flex items-start gap-2 rounded-lg border border-green/25 bg-green/10 px-3 py-2 text-[12.5px] text-[color:var(--green-ink)]">
      <Check size={14} className="mt-0.5 shrink-0" aria-hidden /> {children}
    </div>
  ) : (
    <div role="alert" className="flex items-start gap-2 rounded-lg border border-red/25 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
      <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {children}
    </div>
  )
}

/**
 * Two-step sign-in, the person's own (Settings → My account, and the forced
 * setup on /account-setup): set up with a QR code or a typed key, confirm with
 * a code, save 10 recovery codes (shown once), move to a new phone, make new
 * recovery codes, turn it off (password + code; not while the organization
 * requires it). Every write is session-only on the server.
 */
export function TwoFactorCard({
  forced = false,
  onDone,
  disabled = false,
}: {
  /** The account-setup screen: straight into setup, no "turn off". */
  forced?: boolean
  /** Called once two-step sign-in is on and the recovery codes were acknowledged. */
  onDone?: () => void
  /** Signed in with an API key — nothing here can change. */
  disabled?: boolean
}) {
  const t = useT()
  const f = useFormat()
  const [status, setStatus] = useState<TwoFactorStatus | null>(null)
  const [mode, setMode] = useState<Mode>('idle')
  const [setup, setSetup] = useState<TwoFactorSetup | null>(null)
  const [codes, setCodes] = useState<string[]>([])
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)
  const [copied, setCopied] = useState<'key' | 'codes' | null>(null)
  // what the password + code form is for, in the "enabled" state
  const [action, setAction] = useState<'codes' | 'off' | null>(null)

  const explain = useCallback(
    (e: unknown): string => {
      if (e instanceof SecurityError) {
        switch (e.code) {
          case 'invalid_code':
            return t('account.twoFactor.invalidCode')
          case 'wrong_password':
            return t('account.twoFactor.wrongPassword')
          case 'two_factor_required':
            return t('account.twoFactor.requiredCantDisable')
          case 'too_many_attempts':
            return t('auth.login.locked', { count: minutesFrom(e.retryAfter) })
        }
      }
      return e instanceof Error ? e.message : String(e)
    },
    [t],
  )

  const load = useCallback(async () => {
    try {
      setStatus(await twoFactor.status())
    } catch (e) {
      setMsg({ kind: 'error', text: explain(e) })
    }
  }, [explain])

  useEffect(() => {
    void load()
  }, [load])

  const begin = useCallback(
    async (pw?: string) => {
      setBusy(true)
      setMsg(null)
      try {
        setSetup(await twoFactor.setup(pw))
        setCode('')
        setPassword('')
        setMode('setup')
      } catch (e) {
        setMsg({ kind: 'error', text: explain(e) })
      } finally {
        setBusy(false)
      }
    },
    [explain],
  )

  // the forced screen opens straight on the QR code
  useEffect(() => {
    if (forced && status && !status.enabled && mode === 'idle' && !setup && !busy) void begin()
  }, [forced, status, mode, setup, busy, begin])

  const confirm = async (e: FormEvent) => {
    e.preventDefault()
    if (!code.trim()) return
    setBusy(true)
    setMsg(null)
    try {
      const r = await twoFactor.confirm(code.trim())
      setStatus(r.status)
      setCodes(r.recovery_codes)
      setSetup(null)
      setCode('')
      setMode('codes')
    } catch (err) {
      setMsg({ kind: 'error', text: explain(err) })
    } finally {
      setBusy(false)
    }
  }

  const passwordAndCode = async (e: FormEvent) => {
    e.preventDefault()
    if (!password || !code.trim() || !action) return
    setBusy(true)
    setMsg(null)
    try {
      if (action === 'codes') {
        const r = await twoFactor.newRecoveryCodes(password, code.trim())
        setStatus(r.status)
        setCodes(r.recovery_codes)
        setMode('codes')
        setMsg({ kind: 'ok', text: t('account.twoFactor.codesReplaced') })
      } else {
        const r = await twoFactor.disable(password, code.trim())
        setStatus(r.status)
        setMode('idle')
        setMsg({ kind: 'ok', text: t('account.twoFactor.disabled') })
      }
      setAction(null)
      setPassword('')
      setCode('')
    } catch (err) {
      setMsg({ kind: 'error', text: explain(err) })
    } finally {
      setBusy(false)
    }
  }

  const copy = async (text: string, what: 'key' | 'codes') => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(what)
      window.setTimeout(() => setCopied(null), 1500)
    } catch {
      /* clipboard blocked — the text is on screen to copy by hand */
    }
  }

  const download = () => {
    const body = `${t('account.twoFactor.recoveryTitle')}\n\n${codes.join('\n')}\n`
    const url = URL.createObjectURL(new Blob([body], { type: 'text/plain;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url
    a.download = 'chatbranch-recovery-codes.txt'
    a.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }

  const savedCodes = () => {
    setCodes([])
    setMode('idle')
    setMsg({ kind: 'ok', text: t('account.twoFactor.enabled') })
    onDone?.()
  }

  const cancel = () => {
    setMode('idle')
    setSetup(null)
    setAction(null)
    setCode('')
    setPassword('')
    setMsg(null)
  }

  return (
    <Card className="p-4 sm:p-5" data-tour="account-2fa">
      <CardTitle
        right={
          status && (
            <span
              className={`inline-flex items-center gap-1 font-semibold ${
                status.enabled ? 'text-[color:var(--green-ink)]' : 'text-text-muted'
              }`}
            >
              {status.enabled && <ShieldCheck size={13} aria-hidden />}
              {status.enabled ? t('account.twoFactor.statusOn') : t('account.twoFactor.statusOff')}
            </span>
          )
        }
      >
        {t('account.twoFactor.title')}
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('account.twoFactor.intro')}</p>

      <div className="mt-4 flex flex-col gap-3">
        {msg && <Notice kind={msg.kind}>{msg.text}</Notice>}
        {status?.required && !forced && (
          <p className="text-[12px] font-semibold text-text-high">{t('account.twoFactor.required')}</p>
        )}

        {!status ? (
          <div className="flex items-center gap-2 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </div>
        ) : mode === 'codes' ? (
          <div className="flex flex-col gap-3">
            <div>
              <div className="text-[13px] font-semibold text-text-high">{t('account.twoFactor.recoveryTitle')}</div>
              <p className="mt-0.5 text-[12px] text-text-muted">{t('account.twoFactor.recoveryIntro')}</p>
            </div>
            <ol className="grid grid-cols-2 gap-x-6 gap-y-1 rounded-lg bg-fill-subtle px-4 py-3 font-mono text-[13px] text-text-high sm:grid-cols-2">
              {codes.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ol>
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="secondary" size="sm" onClick={download}>
                <Download size={13} aria-hidden /> {t('account.twoFactor.download')}
              </Button>
              <Button variant="secondary" size="sm" onClick={() => void copy(codes.join('\n'), 'codes')}>
                {copied === 'codes' ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
                {copied === 'codes' ? t('account.twoFactor.copied') : t('account.twoFactor.copyAll')}
              </Button>
              <Button size="sm" className="ml-auto" onClick={savedCodes}>
                {t('account.twoFactor.savedThem')}
              </Button>
            </div>
          </div>
        ) : mode === 'setup' && setup ? (
          <form onSubmit={(e) => void confirm(e)} className="flex flex-col gap-3">
            <div className="text-[13px] font-semibold text-text-high">{t('account.twoFactor.scan')}</div>
            <div className="flex flex-wrap items-start gap-4">
              {setup.qr_svg && (
                // a data: URL keeps the server's SVG out of the DOM as markup
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={qrDataUrl(setup.qr_svg)}
                  alt={t('account.twoFactor.qrAlt')}
                  width={176}
                  height={176}
                  className="rounded-lg border border-line"
                />
              )}
              <div className="min-w-0 flex-1">
                <p className="text-[12px] text-text-muted">{t('account.twoFactor.manual')}</p>
                <div className="mt-1.5 flex flex-wrap items-center gap-2">
                  <code className="break-all rounded-md bg-fill-subtle px-2 py-1 font-mono text-[13px] text-text-high">
                    {setup.manual_key}
                  </code>
                  <Button variant="ghost" size="sm" onClick={() => void copy(setup.manual_key.replace(/\s/g, ''), 'key')}>
                    {copied === 'key' ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
                    {copied === 'key' ? t('account.twoFactor.copied') : t('account.twoFactor.copyKey')}
                  </Button>
                </div>
              </div>
            </div>
            <label className="block max-w-[260px]">
              <span className="mb-1 block text-[11.5px] font-bold text-text-high">{t('account.twoFactor.enterCode')}</span>
              <input
                value={code}
                onChange={(e) => setCode(e.target.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
                placeholder="123456"
                maxLength={8}
                aria-label={t('account.twoFactor.codeLabel')}
                className={`${inputCls} font-mono tracking-[0.2em]`}
              />
            </label>
            <div className="flex flex-wrap justify-end gap-2">
              {!forced && (
                <Button variant="ghost" size="sm" onClick={cancel} disabled={busy}>
                  {t('account.twoFactor.cancel')}
                </Button>
              )}
              <Button type="submit" size="sm" disabled={busy || !code.trim()}>
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('account.twoFactor.turnOn')}
              </Button>
            </div>
          </form>
        ) : mode === 'reauth-device' ? (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (password) void begin(password)
            }}
            className="flex flex-col gap-3"
          >
            <p className="text-[12.5px] text-text-mid">{t('account.twoFactor.passwordForNewDevice')}</p>
            <label className="block max-w-[320px]">
              <span className="mb-1 block text-[11.5px] font-bold text-text-high">{t('account.twoFactor.passwordLabel')}</span>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                className={inputCls}
              />
            </label>
            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={cancel} disabled={busy}>
                {t('account.twoFactor.cancel')}
              </Button>
              <Button type="submit" size="sm" disabled={busy || !password}>
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('account.twoFactor.confirmChange')}
              </Button>
            </div>
          </form>
        ) : action ? (
          <form onSubmit={(e) => void passwordAndCode(e)} className="flex flex-col gap-3">
            <p className="text-[12.5px] text-text-mid">
              {action === 'off' ? t('account.twoFactor.turnOffIntro') : t('account.twoFactor.newCodesIntro')}
            </p>
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="block">
                <span className="mb-1 block text-[11.5px] font-bold text-text-high">{t('account.twoFactor.passwordLabel')}</span>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  className={inputCls}
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[11.5px] font-bold text-text-high">{t('account.twoFactor.codeOrRecovery')}</span>
                <input
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  autoComplete="one-time-code"
                  maxLength={20}
                  className={`${inputCls} font-mono`}
                />
              </label>
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={cancel} disabled={busy}>
                {t('account.twoFactor.cancel')}
              </Button>
              <Button
                type="submit"
                size="sm"
                variant={action === 'off' ? 'danger' : 'primary'}
                disabled={busy || !password || !code.trim()}
              >
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {action === 'off' ? t('account.twoFactor.turnOff') : t('account.twoFactor.newCodes')}
              </Button>
            </div>
          </form>
        ) : status.enabled ? (
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap gap-x-6 gap-y-1 text-[12.5px] text-text-mid">
              {status.confirmed_at && (
                <span>{t('account.twoFactor.onSince', { date: f.date(status.confirmed_at) })}</span>
              )}
              <span>{t('account.twoFactor.recoveryLeft', { count: status.recovery_codes_left })}</span>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="secondary"
                size="sm"
                disabled={disabled || busy}
                onClick={() => {
                  setMsg(null)
                  setMode('reauth-device')
                }}
              >
                <Smartphone size={13} aria-hidden /> {t('account.twoFactor.newDevice')}
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={disabled || busy}
                onClick={() => {
                  setMsg(null)
                  setAction('codes')
                }}
              >
                <KeyRound size={13} aria-hidden /> {t('account.twoFactor.newCodes')}
              </Button>
              {!forced && !status.required && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={disabled || busy}
                  onClick={() => {
                    setMsg(null)
                    setAction('off')
                  }}
                  className="sm:ml-auto"
                >
                  {t('account.twoFactor.turnOff')}
                </Button>
              )}
            </div>
          </div>
        ) : (
          <div className="flex justify-end">
            <Button size="sm" disabled={disabled || busy} onClick={() => void begin()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ShieldCheck size={13} aria-hidden />}
              {t('account.twoFactor.setUp')}
            </Button>
          </div>
        )}
      </div>
    </Card>
  )
}
