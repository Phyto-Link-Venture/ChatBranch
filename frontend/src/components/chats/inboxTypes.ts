/**
 * Shared types + tiny helpers for the /chats inbox (list pane, conversation,
 * info panel, filter modal). The wire is snake_case — these mirror the
 * backend's payloads key for key (GET /api/inbox/threads, the per-channel
 * thread detail, message rows).
 */
import { authHeaders, expireSessionOn401 } from '@/lib/auth'

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type Channel = 'whatsapp' | 'facebook' | 'instagram' | 'telegram' | 'email' | 'website'
export type ThreadStatus = 'open' | 'pending' | 'snoozed' | 'resolved'

export const CHANNELS: Channel[] = ['whatsapp', 'facebook', 'instagram', 'telegram', 'email', 'website']
export const THREAD_STATUSES: ThreadStatus[] = ['open', 'pending', 'snoozed', 'resolved']

export const CHANNEL_META: Record<Channel, { lbl: string; placeholder: string; beta?: boolean }> = {
  whatsapp: { lbl: 'WhatsApp', placeholder: '+60 phone number' },
  facebook: { lbl: 'Messenger', placeholder: 'Messenger PSID', beta: true },
  instagram: { lbl: 'Instagram', placeholder: 'Instagram IGSID', beta: true },
  telegram: { lbl: 'Telegram', placeholder: 'Telegram chat ID' },
  email: { lbl: 'Email', placeholder: 'customer@email.com' },
  // The site widget: the visitor's browser is the transport, so there is no
  // address to start a conversation FROM our side — they always begin it.
  website: { lbl: 'Website', placeholder: 'started by the visitor' },
}

/** Our `facebook` channel is Messenger — show Messenger's mark, not Facebook's. */
export const logoKey = (c: string) => (c === 'facebook' ? 'messenger' : c)

export const statusLabel = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

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
}

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
  call?: WaCall
  status?: ThreadStatus
  assignee?: string
  team_id?: number | null
  team_name?: string
  labels?: string[]
  contact_id?: number | null
  contact_name?: string
  contact_tags?: string[]
  account_id?: string | null
  account_name?: string
  provider?: 'cloud' | 'baileys' | null
  window_open?: boolean
  window_expires_at?: string | null
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
  assignee: string
  team_id: number | null
  team_name: string
  labels: string[]
  contact_id: number | null
  contact_name: string
  contact_tags: string[]
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
  call: t.call,
  status: t.status,
  assignee: t.assignee,
  team_id: t.team_id,
  team_name: t.team_name,
  labels: t.labels,
  contact_id: t.contact_id,
  contact_name: t.contact_name,
  contact_tags: t.contact_tags,
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
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
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

export const timeShort = (ts: string) =>
  ts ? new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''

/** List-row stamp: time today, weekday this week, date beyond. */
export const listStamp = (ts: string) => {
  if (!ts) return ''
  const d = new Date(ts)
  const now = new Date()
  if (d.toDateString() === now.toDateString()) return timeShort(ts)
  const days = (now.getTime() - d.getTime()) / 86_400_000
  if (days < 6) return d.toLocaleDateString([], { weekday: 'short' })
  return d.toLocaleDateString([], { day: 'numeric', month: 'short' })
}

export const timeAgo = (ts: string) => {
  const mins = Math.max(0, Math.round((Date.now() - new Date(ts).getTime()) / 60000))
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins}m ago`
  const hours = Math.round(mins / 60)
  return hours < 24 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`
}

export const dayLabel = (ts: string) => {
  const d = new Date(ts)
  const today = new Date()
  const yest = new Date(today)
  yest.setDate(today.getDate() - 1)
  if (d.toDateString() === today.toDateString()) return 'Today'
  if (d.toDateString() === yest.toDateString()) return 'Yesterday'
  return d.toLocaleDateString([], { day: 'numeric', month: 'short', year: 'numeric' })
}

export const initials = (name: string) =>
  name
    .replace(/[^\p{L}\p{N} +]/gu, '')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('') || '#'
