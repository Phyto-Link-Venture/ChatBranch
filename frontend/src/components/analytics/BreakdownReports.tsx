'use client'

import { useCallback, useEffect, useState } from 'react'
import { Download } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { channelLabel } from '@/components/ui/ChannelLogo'
import { authHeaders } from '@/lib/auth'
import { useFormat, useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Breakdown reports: who handled what, on which team, under which label, over
 * which channel. The tiles above answer "how is the business doing"; this
 * answers "how is the TEAM doing", which is the question a supervisor asks.
 *
 * Each table downloads as CSV from the same window and scope that produced it —
 * the first thing anyone does with a breakdown is put it in a spreadsheet.
 */

interface AgentRow {
  name: string
  conversations: number
  resolved: number
  messagesSent: number
}
interface TeamRow {
  name: string
  conversations: number
  open: number
  resolved: number
  members: number
}
interface LabelRow {
  name: string
  conversations: number
}
interface ChannelRow {
  name: string
  conversations: number
  in: number
  out: number
}

interface ReportPayload {
  days: number
  agents: AgentRow[]
  teams: TeamRow[]
  labels: LabelRow[]
  channels: ChannelRow[]
}

export function BreakdownReports({ days, entityQ }: { days: number; entityQ: string }) {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<ReportPayload | null>(null)
  const [failed, setFailed] = useState(false)

  const query = `days=${days}${entityQ ? `&${entityQ}` : ''}`

  useEffect(() => {
    fetch(`${API}/analytics/reports?${query}`, { headers: authHeaders() })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
      .then((d) => {
        setData(d)
        setFailed(false)
      })
      .catch(() => {
        setData(null)
        setFailed(true)
      })
  }, [query])

  /**
   * CSV needs the bearer token, so it cannot be a plain link — fetch it as a
   * blob and hand the browser a temporary object URL.
   */
  const download = useCallback(
    async (report: string) => {
      try {
        const res = await fetch(`${API}/analytics/reports.csv?report=${report}&${query}`, {
          headers: authHeaders(),
        })
        if (!res.ok) return
        const blob = await res.blob()
        const url = URL.createObjectURL(blob)
        const a = document.createElement('a')
        a.href = url
        a.download = `chatbranch-${report}.csv`
        a.click()
        URL.revokeObjectURL(url)
      } catch {
        /* a failed export must not take the page down */
      }
    },
    [query],
  )

  if (failed) return null // the tiles above still stand on their own
  if (!data) return null

  const Section = ({
    title,
    report,
    head,
    rows,
    empty,
  }: {
    title: string
    report: string
    head: string[]
    rows: (string | number)[][]
    empty: string
  }) => (
    <Card className="min-w-0 px-5 py-[18px]">
      <CardTitle
        className="mb-3"
        right={
          <button
            onClick={() => download(report)}
            title={t('analytics.reports.downloadCsv', { title })}
            aria-label={t('analytics.reports.downloadCsv', { title })}
            className="inline-flex cursor-pointer items-center gap-1 font-semibold text-text-mid transition-colors hover:text-text-high"
          >
            <Download size={12} aria-hidden /> CSV
          </button>
        }
      >
        {title}
      </CardTitle>
      {rows.length === 0 ? (
        <p className="text-[12.5px] text-text-muted">{empty}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[340px] border-collapse">
            <thead>
              <tr>
                {head.map((h, i) => (
                  <th
                    key={h}
                    className={`border-b border-line pb-1.5 text-[11.5px] font-semibold text-text-muted ${
                      i === 0 ? 'text-left' : 'text-right'
                    }`}
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, ri) => (
                <tr key={ri}>
                  {r.map((cell, ci) => (
                    <td
                      key={ci}
                      className={`border-b border-line py-1.5 text-[12.5px] ${
                        ci === 0 ? 'text-left font-semibold text-text-high' : 'text-right tabular-nums text-text-mid'
                      }`}
                    >
                      {typeof cell === 'number' ? f.number(cell) : cell}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 border-b border-line pb-2">
        <h2 className="display text-[22px] leading-tight text-text-heading">{t('analytics.reports.title')}</h2>
        <span className="text-[12.5px] text-text-muted">{t('analytics.reports.subtitle', { count: data.days })}</span>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <Section
          title={t('analytics.reports.byAgent')}
          report="agents"
          head={[
            t('analytics.reports.agent'),
            t('analytics.reports.conversations'),
            t('analytics.reports.resolved'),
            t('analytics.reports.sent'),
          ]}
          rows={data.agents.map((a) => [a.name, a.conversations, a.resolved, a.messagesSent])}
          empty={t('analytics.reports.empty')}
        />
        <Section
          title={t('analytics.reports.byTeam')}
          report="teams"
          head={[
            t('analytics.reports.team'),
            t('analytics.reports.conversations'),
            t('analytics.reports.open'),
            t('analytics.reports.resolved'),
          ]}
          rows={data.teams.map((tm) => [tm.name, tm.conversations, tm.open, tm.resolved])}
          empty={t('analytics.reports.empty')}
        />
        <Section
          title={t('analytics.reports.byLabel')}
          report="labels"
          head={[t('analytics.reports.label'), t('analytics.reports.conversations')]}
          rows={data.labels.map((l) => [l.name, l.conversations])}
          empty={t('analytics.reports.emptyLabels')}
        />
        <Section
          title={t('analytics.reports.byChannel')}
          report="channels"
          head={[
            t('common.fields.channel'),
            t('analytics.reports.conversations'),
            t('analytics.reports.received'),
            t('analytics.reports.sent'),
          ]}
          rows={data.channels.map((c) => [channelLabel(c.name), c.conversations, c.in, c.out])}
          empty={t('analytics.reports.empty')}
        />
      </div>
    </div>
  )
}
