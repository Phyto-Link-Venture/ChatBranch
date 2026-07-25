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
    <div className="flex min-h-dvh bg-app">
      {/* Left — form */}
      <div className="flex flex-1 flex-col px-6 py-8 sm:px-12 lg:px-16">
        {/* Brand mark, top-left */}
        <div className="flex items-center gap-2.5">
          <div
            className="grid h-9 w-9 place-items-center rounded-[11px]"
            style={{
              background: 'linear-gradient(135deg,#10B981,#0D9668)',
              boxShadow: '0 6px 18px rgba(16,185,129,0.35)',
            }}
          >
            <span className="text-[13px] font-extrabold tracking-[-0.5px] text-white">AI</span>
          </div>
          <span className="text-[16px] font-extrabold tracking-[-0.5px] text-text-heading">ChatWood</span>
        </div>

        {/* Centered form */}
        <div className="flex flex-1 items-center">
          <div className="w-full max-w-[360px]">
            <h1 className="text-[28px] font-extrabold tracking-[-0.8px] text-text-heading">Welcome back</h1>
            <p className="mt-1 text-[13px] text-text-muted">Please enter your details</p>

            <div className="mt-7">
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
                style={{ background: 'linear-gradient(135deg,#10B981,#0D9668)' }}
              >
                {busy && <Loader2 size={14} className="animate-spin" aria-hidden />}
                Sign in
              </button>

              <p className="mt-5 text-[11.5px] leading-relaxed text-text-faint">
                Accounts are provisioned by your administrator. Contact them if you can&apos;t sign in.
              </p>
            </div>
          </div>
        </div>
      </div>

      {/* Right — brand panel (hidden on small screens) */}
      <div
        className="relative hidden w-[46%] shrink-0 overflow-hidden lg:flex lg:flex-col lg:justify-end"
        style={{ background: 'linear-gradient(150deg,#0D9668 0%,#10B981 52%,#34D399 100%)' }}
      >
        {/* scattered comms glyphs */}
        {[
          { Icon: MessageSquare, top: '14%', left: '18%', size: 30, rot: -12 },
          { Icon: Phone, top: '22%', left: '62%', size: 26, rot: 8 },
          { Icon: Mic, top: '40%', left: '30%', size: 34, rot: -6 },
          { Icon: Mail, top: '52%', left: '70%', size: 28, rot: 14 },
          { Icon: Bot, top: '30%', left: '82%', size: 24, rot: -10 },
          { Icon: Sparkles, top: '62%', left: '20%', size: 26, rot: 10 },
        ].map((g, i) => (
          <div
            key={i}
            className="absolute"
            style={{ top: g.top, left: g.left, transform: `rotate(${g.rot}deg)`, color: 'rgba(255,255,255,0.16)' }}
            aria-hidden
          >
            <g.Icon size={g.size} strokeWidth={1.5} />
          </div>
        ))}

        {/* central emblem */}
        <div
          className="absolute left-1/2 top-1/2 grid h-[92px] w-[92px] -translate-x-1/2 -translate-y-[130%] place-items-center rounded-[26px]"
          style={{ background: 'rgba(255,255,255,0.14)', backdropFilter: 'blur(2px)' }}
          aria-hidden
        >
          <span className="text-[30px] font-extrabold tracking-[-1px] text-white">AI</span>
        </div>

        <div className="relative z-10 p-12 text-white">
          <div className="text-[24px] font-extrabold leading-tight tracking-[-0.6px]">
            Talk smarter with
            <br />
            AI voice &amp; messaging
          </div>
          <p className="mt-2.5 max-w-[360px] text-[13px] leading-relaxed text-white/85">
            One console for AI calls, WhatsApp and email — engage, follow up and resolve across
            your whole contact book.
          </p>
        </div>
      </div>
    </div>
  )
}
