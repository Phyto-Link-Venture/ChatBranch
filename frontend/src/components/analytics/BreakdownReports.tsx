'use client'

import { useCallback, useEffect, useState } from 'react'
import { Download, Users } from 'lucide-react'
import { authHeaders } from '@/lib/auth'

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
    <div className="rounded-2xl border border-line bg-card p-4">
      <div className="mb-3 flex items-center gap-2">
        <span className="flex-1 text-[13px] font-bold text-text-high">{title}</span>
        <button
          onClick={() => download(report)}
          title={`Download ${title} as CSV`}
          aria-label={`Download ${title} as CSV`}
          className="flex cursor-pointer items-center gap-1.5 rounded-lg border border-line bg-fill px-2.5 py-1 text-[11px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          <Download size={12} aria-hidden /> CSV
        </button>
      </div>
      {rows.length === 0 ? (
        <p className="text-[11.5px] text-text-muted">{empty}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[380px] border-collapse">
            <thead>
              <tr>
                {head.map((h, i) => (
                  <th
                    key={h}
                    className={`border-b border-line pb-1.5 text-[10px] font-bold uppercase tracking-[0.06em] text-text-faint ${
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
                      className={`border-b border-line py-1.5 text-[12px] ${
                        ci === 0 ? 'text-left font-semibold text-text-high' : 'text-right text-text-mid'
                      }`}
                    >
                      {cell}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <Users size={14} className="text-text-muted" aria-hidden />
        <span className="text-[13px] font-bold text-text-high">Breakdowns</span>
        <span className="text-[11px] text-text-muted">
          conversations touched in the last {data.days} days
        </span>
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        <Section
          title="By agent"
          report="agents"
          head={['Agent', 'Conversations', 'Resolved', 'Sent']}
          rows={data.agents.map((a) => [a.name, a.conversations, a.resolved, a.messagesSent])}
          empty="No conversations in this window."
        />
        <Section
          title="By team"
          report="teams"
          head={['Team', 'Conversations', 'Open', 'Resolved']}
          rows={data.teams.map((t) => [t.name, t.conversations, t.open, t.resolved])}
          empty="No conversations in this window."
        />
        <Section
          title="By label"
          report="labels"
          head={['Label', 'Conversations']}
          rows={data.labels.map((l) => [l.name, l.conversations])}
          empty="Nothing labelled yet."
        />
        <Section
          title="By channel"
          report="channels"
          head={['Channel', 'Conversations', 'Received', 'Sent']}
          rows={data.channels.map((c) => [c.name, c.conversations, c.in, c.out])}
          empty="No conversations in this window."
        />
      </div>
    </div>
  )
}
