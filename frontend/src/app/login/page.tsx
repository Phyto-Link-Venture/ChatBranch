'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Eye, EyeOff, Loader2 } from 'lucide-react'
import { login } from '@/lib/auth'
import { Logo } from '@/components/shell/Logo'

const INPUT =
  'h-11 w-full rounded-lg border border-line-strong bg-card px-3 text-[14px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

/** Sign-in, Paper & Ink: the logo, a display-face heading and one paper card
 *  with an ink primary button. */
export default function LoginPage() {
  const router = useRouter()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    // set by expireSessionOn401 when a stale token got a 401 (tokens rotate per login)
    if (new URLSearchParams(window.location.search).get('expired')) {
      setNotice('Your session expired — please sign in again.')
    }
  }, [])

  const submit = async (e?: React.FormEvent) => {
    e?.preventDefault()
    if (!username.trim() || !password) return
    setBusy(true)
    setError(null)
    try {
      await login(username.trim(), password)
      router.replace('/analytics')
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center overflow-y-auto bg-app px-4 py-10">
      <div className="w-full max-w-[400px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>

        <form
          onSubmit={submit}
          className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8 sm:py-8"
          aria-labelledby="login-title"
        >
          <div className="eyebrow">Workspace</div>
          <h1 id="login-title" className="display mt-1 text-[34px] leading-[1.05] text-text-heading">
            Welcome back
          </h1>
          <p className="mt-1.5 text-[13.5px] text-text-muted">Sign in to pick up your conversations.</p>
          <div className="mt-5 border-t border-text-primary/80" />

          <label className="mt-6 block">
            <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">Username</span>
            <input
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              autoFocus
              className={INPUT}
            />
          </label>

          <label className="mt-4 block">
            <span className="mb-1.5 block text-[12.5px] font-semibold text-text-high">Password</span>
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
                aria-label={showPassword ? 'Hide password' : 'Show password'}
                className="absolute right-1 top-1/2 grid h-9 w-9 -translate-y-1/2 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:text-text-primary"
              >
                {showPassword ? <EyeOff size={16} aria-hidden /> : <Eye size={16} aria-hidden />}
              </button>
            </span>
          </label>

          {notice && !error && (
            <div role="status" className="mt-4 rounded-lg border border-amber/30 bg-amber/10 px-3 py-2 text-[12.5px] text-[color:var(--amber-ink)]">
              {notice}
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
            Sign in
          </button>

          <p className="mt-5 text-center text-[12px] leading-relaxed text-text-muted">
            Accounts are provisioned by your administrator — ask them if you can&apos;t sign in.
          </p>
        </form>

        <p className="mt-6 text-center text-[12px] text-text-faint">
          One inbox for WhatsApp, email, social, web chat and voice.
        </p>
      </div>
    </div>
  )
}
