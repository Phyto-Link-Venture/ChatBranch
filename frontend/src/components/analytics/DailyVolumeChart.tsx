'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { intlTag, useFormat, useI18n, useT, type MessageKey } from '@/i18n'
import type { DailyPoint } from './types'

/**
 * Daily volume — inbound vs outbound messages per day as paired columns (the
 * Paper & Ink board: ink = inbound, the accent = outbound). One count axis,
 * so one chart. Inline SVG, no library.
 *
 * Marks follow the dataviz spec: columns capped at 12px with a 2px surface gap
 * between a day's pair, 3px rounded data-end and a square base on a single ink
 * baseline, hairline gridlines. Hover a day (the whole day band is the hit
 * target) for a tooltip with both values and the day's calls; the "Table"
 * toggle is the accessible twin carrying every value.
 *
 * Colours: ink vs tangerine separate by ΔE 44 (light) / 28 (dark) for normal
 * vision and ≥ 24 under CVD simulation (validate_palette.js); the legend
 * names both, so identity never rides on colour alone.
 */

const SERIES: readonly {
  key: 'inbound' | 'outbound'
  lbl: MessageKey
  tip: MessageKey
  color: string
}[] = [
  { key: 'inbound', lbl: 'analytics.daily.inbound', tip: 'analytics.daily.tipInbound', color: 'var(--text-primary)' },
  { key: 'outbound', lbl: 'analytics.daily.outbound', tip: 'analytics.daily.tipOutbound', color: 'rgb(var(--c-brand))' },
]

const PAD_L = 36
const PAD_R = 8
const TOP = 10
const PLOT_H = 170
const AXIS_H = 22

function niceCeil(n: number): number {
  if (n <= 0) return 4
  const pow = Math.pow(10, Math.floor(Math.log10(n)))
  for (const m of [1, 2, 2.5, 5, 10]) if (m * pow >= n) return m * pow
  return 10 * pow
}

export function DailyVolumeChart({ daily }: { daily: DailyPoint[] }) {
  const t = useT()
  const f = useFormat()
  const { locale } = useI18n()
  // "9 Oct" / "9 Okt" / "10月9日" — the axis wants day + month only
  const dayFmt = useMemo(() => new Intl.DateTimeFormat(intlTag(locale), { day: 'numeric', month: 'short' }), [locale])
  const dayLabel = (iso: string) => dayFmt.format(new Date(`${iso}T00:00:00`))
  const compact = (v: number) => f.number(v, { notation: 'compact', maximumFractionDigits: 1 })
  const wrapRef = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(640)
  const [view, setView] = useState<'chart' | 'table'>('chart')
  const [hover, setHover] = useState<number | null>(null)

  useEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const measure = () => setWidth(Math.max(260, el.clientWidth))
    measure()
    const ro = new ResizeObserver(measure)
    ro.observe(el)
    return () => ro.disconnect()
  }, [view])

  const n = daily.length
  const allZero = daily.every((d) => d.inbound === 0 && d.outbound === 0)

  const legend = (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
      {SERIES.map((s) => (
        <span key={s.key} className="flex items-center gap-1.5 text-[12px] text-text-mid">
          <span className="h-2.5 w-2.5" style={{ background: s.color }} aria-hidden />
          {t(s.lbl)}
        </span>
      ))}
      <div className="ml-auto flex overflow-hidden rounded-md border border-line" role="group" aria-label={t('analytics.daily.view')}>
        {(['chart', 'table'] as const).map((v) => (
          <button
            key={v}
            onClick={() => setView(v)}
            aria-pressed={view === v}
            className={`px-2.5 py-1 text-[11.5px] font-semibold transition-colors ${
              view === v ? 'bg-primary text-on-primary' : 'text-text-muted hover:bg-fill-hover'
            }`}
          >
            {v === 'chart' ? t('analytics.daily.chart') : t('analytics.daily.table')}
          </button>
        ))}
      </div>
    </div>
  )

  if (allZero) {
    return (
      <div className="flex flex-col gap-3">
        <div className="grid h-[200px] place-items-center rounded-lg bg-fill-subtle">
          <span className="text-[12.5px] text-text-muted">{t('analytics.daily.empty')}</span>
        </div>
        {legend}
      </div>
    )
  }

  if (view === 'table') {
    return (
      <div className="flex flex-col gap-3">
        <div className="max-h-[230px] overflow-y-auto rounded-lg border border-line">
          <table className="w-full text-[12px]">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b border-line text-left text-text-muted">
                <th className="px-3 py-2 font-semibold">{t('analytics.daily.date')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('analytics.daily.inbound')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('analytics.daily.outbound')}</th>
                <th className="px-3 py-2 text-right font-semibold">{t('analytics.daily.calls')}</th>
              </tr>
            </thead>
            <tbody>
              {[...daily].reverse().map((d) => (
                <tr key={d.date} className="border-b border-line last:border-0">
                  <td className="px-3 py-1.5 text-text-mid">{dayLabel(d.date)}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{f.number(d.inbound)}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{f.number(d.outbound)}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{f.number(d.calls)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {legend}
      </div>
    )
  }

  // ---- geometry ----------------------------------------------------------------
  const innerW = width - PAD_L - PAD_R
  const band = innerW / Math.max(1, n)
  const gap = 2
  const barW = Math.max(1, Math.min(12, (band * 0.72 - gap) / 2))
  const max = niceCeil(Math.max(...daily.map((d) => Math.max(d.inbound, d.outbound))))
  const y = (v: number) => TOP + PLOT_H - (v / max) * PLOT_H
  const base = TOP + PLOT_H
  const totalH = base + AXIS_H

  const col = (x: number, v: number) => {
    const h = base - y(v)
    if (h <= 0) return ''
    const r = Math.min(3, barW / 2, h)
    return `M${x},${base} V${base - h + r} Q${x},${base - h} ${x + r},${base - h} H${x + barW - r} Q${x + barW},${base - h} ${x + barW},${base - h + r} V${base} Z`
  }

  // Label every k-th day counting back from today, so the latest day always
  // has a label and the spacing never bunches up at the right edge.
  const tickEvery = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(innerW / 80))))
  const ticks: number[] = []
  for (let i = n - 1; i >= 0; i -= tickEvery) ticks.unshift(i)

  const onMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect()
    // rect is in visual px; scale back to the SVG's own units (text-size zoom).
    const px = ((e.clientX - rect.left) / rect.width) * width
    const i = Math.floor((px - PAD_L) / band)
    setHover(i >= 0 && i < n ? i : null)
  }

  const hovered = hover !== null ? daily[hover] : null
  const tipW = 150
  const tipLeft = hover !== null ? Math.max(0, Math.min(PAD_L + band * (hover + 1) + 6, width - tipW)) : 0

  return (
    <div className="flex flex-col gap-3">
      <div ref={wrapRef} className="relative">
        <svg
          width={width}
          height={totalH}
          role="img"
          aria-label={t('analytics.daily.aria')}
          onPointerMove={onMove}
          onPointerLeave={() => setHover(null)}
          className="block"
        >
          {[0.5, 1].map((f) => (
            <g key={f}>
              <line x1={PAD_L} x2={width - PAD_R} y1={y(max * f)} y2={y(max * f)} style={{ stroke: 'var(--line)', strokeWidth: 1 }} />
              <text x={PAD_L - 6} y={y(max * f) + 3.5} textAnchor="end" style={{ fill: 'var(--text-muted)', fontSize: 10 }}>
                {compact(max * f)}
              </text>
            </g>
          ))}
          <text x={PAD_L - 6} y={base + 3.5} textAnchor="end" style={{ fill: 'var(--text-muted)', fontSize: 10 }}>
            {f.number(0)}
          </text>

          {hover !== null && (
            <rect x={PAD_L + band * hover} y={TOP} width={band} height={PLOT_H} style={{ fill: 'var(--fill-hover)' }} />
          )}

          {daily.map((d, i) => {
            const cx = PAD_L + band * i + band / 2
            const x0 = cx - barW - gap / 2
            const x1 = cx + gap / 2
            return (
              <g key={d.date}>
                <path d={col(x0, d.inbound)} style={{ fill: SERIES[0].color }} />
                <path d={col(x1, d.outbound)} style={{ fill: SERIES[1].color }} />
              </g>
            )
          })}

          {/* the ink baseline everything grows from */}
          <line x1={PAD_L} x2={width - PAD_R} y1={base + 0.5} y2={base + 0.5} style={{ stroke: 'var(--text-primary)', strokeWidth: 1 }} />

          {ticks.map((i) => (
            <text
              key={i}
              x={PAD_L + band * i + band / 2}
              y={base + 15}
              textAnchor={i === n - 1 && n > 1 ? 'end' : band * (i + 0.5) < 30 ? 'start' : 'middle'}
              style={{ fill: 'var(--text-muted)', fontSize: 10 }}
            >
              {dayLabel(daily[i].date)}
            </text>
          ))}
        </svg>

        {hovered && (
          <div
            className="pointer-events-none absolute top-1 z-10 rounded-lg border border-line bg-card px-3 py-2 shadow-pop"
            style={{ left: tipLeft, width: tipW }}
          >
            <div className="mb-1 text-[11px] font-semibold text-text-muted">{dayLabel(hovered.date)}</div>
            {SERIES.map((s) => (
              <div key={s.key} className="flex items-center gap-1.5 py-px text-[12px] text-text-muted">
                <span className="h-2 w-2 shrink-0" style={{ background: s.color }} aria-hidden />
                <span className="tabular-nums">{t(s.tip, { count: hovered[s.key] })}</span>
              </div>
            ))}
            {hovered.calls > 0 && (
              <div className="pt-0.5 text-[12px] tabular-nums text-text-muted">
                {t('analytics.daily.tipCalls', { count: hovered.calls })}
              </div>
            )}
          </div>
        )}
      </div>
      {legend}
    </div>
  )
}
