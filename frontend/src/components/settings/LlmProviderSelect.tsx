'use client'

import { useEffect, useState } from 'react'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

const LLM_PROVIDER_LABELS: Record<string, string> = {
  openai: 'OpenAI (gpt-4o-mini)',
  ilmu: 'YTL ILMU (ilmu-v3.1, Malaysia)',
  openrouter: 'OpenRouter',
  gemini: 'Google Gemini (2.5 Flash)',
}

/**
 * Conversation brain for ElevenLabs-voice pipeline calls — live-wired to
 * calls/voice_settings.py like SttProviderSelect: persists and takes effect
 * on the next call (no worker restart), since both dial paths read it fresh
 * at dispatch time. Native realtime ("Default voice") calls ignore this —
 * their brain IS the realtime model.
 */
export function LlmProviderSelect() {
  const [provider, setProvider] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetch(`${API}/voice/llm-settings`)
      .then((r) => r.json())
      .then((d) => setProvider(d.llm_provider))
      .catch(() => {})
  }, [])

  async function onChange(next: string) {
    const prev = provider
    setProvider(next)
    setBusy(true)
    try {
      const res = await fetch(`${API}/voice/llm-settings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ llm_provider: next }),
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
      {Object.entries(LLM_PROVIDER_LABELS).map(([id, label]) => (
        <option key={id} value={id}>
          {label}
        </option>
      ))}
    </select>
  )
}
