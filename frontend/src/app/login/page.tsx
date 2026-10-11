'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { ArrowLeft, Eye, EyeOff, Loader2 } from 'lucide-react'
import {
  isTwoFactorChallenge,
  login,
  loginSecondFactor,
  LoginError,
  type AuthUser,
  type TwoFactorChallenge,
} from '@/lib/auth'
import { minutesFrom } from '@/lib/security'
import { Logo } from '@/components/shell/Logo'
import { LanguageSwitcher } from '@/components/i18n/LanguageSwitcher'
import { useT } from '@/i18n'

const INPUT =
  'h-11 w-full rounded-lg border border-line-strong bg-card px-3 text-[14px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

/** Sign-in, Paper & Ink: the logo, a display-face heading and one paper card
 *  with an ink primary button. Two steps when the person has two-step sign-in
 *  on: the password, then a code from their authenticator app (or a recovery
 *  code). A session that must change a seeded password or set up two-step
 *  sign-in lands on /account-setup instead of the app. */
export default function LoginPage() {
  const t = useT()
  const router = useRouter()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // set by expireSessionOn401 when a stale token got a 401 (tokens rotate per login)
  const [expired, setExpired] = useState(false)
  // step 2: the password was right, two-step sign-in is on
  const [challenge, setChallenge] = useState<TwoFactorChallenge | null>(null)
  const [code, setCode] = useState('')
  const [useRecovery, setUseRecovery] = useState(false)

  useEffect(() => {
    if (new URLSearchParams(window.location.search).get('expired')) setExpired(true)
  }, [])

  /** A refusal in the reader's language — the server's `code` says why. */
  const explain = (err: unknown): string => {
    if (err instanceof TypeError) return t('common.errors.network')
    if (err instanceof LoginError) {
      switch (err.code) {
        case 'invalid_credentials':
          return t('auth.login.invalid')
        case 'too_many_attempts':
          return t('auth.login.locked', { count: minutesFrom(err.retryAfter) })
        case 'rate_limited':
          return t('auth.login.rateLimited')
        case 'invalid_code':
          return t('auth.twoFactor.invalid')
        case 'challenge_expired':
          return t('auth.twoFactor.expired')
      }
    }
    return err instanceof Error ? err.message : String(err)
  }

  const enter = (user: AuthUser) => router.replace(user.restriction ? '/account-setup' : '/dashboard')

  const submit = async (e?: React.FormEvent) => {
    e?.preventDefault()
    if (!username.trim() || !password) return
    setBusy(true)
    setError(null)
    try {
      const r = await login(username.trim(), password)
      if (isTwoFactorChallenge(r)) {
        setChallenge(r)
        setCode('')
        setUseRecovery(false)
      } else enter(r)
    } catch (err) {
      setError(explain(err))
    } finally {
      setBusy(false)
    }
  }

  const submitCode = async (e?: React.FormEvent) => {
    e?.preventDefault()
    if (!challenge || !code.trim()) return
    setBusy(true)
    setError(null)
    try {
      enter(await loginSecondFactor(challenge.challenge, code.trim()))
    } catch (err) {
      if (err instanceof LoginError && err.code === 'challenge_expired') {
        setChallenge(null)
        setPassword('')
      }
      setError(explain(err))
    } finally {
      setBusy(false)
    }
  }

  const backToPassword = () => {
    setChallenge(null)
    setPassword('')
    setCode('')
    setError(null)
  }

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center overflow-y-auto bg-app px-4 py-10">
      <div className="w-full max-w-[400px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>

        {challenge ? (
          <form
            onSubmit={submitCode}
            className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8 sm:py-8"
            aria-labelledby="login-2fa-title"
          >
            <div className="eyebrow">{t('auth.twoFactor.eyebrow')}</div>
            <h1 id="login-2fa-title" className="display mt-1 text-[30px] leading-[1.05] text-text-heading">
              {t('auth.twoFactor.title')}
            </h1>
            <p className="mt-1.5 text-[13.5px] text-text-muted">
              {t('auth.twoFactor.subtitle', { issuer: challenge.issuer })}
            </p>
            <div className="mt-5 border-t border-text-primary/80" />

            <label className="mt-6 block">
              <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">
                {useRecovery ? t('auth.twoFactor.recovery') : t('auth.twoFactor.code')}
              </span>
              <input
                key={useRecovery ? 'recovery' : 'app'}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                autoComplete="one-time-code"
                inputMode={useRecovery ? 'text' : 'numeric'}
                placeholder={useRecovery ? 'xxxx-xxxx-xxxx' : '123456'}
                maxLength={useRecovery ? 20 : 8}
                autoFocus
                className={`${INPUT} font-mono tracking-[0.2em]`}
              />
            </label>

            {error && (
              <div role="alert" className="mt-4 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
                {error}
              </div>
            )}

            <button
              type="submit"
              disabled={busy || !code.trim()}
              className="mt-6 flex h-11 w-full cursor-pointer items-center justify-center gap-2 rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy && <Loader2 size={15} className="animate-spin" aria-hidden />}
              {t('auth.twoFactor.verify')}
            </button>

            <div className="mt-4 flex flex-wrap items-center justify-between gap-2 text-[12.5px]">
              <button
                type="button"
                onClick={backToPassword}
                className="inline-flex cursor-pointer items-center gap-1 text-text-muted hover:text-text-primary"
              >
                <ArrowLeft size={13} aria-hidden /> {t('auth.twoFactor.back')}
              </button>
              <button
                type="button"
                onClick={() => {
                  setUseRecovery((v) => !v)
                  setCode('')
                  setError(null)
                }}
                className="cursor-pointer font-semibold text-text-high underline-offset-2 hover:underline"
              >
                {useRecovery ? t('auth.twoFactor.useApp') : t('auth.twoFactor.useRecovery')}
              </button>
            </div>
          </form>
        ) : (
          <form
            onSubmit={submit}
            className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8 sm:py-8"
            aria-labelledby="login-title"
          >
            <div className="eyebrow">{t('auth.login.eyebrow')}</div>
            <h1 id="login-title" className="display mt-1 text-[34px] leading-[1.05] text-text-heading">
              {t('auth.login.title')}
            </h1>
            <p className="mt-1.5 text-[13.5px] text-text-muted">{t('auth.login.subtitle')}</p>
            <div className="mt-5 border-t border-text-primary/80" />

            <label className="mt-6 block">
              <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('auth.login.username')}</span>
              <input
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
                autoFocus
                className={INPUT}
              />
            </label>

            <label className="mt-4 block">
              <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('auth.login.password')}</span>
              <span className="relative block">
                <input
                  type={showPassword ? 'text' : 'password'}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  className={`${INPUT} pr-11`}
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  aria-label={showPassword ? t('auth.login.hidePassword') : t('auth.login.showPassword')}
                  className="absolute right-1 top-1/2 grid h-9 w-9 -translate-y-1/2 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:text-text-primary"
                >
                  {showPassword ? <EyeOff size={16} aria-hidden /> : <Eye size={16} aria-hidden />}
                </button>
              </span>
            </label>

            {expired && !error && (
              <div role="status" className="mt-4 rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12.5px] text-[color:var(--amber-ink)]">
                {t('auth.login.expired')}
              </div>
            )}
            {error && (
              <div role="alert" className="mt-4 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
                {error}
              </div>
            )}

            <button
              type="submit"
              disabled={busy || !username.trim() || !password}
              className="mt-6 flex h-11 w-full cursor-pointer items-center justify-center gap-2 rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy && <Loader2 size={15} className="animate-spin" aria-hidden />}
              {t('auth.login.signIn')}
            </button>

            <p className="mt-5 text-center text-[12px] leading-relaxed text-text-muted">{t('auth.login.provisioned')}</p>
          </form>
        )}

        <p className="mt-6 text-center text-[12px] text-text-faint">{t('auth.login.tagline')}</p>
        <LanguageSwitcher className="mt-3" />
      </div>
    </div>
  )
}
