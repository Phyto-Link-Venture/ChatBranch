'use client'

import Link from 'next/link'
import { useEffect, useState, type FormEvent } from 'react'
import { useRouter } from 'next/navigation'
import { AlertCircle, Check, Circle, Eye, EyeOff, Link2Off, Loader2 } from 'lucide-react'
import { API } from '@/lib/apiBase'
import { passwordProblem } from '@/lib/security'
import { Logo } from '@/components/shell/Logo'
import { LanguageSwitcher } from '@/components/i18n/LanguageSwitcher'
import { useFormat, useT } from '@/i18n'

const INPUT =
  'h-11 w-full rounded-lg border border-line-strong bg-card px-3 text-[14px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

/** GET /api/auth/set-password/{token} for a live link. */
interface LinkInfo {
  username: string
  purpose: 'invite' | 'reset'
  expires_at: string
  password_min_length: number
  password_policy: 'strict' | 'demo'
}

type State =
  | { kind: 'checking' }
  | { kind: 'invalid' }
  | { kind: 'limited' }
  | { kind: 'ready'; token: string; info: LinkInfo }

/**
 * The welcome email's link (and an admin's password reset link): choose your own
 * password. Public — rendered bare by the Shell like /login; the one-time token in
 * the URL is the credential (the API stores only its hash, answers ONE generic 404
 * for an unknown, used, replaced or expired link, and limits attempts per address).
 * The rules are shown as the person types; the server's password policy decides.
 * On success: /login with a "password set — sign in" notice (two-step sign-in, when
 * the organization requires it, is handled by the sign-in).
 */
export default function SetPasswordPage() {
  const t = useT()
  const f = useFormat()
  const router = useRouter()
  const [state, setState] = useState<State>({ kind: 'checking' })
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [show, setShow] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const token = new URLSearchParams(window.location.search).get('token') ?? ''
    if (!token) {
      setState({ kind: 'invalid' })
      return
    }
    let cancelled = false
    fetch(`${API}/auth/set-password/${encodeURIComponent(token)}`)
      .then(async (res) => {
        if (cancelled) return
        if (res.status === 429) return setState({ kind: 'limited' })
        if (!res.ok) return setState({ kind: 'invalid' })
        setState({ kind: 'ready', token, info: (await res.json()) as LinkInfo })
      })
      .catch(() => !cancelled && setError(t('common.errors.network')))
    return () => {
      cancelled = true
    }
  }, [t])

  const info = state.kind === 'ready' ? state.info : null
  const min = info?.password_min_length ?? 10
  const rules = info
    ? [
        { ok: password.length >= min, text: t('auth.setPassword.rules.length', { min }) },
        {
          ok: password.length > 0 && password.trim().toLowerCase() !== info.username.toLowerCase(),
          text: t('auth.setPassword.rules.notUsername'),
        },
        ...(info.password_policy === 'strict'
          ? [{ ok: null as boolean | null, text: t('auth.setPassword.rules.notCommon') }]
          : []),
        { ok: password.length > 0 && password === confirm, text: t('auth.setPassword.rules.match') },
      ]
    : []

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (state.kind !== 'ready') return
    setError(null)
    if (password.length < min) return setError(t('account.password.tooShort', { min }))
    if (password !== confirm) return setError(t('auth.setPassword.mismatch'))
    setBusy(true)
    try {
      const res = await fetch(`${API}/auth/set-password`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: state.token, password }),
      })
      if (res.ok) {
        router.replace('/login?password_set=1')
        return
      }
      const data = (await res.json().catch(() => ({}))) as { detail?: string; code?: string; min?: number }
      if (res.status === 404) setState({ kind: 'invalid' })
      else if (res.status === 429) setError(t('auth.setPassword.rateLimited'))
      else setError(passwordProblem(t, data.code, data.min) ?? data.detail ?? `HTTP ${res.status}`)
    } catch {
      setError(t('common.errors.network'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center overflow-y-auto bg-app px-4 py-10">
      <div className="w-full max-w-[420px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>

        {state.kind === 'checking' && !error && (
          <div role="status" className="flex items-center justify-center gap-2 py-10 text-[13px] text-text-muted">
            <Loader2 size={15} className="animate-spin" aria-hidden /> {t('auth.setPassword.checking')}
          </div>
        )}

        {(state.kind === 'invalid' || state.kind === 'limited') && (
          <div className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8 sm:py-8">
            <Link2Off size={22} className="text-text-muted" aria-hidden />
            <h1 className="display mt-3 text-[26px] leading-[1.1] text-text-heading">
              {state.kind === 'limited' ? t('auth.setPassword.rateLimited') : t('auth.setPassword.invalidTitle')}
            </h1>
            {state.kind === 'invalid' && (
              <p className="mt-2 text-[13.5px] leading-relaxed text-text-muted">{t('auth.setPassword.invalidBody')}</p>
            )}
            <Link
              href="/login"
              className="mt-6 flex h-11 w-full items-center justify-center rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90"
            >
              {t('auth.setPassword.toLogin')}
            </Link>
          </div>
        )}

        {state.kind === 'ready' && info && (
          <form
            onSubmit={(e) => void submit(e)}
            className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8 sm:py-8"
            aria-labelledby="set-password-title"
          >
            <div className="eyebrow">{t('auth.setPassword.eyebrow')}</div>
            <h1 id="set-password-title" className="display mt-1 text-[30px] leading-[1.05] text-text-heading">
              {info.purpose === 'reset' ? t('auth.setPassword.titleReset') : t('auth.setPassword.titleInvite')}
            </h1>
            <p className="mt-1.5 text-[13.5px] text-text-muted">
              {t('auth.setPassword.subtitle', { username: info.username })}
            </p>
            <div className="mt-5 border-t border-text-primary/80" />

            {/* the username, so password managers file the new password under the right login */}
            <input type="text" name="username" value={info.username} autoComplete="username" readOnly hidden />

            <label className="mt-6 block">
              <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('auth.setPassword.password')}</span>
              <span className="relative block">
                <input
                  type={show ? 'text' : 'password'}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="new-password"
                  autoFocus
                  className={`${INPUT} pr-11`}
                />
                <button
                  type="button"
                  onClick={() => setShow((v) => !v)}
                  aria-label={show ? t('auth.login.hidePassword') : t('auth.login.showPassword')}
                  className="absolute right-1 top-1/2 grid h-9 w-9 -translate-y-1/2 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:text-text-primary"
                >
                  {show ? <EyeOff size={16} aria-hidden /> : <Eye size={16} aria-hidden />}
                </button>
              </span>
            </label>

            <label className="mt-4 block">
              <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('auth.setPassword.confirm')}</span>
              <input
                type={show ? 'text' : 'password'}
                value={confirm}
                onChange={(e) => setConfirm(e.target.value)}
                autoComplete="new-password"
                className={INPUT}
              />
            </label>

            {/* the rules, ticked off as they type (the server's policy decides) */}
            <ul className="mt-4 flex flex-col gap-1.5 text-[12.5px]" aria-live="polite">
              {rules.map((r) => (
                <li
                  key={r.text}
                  className={`flex items-center gap-2 ${
                    r.ok === true ? 'text-[color:var(--green-ink)]' : 'text-text-muted'
                  }`}
                >
                  {r.ok === true ? (
                    <Check size={14} strokeWidth={2.5} className="shrink-0" aria-hidden />
                  ) : (
                    <Circle size={8} className="mx-[3px] shrink-0" aria-hidden />
                  )}
                  {r.text}
                </li>
              ))}
            </ul>

            {error && (
              <div
                role="alert"
                className="mt-4 flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
              >
                <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
              </div>
            )}

            <button
              type="submit"
              disabled={busy || !password || !confirm}
              className="mt-6 flex h-11 w-full cursor-pointer items-center justify-center gap-2 rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy && <Loader2 size={15} className="animate-spin" aria-hidden />}
              {t('auth.setPassword.submit')}
            </button>

            <p className="mt-4 text-center text-[12px] text-text-muted">
              {t('auth.setPassword.validUntil', { date: f.dateTime(info.expires_at) })}
            </p>
          </form>
        )}

        {error && state.kind === 'checking' && (
          <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
            {error}
          </div>
        )}

        <LanguageSwitcher className="mt-6" />
      </div>
    </div>
  )
}
