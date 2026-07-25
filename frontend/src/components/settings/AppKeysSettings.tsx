'use client'

import { useEffect, useState } from 'react'
import { KeyRound, Loader2, ShieldAlert } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface GlobalSettings {
  verify_token: string
  openai_api_key_masked: string
  openrouter_api_key_masked: string
  google_api_key_masked: string
  ilmu_api_key_masked: string
  ai_available: boolean
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  hint,
  secret = false,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  hint?: string
  secret?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-[11px] font-bold text-text-high">{label}</span>
      <input
        type={secret ? 'password' : 'text'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        autoComplete="off"
        className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
      />
      {hint && <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

/**
 * App-level keys — SUPER ADMIN only (Whatomate's model: org-level AI settings
 * live above the per-admin WhatsApp accounts). Saved values override the
 * backend/.env defaults at runtime; clearing a field falls back to env.
 */
export function AppKeysSettings() {
  const { role, ready } = useRole()
  const [loaded, setLoaded] = useState<GlobalSettings | null>(null)
  const [openaiKey, setOpenaiKey] = useState('')
  const [openrouterKey, setOpenrouterKey] = useState('')
  const [googleKey, setGoogleKey] = useState('')
  const [ilmuKey, setIlmuKey] = useState('')
  const [verifyToken, setVerifyToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const seed = (s: GlobalSettings) => {
    setLoaded(s)
    setOpenaiKey(s.openai_api_key_masked)
    setOpenrouterKey(s.openrouter_api_key_masked)
    setGoogleKey(s.google_api_key_masked)
    setIlmuKey(s.ilmu_api_key_masked)
    setVerifyToken(s.verify_token)
  }

  useEffect(() => {
    api<GlobalSettings>('settings').then(seed).catch(() => setLoaded(null))
  }, [])

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (role !== 'superadmin') {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <ShieldAlert size={15} className="text-amber-300" aria-hidden />
        App keys are managed by the super admin. Switch role in the sidebar footer to view.
      </div>
    )
  }

  if (loaded === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        App settings unavailable — is the backend API running on port 8000?
      </div>
    )
  }

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      // masked placeholders ("••••abcd") are ignored server-side; the body
      // only carries key fields — chat provider/model/quiet hours live in
      // Settings → AI Configuration now and are untouched here
      const next = await api<GlobalSettings>('settings', {
        method: 'POST',
        body: JSON.stringify({
          openai_api_key: openaiKey,
          openrouter_api_key: openrouterKey,
          google_api_key: googleKey,
          ilmu_api_key: ilmuKey,
          verify_token: verifyToken,
        }),
      })
      seed(next)
      setMsg({
        ok: true,
        text: next.ai_available
          ? 'Saved — AI replies are ready. Live immediately, no restart needed.'
          : 'Saved. AI replies still off — set an OpenAI, OpenRouter, ILMU or Google key.',
      })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-center gap-2">
        <span className="flex items-center gap-1.5 rounded-full bg-purple/15 px-2.5 py-1 text-[11px] font-bold text-purple-300">
          <KeyRound size={12} aria-hidden /> Super admin
        </span>
        <span
          className={`rounded-full px-2.5 py-1 text-[11px] font-bold ${
            loaded.ai_available ? 'bg-green/15 text-green-300' : 'bg-fill text-text-muted'
          }`}
        >
          AI replies {loaded.ai_available ? 'ready' : 'off — no provider key'}
        </span>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <Field
          label="OpenAI API key"
          value={openaiKey}
          onChange={setOpenaiKey}
          placeholder="sk-…"
          hint="Powers WhatsApp AI auto-replies. Clearing falls back to OPENAI_API_KEY in backend/.env."
          secret
        />
        <Field
          label="OpenRouter API key (optional)"
          value={openrouterKey}
          onChange={setOpenrouterKey}
          placeholder="sk-or-…"
          hint="When set, auto-replies go through OpenRouter instead of OpenAI."
          secret
        />
        <Field
          label="Google Gemini API key (optional)"
          value={googleKey}
          onChange={setGoogleKey}
          placeholder="AIza… / AQ.…"
          hint="Used when no OpenAI/OpenRouter key is set (Gemini OpenAI-compatible API). Falls back to GOOGLE_API_KEY in backend/.env."
          secret
        />
        <Field
          label="YTL ILMU API key (optional)"
          value={ilmuKey}
          onChange={setIlmuKey}
          placeholder="sk-…"
          hint="Malaysian sovereign AI (api.ilmu.ai) — data stays in-country. Falls back to ILMU_API_KEY in backend/.env."
          secret
        />
        <Field
          label="Webhook verify token"
          value={verifyToken}
          onChange={setVerifyToken}
          placeholder="chatbranch-verify"
          hint="Shared by all WhatsApp accounts — must match the Meta webhook form."
        />
      </div>

      <div className="flex items-center gap-2">
        <Button variant="primary" onClick={save} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save app keys
        </Button>
        {msg && (
          <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
            {msg.text}
          </span>
        )}
      </div>

      <p className="text-[11px] leading-relaxed text-text-muted">
        Note: the <strong>voice agent</strong> worker reads its keys (LiveKit, realtime voice) from{' '}
        <code className="rounded bg-fill px-1">backend/.env</code> at startup — those still require an env
        edit + agent restart. Keys here apply to WhatsApp text auto-replies instantly. The chat AI
        provider, model and quiet hours moved to Settings → AI Configuration.
      </p>
    </div>
  )
}
