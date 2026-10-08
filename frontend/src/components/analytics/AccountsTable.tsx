'use client'

import { HEALTH_META, type HealthStatus } from '@/lib/channelHealth'
import type { AccountVolume } from './types'

const PROVIDER_LBL: Record<string, string> = {
  cloud: 'Cloud API',
  baileys: 'Baileys',
}

/** Per-account message volumes: name, provider, health dot (shared
 *  HEALTH_META mapping), outbound/inbound counts. Numeric columns use
 *  tabular figures so they align; the null-account bucket renders as
 *  "Unattributed". */
export function AccountsTable({ accounts }: { accounts: AccountVolume[] }) {
  if (accounts.length === 0) {
    return (
      <div className="py-8 text-center text-[12.5px] text-text-muted">
        No messages in this window yet — send something from the inbox to see per-account volume.
      </div>
    )
  }

  return (
    <table className="w-full text-[11.5px]">
      <thead>
        <tr className="border-b border-line text-left text-[10.5px] uppercase tracking-[0.05em] text-text-faint">
          <th className="py-2 pr-2 font-bold">Account</th>
          <th className="py-2 pr-2 font-bold">Health</th>
          <th className="py-2 pl-2 text-right font-bold">Out</th>
          <th className="py-2 pl-2 text-right font-bold">In</th>
        </tr>
      </thead>
      <tbody>
        {accounts.map((a) => {
          const meta = a.account_id === null ? null : HEALTH_META[(a.health_status as HealthStatus)] ?? HEALTH_META.unknown
          return (
            <tr key={a.account_id ?? 'unattributed'} className="border-b border-line last:border-0">
              <td className="max-w-0 py-2 pr-2" style={{ width: '46%' }}>
                <div
                  className={`truncate text-[12px] font-semibold ${a.account_id === null ? 'text-text-muted' : 'text-text-high'}`}
                >
                  {a.name}
                </div>
                <div className="truncate text-[10px] text-text-faint">
                  {a.account_id === null
                    ? 'no account attribution'
                    : [a.channel, a.provider ? PROVIDER_LBL[a.provider] ?? a.provider : null]
                        .filter(Boolean)
                        .join(' · ')}
                </div>
              </td>
              <td className="py-2 pr-2">
                {meta ? (
                  <span className="flex items-center gap-1.5">
                    <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: meta.dot }} />
                    <span className="text-[11px] font-semibold" style={{ color: meta.ink }}>
                      {meta.lbl}
                    </span>
                  </span>
                ) : (
                  <span className="text-[11px] text-text-faint">—</span>
                )}
              </td>
              <td className="py-2 pl-2 text-right font-bold tabular-nums text-text-high">
                {a.outbound.toLocaleString('en-US')}
              </td>
              <td className="py-2 pl-2 text-right tabular-nums text-text-mid">
                {a.inbound.toLocaleString('en-US')}
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}
