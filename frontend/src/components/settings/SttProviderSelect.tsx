'use client'

import { useEffect, useState } from 'react'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

const STT_PROVIDER_LABELS: Record<string, string> = {
  elevenlabs: 'ElevenLabs Scribe v2 Realtime',
  openai: 'OpenAI gpt-4o-transcribe',
  ilmu: 'YTL ILMU ASR (Malaysian-trained)',
}

/**
 * Live-wired to calls/voice_settings.py — unlike the other rows on the AI tab,
 * this one actually persists and takes effect on the next call (no worker
 * restart), since the dial view reads it fresh at dispatch time.
 */
export function SttProviderSelect() {
  const [provider, setProvider] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetch(`${API}/voice/stt-settings`)
      .then((r) => r.json())
      .then((d) => setProvider(d.stt_provider))
      .catch(() => {})
  }, [])

  async function onChange(next: string) {
    const prev = provider
    setProvider(next)
    setBusy(true)
    try {
      const res = await fetch(`${API}/voice/stt-settings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ stt_provider: next }),
      })
      if (!res.ok) throw new Error()
    } catch {
      setProvider(prev)
    } finally {
      setBusy(false)
    }
  }

  return (
    <select
      value={provider ?? ''}
      disabled={provider === null || busy}
      onChange={(e) => onChange(e.target.value)}
      className="cursor-pointer rounded-[7px] border border-line-strong bg-surface px-2.5 py-1.5 text-[12px] font-semibold text-text-high outline-none disabled:opacity-50"
    >
      {provider === null && <option value="">Loading…</option>}
      {Object.entries(STT_PROVIDER_LABELS).map(([id, label]) => (
        <option key={id} value={id}>
          {label}
        </option>
      ))}
    </select>
  )
}
