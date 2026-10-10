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
import { useT, type TFunction } from '@/i18n'

export const EMBEDDING_KEYS = ['embedding_provider', 'embedding_model']

/** Provider choices (OpenAI / Google Gemini are brand names and stay as they are). */
const providers = (t: TFunction): { value: string; label: string; hint: string }[] => [
  { value: '', label: t('ai.embeddings.providers.auto'), hint: t('ai.embeddings.providers.autoHint') },
  { value: 'openai', label: 'OpenAI', hint: t('ai.embeddings.providers.openaiHint') },
  { value: 'google', label: 'Google Gemini', hint: t('ai.embeddings.providers.googleHint') },
  { value: 'openrouter', label: 'OpenRouter', hint: t('ai.embeddings.providers.openrouterHint') },
  { value: 'local', label: t('ai.embeddings.providers.local'), hint: t('ai.embeddings.providers.localHint') },
  { value: 'none', label: t('ai.embeddings.providers.off'), hint: t('ai.embeddings.providers.offHint') },
]

const DEFAULT_MODEL: Record<string, string> = {
  openai: 'text-embedding-3-small',
  google: 'gemini-embedding-001',
  openrouter: 'openai/text-embedding-3-small',
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
  const t = useT()
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
      ? t('ai.embeddings.notUsed')
      : DEFAULT_MODEL[provider] ??
        (e?.provider && DEFAULT_MODEL[e.provider]
          ? t('ai.embeddings.providerDefaultFor', { model: DEFAULT_MODEL[e.provider] })
          : t('ai.embeddings.providerDefault'))

  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <div className="flex items-center gap-2">
        <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-fill">
          <Sparkles size={14} className="text-brand-300" aria-hidden />
        </span>
        <span className="text-[12.5px] font-bold text-text-high">{t('ai.embeddings.title')}</span>
      </div>
      <p className="mb-3 mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">{t('ai.embeddings.body')}</p>

      <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2">
        <label className="block min-w-0">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('ai.embeddings.provider')}</span>
          <Select
            value={provider}
            disabled={disabled}
            onChange={(v) => onChange('embedding_provider', v)}
            options={providers(t)}
            size="md"
            ariaLabel={t('ai.embeddings.providerAria')}
            className="w-full"
          />
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{t('ai.embeddings.providerHelp')}</span>
        </label>
        <label className="block min-w-0">
          <span className="mb-1 block text-[11px] font-bold text-text-high">{t('ai.embeddings.model')}</span>
          <input
            value={model}
            readOnly={disabled}
            disabled={provider === 'none'}
            autoComplete="off"
            placeholder={placeholder}
            onChange={(ev) => onChange('embedding_model', ev.target.value)}
            className={`${inputCls} ${disabled || provider === 'none' ? 'cursor-default bg-fill-subtle' : ''}`}
          />
          <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{t('ai.embeddings.modelHelp')}</span>
        </label>
      </div>

      <div className="mt-3 rounded-lg border border-line bg-fill-subtle px-3 py-2.5 text-[11.5px] text-text-mid">
        {status === undefined ? (
          <span className="flex items-center gap-1.5 text-text-muted">
            <Loader2 size={12} className="animate-spin" aria-hidden /> {t('ai.embeddings.checkingIndex')}
          </span>
        ) : status === null ? (
          <span className="text-text-muted">{t('ai.embeddings.statusUnavailable')}</span>
        ) : (
          <div className="flex flex-col gap-1">
            <span>
              <span className="font-semibold text-text-high">
                {e?.configured
                  ? e.model
                    ? t('ai.embeddings.inUseModel', { provider: embeddingProviderLabel(e.provider, t), model: e.model })
                    : t('ai.embeddings.inUse', { provider: embeddingProviderLabel(e.provider, t) })
                  : t('ai.embeddings.keywordNoProvider')}
              </span>
              {e?.configured && (
                <>
                  {' · '}
                  {t('ai.learning.piecesIndexed', { count: e.indexed ?? 0 })}
                  {(e.pending ?? 0) > 0 ? ` · ${t('ai.learning.piecesWaiting', { count: e.pending ?? 0 })}` : ''}
                </>
              )}
            </span>
            {e?.last_error && (
              <span role="alert" className="flex items-start gap-1 text-[color:var(--red-ink)]">
                <AlertCircle size={12} className="mt-0.5 shrink-0" aria-hidden /> {e.last_error}
              </span>
            )}
            <Link href="/ai-monitor?tab=learning" className="w-fit font-semibold text-text-mid underline-offset-2 hover:text-text-high hover:underline">
              {t('ai.embeddings.reindexLink')}
            </Link>
          </div>
        )}
      </div>
    </div>
  )
}
