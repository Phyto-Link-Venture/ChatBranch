'use client'

/**
 * Knowledge Base → "Test retrieval": type what a customer might ask and see
 * what the AI would be handed — the scored matches (knowledge pieces and
 * learned lessons, with their meaning / keyword scores when the server sends
 * them) and the exact prompt blocks, within the character budget. "Why did
 * the bot say that?" stays a button rather than a code investigation.
 */

import { useState } from 'react'
import { BookOpen, ChevronDown, ChevronRight, GraduationCap, Loader2, Search } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { aiChannelLabel, embeddingProviderLabel, previewRetrieval, type RetrievalPreview } from '@/lib/aiLearning'
import { useFormat, useT } from '@/i18n'

const CHANNELS = ['whatsapp', 'telegram', 'facebook', 'instagram', 'email', 'website', 'voice']

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

function Score({ label, v }: { label: string; v: number | null }) {
  const f = useFormat()
  if (v == null) return null
  return (
    <span className="tabular-nums">
      {label} {v <= 1 ? f.percent(v) : f.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
    </span>
  )
}

export function RetrievalTester({ entity }: { entity: string | null }) {
  const t = useT()
  const [query, setQuery] = useState('')
  const [channel, setChannel] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<RetrievalPreview | null>(null)
  const [open, setOpen] = useState<Set<number>>(new Set())
  const [showBlock, setShowBlock] = useState(true)

  const run = async () => {
    setBusy(true)
    setError(null)
    try {
      setResult(await previewRetrieval(query.trim(), channel, entity))
      setOpen(new Set())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const top = result?.hits.reduce((m, h) => Math.max(m, h.score), 0) || 1

  return (
    <div className="rounded-xl border border-line bg-card p-4">
      <div className="mb-1 flex items-center gap-2 text-[12.5px] font-bold text-text-high">
        <Search size={14} className="text-text-muted" aria-hidden /> {t('ai.retrieval.title')}
      </div>
      <p className="mb-3 max-w-[680px] text-[11.5px] leading-relaxed text-text-muted">{t('ai.retrieval.body')}</p>
      <form
        className="flex flex-col gap-2 sm:flex-row"
        onSubmit={(e) => {
          e.preventDefault()
          void run()
        }}
      >
        <input
          value={query}
          placeholder={t('ai.retrieval.placeholder')}
          onChange={(e) => setQuery(e.target.value)}
          className={inputCls}
          aria-label={t('ai.retrieval.queryAria')}
        />
        <div className="flex gap-2">
          <Select
            value={channel}
            onChange={setChannel}
            ariaLabel={t('common.fields.channel')}
            className="w-[150px] min-w-0 shrink sm:shrink-0"
            options={[
              { value: '', label: t('ai.common.anyChannel') },
              ...CHANNELS.map((c) => ({ value: c, label: aiChannelLabel(c, t) })),
            ]}
          />
          <Button type="submit" variant="secondary" disabled={busy} className="shrink-0">
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Search size={13} aria-hidden />}{' '}
            {t('ai.retrieval.test')}
          </Button>
        </div>
      </form>

      {error && (
        <div role="alert" className="mt-3 text-[12px] text-[color:var(--red-ink)]">
          {t('ai.retrieval.runError', { error })}
        </div>
      )}

      {result && (
        <div className="mt-4 flex flex-col gap-4">
          {result.scored && (
            <section aria-label={t('ai.retrieval.whatFound')}>
              <div className="mb-1.5 flex flex-wrap items-baseline gap-x-2">
                <span className="eyebrow">{t('ai.retrieval.whatFound')}</span>
                {result.semantic !== null && (
                  <span className="text-[11px] text-text-muted">
                    {result.semantic
                      ? result.provider
                        ? t('ai.retrieval.matchedMeaningProvider', {
                            provider: `${embeddingProviderLabel(result.provider, t)}${result.model ? ` ${result.model}` : ''}`,
                          })
                        : t('ai.retrieval.matchedMeaning')
                      : t('ai.retrieval.matchedKeywords')}
                  </span>
                )}
              </div>
              {result.hits.length === 0 ? (
                <p className="text-[12px] text-text-muted">
                  {query.trim() ? t('ai.retrieval.nothingMatched') : t('ai.retrieval.nothingMatchedEmpty')}
                </p>
              ) : (
                <ol className="flex flex-col gap-1.5">
                  {result.hits.map((h, i) => {
                    const lesson = h.type === 'lesson'
                    const Icon = lesson ? GraduationCap : BookOpen
                    const expanded = open.has(i)
                    return (
                      <li key={`${h.type}-${h.id}-${i}`} className="rounded-lg border border-line bg-fill-subtle">
                        <button
                          type="button"
                          aria-expanded={expanded}
                          onClick={() =>
                            setOpen((cur) => {
                              const n = new Set(cur)
                              if (n.has(i)) n.delete(i)
                              else n.add(i)
                              return n
                            })
                          }
                          className="flex w-full items-start gap-2.5 px-3 py-2 text-left"
                        >
                          <span className="mt-0.5 w-4 shrink-0 text-right text-[11px] font-semibold tabular-nums text-text-faint">{i + 1}</span>
                          <Icon size={14} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
                          <span className="min-w-0 flex-1">
                            <span className="flex flex-wrap items-baseline gap-x-2">
                              <span className="truncate text-[12.5px] font-semibold text-text-high">
                                {h.title ||
                                  (lesson
                                    ? t('ai.common.lessonN', { id: String(h.id) })
                                    : t('ai.common.knowledgeN', { id: String(h.id) }))}
                              </span>
                              <span className="text-[10.5px] font-semibold uppercase tracking-wide text-text-faint">
                                {lesson ? t('ai.retrieval.typeLesson') : t('ai.retrieval.typeKnowledge')}
                              </span>
                            </span>
                            {h.chunk && (
                              <span
                                className={`mt-0.5 block whitespace-pre-wrap break-words text-[12px] leading-snug text-text-mid ${expanded ? '' : 'line-clamp-2'}`}
                              >
                                {h.chunk}
                              </span>
                            )}
                            <span className="mt-1 flex items-center gap-2">
                              <span className="h-1 w-24 shrink-0 rounded-full bg-fill" aria-hidden>
                                <span className="block h-1 rounded-full bg-primary" style={{ width: `${Math.max(4, (h.score / top) * 100)}%` }} />
                              </span>
                              <span className="flex flex-wrap gap-x-2 text-[11px] text-text-muted">
                                <Score label={t('ai.retrieval.score')} v={h.score} />
                                {result.semantic !== false && <Score label={t('ai.retrieval.meaning')} v={h.semantic} />}
                                <Score label={t('ai.retrieval.keywords')} v={h.keyword} />
                              </span>
                            </span>
                          </span>
                          {h.chunk && h.chunk.length > 140 && (
                            <span className="mt-0.5 shrink-0 text-text-faint" aria-hidden>
                              {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                            </span>
                          )}
                        </button>
                      </li>
                    )
                  })}
                </ol>
              )}
            </section>
          )}

          <section aria-label={t('ai.retrieval.handedLabel')}>
            <button
              type="button"
              aria-expanded={showBlock}
              onClick={() => setShowBlock((v) => !v)}
              className="eyebrow mb-1.5 inline-flex items-center gap-1"
            >
              {showBlock ? <ChevronDown size={12} aria-hidden /> : <ChevronRight size={12} aria-hidden />}{' '}
              {t('ai.retrieval.exactly')}
            </button>
            {showBlock && (
              <>
                <pre className="max-h-[280px] overflow-auto whitespace-pre-wrap break-words rounded-lg border border-line bg-fill-subtle p-3 text-[11.5px] leading-[1.6] text-text-mid">
                  {[result.block, ...result.extraBlocks.map((b) => b.text)].filter((x) => x.trim()).join('\n\n') ||
                    t('ai.retrieval.nothingHanded')}
                </pre>
                <div className="mt-1 text-[10.5px] text-text-muted">
                  {t('ai.retrieval.budget', { used: result.chars, budget: result.budget })}
                </div>
              </>
            )}
          </section>
        </div>
      )}
    </div>
  )
}
