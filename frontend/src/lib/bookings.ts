/**
 * Client + types for calendar bookings (GET /api/bookings,
 * POST /api/bookings/{id}/cancel). A booking is made by an AI calendar skill
 * ("Connected app" on /skills): RESERVED (`pending`) while the AI writes, made
 * (`booked`) when the reply is delivered, `released` when a held reply is
 * rejected/superseded, `failed` when the calendar refused at delivery. A cancel
 * goes `cancelling` → `cancelled` the same way.
 *
 * Every caller fails soft: a missing endpoint (an older API) or no access
 * reads as "no bookings", never a crash.
 */
import { authHeaders, expireSessionOn401 } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export type BookingStatus = 'pending' | 'booked' | 'cancelling' | 'cancelled' | 'released' | 'failed'

export interface Booking {
  id: number
  contact_id: number | null
  contact_name: string
  /** "channel:key" of the conversation it was booked from, or null. */
  thread: string | null
  /** UTC instants (ISO; an offset-less value is UTC, like every server timestamp). */
  start: string
  end: string
  timezone: string
  /** Human label in the calendar's timezone, e.g. "Tue 14 Oct, 3:00 PM". */
  label: string
  title: string
  status: BookingStatus
  source: 'google' | 'chatbranch'
  calendar_label: string
  event_link: string | null
  /** "ai" or "agent:<username>". */
  created_by: string
  skill_id: string
  draft_id: number | null
  /** Why it failed / was released (empty otherwise). */
  detail: string
  created_at: string
}

export interface BookingQuery {
  contact_id?: number
  /** "channel:key" */
  thread?: string
  status?: string
  /** ISO instants. */
  from?: string
  to?: string
  /** Soonest-first future bookings; otherwise newest-first. */
  upcoming?: boolean
  /** `entity=<slug>` from useEntityQuery() — '' for All entities. */
  entityQuery?: string
}

export class BookingsError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
    cache: 'no-store',
  })
  if (expireSessionOn401(res)) throw new BookingsError('Session expired', 401)
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new BookingsError((data as { detail?: string }).detail || `HTTP ${res.status}`, res.status)
  return data as T
}

export async function listBookings(q: BookingQuery = {}): Promise<Booking[]> {
  const p = new URLSearchParams()
  if (q.contact_id != null) p.set('contact_id', String(q.contact_id))
  if (q.thread) p.set('thread', q.thread)
  if (q.status) p.set('status', q.status)
  if (q.from) p.set('from', q.from)
  if (q.to) p.set('to', q.to)
  if (q.upcoming) p.set('upcoming', '1')
  const qs = [p.toString(), q.entityQuery ?? ''].filter(Boolean).join('&')
  const d = await call<{ bookings?: Booking[] }>(`bookings${qs ? `?${qs}` : ''}`)
  return Array.isArray(d.bookings) ? d.bookings : []
}

/** Operator cancel — cancels now (the Google event is deleted), notes + audits it. */
export function cancelBooking(id: number) {
  return call<{ ok?: boolean; detail?: string; booking?: Booking }>(`bookings/${id}/cancel`, { method: 'POST', body: '{}' })
}

/** "No such endpoint / not allowed" — show the empty state, not an error. */
export const isQuietError = (e: unknown) => e instanceof BookingsError && [403, 404, 405].includes(e.status)

export const BOOKING_STATUS: Record<
  BookingStatus,
  { label: string; tone: 'green' | 'amber' | 'red' | 'muted'; hint: string }
> = {
  pending: { label: 'Reserved', tone: 'amber', hint: 'Held for an AI reply that hasn’t been sent yet — booked when it is.' },
  booked: { label: 'Booked', tone: 'green', hint: 'On the calendar.' },
  cancelling: { label: 'Cancelling', tone: 'amber', hint: 'Cancelled when the AI reply waiting for review is sent.' },
  cancelled: { label: 'Cancelled', tone: 'muted', hint: 'Cancelled — the time is free again.' },
  released: { label: 'Released', tone: 'muted', hint: 'The AI reply was never sent, so nothing was booked.' },
  failed: { label: 'Failed', tone: 'red', hint: 'The calendar refused the booking when the reply was due.' },
}

export const ACTIVE_STATUSES: BookingStatus[] = ['pending', 'booked', 'cancelling']

/** "AI" or the agent's username. */
export function bookedBy(createdBy: string): string {
  if (!createdBy || createdBy === 'ai') return 'AI'
  return createdBy.startsWith('agent:') ? createdBy.slice(6) : createdBy
}

/** Deep link into the inbox for a booking's conversation. */
export const threadHref = (thread: string) => `/chats?thread=${encodeURIComponent(thread)}`

/** Server timestamps without an offset are UTC. */
export function instant(iso: string | null | undefined): number | null {
  if (!iso) return null
  const t = Date.parse(/[zZ]$|[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return Number.isNaN(t) ? null : t
}

/** "in 3 days", "in 2 h", "5 days ago" — relative to now. */
export function relative(iso: string, now = Date.now()): string {
  const t = instant(iso)
  if (t == null) return ''
  const diff = t - now
  const abs = Math.abs(diff)
  const mins = Math.round(abs / 60000)
  const v =
    mins < 60
      ? `${Math.max(1, mins)} min`
      : mins < 60 * 36
        ? `${Math.round(mins / 60)} h`
        : `${Math.round(mins / 1440)} day${Math.round(mins / 1440) === 1 ? '' : 's'}`
  return diff >= 0 ? `in ${v}` : `${v} ago`
}

/** A booking's display label when the server sent none. */
export function bookingLabel(b: Pick<Booking, 'label' | 'start' | 'timezone'>): string {
  if (b.label) return b.label
  const t = instant(b.start)
  if (t == null) return b.start
  try {
    return new Date(t).toLocaleString('en-GB', {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      hour: 'numeric',
      minute: '2-digit',
      timeZone: b.timezone || undefined,
    })
  } catch {
    return new Date(t).toLocaleString()
  }
}
