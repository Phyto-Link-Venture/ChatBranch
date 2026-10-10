'use client'

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { Card, CardTitle } from '@/components/ui/Card'
import { authHeaders } from '@/lib/auth'
import type { FlowsOverview } from '@/lib/flow/api'
import { Trans, useFormat, useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * "Flows today" — for every live flow, how many people entered it today and
 * how many of them already reached the end (a meter: the track is everyone who
 * entered, the ink fill those who finished). One line under each bar puts it
 * in the context of the page's period. Reads GET /api/analytics/flows, which
 * is bounded by the caller's contact scope and the entity switcher.
 */
export function FlowsCard({ days = 30, entityQ = '' }: { days?: number; entityQ?: string }) {
  const t = useT()
  const f = useFormat()
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

  const live = (today?.flows ?? []).filter((fl) => fl.active)
  const inPeriod = new Map((period?.flows ?? []).map((fl) => [fl.flowId, fl]))

  return (
    <Card className="flex min-w-0 flex-col gap-1 px-5 py-[18px]">
      <CardTitle
        className="mb-2"
        right={
          <Link href="/journeys" className="font-semibold text-[color:var(--brand-ink)] hover:underline">
            {t('analytics.flows.open')}
          </Link>
        }
      >
        {t('analytics.flows.title')}
        {live.length > 0 && (
          <span className="ml-2 font-sans text-[13px] font-semibold tabular-nums text-text-muted">
            {t('analytics.flows.live', { count: live.length })}
          </span>
        )}
      </CardTitle>

      {!today ? (
        <p className="py-3 text-[13px] text-text-muted">
          {failed ? t('analytics.flows.unavailable') : t('common.status.loading')}
        </p>
      ) : live.length === 0 ? (
        <p className="py-3 text-[13px] text-text-muted">
          <Trans
            k="analytics.flows.none"
            values={{
              link: (
                <Link href="/journeys" className="font-semibold text-text-high hover:underline">
                  {t('analytics.flows.startOne')}
                </Link>
              ),
            }}
          />
        </p>
      ) : (
        <ul className="flex flex-col gap-3.5">
          {live.slice(0, 6).map((fl) => {
            const share = fl.entered ? fl.reachedEnd / fl.entered : 0
            const p = inPeriod.get(fl.flowId)
            return (
              <li key={fl.flowId}>
                <Link
                  href={`/journeys?flow=${encodeURIComponent(fl.flowId)}`}
                  className="group block"
                  title={t('analytics.flows.tooltip', {
                    name: fl.name,
                    reached: f.number(fl.reachedEnd),
                    entered: f.number(fl.entered),
                  })}
                >
                  <div className="mb-1 flex items-baseline justify-between gap-2">
                    <span className="truncate text-[13px] font-semibold text-text-high group-hover:underline">{fl.name}</span>
                    <span className="shrink-0 text-[12px] text-text-muted">
                      <Trans
                        k="analytics.flows.reached"
                        values={{
                          reached: (
                            <span className="font-semibold tabular-nums text-text-high">{f.number(fl.reachedEnd)}</span>
                          ),
                          entered: <span className="tabular-nums">{f.number(fl.entered)}</span>,
                        }}
                      />
                    </span>
                  </div>
                  <div
                    className="h-[10px] overflow-hidden rounded-[3px] bg-fill-hover"
                    role="meter"
                    aria-valuemin={0}
                    aria-valuemax={fl.entered}
                    aria-valuenow={fl.reachedEnd}
                    aria-label={t('analytics.flows.meterAria', { name: fl.name })}
                  >
                    <div
                      className="h-full rounded-r-[3px]"
                      style={{ width: `${share * 100}%`, minWidth: fl.reachedEnd ? 4 : 0, background: 'var(--text-primary)' }}
                    />
                  </div>
                  <div className="mt-1 flex flex-wrap gap-x-3 text-[11.5px] text-text-muted">
                    <span className="tabular-nums">{t('analytics.flows.inProgress', { count: fl.inProgress })}</span>
                    {fl.failed > 0 && (
                      <span className="tabular-nums text-[color:var(--red-ink)]">
                        {t('analytics.flows.failedToday', { count: fl.failed })}
                      </span>
                    )}
                    {p && (
                      <span className="tabular-nums">
                        {t('analytics.flows.enteredIn', { count: days, n: f.number(p.entered) })}
                      </span>
                    )}
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
