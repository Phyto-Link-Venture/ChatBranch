/**
 * AI price books, as the API serves them (GET/POST /api/billing/ai/price-books,
 * DELETE /api/billing/ai/price-books/{slug}, and the bulk assignment
 * POST /api/bindings/pricebook-entities — see
 * backend-csharp/src/ChatBranch.Api/Endpoints/AiPriceBookEndpoints.cs).
 *
 * A plan is named once and priced one of three ways; assigning it to an entity
 * prices that entity and every branch beneath it. The resolution order lives on
 * the server (AiPriceBooks.Resolve): company override → the plan assigned
 * nearest up the tree → the organisation default plan → the default terms.
 */

import { aiApi } from '@/lib/aiMonitor'

export type PriceMode = 'markup' | 'flat' | 'unit'

export type PriceSource = 'company_override' | 'plan' | 'org_default_plan' | 'builtin'

export interface PricePlan {
  id: number
  slug: string
  label: string
  mode: PriceMode
  markup_pct: number
  flat_amount: number
  unit_per_1k_tokens: number
  unit_per_reply: number
  monthly_fee: number
  /** The monthly AI allowance each login this plan covers gets (0 = none). */
  user_allowance: number
  active: boolean
  is_default: boolean
  note: string
  /** How many entities this plan prices right now. */
  entities: number
  summary: string
  /** The month's terms already carry this plan exactly as it is here. */
  in_terms: boolean
  created_at: string
  updated_at: string
}

export interface PriceUsage {
  requests: number
  tokens: number
  cost_usd: number | null
  usage_amount: number
  monthly_fee: number
  total: number
}

export interface PriceRow {
  id: number
  name: string
  slug: string
  parent_id: number | null
  company: boolean
  depth: number
  company_id: number
  plan: string | null
  plan_label: string | null
  mode: PriceMode
  plan_source: PriceSource
  /** The plan came from an ancestor, not from this row. */
  inherited: boolean
  assigned_at: number | null
  assigned_at_name: string | null
  /** A plan is pinned on this row itself. */
  assigned_here: boolean
  markup_pct: number
  monthly_fee: number
  user_allowance: number
  unit_per_1k_tokens: number
  unit_per_reply: number
  override: { markup_pct: number | null; monthly_fee: number | null } | null
  /** This month's real figures — companies only (a bill is issued to a company). */
  usage: PriceUsage | null
}

export interface PriceBookPayload {
  month: string
  current_month: string
  currency: string
  usd_rate: number
  defaults: { markup_pct: number; monthly_fee: number }
  default_plan: string | null
  plans: PricePlan[]
  scope: 'companies' | 'entities'
  q: string
  overrides_only: boolean
  page: number
  page_size: number
  total: number
  rows: PriceRow[]
  reply_features: string[]
  note: string
}

export interface PlanBody {
  slug?: string
  label?: string
  mode?: PriceMode
  markup_pct?: number
  flat_amount?: number
  unit_per_1k_tokens?: number
  unit_per_reply?: number
  monthly_fee?: number
  user_allowance?: number
  active?: boolean
  is_default?: boolean
  note?: string
}

export interface ListParams {
  q?: string
  scope?: 'companies' | 'entities'
  page?: number
  page_size?: number
  overrides_only?: boolean
  month?: string
}

export const priceBooks = {
  list: (params: ListParams = {}) => {
    const qs = new URLSearchParams()
    if (params.q) qs.set('q', params.q)
    if (params.scope) qs.set('scope', params.scope)
    if (params.page) qs.set('page', String(params.page))
    if (params.page_size) qs.set('page_size', String(params.page_size))
    if (params.overrides_only) qs.set('overrides_only', 'true')
    if (params.month) qs.set('month', params.month)
    const query = qs.toString()
    return aiApi<PriceBookPayload>(`billing/ai/price-books${query ? `?${query}` : ''}`)
  },

  save: (body: PlanBody) =>
    aiApi<{ ok: boolean; created: boolean; plan: PricePlan; effective_from: string | null }>(
      'billing/ai/price-books',
      { method: 'POST', body: JSON.stringify(body) },
    ),

  remove: (slug: string) =>
    aiApi<{ ok: boolean; deleted: string }>(`billing/ai/price-books/${encodeURIComponent(slug)}`, {
      method: 'DELETE',
    }),

  /** The bulk assignment: one call, as many entities as you like. */
  assign: (planId: number, add: number[] = [], remove: number[] = []) =>
    aiApi<{ ok: boolean; added: number; removed: number; member_ids: number[] }>(
      'bindings/pricebook-entities',
      { method: 'POST', body: JSON.stringify({ target: planId, add, remove }) },
    ),
}

/** What one plan would bill for a month that cost `costUsd` with `tokens` and `replies`. */
export function usageAmount(
  row: Pick<PriceRow, 'mode' | 'markup_pct' | 'unit_per_1k_tokens' | 'unit_per_reply'>,
  costUsd: number,
  tokens: number,
  replies: number,
  usdRate: number,
): number {
  if (row.mode === 'flat') return 0
  if (row.mode === 'unit') return (tokens / 1000) * row.unit_per_1k_tokens + replies * row.unit_per_reply
  return costUsd * (1 + row.markup_pct / 100) * usdRate
}
