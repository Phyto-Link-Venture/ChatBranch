'use client'

import { API } from '@/lib/apiBase'
import Link from 'next/link'
import { useEffect, useState } from 'react'
import { authHeaders } from '@/lib/auth'
import { useFormat, useT } from '@/i18n'
import { useDuration } from './KpiStrip'
import type { AnalyticsSummary } from './types'

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
  const t = useT()
  const f = useFormat()
  const duration = useDuration()
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
  const items: { id: string; lbl: string; val: string; href?: string }[] = [
    { id: 'conv', lbl: t('analytics.engagement.conversations'), val: e ? f.number(e.threads_total) : '—', href: '/chats' },
    { id: 'replied', lbl: t('analytics.engagement.replied'), val: e ? f.number(e.replied) : '—', href: '/chats' },
    { id: 'awaiting', lbl: t('analytics.engagement.awaiting'), val: e ? f.number(e.awaiting_reply) : '—', href: '/chats' },
    {
      id: 'contacted',
      lbl: t('analytics.engagement.contacted', { period: t('analytics.page.periodShort', { count: summary.days }) }),
      val: f.number(summary.engagement?.contacted ?? 0),
      href: '/contacts',
    },
    { id: 'calls', lbl: t('analytics.engagement.calls'), val: e ? f.number(e.calls_total) : '—', href: '/call-logs' },
    { id: 'human', lbl: t('analytics.engagement.needsHuman'), val: e ? f.number(e.needs_human) : '—', href: '/call-logs' },
    {
      id: 'chatLen',
      lbl: t('analytics.engagement.avgChat'),
      val: s && s.chat_sessions > 0 ? duration(s.avg_chat_session_seconds) : '—',
    },
    { id: 'callLen', lbl: t('analytics.engagement.avgCall'), val: s && s.calls_counted > 0 ? duration(s.avg_call_seconds) : '—' },
  ]

  return (
    <div className="overflow-hidden rounded-xl border border-line bg-card">
      <div className="flex items-baseline gap-3 border-b border-line px-5 py-2.5">
        <span className="eyebrow">{t('analytics.engagement.title')}</span>
        <span className="truncate text-[12px] text-text-muted">{t('analytics.engagement.subtitle')}</span>
      </div>
      {/* gap-px over a hairline ground = rules between cells, none doubled at the edge */}
      <div className="grid grid-cols-2 gap-px bg-line sm:grid-cols-4 xl:grid-cols-8">
        {items.map((it) => {
          const body = (
            <>
              <div className="display text-[22px] leading-none text-text-heading">{it.val}</div>
              <div className="mt-1 truncate text-[11.5px] text-text-muted" title={it.lbl}>
                {it.lbl}
              </div>
            </>
          )
          const cls = 'block min-w-0 bg-card px-4 py-3'
          return it.href ? (
            <Link key={it.id} href={it.href} className={`${cls} transition-colors hover:bg-well`}>
              {body}
            </Link>
          ) : (
            <div key={it.id} className={cls}>
              {body}
            </div>
          )
        })}
      </div>
    </div>
  )
}
