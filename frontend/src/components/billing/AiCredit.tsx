'use client'

/**
 * Prepaid AI credit — the balance each company holds, and the ledger behind it
 * (GET /api/billing/ai/credit with billing.view or billing.manage). A branch has
 * no balance of its own: it draws on its company's, so branches are listed under
 * the company that holds the money.
 *
 * Nothing debits these balances yet — AI usage is still billed by the monthly AI
 * bill above, and no balance can stop an AI reply. A billing manager grants
 * credit in Settings → AI billing.
 */

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { ArrowUpRight, Loader2, Wallet } from 'lucide-react'
import { Card, CardTitle } from '@/components/ui/Card'
import { ApiError } from '@/lib/aiMonitor'
import { type CreditPayload, creditDigits, creditKindKey, getCredit } from '@/lib/aiCredit'
import { useFormat, useT } from '@/i18n'

export function AiCredit() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<CreditPayload | null>(null)
  const [hidden, setHidden] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await getCredit('?page_size=10'))
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
                      {c.credit.exhausted && (
                        <span className="rounded-full bg-red-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--red-ink)]">
                          {t('billing.credit.empty')}
                        </span>
                      )}
                    </div>
                    <div className="text-[11.5px] text-text-muted">
                      {c.branches.length > 0
                        ? t('billing.credit.branches', { names: c.branches.map((b) => b.name).join(', ') })
                        : t('billing.credit.companyOnly')}
                    </div>
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

          <p className="mt-4 rounded-lg bg-fill-subtle px-3 py-2 text-[12px] text-text-mid">
            {t('billing.credit.phaseNote')}
          </p>
        </>
      )}
    </Card>
  )
}
