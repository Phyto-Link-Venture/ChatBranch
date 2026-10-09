'use client'

import { TriangleAlert } from 'lucide-react'
import { useFormat, useT } from '@/i18n'
import { HBarList } from './HBarList'
import type { CallOutcomes } from './types'

/** Outcome codes are written by the voice agent (data) — shown tidied, not translated. */
const pretty = (outcome: string) => {
  const s = outcome.replace(/_/g, ' ')
  return s.charAt(0).toUpperCase() + s.slice(1)
}

/** Call-outcome breakdown. Outcomes are NOMINAL categories of one series, so
 *  every bar wears the same slot-1 hue (never a value ramp on nominal data —
 *  bar length already carries the count). needs_human is a status flag and
 *  ships with icon + label, never color alone. */
export function OutcomeBars({ callOutcomes }: { callOutcomes: CallOutcomes }) {
  const t = useT()
  const f = useFormat()
  const stages = callOutcomes.outcomes.map((o) => ({
    label: pretty(o.outcome),
    value: o.count,
    note: callOutcomes.total > 0 ? f.percent(o.count / callOutcomes.total) : undefined,
  }))

  return (
    <div>
      <HBarList stages={stages} colors={stages.map(() => 'var(--viz-s1)')} empty={t('analytics.outcomes.empty')} />
      {callOutcomes.needs_human > 0 && (
        <div className="mt-3.5 flex items-center gap-2 rounded-lg bg-fill px-3 py-2">
          <TriangleAlert size={13} className="shrink-0 text-amber-300" aria-hidden />
          <span className="text-[11.5px] font-semibold text-text-mid">
            {t('analytics.outcomes.flagged', { count: callOutcomes.needs_human })}
          </span>
        </div>
      )}
    </div>
  )
}
