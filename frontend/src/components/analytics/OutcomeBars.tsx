'use client'

import { TriangleAlert } from 'lucide-react'
import { HBarList } from './HBarList'
import type { CallOutcomes } from './types'

const pretty = (outcome: string) => {
  const s = outcome.replace(/_/g, ' ')
  return s.charAt(0).toUpperCase() + s.slice(1)
}

/** Call-outcome breakdown. Outcomes are NOMINAL categories of one series, so
 *  every bar wears the same slot-1 hue (never a value ramp on nominal data —
 *  bar length already carries the count). needs_human is a status flag and
 *  ships with icon + label, never color alone. */
export function OutcomeBars({ callOutcomes }: { callOutcomes: CallOutcomes }) {
  const stages = callOutcomes.outcomes.map((o) => ({
    label: pretty(o.outcome),
    value: o.count,
    note:
      callOutcomes.total > 0 ? `${Math.round((o.count / callOutcomes.total) * 100)}%` : undefined,
  }))

  return (
    <div>
      <HBarList
        stages={stages}
        colors={stages.map(() => 'var(--viz-s1)')}
        empty="No calls completed in this window yet."
      />
      {callOutcomes.needs_human > 0 && (
        <div className="mt-3.5 flex items-center gap-2 rounded-lg bg-fill px-3 py-2">
          <TriangleAlert size={13} className="shrink-0 text-amber-300" aria-hidden />
          <span className="text-[11.5px] text-text-mid">
            <span className="font-bold text-text-high">{callOutcomes.needs_human}</span>{' '}
            call{callOutcomes.needs_human === 1 ? '' : 's'} flagged for human follow-up
          </span>
        </div>
      )}
    </div>
  )
}
