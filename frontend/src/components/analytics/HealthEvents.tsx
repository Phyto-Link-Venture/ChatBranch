'use client'

import { HEALTH_META, relTime, type HealthStatus } from '@/lib/channelHealth'
import type { HealthEvent } from './types'

/** Parse the transition target out of the audit detail
 *  ("healthy → degraded — 429 too many requests" → "degraded"). */
function transitionTarget(detail: string): HealthStatus | null {
  const arrow = detail.split('—')[0]
  if (!arrow.includes('→')) return null
  const to = arrow.split('→').pop()?.trim() ?? ''
  return to in HEALTH_META ? (to as HealthStatus) : null
}

/** The account-health transition trail (AuditLog action="account.health"),
 *  newest first: dot + status word colored by the transition TARGET via the
 *  shared HEALTH_META inks (theme-flipping), relative timestamps. */
export function HealthEvents({ events }: { events: HealthEvent[] }) {
  if (events.length === 0) {
    return (
      <div className="py-8 text-center text-[12.5px] text-text-muted">
        No health transitions in this window — accounts have been stable.
      </div>
    )
  }

  return (
    <div className="max-h-[300px] overflow-y-auto pr-1">
      {events.map((e, i) => {
        const to = transitionTarget(e.detail)
        const meta = to ? HEALTH_META[to] : HEALTH_META.unknown
        const [transition, ...reason] = e.detail.split('—')
        return (
          <div key={`${e.created_at}-${i}`} className="flex gap-2.5 border-b border-line py-2.5 last:border-0">
            <span className="mt-[5px] h-2 w-2 shrink-0 rounded-full" style={{ background: meta.dot }} />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline gap-2">
                <span className="min-w-0 truncate text-[12px] font-semibold text-text-high">
                  {e.target || 'account'}
                </span>
                <span className="ml-auto shrink-0 text-[10px] text-text-faint">
                  {relTime(e.created_at)}
                </span>
              </div>
              <div className="truncate text-[11px] text-text-muted">
                <span style={{ color: meta.ink }} className="font-semibold">
                  {transition.trim()}
                </span>
                {reason.length > 0 && <span> — {reason.join('—').trim()}</span>}
              </div>
            </div>
          </div>
        )
      })}
    </div>
  )
}
