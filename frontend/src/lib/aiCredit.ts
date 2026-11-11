/**
 * Prepaid AI credit, as the API serves it (GET /api/billing/ai/credit,
 * POST /api/billing/ai/credit/grant | /adjust — see
 * backend-csharp/src/ChatBranch.Api/Endpoints/AiCreditEndpoints.cs).
 *
 * Credit is held per COMPANY (a top-level entity); a branch draws on its
 * company's balance. Balances are SIGNED — a balance may be negative and is
 * never clamped to zero.
 */

import type { MessageKey } from '@/i18n'
import { aiApi } from '@/lib/aiMonitor'

export type CreditKind =
  | 'grant'
  | 'topup'
  | 'promo_bonus'
  | 'consumption'
  | 'adjustment'
  | 'refund'
  | 'chargeback'
  | 'expiry'
  | 'conversion'

export interface CreditStatus {
  scope: 'entity' | 'user'
  key: string
  entity_id: number | null
  user_name: string
  name: string
  /** Signed: may be negative. */
  balance: number
  currency: string
  enabled: boolean
  /** An account row exists (credit was granted at least once). */
  opened: boolean
  /** The money is gone — shown as such; nothing stops an AI reply yet. */
  exhausted: boolean
  overdraft_limit: number
  low_balance_threshold: number
  updated_at: string | null
}

export interface CreditCompany {
  id: number
  name: string
  slug: string
  /** Branches that draw on this company's balance (they hold none of their own). */
  branches: { id: number; name: string; slug: string }[]
  credit: CreditStatus
}

export interface CreditLine {
  id: number
  scope: 'entity' | 'user'
  entity_id: number | null
  company: string | null
  user_name: string
  kind: CreditKind
  amount: number
  currency: string
  balance_after: number
  ref: string
  note: string
  actor: string
  source: string
  created_at: string
}

export interface CreditPayload {
  currency: string
  is_manager: boolean
  can_grant: boolean
  companies: CreditCompany[]
  users: CreditStatus[]
  resolved: { entity_id: number; entity_name: string; draws_on: CreditStatus } | null
  totals: { balance: number; accounts: number; exhausted: number }
  ledger: CreditLine[]
  page: number
  page_size: number
  note: string
}

export interface CreditWriteResult {
  ok: boolean
  /** The same idempotency key was already recorded — nothing was credited twice. */
  replayed: boolean
  currency: string
  entry: {
    id: number
    scope: 'entity' | 'user'
    entity_id: number | null
    user_name: string
    kind: CreditKind
    amount: number
    balance_after: number
    note: string
    actor: string
    created_at: string
  }
  balance: number
  name: string
}

export interface CreditWrite {
  /** A company (top-level entity) slug or id — credit is held per company. */
  entity?: string
  /** A username, for the optional per-login balance (instead of `entity`). */
  user?: string
  amount: number
  note?: string
  /** Make a retry safe: the same key credits exactly once. */
  idempotency_key?: string
}

export const getCredit = (query = '') => aiApi<CreditPayload>(`billing/ai/credit${query}`)

export const writeCredit = (kind: 'grant' | 'adjust', body: CreditWrite) =>
  aiApi<CreditWriteResult>(`billing/ai/credit/${kind}`, { method: 'POST', body: JSON.stringify(body) })

/** Sub-unit balances must not read as 0 (the bill's rule). */
export const creditDigits = (n: number): number | undefined => (n !== 0 && Math.abs(n) < 1 ? 4 : undefined)

const KIND_KEYS: Record<CreditKind, MessageKey> = {
  grant: 'billing.credit.kinds.grant',
  topup: 'billing.credit.kinds.topup',
  promo_bonus: 'billing.credit.kinds.promoBonus',
  consumption: 'billing.credit.kinds.consumption',
  adjustment: 'billing.credit.kinds.adjustment',
  refund: 'billing.credit.kinds.refund',
  chargeback: 'billing.credit.kinds.chargeback',
  expiry: 'billing.credit.kinds.expiry',
  conversion: 'billing.credit.kinds.conversion',
}

/** The label of a ledger line's kind (unknown kinds fall back to "adjustment"). */
export const creditKindKey = (kind: string): MessageKey =>
  KIND_KEYS[kind as CreditKind] ?? KIND_KEYS.adjustment
