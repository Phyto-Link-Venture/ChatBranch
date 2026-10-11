'use client'

/**
 * AI monitor → Stats, "Feedback" section (GET /api/ai/learning/stats): how
 * often people rated the AI's replies helpful, why the 👎s, and the KNOWLEDGE
 * GAPS — questions customers keep asking that the AI couldn't answer (it
 * handed over, was blocked, or got a 👎 for wrong / missing information),
 * each with "Add answer" straight into the knowledge base.
 *
 * Chart (dataviz skill): one diverging daily bar chart on ONE axis — 👍 above
 * the baseline, 👎 below — so polarity is carried by position, colour only
 * repeats it. Colours reuse StatsPanel's validated emphasis pair (the brand
 * fill, stepped 15% toward black in dark mode, against --text-faint; checked
 * for every theme × mode): 👎 gets the accent because it is what needs
 * attention, 👍 the de-emphasised gray. The legend, the per-day tooltip and the
 * table view carry every value, so nothing relies on colour alone.
 *
 * Renders nothing on a server without the endpoint.
 */

import Link from 'next/link'
import { useCallback, useEffect, useId, useState } from 'react'
import { ExternalLink, Lightbulb, Loader2, Plus, Table2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { useAccess } from '@/lib/useAccess'
import { ApiError, parseUtc } from '@/lib/aiMonitor'
import { learningApi, reasonLabel, threadHref, type LearningStats as Stats } from '@/lib/aiLearning'
import { AddAnswerModal } from './AddAnswerModal'
import { useDefaultAvailability } from '@/components/ui/AvailabilityPicker'
import { useFormat, useT } from '@/i18n'

const VIZ_CSS = `
.ail-viz{--ail-down:rgb(var(--c-brand));--ail-up:var(--text-faint);}
:root.dark .ail-viz{--ail-down:color-mix(in srgb, rgb(var(--c-brand)) 85%, black);}
`

function niceMax(v: number): number {
  if (v <= 4) return 4
  const pow = 10 ** Math.floor(Math.log10(v))
  for (const m of [1, 2, 2.5, 5, 10]) if (m * pow >= v) return m * pow
  return 10 * pow
}

/** "2026-10-09" → a local Date at that calendar day (null when it isn't one). */
function dayDate(iso: string): Date | null {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return y && m && d ? new Date(y, m - 1, d) : null
}

function rateOf(up: number, down: number): number | null {
  return up + down > 0 ? Math.round((up * 100) / (up + down)) : null
}

function Tile({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="rounded-xl border border-line bg-card px-4 py-3.5">
      <div className="text-[11.5px] font-medium text-text-muted">{label}</div>
      <div className="mt-1 text-[24px] font-semibold leading-none tracking-[-0.5px] text-text-heading">{value}</div>
      {sub && <div className="mt-1.5 text-[11px] text-text-faint">{sub}</div>}
    </div>
  )
}

function RatingChart({ daily }: { daily: Stats['daily'] }) {
  const t = useT()
  const f = useFormat()
  const shortDate = (iso: string) => {
    const d = dayDate(iso)
    return d ? f.dayMonth(d) : iso
  }
  const pct = (r: number | null) => (r == null ? '—' : f.percent(r / 100))
  const [hover, setHover] = useState<number | null>(null)
  const [table, setTable] = useState(false)
  const tipId = useId()
  const maxUp = Math.max(0, ...daily.map((d) => d.up))
  const maxDown = Math.max(0, ...daily.map((d) => d.down))
  const top = niceMax(Math.max(1, maxUp))
  const bottom = maxDown > 0 ? niceMax(maxDown) : 0
  const H = 150
  // one scale for both sides; an all-👍 window still keeps a sliver below 0
  const span = top + Math.max(bottom, top * 0.12)
  const px = H / span
  const base = top * px
  const colW = daily.length > 45 ? 8 : daily.length > 14 ? 14 : 32

  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[12px] text-text-mid">
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: 'var(--ail-up)' }} aria-hidden />
          {t('ai.learningStats.helpfulAbove')}
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: 'var(--ail-down)' }} aria-hidden />
          {t('ai.learningStats.notHelpfulBelow')}
        </span>
        <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setTable((v) => !v)} aria-pressed={table}>
          <Table2 size={13} aria-hidden /> {table ? t('ai.common.showChart') : t('ai.common.showTable')}
        </Button>
      </div>

      {table ? (
        <div className="max-h-[320px] overflow-auto rounded-lg border border-line">
          <table className="w-full text-left text-[12.5px]">
            <thead className="sticky top-0 bg-card text-text-muted">
              <tr>
                <th className="px-3 py-2 font-semibold">{t('ai.common.day')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('ai.lesson.helpful')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('ai.lesson.notHelpful')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('ai.learningStats.ratedHelpful')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line tabular-nums text-text-high">
              {[...daily].reverse().map((d) => {
                const r = rateOf(d.up, d.down)
                return (
                  <tr key={d.day}>
                    <td className="px-3 py-1.5">{shortDate(d.day)}</td>
                    <td className="px-3 py-1.5 text-right">{d.up}</td>
                    <td className="px-3 py-1.5 text-right">{d.down}</td>
                    <td className="px-3 py-1.5 text-right">{pct(r)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="flex gap-2">
          <div className="relative w-7 shrink-0 text-right text-[10.5px] tabular-nums text-text-faint" style={{ height: H }} aria-hidden>
            <span className="absolute right-0 -translate-y-1/2" style={{ top: 0 }}>
              {top}
            </span>
            <span className="absolute right-0 -translate-y-1/2" style={{ top: base }}>
              0
            </span>
            {bottom > 0 && (
              <span className="absolute right-0 -translate-y-1/2" style={{ top: base + bottom * px }}>
                {bottom}
              </span>
            )}
          </div>
          <div className="min-w-0 flex-1 overflow-x-auto">
            <div className="relative" style={{ minWidth: daily.length * (colW > 8 ? colW : 6) }}>
              {[0, base, bottom > 0 ? base + bottom * px : null]
                .filter((y): y is number => y != null)
                .map((y) => (
                  <div
                    key={y}
                    className={`pointer-events-none absolute inset-x-0 border-t ${y === base ? 'border-line-strong' : 'border-line'}`}
                    style={{ top: y }}
                    aria-hidden
                  />
                ))}
              <div className="relative flex" style={{ height: H }} role="list" aria-label={t('ai.learningStats.perDayLabel')}>
                {daily.map((d, i) => {
                  const r = rateOf(d.up, d.down)
                  return (
                    <button
                      key={d.day}
                      type="button"
                      role="listitem"
                      className="relative h-full flex-1 cursor-default outline-none"
                      onPointerEnter={() => setHover(i)}
                      onPointerLeave={() => setHover((h) => (h === i ? null : h))}
                      onFocus={() => setHover(i)}
                      onBlur={() => setHover((h) => (h === i ? null : h))}
                      aria-label={
                        r == null
                          ? t('ai.learningStats.barLabel', { date: shortDate(d.day), up: d.up, down: d.down })
                          : t('ai.learningStats.barLabelRate', { date: shortDate(d.day), up: d.up, down: d.down, rate: pct(r) })
                      }
                      aria-describedby={hover === i ? tipId : undefined}
                    >
                      <span
                        className={`absolute left-1/2 flex w-full -translate-x-1/2 flex-col items-center ${hover === i ? 'opacity-80' : ''}`}
                        style={{ top: 0, height: H, maxWidth: Math.min(24, colW) }}
                      >
                        {d.up > 0 && (
                          <span
                            className="absolute w-full rounded-t"
                            style={{ height: Math.max(2, d.up * px - 1), top: base - Math.max(2, d.up * px - 1) - 1, background: 'var(--ail-up)' }}
                          />
                        )}
                        {d.down > 0 && (
                          <span
                            className="absolute w-full rounded-b"
                            style={{ height: Math.max(2, d.down * px - 1), top: base + 1, background: 'var(--ail-down)' }}
                          />
                        )}
                      </span>
                    </button>
                  )
                })}
              </div>
              {hover != null && daily[hover] && (
                <div
                  id={tipId}
                  role="tooltip"
                  className="pointer-events-none absolute z-10 w-max min-w-[150px] rounded-lg border border-line-strong bg-card px-3 py-2 text-[12px] shadow-pop"
                  style={{
                    top: 4,
                    left: `${((hover + 0.5) / daily.length) * 100}%`,
                    transform: `translateX(${hover / daily.length > 0.6 ? '-100%' : hover / daily.length < 0.2 ? '0' : '-50%'})`,
                  }}
                >
                  <div className="mb-1 text-[11px] text-text-muted">{shortDate(daily[hover].day)}</div>
                  {[
                    { k: t('ai.lesson.helpful'), v: f.number(daily[hover].up), c: 'var(--ail-up)' },
                    { k: t('ai.lesson.notHelpful'), v: f.number(daily[hover].down), c: 'var(--ail-down)' },
                    {
                      k: t('ai.learningStats.ratedHelpfulLower'),
                      v: pct(rateOf(daily[hover].up, daily[hover].down)),
                      c: '',
                    },
                  ].map((row) => (
                    <div key={row.k} className="flex items-center gap-2">
                      <span className="inline-block h-[2px] w-3" style={{ background: row.c || 'transparent' }} aria-hidden />
                      <span className="font-semibold tabular-nums text-text-heading">{row.v}</span>
                      <span className="text-text-muted">{row.k}</span>
                    </div>
                  ))}
                </div>
              )}
              <div className="relative mt-1.5 h-4 text-[10.5px] text-text-faint">
                {[0, Math.floor((daily.length - 1) / 2), daily.length - 1]
                  .filter((v, i, a) => a.indexOf(v) === i)
                  .map((i) => (
                    <span
                      key={i}
                      className="absolute whitespace-nowrap"
                      style={{
                        left: `${((i + 0.5) / daily.length) * 100}%`,
                        transform: i === 0 ? 'translateX(-20%)' : i === daily.length - 1 ? 'translateX(-80%)' : 'translateX(-50%)',
                      }}
                    >
                      {shortDate(daily[i].day)}
                    </span>
                  ))}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function ReasonBars({ rows }: { rows: Stats['reasons'] }) {
  const t = useT()
  const f = useFormat()
  if (!rows.length) return <p className="text-[12.5px] text-text-muted">{t('ai.learningStats.noDown')}</p>
  const max = Math.max(...rows.map((r) => r.count))
  return (
    <ul className="flex flex-col gap-2.5">
      {rows.map((r) => (
        <li key={r.reason}>
          <div className="mb-1 flex items-baseline justify-between gap-3 text-[12.5px]">
            <span className="min-w-0 break-words text-text-high">{reasonLabel(r.reason, t)}</span>
            <span className="shrink-0 font-semibold tabular-nums text-text-heading">{f.number(r.count)}</span>
          </div>
          <div className="h-1.5 w-full rounded-full bg-fill">
            <div className="h-1.5 rounded-full" style={{ width: `${Math.max(3, (r.count / max) * 100)}%`, background: 'var(--ail-down)' }} />
          </div>
        </li>
      ))}
    </ul>
  )
}

export function LearningStats({ entityQuery, days }: { entityQuery: string; days: number }) {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const defaultAvailability = useDefaultAvailability()
  const [stats, setStats] = useState<Stats | null>(null)
  const [hidden, setHidden] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [answering, setAnswering] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setStats(await learningApi.stats(days, entityQuery))
      setHidden(false)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && [403, 404, 405].includes(e.status)) setHidden(true)
      else setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [days, entityQuery])

  useEffect(() => {
    void load()
  }, [load])

  if (hidden) return null
  const canAddKnowledge = ready && can('settings.ai.manage')
  const tot = stats?.totals
  const rate = tot ? rateOf(tot.up, tot.down) : null
  const decisions = tot ? tot.approved + tot.edited + tot.rejected : 0

  return (
    <section className="ail-viz flex flex-col gap-4" aria-label={t('ai.learningStats.sectionLabel')}>
      <style>{VIZ_CSS}</style>
      <div className="flex items-center gap-2 pt-2">
        <h2 className="display text-[21px] leading-tight text-text-heading">{t('ai.learningStats.title')}</h2>
        {loading && <Loader2 size={14} className="animate-spin text-text-faint" aria-label={t('common.status.loading')} />}
      </div>
      {error && (
        <div role="alert" className="text-[12.5px] text-[color:var(--red-ink)]">
          {t('ai.learningStats.loadError', { error })}
        </div>
      )}
      {stats && tot && (
        <div className={`flex flex-col gap-4 transition-opacity ${loading ? 'opacity-70' : ''}`}>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <Tile
              label={t('ai.learningStats.ratedHelpful')}
              value={rate == null ? '—' : f.percent(rate / 100)}
              sub={tot.rated ? t('ai.learningStats.ofRated', { count: tot.rated }) : t('ai.learningStats.nobodyRated')}
            />
            <Tile
              label={t('ai.learningStats.ratings')}
              value={f.number(tot.rated)}
              sub={t('ai.learningStats.upDown', { up: tot.up, down: tot.down })}
            />
            <Tile
              label={t('ai.learningStats.decisions')}
              value={f.number(decisions)}
              sub={t('ai.learningStats.decisionsSub', { approved: tot.approved, edited: tot.edited, rejected: tot.rejected })}
            />
          </div>

          {stats.daily.length > 0 && (
            <Card className="px-5 py-4">
              <CardTitle className="mb-3">{t('ai.learningStats.perDayLabel')}</CardTitle>
              <RatingChart daily={stats.daily} />
            </Card>
          )}

          <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
            <Card className="px-5 py-4">
              <CardTitle className="mb-3">{t('ai.learningStats.whyDown')}</CardTitle>
              <ReasonBars rows={stats.reasons} />
            </Card>
            <Card className="px-5 py-4">
              <CardTitle className="mb-1">{t('ai.learningStats.gaps')}</CardTitle>
              <p className="mb-3 text-[12.5px] leading-relaxed text-text-muted">{t('ai.learningStats.gapsBody')}</p>
              {notice && (
                <div role="status" className="mb-2 rounded-lg border border-line bg-well px-3 py-1.5 text-[12.5px] text-text-high">
                  {notice}
                </div>
              )}
              {stats.gaps.length === 0 ? (
                <p className="text-[12.5px] text-text-muted">{t('ai.learningStats.noGaps')}</p>
              ) : (
                <ul className="flex flex-col divide-y divide-line">
                  {stats.gaps.map((g, i) => {
                    const href = threadHref(g.thread, g.thread_id)
                    return (
                      <li key={`${g.topic}-${i}`} className="flex flex-col gap-1.5 py-2.5 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:gap-3">
                        <Lightbulb size={15} className="mt-0.5 hidden shrink-0 text-text-faint sm:block" aria-hidden />
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-baseline gap-x-2">
                            <span className="text-[13px] font-semibold text-text-heading">
                              {g.topic || t('ai.learningStats.untitled')}
                            </span>
                            <span className="text-[11.5px] tabular-nums text-text-muted">
                              {g.last_at
                                ? t('ai.learningStats.askedLast', { count: g.count, when: f.relative(parseUtc(g.last_at)) })
                                : t('ai.learningStats.asked', { count: g.count })}
                            </span>
                          </div>
                          {g.example && (
                            <p className="mt-0.5 break-words text-[12.5px] leading-snug text-text-mid">
                              {t('ai.common.quoted', { text: g.example })}
                            </p>
                          )}
                        </div>
                        <div className="flex shrink-0 items-center gap-2">
                          {href && (
                            <Link href={href} className="inline-flex items-center gap-1 text-[12px] font-semibold text-text-mid hover:text-text-high hover:underline">
                              {t('ai.learningStats.chat')} <ExternalLink size={11} aria-hidden />
                            </Link>
                          )}
                          {canAddKnowledge && (
                            <Button
                              size="sm"
                              variant="secondary"
                              onClick={() => {
                                setNotice(null)
                                setAnswering(g.example || g.topic)
                              }}
                            >
                              <Plus size={13} aria-hidden /> {t('ai.learningStats.addAnswer')}
                            </Button>
                          )}
                        </div>
                      </li>
                    )
                  })}
                </ul>
              )}
            </Card>
          </div>
        </div>
      )}
      <AddAnswerModal
        question={answering}
        onClose={() => setAnswering(null)}
        defaultEntities={defaultAvailability}
        onAdded={(title) => setNotice(t('ai.learningStats.added', { title }))}
      />
    </section>
  )
}
