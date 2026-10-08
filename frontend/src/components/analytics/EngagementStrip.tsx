'use client'

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { authHeaders } from '@/lib/auth'
import { duration } from './KpiStrip'
import type { AnalyticsSummary } from './types'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface Engagement {
  threads_total: number
  awaiting_reply: number
  replied: number
  calls_total: number
  needs_human: number
  followups_set: number
}

/**
 * The engagement strip (moved from the old Dashboard): conversation volume and
 * follow-through as one ruled row of small figures under the KPI strip. The
 * conversation counters come from /api/messaging/engagement (all time, entity
 * scoped); contacted and the session averages ride on the summary's window.
 */
export function EngagementStrip({ entityQ, summary }: { entityQ: string; summary: AnalyticsSummary }) {
  const [e, setE] = useState<Engagement | null>(null)

  useEffect(() => {
    let cancelled = false
    fetch(`${API}/messaging/engagement${entityQ ? `?${entityQ}` : ''}`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => !cancelled && setE(d?.stats ?? null))
      .catch(() => {})
    return () => {
      cancelled = true
    }
  }, [entityQ])

  const s = summary.sessions
  const items: { lbl: string; val: string; href?: string }[] = [
    { lbl: 'Conversations', val: e ? e.threads_total.toLocaleString('en-US') : '—', href: '/chats' },
    { lbl: 'Customers replied', val: e ? e.replied.toLocaleString('en-US') : '—', href: '/chats' },
    { lbl: 'Awaiting customer', val: e ? e.awaiting_reply.toLocaleString('en-US') : '—', href: '/chats' },
    { lbl: `Contacted · ${summary.days}d`, val: (summary.engagement?.contacted ?? 0).toLocaleString('en-US'), href: '/contacts' },
    { lbl: 'Calls', val: e ? e.calls_total.toLocaleString('en-US') : '—', href: '/call-logs' },
    { lbl: 'Needs a human', val: e ? e.needs_human.toLocaleString('en-US') : '—', href: '/call-logs' },
    { lbl: 'Avg chat session', val: s && s.chat_sessions > 0 ? duration(s.avg_chat_session_seconds) : '—' },
    { lbl: 'Avg call length', val: s && s.calls_counted > 0 ? duration(s.avg_call_seconds) : '—' },
  ]

  return (
    <div className="overflow-hidden rounded-xl border border-line bg-card">
      <div className="flex items-baseline gap-3 border-b border-line px-5 py-2.5">
        <span className="eyebrow">Engagement</span>
        <span className="text-[12px] text-text-muted">Conversation volume &amp; follow-through</span>
      </div>
      {/* gap-px over a hairline ground = rules between cells, none doubled at the edge */}
      <div className="grid grid-cols-2 gap-px bg-line sm:grid-cols-4 xl:grid-cols-8">
        {items.map((it) => {
          const body = (
            <>
              <div className="display text-[22px] leading-none text-text-heading">{it.val}</div>
              <div className="mt-1 truncate text-[11.5px] text-text-muted">{it.lbl}</div>
            </>
          )
          const cls = 'block min-w-0 bg-card px-4 py-3'
          return it.href ? (
            <Link key={it.lbl} href={it.href} className={`${cls} transition-colors hover:bg-well`}>
              {body}
            </Link>
          ) : (
            <div key={it.lbl} className={cls}>
              {body}
            </div>
          )
        })}
      </div>
    </div>
  )
}
