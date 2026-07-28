'use client'

import { useEffect, useState, type ReactNode } from 'react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { fmt } from '@/lib/mocks'
import { StatTile } from '@/components/analytics/StatTile'
import { HBarList } from '@/components/analytics/HBarList'
import { DailyChart } from '@/components/analytics/DailyChart'
import { AccountsTable } from '@/components/analytics/AccountsTable'
import { OutcomeBars } from '@/components/analytics/OutcomeBars'
import { HealthEvents } from '@/components/analytics/HealthEvents'
import { AiUsageCard } from '@/components/analytics/AiUsageCard'
import { ReviewTabs } from '@/components/shell/ReviewTabs'
import type { AnalyticsSummary } from '@/components/analytics/types'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'
const DAY_OPTIONS = [7, 30, 90]

/*
 * Chart palette (dataviz skill, validated with scripts/validate_palette.js
 * against both card surfaces — dark #151517, light #ffffff):
 *   categorical trio  --viz-s1/s2/s3  (#3B82F6 / #059669 / #8B5CF6) — PASS
 *     both modes, worst adjacent CVD ΔE 80.1, all ≥3:1 contrast
 *   funnel ordinal ramp (emerald, --viz-f1..f4) and conversion ordinal ramp
 *     (blue, --viz-c1..c4) — each mode's steps validated with --ordinal
 *     (monotone L, ΔL ≥ 0.06, light-end ≥2:1); dark mode gets its own steps,
 *     not a flip. :root is dark in this app; :root.light overrides.
 */
const VIZ_VARS = `
.viz-analytics{
  --viz-s1:#3B82F6;--viz-s2:#059669;--viz-s3:#8B5CF6;
  --viz-pay:#10B981;
  --viz-f1:#A7F3D0;--viz-f2:#6EE7B7;--viz-f3:#34D399;--viz-f4:#10B981;
  --viz-c1:#93C5FD;--viz-c2:#60A5FA;--viz-c3:#3B82F6;--viz-c4:#2563EB;
}
:root.light .viz-analytics{
  --viz-pay:#059669;
  --viz-f1:#10B981;--viz-f2:#059669;--viz-f3:#047857;--viz-f4:#065F46;
  --viz-c1:#60A5FA;--viz-c2:#3B82F6;--viz-c3:#2563EB;--viz-c4:#1E40AF;
}
`

function Card({ title, sub, children }: { title: string; sub?: string; children: ReactNode }) {
  return (
    <div className="rounded-2xl border border-line bg-card px-5 py-[18px]">
      <div className={sub ? 'mb-0.5' : 'mb-3.5'}>
        <span className="text-[13px] font-bold text-text-high">{title}</span>
      </div>
      {sub && <div className="mb-4 text-[11px] text-text-muted">{sub}</div>}
      {children}
    </div>
  )
}

const pct = (rate: number) => `${Math.round(rate * 1000) / 10}%`

/** 95 -> "1m 35s", 3610 -> "1h 0m", 42 -> "42s" */
const dur = (secs: number) => {
  if (secs >= 3600) return `${Math.floor(secs / 3600)}h ${Math.round((secs % 3600) / 60)}m`
  if (secs >= 60) return `${Math.floor(secs / 60)}m ${Math.round(secs % 60)}s`
  return `${Math.round(secs)}s`
}

export default function AnalyticsPage() {
  const [days, setDays] = useState(30)
  const [data, setData] = useState<AnalyticsSummary | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState(false)

  useEffect(() => {
    let cancelled = false
    setRefreshing(true)
    fetch(`${API}/analytics/summary?days=${days}`, { headers: authHeaders() })
      .then((r) => {
        if (expireSessionOn401(r)) return null
        if (!r.ok) throw new Error(`HTTP ${r.status}`)
        return r.json()
      })
      .then((d) => {
        if (cancelled || !d) return
        setData(d as AnalyticsSummary)
        setError(false)
      })
      .catch(() => !cancelled && setError(true))
      .finally(() => !cancelled && setRefreshing(false))
    return () => {
      cancelled = true
    }
  }, [days])

  const f = data?.funnel
  const e = data?.engagement
  const noSends = !f || f.sent === 0

  return (
    <div className="viz-analytics flex flex-col gap-4">
      <ReviewTabs />
      <style>{VIZ_VARS}</style>

      {/* Filter row — date range scopes everything below it */}
      <div className="flex items-center gap-2">
        <div className="flex overflow-hidden rounded-lg border border-line bg-fill-subtle">
          {DAY_OPTIONS.map((d) => (
            <button
              key={d}
              onClick={() => setDays(d)}
              className={`px-3 py-1.5 text-[11.5px] font-semibold transition-colors ${
                days === d ? 'bg-fill-hover text-text-high' : 'text-text-muted hover:bg-fill'
              }`}
            >
              Last {d} days
            </button>
          ))}
        </div>
        {error && data && (
          <span className="text-[11px] text-red-300">Refresh failed — showing previous data.</span>
        )}
      </div>

      {error && !data ? (
        <div className="grid h-[300px] place-items-center rounded-2xl border border-line bg-card">
          <div className="text-center">
            <div className="mb-1 text-[13px] font-bold text-text-high">Analytics unavailable</div>
            <div className="text-[11.5px] text-text-muted">
              Could not reach the API — is the backend running?
            </div>
          </div>
        </div>
      ) : !data ? (
        <div className="grid h-[300px] place-items-center rounded-2xl border border-line bg-card">
          <span className="text-[12px] text-text-faint">Loading analytics…</span>
        </div>
      ) : (
        <div
          className={`flex flex-col gap-4 transition-opacity ${refreshing ? 'opacity-60' : 'opacity-100'}`}
        >
          {/* KPI stat row */}
          <div className="grid grid-cols-2 gap-3.5 md:grid-cols-4 xl:grid-cols-4">
            <StatTile
              label="Messages sent"
              value={f!.sent.toLocaleString('en-US')}
              sub={`last ${data.days} days`}
            />
            <StatTile
              label="Delivered"
              value={noSends ? '—' : pct(f!.delivered_rate)}
              sub={noSends ? 'no sends yet' : `${f!.delivered.toLocaleString('en-US')} of ${f!.sent.toLocaleString('en-US')}`}
            />
            <StatTile
              label="Read"
              value={noSends ? '—' : pct(f!.read_rate)}
              sub={noSends ? 'no sends yet' : `${f!.read.toLocaleString('en-US')} messages`}
            />
            <StatTile
              label="Replied"
              value={noSends ? '—' : pct(f!.reply_rate)}
              sub={noSends ? 'no sends yet' : `${f!.replied.toLocaleString('en-US')} threads answered`}
            />
            <StatTile
              label="Contacted"
              value={(e?.contacted ?? 0).toLocaleString('en-US')}
              sub={`last ${data.days} days`}
            />
            <StatTile
              label="Resolved"
              value={(e?.resolved ?? 0).toLocaleString('en-US')}
              sub="contacts marked resolved"
            />
            <StatTile
              label="Avg call length"
              value={data.sessions && data.sessions.calls_counted > 0 ? dur(data.sessions.avg_call_seconds) : '—'}
              sub={
                data.sessions && data.sessions.calls_counted > 0
                  ? `${data.sessions.calls_counted.toLocaleString('en-US')} completed call${data.sessions.calls_counted === 1 ? '' : 's'}`
                  : 'no completed calls yet'
              }
            />
            <StatTile
              label="Avg chat session"
              value={data.sessions && data.sessions.chat_sessions > 0 ? dur(data.sessions.avg_chat_session_seconds) : '—'}
              sub={
                data.sessions && data.sessions.chat_sessions > 0
                  ? `~${data.sessions.avg_chat_messages} msgs · ${data.sessions.chat_sessions.toLocaleString('en-US')} sessions`
                  : 'no chat activity yet'
              }
            />
          </div>

          {/* Funnel + records-by-status strips */}
          <div className="grid gap-3.5 lg:grid-cols-2">
            <Card title="Message funnel" sub="Outbound messages: sent → delivered → read → replied">
              <HBarList
                stages={[
                  { label: 'Sent', value: f!.sent },
                  { label: 'Delivered', value: f!.delivered, note: noSends ? undefined : pct(f!.delivered_rate) },
                  { label: 'Read', value: f!.read, note: noSends ? undefined : pct(f!.read_rate) },
                  { label: 'Replied', value: f!.replied, note: noSends ? undefined : pct(f!.reply_rate) },
                ]}
                colors={['var(--viz-f1)', 'var(--viz-f2)', 'var(--viz-f3)', 'var(--viz-f4)']}
                empty="No outbound messages in this window yet — the funnel fills as campaigns send."
              />
            </Card>

            <Card title="Records by status" sub="Linked orders/shipments/invoices/bookings/tickets, grouped by status">
              <HBarList
                stages={(data?.records_by_status ?? []).map((r) => ({
                  label: r.status,
                  value: r.count,
                  note: r.amount > 0 ? fmt(r.amount) : undefined,
                }))}
                colors={['var(--viz-c1)', 'var(--viz-c2)', 'var(--viz-c3)', 'var(--viz-c4)']}
                empty="No records linked to contacts in this window yet."
              />
            </Card>
          </div>

          {/* Daily activity — small multiples sharing one x-axis */}
          <Card title="Daily activity" sub="Messages and calls per day, record events in their own panel">
            <DailyChart daily={data.daily} />
          </Card>

          {/* LLM metering — what the AI consumed and what it costs */}
          <AiUsageCard days={days} />

          {/* Accounts · call outcomes · health trail */}
          <div className="grid gap-3.5 lg:grid-cols-2 xl:grid-cols-3">
            <Card title="Per-account volume" sub="Messages carried by each channel account">
              <AccountsTable accounts={data.accounts} />
            </Card>
            <Card
              title="Call outcomes"
              sub={`${data.call_outcomes.total.toLocaleString('en-US')} completed call${data.call_outcomes.total === 1 ? '' : 's'} in this window`}
            >
              <OutcomeBars callOutcomes={data.call_outcomes} />
            </Card>
            <Card title="Account health events" sub="Health transitions from the channel monitor">
              <HealthEvents events={data.health_events} />
            </Card>
          </div>
        </div>
      )}
    </div>
  )
}
