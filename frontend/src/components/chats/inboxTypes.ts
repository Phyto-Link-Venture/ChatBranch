/**
 * Shared types + tiny helpers for the /chats inbox (list pane, conversation,
 * info panel, filter modal). The wire is snake_case — these mirror the
 * backend's payloads key for key (GET /api/inbox/threads, the per-channel
 * thread detail, message rows).
 */
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { UiHandover } from '@/lib/aiControls'
import type { MessageKey, TFunction, useFormat } from '@/i18n'

/** The formatter bag `useFormat()` returns (helpers below take it as an argument). */
export type Fmt = ReturnType<typeof useFormat>

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type Channel =
  | 'whatsapp'
  | 'facebook'
  | 'instagram'
  | 'telegram'
  | 'email'
  | 'website'
  | 'line'
  | 'viber'
  | 'sms'
  | 'wechat'
  | 'tiktok'
  | 'douyin'
  | 'zalo'
export type ThreadStatus = 'open' | 'pending' | 'snoozed' | 'resolved'

export const CHANNELS: Channel[] = [
  'whatsapp',
  'facebook',
  'instagram',
  'telegram',
  'email',
  'website',
  'line',
  'viber',
  'sms',
  'wechat',
  'tiktok',
  'douyin',
  'zalo',
]

/** Channels with a platform reply window enforced server-side (WeChat 48 h, Zalo 7 d, TikTok / Douyin 48 h). */
export const WINDOWED_CHANNELS: Channel[] = ['wechat', 'zalo', 'tiktok', 'douyin']
export const THREAD_STATUSES: ThreadStatus[] = ['open', 'pending', 'snoozed', 'resolved']

export const CHANNEL_META: Record<Channel, { lbl: string; beta?: boolean }> = {
  whatsapp: { lbl: 'WhatsApp' },
  facebook: { lbl: 'Messenger', beta: true },
  instagram: { lbl: 'Instagram', beta: true },
  telegram: { lbl: 'Telegram' },
  email: { lbl: 'Email' },
  // The site widget: the visitor's browser is the transport (they always begin it).
  // Email / Website labels are translated by channelName().
  website: { lbl: 'Website' },
  line: { lbl: 'LINE' },
  viber: { lbl: 'Viber' },
  sms: { lbl: 'SMS' },
  wechat: { lbl: 'WeChat' },
  tiktok: { lbl: 'TikTok', beta: true },
  douyin: { lbl: 'Douyin', beta: true },
  zalo: { lbl: 'Zalo' },
}

/** Our `facebook` channel is Messenger — show Messenger's mark, not Facebook's. */
export const logoKey = (c: string) => (c === 'facebook' ? 'messenger' : c)

/** A channel's display name: brand names as they are, Email / Website translated. */
export const channelName = (t: TFunction, c: string): string =>
  c === 'email' || c === 'website' ? t(`chats.channels.${c}`) : (CHANNEL_META[c as Channel]?.lbl ?? c)

/** A conversation status in the current language (unknown values capitalised as they come). */
export const statusLabel = (t: TFunction, s: string): string =>
  (THREAD_STATUSES as string[]).includes(s)
    ? t(`chats.status.${s}` as MessageKey)
    : s.charAt(0).toUpperCase() + s.slice(1)

/** A teammate's role as a hint ("Agent", "Admin"); custom role names as they are. */
export const roleLabel = (t: TFunction, r: string): string =>
  r === 'superadmin' || r === 'admin' || r === 'agent' ? t(`chats.roles.${r}`) : r

export interface UiMedia {
  type: 'image' | 'document' | 'audio' | 'video'
  url: string
}

export interface UiMessage {
  id: string
  dir: 'in' | 'out'
  text: string
  ts: string
  status: string
  source: 'customer' | 'agent' | 'ai' | 'system'
  private?: boolean
  media?: UiMedia | null
  /** Which provider account carried this message (sender number for outbound). */
  account_id?: string | null
  account_name?: string
  /** The database id (reactions, quotes and AI feedback key on it). */
  pk?: number
  /** The message this one quotes (WhatsApp / Telegram replies). */
  reply_to?: UiQuote | null
  /** Emoji reactions on this message — the customer's and ours. */
  reactions?: UiReaction[]
}

/** A quoted message as the payload carries it; `missing` = older than our history. */
export interface UiQuote {
  pk?: number
  id: string
  dir?: 'in' | 'out'
  source?: string
  text?: string
  media_type?: string | null
  missing?: boolean
}

/** One reaction: `actor` is `contact` or `user:<username>`. */
export interface UiReaction {
  emoji: string
  actor: string
  at?: string
}

/** Channels whose provider carries quoted replies + reactions both ways. */
export const REACTION_CHANNELS: Channel[] = ['whatsapp', 'telegram']

export interface WaCall {
  permission: string
  call_id: string | null
  status: string
  room?: string | null
}

/** One inbox row — GET /api/inbox/threads. */
export interface UiThreadSummary {
  channel: Channel
  key: string
  name: string
  /** address line: phone / email / handle */
  sub: string
  ai_enabled: boolean
  /** Why the AI is on/off ("Off: AI is switched off for North Branch") — the AI switch's tooltip. */
  ai_reason?: string
  /** A rule above the per-chat tick decided (master switch, schedule, contact never…): the switch is disabled. */
  ai_forced?: boolean
  /** Human handover (null = never handed over). */
  handover?: UiHandover | null
  call?: WaCall
  status?: ThreadStatus
  assignee?: string
  team_id?: number | null
  team_name?: string
  labels?: string[]
  contact_id?: number | null
  contact_name?: string
  contact_tags?: string[]
  /** The conversation's entity slug (null = unassigned). */
  entity?: string | null
  account_id?: string | null
  account_name?: string
  provider?: 'cloud' | 'baileys' | null
  window_open?: boolean
  window_expires_at?: string | null
  /** Messenger / Instagram: a person may still reply (Meta's 7-day HUMAN_AGENT window) */
  human_window_open?: boolean
  human_window_expires_at?: string | null
  opted_out?: boolean
  waiting?: boolean
  has_call?: boolean
  last_message: string
  last_dir?: string
  last_status?: string
  last_ts: string
  /** Synthetic row for a contact with NO conversation yet — opening one
   *  creates the thread on demand. */
  pending?: boolean
  /** Set when this row stands in for several per-channel threads of the same
   *  linked contact, collapsed into one inbox entry. */
  _mergedChannels?: Channel[]
}

export interface UiThread extends Omit<UiThreadSummary, 'last_message' | 'last_ts'> {
  messages: UiMessage[]
}

/** The raw /api/inbox/threads row. */
export interface InboxRow {
  channel: Channel
  key: string
  name: string
  address: string
  status: ThreadStatus
  ai_enabled: boolean
  ai_reason?: string
  ai_forced?: boolean
  handover?: UiHandover | null
  assignee: string
  team_id: number | null
  team_name: string
  labels: string[]
  contact_id: number | null
  contact_name: string
  contact_tags: string[]
  entity?: string | null
  account_id: string | null
  account_name: string
  provider: 'cloud' | 'baileys' | null
  window_open: boolean
  window_expires_at: string | null
  opted_out: boolean
  waiting: boolean
  call: WaCall
  has_call: boolean
  last_message: string
  last_dir: string
  last_status: string
  /** the newest message is a private note (no delivery ticks) */
  last_private?: boolean
  last_ts: string
}

export const rowToSummary = (t: InboxRow): UiThreadSummary => ({
  channel: t.channel,
  key: t.key,
  // prefer the CRM name — a thread created by phone stores the number as its
  // name, but once matched to a contact the list should say who it is
  name: t.contact_name || t.name,
  sub: t.address,
  ai_enabled: t.ai_enabled,
  ai_reason: t.ai_reason,
  ai_forced: t.ai_forced,
  handover: t.handover ?? null,
  call: t.call,
  status: t.status,
  assignee: t.assignee,
  team_id: t.team_id,
  team_name: t.team_name,
  labels: t.labels,
  contact_id: t.contact_id,
  contact_name: t.contact_name,
  contact_tags: t.contact_tags,
  entity: t.entity ?? null,
  account_id: t.account_id,
  account_name: t.account_name,
  provider: t.provider,
  window_open: t.window_open,
  window_expires_at: t.window_expires_at,
  opted_out: t.opted_out,
  waiting: t.waiting,
  has_call: t.has_call,
  last_message: t.last_message,
  last_dir: t.last_dir,
  last_status: t.last_private ? '' : t.last_status,
  last_ts: t.last_ts,
})

export interface UiAgent {
  username: string
  name: string
  role: string
}

export interface UiTeam {
  id: number
  name: string
  auto_assign: boolean
}

export interface UiAccount {
  id: string
  name: string
  channel?: string
  provider?: string
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new ApiError((data as { detail?: string }).detail || `HTTP ${res.status}`, res.status)
  return data as T
}

/** An API refusal that keeps its HTTP status (a 404 opens "Conversation not found"). */
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message)
  }
}

/** Multipart POST — no Content-Type header so the browser sets the boundary. */
export async function apiUpload<T>(path: string, form: FormData): Promise<T> {
  const res = await fetch(`${API}/${path}`, { method: 'POST', headers: { ...authHeaders() }, body: form })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

/** Media URLs are authenticated GETs — absolute, host-relative or api-relative. */
export const resolveMediaUrl = (u: string) =>
  u.startsWith('http') ? u : u.startsWith('/') ? `${API.replace(/\/api$/, '')}${u}` : `${API}/${u}`

/** Per-thread API prefix — WhatsApp lives in its own app. */
export const threadPath = (t: { channel: Channel; key: string }) =>
  t.channel === 'whatsapp' ? `whatsapp/threads/${t.key}` : `messaging/threads/${t.channel}/${t.key}`

export const timeShort = (ts: string, f: Fmt) => (ts ? f.time(ts) : '')

/** List-row stamp: time today, weekday this week, date beyond. */
export const listStamp = (ts: string, f: Fmt) => {
  if (!ts) return ''
  const d = new Date(ts)
  const now = new Date()
  if (d.toDateString() === now.toDateString()) return f.time(ts)
  const days = (now.getTime() - d.getTime()) / 86_400_000
  if (days < 6) return f.weekday(ts)
  return f.date(ts)
}

export const timeAgo = (ts: string, f: Fmt) => f.relative(ts)

export const dayLabel = (ts: string, f: Fmt, t: TFunction) => {
  const d = new Date(ts)
  const today = new Date()
  const yest = new Date(today)
  yest.setDate(today.getDate() - 1)
  if (d.toDateString() === today.toDateString()) return t('common.time.today')
  if (d.toDateString() === yest.toDateString()) return t('common.time.yesterday')
  return f.date(ts)
}

export const initials = (name: string) =>
  name
    .replace(/[^\p{L}\p{N} +]/gu, '')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('') || '#'
