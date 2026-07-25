'use client'

import { useEffect, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

const CHAT_PROVIDER_OPTIONS = [
  { value: '', label: 'Auto (OpenRouter → OpenAI → ILMU → Gemini)' },
  { value: 'ilmu', label: 'YTL ILMU (Malaysia)' },
  { value: 'openrouter', label: 'OpenRouter' },
  { value: 'openai', label: 'OpenAI' },
  { value: 'google', label: 'Google Gemini' },
]

interface ChatConfig {
  chat_provider_pref?: string
  chat_model: string
  quiet_start?: string
  quiet_end?: string
  ai_available: boolean
}

/**
 * Chat-AI knobs (provider pin, model, quiet hours) — moved out of App Keys so
 * AI Configuration owns all model behavior. Same backing store + gate as App
 * Keys: GET/POST /api/whatsapp/settings (reads any role, writes superadmin);
 * the POST body only carries these keys, so API keys are untouched.
 *
 * Send pacing (queue min/max gap) rides along on this page but is backed by
 * GET/PUT /api/whatsapp/routing-rules — the queued AI replies these gaps pace
 * are configured here, so the knob lives next to them.
 */
export function AiChatSettings() {
  const [loaded, setLoaded] = useState<ChatConfig | null>(null)
  const [providerPref, setProviderPref] = useState('')
  const [model, setModel] = useState('')
  const [quietStart, setQuietStart] = useState('')
  const [quietEnd, setQuietEnd] = useState('')
  const [pacingLoaded, setPacingLoaded] = useState(false)
  const [minGap, setMinGap] = useState('')
  const [maxGap, setMaxGap] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const { role } = useRole()
  const canEdit = role === 'superadmin'

  const seed = (s: ChatConfig) => {
    setLoaded(s)
    setProviderPref(s.chat_provider_pref || '')
    setModel(s.chat_model)
    setQuietStart(s.quiet_start || '')
    setQuietEnd(s.quiet_end || '')
  }

  const seedPacing = (r: { queue_min_gap_s?: number; queue_max_gap_s?: number }) => {
    if (typeof r.queue_min_gap_s !== 'number' || typeof r.queue_max_gap_s !== 'number') return
    setMinGap(String(r.queue_min_gap_s))
    setMaxGap(String(r.queue_max_gap_s))
    setPacingLoaded(true)
  }

  useEffect(() => {
    fetch(`${API}/whatsapp/settings`, { headers: roleHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then(seed)
      .catch(() => setLoaded(null))
    fetch(`${API}/whatsapp/routing-rules`, { headers: roleHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then(seedPacing)
      .catch(() => setPacingLoaded(false)) // hide the row rather than show stale zeros
  }, [])

  const save = async () => {
    if (pacingLoaded) {
      const lo = Number(minGap)
      const hi = Number(maxGap)
      if (!Number.isInteger(lo) || !Number.isInteger(hi) || lo < 0 || hi < 0) {
        setMsg({ ok: false, text: 'Send pacing gaps must be whole seconds (0 or more).' })
        return
      }
      if (lo > hi) {
        setMsg({ ok: false, text: 'Min gap cannot exceed max gap.' })
        return
      }
    }
    setBusy(true)
    setMsg(null)
    try {
      const res = await fetch(`${API}/whatsapp/settings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...roleHeaders() },
        body: JSON.stringify({
          chat_provider_pref: providerPref,
          chat_model: model,
          quiet_start: quietStart.trim(),
          quiet_end: quietEnd.trim(),
        }),
      })
      const data = await res.json().catch(() => ({}))
      if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
      if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
      seed(data as ChatConfig)
      if (pacingLoaded) {
        const pr = await fetch(`${API}/whatsapp/routing-rules`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', ...roleHeaders() },
          body: JSON.stringify({
            queue_min_gap_s: Number(minGap),
            queue_max_gap_s: Number(maxGap),
          }),
        })
        const pdata = await pr.json().catch(() => ({}))
        if (expireSessionOn401(pr)) throw new Error('Session expired — redirecting to login…')
        if (!pr.ok) throw new Error((pdata as { detail?: string }).detail || `HTTP ${pr.status}`)
        seedPacing(pdata as { queue_min_gap_s?: number; queue_max_gap_s?: number })
      }
      setMsg({ ok: true, text: 'Saved — applies to the next AI reply, no restart needed.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (loaded === null) {
    return (
      <div className="border-b border-line py-3.5 text-[12px] text-text-muted">
        Chat AI settings unavailable — is the backend API running on port 8000?
      </div>
    )
  }

  const inputCls =
    'rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none disabled:cursor-not-allowed disabled:opacity-50'

  return (
    <>
      <div className="flex items-center justify-between gap-3 border-b border-line py-3.5">
        <div>
          <div className="mb-0.5 text-[13px] font-semibold text-text-high">Chat AI Provider</div>
          <div className="text-[11px] text-text-muted">
            Pin one provider for text auto-replies — ignored if that provider has no key (keys live in App Keys)
          </div>
        </div>
        <select
          value={providerPref}
          onChange={(e) => setProviderPref(e.target.value)}
          disabled={!canEdit}
          className={`cursor-pointer ${inputCls}`}
        >
          {CHAT_PROVIDER_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </div>
      <div className="flex items-center justify-between gap-3 border-b border-line py-3.5">
        <div>
          <div className="mb-0.5 text-[13px] font-semibold text-text-high">AI Chat Model</div>
          <div className="text-[11px] text-text-muted">
            Model for auto-replies &amp; summaries; empty uses the provider&apos;s default — clear when switching providers
          </div>
        </div>
        <input
          value={model}
          onChange={(e) => setModel(e.target.value)}
          placeholder="gpt-4o-mini / ilmu-v3.1 / gemini-2.0-flash"
          disabled={!canEdit}
          className={`w-[240px] ${inputCls}`}
        />
      </div>
      <div className="flex items-center justify-between gap-3 border-b border-line py-3.5">
        <div>
          <div className="mb-0.5 text-[13px] font-semibold text-text-high">Quiet Hours</div>
          <div className="text-[11px] text-text-muted">
            Outbound messages are held between these times (HH:MM 24h, server time) — both empty disables
          </div>
        </div>
        <div className="flex items-center gap-1.5">
          <input
            value={quietStart}
            onChange={(e) => setQuietStart(e.target.value)}
            placeholder="21:00"
            disabled={!canEdit}
            className={`w-[70px] text-center ${inputCls}`}
          />
          <span className="text-[11px] text-text-muted">to</span>
          <input
            value={quietEnd}
            onChange={(e) => setQuietEnd(e.target.value)}
            placeholder="09:00"
            disabled={!canEdit}
            className={`w-[70px] text-center ${inputCls}`}
          />
        </div>
      </div>
      {pacingLoaded && (
        <div className="flex items-center justify-between gap-3 border-b border-line py-3.5">
          <div>
            <div className="mb-0.5 text-[13px] font-semibold text-text-high">Send Pacing</div>
            <div className="text-[11px] text-text-muted">
              Jittered gap between an account&apos;s queued sends, in seconds — paces personal
              (Baileys) numbers for ban safety; Cloud template blasts are not slowed by this
            </div>
          </div>
          <div className="flex items-center gap-1.5">
            <input
              value={minGap}
              onChange={(e) => setMinGap(e.target.value)}
              placeholder="10"
              disabled={!canEdit}
              className={`w-[70px] text-center ${inputCls}`}
            />
            <span className="text-[11px] text-text-muted">to</span>
            <input
              value={maxGap}
              onChange={(e) => setMaxGap(e.target.value)}
              placeholder="30"
              disabled={!canEdit}
              className={`w-[70px] text-center ${inputCls}`}
            />
          </div>
        </div>
      )}
      <div className="flex items-center gap-2 py-3.5">
        {canEdit ? (
          <Button variant="primary" onClick={save} disabled={busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save chat AI settings
          </Button>
        ) : (
          <span className="text-[11px] text-text-muted">Super admin role required to change these.</span>
        )}
        {msg && (
          <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
            {msg.text}
          </span>
        )}
      </div>
    </>
  )
}
