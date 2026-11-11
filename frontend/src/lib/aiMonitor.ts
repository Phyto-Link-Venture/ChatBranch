/**
 * Client + types for the AI monitor (/api/ai/monitor) and the AI behaviour
 * settings (/api/ai/behaviour), which are read and written at one of three
 * levels: the organization, one entity (`?entity=`) or one inbox
 * (`?account=`). Server is the source of truth for every label and rule; this
 * file only shapes the wire.
 */
import { API } from '@/lib/apiBase'
import { translate, type MessageKey, type TFunction } from '@/i18n'
import { en } from '@/i18n/messages/en'
import { authHeaders, expireSessionOn401 } from './auth'

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

/** One reply check (AiReplyResult.ToMeta: ok/reason/attempt; older rows: passed/detail). */
export interface CheckRun {
  name?: string
  ok?: boolean
  passed?: boolean
  reason?: string
  detail?: string
  fix?: string
  attempt?: number
}

export interface DraftContext {
  answering?: { id: number; text: string; at: string }[]
  tools?: ToolCall[]
  checks?: CheckRun[]
  retries?: number
  handover?: boolean
  /** send | handover | block — what the assistant itself decided. */
  action?: string
  reason?: string
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
  /** Calendar reservations this reply carries (from context.pending_actions):
   *  approving/sending it MAKES them; rejecting releases them. */
  bookings?: DraftBooking[]
  /** Files this reply's tools made (Documents PDFs, QR codes) — sent right after
   *  its text on delivery; a rejected reply voids them. */
  documents?: DraftDocument[]
  /** The emoji reaction this reply makes on a customer message — added on delivery, before
   *  the text (`done` once made); a held / rejected reply never reacts. */
  reactions?: DraftReaction[]
  /** The customer message the reply's first bubble quotes (WhatsApp / Telegram). */
  quote?: { message_id: number; text: string } | null
  /** A reply in parts: each bubble and the customer message it quotes ([] = one message). */
  parts?: { text: string; quote: { message_id: number; text: string } | null }[]
  /** AI voice replies: '' | planned (would go as a voice note) | sent | text (fell back — voice_error says why). */
  voice?: '' | 'planned' | 'sent' | 'text'
  voice_error?: string
}

export interface DraftReaction {
  message_id: number
  emoji: string
  /** The customer message it goes on. */
  text: string
  done: boolean
  /** Why the provider refused it (the reply's text still went). */
  error: string
}

/** One document a draft carries (`DocumentService.Payload`). `url` is auth-gated. */
export interface DraftDocument {
  id: number
  type: 'invoice' | 'quotation' | 'receipt' | 'letter' | 'qr' | string
  label: string
  number: string
  status: 'pending' | 'sent' | 'void' | 'failed' | 'issued' | string
  currency: string
  total: number | null
  file_name: string
  url: string
  detail: string
}

/** One booking a draft will make or cancel when delivered. `status` is the
 *  booking's own (pending/cancelling while held; booked/cancelled/released/failed after). */
export interface DraftBooking {
  type: 'book' | 'cancel'
  booking_id: number
  label: string
  status: string
  calendar_label: string
}

/** "Will book" / "Booked" / "Not booked" … — the line a reviewer reads (a message key). */
export function draftBookingVerb(b: DraftBooking): { verb: MessageKey; tone: 'pending' | 'done' | 'off' | 'failed' } {
  if (b.type === 'cancel') {
    if (b.status === 'cancelled') return { verb: 'ai.draft.booking.cancelled', tone: 'done' }
    if (b.status === 'failed') return { verb: 'ai.draft.booking.cancelFailed', tone: 'failed' }
    if (b.status === 'booked') return { verb: 'ai.draft.booking.notCancelled', tone: 'off' }
    return { verb: 'ai.draft.booking.willCancel', tone: 'pending' }
  }
  if (b.status === 'booked') return { verb: 'ai.draft.booking.booked', tone: 'done' }
  if (b.status === 'cancelled' || b.status === 'cancelling') return { verb: 'ai.draft.booking.bookedSinceCancelled', tone: 'off' }
  if (b.status === 'released') return { verb: 'ai.draft.booking.notBooked', tone: 'off' }
  if (b.status === 'failed') return { verb: 'ai.draft.booking.bookingFailed', tone: 'failed' }
  return { verb: 'ai.draft.booking.willBook', tone: 'pending' }
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
  /** What this level would inherit: the entity/organization resolved value when
   *  an inbox is read, the parent's (else the organization's) otherwise. */
  inherited_value: string
  /** Where the value on screen comes from: this inbox's own override, the
   *  entity's own row, an ancestor entity, the organization, or the engine. */
  scope: 'account' | 'entity' | 'parent' | 'global' | 'default'
}

/** The inbox (channel account) a behaviour payload was read for (`?account=`);
 *  null when the entity or organization level was read. */
export interface BehaviourAccount {
  id: number
  name: string
  channel: string
  provider: string
  /** The outlet that owns the inbox — the level it inherits from (null = the organization). */
  entity_id: number | null
  /** How many settings this inbox overrides. */
  overrides: number
}

export interface BehaviourPayload {
  entity: string | null
  entity_name: string | null
  account: BehaviourAccount | null
  can_manage: boolean
  groups: { id: string; title: string; description: string }[]
  fields: BehaviourField[]
}

/**
 * The level a behaviour read/write targets: the organization (both empty), one
 * entity, or one inbox. An inbox is absolute — it carries its own entity, so
 * `?account=` is sent alone and the server reads/writes at that inbox's level.
 */
export interface BehaviourLevel {
  entity?: string | null
  account?: number | null
}

/** `?account=…` for an inbox, else `?entity=…`, else "" (the organization). */
export function behaviourQuery(level: BehaviourLevel, extra: Record<string, string> = {}): string {
  const p = new URLSearchParams()
  if (level.account) p.set('account', String(level.account))
  else if (level.entity) p.set('entity', level.entity)
  for (const [k, v] of Object.entries(extra)) p.set(k, v)
  const q = p.toString()
  return q ? `?${q}` : ''
}

/** One inbox the AI behaviour can be tuned on (the picker shape of
 *  GET /api/whatsapp/accounts). */
export interface BehaviourInbox {
  id: number
  name: string
  channel: string
  provider: string
  /** Owning outlet's slug; null = an inbox the whole organization shares. */
  entity: string | null
  entity_name: string
}

/**
 * Every inbox this viewer can see, in channel + name order — numbers,
 * mailboxes, bots, pages and website widgets alike, since a widget IS a
 * channel account (`?channel=all` on the one accounts endpoint the channels
 * page and the chats sender picker already read).
 */
export async function listBehaviourInboxes(): Promise<BehaviourInbox[]> {
  const { accounts } = await aiApi<{ accounts: Record<string, unknown>[] }>('whatsapp/accounts?channel=all')
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  return (accounts ?? [])
    // the .env-managed pseudo-account has no row to override (its id is not a pk)
    .filter((a) => /^\d+$/.test(str(a.id)))
    .map((a) => ({
      id: Number(str(a.id)),
      name: str(a.name).trim() || `#${str(a.id)}`,
      channel: str(a.channel),
      provider: str(a.provider),
      entity: str(a.entity) || null,
      entity_name: str(a.entity_name),
    }))
    .sort((a, b) => a.channel.localeCompare(b.channel) || a.name.localeCompare(b.name))
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

/** Status chip: a message key (translated at render) + its tone. */
export const STATUS_META: Record<DraftStatus, { label: MessageKey; tone: 'brand' | 'green' | 'amber' | 'red' | 'muted' | 'purple' }> = {
  pending: { label: 'ai.draftStatus.pending', tone: 'amber' },
  approved: { label: 'ai.draftStatus.approved', tone: 'green' },
  edited: { label: 'ai.draftStatus.edited', tone: 'green' },
  rejected: { label: 'ai.draftStatus.rejected', tone: 'red' },
  sent: { label: 'ai.draftStatus.sent', tone: 'brand' },
  auto_sent: { label: 'ai.draftStatus.autoSent', tone: 'muted' },
  expired: { label: 'ai.draftStatus.expired', tone: 'muted' },
  failed: { label: 'ai.draftStatus.failed', tone: 'red' },
  shadow: { label: 'ai.draftStatus.shadow', tone: 'purple' },
}

/** Draft kind → message key of its name. */
export const KIND_LABEL: Record<DraftKind, MessageKey> = {
  reply: 'ai.draftKind.reply',
  follow_up: 'ai.draftKind.followUp',
  flow_message: 'ai.draftKind.flowMessage',
}

/** Draft kind → message key of "{count} replies" (plural pair). */
export const KIND_COUNT: Record<DraftKind, MessageKey> = {
  reply: 'ai.draftKind.replyCount',
  follow_up: 'ai.draftKind.followUpCount',
  flow_message: 'ai.draftKind.flowMessageCount',
}

/** The one-tap reject reasons of the review queue. */
export const QUICK_REJECT_REASONS: MessageKey[] = [
  'ai.review.quickReasons.wrongInfo',
  'ai.review.quickReasons.promised',
  'ai.review.quickReasons.tone',
  'ai.review.quickReasons.person',
]

/** A quick reason is STORED in English, so the stats group it the same whatever language the reviewer reads. */
export function quickReasonWire(k: MessageKey): string {
  return translate(en, 'en', k)
}

/** What to store for a typed/picked reason: a quick reason in any language → its English wording. */
export function rejectReasonWire(text: string, t: TFunction): string {
  const k = QUICK_REJECT_REASONS.find((q) => t(q) === text || quickReasonWire(q) === text)
  return k ? quickReasonWire(k) : text
}

/** A stored reject reason in the reader's language when it is one of the quick reasons (else as written). */
export function rejectReasonLabel(text: string, t: TFunction): string {
  const k = QUICK_REJECT_REASONS.find((q) => quickReasonWire(q) === text)
  return k ? t(k) : text
}

/** Server timestamps are UTC without an offset — parse them as such. */
export function parseUtc(iso: string | null | undefined): number | null {
  if (!iso) return null
  const t = Date.parse(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return Number.isNaN(t) ? null : t
}

/** 95 -> "1m 35s", 3610 -> "1h 0m", 42 -> "42s" (units translated). */
export function duration(secs: number | null | undefined, t: TFunction): string {
  if (secs == null) return '—'
  const s = Math.round(secs)
  if (s < 60) return t('ai.units.seconds', { s })
  if (s < 3600) return t('ai.units.minSec', { m: Math.floor(s / 60), s: s % 60 })
  return t('ai.units.hourMin', { h: Math.floor(s / 3600), m: Math.floor((s % 3600) / 60) })
}
