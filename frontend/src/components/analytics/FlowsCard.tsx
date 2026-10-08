'use client'

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { Card, CardTitle } from '@/components/ui/Card'
import { authHeaders } from '@/lib/auth'
import type { FlowsOverview } from '@/lib/flow/api'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * "Flows today" — for every live flow, how many people entered it today and
 * how many of them already reached the end (a meter: the track is everyone who
 * entered, the ink fill those who finished). One line under each bar puts it
 * in the context of the page's period. Reads GET /api/analytics/flows, which
 * is bounded by the caller's contact scope and the entity switcher.
 */
export function FlowsCard({ days = 30, entityQ = '' }: { days?: number; entityQ?: string }) {
  const [today, setToday] = useState<FlowsOverview | null>(null)
  const [period, setPeriod] = useState<FlowsOverview | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let alive = true
    const get = (d: number) =>
      fetch(`${API}/analytics/flows?days=${d}${entityQ ? `&${entityQ}` : ''}`, { headers: authHeaders(), cache: 'no-store' })
        .then((r) => (r.ok ? (r.json() as Promise<FlowsOverview>) : null))
        .catch(() => null)
    Promise.all([get(1), get(days)]).then(([t, p]) => {
      if (!alive) return
      setToday(t)
      setPeriod(p)
      setFailed(!t)
    })
    return () => {
      alive = false
    }
  }, [days, entityQ])

  const live = (today?.flows ?? []).filter((f) => f.active)
  const inPeriod = new Map((period?.flows ?? []).map((f) => [f.flowId, f]))

  return (
    <Card className="flex min-w-0 flex-col gap-1 px-5 py-[18px]">
      <CardTitle
        className="mb-2"
        right={
          <Link href="/flow-builder" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
            Open flows
          </Link>
        }
      >
        Flows today
        {live.length > 0 && (
          <span className="ml-2 font-sans text-[13px] font-semibold tabular-nums text-text-muted">{live.length} live</span>
        )}
      </CardTitle>

      {!today ? (
        <p className="py-3 text-[13px] text-text-muted">{failed ? 'Flow numbers are unavailable right now.' : 'Loading…'}</p>
      ) : live.length === 0 ? (
        <p className="py-3 text-[13px] text-text-muted">
          No flow is live. Flows send follow-ups for you —{' '}
          <Link href="/flow-builder" className="font-semibold text-text-high hover:underline">
            start one from a template
          </Link>
          .
        </p>
      ) : (
        <ul className="flex flex-col gap-3.5">
          {live.slice(0, 6).map((f) => {
            const share = f.entered ? f.reachedEnd / f.entered : 0
            const p = inPeriod.get(f.flowId)
            return (
              <li key={f.flowId}>
                <Link href="/flow-builder" className="group block" title={`${f.name}: ${f.reachedEnd} of ${f.entered} reached the end today`}>
                  <div className="mb-1 flex items-baseline justify-between gap-2">
                    <span className="truncate text-[13px] font-semibold text-text-high group-hover:underline">{f.name}</span>
                    <span className="shrink-0 text-[12px] text-text-muted">
                      <span className="font-semibold tabular-nums text-text-high">{f.reachedEnd.toLocaleString()}</span>
                      {' of '}
                      <span className="tabular-nums">{f.entered.toLocaleString()}</span> reached the end
                    </span>
                  </div>
                  <div className="h-[10px] overflow-hidden rounded-[3px] bg-fill-hover" role="meter" aria-valuemin={0} aria-valuemax={f.entered} aria-valuenow={f.reachedEnd} aria-label={`${f.name} completion today`}>
                    <div
                      className="h-full rounded-r-[3px]"
                      style={{ width: `${share * 100}%`, minWidth: f.reachedEnd ? 4 : 0, background: 'var(--text-primary)' }}
                    />
                  </div>
                  <div className="mt-1 flex flex-wrap gap-x-3 text-[11.5px] text-text-muted">
                    <span className="tabular-nums">{f.inProgress.toLocaleString()} in progress</span>
                    {f.failed > 0 && <span className="tabular-nums text-[color:var(--red-ink)]">{f.failed} failed today</span>}
                    {p && <span className="tabular-nums">{p.entered.toLocaleString()} entered in {days} days</span>}
                  </div>
                </Link>
              </li>
            )
          })}
        </ul>
      )}
    </Card>
  )
}

export default FlowsCard
