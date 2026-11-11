/**
 * Buying prepaid AI credit (Stripe Checkout) and the promotions on it — see
 * backend-csharp/src/ChatBranch.Api/Endpoints/AiCreditTopupEndpoints.cs.
 *
 *   GET  /api/billing/ai/credit/topup            what the caller may buy (billing.topup | billing.manage)
 *   POST /api/billing/ai/credit/topup            open a Checkout Session → { url }
 *   GET|POST /api/billing/ai/credit/offers       offer CRUD (billing.manage)
 *   DELETE   /api/billing/ai/credit/offers/:slug
 *   POST     /api/billing/ai/credit/offers/:slug/grant   a manual grant of an offer's bonus
 *
 * The buyer pays on Stripe's own page; the credit lands when Stripe's webhook
 * confirms the payment — never because the browser came back to the success page.
 */

import type { MessageKey } from '@/i18n'
import { aiApi } from '@/lib/aiMonitor'

export type OfferKind = 'bonus_pct' | 'bonus_fixed' | 'bundle'
export type PaymentStatus = 'created' | 'paid' | 'review' | 'failed' | 'expired' | 'refunded' | 'disputed'

export interface CreditOffer {
  slug: string
  label: string
  kind: OfferKind
  /** bundle: the price. bonus offers: the minimum purchase (0 = none). */
  pay_amount: number
  /** bundle: the credit received in total. */
  credit_amount: number
  bonus_pct: number
  bonus_fixed: number
  currency: string
  active: boolean
  starts_at: string | null
  ends_at: string | null
  max_redemptions: number
  /** Billing managers only. */
  redemptions: number | null
  /** Redemptions left; null = unlimited. */
  remaining: number | null
  /** Limited to one company; null = every company. */
  entity_id: number | null
  company: string | null
  summary: string
  note: string | null
  /** Why a buyer cannot pick it now; null when it is open. */
  unavailable: string | null
  updated_at: string
}

export interface CreditPayment {
  id: number
  entity_id: number
  company: string
  status: PaymentStatus
  status_detail: string
  mode: 'test' | 'live'
  currency: string
  /** What Stripe charges, in major units. */
  amount: number
  credit_amount: number
  bonus_amount: number
  offer_id: number | null
  refunded: number
  created_by: string
  created_at: string
  completed_at: string | null
}

export interface TopupPayload {
  ready: boolean
  reason: string | null
  mode: 'test' | 'live'
  currency: string
  min: number
  max: number
  presets: number[]
  companies: { id: number; name: string; slug: string; balance: number; opened: boolean }[]
  offers: CreditOffer[]
  payments: CreditPayment[]
  payment: CreditPayment | null
  note: string
}

export interface TopupRequest {
  company: string
  amount: number
  offer?: string
  success_url: string
  cancel_url: string
}

export interface OffersPayload {
  currency: string
  kinds: OfferKind[]
  offers: CreditOffer[]
  companies: { id: number; name: string; slug: string }[]
}

export interface OfferWrite {
  slug?: string
  label?: string
  kind?: OfferKind
  bonus_pct?: number
  bonus_fixed?: number
  pay_amount?: number
  credit_amount?: number
  active?: boolean
  starts_at?: string
  ends_at?: string
  max_redemptions?: number
  entity?: string
  note?: string
}

export const getTopup = (query = '') => aiApi<TopupPayload>(`billing/ai/credit/topup${query}`)

/** `key` is sent as the Idempotency-Key header: a double click opens one checkout. */
export const startTopup = (body: TopupRequest, key: string) =>
  aiApi<{ ok: boolean; replayed: boolean; url: string; payment: CreditPayment }>('billing/ai/credit/topup', {
    method: 'POST',
    headers: { 'Idempotency-Key': key },
    body: JSON.stringify(body),
  })

export const getOffers = () => aiApi<OffersPayload>('billing/ai/credit/offers')

export const saveOffer = (body: OfferWrite) =>
  aiApi<{ ok: boolean; created: boolean; offer: CreditOffer }>('billing/ai/credit/offers', {
    method: 'POST',
    body: JSON.stringify(body),
  })

export const deleteOffer = (slug: string) =>
  aiApi<{ ok: boolean }>(`billing/ai/credit/offers/${encodeURIComponent(slug)}`, { method: 'DELETE' })

export const grantOffer = (slug: string, body: { entity: string; amount?: number; note?: string; idempotency_key?: string }) =>
  aiApi<{ ok: boolean; replayed: boolean; balance: number; name: string; entry: { amount: number } }>(
    `billing/ai/credit/offers/${encodeURIComponent(slug)}/grant`,
    { method: 'POST', body: JSON.stringify(body) },
  )

/** The bonus an offer adds to a purchase of `amount` (0 when it does not apply). */
export function offerBonus(o: CreditOffer | undefined, amount: number): number {
  if (!o) return 0
  if (o.kind === 'bundle') return Math.max(0, o.credit_amount - o.pay_amount)
  if (o.pay_amount > 0 && amount < o.pay_amount) return 0
  return o.kind === 'bonus_pct' ? Math.round(amount * o.bonus_pct) / 100 : o.bonus_fixed
}

const STATUS_KEYS: Record<PaymentStatus, MessageKey> = {
  created: 'billing.credit.topup.status.created',
  paid: 'billing.credit.topup.status.paid',
  review: 'billing.credit.topup.status.review',
  failed: 'billing.credit.topup.status.failed',
  expired: 'billing.credit.topup.status.expired',
  refunded: 'billing.credit.topup.status.refunded',
  disputed: 'billing.credit.topup.status.disputed',
}

export const paymentStatusKey = (s: string): MessageKey => STATUS_KEYS[s as PaymentStatus] ?? STATUS_KEYS.created

/** A fresh retry key for one press of the Pay button. */
export const newIdempotencyKey = (): string =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
