'use client'

/**
 * Settings → AI billing (billing.manage, superadmin by default): the terms
 * every company's AI bill is computed with — the billing currency and its
 * rate against USD (the cost ledger is priced in USD), a default markup % and
 * monthly fee, and per-company overrides. GET/PUT /api/billing/ai/settings.
 */

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { ArrowUpRight, Loader2, Save } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { aiApi } from '@/lib/aiMonitor'
import { useT } from '@/i18n'

interface Terms {
  currency: string
  usd_rate: number
  default_markup_pct: number
  default_monthly_fee: number
  companies: Record<string, { markup_pct?: number | null; monthly_fee?: number | null }>
}

interface Payload {
  settings: Terms
  companies: { id: number; name: string; slug: string; markup_pct: number | null; monthly_fee: number | null }[]
}

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

/** "" ⇄ null for the optional per-company numbers. */
const num = (v: string): number | null => (v.trim() === '' ? null : Number(v))

export function BillingSettings() {
  const t = useT()
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
  const setCompany = (id: number, patch: { markup_pct?: number | null; monthly_fee?: number | null }) =>
    set({ companies: { ...terms.companies, [String(id)]: { ...terms.companies[String(id)], ...patch } } })

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const d = await aiApi<{ settings: Terms }>('billing/ai/settings', { method: 'PUT', body: JSON.stringify(terms) })
      setTerms(d.settings)
      setNotice(t('common.status.saved'))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

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
              step="1"
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

      <Card className="p-5" data-tour="billingset-companies">
        <CardTitle>{t('billing.settings.companiesTitle')}</CardTitle>
        <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.settings.companiesHelp')}</p>
        {data.companies.length === 0 ? (
          <p className="mt-3 text-[13px] text-text-muted">{t('billing.settings.noCompanies')}</p>
        ) : (
          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[480px] text-[13px]">
              <thead>
                <tr className="border-b border-line text-left text-[11.5px] uppercase tracking-[0.05em] text-text-muted">
                  <th className="py-2 pr-3 font-semibold">{t('billing.company')}</th>
                  <th className="py-2 pr-3 font-semibold">{t('billing.settings.markup')}</th>
                  <th className="py-2 font-semibold">{t('billing.settings.fee', { currency: terms.currency })}</th>
                </tr>
              </thead>
              <tbody>
                {data.companies.map((c) => {
                  const own = terms.companies[String(c.id)] ?? {}
                  return (
                    <tr key={c.id} className="border-b border-line last:border-b-0">
                      <td className="py-2 pr-3 font-semibold text-text-high">{c.name}</td>
                      <td className="py-2 pr-3">
                        <input
                          type="number"
                          min={0}
                          value={own.markup_pct ?? ''}
                          placeholder={t('billing.settings.useDefault', { value: `${terms.default_markup_pct}%` })}
                          onChange={(e) => setCompany(c.id, { markup_pct: num(e.target.value) })}
                          aria-label={`${c.name} — ${t('billing.settings.markup')}`}
                          className={fieldCls}
                        />
                      </td>
                      <td className="py-2">
                        <input
                          type="number"
                          min={0}
                          value={own.monthly_fee ?? ''}
                          placeholder={t('billing.settings.useDefault', { value: String(terms.default_monthly_fee) })}
                          onChange={(e) => setCompany(c.id, { monthly_fee: num(e.target.value) })}
                          aria-label={`${c.name} — ${t('billing.settings.fee', { currency: terms.currency })}`}
                          className={fieldCls}
                        />
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>

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
