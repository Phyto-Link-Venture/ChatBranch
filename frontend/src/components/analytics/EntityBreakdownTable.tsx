'use client'

/**
 * The franchise view: one row per outlet, shown only when "All entities" is
 * selected. This is the whole-company picture an owner asks for — the
 * headline figures side by side so branches can be compared without switching
 * back and forth.
 *
 * The "Unassigned" row is deliberately kept rather than hidden: those are real
 * conversations that arrived on a shared number before anyone tagged them, and
 * dropping them would make the outlet rows silently fail to add up to the
 * totals above.
 */

import { useFormat, useT, type MessageKey } from '@/i18n'
import type { EntityBreakdown } from './types'

const HEAD: MessageKey[] = [
  'common.fields.entity',
  'analytics.entities.messages',
  'analytics.entities.sent',
  'analytics.entities.received',
  'analytics.entities.contacts',
  'analytics.entities.calls',
]

export function EntityBreakdownTable({ rows }: { rows: EntityBreakdown[] }) {
  const t = useT()
  const f = useFormat()
  const num = (n: number) => f.number(n)
  if (!rows.length) return null

  // Scale the inline bars against the busiest outlet, so the comparison is
  // visual rather than a wall of digits.
  const peak = Math.max(...rows.map((r) => r.outbound + r.inbound), 1)

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[520px] border-collapse">
        <thead>
          <tr className="border-b border-line text-left">
            {HEAD.map((h, i) => (
              <th
                key={h}
                className={`pb-2 text-[11px] font-semibold uppercase tracking-wide text-text-muted ${
                  i === 0 ? '' : 'text-right'
                }`}
              >
                {t(h)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const total = r.outbound + r.inbound
            return (
              <tr key={r.entity ?? '__unassigned__'} className="border-b border-line/60 last:border-0">
                <td className="py-2.5 pr-3">
                  <span
                    className={`text-[12.5px] font-semibold ${
                      r.entity ? 'text-text-high' : 'text-text-muted italic'
                    }`}
                  >
                    {r.name}
                  </span>
                  {!r.entity && (
                    <span className="ml-2 text-[10.5px] text-text-faint">{t('analytics.entities.untagged')}</span>
                  )}
                </td>
                <td className="py-2.5 pl-3">
                  <div className="flex items-center justify-end gap-2">
                    <div className="h-1.5 w-24 overflow-hidden rounded-full bg-fill">
                      <div
                        className="h-full rounded-full bg-[var(--viz-s2)]"
                        style={{ width: `${Math.round((total / peak) * 100)}%` }}
                      />
                    </div>
                    <span className="w-12 text-right text-[12px] tabular-nums text-text-high">
                      {num(total)}
                    </span>
                  </div>
                </td>
                <td className="py-2.5 pl-3 text-right text-[12px] tabular-nums text-text-muted">
                  {num(r.outbound)}
                </td>
                <td className="py-2.5 pl-3 text-right text-[12px] tabular-nums text-text-muted">
                  {num(r.inbound)}
                </td>
                <td className="py-2.5 pl-3 text-right text-[12px] tabular-nums text-text-muted">
                  {num(r.contacts)}
                </td>
                <td className="py-2.5 pl-3 text-right text-[12px] tabular-nums text-text-muted">
                  {num(r.calls)}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
