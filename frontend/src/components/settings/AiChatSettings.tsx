'use client'

import { useEffect, useState } from 'react'
import { Info, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface ChatConfig {
  quiet_start?: string
  quiet_end?: string
  ai_available: boolean
}

/**
 * Chat-AI delivery knobs (quiet hours + send pacing). The chat AI provider pin
 * and model live in Settings → AI & Providers (next to the API keys they
 * depend on) — this tab used to duplicate them, writing the same backing store,
 * which silently overwrote the other page. Same backing store + gate as App
 * Keys: GET/POST /api/whatsapp/settings (reads any role, writes superadmin);
 * the POST body only carries quiet-hours keys, so provider/model + API keys are
 * untouched.
 *
 * Send pacing (queue min/max gap) rides along on this page but is backed by
 * GET/PUT /api/whatsapp/routing-rules — the queued AI replies these gaps pace
 * are configured here, so the knob lives next to them.
 */
export function AiChatSettings() {
  const [loaded, setLoaded] = useState<ChatConfig | null>(null)
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
      <div className="flex items-start gap-2 border-b border-line py-3 text-[11px] leading-relaxed text-text-muted">
        <Info size={13} className="mt-0.5 shrink-0" color="#3B82F6" aria-hidden />
        <span>
          Looking for the chat AI provider or model? Those now live in{' '}
          <span className="font-semibold text-text-mid">AI &amp; Providers</span>, next to the API keys they depend on.
        </span>
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
