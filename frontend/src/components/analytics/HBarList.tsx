'use client'

export interface HBarStage {
  label: string
  value: number
  /** secondary text at the right edge (e.g. "67% of sent", "RM 1,200") */
  note?: string
}

/**
 * Horizontal bar list — used for the message funnel and records-by-status
 * strips (ordinal ramp: one color per stage) and the call-outcome breakdown
 * (nominal: the same slot-1 hue for every bar). Per the dataviz mark spec:
 * thin bars (14px < 24px cap), 4px rounded data-end, square at the baseline,
 * every value directly labeled (so tooltips never gate a value).
 */
export function HBarList({
  stages,
  colors,
  empty,
  formatValue = (n) => n.toLocaleString('en-US'),
}: {
  stages: HBarStage[]
  /** CSS color per stage, aligned by index (e.g. 'var(--viz-f1)') */
  colors: string[]
  empty: string
  formatValue?: (n: number) => string
}) {
  const max = Math.max(1, ...stages.map((s) => s.value))
  const allZero = stages.every((s) => s.value === 0)

  if (allZero) {
    return <div className="py-8 text-center text-[12px] text-text-faint">{empty}</div>
  }

  return (
    <div className="flex flex-col gap-3">
      {stages.map((s, i) => (
        <div key={s.label} title={`${s.label}: ${formatValue(s.value)}`} className="group">
          <div className="mb-1 flex items-baseline justify-between gap-2">
            <span className="text-[11.5px] font-semibold text-text-mid">{s.label}</span>
            <span className="shrink-0 text-[11px] text-text-muted">
              <span className="font-bold text-text-high">{formatValue(s.value)}</span>
              {s.note && <span className="text-text-faint"> · {s.note}</span>}
            </span>
          </div>
          <div className="h-[14px]">
            <div
              className="h-full transition-[filter] group-hover:brightness-110"
              style={{
                width: `${(s.value / max) * 100}%`,
                minWidth: s.value > 0 ? 6 : 0,
                borderRadius: '0 4px 4px 0', // rounded data-end, square baseline
                background: colors[i] ?? colors[colors.length - 1],
              }}
            />
          </div>
        </div>
      ))}
    </div>
  )
}
