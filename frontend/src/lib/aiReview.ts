/**
 * Client + types for the on-demand AI reviewer (/api/ai/review): a stronger
 * model checks a sample of the AI's past replies and suggests changes to the
 * team's setup. Runs only when someone clicks "Review now".
 */
import { aiApi } from './aiMonitor'

export type ReviewSeverity = 'low' | 'medium' | 'high'
export type ReviewFixType = 'knowledge' | 'house_rule' | 'lesson' | 'persona' | 'review_setting' | 'none'
export type ReviewCategory =
  | 'wrong_info'
  | 'unsupported_claim'
  | 'missed_question'
  | 'tone'
  | 'too_long'
  | 'should_handover'
  | 'privacy'
  | 'policy_violation'
  | 'language_mismatch'
  | 'other'
export type ApplyAs = 'lesson' | 'knowledge' | 'house_rule'

export interface ReviewSummary {
  summary?: string
  source?: 'model' | 'heuristic'
  top_problems?: { problem: string; count: number; category?: string }[]
  suggestions?: { type: ReviewFixType; text: string; why?: string }[]
}

export interface ReviewFinding {
  id: number
  run_id: number
  draft_id: number | null
  channel: string
  thread_key: string
  entity_id: number | null
  draft_status: string
  verdict: 'good' | 'issue'
  severity: ReviewSeverity | ''
  categories: ReviewCategory[]
  explanation: string
  evidence: string
  fix: { type: ReviewFixType; text: string }
  customer_excerpt: string
  reply_excerpt: string
  source: 'model' | 'heuristic'
  applied: boolean
  applied_as: ApplyAs | ''
  links: { chat: string | null; draft: string | null }
}

export interface ReviewRun {
  id: number
  status: 'running' | 'done' | 'failed'
  mode: 'model' | 'heuristic'
  provider: string
  model: string
  /** The reviewer picked for this run ("provider:model"), "" = the default. */
  requested_model: string
  note: string
  error: string
  created_by: string
  created_at: string
  finished_at: string | null
  period_from: string
  period_to: string
  limit: number
  entity_id: number | null
  reviewed: number
  issues: number
  high_severity: number
  prompt_tokens: number
  completion_tokens: number
  cost_usd: number | null
  summary: ReviewSummary
}

export interface ReviewRunDetail extends ReviewRun {
  entity: string | null
  entity_name: string
  counts: { all: number; issues: number }
  findings: ReviewFinding[]
}

export interface Reviewer {
  mode: 'model' | 'heuristic'
  provider: string
  model: string
  configured_provider: string
  configured_model: string
  note: string
  default_limit: number
  max_limit: number
  batch_size: number
}

export interface RunList {
  runs: ReviewRun[]
  reviewer: Reviewer
  running: boolean
}

/** The reviewer picker (GET /api/ai/review/options, ai.behaviour.manage). */
export interface ReviewerModelOption {
  /** "provider:model" — what POST /api/ai/review takes as `model`. */
  value: string
  model: string
  is_default: boolean
}

export interface ReviewerProviderOption {
  id: string
  label: string
  /** false = no API key: its models can't be picked. */
  configured: boolean
  models: ReviewerModelOption[]
}

export interface ReviewerOptions {
  providers: ReviewerProviderOption[]
  default: {
    value: string
    raw: string
    provider: string
    label: string
    model: string
    source: 'setting' | 'env' | 'builtin'
    configured: boolean
  }
  reviewer: { mode: 'model' | 'heuristic'; provider: string; model: string; note: string }
  /** settings.ai_keys.manage: may save a pick as the Setting ai_reviewer_model. */
  can_set_default: boolean
  setting_key: string
  max_chars: number
}

/** The server's rule (AiReviewService.ParseModel): a known provider prefix, else OpenRouter. */
export const REVIEW_PROVIDERS = ['openrouter', 'openai', 'ilmu', 'google', 'local'] as const

export function parseReviewerModel(raw: string): { provider: string; model: string } {
  const text = raw.trim()
  const colon = text.indexOf(':')
  if (colon > 0) {
    let prefix = text.slice(0, colon).trim().toLowerCase()
    if (prefix === 'gemini') prefix = 'google'
    if ((REVIEW_PROVIDERS as readonly string[]).includes(prefix)) {
      return { provider: prefix, model: text.slice(colon + 1).trim() }
    }
  }
  return { provider: 'openrouter', model: text }
}

export const reviewApi = {
  list: (entityQuery: string) => aiApi<RunList>(`ai/review/runs?limit=20${entityQuery ? `&${entityQuery}` : ''}`),
  get: (id: number) => aiApi<{ run: ReviewRunDetail }>(`ai/review/runs/${id}`),
  options: () => aiApi<ReviewerOptions>('ai/review/options'),
  /** Save the default reviewer (Setting ai_reviewer_model) — settings.ai_keys.manage. */
  setDefault: (value: string) =>
    aiApi<unknown>('settings/ai-providers', { method: 'PUT', body: JSON.stringify({ ai_reviewer_model: value }) }),
  start: (body: { from?: string; to?: string; days?: number; entity?: string; limit?: number; model?: string }) =>
    aiApi<{ run: ReviewRunDetail }>('ai/review', { method: 'POST', body: JSON.stringify(body) }),
  /** `level` = the entity slug it belongs to, "" = organization-wide (omitted = the server's default). */
  apply: (findingId: number, as: ApplyAs, text?: string, level?: string) =>
    aiApi<{ ok: boolean; applied_as: ApplyAs; result: { message?: string }; finding: ReviewFinding }>(
      `ai/review/findings/${findingId}/apply`,
      { method: 'POST', body: JSON.stringify(level === undefined ? { as, text } : { as, text, level }) },
    ),
}
