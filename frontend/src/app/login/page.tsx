'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import {
  Bot,
  Eye,
  EyeOff,
  Loader2,
  Lock,
  Mail,
  MessageSquare,
  Mic,
  Phone,
  Sparkles,
  User,
} from 'lucide-react'
import { login } from '@/lib/auth'

// Faint comms glyphs scattered behind the card — brand texture over the glass
// backdrop (the fixed blue gradient painted globally by body::before).
const GLYPHS = [
  { Icon: MessageSquare, top: '13%', left: '9%', size: 34, rot: -12 },
  { Icon: Phone, top: '22%', left: '84%', size: 30, rot: 10 },
  { Icon: Mic, top: '66%', left: '13%', size: 40, rot: -8 },
  { Icon: Mail, top: '74%', left: '82%', size: 32, rot: 13 },
  { Icon: Bot, top: '42%', left: '91%', size: 28, rot: -10 },
  { Icon: Sparkles, top: '30%', left: '5%', size: 30, rot: 10 },
]

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

  const submit = async () => {
    if (!username.trim() || !password) return
    setBusy(true)
    setError(null)
    try {
      await login(username.trim(), password)
      router.replace('/dashboard')
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="relative flex min-h-dvh items-center justify-center overflow-hidden bg-app px-4 py-10">
      {/* Background comms glyphs (behind the card) */}
      {GLYPHS.map((g, i) => (
        <div
          key={i}
          className="pointer-events-none absolute hidden sm:block"
          style={{ top: g.top, left: g.left, transform: `rotate(${g.rot}deg)`, color: 'rgba(147,197,253,0.10)' }}
          aria-hidden
        >
          <g.Icon size={g.size} strokeWidth={1.4} />
        </div>
      ))}

      {/* Glass card */}
      <div className="relative w-full max-w-[420px] rounded-[26px] border border-line-strong bg-card px-8 py-9 shadow-[0_24px_70px_rgba(0,0,0,0.4)]">
        {/* Brand mark + heading, centered */}
        <div className="flex flex-col items-center text-center">
          <div
            className="grid h-14 w-14 place-items-center rounded-[18px]"
            style={{
              background: 'linear-gradient(135deg,#3B82F6,#2563EB)',
              boxShadow: '0 10px 26px rgba(59,130,246,0.45)',
            }}
          >
            <span className="text-[20px] font-extrabold tracking-[-0.5px] text-white">CB</span>
          </div>
          <h1 className="mt-5 text-[24px] font-extrabold tracking-[-0.7px] text-text-heading">
            Welcome back
          </h1>
          <p className="mt-1 text-[13px] text-text-muted">Sign in to ChatBranch</p>
        </div>

        {/* Form */}
        <div className="mt-8">
          <label className="block">
            <span className="mb-1.5 block text-[12px] font-bold text-text-high">Username</span>
            <div className="relative">
              <User size={14} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
              <input
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && submit()}
                autoComplete="username"
                autoFocus
                className="w-full rounded-xl border border-line-strong bg-well py-2.5 pl-9 pr-3 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400"
              />
            </div>
          </label>

          <label className="mt-4 block">
            <span className="mb-1.5 block text-[12px] font-bold text-text-high">Password</span>
            <div className="relative">
              <Lock size={14} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
              <input
                type={showPassword ? 'text' : 'password'}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && submit()}
                autoComplete="current-password"
                className="w-full rounded-xl border border-line-strong bg-well py-2.5 pl-9 pr-10 text-[13px] text-text-primary outline-none transition-colors focus:border-brand-400"
              />
              <button
                type="button"
                onClick={() => setShowPassword((v) => !v)}
                aria-label={showPassword ? 'Hide password' : 'Show password'}
                className="absolute right-1.5 top-1/2 -translate-y-1/2 cursor-pointer rounded-md p-1.5 text-text-faint transition-colors hover:text-text-primary"
              >
                {showPassword ? <EyeOff size={15} aria-hidden /> : <Eye size={15} aria-hidden />}
              </button>
            </div>
          </label>

          {notice && !error && (
            <div className="mt-4 rounded-xl border border-amber/25 bg-amber/10 px-3 py-2 text-[12px] text-amber-300">
              {notice}
            </div>
          )}
          {error && (
            <div className="mt-4 rounded-xl border border-red/25 bg-red/10 px-3 py-2 text-[12px] text-red-300">
              {error}
            </div>
          )}

          <button
            onClick={submit}
            disabled={busy || !username.trim() || !password}
            className="mt-6 flex w-full cursor-pointer items-center justify-center gap-2 rounded-xl py-2.5 text-[13px] font-bold text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
            style={{ background: 'linear-gradient(135deg,#3B82F6,#2563EB)' }}
          >
            {busy && <Loader2 size={14} className="animate-spin" aria-hidden />}
            Sign in
          </button>

          <p className="mt-5 text-center text-[11.5px] leading-relaxed text-text-faint">
            Accounts are provisioned by your administrator.
            <br />
            Contact them if you can&apos;t sign in.
          </p>
        </div>
      </div>

      {/* Tagline under the card */}
      <p className="pointer-events-none absolute bottom-6 left-0 right-0 text-center text-[12px] text-text-faint">
        Talk smarter with AI voice &amp; messaging — one console for calls, WhatsApp and email.
      </p>
    </div>
  )
}
