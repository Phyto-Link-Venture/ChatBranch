'use client'

/**
 * Settings → AI billing (billing.manage, superadmin by default): the terms
 * every company's AI bill FALLS BACK to — the billing currency and its rate
 * against USD (the cost ledger is priced in USD), and the default markup % and
 * monthly fee for an entity no price book prices. GET/PUT
 * /api/billing/ai/settings. A change bills the CURRENT month and later —
 * earlier months keep the terms that applied to them (the server keeps the
 * history; shown under the form).
 *
 * Pricing a client is the job of the price books below (PriceBooks.tsx): a
 * named plan assigned to as many companies as you like, with the per-company
 * override living there too, on a searchable and paginated list instead of one
 * live input per company.
 */

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { ArrowUpRight, Loader2, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { aiApi } from '@/lib/aiMonitor'
import { useFormat, useT } from '@/i18n'

interface Terms {
  currency: string
  usd_rate: number
  default_markup_pct: number
  default_monthly_fee: number
  companies: Record<string, { markup_pct?: number | null; monthly_fee?: number | null }>
  effective_from?: string | null
}

interface HistoryRow {
  effective_from: string | null
  until: string | null
  currency: string
  usd_rate: number
  default_markup_pct: number
  default_monthly_fee: number
  companies: number
}

interface Payload {
  settings: Terms
  history?: HistoryRow[]
  companies: { id: number; name: string; slug: string; markup_pct: number | null; monthly_fee: number | null }[]
}

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

export function BillingSettings() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<Payload | null>(null)
  const [terms, setTerms] = useState<Terms | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    aiApi<Payload>('billing/ai/settings')
      .then((d) => {
        setData(d)
        setTerms(d.settings)
      })
      .catch((e: Error) => setError(e.message))
  }, [])

  if (!terms || !data) {
    return error ? (
      <p role="alert" className="text-[13px] text-[var(--red-ink)]">{error}</p>
    ) : (
      <div className="flex items-center gap-2 text-[13px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
      </div>
    )
  }

  const set = (patch: Partial<Terms>) => {
    setTerms({ ...terms, ...patch })
    setNotice(null)
  }

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const d = await aiApi<{ settings: Terms; history: HistoryRow[] }>('billing/ai/settings', {
        method: 'PUT',
        body: JSON.stringify(terms),
      })
      setTerms(d.settings)
      setData({ ...data, history: d.history })
      setNotice(t('common.status.saved'))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const monthName = (m: string | null) =>
    m ? f.monthYear(`${m}-01T00:00:00Z`, 'UTC') : ''
  const history = data.history ?? []

  const example = 10 * (1 + (terms.default_markup_pct || 0) / 100) * (terms.usd_rate || 0)

  return (
    <div className="flex max-w-[860px] flex-col gap-4">
      <Card className="p-5" data-tour="billingset-defaults">
        <CardTitle
          right={
            <Link href="/billing" className="inline-flex items-center gap-1 font-semibold text-[var(--brand-ink)] hover:underline">
              {t('billing.settings.openBill')} <ArrowUpRight size={12} aria-hidden />
            </Link>
          }
        >
          {t('billing.settings.defaultsTitle')}
        </CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.settings.defaultsHelp')}</p>
        <p
          data-tour="billingset-locked"
          className="mt-2 rounded-lg border border-line px-3 py-2 text-[12.5px] font-semibold text-text-high"
        >
          {t('billing.settings.lockedNote')}
        </p>
        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.settings.currency')}</span>
            <input
              value={terms.currency}
              maxLength={3}
              onChange={(e) => set({ currency: e.target.value.toUpperCase() })}
              className={fieldCls}
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">
              {t('billing.settings.rate', { currency: terms.currency || '…' })}
            </span>
            <input
              type="number"
              min={0}
              step="0.0001"
              value={terms.usd_rate}
              onChange={(e) => set({ usd_rate: Number(e.target.value) })}
              className={fieldCls}
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.settings.markup')}</span>
            <input
              type="number"
              min={0}
              step="0.01"
              value={terms.default_markup_pct}
              onChange={(e) => set({ default_markup_pct: Number(e.target.value) })}
              className={fieldCls}
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">
              {t('billing.settings.fee', { currency: terms.currency || '…' })}
            </span>
            <input
              type="number"
              min={0}
              step="0.01"
              value={terms.default_monthly_fee}
              onChange={(e) => set({ default_monthly_fee: Number(e.target.value) })}
              className={fieldCls}
            />
          </label>
        </div>
        <p data-tour="billingset-example" className="mt-3 rounded-lg bg-fill-subtle px-3 py-2 text-[12.5px] text-text-mid">
          {t('billing.settings.example', {
            amount: Number.isFinite(example) ? example.toFixed(2) : '—',
            currency: terms.currency,
            markup: terms.default_markup_pct || 0,
          })}
        </p>
      </Card>

      {history.length > 0 && (
        <Card className="p-5" data-tour="billingset-history">
          <CardTitle>{t('billing.settings.historyTitle')}</CardTitle>
          <ul className="mt-2 flex flex-col gap-1.5 text-[12.5px]">
            {history.map((h, i) => (
              <li key={`${h.effective_from ?? 'start'}-${i}`} className={i === 0 ? 'font-semibold text-text-high' : 'text-text-mid'}>
                {(h.effective_from
                  ? h.until
                    ? t('billing.settings.historyRange', { from: monthName(h.effective_from), until: monthName(h.until) })
                    : t('billing.settings.historySince', { from: monthName(h.effective_from) })
                  : h.until
                    ? t('billing.settings.historyUntil', { until: monthName(h.until) })
                    : t('billing.settings.historyAlways')) +
                  ' · ' +
                  t('billing.settings.historyTerms', {
                    markup: f.number(h.default_markup_pct),
                    fee: f.number(h.default_monthly_fee),
                    currency: h.currency,
                    rate: f.number(h.usd_rate),
                    companies: h.companies,
                  })}
              </li>
            ))}
          </ul>
        </Card>
      )}

      <div data-tour="billingset-save" className="flex items-center gap-3">
        <Button onClick={save} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Save size={13} aria-hidden />}
          {t('billing.settings.save')}
        </Button>
        {error && <span role="alert" className="text-[12.5px] text-[var(--red-ink)]">{error}</span>}
        {notice && <span role="status" className="text-[12.5px] text-[var(--green-ink)]">{notice}</span>}
      </div>
    </div>
  )
}
