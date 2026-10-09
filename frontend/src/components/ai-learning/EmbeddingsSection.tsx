'use client'

/**
 * Settings → AI keys & models → "Embeddings (semantic search)". Which service
 * turns knowledge and lessons into vectors so the AI finds answers by MEANING
 * (a reworded question still matches), plus the live indexing status.
 * Automatic = the first configured of OpenAI → Gemini → local; Off = keyword
 * search only. The values ride on the page's shared Save / Discard (they are
 * `embedding_provider` / `embedding_model` on /api/settings/ai-providers);
 * the status comes from /api/ai/learning/status.
 */

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { AlertCircle, Loader2, Sparkles } from 'lucide-react'
import { Select } from '@/components/ui/Select'
import { embeddingProviderLabel, learningApi, type LearningStatus } from '@/lib/aiLearning'

export const EMBEDDING_KEYS = ['embedding_provider', 'embedding_model']

const PROVIDERS: { value: string; label: string; hint: string }[] = [
  { value: '', label: 'Automatic', hint: 'OpenAI → Gemini → local' },
  { value: 'openai', label: 'OpenAI', hint: 'uses the OpenAI key' },
  { value: 'google', label: 'Google Gemini', hint: 'uses the Google key' },
  { value: 'local', label: 'Local server', hint: 'OpenAI-compatible /embeddings at the local base URL' },
  { value: 'none', label: 'Off', hint: 'keyword search only' },
]

const DEFAULT_MODEL: Record<string, string> = {
  openai: 'text-embedding-3-small',
  google: 'text-embedding-004',
  local: 'nomic-embed-text',
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

export function EmbeddingsSection({
  provider,
  model,
  disabled,
  onChange,
}: {
  provider: string
  model: string
  disabled: boolean
  onChange: (key: string, value: string) => void
}) {
  const [status, setStatus] = useState<LearningStatus | null | undefined>(undefined)

  useEffect(() => {
    let alive = true
    void learningApi.status().then((s) => {
      if (alive) setStatus(s)
    })
    return () => {
      alive = false
    }
  }, [])

  const e = status?.embedding
  const placeholder =
    provider === 'none'
      ? 'not used'
      : DEFAULT_MODEL[provider] ??
        (e?.provider && DEFAULT_MODEL[e.provider] ? `${DEFAULT_MODEL[e.provider]} (provider default)` : 'Provider default')

  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <div className="flex items-center gap-2">
        <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill">
          <Sparkles size={14} className="text-brand-300" aria-hidden />
        </span>
        <span className="text-[12.5px] font-bold text-text-high">Embeddings (semantic search)</span>
      </div>
      <p className="mb-3 mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">
        Lets the AI find the right knowledge and lessons by meaning, so “can I get my money back?” still finds your refund
        policy. Without a provider it matches by keywords. Changing the provider or model re-indexes everything in the
        background.
      </p>

      <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2">
        <label className="block min-w-0">
          <span className="mb-1 block text-[11px] font-bold text-text-high">Provider</span>
          <Select
            value={provider}
            disabled={disabled}
            onChange={(v) => onChange('embedding_provider', v)}
            options={PROVIDERS.map((p) => ({ value: p.value, label: p.label, hint: p.hint }))}
            size="md"
            ariaLabel="Embedding provider"
            className="w-full"
          />
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
            OpenAI and Gemini use the keys above; Local uses the local base URL.
          </span>
        </label>
        <label className="block min-w-0">
          <span className="mb-1 block text-[11px] font-bold text-text-high">Model</span>
          <input
            value={model}
            readOnly={disabled}
            disabled={provider === 'none'}
            autoComplete="off"
            placeholder={placeholder}
            onChange={(ev) => onChange('embedding_model', ev.target.value)}
            className={`${inputCls} ${disabled || provider === 'none' ? 'cursor-default bg-fill-subtle' : ''}`}
          />
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">Optional — leave empty for the provider’s default.</span>
        </label>
      </div>

      <div className="mt-3 rounded-lg border border-line bg-fill-subtle px-3 py-2.5 text-[11.5px] text-text-mid">
        {status === undefined ? (
          <span className="flex items-center gap-1.5 text-text-muted">
            <Loader2 size={12} className="animate-spin" aria-hidden /> Checking the index…
          </span>
        ) : status === null ? (
          <span className="text-text-muted">Indexing status isn’t available on this server yet.</span>
        ) : (
          <div className="flex flex-col gap-1">
            <span>
              <span className="font-semibold text-text-high">
                {e?.configured ? `In use: ${embeddingProviderLabel(e.provider)}${e.model ? ` · ${e.model}` : ''}` : 'Keyword search — no embedding provider is configured'}
              </span>
              {e?.configured && (
                <>
                  {' · '}
                  {(e.indexed ?? 0).toLocaleString()} pieces indexed
                  {(e.pending ?? 0) > 0 ? `, ${(e.pending ?? 0).toLocaleString()} waiting` : ''}
                </>
              )}
            </span>
            {e?.last_error && (
              <span role="alert" className="flex items-start gap-1 text-[color:var(--red-ink)]">
                <AlertCircle size={12} className="mt-0.5 shrink-0" aria-hidden /> {e.last_error}
              </span>
            )}
            <Link href="/ai-monitor?tab=learning" className="w-fit font-semibold text-text-mid underline-offset-2 hover:text-text-high hover:underline">
              Re-index or see what the AI learned → AI monitor
            </Link>
          </div>
        )}
      </div>
    </div>
  )
}
