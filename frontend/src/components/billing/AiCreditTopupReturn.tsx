'use client'

/**
 * Where Stripe sends a buyer back after a top-up (/billing/topup/success and
 * /billing/topup/cancel, `?payment=<id>`). Coming back proves nothing — the credit
 * is added only when Stripe's webhook confirms the payment — so the success page
 * watches the purchase's status (GET /api/billing/ai/credit/topup?payment=) until
 * it settles, and says what happened.
 */

import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { useEffect, useState } from 'react'
import { CheckCircle2, Clock, Loader2, XCircle } from 'lucide-react'
import { Card } from '@/components/ui/Card'
import { type CreditPayment, getTopup } from '@/lib/aiCreditTopup'
import { useFormat, useT } from '@/i18n'

const POLL_MS = 2500
const GIVE_UP_MS = 90_000

export function AiCreditTopupReturn({ kind }: { kind: 'success' | 'cancel' }) {
  const t = useT()
  const f = useFormat()
  const params = useSearchParams()
  const id = Number(params.get('payment') || '')
  const [payment, setPayment] = useState<CreditPayment | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [slow, setSlow] = useState(false)

  useEffect(() => {
    if (!Number.isFinite(id) || id <= 0) return
    let stop = false
    const started = Date.now()
    const tick = async () => {
      try {
        const d = await getTopup(`?payment=${id}`)
        if (stop) return
        setPayment(d.payment)
        // the cancel page reads once; the success page waits for the webhook
        if (kind === 'cancel' || (d.payment && d.payment.status !== 'created')) return
      } catch (e) {
        if (stop) return
        setError(e instanceof Error ? e.message : String(e))
        return
      }
      if (Date.now() - started > GIVE_UP_MS) {
        setSlow(true)
        return
      }
      setTimeout(() => void tick(), POLL_MS)
    }
    void tick()
    return () => {
      stop = true
    }
  }, [id, kind])

  const status = payment?.status
  const money = (n: number) => f.currency(n, payment?.currency || 'USD')

  let Icon = Loader2
  let tone = 'text-text-muted'
  let title = t('billing.credit.topup.return.successTitle')
  let body = t('billing.credit.topup.return.waiting')
  if (!Number.isFinite(id) || id <= 0) {
    Icon = XCircle
    body = t('billing.credit.topup.return.missing')
  } else if (kind === 'cancel') {
    Icon = XCircle
    title = t('billing.credit.topup.return.cancelTitle')
    body = t('billing.credit.topup.return.cancelBody')
  } else if (status === 'paid' || status === 'refunded' || status === 'disputed') {
    Icon = CheckCircle2
    tone = 'text-[var(--green-ink)]'
    body = t('billing.credit.topup.return.paid', {
      amount: money((payment?.credit_amount ?? 0) + (payment?.bonus_amount ?? 0)),
      company: payment?.company ?? '',
    })
  } else if (status === 'review') {
    Icon = Clock
    tone = 'text-[var(--amber-ink)]'
    body = t('billing.credit.topup.return.review')
  } else if (status === 'failed' || status === 'expired') {
    Icon = XCircle
    tone = 'text-[var(--red-ink)]'
    body = t('billing.credit.topup.return.failed')
  } else if (slow) {
    Icon = Clock
    body = t('billing.credit.topup.return.slow')
  }

  return (
    <div className="flex min-h-[50vh] flex-1 items-center justify-center p-6">
      <Card className="w-full max-w-md p-6 text-center">
        <Icon size={28} className={`mx-auto ${tone} ${Icon === Loader2 ? 'animate-spin' : ''}`} aria-hidden />
        <h1 className="display mt-3 text-[22px] text-text-heading">{title}</h1>
        <p role="status" className="mt-2 text-[13px] leading-relaxed text-text-mid">
          {body}
        </p>
        {error && (
          <p role="alert" className="mt-2 text-[12.5px] text-[var(--red-ink)]">
            {error}
          </p>
        )}
        <Link
          href="/billing"
          className="mt-4 inline-flex items-center justify-center rounded-lg bg-primary px-4 py-2 text-[13px] font-semibold text-on-primary hover:opacity-90"
        >
          {t('billing.credit.topup.return.back')}
        </Link>
      </Card>
    </div>
  )
}
