'use client'

import { useEffect, useState, type ReactNode } from 'react'
import { Download } from 'lucide-react'
import { authHeaders, expireSessionOn401, hasCap } from '@/lib/auth'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { useRole } from '@/lib/useRole'
import { relTime } from '@/lib/channelHealth'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { KpiStrip } from '@/components/analytics/KpiStrip'
import { EngagementStrip } from '@/components/analytics/EngagementStrip'
import { DailyVolumeChart } from '@/components/analytics/DailyVolumeChart'
import { ChannelHealthCard } from '@/components/analytics/ChannelHealthCard'
import { WaitingCard } from '@/components/analytics/WaitingCard'
import { HBarList } from '@/components/analytics/HBarList'
import { AccountsTable } from '@/components/analytics/AccountsTable'
import { OutcomeBars } from '@/components/analytics/OutcomeBars'
import { HealthEvents } from '@/components/analytics/HealthEvents'
import { AiUsageCard } from '@/components/analytics/AiUsageCard'
import { BreakdownReports } from '@/components/analytics/BreakdownReports'
import { EntityBreakdownTable } from '@/components/analytics/EntityBreakdownTable'
import { FlowsCard } from '@/components/analytics/FlowsCard'
import type { AnalyticsSummary } from '@/components/analytics/types'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const PERIODS = [7, 30, 90]

/*
 * Chart inks (dataviz skill): the board pairs INK with the theme ACCENT. Every
 * bar list here is one series of one measure (stages, statuses, outcomes are
 * labelled with their values), so bars wear the ink and only the outcome a
 * funnel exists for — "Replied" — wears the accent. --viz-s2 is what the
 * per-entity table's inline bars read.
 */
const VIZ_VARS = `
.viz-analytics{
  --viz-ink: var(--text-primary);
  --viz-accent: rgb(var(--c-brand));
  --viz-s1: var(--text-primary);
  --viz-s2: var(--text-primary);
}`

const pct = (rate: number) => `${Math.round(rate * 1000) / 10}%`

/*
 * FLOWS CARD SLOT — the flows agent ships components/analytics/FlowsCard.tsx
 * ("Flows today": reached the last step / entered today, per live flow). Mount
 * it here as:
 *     const FLOWS_SLOT = (days, entityQ) => <FlowsCard days={days} entityQ={entityQ} />
 * It renders beside "Waiting for a reply"; while this returns null the waiting
 * card spans the full row.
 */
const FLOWS_SLOT = (days: number, entityQ: string): ReactNode => <FlowsCard days={days} entityQ={entityQ} />

function Panel({ title, sub, children, right }: { title: string; sub?: string; children: ReactNode; right?: ReactNode }) {
  return (
    <Card className="min-w-0 px-5 py-[18px]">
      <CardTitle right={right}>{title}</CardTitle>
      {sub && <div className="mt-1 text-[12px] text-text-muted">{sub}</div>}
      <div className="mt-4">{children}</div>
    </Card>
  )
}

/** Client-side CSV of what's on screen — the daily series plus the headline
 *  figures — so it needs no auth round trip. */
function exportCsv(data: AnalyticsSummary, scope: string) {
  const f = data.funnel
  const q = (v: string | number) => `"${String(v).replace(/"/g, '""')}"`
  const lines = [
    ['scope', scope],
    ['window_days', data.days],
    ['since', data.since],
    ['messages_sent', f.sent],
    ['delivered', f.delivered],
    ['read', f.read],
    ['replied_conversations', f.replied],
    ['reply_rate', f.reply_rate],
    ['first_response_median_seconds', data.first_response?.median_seconds ?? ''],
    [],
    ['date', 'inbound', 'outbound', 'calls', 'record_events'],
    ...data.daily.map((d) => [d.date, d.inbound, d.outbound, d.calls, d.record_events]),
  ]
  const csv = lines.map((r) => r.map(q).join(',')).join('\r\n')
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url
  a.download = `chatbranch-analytics-${data.days}d.csv`
  a.click()
  URL.revokeObjectURL(url)
}

/**
 * Analytics — the home page (Paper & Ink): masthead with the period control
 * and CSV export, the KPI strip, engagement, daily volume beside channel
 * health, flows + who's waiting, then the breakdowns, funnel, per-account
 * volume, call outcomes, health trail and (admins) AI usage.
 *
 * Every section follows the entity switcher — `entityQ` is in each fetch's
 * dependencies, so switching outlets re-fetches instead of showing the
 * previous outlet's figures.
 */
export default function AnalyticsPage() {
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const { role, ready } = useRole()
  const isAdmin = role === 'admin' || role === 'superadmin'
  const [canChannels, setCanChannels] = useState(false)
  useEffect(() => setCanChannels(hasCap('channels.view')), []) // after mount: reads the session
  const [days, setDays] = useState(30)
  const [data, setData] = useState<AnalyticsSummary | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState(false)

  useEffect(() => {
    let cancelled = false
    setRefreshing(true)
    fetch(`${API}/analytics/summary?days=${days}${entityQ && `&${entityQ}`}`, { headers: authHeaders() })
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
  }, [days, entityQ])

  const scope = activeEntity ? activeEntity.name : 'All entities'
  const f = data?.funnel
  const noSends = !f || f.sent === 0
  const flows = FLOWS_SLOT(days, entityQ)

  return (
    <div className="viz-analytics mx-auto flex w-full max-w-[1280px] flex-col gap-6">
      <style>{VIZ_VARS}</style>

      {/* Masthead */}
      <header className="flex flex-wrap items-end gap-x-4 gap-y-3 border-b border-text-primary/80 pb-4">
        <div className="min-w-0">
          <div className="eyebrow">Overview</div>
          <h1 className="display mt-1 text-[36px] leading-none text-text-heading sm:text-[44px]">Analytics</h1>
          <div className="mt-2 text-[12.5px] text-text-muted">
            {scope} · last {days} days
            {data?.generated_at && <> · updated {relTime(data.generated_at)}</>}
            {error && data && <span className="text-[color:var(--red-ink)]"> · refresh failed, showing previous data</span>}
          </div>
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <div role="group" aria-label="Period" className="flex overflow-hidden rounded-lg border border-line-strong">
            {PERIODS.map((d) => (
              <button
                key={d}
                onClick={() => setDays(d)}
                aria-pressed={days === d}
                className={`h-9 px-3.5 text-[13px] transition-colors ${
                  days === d ? 'bg-primary font-semibold text-on-primary' : 'text-text-mid hover:bg-fill-hover'
                }`}
              >
                {d}d
              </button>
            ))}
          </div>
          <Button variant="secondary" onClick={() => data && exportCsv(data, scope)} disabled={!data}>
            <Download size={14} aria-hidden /> Export CSV
          </Button>
        </div>
      </header>

      {error && !data ? (
        <Card className="grid h-[260px] place-items-center px-5">
          <div className="text-center">
            <div className="display text-[20px] text-text-heading">Analytics unavailable</div>
            <div className="mt-1 text-[13px] text-text-muted">Could not reach the API — is the backend running?</div>
          </div>
        </Card>
      ) : !data ? (
        <Card className="grid h-[260px] place-items-center">
          <span className="text-[13px] text-text-muted">Loading analytics…</span>
        </Card>
      ) : (
        <div className={`flex flex-col gap-6 transition-opacity ${refreshing ? 'opacity-60' : 'opacity-100'}`}>
          <KpiStrip data={data} />
          <EngagementStrip entityQ={entityQ} summary={data} />

          <div className="grid gap-5 lg:grid-cols-3">
            <Card className="min-w-0 px-5 py-[18px] lg:col-span-2">
              <div className="mb-4 flex flex-wrap items-baseline gap-x-3">
                <h2 className="display text-[19px] leading-tight text-text-heading">Daily volume</h2>
                <span className="text-[12px] text-text-muted">inbound · outbound messages per day</span>
              </div>
              <DailyVolumeChart daily={data.daily} />
            </Card>
            <ChannelHealthCard entityQ={entityQ} canCheck={ready && isAdmin} canManage={canChannels} />
          </div>

          <div className="grid gap-5 lg:grid-cols-2">
            {flows}
            <WaitingCard entityQ={entityQ} className={flows ? '' : 'lg:col-span-2'} />
          </div>

          {!!data.by_entity?.length && (
            <Panel title="By entity" sub="Every outlet side by side — pick one in the top bar to drill in">
              <EntityBreakdownTable rows={data.by_entity} />
            </Panel>
          )}

          <BreakdownReports days={days} entityQ={entityQ} />

          <div className="grid gap-5 lg:grid-cols-2">
            <Panel title="Message funnel" sub="Outbound messages: sent → delivered → read → replied">
              <HBarList
                stages={[
                  { label: 'Sent', value: f!.sent },
                  { label: 'Delivered', value: f!.delivered, note: noSends ? undefined : pct(f!.delivered_rate) },
                  { label: 'Read', value: f!.read, note: noSends ? undefined : pct(f!.read_rate) },
                  { label: 'Replied', value: f!.replied, note: noSends ? undefined : pct(f!.reply_rate) },
                ]}
                colors={['var(--viz-ink)', 'var(--viz-ink)', 'var(--viz-ink)', 'var(--viz-accent)']}
                empty="No outbound messages in this window yet — the funnel fills as you send."
              />
            </Panel>
            <Panel title="Records by status" sub="Orders, bookings, tickets… linked to contacts, by status">
              <HBarList
                stages={(data.records_by_status ?? []).map((r) => ({
                  label: r.status,
                  value: r.count,
                  note: r.amount > 0 ? r.amount.toLocaleString('en-US') : undefined,
                }))}
                colors={['var(--viz-ink)']}
                empty="No records linked to contacts in this window yet."
              />
            </Panel>
          </div>

          <div className="grid gap-5 lg:grid-cols-2 xl:grid-cols-3">
            <Panel title="Per-account volume" sub="Messages carried by each channel account">
              <AccountsTable accounts={data.accounts} />
            </Panel>
            <Panel
              title="Call outcomes"
              sub={`${data.call_outcomes.total.toLocaleString('en-US')} completed call${data.call_outcomes.total === 1 ? '' : 's'} in this window`}
            >
              <OutcomeBars callOutcomes={data.call_outcomes} />
            </Panel>
            <Panel title="Account health events" sub="Every status change the channel monitor recorded">
              <HealthEvents events={data.health_events} />
            </Panel>
          </div>

          {/* LLM metering is admin-only server-side; hide it rather than show
              an error to roles that can read analytics but not billing. */}
          {ready && isAdmin && <AiUsageCard days={days} />}
        </div>
      )}
    </div>
  )
}
