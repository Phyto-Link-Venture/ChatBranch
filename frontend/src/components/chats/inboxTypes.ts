/**
 * Shared types + tiny helpers for the /chats inbox (list pane, conversation,
 * info panel, filter modal). The wire is snake_case — these mirror the
 * backend's payloads key for key (GET /api/inbox/threads, the per-channel
 * thread detail, message rows).
 */
import { API, apiResource } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { UiHandover } from '@/lib/aiControls'
import type { MessageKey, TFunction, useFormat } from '@/i18n'

/** The formatter bag `useFormat()` returns (helpers below take it as an argument). */
export type Fmt = ReturnType<typeof useFormat>

export { API }

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
  // marketplace seller channels: buyer chat carried by the seller service
  | 'shopee'
  | 'lazada'
  | 'tiktok_shop'
  | 'shopify'
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
  'shopee',
  'lazada',
  'tiktok_shop',
  'shopify',
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
  // The shops are connected in the seller service, which holds the marketplace
  // grant; here they are ordinary inboxes. Shopify has no buyer-chat API, so a
  // Shopify shop never carries a conversation — it is listed for completeness.
  shopee: { lbl: 'Shopee', beta: true },
  lazada: { lbl: 'Lazada', beta: true },
  tiktok_shop: { lbl: 'TikTok Shop', beta: true },
  shopify: { lbl: 'Shopify', beta: true },
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
  /** Auth-gated (bearer) route — the fallback when no signed link was minted. */
  url: string
  mime?: string
  /** The original file name (or "{type}-{pk}.{ext}"). */
  name?: string
  /** Bytes; null when unknown. */
  size?: number | null
  /** Short-lived signed link (plain src, Range) — sessions only. */
  stream_url?: string
  /** The same link served as an attachment with the original name. */
  download_url?: string
  /** An AAC/M4A rendering of Ogg/Opus/AMR audio (servers with ffmpeg). */
  alt_url?: string
}

export interface UiMessage {
  id: string
  dir: 'in' | 'out'
  text: string
  ts: string
  status: string
  /** external_client = a person's reply from outside ChatBranch (a QR number's phone, a mail app's Sent folder). */
  source: 'customer' | 'agent' | 'ai' | 'system' | 'external_client'
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
  /** What a voice note says (speech-to-text), when it was transcribed. */
  transcript?: string | null
  /** Taken back on the channel ("delete for everyone"): kept = still readable here (the
   *  inbox keeps deleted messages), else its words and file were erased. */
  recalled?: UiRecall | null
  /** When WE edited this message on the channel (WhatsApp's ~15-minute edit).
   *  `text` is already the NEW wording — the old one lives in the activity log. */
  edited_at?: string | null
  /** An inbound / imported email shown as a mail card (the HTML is fetched by the card). */
  email?: UiEmail | null
  /** One of an email's extra attachments: the pk of the mail whose card shows it. */
  email_part_of?: number | null
}

export interface UiEmail {
  subject: string
  from: string
  to: string
  cc: string
  has_html: boolean
}

export interface UiRecall {
  at: string
  by: 'customer' | 'business'
  kept: boolean
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
  /** The quoted message was deleted on the channel. */
  recalled?: boolean
}

/** One reaction: `actor` is `contact`, `user:<username>` or `ai` (the AI's own, made with its reply). */
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

/** The company (top-level entity) a conversation belongs to. */
export interface UiCompany {
  id: number
  name: string
}

/**
 * One conversation. channel + key name the CUSTOMER; a customer has one
 * conversation PER COMPANY, so `thread_id` is what names the conversation
 * (absent only on a deep link that has not been resolved yet).
 */
export interface ThreadRef {
  channel: Channel
  key: string
  thread_id?: number | null
}

/** One inbox row — GET /api/inbox/threads. */
export interface UiThreadSummary {
  channel: Channel
  key: string
  /** The conversation's id — unique, unlike channel + key (one per company). */
  thread_id?: number | null
  /** Its company (null = unattributed). */
  company?: UiCompany | null
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
  provider?: 'cloud' | 'baileys' | 'ycloud' | 'twilio' | null
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
  /** The customer's newest message (null = they never wrote) — the
   *  "Newest from customer" order and its time stamp. */
  last_inbound_ts?: string | null
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
  thread_id?: number | null
  company?: UiCompany | null
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
  provider: 'cloud' | 'baileys' | 'ycloud' | 'twilio' | null
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
  /** the customer's newest inbound message, null when they never wrote */
  last_inbound_ts?: string | null
}

export const rowToSummary = (t: InboxRow): UiThreadSummary => ({
  channel: t.channel,
  key: t.key,
  thread_id: t.thread_id ?? null,
  company: t.company ?? null,
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
  last_inbound_ts: t.last_inbound_ts ?? null,
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
  if (!res.ok) throw new ApiError((data as { detail?: string }).detail || `HTTP ${res.status}`, res.status, data)
  return data as T
}

/** An API refusal that keeps its HTTP status (a 404 opens "Conversation not found"). */
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    /** The refusal's body (e.g. `{code, can_override}`). */
    public body: Record<string, unknown> = {},
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
export const resolveMediaUrl = (u: string) => apiResource(u)

/** Per-thread API prefix — WhatsApp lives in its own app. */
export const threadPath = (t: { channel: Channel; key: string }) =>
  t.channel === 'whatsapp' ? `whatsapp/threads/${t.key}` : `messaging/threads/${t.channel}/${t.key}`

/** `path` pinned to ONE conversation: `?thread_id=` (or `&`) when the id is known. */
export const withThreadId = (path: string, id?: number | null) =>
  id ? `${path}${path.includes('?') ? '&' : '?'}thread_id=${id}` : path

/** A per-thread API route (`suffix` = "/send", "/ai"…) pinned to that conversation. */
export const threadApi = (t: ThreadRef, suffix = '') => withThreadId(`${threadPath(t)}${suffix}`, t.thread_id)

/** A stable identity for one conversation in the list, a selection or a React key. */
export const threadIdentity = (t: { channel: string; key: string; thread_id?: number | null }) =>
  t.thread_id ? `${t.channel}:${t.key}#${t.thread_id}` : `${t.channel}:${t.key}`

/** Same conversation? By id when both sides know it, else by channel + key. */
export const sameThread = (
  a: { channel: string; key: string; thread_id?: number | null } | null | undefined,
  b: { channel: string; key: string; thread_id?: number | null } | null | undefined,
) => !!a && !!b && a.channel === b.channel && a.key === b.key && (!a.thread_id || !b.thread_id || a.thread_id === b.thread_id)

/** The /chats deep link to one conversation (`thread=channel:key` + `thread_id`). */
export const chatHref = (t: { channel: string; key: string; thread_id?: number | null }) =>
  `/chats?thread=${encodeURIComponent(`${t.channel}:${t.key}`)}${t.thread_id ? `&thread_id=${t.thread_id}` : ''}`

/** A 409 "this customer has a conversation with more than one company" candidate. */
export interface ThreadCandidate {
  thread_id: number
  name: string
  company: UiCompany | null
  entity: UiCompany | null
  account: UiCompany | null
  updated_at: string
}

export const timeShort = (ts: string, f: Fmt) => (ts ? f.time(ts) : '')

/** List-row stamp: time today, weekday this week, date beyond. */
export const listStamp = (ts: string, f: Fmt) => {
  if (!ts) return ''
  const days = f.daysAgo(ts) // calendar days in the zone times are shown in
  if (days === 0) return f.time(ts)
  if (days != null && days > 0 && days < 6) return f.weekday(ts)
  return f.date(ts)
}

export const timeAgo = (ts: string, f: Fmt) => f.relative(ts)

export const dayLabel = (ts: string, f: Fmt, t: TFunction) => {
  const days = f.daysAgo(ts)
  if (days === 0) return t('common.time.today')
  if (days === 1) return t('common.time.yesterday')
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
