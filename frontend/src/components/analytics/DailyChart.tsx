'use client'

import { useEffect, useRef, useState } from 'react'
import type { DailyPoint } from './types'

/**
 * Daily activity — inline SVG, no chart library. Message/call COUNTS and
 * record-event COUNTS are different series, so per the dataviz one-axis rule
 * they are small multiples sharing the x-axis (counts panel: 2px lines with
 * surface-ringed end markers; record-events panel: thin single-series
 * columns), never a dual-axis plot. Hover = nearest-X crosshair with a single
 * tooltip listing every series (values lead, line-key swatches, big hit
 * target = the whole plot). Tooltips never gate: the "Table" toggle is the
 * WCAG twin with every value. Chart chrome uses theme vars; series colors
 * are the validated --viz-* slots defined by the analytics page.
 */

const SERIES = [
  { key: 'outbound', lbl: 'Outbound', color: 'var(--viz-s1)' },
  { key: 'inbound', lbl: 'Inbound', color: 'var(--viz-s2)' },
  { key: 'calls', lbl: 'Calls', color: 'var(--viz-s3)' },
] as const

const PAD_L = 38
const PAD_R = 14
const TOP = 8
const COUNTS_H = 148
const GAP = 30
const PAY_H = 54
const AXIS_H = 20

function niceCeil(n: number): number {
  if (n <= 0) return 4
  const pow = Math.pow(10, Math.floor(Math.log10(n)))
  for (const m of [1, 2, 4, 5, 10]) if (m * pow >= n) return m * pow
  return 10 * pow
}

const compact = (v: number) =>
  v >= 1000 ? `${Math.round((v / 1000) * 10) / 10}k` : `${Math.round(v * 10) / 10}`

const dayLabel = (iso: string) =>
  new Date(`${iso}T00:00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })

export function DailyChart({ daily }: { daily: DailyPoint[] }) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(720)
  const [view, setView] = useState<'chart' | 'table'>('chart')
  const [hover, setHover] = useState<number | null>(null)

  useEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(Math.max(280, el.clientWidth)))
    setWidth(Math.max(280, el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  }, [view])

  const n = daily.length
  const allZero = daily.every(
    (d) => d.outbound === 0 && d.inbound === 0 && d.calls === 0 && d.record_events === 0,
  )

  const header = (
    <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-2">
      {/* legend — 3 series, line keys mirror the line marks */}
      <div className="flex flex-wrap items-center gap-x-3.5 gap-y-1">
        {SERIES.map((s) => (
          <span key={s.key} className="flex items-center gap-1.5 text-[11px] text-text-muted">
            <span className="h-[2px] w-[12px] rounded-full" style={{ background: s.color }} />
            {s.lbl}
          </span>
        ))}
        <span className="flex items-center gap-1.5 text-[11px] text-text-muted">
          <span className="h-[8px] w-[8px] rounded-[2px]" style={{ background: 'var(--viz-pay)' }} />
          Record events
        </span>
      </div>
      <div className="ml-auto flex overflow-hidden rounded-lg border border-line">
        {(['chart', 'table'] as const).map((v) => (
          <button
            key={v}
            onClick={() => setView(v)}
            className={`px-2.5 py-1 text-[10.5px] font-semibold capitalize transition-colors ${
              view === v ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill'
            }`}
          >
            {v}
          </button>
        ))}
      </div>
    </div>
  )

  if (allZero) {
    return (
      <div>
        {header}
        <div className="grid h-[200px] place-items-center rounded-lg bg-fill-subtle">
          <span className="text-[12px] text-text-faint">
            No messages, calls or record events in this window yet.
          </span>
        </div>
      </div>
    )
  }

  if (view === 'table') {
    return (
      <div>
        {header}
        <div className="max-h-[280px] overflow-y-auto rounded-lg border border-line">
          <table className="w-full text-[11.5px]">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b border-line text-left text-[10.5px] uppercase tracking-[0.05em] text-text-faint">
                <th className="px-3 py-2 font-bold">Date</th>
                <th className="px-3 py-2 text-right font-bold">Outbound</th>
                <th className="px-3 py-2 text-right font-bold">Inbound</th>
                <th className="px-3 py-2 text-right font-bold">Calls</th>
                <th className="px-3 py-2 text-right font-bold">Record events</th>
              </tr>
            </thead>
            <tbody>
              {[...daily].reverse().map((d) => (
                <tr key={d.date} className="border-b border-line last:border-0">
                  <td className="px-3 py-1.5 text-text-mid">{dayLabel(d.date)}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{d.outbound}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{d.inbound}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{d.calls}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">
                    {d.record_events.toLocaleString('en-US')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    )
  }

  // ---- geometry -------------------------------------------------------------------
  const innerW = width - PAD_L - PAD_R
  const x = (i: number) => PAD_L + (n <= 1 ? innerW / 2 : (i * innerW) / (n - 1))
  const maxC = niceCeil(Math.max(...daily.map((d) => Math.max(d.outbound, d.inbound, d.calls))))
  const yC = (v: number) => TOP + COUNTS_H - (v / maxC) * COUNTS_H
  const payTop = TOP + COUNTS_H + GAP
  const maxP = niceCeil(Math.max(...daily.map((d) => d.record_events)))
  const yP = (v: number) => payTop + PAY_H - (v / maxP) * PAY_H
  const totalH = payTop + PAY_H + AXIS_H
  const barW = Math.max(2, Math.min(14, innerW / n - 2))
  const last = n - 1

  const tickIdx: number[] = []
  const step = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(innerW / 88))))
  for (let i = 0; i < n; i += step) tickIdx.push(i)
  if (tickIdx[tickIdx.length - 1] !== last && n > 1) tickIdx.push(last)

  const barPath = (bx: number, by: number, bw: number, bh: number) => {
    const r = Math.min(4, bw / 2, bh)
    return `M${bx},${by + bh} V${by + r} Q${bx},${by} ${bx + r},${by} H${bx + bw - r} Q${bx + bw},${by} ${bx + bw},${by + r} V${by + bh} Z`
  }

  const onMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - rect.left
    const i = Math.round(((px - PAD_L) / Math.max(1, innerW)) * (n - 1))
    setHover(Math.max(0, Math.min(n - 1, i)))
  }

  const hovered = hover !== null ? daily[hover] : null
  const tipW = 168
  const tipLeft = hover !== null ? Math.max(4, Math.min(x(hover) + 12, width - tipW - 4)) : 0

  return (
    <div>
      {header}
      <div ref={wrapRef} className="relative">
        <svg
          width={width}
          height={totalH}
          role="img"
          aria-label="Daily outbound, inbound, call and record-event activity"
          onPointerMove={onMove}
          onPointerLeave={() => setHover(null)}
        >
          {/* counts panel grid — solid hairlines, one step off the surface */}
          {[0, 0.5, 1].map((f) => (
            <g key={f}>
              <line
                x1={PAD_L} x2={width - PAD_R}
                y1={yC(maxC * f)} y2={yC(maxC * f)}
                style={{ stroke: 'var(--line)', strokeWidth: 1 }}
              />
              <text
                x={PAD_L - 6} y={yC(maxC * f) + 3} textAnchor="end"
                style={{ fill: 'var(--text-faint)', fontSize: 9 }}
              >
                {compact(maxC * f)}
              </text>
            </g>
          ))}

          {/* count series — 2px lines, round joins */}
          {SERIES.map((s) => (
            <path
              key={s.key}
              d={daily.map((d, i) => `${i ? 'L' : 'M'}${x(i)},${yC(d[s.key])}`).join('')}
              fill="none"
              style={{ stroke: s.color, strokeWidth: 2, strokeLinejoin: 'round', strokeLinecap: 'round' }}
            />
          ))}
          {/* end markers with a 2px surface ring */}
          {SERIES.map((s) => (
            <circle
              key={`${s.key}-end`}
              cx={x(last)} cy={yC(daily[last][s.key])} r={4}
              style={{ fill: s.color, stroke: 'var(--card)', strokeWidth: 2 }}
            />
          ))}

          {/* record-events panel — single emerald series, no legend box needed */}
          <text
            x={PAD_L - 6} y={payTop + 3} textAnchor="end"
            style={{ fill: 'var(--text-faint)', fontSize: 9 }}
          >
            {compact(maxP)}
          </text>
          <line
            x1={PAD_L} x2={width - PAD_R} y1={payTop} y2={payTop}
            style={{ stroke: 'var(--line)', strokeWidth: 1 }}
          />
          <line
            x1={PAD_L} x2={width - PAD_R} y1={payTop + PAY_H} y2={payTop + PAY_H}
            style={{ stroke: 'var(--line)', strokeWidth: 1 }}
          />
          {daily.map(
            (d, i) =>
              d.record_events > 0 && (
                <path
                  key={d.date}
                  d={barPath(x(i) - barW / 2, yP(d.record_events), barW, payTop + PAY_H - yP(d.record_events))}
                  style={{ fill: 'var(--viz-pay)' }}
                />
              ),
          )}

          {/* x axis labels (shared by both panels) */}
          {tickIdx.map((i) => (
            <text
              key={i}
              x={x(i)} y={payTop + PAY_H + 14}
              textAnchor={i === 0 ? 'start' : i === last ? 'end' : 'middle'}
              style={{ fill: 'var(--text-faint)', fontSize: 9.5 }}
            >
              {dayLabel(daily[i].date)}
            </text>
          ))}

          {/* crosshair + hovered markers */}
          {hover !== null && (
            <g pointerEvents="none">
              <line
                x1={x(hover)} x2={x(hover)} y1={TOP} y2={payTop + PAY_H}
                style={{ stroke: 'var(--line-strong)', strokeWidth: 1 }}
              />
              {SERIES.map((s) => (
                <circle
                  key={`${s.key}-h`}
                  cx={x(hover)} cy={yC(daily[hover][s.key])} r={4}
                  style={{ fill: s.color, stroke: 'var(--card)', strokeWidth: 2 }}
                />
              ))}
            </g>
          )}
        </svg>

        {/* one tooltip, every series — values lead, labels follow */}
        {hovered && (
          <div
            className="pointer-events-none absolute top-2 z-10 rounded-lg border border-line bg-card px-3 py-2 shadow-lg"
            style={{ left: tipLeft, width: tipW }}
          >
            <div className="mb-1 text-[10px] font-semibold uppercase tracking-[0.05em] text-text-faint">
              {dayLabel(hovered.date)}
            </div>
            {SERIES.map((s) => (
              <div key={s.key} className="flex items-center gap-1.5 py-px text-[11px]">
                <span className="h-[2px] w-[10px] shrink-0 rounded-full" style={{ background: s.color }} />
                <span className="font-bold tabular-nums text-text-high">{hovered[s.key]}</span>
                <span className="text-text-muted">{s.lbl.toLowerCase()}</span>
              </div>
            ))}
            <div className="flex items-center gap-1.5 py-px text-[11px]">
              <span className="h-[8px] w-[8px] shrink-0 rounded-[2px]" style={{ background: 'var(--viz-pay)' }} />
              <span className="font-bold tabular-nums text-text-high">
                {hovered.record_events.toLocaleString('en-US')}
              </span>
              <span className="text-text-muted">record events</span>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
