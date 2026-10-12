'use client'

import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useRouter } from 'next/navigation'
import { AlertCircle, Loader2, LogOut } from 'lucide-react'
import { getAuth, logout, refreshIdentity, type AuthUser } from '@/lib/auth'
import { minutesFrom, passwordProblem, SecurityError, securityApi } from '@/lib/security'
import { Logo } from '@/components/shell/Logo'
import { LanguageSwitcher } from '@/components/i18n/LanguageSwitcher'
import { TwoFactorCard } from '@/components/settings/TwoFactorCard'
import { useT } from '@/i18n'

const INPUT =
  'h-11 w-full rounded-lg border border-line-strong bg-card px-3 text-[14px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

/**
 * The account-setup screen a RESTRICTED session sees instead of the app
 * (Security/SessionGate on the server answers nothing else for it): change a
 * seeded initial password on a real install, then — when the organization
 * requires it — set up two-step sign-in. Rendered bare by the Shell, like /login.
 */
export default function AccountSetupPage() {
  const t = useT()
  const router = useRouter()
  const [user, setUser] = useState<AuthUser | null>(null)
  const [ready, setReady] = useState(false)

  const sync = useCallback(async () => {
    const next = await refreshIdentity()
    setUser(next)
    if (next && !next.restriction) router.replace('/dashboard')
  }, [router])

  useEffect(() => {
    const u = getAuth()
    if (!u) {
      router.replace('/login')
      return
    }
    setUser(u)
    setReady(true)
    void sync()
  }, [router, sync])

  const signOut = async () => {
    await logout()
    router.replace('/login')
  }

  if (!ready || !user) return null

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center overflow-y-auto bg-app px-4 py-10">
      <div className="w-full max-w-[560px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>
        <div className="eyebrow">{t('auth.setup.eyebrow')}</div>
        {user.restriction === 'change_password' ? (
          <>
            <h1 className="display mt-1 text-[30px] leading-[1.05] text-text-heading">{t('auth.setup.passwordTitle')}</h1>
            <p className="mt-1.5 text-[13.5px] text-text-muted">{t('auth.setup.passwordIntro')}</p>
            <ForcedPasswordChange username={user.username} onDone={() => void sync()} />
          </>
        ) : user.restriction === 'enroll_2fa' ? (
          <>
            <h1 className="display mt-1 text-[30px] leading-[1.05] text-text-heading">{t('auth.setup.twoFactorTitle')}</h1>
            <p className="mb-4 mt-1.5 text-[13.5px] text-text-muted">{t('auth.setup.twoFactorIntro')}</p>
            <TwoFactorCard forced onDone={() => void sync()} />
          </>
        ) : (
          <div className="mt-6 flex items-center gap-2 text-[13px] text-text-muted">
            <Loader2 size={15} className="animate-spin" aria-hidden /> {t('auth.setup.continue')}
          </div>
        )}
        <div className="mt-6 flex items-center justify-between">
          <button
            type="button"
            onClick={() => void signOut()}
            className="inline-flex cursor-pointer items-center gap-1.5 text-[12.5px] text-text-muted hover:text-text-primary"
          >
            <LogOut size={13} aria-hidden /> {t('auth.setup.signOut')}
          </button>
          <LanguageSwitcher />
        </div>
      </div>
    </div>
  )
}

function ForcedPasswordChange({ username, onDone }: { username: string; onDone: () => void }) {
  const t = useT()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [min, setMin] = useState(8)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    securityApi<{ password_min_length?: number }>('auth/me/account')
      .then((a) => setMin(a.password_min_length ?? 8))
      .catch(() => undefined)
  }, [])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!current) return setError(t('account.password.required'))
    if (next.length < min) return setError(t('account.password.tooShort', { min }))
    if (next === current) return setError(t('account.password.same'))
    if (next !== confirm) return setError(t('account.password.mismatch'))
    setBusy(true)
    try {
      await securityApi('auth/me/password', {
        method: 'POST',
        body: JSON.stringify({ current_password: current, new_password: next }),
      })
      onDone()
    } catch (err) {
      const e2 = err as SecurityError
      setError(
        e2.code === 'too_many_attempts'
          ? t('auth.login.locked', { count: minutesFrom(e2.retryAfter) })
          : e2.field === 'current_password'
            ? t('account.password.wrong')
            : passwordProblem(t, e2.code, e2.min) ?? e2.message,
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={(e) => void submit(e)} className="mt-5 rounded-xl border border-line bg-card px-6 py-6">
      {/* a hidden username lets password managers file the new password correctly */}
      <input type="text" name="username" value={username} autoComplete="username" readOnly hidden />
      <label className="block">
        <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('account.password.current')}</span>
        <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" autoFocus className={INPUT} />
      </label>
      <label className="mt-4 block">
        <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('account.password.new')}</span>
        <input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" className={INPUT} />
      </label>
      <label className="mt-4 block">
        <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">{t('account.password.confirm')}</span>
        <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" className={INPUT} />
      </label>
      <p className="mt-2 text-[11.5px] text-text-muted">{t('account.password.policyHint', { min })}</p>
      {error && (
        <div role="alert" className="mt-4 flex items-start gap-2 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      <button
        type="submit"
        disabled={busy || !current || !next || !confirm}
        className="mt-5 flex h-11 w-full cursor-pointer items-center justify-center gap-2 rounded-lg bg-primary text-[14px] font-semibold text-on-primary transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
      >
        {busy && <Loader2 size={15} className="animate-spin" aria-hidden />}
        {t('account.password.submit')}
      </button>
    </form>
  )
}
