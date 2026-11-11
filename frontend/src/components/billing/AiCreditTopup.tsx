'use client'

/**
 * Top up AI credit — the card beside the balances on /billing
 * (GET/POST /api/billing/ai/credit/topup, billing.topup or billing.manage).
 *
 * The buyer picks a company, an amount and optionally a promotion; the server
 * decides what will be credited, opens a Stripe Checkout Session and answers its
 * URL, and the browser goes to Stripe's own page (cards, FPX, …). The credit lands
 * when Stripe's webhook confirms the payment — the success page only watches for it.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { CreditCard, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { ApiError } from '@/lib/aiMonitor'
import { absoluteUrl } from '@/lib/apiBase'
import {
  type TopupPayload,
  getTopup,
  newIdempotencyKey,
  offerBonus,
  paymentStatusKey,
  startTopup,
} from '@/lib/aiCreditTopup'
import { useFormat, useT } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const STATUS_TONE: Record<string, string> = {
  paid: 'text-[var(--green-ink)]',
  review: 'text-[var(--red-ink)]',
  failed: 'text-[var(--red-ink)]',
  disputed: 'text-[var(--red-ink)]',
  refunded: 'text-text-muted',
  expired: 'text-text-faint',
  created: 'text-text-muted',
}

export function AiCreditTopup() {
  const t = useT()
  const f = useFormat()
  const [data, setData] = useState<TopupPayload | null>(null)
  const [hidden, setHidden] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [company, setCompany] = useState('')
  const [amount, setAmount] = useState('100')
  const [offer, setOffer] = useState('')
  // one key per intended purchase: a double click re-sends the same key (one checkout)
  const keyRef = useRef<string>(newIdempotencyKey())

  const load = useCallback(async () => {
    try {
      const d = await getTopup()
      setData(d)
      setCompany((cur) => cur || (d.companies.length === 1 ? String(d.companies[0].slug || d.companies[0].id) : ''))
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) setHidden(true)
      else setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const currency = data?.currency || 'USD'
  const money = (n: number) => f.currency(n, currency)
  const picked = data?.companies.find((c) => String(c.slug || c.id) === company)
  const offers = useMemo(
    () => (data?.offers ?? []).filter((o) => o.entity_id == null || (picked && o.entity_id === picked.id)),
    [data, picked],
  )
  const chosen = offers.find((o) => o.slug === offer)
  const value = chosen?.kind === 'bundle' ? chosen.pay_amount : Number(amount)
  const bonus = offerBonus(chosen, value)
  const valid = Number.isFinite(value) && !!data && value >= data.min && value <= data.max

  // a new choice is a new purchase: a new retry key
  useEffect(() => {
    keyRef.current = newIdempotencyKey()
  }, [company, amount, offer])

  if (hidden) return null

  const pay = async () => {
    if (!data) return
    if (!picked) {
      setError(t('billing.credit.topup.pickCompany'))
      return
    }
    if (!valid) {
      setError(t('billing.credit.topup.invalid', { min: money(data.min), max: money(data.max) }))
      return
    }
    setBusy(true)
    setError(null)
    try {
      const res = await startTopup(
        {
          company: String(picked.slug || picked.id),
          amount: value,
          offer: chosen?.slug,
          // dashboard pages Stripe sends the buyer back to — absolute, on this origin
          success_url: absoluteUrl('/billing/topup/success'),
          cancel_url: absoluteUrl('/billing/topup/cancel'),
        },
        keyRef.current,
      )
      window.location.assign(res.url)
    } catch (e) {
      // that key's checkout failed or never opened: the next press is a new purchase
      keyRef.current = newIdempotencyKey()
      setError(e instanceof Error ? e.message : String(e))
      setBusy(false)
    }
  }

  return (
    <Card className="p-5 print:hidden" data-tour="billing-topup">
      <CardTitle
        right={
          data?.mode === 'test' ? (
            <span className="rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-semibold text-[var(--amber-ink)]">
              {t('billing.credit.topup.testMode')}
            </span>
          ) : undefined
        }
      >
        <span className="inline-flex items-center gap-2">
          <CreditCard size={15} aria-hidden /> {t('billing.credit.topup.title')}
        </span>
      </CardTitle>
      <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.credit.topup.help')}</p>

      {!data && !error && (
        <div className="mt-3 flex items-center gap-2 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      )}

      {data && !data.ready && (
        <p className="mt-3 rounded-lg bg-fill-subtle px-3 py-2 text-[12.5px] text-text-mid">
          {t('billing.credit.topup.notReady', { reason: data.reason ?? '' })}
        </p>
      )}

      {data && data.ready && (
        <>
          <div className="mt-4 grid gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('billing.credit.topup.company')}</span>
              <Select
                value={company}
                onChange={(v) => {
                  setCompany(v)
                  setOffer('')
                }}
                options={data.companies.map((c) => ({
                  value: String(c.slug || c.id),
                  label: c.name,
                  hint: c.opened ? money(c.balance) : undefined,
                }))}
                size="md"
                ariaLabel={t('billing.credit.topup.company')}
                placeholder={t('billing.credit.topup.pickCompany')}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">
                {t('billing.credit.topup.amount', { currency })}
              </span>
              <input
                type="number"
                step="0.01"
                min={data.min}
                max={data.max}
                value={chosen?.kind === 'bundle' ? String(chosen.pay_amount) : amount}
                disabled={chosen?.kind === 'bundle'}
                onChange={(e) => setAmount(e.target.value)}
                className={fieldCls}
              />
            </label>
          </div>
          <div className="mt-2 flex flex-wrap gap-2">
            {data.presets.map((p) => (
              <Button
                key={p}
                size="sm"
                variant={Number(amount) === p && chosen?.kind !== 'bundle' ? 'primary' : 'secondary'}
                disabled={chosen?.kind === 'bundle'}
                onClick={() => setAmount(String(p))}
              >
                {money(p)}
              </Button>
            ))}
          </div>
          <p className="mt-1 text-[11.5px] text-text-muted">
            {t('billing.credit.topup.range', { min: money(data.min), max: money(data.max) })}
          </p>

          {offers.length > 0 && (
            <fieldset className="mt-3">
              <legend className="mb-1 text-[12px] font-semibold text-text-high">{t('billing.credit.topup.offer')}</legend>
              <div className="flex flex-col gap-1.5">
                {[{ slug: '', label: t('billing.credit.topup.noOffer'), summary: '' }, ...offers].map((o) => {
                  const full = offers.find((x) => x.slug === o.slug)
                  return (
                    <label key={o.slug || 'none'} className="flex cursor-pointer items-start gap-2 text-[12.5px] text-text-mid">
                      <input
                        type="radio"
                        name="topup-offer"
                        className="mt-0.5"
                        checked={offer === o.slug}
                        onChange={() => setOffer(o.slug)}
                      />
                      <span>
                        <span className="font-semibold text-text-high">{o.label}</span>
                        {o.summary && <span className="text-text-muted"> · {o.summary}</span>}
                        {full?.ends_at && (
                          <span className="text-text-faint"> · {t('billing.credit.topup.offerEnds', { when: f.dateTime(full.ends_at) })}</span>
                        )}
                        {full?.remaining != null && (
                          <span className="text-text-faint"> · {t('billing.credit.topup.offerLeft', { n: full.remaining })}</span>
                        )}
                      </span>
                    </label>
                  )
                })}
              </div>
            </fieldset>
          )}

          {valid && (
            <p className="mt-3 text-[13px] text-text-high">
              {bonus > 0
                ? t('billing.credit.topup.summaryBonus', {
                    pay: money(value),
                    total: money(value + bonus),
                    credit: money(value),
                    bonus: money(bonus),
                  })
                : t('billing.credit.topup.summary', { pay: money(value), total: money(value) })}
            </p>
          )}

          <div className="mt-3 flex flex-wrap items-center gap-3">
            <Button onClick={pay} disabled={busy || !picked}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CreditCard size={13} aria-hidden />}
              {busy ? t('billing.credit.topup.opening') : t('billing.credit.topup.pay', { amount: valid ? money(value) : '' })}
            </Button>
          </div>
        </>
      )}

      {error && (
        <p role="alert" className="mt-3 text-[12.5px] text-[var(--red-ink)]">
          {error}
        </p>
      )}

      {data && (
        <>
          <div className="eyebrow mt-5">{t('billing.credit.topup.recent')}</div>
          {data.payments.length === 0 ? (
            <p className="mt-1 text-[12.5px] text-text-muted">{t('billing.credit.topup.none')}</p>
          ) : (
            <ul className="mt-1 flex flex-col gap-1">
              {data.payments.map((p) => (
                <li key={p.id} className="flex flex-wrap items-baseline gap-x-2 text-[12.5px] text-text-mid">
                  <span className="tabular-nums text-text-muted">{f.dateTime(p.created_at)}</span>
                  <span className="font-semibold tabular-nums text-text-high">{f.currency(p.amount, p.currency)}</span>
                  {p.bonus_amount > 0 && (
                    <span className="text-[var(--green-ink)]">
                      {t('billing.credit.topup.bonus', { amount: f.currency(p.bonus_amount, p.currency) })}
                    </span>
                  )}
                  <span className="text-text-high">{p.company}</span>
                  <span className={STATUS_TONE[p.status] ?? 'text-text-muted'}>· {t(paymentStatusKey(p.status))}</span>
                  {p.mode === 'test' && <span className="text-text-faint">· {t('billing.credit.topup.testShort')}</span>}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </Card>
  )
}
