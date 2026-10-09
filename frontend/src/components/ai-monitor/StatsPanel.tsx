'use client'

/**
 * Stats — how the AI and its reviewers are doing over a window: a KPI row,
 * one daily chart (AI messages per day, held ones emphasised), and the
 * reasons replies were held / rejected.
 *
 * Chart colour (dataviz skill, emphasis form — one accent + de-emphasis gray,
 * checked with scripts/validate_palette.js for every theme × mode against the
 * card surface): held = the theme's brand fill (dark mode steps it 15% toward
 * black so it sits inside the dark lightness band); sent automatically =
 * --text-faint. CVD ΔE ≥ 9.3 and normal-vision ΔE ≥ 15.8 in all six; the gray
 * reads < 3:1 on some surfaces, so the legend, tooltip and table view carry
 * every value (never colour alone).
 */

import { useCallback, useEffect, useId, useMemo, useState } from 'react'
import { Loader2, Table2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { KIND_COUNT, aiApi, duration, rejectReasonLabel, type DraftKind, type MonitorStats } from '@/lib/aiMonitor'
import { LearningStats } from '@/components/ai-learning/LearningStats'
import { useFormat, useT } from '@/i18n'

const DAY_OPTIONS = [7, 30, 90]

const VIZ_CSS = `
.aim-viz{--aim-held:rgb(var(--c-brand));--aim-auto:var(--text-faint);}
:root.dark .aim-viz{--aim-held:color-mix(in srgb, rgb(var(--c-brand)) 85%, black);}
`

function Tile({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="rounded-xl border border-line bg-card px-4 py-3.5">
      <div className="text-[11.5px] font-medium text-text-muted">{label}</div>
      <div className="mt-1 text-[24px] font-semibold leading-none tracking-[-0.5px] text-text-heading">{value}</div>
      {sub && <div className="mt-1.5 text-[11px] text-text-faint">{sub}</div>}
    </div>
  )
}

function niceMax(v: number): number {
  if (v <= 4) return 4
  const pow = 10 ** Math.floor(Math.log10(v))
  for (const m of [1, 2, 2.5, 5, 10]) if (m * pow >= v) return m * pow
  return 10 * pow
}

/** "2026-10-09" → a local Date at that calendar day (so formatting never shifts it a day). */
function dayDate(iso: string): Date {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return new Date(y, m - 1, d)
}

function DailyChart({ daily }: { daily: MonitorStats['daily'] }) {
  const t = useT()
  const f = useFormat()
  const shortDate = (iso: string) => f.date(dayDate(iso))
  const [hover, setHover] = useState<number | null>(null)
  const [table, setTable] = useState(false)
  const tipId = useId()
  const max = niceMax(Math.max(1, ...daily.map((d) => d.auto_sent + d.held)))
  const ticks = [0, max / 2, max]
  const H = 160
  const colW = daily.length > 45 ? 8 : daily.length > 14 ? 14 : 32

  return (
    <div className="aim-viz">
      <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[12px] text-text-mid">
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: 'var(--aim-held)' }} aria-hidden />
          {t('ai.stats.heldForReview')}
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: 'var(--aim-auto)' }} aria-hidden />
          {t('ai.draftStatus.autoSent')}
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
                <th className="px-3 py-2 text-right font-semibold">{t('ai.stats.held')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('ai.draftStatus.autoSent')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('ai.draftStatus.rejected')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line tabular-nums text-text-high">
              {[...daily].reverse().map((d) => (
                <tr key={d.date}>
                  <td className="px-3 py-1.5">{shortDate(d.date)}</td>
                  <td className="px-3 py-1.5 text-right">{d.held}</td>
                  <td className="px-3 py-1.5 text-right">{d.auto_sent}</td>
                  <td className="px-3 py-1.5 text-right">{d.rejected}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="flex gap-2">
          {/* y-axis ticks */}
          <div className="relative w-7 shrink-0 text-right text-[10.5px] tabular-nums text-text-faint" style={{ height: H }}>
            {ticks.map((t) => (
              <span key={t} className="absolute right-0 -translate-y-1/2" style={{ top: H - (t / max) * H }}>
                {Math.round(t)}
              </span>
            ))}
          </div>
          <div className="min-w-0 flex-1 overflow-x-auto">
            <div className="relative" style={{ minWidth: daily.length * (colW > 8 ? colW : 6) }}>
              {/* hairline grid */}
              {ticks.map((t) => (
                <div
                  key={t}
                  className="pointer-events-none absolute inset-x-0 border-t border-line"
                  style={{ top: H - (t / max) * H }}
                  aria-hidden
                />
              ))}
              <div className="relative flex items-end" style={{ height: H }} role="list" aria-label={t('ai.stats.chartLabel')}>
                {daily.map((d, i) => {
                  const total = d.auto_sent + d.held
                  const autoH = (d.auto_sent / max) * H
                  const heldH = (d.held / max) * H
                  return (
                    <button
                      key={d.date}
                      type="button"
                      role="listitem"
                      className="group relative flex h-full flex-1 cursor-default flex-col items-center justify-end outline-none"
                      onPointerEnter={() => setHover(i)}
                      onPointerLeave={() => setHover((h) => (h === i ? null : h))}
                      onFocus={() => setHover(i)}
                      onBlur={() => setHover((h) => (h === i ? null : h))}
                      aria-label={t('ai.stats.barLabel', { date: shortDate(d.date), held: d.held, sent: d.auto_sent })}
                      aria-describedby={hover === i ? tipId : undefined}
                    >
                      <span
                        className={`flex w-full flex-col items-center justify-end ${hover === i ? 'opacity-80' : ''}`}
                        style={{ maxWidth: Math.min(24, colW) }}
                      >
                        {d.held > 0 && (
                          <span
                            className="w-full rounded-t"
                            style={{ height: Math.max(2, heldH), background: 'var(--aim-held)' }}
                          />
                        )}
                        {d.held > 0 && d.auto_sent > 0 && <span className="h-[2px] w-full" aria-hidden />}
                        {d.auto_sent > 0 && (
                          <span
                            className={`w-full ${d.held > 0 ? '' : 'rounded-t'}`}
                            style={{ height: Math.max(2, autoH), background: 'var(--aim-auto)' }}
                          />
                        )}
                        {total === 0 && <span className="h-px w-full bg-line" aria-hidden />}
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
                    // inside the plot: the horizontal scroll box would clip anything above it
                    top: 4,
                    left: `${((hover + 0.5) / daily.length) * 100}%`,
                    transform: `translateX(${hover / daily.length > 0.6 ? '-100%' : hover / daily.length < 0.2 ? '0' : '-50%'})`,
                  }}
                >
                  <div className="mb-1 text-[11px] text-text-muted">{shortDate(daily[hover].date)}</div>
                  {[
                    { k: t('ai.stats.heldForReview'), v: daily[hover].held, c: 'var(--aim-held)' },
                    { k: t('ai.draftStatus.autoSent'), v: daily[hover].auto_sent, c: 'var(--aim-auto)' },
                    { k: t('ai.draftStatus.rejected'), v: daily[hover].rejected, c: '' },
                  ].map((row) => (
                    <div key={row.k} className="flex items-center gap-2">
                      <span className="inline-block h-[2px] w-3" style={{ background: row.c || 'transparent' }} aria-hidden />
                      <span className="font-semibold tabular-nums text-text-heading">{f.number(row.v)}</span>
                      <span className="text-text-muted">{row.k}</span>
                    </div>
                  ))}
                </div>
              )}
              {/* x labels: first, middle, last */}
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
                      {shortDate(daily[i].date)}
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

function ReasonBars({
  rows,
  empty,
  label = (r) => r,
}: {
  rows: { reason: string; count: number }[]
  empty: string
  label?: (reason: string) => string
}) {
  const f = useFormat()
  if (!rows.length) return <p className="text-[12.5px] text-text-muted">{empty}</p>
  const max = Math.max(...rows.map((r) => r.count))
  return (
    <ul className="flex flex-col gap-2.5">
      {rows.map((r) => (
        <li key={r.reason}>
          <div className="mb-1 flex items-baseline justify-between gap-3 text-[12.5px]">
            <span className="min-w-0 break-words text-text-high">{label(r.reason)}</span>
            <span className="shrink-0 font-semibold tabular-nums text-text-heading">{f.number(r.count)}</span>
          </div>
          <div className="h-1.5 w-full rounded-full bg-fill">
            <div
              className="h-1.5 rounded-full"
              style={{ width: `${Math.max(3, (r.count / max) * 100)}%`, background: 'var(--aim-auto)' }}
            />
          </div>
        </li>
      ))}
    </ul>
  )
}

export function StatsPanel({ entityQuery }: { entityQuery: string }) {
  const t = useT()
  const f = useFormat()
  const pct = (v: number | null) => (v == null ? '—' : f.percent(v / 100))
  const [days, setDays] = useState(30)
  const [stats, setStats] = useState<MonitorStats | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setStats(await aiApi<MonitorStats>(`ai/monitor/stats?days=${days}${entityQuery ? `&${entityQuery}` : ''}`))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [days, entityQuery])

  useEffect(() => {
    void load()
  }, [load])

  const decided = useMemo(() => (stats ? stats.approved + stats.edited + stats.rejected : 0), [stats])

  return (
    <div className="aim-viz flex flex-col gap-4">
      <style>{VIZ_CSS}</style>
      {/* one filter row above everything it scopes */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="inline-flex rounded-lg border border-line-strong bg-card p-0.5" role="group" aria-label={t('ai.common.timeRange')}>
          {DAY_OPTIONS.map((d) => (
            <button
              key={d}
              type="button"
              aria-pressed={days === d}
              onClick={() => setDays(d)}
              className={`rounded-md px-3 py-1 text-[12.5px] font-semibold transition-colors ${
                days === d ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
              }`}
            >
              {t('ai.common.lastDays', { count: d })}
            </button>
          ))}
        </div>
        {loading && <Loader2 size={14} className="animate-spin text-text-faint" aria-label={t('common.status.loading')} />}
      </div>

      {error && (
        <div role="alert" className="text-[12.5px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}

      {stats && (
        <div className={`flex flex-col gap-4 transition-opacity ${loading ? 'opacity-70' : ''}`}>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
            <Tile
              label={t('ai.stats.aiMessages')}
              value={f.number(stats.total)}
              sub={
                Object.entries(stats.by_kind)
                  .filter(([k, n]) => n > 0 && k in KIND_COUNT)
                  .map(([k, n]) => t(KIND_COUNT[k as DraftKind], { count: n }))
                  .join(' · ') || t('ai.stats.noneYet')
              }
            />
            <Tile
              label={t('ai.stats.heldForReview')}
              value={f.number(stats.held)}
              sub={t('ai.stats.waitingNow', { count: stats.pending })}
            />
            <Tile
              label={t('ai.stats.approvalRate')}
              value={pct(stats.approval_rate)}
              sub={
                decided
                  ? t('ai.stats.decisionsOf', { approved: stats.approved + stats.edited, count: decided })
                  : t('ai.stats.noDecisions')
              }
            />
            <Tile
              label={t('ai.stats.editedBefore')}
              value={pct(stats.edit_rate)}
              sub={t('ai.stats.editedCount', { count: stats.edited })}
            />
            <Tile
              label={t('ai.draftStatus.rejected')}
              value={f.number(stats.rejected)}
              sub={t('ai.stats.supersededNotSent', { superseded: stats.expired, failed: stats.failed })}
            />
            <Tile
              label={t('ai.stats.medianReview')}
              value={duration(stats.median_review_seconds, t)}
              sub={
                stats.sent_after_deadline
                  ? t('ai.stats.sentAtDeadline', { count: stats.sent_after_deadline })
                  : t('ai.stats.draftToDecision')
              }
            />
          </div>

          <Card className="px-5 py-4">
            <CardTitle className="mb-3">{t('ai.stats.chartLabel')}</CardTitle>
            <DailyChart daily={stats.daily} />
          </Card>

          <div className="grid gap-4 lg:grid-cols-2">
            <Card className="px-5 py-4">
              <CardTitle className="mb-3">{t('ai.stats.whyHeld')}</CardTitle>
              <ReasonBars rows={stats.top_hold_reasons} empty={t('ai.stats.nothingHeld')} />
            </Card>
            <Card className="px-5 py-4">
              <CardTitle className="mb-3">{t('ai.stats.whyRejected')}</CardTitle>
              <ReasonBars
                rows={stats.top_reject_reasons}
                empty={t('ai.stats.noRejections')}
                label={(r) => rejectReasonLabel(r, t)}
              />
            </Card>
          </div>
          {stats.shadow > 0 && (
            <p className="text-[12px] text-text-muted">{t('ai.stats.shadowNote', { count: stats.shadow })}</p>
          )}
        </div>
      )}
      <LearningStats entityQuery={entityQuery} days={days} />
    </div>
  )
}
