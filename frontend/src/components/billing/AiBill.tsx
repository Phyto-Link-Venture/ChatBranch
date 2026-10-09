'use client'

/**
 * The AI usage bill — one calendar month, per COMPANY (a top-level entity; its
 * branches roll up into it). GET /api/billing/ai (billing.view).
 *
 * Everyone with billing.view sees billed amounts: usage after markup, the
 * monthly fee, the total, line items by feature. Billing managers
 * (billing.manage, superadmin by default) also see the cost, the markup, the
 * margin and the usage no company is billed for. Print = the browser's print,
 * styled by the `print:` variants (the shell hides itself when printing).
 */

import Link from 'next/link'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight, Loader2, Printer, Settings2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { aiApi } from '@/lib/aiMonitor'
import { useFormat, useT, type MessageKey } from '@/i18n'

interface CompanySummary {
  id: number
  name: string
  slug: string
  requests: number
  tokens: number
  usage_amount: number
  monthly_fee: number
  total: number
  cost_usd: number | null
  markup_pct: number | null
  margin: number | null
}

interface Bill {
  month: string
  from: string
  to: string
  currency: string
  is_manager: boolean
  companies: CompanySummary[]
  company: {
    summary: CompanySummary
    lines: { feature: string; requests: number; tokens: number; amount: number; cost_usd: number | null }[]
    daily: { date: string; amount: number }[]
  } | null
  unattributed: { requests: number; tokens: number; cost_usd: number } | null
  usd_rate: number | null
  pricing_note: string | null
}

export const FEATURE_LABELS: Record<string, MessageKey> = {
  chat_reply: 'billing.features.chatReply',
  follow_up: 'billing.features.followUp',
  flow_message: 'billing.features.flowMessage',
  flow_decision: 'billing.features.flowDecision',
  voice_call: 'billing.features.voiceCall',
  summary: 'billing.features.summary',
  timeline: 'billing.features.timeline',
  playground: 'billing.features.playground',
  assistant: 'billing.features.assistant',
  embedding: 'billing.features.embedding',
  skill_draft: 'billing.features.skillDraft',
}

const thisMonth = () => new Date().toISOString().slice(0, 7)
const shiftMonth = (m: string, by: number) => {
  const [y, mo] = m.split('-').map(Number)
  const d = new Date(Date.UTC(y, mo - 1 + by, 1))
  return d.toISOString().slice(0, 7)
}

export function AiBill() {
  const t = useT()
  const f = useFormat()
  const [month, setMonth] = useState(thisMonth())
  const [company, setCompany] = useState('')
  const [bill, setBill] = useState<Bill | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const q = new URLSearchParams({ month })
      if (company) q.set('company', company)
      const b = await aiApi<Bill>(`billing/ai?${q}`)
      setBill(b)
      // a company admin lands straight on their own bill
      if (!company && b.company) setCompany(b.company.summary.slug)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [month, company])

  useEffect(() => {
    void load()
  }, [load])

  const money = (n: number | null | undefined) => f.currency(n ?? 0, bill?.currency || 'USD')
  const usd = (n: number | null | undefined) => f.currency(n ?? 0, 'USD')
  const featureLabel = (k: string) => (FEATURE_LABELS[k] ? t(FEATURE_LABELS[k]) : k)
  const monthLabel = useMemo(
    () => new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${month}-01T00:00:00Z`)),
    [month],
  )
  const manager = !!bill?.is_manager
  const companyOptions = useMemo(
    () => [
      ...(manager || (bill?.companies.length ?? 0) > 1 ? [{ value: '', label: t('billing.allCompanies') }] : []),
      ...(bill?.companies ?? []).map((c) => ({ value: c.slug, label: c.name })),
    ],
    [bill?.companies, manager, t],
  )
  const detail = bill?.company
  const maxDay = Math.max(0.01, ...(detail?.daily.map((d) => d.amount) ?? [0]))

  return (
    <div className="flex flex-col gap-4">
      {/* controls */}
      <div data-tour="billing-controls" className="flex flex-wrap items-center gap-2 print:hidden">
        <div className="inline-flex items-center rounded-lg border border-line-strong bg-card">
          <button
            type="button"
            onClick={() => setMonth(shiftMonth(month, -1))}
            className="grid h-9 w-9 place-items-center text-text-mid hover:text-text-high"
            aria-label={t('billing.prevMonth')}
          >
            <ChevronLeft size={15} aria-hidden />
          </button>
          <span className="min-w-[140px] text-center text-[13px] font-semibold text-text-high">{monthLabel}</span>
          <button
            type="button"
            onClick={() => setMonth(shiftMonth(month, 1))}
            disabled={month >= thisMonth()}
            className="grid h-9 w-9 place-items-center text-text-mid hover:text-text-high disabled:opacity-40"
            aria-label={t('billing.nextMonth')}
          >
            <ChevronRight size={15} aria-hidden />
          </button>
        </div>
        {companyOptions.length > 1 && (
          <Select
            value={company}
            onChange={setCompany}
            options={companyOptions}
            ariaLabel={t('billing.company')}
            className="min-w-[220px]"
          />
        )}
        <div className="ml-auto flex items-center gap-2">
          {manager && (
            <Link
              href="/settings/billing"
              className="inline-flex h-9 items-center gap-1.5 rounded-lg px-3 text-[13px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
            >
              <Settings2 size={14} aria-hidden /> {t('billing.terms')}
            </Link>
          )}
          <Button variant="secondary" size="sm" onClick={() => window.print()} disabled={!detail}>
            <Printer size={13} aria-hidden /> {t('billing.print')}
          </Button>
        </div>
      </div>

      {error && (
        <Card className="p-4">
          <p role="alert" className="text-[13px] text-[var(--red-ink)]">{error}</p>
        </Card>
      )}
      {loading && !bill && (
        <div className="flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {/* one company's bill */}
      {bill && detail && (
        <Card className="p-5 sm:p-6" data-tour="billing-statement">
          <div className="flex flex-wrap items-start justify-between gap-4 border-b border-line pb-4">
            <div>
              <div className="eyebrow">{t('billing.statement')}</div>
              <h2 className="display mt-1 text-[24px] leading-tight text-text-heading">{detail.summary.name}</h2>
              <p className="mt-1 text-[12.5px] text-text-muted">
                {t('billing.period', { from: f.date(`${bill.from}T00:00:00Z`, 'UTC'), to: f.date(`${bill.to}T00:00:00Z`, 'UTC') })}
              </p>
            </div>
            <div className="text-right">
              <div className="eyebrow">{t('billing.totalDue')}</div>
              <div className="display mt-1 text-[30px] leading-none text-text-heading">{money(detail.summary.total)}</div>
              <div className="mt-1 text-[12px] text-text-muted">
                {t('billing.requestsTokens', {
                  requests: f.number(detail.summary.requests),
                  tokens: f.number(detail.summary.tokens, { notation: 'compact' }),
                })}
              </div>
            </div>
          </div>

          <div className="mt-4 overflow-x-auto">
            <table className="w-full min-w-[480px] text-[13px]">
              <thead>
                <tr className="border-b border-line text-left text-[11.5px] uppercase tracking-[0.05em] text-text-muted">
                  <th className="py-2 pr-3 font-semibold">{t('billing.cols.item')}</th>
                  <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.requests')}</th>
                  <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.tokens')}</th>
                  {manager && <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.cost')}</th>}
                  <th className="py-2 text-right font-semibold">{t('billing.cols.amount')}</th>
                </tr>
              </thead>
              <tbody>
                {detail.lines.length === 0 && (
                  <tr>
                    <td colSpan={manager ? 5 : 4} className="py-4 text-center text-text-muted">{t('billing.noUsage')}</td>
                  </tr>
                )}
                {detail.lines.map((l) => (
                  <tr key={l.feature} className="border-b border-line">
                    <td className="py-2 pr-3 text-text-high">{featureLabel(l.feature)}</td>
                    <td className="py-2 pr-3 text-right tabular-nums text-text-mid">{f.number(l.requests)}</td>
                    <td className="py-2 pr-3 text-right tabular-nums text-text-mid">{f.number(l.tokens)}</td>
                    {manager && <td className="py-2 pr-3 text-right tabular-nums text-text-muted">{usd(l.cost_usd)}</td>}
                    <td className="py-2 text-right tabular-nums text-text-high">{money(l.amount)}</td>
                  </tr>
                ))}
                <tr className="border-b border-line">
                  <td className="py-2 pr-3 text-text-high" colSpan={manager ? 4 : 3}>{t('billing.monthlyFee')}</td>
                  <td className="py-2 text-right tabular-nums text-text-high">{money(detail.summary.monthly_fee)}</td>
                </tr>
              </tbody>
              <tfoot>
                <tr>
                  <td className="pt-3 font-semibold text-text-heading" colSpan={manager ? 4 : 3}>{t('billing.total')}</td>
                  <td className="pt-3 text-right text-[15px] font-semibold tabular-nums text-text-heading">{money(detail.summary.total)}</td>
                </tr>
              </tfoot>
            </table>
          </div>

          {manager && (
            <p className="mt-3 text-[12px] text-text-muted print:hidden">
              {t('billing.managerLine', {
                cost: usd(detail.summary.cost_usd),
                markup: f.number(detail.summary.markup_pct ?? 0),
                margin: money(detail.summary.margin),
              })}
            </p>
          )}

          {/* daily amounts */}
          <div className="mt-5 print:hidden">
            <div className="eyebrow mb-2">{t('billing.daily')}</div>
            <div className="flex h-24 items-end gap-[3px]" role="img" aria-label={t('billing.daily')}>
              {detail.daily.map((d) => (
                <div
                  key={d.date}
                  title={`${f.date(`${d.date}T00:00:00Z`, 'UTC')} · ${money(d.amount)}`}
                  className="min-w-0 flex-1 rounded-t-sm bg-brand-400/70"
                  style={{ height: `${Math.max(d.amount > 0 ? 4 : 1, (d.amount / maxDay) * 100)}%` }}
                />
              ))}
            </div>
          </div>
        </Card>
      )}

      {/* every company (billing managers, or a reader with several) */}
      {bill && !detail && (
        <Card className="p-5">
          <CardTitle>{t('billing.companiesTitle', { month: monthLabel })}</CardTitle>
          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[560px] text-[13px]">
              <thead>
                <tr className="border-b border-line text-left text-[11.5px] uppercase tracking-[0.05em] text-text-muted">
                  <th className="py-2 pr-3 font-semibold">{t('billing.company')}</th>
                  <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.requests')}</th>
                  {manager && <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.cost')}</th>}
                  {manager && <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.markup')}</th>}
                  <th className="py-2 pr-3 text-right font-semibold">{t('billing.cols.total')}</th>
                  {manager && <th className="py-2 text-right font-semibold">{t('billing.cols.margin')}</th>}
                </tr>
              </thead>
              <tbody>
                {bill.companies.map((c) => (
                  <tr key={c.id} className="border-b border-line">
                    <td className="py-2 pr-3">
                      <button type="button" onClick={() => setCompany(c.slug)} className="font-semibold text-text-high hover:underline">
                        {c.name}
                      </button>
                    </td>
                    <td className="py-2 pr-3 text-right tabular-nums text-text-mid">{f.number(c.requests)}</td>
                    {manager && <td className="py-2 pr-3 text-right tabular-nums text-text-muted">{usd(c.cost_usd)}</td>}
                    {manager && <td className="py-2 pr-3 text-right tabular-nums text-text-muted">{f.number(c.markup_pct ?? 0)}%</td>}
                    <td className="py-2 pr-3 text-right tabular-nums text-text-high">{money(c.total)}</td>
                    {manager && <td className="py-2 text-right tabular-nums text-[var(--green-ink)]">{money(c.margin)}</td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {manager && bill.unattributed && bill.unattributed.requests > 0 && (
            <p className="mt-3 text-[12px] text-text-muted">
              {t('billing.unattributed', {
                requests: f.number(bill.unattributed.requests),
                cost: usd(bill.unattributed.cost_usd),
              })}
            </p>
          )}
          {bill.pricing_note && <p className="mt-2 text-[11.5px] text-text-faint">{bill.pricing_note}</p>}
        </Card>
      )}
    </div>
  )
}
