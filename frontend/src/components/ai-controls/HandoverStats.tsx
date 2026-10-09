'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { ArrowUpRight, Loader2 } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import DailyBars from '@/components/flow/DailyBars'
import { subscribeChatStream } from '@/lib/chatStream'
import { aiControlsApi, type HandoverStats as Stats } from '@/lib/aiControls'

const DAYS = [7, 30, 90]
const BY_LABEL: Record<string, string> = {
  ai: 'The AI',
  rule: 'Automation rules',
  flow: 'Flows',
  agent: 'People',
  other: 'Other',
}

function Tile({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="rounded-xl border border-line bg-card px-4 py-3.5">
      <div className="text-[11.5px] font-medium text-text-muted">{label}</div>
      <div className="mt-1 text-[24px] font-semibold leading-none tracking-[-0.5px] text-text-heading">{value}</div>
      {sub && <div className="mt-1.5 text-[11px] text-text-faint">{sub}</div>}
    </div>
  )
}

const pct = (r: number | null) => (r == null ? '—' : `${Math.round(r * 1000) / 10}%`)
const mins = (m: number | null) =>
  m == null ? '—' : m < 60 ? `${Math.round(m)} min` : m < 60 * 24 ? `${Math.round(m / 6) / 10} h` : `${Math.round(m / 144) / 10} d`

/**
 * Human handovers on the AI monitor's Stats tab: how many conversations the
 * AI (or a rule, a flow, a person) handed to a human, the rate against the
 * conversations the AI worked on, how long people took to answer, per day and
 * by reason. GET /api/ai/handovers/stats.
 */
export function HandoverStats({ entityQuery }: { entityQuery: string }) {
  const [days, setDays] = useState(30)
  const [data, setData] = useState<Stats | null>(null)
  const [err, setErr] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await aiControlsApi<Stats>(`ai/handovers/stats?days=${days}${entityQuery ? `&${entityQuery}` : ''}`))
      setErr(null)
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    }
  }, [days, entityQuery])

  useEffect(() => {
    void load()
    let t: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(t)
      t = window.setTimeout(() => void load(), 800)
    })
    return () => {
      unsub()
      window.clearTimeout(t)
    }
  }, [load])

  const kindMax = Math.max(1, ...(data?.by_kind ?? []).map((k) => k.count))

  return (
    <Card className="px-4 py-4 sm:px-5">
      <CardTitle
        right={
          <div role="radiogroup" aria-label="Period" className="inline-flex rounded-lg border border-line-strong bg-card p-0.5">
            {DAYS.map((d) => (
              <button
                key={d}
                type="button"
                role="radio"
                aria-checked={days === d}
                onClick={() => setDays(d)}
                className={`rounded-md px-2.5 py-0.5 text-[12px] font-medium ${
                  days === d ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
                }`}
              >
                {d}d
              </button>
            ))}
          </div>
        }
      >
        Handed to a human
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">
        Conversations the AI — or a rule, a flow or a person — passed to a human, and how fast someone picked them up.
      </p>

      {err && (
        <div role="alert" className="mt-3 rounded-md bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {err}
        </div>
      )}
      {!data && !err && (
        <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading…
        </div>
      )}

      {data && (
        <div className="mt-4 flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Tile label="Handovers" value={String(data.total)} sub={`last ${data.days} days`} />
            <Tile label="Waiting for a person" value={String(data.open)} sub="right now" />
            <Tile
              label="Handover rate"
              value={pct(data.rate)}
              sub={`${data.handed_over_conversations} of ${data.conversations} conversations`}
            />
            <Tile label="Time to a human" value={mins(data.avg_minutes_to_human)} sub="average, answered ones" />
          </div>

          <DailyBars data={data.series.map((s) => ({ day: s.day, value: s.handovers }))} label="Handovers per day" />

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <div>
              <div className="eyebrow mb-2">Why</div>
              {data.by_kind.length === 0 ? (
                <div className="text-[12.5px] text-text-muted">No handovers in this period.</div>
              ) : (
                <ul className="flex flex-col gap-2">
                  {data.by_kind.map((k) => (
                    <li key={k.kind} className="text-[12.5px]">
                      <div className="flex items-baseline justify-between gap-2">
                        <span className="truncate text-text-high">{k.label}</span>
                        <span className="tabular-nums text-text-muted">{k.count}</span>
                      </div>
                      <div className="mt-1 h-1.5 rounded-full bg-fill">
                        <div className="h-1.5 rounded-full bg-brand" style={{ width: `${(k.count / kindMax) * 100}%` }} />
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </div>
            <div>
              <div className="eyebrow mb-2">Handed over by</div>
              {data.by_source.length === 0 ? (
                <div className="text-[12.5px] text-text-muted">—</div>
              ) : (
                <ul className="flex flex-col divide-y divide-line rounded-lg border border-line">
                  {data.by_source.map((s) => (
                    <li key={s.by} className="flex items-center justify-between px-3 py-2 text-[12.5px]">
                      <span className="text-text-high">{BY_LABEL[s.by] ?? s.by}</span>
                      <span className="tabular-nums text-text-muted">{s.count}</span>
                    </li>
                  ))}
                </ul>
              )}
              <Link
                href="/chats"
                onClick={() => {
                  try {
                    localStorage.setItem('chats-view', 'needs_human')
                  } catch {
                    /* ignore */
                  }
                }}
                className="mt-3 inline-flex items-center gap-1 text-[12.5px] font-semibold text-[color:var(--brand-ink)] hover:underline"
              >
                Open “Needs human” in Chats <ArrowUpRight size={13} aria-hidden />
              </Link>
            </div>
          </div>
        </div>
      )}
    </Card>
  )
}
