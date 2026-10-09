'use client'

import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'
import { useFormat, useT } from '@/i18n'
import type { HealthEvent } from './types'

const asStatus = (s: string | undefined): HealthStatus | null => {
  const v = (s ?? '').trim()
  return v in HEALTH_META ? (v as HealthStatus) : null
}

/** Parse the transition out of the audit detail
 *  ("healthy → degraded — 429 too many requests" → healthy, degraded, reason). */
function parse(detail: string): { from: HealthStatus | null; to: HealthStatus | null; arrow: string; reason: string } {
  const [arrow, ...rest] = detail.split('—')
  const parts = arrow.includes('→') ? arrow.split('→') : []
  return {
    from: parts.length ? asStatus(parts[0]) : null,
    to: parts.length ? asStatus(parts[parts.length - 1]) : null,
    arrow: arrow.trim(),
    reason: rest.join('—').trim(),
  }
}

/** The account-health transition trail (AuditLog action="account.health"),
 *  newest first: dot + status word colored by the transition TARGET via the
 *  shared HEALTH_META inks (theme-flipping), relative timestamps. The status
 *  words are translated; the reason after the dash is the server's own text. */
export function HealthEvents({ events }: { events: HealthEvent[] }) {
  const t = useT()
  const f = useFormat()
  if (events.length === 0) {
    return <div className="py-8 text-center text-[12.5px] text-text-muted">{t('analytics.healthEvents.empty')}</div>
  }

  return (
    <div className="max-h-[300px] overflow-y-auto pr-1">
      {events.map((e, i) => {
        const p = parse(e.detail)
        const meta = p.to ? HEALTH_META[p.to] : HEALTH_META.unknown
        const transition =
          p.from && p.to ? t('analytics.healthEvents.transition', { from: t(HEALTH_META[p.from].key), to: t(meta.key) }) : p.arrow
        return (
          <div key={`${e.created_at}-${i}`} className="flex gap-2.5 border-b border-line py-2.5 last:border-0">
            <span className="mt-[5px] h-2 w-2 shrink-0 rounded-full" style={{ background: meta.dot }} />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline gap-2">
                <span className="min-w-0 truncate text-[12px] font-semibold text-text-high">
                  {e.target || t('analytics.healthEvents.account')}
                </span>
                <span className="ml-auto shrink-0 text-[10px] text-text-faint" title={f.dateTime(e.created_at)}>
                  {f.relative(e.created_at)}
                </span>
              </div>
              <div className="truncate text-[11px] text-text-muted">
                <span style={{ color: meta.ink }} className="font-semibold">
                  {transition}
                </span>
                {p.reason && <span> — {p.reason}</span>}
              </div>
            </div>
          </div>
        )
      })}
    </div>
  )
}
