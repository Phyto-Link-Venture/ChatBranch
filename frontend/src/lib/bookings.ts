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
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from './auth'
import type { MessageKey } from '@/i18n'

export type BookingStatus = 'pending' | 'booked' | 'cancelling' | 'cancelled' | 'released' | 'failed'

export interface Booking {
  id: number
  contact_id: number | null
  contact_name: string
  /** "channel:key" of the conversation it was booked from, or null. */
  thread: string | null
  /** its id — a customer has one conversation per company under that channel:key */
  thread_id?: number | null
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
  /** The video meeting made with it (while it stands): zoom | google_meet | link (the skill's own). */
  meeting_provider?: 'zoom' | 'google_meet' | 'link' | null
  meeting_url?: string | null
  /** When its link was sent to the customer (null = not yet). */
  meeting_sent_at?: string | null
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
  /** At most this many (the API's default 200, up to 500). */
  limit?: number
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
  if (q.limit) p.set('limit', String(q.limit))
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

/** Label and hint are translation keys — render with `t(m.label)`. */
export const BOOKING_STATUS: Record<
  BookingStatus,
  { label: MessageKey; tone: 'green' | 'amber' | 'red' | 'muted'; hint: MessageKey }
> = {
  pending: { label: 'bookings.status.pending', tone: 'amber', hint: 'bookings.statusHint.pending' },
  booked: { label: 'bookings.status.booked', tone: 'green', hint: 'bookings.statusHint.booked' },
  cancelling: { label: 'bookings.status.cancelling', tone: 'amber', hint: 'bookings.statusHint.cancelling' },
  cancelled: { label: 'bookings.status.cancelled', tone: 'muted', hint: 'bookings.statusHint.cancelled' },
  released: { label: 'bookings.status.released', tone: 'muted', hint: 'bookings.statusHint.released' },
  failed: { label: 'bookings.status.failed', tone: 'red', hint: 'bookings.statusHint.failed' },
}

export const ACTIVE_STATUSES: BookingStatus[] = ['pending', 'booked', 'cancelling']

/** "AI" or the agent's username. */
export function bookedBy(createdBy: string): string {
  if (!createdBy || createdBy === 'ai') return 'AI'
  return createdBy.startsWith('agent:') ? createdBy.slice(6) : createdBy
}

/** Deep link into the inbox for a booking's conversation. */
export const threadHref = (thread: string, threadId?: number | null) =>
  `/chats?thread=${encodeURIComponent(thread)}${threadId ? `&thread_id=${threadId}` : ''}`

/** Server timestamps without an offset are UTC. */
export function instant(iso: string | null | undefined): number | null {
  if (!iso) return null
  const t = Date.parse(/[zZ]$|[+-]\d\d:\d\d$/.test(iso) ? iso : `${iso}Z`)
  return Number.isNaN(t) ? null : t
}

/**
 * A booking's display label — "Tue 14 Oct, 15:00" in the calendar's timezone,
 * in the dashboard's language (`intl` = the locale's BCP 47 tag). Falls back to
 * the server's own label when the start can't be read.
 */
export function bookingLabel(b: Pick<Booking, 'label' | 'start' | 'timezone'>, intl: string): string {
  const t = instant(b.start)
  if (t == null) return b.label || b.start
  const o: Intl.DateTimeFormatOptions = { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
  try {
    return new Intl.DateTimeFormat(intl, { ...o, timeZone: b.timezone || undefined }).format(t)
  } catch {
    return b.label || new Intl.DateTimeFormat(intl, o).format(t) // unknown timezone
  }
}

// ---- the connected Google Calendar, read-only (the page's Calendar tab) -------------

/** A Google Calendar connection the caller may read: their own, or a shared one. */
export interface CalendarConnection {
  id: number
  label: string
  scope: 'personal' | 'entity' | 'organization'
  entity_name: string | null
  status: string
}

/** One event as Google has it (never its description or attendees). */
export interface CalendarEvent {
  id: string
  title: string
  all_day: boolean
  /** Timed: an ISO instant with the calendar's offset; all-day: "YYYY-MM-DD". */
  start: string
  /** Timed: an ISO instant; all-day: the day AFTER the last one (Google's exclusive end). */
  end: string | null
  /** The local day (YYYY-MM-DD) it is listed under. */
  day: string
  location: string
  link: string | null
  /** Shown as free ("transparent") on Google. */
  free: boolean
  /** ChatBranch booked it — `booking` is filled when its conversation is in your scope. */
  chatbranch: boolean
  booking: Booking | null
}

export interface CalendarEventsResponse {
  ok: boolean
  /** no_calendar | not_connected | google */
  error?: string
  detail?: string
  connection?: CalendarConnection
  calendar_id?: string
  calendars?: { id: string; name: string; primary: boolean }[]
  timezone?: string
  from?: string
  to?: string
  truncated?: boolean
  events: CalendarEvent[]
}

export async function listCalendarConnections(entityQuery = ''): Promise<CalendarConnection[]> {
  const d = await call<{ calendars?: CalendarConnection[] }>(`bookings/calendars${entityQuery ? `?${entityQuery}` : ''}`)
  return Array.isArray(d.calendars) ? d.calendars : []
}

export async function calendarEvents(q: {
  connection?: number
  calendar?: string
  /** YYYY-MM-DD, inclusive. */
  from: string
  to: string
  entityQuery?: string
}): Promise<CalendarEventsResponse> {
  const p = new URLSearchParams({ from: q.from, to: q.to })
  if (q.connection != null) p.set('connection', String(q.connection))
  if (q.calendar) p.set('calendar', q.calendar)
  const qs = [p.toString(), q.entityQuery ?? ''].filter(Boolean).join('&')
  const d = await call<CalendarEventsResponse>(`bookings/calendar-events?${qs}`)
  return { ...d, events: Array.isArray(d.events) ? d.events : [] }
}

// ---- plain calendar-day arithmetic on "YYYY-MM-DD" (no timezone involved) ----------

const dayMs = 86_400_000
/** "YYYY-MM-DD" → midnight UTC of that date (format it with timeZone 'UTC'). */
export const dayStamp = (ymd: string) => Date.parse(`${ymd}T00:00:00Z`)
export const ymdOf = (stamp: number) => new Date(stamp).toISOString().slice(0, 10)
export const addDays = (ymd: string, n: number) => ymdOf(dayStamp(ymd) + n * dayMs)
/** The Monday of the week holding `ymd`. */
export const mondayOf = (ymd: string) => addDays(ymd, -((new Date(dayStamp(ymd)).getUTCDay() + 6) % 7))
/** Today's date where the browser is. */
export function todayYmd(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
