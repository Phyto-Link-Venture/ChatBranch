/**
 * Client + types for the AI monitor (/api/ai/monitor) and the AI behaviour
 * settings (/api/ai/behaviour). Server is the source of truth for every label
 * and rule; this file only shapes the wire.
 */
import { authHeaders, expireSessionOn401 } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type DraftStatus =
  | 'pending'
  | 'approved'
  | 'edited'
  | 'rejected'
  | 'sent'
  | 'auto_sent'
  | 'expired'
  | 'failed'
  | 'shadow'

export type DraftKind = 'reply' | 'follow_up' | 'flow_message'

export interface RecentMessage {
  id: number
  direction: 'in' | 'out'
  source: string
  text: string
  at: string
}

export interface ToolCall {
  name?: string
  ok?: boolean
  result?: string
  arguments?: string
}

export interface CheckRun {
  name?: string
  passed?: boolean
  detail?: string
}

export interface DraftContext {
  answering?: { id: number; text: string; at: string }[]
  tools?: ToolCall[]
  checks?: CheckRun[]
  retries?: number
  handover?: boolean
  model?: string
  provider?: string
  prompt_tokens?: number
  completion_tokens?: number
  flow_run_id?: number | null
  flow_node?: string
  attempt?: number
  template?: string
  acknowledgement?: boolean
  flags?: string[]
  review_mode?: string
  [key: string]: unknown
}

export interface Draft {
  id: number
  kind: DraftKind
  status: DraftStatus
  status_label: string
  channel: string
  thread: {
    id: number
    key: string
    name: string
    status: string
    assignee: string
    ai_enabled: boolean
    opted_out: boolean
  } | null
  thread_key: string
  contact: { id: number; name: string } | null
  account: { id: number; name: string; provider: string } | null
  entity: { slug: string; name: string } | null
  content: string
  original_content: string
  hold_reason: string
  reasons: string[]
  flags: string[]
  context: DraftContext
  created_at: string
  updated_at: string
  send_after: string | null
  seconds_left: number | null
  decided_by: string
  decided_at: string | null
  reject_reason: string
  sent_at: string | null
  sent_message_id: number | null
  error: string
  superseded: boolean
  recent: RecentMessage[] | null
}

export interface DraftList {
  drafts: Draft[]
  total: number
  page: number
  page_size: number
  counts: Record<DraftStatus, number>
}

export interface DecisionResult {
  ok: boolean
  status: string
  detail: string
  draft: Draft | null
}

export interface MonitorStats {
  days: number
  total: number
  held: number
  pending: number
  approved: number
  edited: number
  rejected: number
  auto_sent: number
  sent_after_deadline: number
  expired: number
  failed: number
  shadow: number
  approval_rate: number | null
  edit_rate: number | null
  median_review_seconds: number | null
  by_kind: Record<DraftKind, number>
  top_reject_reasons: { reason: string; count: number }[]
  top_hold_reasons: { reason: string; count: number }[]
  daily: { date: string; auto_sent: number; held: number; rejected: number }[]
}

export interface BehaviourField {
  key: string
  group: string
  type: 'select' | 'number' | 'bool' | 'text' | 'textarea'
  label: string
  help: string
  options: string[] | null
  min: number | null
  max: number | null
  default: string
  value: string
  inherited_value: string
  scope: 'entity' | 'global' | 'default'
}

export interface BehaviourPayload {
  entity: string | null
  entity_name: string | null
  can_manage: boolean
  groups: { id: string; title: string; description: string }[]
  fields: BehaviourField[]
}

export class ApiError extends Error {
  status: number
  body: unknown
  constructor(message: string, status: number, body: unknown) {
    super(message)
    this.status = status
    this.body = body
  }
}

export async function aiApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers || {}) },
    cache: 'no-store',
  })
  if (expireSessionOn401(res)) throw new ApiError('Session expired', 401, null)
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string }
    throw new ApiError(d.detail || `HTTP ${res.status}`, res.status, data)
  }
  return data as T
}

export const decide = {
  approve: (id: number, force = false) =>
    aiApi<DecisionResult>(`ai/monitor/${id}/approve`, { method: 'POST', body: JSON.stringify({ force }) }),
  send: (id: number, content: string, force = false) =>
    aiApi<DecisionResult>(`ai/monitor/${id}/send`, { method: 'POST', body: JSON.stringify({ content, force }) }),
  reject: (id: number, reason: string) =>
    aiApi<DecisionResult>(`ai/monitor/${id}/reject`, { method: 'POST', body: JSON.stringify({ reason }) }),
}

/** The body of a 409 from approve/send/reject — still carries the draft. */
export function decisionFromError(e: unknown): DecisionResult | null {
  if (e instanceof ApiError && e.body && typeof e.body === 'object' && 'status' in e.body) {
    return e.body as DecisionResult
  }
  return null
}

export const STATUS_META: Record<DraftStatus, { label: string; tone: 'brand' | 'green' | 'amber' | 'red' | 'muted' | 'purple' }> = {
  pending: { label: 'Waiting for review', tone: 'amber' },
  approved: { label: 'Approved', tone: 'green' },
  edited: { label: 'Edited & sent', tone: 'green' },
  rejected: { label: 'Rejected', tone: 'red' },
  sent: { label: 'Sent after deadline', tone: 'brand' },
  auto_sent: { label: 'Sent automatically', tone: 'muted' },
  expired: { label: 'Superseded', tone: 'muted' },
  failed: { label: 'Not sent', tone: 'red' },
  shadow: { label: 'Shadow draft', tone: 'purple' },
}

export const KIND_LABEL: Record<DraftKind, string> = {
  reply: 'Reply',
  follow_up: 'Follow-up',
  flow_message: 'Flow message',
}

/** "2m", "3h", "4d" — compact age for dense rows. */
export function ago(iso: string | null | undefined, now = Date.now()): string {
  const t = parseUtc(iso)
  if (t == null) return ''
  const s = Math.max(0, Math.round((now - t) / 1000))
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.floor(s / 60)}m`
  if (s < 86400) return `${Math.floor(s / 3600)}h`
  return `${Math.floor(s / 86400)}d`
}

/** Server timestamps are UTC without an offset — parse them as such. */
export function parseUtc(iso: string | null | undefined): number | null {
  if (!iso) return null
  const t = Date.parse(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return Number.isNaN(t) ? null : t
}

/** 95 -> "1m 35s", 3610 -> "1h 0m", 42 -> "42s" */
export function duration(secs: number | null | undefined): string {
  if (secs == null) return '—'
  const s = Math.round(secs)
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60}s`
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
}
