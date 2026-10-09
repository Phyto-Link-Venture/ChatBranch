'use client'

/**
 * Why the AI answered the selected turn the way it did: what production would
 * do with it, what it was based on (knowledge hits and lessons with their
 * scores), the tools it called (a calendar booking is only previewed), the
 * reply checks and rewrites, the model, tokens, estimated cost and time — and,
 * when asked for, the full prompt as a copyable block.
 */

import { useState, type ReactNode } from 'react'
import { BookOpen, CalendarCheck, CalendarX, Check, Copy, GraduationCap, Wrench, X } from 'lucide-react'
import { useFormat, useT, type MessageKey } from '@/i18n'
import type { ColumnResult, PlaygroundReply } from '@/lib/aiPlayground'
import { ProductionChip } from './ProductionChip'

const ACTION_LABEL: Record<string, MessageKey> = {
  send: 'playground.inspector.actions.send',
  handover: 'playground.inspector.actions.handover',
  block: 'playground.inspector.actions.block',
  empty: 'playground.inspector.actions.empty',
  error: 'playground.inspector.actions.error',
}

function Block({ title, children, aside }: { title: string; children: ReactNode; aside?: ReactNode }) {
  return (
    <section className="border-t border-line pt-3 first:border-t-0 first:pt-0">
      <div className="mb-1.5 flex items-center justify-between gap-2">
        <h3 className="eyebrow">{title}</h3>
        {aside}
      </div>
      {children}
    </section>
  )
}

function Mono({ children }: { children: string }) {
  return (
    <pre className="mt-1 max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-md bg-well px-2.5 py-2 font-mono text-[11px] leading-relaxed text-text-mid">
      {children}
    </pre>
  )
}

function CopyButton({ text }: { text: string }) {
  const t = useT()
  const [done, setDone] = useState(false)
  return (
    <button
      type="button"
      onClick={() => {
        void navigator.clipboard?.writeText(text).then(() => {
          setDone(true)
          window.setTimeout(() => setDone(false), 1500)
        })
      }}
      className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-[11.5px] font-semibold text-text-muted hover:bg-fill-hover hover:text-text-high"
    >
      {done ? <Check size={12} aria-hidden /> : <Copy size={12} aria-hidden />}
      {done ? t('playground.inspector.copied') : t('playground.inspector.copy')}
    </button>
  )
}

function promptText(p: NonNullable<PlaygroundReply['prompt']>, labels: { system: string; messages: string }) {
  return [
    `# ${labels.system}`,
    p.system,
    '',
    `# ${labels.messages}`,
    ...p.messages.map((m) => `[${m.role}] ${m.content}`),
  ].join('\n')
}

export function InspectorPane({
  result,
  columnName,
  canOverride,
}: {
  result: ColumnResult | null
  columnName: string | null
  canOverride: boolean
}) {
  const t = useT()
  const f = useFormat()
  const r = result?.reply
  if (!r)
    return <p className="px-1 py-6 text-center text-[13px] text-text-muted">{t('playground.inspector.empty')}</p>

  const score = (n: number) => f.number(n, { maximumFractionDigits: 2, minimumFractionDigits: 2 })
  const sandboxModel = r.provider === 'sandbox' || !r.provider
  const prompt = r.prompt
    ? promptText(r.prompt, { system: t('playground.inspector.system'), messages: t('playground.inspector.messages') })
    : ''

  return (
    <div className="flex flex-col gap-3 text-[12.5px]">
      {columnName && <div className="text-[11.5px] font-semibold text-text-muted">{t('playground.inspector.column', { name: columnName })}</div>}

      <Block title={t('playground.inspector.production')}>
        <ProductionChip production={r.production} full />
        {r.production.flags.length > 0 && (
          <div className="mt-2">
            <div className="text-[11.5px] font-semibold text-text-high">{t('playground.inspector.signals')}</div>
            <ul className="mt-0.5 list-disc pl-4 text-[11.5px] text-text-mid">
              {r.production.flags.map((x) => (
                <li key={x}>{x}</li>
              ))}
            </ul>
          </div>
        )}
      </Block>

      <Block title={t('playground.inspector.decision')}>
        <div className="text-text-high">{ACTION_LABEL[r.action] ? t(ACTION_LABEL[r.action]) : r.action}</div>
        {r.reason && <div className="mt-0.5 text-[11.5px] text-text-muted">{r.reason}</div>}
        {r.retries > 0 && (
          <div className="mt-0.5 text-[11.5px] text-amber-300">{t('playground.inspector.retries', { count: r.retries })}</div>
        )}
      </Block>

      <Block title={t('playground.inspector.basedOn')}>
        {r.grounding.length === 0 ? (
          <p className="text-[12px] text-text-muted">{t('playground.inspector.noGrounding')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {r.grounding.map((g) => (
              <li key={`${g.type}-${g.id}-${g.title}`} className="rounded-lg border border-line px-2.5 py-2">
                <div className="flex items-center gap-1.5">
                  {g.type === 'lesson' ? (
                    <GraduationCap size={12} className="shrink-0 text-purple-300" aria-hidden />
                  ) : (
                    <BookOpen size={12} className="shrink-0 text-brand-300" aria-hidden />
                  )}
                  <span className="min-w-0 flex-1 truncate font-semibold text-text-high">{g.title}</span>
                  <span className="shrink-0 font-mono text-[11px] text-text-muted">{score(g.score)}</span>
                </div>
                <div className="mt-0.5 text-[11px] uppercase tracking-wide text-text-faint">
                  {g.type === 'lesson' ? t('playground.inspector.lesson') : t('playground.inspector.knowledge')}
                </div>
                {g.chunk && <p className="mt-1 line-clamp-3 text-[11.5px] text-text-mid">{g.chunk}</p>}
              </li>
            ))}
          </ul>
        )}
        {r.hits.length > 0 && (
          <details className="mt-2">
            <summary className="cursor-pointer text-[11.5px] font-semibold text-text-muted hover:text-text-high">
              {t('playground.inspector.hits')}
            </summary>
            <ul className="mt-1 flex flex-col gap-0.5">
              {r.hits.map((h, i) => (
                <li key={i} className="flex items-center gap-2 text-[11.5px]">
                  <span className="min-w-0 flex-1 truncate text-text-mid">
                    {h.title}
                    {h.part ? ` (${h.part})` : ''}
                  </span>
                  <span className={h.used ? 'text-green-300' : 'text-text-faint'}>
                    {h.used ? t('playground.inspector.used') : t('playground.inspector.notUsed')}
                  </span>
                  <span className="w-10 shrink-0 text-right font-mono text-text-muted">{score(h.score)}</span>
                </li>
              ))}
            </ul>
          </details>
        )}
      </Block>

      <Block
        title={t('playground.inspector.tools')}
        aside={
          r.tools_offered.length > 0 ? (
            <span className="text-[11px] text-text-muted">
              {t('playground.inspector.offered', { count: r.tools_offered.length })}
            </span>
          ) : undefined
        }
      >
        {r.bookings_previewed.map((b, i) => (
          <div key={i} className="mb-2 flex items-start gap-1.5 rounded-lg bg-amber/15 px-2.5 py-2 text-[12px] text-amber-300">
            {b.type === 'cancel' ? (
              <CalendarX size={13} className="mt-[1px] shrink-0" aria-hidden />
            ) : (
              <CalendarCheck size={13} className="mt-[1px] shrink-0" aria-hidden />
            )}
            <span>
              {b.type === 'cancel'
                ? t('playground.inspector.wouldCancel', { label: b.label, timezone: b.timezone })
                : t('playground.inspector.wouldBook', { label: b.label, timezone: b.timezone })}
            </span>
          </div>
        ))}
        {r.tools.length === 0 ? (
          <p className="text-[12px] text-text-muted">{t('playground.inspector.noTools')}</p>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {r.tools.map((tool, i) => (
              <li key={i} className="rounded-lg border border-line px-2.5 py-2">
                <details>
                  <summary className="flex cursor-pointer list-none items-center gap-1.5">
                    <Wrench size={12} className="shrink-0 text-text-muted" aria-hidden />
                    <span className="min-w-0 flex-1 truncate font-mono text-[11.5px] font-semibold text-text-high">
                      {tool.name}
                    </span>
                    {tool.dry_run && (
                      <span className="rounded-full bg-amber/15 px-1.5 text-[10.5px] font-semibold text-amber-300">
                        {t('playground.inspector.dryRun')}
                      </span>
                    )}
                    {tool.ok ? (
                      <Check size={12} className="shrink-0 text-green-300" aria-label={t('playground.inspector.passed')} />
                    ) : (
                      <X size={12} className="shrink-0 text-red-300" aria-label={t('playground.inspector.failed')} />
                    )}
                  </summary>
                  <div className="mt-1.5 text-[11px] font-semibold text-text-muted">{t('playground.inspector.args')}</div>
                  <Mono>{tool.args || '{}'}</Mono>
                  <div className="mt-1.5 text-[11px] font-semibold text-text-muted">{t('playground.inspector.result')}</div>
                  <Mono>{tool.result}</Mono>
                </details>
              </li>
            ))}
          </ul>
        )}
      </Block>

      <Block title={t('playground.inspector.checks')}>
        {r.checks.length === 0 ? (
          <p className="text-[12px] text-text-muted">{t('playground.inspector.noChecks')}</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {r.checks.map((c, i) => (
              <li key={i} className="flex items-start gap-1.5">
                {c.passed ? (
                  <Check size={12} className="mt-[3px] shrink-0 text-green-300" aria-hidden />
                ) : (
                  <X size={12} className="mt-[3px] shrink-0 text-red-300" aria-hidden />
                )}
                <div className="min-w-0">
                  <span className="font-semibold text-text-high">{c.name}</span>{' '}
                  <span className="text-[11px] text-text-muted">
                    {c.passed ? t('playground.inspector.passed') : t('playground.inspector.failed')} ·{' '}
                    {t('playground.inspector.attempt', { n: c.attempt + 1 })}
                  </span>
                  {(c.reason || c.fix || c.error) && (
                    <div className="text-[11.5px] text-text-mid">{[c.reason, c.fix, c.error].filter(Boolean).join(' — ')}</div>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Block>

      <Block title={t('playground.inspector.model')}>
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12px]">
          <dt className="text-text-muted">{t('playground.inspector.model')}</dt>
          <dd className="min-w-0 truncate font-mono text-text-high">
            {sandboxModel ? t('playground.inspector.sandboxModel') : `${r.provider} · ${r.model}`}
          </dd>
          <dt className="text-text-muted">{t('playground.inspector.tokens')}</dt>
          <dd className="text-text-high">
            {t('playground.inspector.tokensValue', {
              prompt: f.number(r.tokens.prompt),
              completion: f.number(r.tokens.completion),
            })}
          </dd>
          <dt className="text-text-muted">{t('playground.inspector.cost')}</dt>
          <dd className="text-text-high">
            {r.cost_estimate.priced
              ? f.number(r.cost_estimate.usd, { style: 'currency', currency: 'USD', maximumFractionDigits: 6 })
              : t('playground.inspector.unpriced')}
          </dd>
          <dt className="text-text-muted">{t('playground.inspector.latency')}</dt>
          <dd className="text-text-high">{t('playground.inspector.latencyValue', { ms: f.number(r.latency_ms) })}</dd>
        </dl>
        {r.provider_error && (
          <p role="alert" className="mt-1.5 text-[11.5px] text-[color:var(--red-ink)]">
            {t('playground.inspector.providerError', { error: r.provider_error })}
          </p>
        )}
      </Block>

      {(r.prompt || canOverride) && (
        <Block title={t('playground.inspector.prompt')} aside={r.prompt ? <CopyButton text={prompt} /> : undefined}>
          {r.prompt ? (
            <details>
              <summary className="cursor-pointer text-[12px] font-semibold text-text-high">
                {t('playground.inspector.showPrompt')}
              </summary>
              <pre className="mt-1.5 max-h-[420px] overflow-auto whitespace-pre-wrap break-words rounded-md bg-well px-2.5 py-2 font-mono text-[11px] leading-relaxed text-text-mid">
                {prompt}
              </pre>
            </details>
          ) : (
            <p className="text-[11.5px] text-text-muted">{t('playground.inspector.promptHint')}</p>
          )}
        </Block>
      )}
    </div>
  )
}
