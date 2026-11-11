'use client'

/**
 * Prepaid AI credit — the balance each company holds, and the ledger behind it
 * (GET /api/billing/ai/credit with billing.view or billing.manage). A branch has
 * no balance of its own: it draws on its company's, so branches are listed under
 * the company that holds the money.
 *
 * Phase 2: a company ON CREDIT (an enabled balance) has its AI usage debited
 * by the consumption sweep (GET /api/billing/ai/credit/consumption shows the
 * recent lines and 24 h / 7 d / 30 d totals), and once its balance reaches minus
 * its overdraft limit its AI is paused — chats are handed to a person — which is
 * shown here honestly as "AI paused: out of credit". A company that is not on
 * credit is billed monthly as before. A billing manager grants credit in
 * Settings → AI billing.
 */

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, ArrowUpRight, Loader2, PauseCircle, Wallet } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { ApiError } from '@/lib/aiMonitor'
import {
  type CreditConsumptionPayload,
  type CreditPayload,
  NON_CONSUMPTION_KINDS,
  creditDigits,
  creditKindKey,
  getCredit,
  getCreditConsumption,
} from '@/lib/aiCredit'
import { useFormat, useT } from '@/i18n'

export function AiCredit() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<CreditPayload | null>(null)
  const [usage, setUsage] = useState<CreditConsumptionPayload | null>(null)
  const [hidden, setHidden] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      // the ledger without the frequent consumption lines — those are summarised per company below
      const [credit, consumption] = await Promise.all([
        getCredit(`?page_size=10&kind=${NON_CONSUMPTION_KINDS.join(',')}`),
        getCreditConsumption('?limit=5').catch(() => null),
      ])
      setData(credit)
      setUsage(consumption)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) setHidden(true)
      else setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  if (hidden) return null

  const currency = data?.currency || 'USD'
  const money = (n: number) => f.currency(n, currency, creditDigits(n))
  const usageOf = (id: number) => usage?.companies.find((u) => u.id === id)
  const metering = usage?.metering

  return (
    <Card className="p-5 print:hidden" data-tour="billing-credit">
      <CardTitle
        right={
          data?.is_manager ? (
            <Link
              href="/settings/billing"
              className="inline-flex items-center gap-1 font-semibold text-[var(--brand-ink)] hover:underline"
            >
              {t('billing.credit.manage')} <ArrowUpRight size={12} aria-hidden />
            </Link>
          ) : undefined
        }
      >
        <span className="inline-flex items-center gap-2">
          <Wallet size={15} aria-hidden /> {t('billing.credit.title')}
        </span>
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.credit.help')}</p>

      {error && (
        <p role="alert" className="mt-3 text-[13px] text-[var(--red-ink)]">
          {error}
        </p>
      )}
      {!data && !error && (
        <div className="mt-3 flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {data && (
        <>
          {data.companies.length === 0 ? (
            <p className="mt-3 text-[13px] text-text-muted">{t('billing.credit.noCompanies')}</p>
          ) : (
            <ul className="mt-4">
              {data.companies.map((c) => (
                <li key={c.id} className="flex flex-wrap items-center gap-x-4 gap-y-1.5 border-b border-line py-2.5 last:border-b-0">
                  <div className="min-w-[160px] flex-1">
                    <div className="flex items-center gap-2">
                      <span className="truncate text-[13px] font-semibold text-text-high">{c.name}</span>
                      {c.credit.exhausted && c.credit.enabled ? (
                        <span className="inline-flex items-center gap-1 rounded-full bg-red-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--red-ink)]">
                          <PauseCircle size={11} aria-hidden /> {t('billing.credit.enforcement.paused')}
                        </span>
                      ) : c.credit.exhausted ? (
                        <span className="rounded-full bg-red-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--red-ink)]">
                          {t('billing.credit.empty')}
                        </span>
                      ) : usageOf(c.id)?.low ? (
                        <span className="rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--amber-ink)]">
                          {t('billing.credit.enforcement.low')}
                        </span>
                      ) : null}
                    </div>
                    <div className="text-[11.5px] text-text-muted">
                      {c.branches.length > 0
                        ? t('billing.credit.branches', { names: c.branches.map((b) => b.name).join(', ') })
                        : t('billing.credit.companyOnly')}
                    </div>
                    <div className="text-[11.5px] text-text-muted">
                      {!c.credit.opened
                        ? t('billing.credit.enforcement.notOnCredit')
                        : !c.credit.enabled
                          ? t('billing.credit.enforcement.off')
                          : [
                              usageOf(c.id)?.warn_at != null &&
                                t('billing.credit.enforcement.warnAt', { amount: money(usageOf(c.id)?.warn_at ?? 0) }),
                              c.credit.overdraft_limit > 0 &&
                                t('billing.credit.enforcement.overdraft', { amount: money(c.credit.overdraft_limit) }),
                            ]
                              .filter(Boolean)
                              .join(' · ')}
                    </div>
                    {c.credit.exhausted && c.credit.enabled && (
                      <p role="status" className="mt-1 text-[12px] text-[var(--red-ink)]">
                        {t('billing.credit.enforcement.pausedHelp')}
                      </p>
                    )}
                  </div>
                  <div className="w-full min-w-[160px] text-right sm:w-[200px]">
                    {c.credit.opened ? (
                      <>
                        <div
                          className={`text-[15px] font-semibold tabular-nums ${
                            c.credit.balance < 0 ? 'text-[var(--red-ink)]' : 'text-text-high'
                          }`}
                        >
                          {money(c.credit.balance)}
                        </div>
                        <div className="text-[11.5px] text-text-muted">
                          {t('billing.credit.updated', { when: f.relative(c.credit.updated_at ?? '') })}
                        </div>
                      </>
                    ) : (
                      <div className="text-[12px] text-text-faint">{t('billing.credit.none')}</div>
                    )}
                  </div>
                  {(() => {
                    const u = usageOf(c.id)
                    if (!u || !u.on_credit) return null
                    return (
                      <div className="w-full rounded-lg bg-fill-subtle px-3 py-2">
                        <div className="text-[12px] font-semibold text-text-high">{t('billing.credit.consumption.title')}</div>
                        <div className="text-[11.5px] tabular-nums text-text-muted">
                          {t('billing.credit.consumption.used', {
                            day: money(u.used_24h),
                            week: money(u.used_7d),
                            month: money(u.used_30d),
                          })}
                        </div>
                        {u.recent.length === 0 ? (
                          <div className="mt-1 text-[11.5px] text-text-faint">{t('billing.credit.consumption.none')}</div>
                        ) : (
                          <ul className="mt-1 flex flex-col gap-0.5" aria-label={t('billing.credit.consumption.recent')}>
                            {u.recent.map((l) => (
                              <li key={l.id} className="flex flex-wrap items-baseline gap-x-2 text-[11.5px] text-text-mid">
                                <span className="tabular-nums text-text-muted">{f.dateTime(l.created_at)}</span>
                                <span className="font-semibold tabular-nums text-[var(--red-ink)]">−{money(Math.abs(l.amount))}</span>
                                <span className="text-text-muted">{t('billing.credit.consumption.line', { note: l.note })}</span>
                                <span className="text-text-faint">· {t('billing.credit.form.balanceAfter', { balance: money(l.balance_after) })}</span>
                              </li>
                            ))}
                          </ul>
                        )}
                      </div>
                    )
                  })()}
                </li>
              ))}
            </ul>
          )}

          {data.users.length > 0 && (
            <>
              <div className="eyebrow mt-4">{t('billing.credit.users')}</div>
              <ul className="mt-1">
                {data.users.map((u) => (
                  <li key={u.user_name} className="flex items-center justify-between gap-4 border-b border-line py-2 last:border-b-0">
                    <div>
                      <div className="text-[13px] font-semibold text-text-high">{u.user_name}</div>
                      <div className="text-[11.5px] text-text-muted">{t('billing.credit.userScope')}</div>
                    </div>
                    <div className="text-[14px] font-semibold tabular-nums text-text-high">{money(u.balance)}</div>
                  </li>
                ))}
              </ul>
            </>
          )}

          {data.ledger.length > 0 && (
            <>
              <div className="eyebrow mt-4">{t('billing.credit.ledger')}</div>
              <ul className="mt-1 flex flex-col gap-1">
                {data.ledger.map((l) => (
                  <li key={l.id} className="flex flex-wrap items-baseline gap-x-2 text-[12.5px] text-text-mid">
                    <span className="tabular-nums text-text-muted">{f.dateTime(l.created_at)}</span>
                    <span className={`font-semibold tabular-nums ${l.amount < 0 ? 'text-[var(--red-ink)]' : 'text-[var(--green-ink)]'}`}>
                      {l.amount < 0 ? '−' : '+'}
                      {money(Math.abs(l.amount))}
                    </span>
                    <span className="text-text-high">{l.company || l.user_name}</span>
                    <span className="text-text-muted">{t(creditKindKey(l.kind))}</span>
                    {l.note && <span className="text-text-muted">· {l.note}</span>}
                    <span className="text-text-faint">· {l.actor}</span>
                  </li>
                ))}
              </ul>
            </>
          )}

          {metering && (metering.fail_open > 0 || metering.usage_write_failures > 0) && (
            <div role="alert" className="mt-4 flex flex-col gap-1 rounded-lg bg-red-400/10 px-3 py-2 text-[12px] text-[var(--red-ink)]">
              {metering.fail_open > 0 && (
                <span className="inline-flex items-start gap-1.5">
                  <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
                  {t('billing.credit.enforcement.failOpen', {
                    count: metering.fail_open,
                    when: f.relative(metering.last_fail_open_at ?? ''),
                  })}
                </span>
              )}
              {metering.usage_write_failures > 0 && (
                <span className="inline-flex items-start gap-1.5">
                  <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
                  {t('billing.credit.enforcement.writeFailures', { count: metering.usage_write_failures })}
                </span>
              )}
            </div>
          )}

          <p className="mt-4 rounded-lg bg-fill-subtle px-3 py-2 text-[12px] text-text-mid">
            {t('billing.credit.consumption.note')}
            {metering?.last_sweep_at && (
              <span className="block text-text-muted">
                {t('billing.credit.consumption.lastSweep', { when: f.relative(metering.last_sweep_at) })}
              </span>
            )}
          </p>
        </>
      )}
    </Card>
  )
}
