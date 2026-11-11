/**
 * Client + types for the AI on/off controls and human-handover tracking:
 *   GET/PUT/DELETE /api/ai/controls?entity=&account=
 *                                           master switch · schedule · never-AI tags,
 *                                           org-wide, per outlet, or per inbox
 *   GET/PUT /api/contacts/{id}/ai-mode      "" | never | always
 *   POST    /api/inbox/threads/bulk         ai_on | ai_off | resolve | reopen | assign
 *   POST    /api/inbox/threads/{ch}/{key}/handover   take | continue | flag
 *   GET     /api/ai/handovers/stats?days=&entity=
 * The server owns every rule (Ai/AiEligibility, Ai/Handover); this file only
 * shapes the wire.
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from './auth'
import type { MessageKey, TFunction } from '@/i18n'

export class AiControlsError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export async function aiControlsApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers || {}) },
    cache: 'no-store',
  })
  if (expireSessionOn401(res)) throw new AiControlsError('Session expired', 401)
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new AiControlsError((data as { detail?: string }).detail || `HTTP ${res.status}`, res.status)
  return data as T
}

// ---- master switch + schedule ------------------------------------------------

export type AiMasterMode = 'on' | 'off' | 'schedule'
export type AiScheduleMode = 'inside' | 'outside'
export const WEEKDAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const
export type Weekday = (typeof WEEKDAYS)[number]
/** A date that falls on each weekday (5 Jan 2026 is a Monday) — format it with
 *  `useFormat().weekday()` for the localised short day name. */
export const weekdayDate = (d: Weekday) => new Date(2026, 0, 5 + WEEKDAYS.indexOf(d), 12)
/** {"mon": [["18:00","23:59"]], …} — the booking-hours shape. */
export type AiSchedule = Record<Weekday, [string, string][]>

/** The inbox (channel account) a controls payload was read for (`?account=`);
 *  null when the entity or organization level was read. */
export interface AiControlsAccount {
  id: number
  name: string
  channel: string
  provider: string
  /** The outlet that owns the inbox — the level it inherits from (null = the organization). */
  entity_id: number | null
  entity: string | null
  entity_name: string | null
  /** How many of the four values this inbox sets itself. */
  overrides: number
}

/** The AI master-switch keys, in the order the editor shows them. */
export type AiControlKey = 'ai_enabled' | 'ai_schedule' | 'ai_schedule_mode' | 'ai_never_tags'

/** Where a value on screen comes from — the same vocabulary as the AI behaviour fields. */
export type AiControlScope = 'account' | 'entity' | 'parent' | 'global' | 'default'

export interface AiControls {
  entity: string | null
  entity_name: string
  /** The inbox being edited (`?account=`), else null. */
  account: AiControlsAccount | null
  /** Which level this payload was read at. */
  level: 'organization' | 'entity' | 'account'
  /**
   * True on an inbox: its own values can only ever switch the AI OFF or narrow
   * its hours. An inbox never answers while the level above is off or outside
   * its schedule — the master switch is "off anywhere up the tree wins", not
   * nearest-level-wins like the AI behaviour settings.
   */
  own_only_turns_off: boolean
  mode: AiMasterMode
  /** account = set on this inbox · entity = on this outlet · inherited = a parent's · organization · default */
  mode_from: 'account' | 'entity' | 'inherited' | 'organization' | 'default'
  mode_set_at: string | null
  /** What the level on screen sets itself ('' / null / [] = it inherits). */
  own: {
    mode: AiMasterMode | ''
    schedule: AiSchedule | null
    schedule_mode: AiScheduleMode | ''
    never_tags: string[]
  }
  /** What this level would follow if it set nothing of its own. */
  inherited: {
    mode: AiMasterMode
    mode_from: 'entity' | 'inherited' | 'organization' | 'default'
    mode_set_at: string | null
    schedule: AiSchedule
    schedule_mode: AiScheduleMode
    never_tags: string[]
    /** Is the AI on at this moment at that level, the inbox left out? */
    now_on: boolean
  }
  /** Which level each value on screen came from. */
  scopes: Record<'mode' | 'schedule' | 'schedule_mode' | 'never_tags', AiControlScope>
  schedule: AiSchedule
  schedule_mode: AiScheduleMode
  /** In force here — never-AI tags ADD up, so an inbox's own are included. */
  never_tags: string[]
  inherited_never_tags: string[]
  /** The AI's state right now: on an inbox the tree AND the inbox must allow it. */
  now_on: boolean
  next_change_at: string | null
  timezone: string
  label: string
  can_manage: boolean
}

export interface AiControlsWrite {
  mode?: AiMasterMode | 'inherit'
  schedule?: AiSchedule | null
  schedule_mode?: AiScheduleMode | ''
  never_tags?: string[]
}

/**
 * The level a controls read/write targets: the organization (both empty), one
 * outlet, or one inbox. An inbox is absolute — it carries its own outlet, so
 * `?account=` is sent alone and the server reads/writes at that inbox's level
 * (the same rule as `?account=` on /api/ai/behaviour).
 */
export interface AiControlsLevel {
  entity?: string | null
  account?: number | null
}

/** An outlet slug (the older call style) or a level object. */
export type AiControlsTarget = string | null | undefined | AiControlsLevel

const q = (target: AiControlsTarget, extra: Record<string, string> = {}) => {
  const level: AiControlsLevel = typeof target === 'string' || target == null ? { entity: target } : target
  const p = new URLSearchParams()
  if (level.account) p.set('account', String(level.account))
  else if (level.entity) p.set('entity', level.entity)
  for (const [k, v] of Object.entries(extra)) p.set(k, v)
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const getAiControls = (target?: AiControlsTarget) => aiControlsApi<AiControls>(`ai/controls${q(target)}`)

export const putAiControls = (target: AiControlsTarget, body: AiControlsWrite) =>
  aiControlsApi<AiControls>(`ai/controls${q(target)}`, { method: 'PUT', body: JSON.stringify(body) })

/** Drop this level's own value(s) so it follows the level above again. */
export const resetAiControls = (target: AiControlsTarget, key?: AiControlKey) =>
  aiControlsApi<AiControls>(`ai/controls${q(target, key ? { key } : {})}`, { method: 'DELETE' })

export const emptySchedule = (): AiSchedule =>
  Object.fromEntries(WEEKDAYS.map((d) => [d, [] as [string, string][]])) as unknown as AiSchedule

/** "Mon–Fri 18:00–23:59 · Sat 10:00–14:00" — a compact summary for chips and
 *  subtitles; `dayLabel` names a weekday in the current language. */
export function describeSchedule(
  s: AiSchedule | null | undefined,
  t: TFunction,
  dayLabel: (d: Weekday) => string,
): string {
  if (!s) return t('chats.aiControls.noHoursSet')
  const groups: { days: Weekday[]; key: string }[] = []
  for (const d of WEEKDAYS) {
    const w = s[d] ?? []
    if (!w.length) continue
    const key = w.map(([a, b]) => `${a}–${b}`).join(', ')
    const last = groups[groups.length - 1]
    const prevDay = last ? WEEKDAYS[WEEKDAYS.indexOf(last.days[last.days.length - 1]) + 1] : null
    if (last && last.key === key && prevDay === d) last.days.push(d)
    else groups.push({ days: [d], key })
  }
  if (!groups.length) return t('chats.aiControls.noHoursSet')
  return groups
    .map((g) => {
      const days =
        g.days.length === 1
          ? dayLabel(g.days[0])
          : t('chats.aiControls.dayRange', { from: dayLabel(g.days[0]), to: dayLabel(g.days[g.days.length - 1]) })
      return t('chats.aiControls.dayHours', { days, hours: g.key === '00:00–00:00' ? t('chats.aiControls.allDay') : g.key })
    })
    .join(' · ')
}

// ---- contact AI mode ------------------------------------------------------------

export type ContactAiMode = '' | 'never' | 'always'

export interface ContactAiModePayload {
  contact_id: number
  mode: ContactAiMode
  never_tag: string | null
  effective: 'follow' | 'never' | 'always'
  can_manage: boolean
}

export const getContactAiMode = (id: number) => aiControlsApi<ContactAiModePayload>(`contacts/${id}/ai-mode`)
export const putContactAiMode = (id: number, mode: ContactAiMode) =>
  aiControlsApi<ContactAiModePayload>(`contacts/${id}/ai-mode`, { method: 'PUT', body: JSON.stringify({ mode }) })

// ---- handover --------------------------------------------------------------------

export type HandoverKind =
  | 'check_failed'
  | 'asked_for_human'
  | 'send_blocked'
  | 'booking_failed'
  | 'low_confidence'
  | 'rule'
  | 'manual'
  | 'unknown_customer'
  | 'quota'
  | 'credit'

/** Labels are translation keys — render with `t(k.label)`. */
export const HANDOVER_KINDS: { value: HandoverKind; label: MessageKey }[] = [
  { value: 'check_failed', label: 'chats.handoverKinds.check_failed' },
  { value: 'asked_for_human', label: 'chats.handoverKinds.asked_for_human' },
  { value: 'send_blocked', label: 'chats.handoverKinds.send_blocked' },
  { value: 'booking_failed', label: 'chats.handoverKinds.booking_failed' },
  { value: 'low_confidence', label: 'chats.handoverKinds.low_confidence' },
  { value: 'rule', label: 'chats.handoverKinds.rule' },
  { value: 'manual', label: 'chats.handoverKinds.manual' },
  { value: 'unknown_customer', label: 'chats.handoverKinds.unknown_customer' },
  { value: 'quota', label: 'chats.handoverKinds.quota' },
  { value: 'credit', label: 'chats.handoverKinds.credit' },
]

/** A handover kind's label in the current language; the server's own label for kinds we don't know. */
export const handoverKindLabel = (t: TFunction, kind: string, fallback?: string) => {
  const k = HANDOVER_KINDS.find((x) => x.value === kind)
  return k ? t(k.label) : fallback || kind.replace(/_/g, ' ')
}

/** The `handover` object on every thread payload (null = never handed over). */
export interface UiHandover {
  open: boolean
  at: string
  reason: string
  kind: HandoverKind | string
  kind_label: string
  /** ai | rule:<id> | flow | agent:<username> */
  by: string
  resolved_at: string | null
  count: number
}

export const handoverBy = (by: string, t: TFunction) =>
  by === 'ai'
    ? t('chats.handover.byAi')
    : by === 'flow'
      ? t('chats.handover.byFlow')
      : by.startsWith('rule:')
        ? t('chats.handover.byRule')
        : by.startsWith('agent:')
          ? by.slice(6)
          : by

export interface HandoverActionResult {
  ok: boolean
  status: string
  assignee: string
  ai_enabled: boolean
  ai_reason: string
  ai_forced: boolean
  /** "continue": the AI was queued to answer the customer's waiting message */
  ai_reply_queued?: boolean
  handover: UiHandover | null
}

export const handoverAction = (
  channel: string,
  key: string,
  action: 'take' | 'continue' | 'flag',
  reason?: string,
  /** which company's conversation (a customer has one per company) */
  threadId?: number | null,
) =>
  aiControlsApi<HandoverActionResult>(
    `inbox/threads/${channel}/${encodeURIComponent(key)}/handover${threadId ? `?thread_id=${threadId}` : ''}`,
    {
      method: 'POST',
      body: JSON.stringify({ action, reason }),
    },
  )

export interface HandoverStats {
  days: number
  total: number
  open: number
  /** conversations the AI wrote in during the window */
  ai_conversations: number
  /** distinct conversations handed over during the window */
  handed_over_conversations: number
  /** the rate's base: AI conversations ∪ handed-over ones */
  conversations: number
  rate: number | null
  avg_minutes_to_human: number | null
  by_kind: { kind: string; label: string; count: number }[]
  by_source: { by: string; count: number }[]
  series: { day: string; handovers: number; ai_conversations: number; rate: number | null }[]
}

// ---- bulk ---------------------------------------------------------------------------

export type BulkAction = 'ai_on' | 'ai_off' | 'resolve' | 'reopen' | 'assign'

export interface BulkResult {
  ok: boolean
  action: BulkAction
  matched: number
  changed: number
  skipped: string[]
}

/** Picked conversations (by id — a customer has one per company — or the older
 *  `channel:key`) or everything the list's current filter matches. */
export const bulkThreads = (
  target: { thread_ids: number[] } | { threads: string[] } | { filter: Record<string, string> },
  action: BulkAction,
  value?: string,
) =>
  aiControlsApi<BulkResult>('inbox/threads/bulk', {
    method: 'POST',
    body: JSON.stringify({ ...target, action, value }),
  })

/** URLSearchParams of the list request → the bulk endpoint's filter object. */
export function paramsToFilter(params: URLSearchParams): Record<string, string> {
  const out: Record<string, string> = {}
  params.forEach((v, k) => {
    if (!['limit', 'cursor', 'count_only', 'before'].includes(k)) out[k] = v
  })
  return out
}
