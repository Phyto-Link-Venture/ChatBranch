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

export const reviewApi = {
  list: (entityQuery: string) => aiApi<RunList>(`ai/review/runs?limit=20${entityQuery ? `&${entityQuery}` : ''}`),
  get: (id: number) => aiApi<{ run: ReviewRunDetail }>(`ai/review/runs/${id}`),
  start: (body: { from?: string; to?: string; days?: number; entity?: string; limit?: number }) =>
    aiApi<{ run: ReviewRunDetail }>('ai/review', { method: 'POST', body: JSON.stringify(body) }),
  apply: (findingId: number, as: ApplyAs, text?: string) =>
    aiApi<{ ok: boolean; applied_as: ApplyAs; result: { message?: string }; finding: ReviewFinding }>(
      `ai/review/findings/${findingId}/apply`,
      { method: 'POST', body: JSON.stringify({ as, text }) },
    ),
}
