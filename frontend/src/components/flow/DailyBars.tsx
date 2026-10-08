'use client'

import { useEffect, useRef, useState } from 'react'

/**
 * One series of daily counts as thin columns (inline SVG, no chart library):
 * ≤24px columns with a 4px rounded data-end and a square baseline, a hairline
 * grid at the nice maximum, direct labels on the first/last/peak day only, a
 * per-column hover tooltip, and a Table toggle as the accessible twin.
 */
export interface DailyValue {
  day: string
  value: number
}

const H = 120
const PAD_L = 28
const PAD_B = 18
const TOP = 10

function niceCeil(n: number): number {
  if (n <= 4) return 4
  const pow = Math.pow(10, Math.floor(Math.log10(n)))
  for (const m of [1, 2, 2.5, 5, 10]) if (m * pow >= n) return m * pow
  return 10 * pow
}

export const dayLabel = (iso: string) =>
  new Date(`${iso}T00:00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })

export default function DailyBars({ data, label, color = 'rgb(var(--c-brand))' }: { data: DailyValue[]; label: string; color?: string }) {
  const wrap = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(360)
  const [view, setView] = useState<'chart' | 'table'>('chart')
  const [hover, setHover] = useState<number | null>(null)

  useEffect(() => {
    const el = wrap.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(Math.max(220, el.clientWidth)))
    setWidth(Math.max(220, el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  }, [view])

  const max = niceCeil(Math.max(0, ...data.map((d) => d.value)))
  const n = Math.max(1, data.length)
  const plotW = width - PAD_L - 4
  const band = plotW / n
  const barW = Math.max(2, Math.min(24, band - 2))
  const y = (v: number) => TOP + (H - TOP - PAD_B) * (1 - v / max)
  const base = H - PAD_B
  const peak = data.reduce((best, d, i) => (d.value > (data[best]?.value ?? -1) ? i : best), 0)
  const labelled = new Set([0, data.length - 1, peak])

  return (
    <div>
      <div className="mb-2 flex items-center justify-between gap-2">
        <span className="text-[11.5px] text-text-muted">{label}</span>
        <div className="flex overflow-hidden rounded-md border border-line">
          {(['chart', 'table'] as const).map((v) => (
            <button
              key={v}
              type="button"
              onClick={() => setView(v)}
              className={`px-2 py-0.5 text-[10.5px] font-semibold capitalize ${view === v ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill'}`}
            >
              {v}
            </button>
          ))}
        </div>
      </div>
      {view === 'table' ? (
        <div className="max-h-[200px] overflow-y-auto rounded-lg border border-line">
          <table className="w-full text-[12px]">
            <tbody>
              {data.map((d) => (
                <tr key={d.day} className="border-b border-line last:border-0">
                  <td className="px-3 py-1.5 text-text-mid">{dayLabel(d.day)}</td>
                  <td className="px-3 py-1.5 text-right tabular-nums text-text-high">{d.value.toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div ref={wrap} className="relative" onMouseLeave={() => setHover(null)}>
          <svg width={width} height={H} role="img" aria-label={`${label}, by day`} className="block">
            <line x1={PAD_L} x2={width} y1={y(max)} y2={y(max)} stroke="var(--line)" strokeWidth={1} />
            <line x1={PAD_L} x2={width} y1={base} y2={base} stroke="var(--line-strong)" strokeWidth={1} />
            <text x={PAD_L - 6} y={y(max) + 3} textAnchor="end" fontSize={10} fill="var(--text-muted)">
              {max.toLocaleString()}
            </text>
            <text x={PAD_L - 6} y={base + 3} textAnchor="end" fontSize={10} fill="var(--text-muted)">
              0
            </text>
            {data.map((d, i) => {
              const cx = PAD_L + band * i + band / 2
              const top = y(d.value)
              const h = Math.max(0, base - top)
              const r = Math.min(4, h / 2, barW / 2)
              const x = cx - barW / 2
              const path =
                h <= 0
                  ? ''
                  : `M${x},${base} L${x},${top + r} Q${x},${top} ${x + r},${top} L${x + barW - r},${top} Q${x + barW},${top} ${x + barW},${top + r} L${x + barW},${base} Z`
              return (
                <g key={d.day} onMouseEnter={() => setHover(i)}>
                  <rect x={PAD_L + band * i} y={TOP} width={band} height={H - TOP} fill="transparent" />
                  {path && <path d={path} fill={color} opacity={hover === null || hover === i ? 1 : 0.55} />}
                  {labelled.has(i) && n > 1 && (
                    <text x={cx} y={H - 4} textAnchor="middle" fontSize={10} fill="var(--text-muted)">
                      {dayLabel(d.day)}
                    </text>
                  )}
                </g>
              )
            })}
          </svg>
          {hover !== null && data[hover] && (
            <div
              className="pointer-events-none absolute -top-1 z-10 -translate-x-1/2 -translate-y-full whitespace-nowrap rounded-md border border-line-strong bg-card px-2 py-1 text-[11px] shadow-pop"
              style={{ left: Math.min(width - 50, Math.max(50, PAD_L + band * hover + band / 2)) }}
            >
              <span className="font-semibold text-text-high">{data[hover].value.toLocaleString()}</span>
              <span className="text-text-muted"> · {dayLabel(data[hover].day)}</span>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
