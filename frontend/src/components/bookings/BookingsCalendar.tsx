'use client'

/**
 * The Bookings page's Calendar — its default view: every appointment in a real
 * calendar, Month · Week · Day, in the zone the dashboard shows times in
 * (Settings → Time zone, `useFormat`). ChatBranch's own bookings always — no Google
 * needed: booked solid, reserved / cancelling dashed, cancelled ones faded behind
 * "Show cancelled". A click opens the booking's card (time, contact, calendar,
 * status, who booked it, Open the conversation, Cancel).
 *
 * With a Google Calendar you may read (your own, or a shared one you can see —
 * GET /api/bookings/calendars), its OTHER events can be laid over in grey, read-only
 * (GET /api/bookings/calendar-events; the events ChatBranch made are already on
 * the calendar as bookings, so they are not shown twice).
 *
 * Reads GET /api/bookings?from=&to= (chats.view, scoped like conversations, the
 * top bar's entity). Refreshes live off the inbox stream — a booking is made the
 * moment the AI's reply is delivered.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import {
  CalendarDays,
  ChevronLeft,
  ChevronRight,
  ExternalLink,
  Loader2,
  MapPin,
  MessageSquare,
  RefreshCw,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { BookingStatusChip, CalendarMark, calendarName } from '@/components/bookings/BookingBits'
import { MeetingLink } from '@/components/bookings/MeetingLink'
import { Trans, useFormat, useT, type MessageKey } from '@/i18n'
import { useAccess } from '@/lib/useAccess'
import { subscribeChatStream } from '@/lib/chatStream'
import {
  BOOKING_STATUS,
  addDays,
  bookedBy,
  calendarEvents,
  dayStamp,
  instant,
  listBookings,
  listCalendarConnections,
  mondayOf,
  threadHref,
  type Booking,
  type CalendarConnection,
  type CalendarEvent,
  type CalendarEventsResponse,
} from '@/lib/bookings'

export type CalendarView = 'month' | 'week' | 'day'
const VIEWS: { id: CalendarView; label: MessageKey }[] = [
  { id: 'month', label: 'bookings.grid.month' },
  { id: 'week', label: 'bookings.grid.week' },
  { id: 'day', label: 'bookings.grid.day' },
]
const VIEW_KEY = 'bookings-view'
const HOUR_PX = 48

type Tone = 'booked' | 'held' | 'gone' | 'google'

/** One thing on the calendar: a ChatBranch booking or a Google event. */
interface Item {
  key: string
  tone: Tone
  title: string
  allDay: boolean
  /** Local (display-zone) days it covers, first → last. */
  day: string
  lastDay: string
  /** Minutes since local midnight (timed only). */
  startMin: number
  endMin: number
  startMs: number
  endMs: number
  booking?: Booking
  event?: CalendarEvent
}

const TONE_CLASS: Record<Tone, string> = {
  booked: 'border-l-[3px] border-brand bg-brand-400/15 text-text-heading',
  held: 'border border-dashed border-amber-400 bg-amber-400/10 text-text-heading',
  gone: 'border border-line bg-fill-subtle text-text-muted line-through',
  google: 'border border-line bg-well text-text-mid',
}

const toneOf = (b: Booking): Tone =>
  b.status === 'booked' ? 'booked' : b.status === 'pending' || b.status === 'cancelling' ? 'held' : 'gone'

/** Local day + minutes of an instant in `zone` (undefined = this device). */
function useZoned(zone: string | undefined) {
  return useMemo(() => {
    const opts: Intl.DateTimeFormatOptions = {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    }
    let fmt: Intl.DateTimeFormat
    try {
      fmt = new Intl.DateTimeFormat('en-CA', zone ? { ...opts, timeZone: zone } : opts)
    } catch {
      fmt = new Intl.DateTimeFormat('en-CA', opts)
    }
    return (ms: number) => {
      const parts = fmt.formatToParts(ms)
      const g = (type: string) => parts.find((p) => p.type === type)?.value ?? '00'
      return {
        day: `${g('year')}-${g('month')}-${g('day')}`,
        min: (Number(g('hour')) % 24) * 60 + Number(g('minute')),
      }
    }
  }, [zone])
}

/** The first day of the month holding `ymd`, and of the next one. */
const monthStart = (ymd: string) => `${ymd.slice(0, 7)}-01`
function nextMonth(ymd: string, n = 1): string {
  const y = Number(ymd.slice(0, 4))
  const m = Number(ymd.slice(5, 7)) - 1 + n
  const yy = y + Math.floor(m / 12)
  const mm = ((m % 12) + 12) % 12
  return `${yy}-${String(mm + 1).padStart(2, '0')}-01`
}

/** Side-by-side columns for overlapping timed items of one day. */
function layout(items: Item[]): Map<string, { col: number; cols: number }> {
  const out = new Map<string, { col: number; cols: number }>()
  const sorted = [...items].sort((a, b) => a.startMin - b.startMin || b.endMin - a.endMin)
  let cluster: Item[] = []
  let ends: number[] = []
  let clusterEnd = -1
  const flush = () => {
    for (const it of cluster) out.set(it.key, { col: out.get(it.key)?.col ?? 0, cols: ends.length })
    cluster = []
    ends = []
  }
  for (const it of sorted) {
    const end = Math.max(it.endMin, it.startMin + 20)
    if (cluster.length > 0 && it.startMin >= clusterEnd) flush()
    let col = ends.findIndex((e) => e <= it.startMin)
    if (col < 0) {
      col = ends.length
      ends.push(end)
    } else ends[col] = end
    out.set(it.key, { col, cols: 0 })
    cluster.push(it)
    clusterEnd = Math.max(clusterEnd, end)
  }
  flush()
  return out
}

export function BookingsCalendar({
  entityQuery,
  contactId,
  refreshKey,
  onCancel,
}: {
  entityQuery: string
  contactId: number | null
  /** Bumped by the page after a cancel — reload. */
  refreshKey: number
  onCancel: (b: Booking) => void
}) {
  const t = useT()
  const f = useFormat()
  const zoned = useZoned(f.timeZone)
  const { ready, can } = useAccess()

  const [view, setView] = useState<CalendarView>('month')
  const [cursor, setCursor] = useState<string>('') // the day in focus (YYYY-MM-DD)
  const [rows, setRows] = useState<Booking[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [showCancelled, setShowCancelled] = useState(false)
  const [calendarFilter, setCalendarFilter] = useState('')
  const [open, setOpen] = useState<Item | null>(null)
  const [now, setNow] = useState(() => Date.now())

  // Google layer
  const [connections, setConnections] = useState<CalendarConnection[]>([])
  const [googleConn, setGoogleConn] = useState('')
  const [googleCal, setGoogleCal] = useState('')
  const [google, setGoogle] = useState<CalendarEventsResponse | null>(null)
  const [googleFailure, setGoogleFailure] = useState<string | null>(null)

  const today = useMemo(() => zoned(now).day, [zoned, now])

  // the view (?view=, else this browser's last one) and today, after mount
  useEffect(() => {
    const fromUrl = new URLSearchParams(window.location.search).get('view') as CalendarView | null
    let stored: string | null = null
    try {
      stored = window.localStorage.getItem(VIEW_KEY)
    } catch {
      /* storage blocked */
    }
    const v = [fromUrl, stored].find((x): x is CalendarView => VIEWS.some((o) => o.id === x))
    if (v) setView(v)
    const tick = window.setInterval(() => setNow(Date.now()), 60_000)
    return () => window.clearInterval(tick)
  }, [])
  useEffect(() => {
    if (!cursor && today) setCursor(today)
  }, [cursor, today])

  const pickView = (v: CalendarView) => {
    setView(v)
    try {
      window.localStorage.setItem(VIEW_KEY, v)
    } catch {
      /* storage blocked */
    }
    const url = new URL(window.location.href)
    url.searchParams.set('view', v)
    window.history.replaceState(null, '', url.toString())
  }

  // the days on screen
  const days = useMemo(() => {
    if (!cursor) return [] as string[]
    if (view === 'day') return [cursor]
    if (view === 'week') return Array.from({ length: 7 }, (_, i) => addDays(mondayOf(cursor), i))
    const first = mondayOf(monthStart(cursor))
    const last = addDays(mondayOf(addDays(nextMonth(cursor), -1)), 6)
    const n = Math.round((dayStamp(last) - dayStamp(first)) / 86_400_000) + 1
    return Array.from({ length: n }, (_, i) => addDays(first, i))
  }, [view, cursor])
  const from = days[0]
  const to = days[days.length - 1]

  const load = useCallback(async () => {
    if (!from || !to) return
    setLoading(true)
    try {
      // a day either side: the server reads days in the business time zone, the grid in yours
      const list = await listBookings({
        from: addDays(from, -1),
        to: addDays(to, 1),
        contact_id: contactId ?? undefined,
        entityQuery,
        limit: 500,
      })
      setRows(list)
      setFailure(null)
    } catch (e) {
      setRows([])
      setFailure(
        t('bookings.page.loadFailed', {
          error: e instanceof Error ? e.message : String(e),
        }),
      )
    } finally {
      setLoading(false)
    }
  }, [from, to, contactId, entityQuery, t])

  useEffect(() => {
    void load()
  }, [load, refreshKey])

  useEffect(() => {
    let timer: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => void load(), 800)
    })
    return () => {
      unsub()
      window.clearTimeout(timer)
    }
  }, [load])

  // the Google calendars you may read (none = no layer offered)
  useEffect(() => {
    let live = true
    listCalendarConnections(entityQuery)
      .then((list) => {
        if (!live) return
        setConnections(list)
        setGoogleConn((cur) => (cur && list.some((c) => String(c.id) === cur) ? cur : ''))
      })
      .catch(() => live && setConnections([]))
    return () => {
      live = false
    }
  }, [entityQuery])

  useEffect(() => {
    if (!googleConn || !from || !to) {
      setGoogle(null)
      setGoogleFailure(null)
      return
    }
    let live = true
    calendarEvents({
      connection: Number(googleConn),
      calendar: googleCal || undefined,
      from: addDays(from, -1),
      to: addDays(to, 1),
      entityQuery,
    })
      .then((d) => {
        if (!live) return
        setGoogle(d)
        setGoogleFailure(null)
      })
      .catch((e) => {
        if (!live) return
        setGoogle(null)
        setGoogleFailure(
          t('bookings.calendar.loadFailed', {
            error: e instanceof Error ? e.message : String(e),
          }),
        )
      })
    return () => {
      live = false
    }
  }, [googleConn, googleCal, from, to, entityQuery, refreshKey, t])

  // ---- what goes on the calendar ---------------------------------------------------------

  const calendars = useMemo(() => Array.from(new Set((rows ?? []).map((b) => calendarName(t, b)))).sort(), [rows, t])

  const items = useMemo(() => {
    const out: Item[] = []
    const ids = new Set<number>()
    for (const b of rows ?? []) {
      ids.add(b.id)
      const tone = toneOf(b)
      if (tone === 'gone' && !showCancelled) continue
      if (calendarFilter && calendarName(t, b) !== calendarFilter) continue
      const s = instant(b.start)
      const e = instant(b.end) ?? s
      if (s == null || e == null) continue
      const a = zoned(s)
      const z = zoned(Math.max(s, e - 1))
      out.push({
        key: `b${b.id}`,
        tone,
        title: b.contact_name || b.title || t('bookings.grid.noName'),
        allDay: false,
        day: a.day,
        lastDay: a.day,
        startMin: a.min,
        endMin: z.day === a.day ? Math.max(z.min + 1, a.min + 15) : 24 * 60,
        startMs: s,
        endMs: e,
        booking: b,
      })
    }
    for (const ev of google?.events ?? []) {
      // ChatBranch made it: it is on the calendar already, as a booking
      if (ev.chatbranch && ev.booking && ids.has(ev.booking.id)) continue
      if (ev.all_day) {
        const last = ev.end ? addDays(ev.end, -1) : ev.start
        out.push({
          key: `g${ev.id}${ev.start}`,
          tone: 'google',
          title: ev.title || t('bookings.calendar.noTitle'),
          allDay: true,
          day: ev.start,
          lastDay: last < ev.start ? ev.start : last,
          startMin: 0,
          endMin: 24 * 60,
          startMs: dayStamp(ev.start),
          endMs: dayStamp(last),
          event: ev,
        })
        continue
      }
      const s = instant(ev.start)
      const e = instant(ev.end) ?? s
      if (s == null || e == null) continue
      const a = zoned(s)
      const z = zoned(Math.max(s, e - 1))
      out.push({
        key: `g${ev.id}${ev.start}`,
        tone: 'google',
        title: ev.title || t('bookings.calendar.noTitle'),
        allDay: false,
        day: a.day,
        lastDay: a.day,
        startMin: a.min,
        endMin: z.day === a.day ? Math.max(z.min + 1, a.min + 15) : 24 * 60,
        startMs: s,
        endMs: e,
        event: ev,
      })
    }
    return out
  }, [rows, google, showCancelled, calendarFilter, zoned, t])

  const byDay = useMemo(() => {
    const map = new Map<string, Item[]>()
    for (const it of items) {
      // an all-day event covers every day of its span
      for (let d = it.day; d <= it.lastDay; d = addDays(d, 1)) map.set(d, [...(map.get(d) ?? []), it])
    }
    for (const list of map.values()) list.sort((a, b) => Number(b.allDay) - Number(a.allDay) || a.startMs - b.startMs)
    return map
  }, [items])

  // ---- navigation ---------------------------------------------------------------------

  const step = (dir: -1 | 1) => {
    if (!cursor) return
    setCursor(
      view === 'day' ? addDays(cursor, dir) : view === 'week' ? addDays(cursor, 7 * dir) : nextMonth(cursor, dir),
    )
  }
  const openDay = (day: string) => {
    setCursor(day)
    pickView('day')
  }

  const title = !cursor
    ? ''
    : view === 'month'
      ? f.monthYearLong(dayStamp(cursor), 'UTC')
      : view === 'week'
        ? t('bookings.calendar.weekOf', {
            from: f.dayMonth(dayStamp(days[0]), 'UTC'),
            to: f.date(dayStamp(days[6]), 'UTC'),
          })
        : `${f.weekdayLong(dayStamp(cursor), 'UTC')}, ${f.date(dayStamp(cursor), 'UTC')}`

  const time = (it: Item) => (it.allDay ? t('bookings.calendar.allDay') : `${f.time(it.startMs)} – ${f.time(it.endMs)}`)

  const counts = useMemo(() => {
    const visible = (rows ?? []).filter((b) => days.includes(zoned(instant(b.start) ?? 0).day))
    return {
      booked: visible.filter((b) => b.status === 'booked').length,
      held: visible.filter((b) => b.status === 'pending' || b.status === 'cancelling').length,
    }
  }, [rows, days, zoned])

  const connName = (c: CalendarConnection) =>
    c.scope === 'personal'
      ? t('bookings.calendar.yours')
      : c.scope === 'entity'
        ? t('bookings.calendar.sharedEntity', { name: c.entity_name ?? '' })
        : t('bookings.calendar.sharedOrg')

  // ---- render ---------------------------------------------------------------------------

  return (
    <div data-tour="bookings-calendar" className="flex flex-col gap-3">
      {/* period + views */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => step(-1)}
            aria-label={t(`bookings.grid.prev.${view}` as MessageKey)}
          >
            <ChevronLeft size={15} aria-hidden />
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setCursor(today)}>
            {t('bookings.calendar.today')}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => step(1)}
            aria-label={t(`bookings.grid.next.${view}` as MessageKey)}
          >
            <ChevronRight size={15} aria-hidden />
          </Button>
        </div>
        <h3 className="display min-w-0 text-[19px] leading-tight text-text-heading" aria-live="polite">
          {title}
        </h3>
        {loading && <Loader2 size={14} className="animate-spin text-text-muted" aria-hidden />}
        <div
          role="radiogroup"
          aria-label={t('bookings.grid.viewAria')}
          data-tour="bookings-views"
          className="ml-auto flex rounded-lg border border-line-strong bg-card p-0.5"
        >
          {VIEWS.map((v) => {
            const on = v.id === view
            return (
              <button
                key={v.id}
                type="button"
                role="radio"
                aria-checked={on}
                onClick={() => pickView(v.id)}
                className={`rounded-md px-3 py-1 text-[12.5px] font-semibold transition-colors ${
                  on ? 'bg-primary text-on-primary' : 'text-text-mid hover:text-text-high'
                }`}
              >
                {t(v.label)}
              </button>
            )
          })}
        </div>
      </div>

      {/* filters + the Google layer */}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <span className="text-[12px] text-text-muted">
          {t('bookings.grid.summary', {
            booked: counts.booked,
            held: counts.held,
          })}
        </span>
        {calendars.length > 1 && (
          <Select
            value={calendarFilter}
            onChange={setCalendarFilter}
            options={[
              { value: '', label: t('bookings.page.allCalendars') },
              ...calendars.map((c) => ({ value: c, label: c })),
            ]}
            ariaLabel={t('bookings.page.calendarAria')}
            className="w-[170px]"
          />
        )}
        {connections.length > 0 && (
          <div className="flex flex-wrap items-center gap-2" data-tour="bookings-google-layer">
            <Select
              value={googleConn}
              onChange={(v) => {
                setGoogleConn(v)
                setGoogleCal('')
              }}
              options={[
                { value: '', label: t('bookings.grid.googleOff') },
                ...connections.map((c) => ({
                  value: String(c.id),
                  label: t('bookings.grid.googleOn', { name: connName(c) }),
                })),
              ]}
              ariaLabel={t('bookings.calendar.connectionAria')}
              className="w-[230px]"
            />
            {googleConn && (google?.calendars?.length ?? 0) > 1 && (
              <Select
                value={googleCal || google?.calendar_id || ''}
                onChange={setGoogleCal}
                options={(google?.calendars ?? []).map((c) => ({
                  value: c.id,
                  label: c.name,
                  hint: c.primary ? t('bookings.calendar.primary') : undefined,
                }))}
                ariaLabel={t('bookings.calendar.calendarAria')}
                className="w-[200px]"
              />
            )}
          </div>
        )}
        <label className="flex items-center gap-2 text-[12.5px] text-text-mid sm:ml-auto">
          <Toggle on={showCancelled} onChange={setShowCancelled} label={t('bookings.grid.showCancelled')} />
          {t('bookings.grid.showCancelled')}
        </label>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => void load()}
          disabled={loading}
          aria-label={t('common.actions.refresh')}
        >
          <RefreshCw size={13} className={loading ? 'animate-spin' : ''} aria-hidden />
        </Button>
      </div>

      {failure && (
        <div
          role="alert"
          className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          {failure}
        </div>
      )}
      {googleFailure && (
        <div
          role="alert"
          className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px] text-text-high"
        >
          {googleFailure}
        </div>
      )}
      {google && !google.ok && (
        <div
          role="alert"
          className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px] text-text-high"
        >
          {google.error === 'not_connected' ? (
            <Trans
              k="bookings.calendar.reconnect"
              values={{
                link: (
                  <Link
                    href={google.connection?.scope === 'personal' ? '/settings/connections' : '/integrations'}
                    className="font-semibold underline-offset-2 hover:underline"
                  >
                    {google.connection?.scope === 'personal'
                      ? t('bookings.calendar.myConnections')
                      : t('shell.nav.integrations')}
                  </Link>
                ),
              }}
            />
          ) : (
            t('bookings.calendar.googleFailed', { error: google.detail ?? '' })
          )}
        </div>
      )}
      {google?.truncated && (
        <div className="rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12px] text-text-muted">
          {t('bookings.grid.googleTruncated')}
        </div>
      )}

      {!cursor || rows === null ? (
        <div className="flex items-center gap-2 py-10 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('bookings.page.loading')}
        </div>
      ) : view === 'month' ? (
        <MonthGrid
          days={days}
          month={cursor.slice(0, 7)}
          today={today}
          selected={cursor}
          byDay={byDay}
          time={time}
          onSelect={setCursor}
          onOpenDay={openDay}
          onOpen={setOpen}
        />
      ) : (
        <TimeGrid
          days={days}
          today={today}
          byDay={byDay}
          now={now}
          zoned={zoned}
          onOpenDay={openDay}
          onOpen={setOpen}
        />
      )}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11.5px] text-text-muted">
        <Legend tone="booked" label={t('bookings.status.booked')} />
        <Legend tone="held" label={t('bookings.grid.legendHeld')} />
        {showCancelled && <Legend tone="gone" label={t('bookings.grid.legendGone')} />}
        {googleConn && <Legend tone="google" label={t('bookings.grid.legendGoogle')} />}
        <span className="sm:ml-auto">{t('bookings.calendar.timesIn', { tz: f.timeZone ?? deviceZone() })}</span>
      </div>

      <ItemModal
        item={open}
        onClose={() => setOpen(null)}
        time={time}
        canCancel={ready && can('chats.send')}
        onCancel={(b) => {
          setOpen(null)
          onCancel(b)
        }}
      />
    </div>
  )
}

const deviceZone = () => {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

function Legend({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span aria-hidden className={`inline-block h-3 w-4 rounded-sm ${TONE_CLASS[tone]}`} />
      {label}
    </span>
  )
}

/** One booking / event as a small clickable chip (month cells, all-day rows). */
function Chip({ it, time, onOpen }: { it: Item; time: (it: Item) => string; onOpen: (it: Item) => void }) {
  return (
    <button
      type="button"
      onClick={(e) => {
        e.stopPropagation()
        onOpen(it)
      }}
      title={`${time(it)} · ${it.title}`}
      className={`flex w-full min-w-0 items-center gap-1 truncate rounded px-1.5 py-[3px] text-left text-[11.5px] leading-tight transition-opacity hover:opacity-80 ${TONE_CLASS[it.tone]}`}
    >
      {!it.allDay && <span className="shrink-0 font-semibold tabular-nums">{time(it).split(' – ')[0]}</span>}
      <span className="truncate">{it.title}</span>
    </button>
  )
}

function MonthGrid({
  days,
  month,
  today,
  selected,
  byDay,
  time,
  onSelect,
  onOpenDay,
  onOpen,
}: {
  days: string[]
  month: string
  today: string
  selected: string
  byDay: Map<string, Item[]>
  time: (it: Item) => string
  onSelect: (day: string) => void
  onOpenDay: (day: string) => void
  onOpen: (it: Item) => void
}) {
  const t = useT()
  const f = useFormat()
  const MAX = 3
  const selectedItems = byDay.get(selected) ?? []
  return (
    <div className="flex flex-col gap-3">
      <div className="overflow-hidden rounded-xl border border-line bg-card">
        <div className="grid grid-cols-7 border-b border-line bg-well">
          {days.slice(0, 7).map((d) => (
            <div
              key={d}
              className="px-2 py-1.5 text-center text-[11px] font-semibold uppercase tracking-wide text-text-muted"
            >
              {f.weekday(dayStamp(d), 'UTC')}
            </div>
          ))}
        </div>
        <div className="grid grid-cols-7">
          {days.map((d, i) => {
            const list = byDay.get(d) ?? []
            const inMonth = d.slice(0, 7) === month
            const isToday = d === today
            const isSelected = d === selected
            return (
              <div
                key={d}
                role="button"
                tabIndex={0}
                onClick={() => onSelect(d)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    onOpenDay(d)
                  }
                }}
                aria-label={`${f.date(dayStamp(d), 'UTC')} — ${t('bookings.grid.count', { count: list.length })}`}
                className={`flex min-h-[64px] cursor-pointer flex-col gap-1 p-1 sm:min-h-[112px] sm:p-1.5 ${
                  i % 7 !== 6 ? 'border-r border-line' : ''
                } ${i < days.length - 7 ? 'border-b border-line' : ''} ${inMonth ? '' : 'bg-fill-subtle'} ${
                  isSelected ? 'ring-2 ring-inset ring-line-strong sm:ring-0' : ''
                }`}
              >
                <div className="flex items-center justify-between">
                  <button
                    type="button"
                    onClick={(e) => {
                      e.stopPropagation()
                      onOpenDay(d)
                    }}
                    title={t('bookings.grid.openDay')}
                    className={`grid h-6 min-w-6 place-items-center rounded-full px-1 text-[12px] font-semibold tabular-nums transition-colors hover:bg-fill-hover ${
                      isToday
                        ? 'bg-primary text-on-primary hover:bg-primary'
                        : inMonth
                          ? 'text-text-heading'
                          : 'text-text-faint'
                    }`}
                  >
                    {Number(d.slice(8))}
                  </button>
                </div>
                {/* phones: dots; wider: the chips themselves */}
                <div className="flex flex-wrap gap-0.5 sm:hidden">
                  {list.slice(0, 4).map((it) => (
                    <span key={it.key} aria-hidden className={`h-1.5 w-1.5 rounded-full ${DOT[it.tone]}`} />
                  ))}
                </div>
                <div className="hidden min-w-0 flex-col gap-0.5 sm:flex">
                  {list.slice(0, MAX).map((it) => (
                    <Chip key={it.key} it={it} time={time} onOpen={onOpen} />
                  ))}
                  {list.length > MAX && (
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation()
                        onOpenDay(d)
                      }}
                      className="rounded px-1.5 text-left text-[11px] font-semibold text-text-muted hover:text-text-high"
                    >
                      {t('bookings.grid.more', { count: list.length - MAX })}
                    </button>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      </div>

      {/* phones: the tapped day's appointments under the month */}
      <div className="flex flex-col gap-1.5 sm:hidden">
        <div className="text-[12.5px] font-semibold text-text-heading">
          {f.weekdayLong(dayStamp(selected), 'UTC')}, {f.date(dayStamp(selected), 'UTC')}
        </div>
        {selectedItems.length === 0 ? (
          <span className="text-[12.5px] text-text-faint">{t('bookings.grid.nothing')}</span>
        ) : (
          selectedItems.map((it) => <AgendaRow key={it.key} it={it} time={time} onOpen={onOpen} />)
        )}
      </div>
    </div>
  )
}

const DOT: Record<Tone, string> = {
  booked: 'bg-brand',
  held: 'bg-amber-400',
  gone: 'bg-line-strong',
  google: 'bg-text-faint',
}

function AgendaRow({ it, time, onOpen }: { it: Item; time: (it: Item) => string; onOpen: (it: Item) => void }) {
  return (
    <button
      type="button"
      onClick={() => onOpen(it)}
      className={`flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-[12.5px] ${TONE_CLASS[it.tone]}`}
    >
      <span className="shrink-0 font-semibold tabular-nums">{time(it)}</span>
      <span className="truncate">{it.title}</span>
    </button>
  )
}

function TimeGrid({
  days,
  today,
  byDay,
  now,
  zoned,
  onOpenDay,
  onOpen,
}: {
  days: string[]
  today: string
  byDay: Map<string, Item[]>
  now: number
  zoned: (ms: number) => { day: string; min: number }
  onOpenDay: (day: string) => void
  onOpen: (it: Item) => void
}) {
  const t = useT()
  const f = useFormat()
  const timed = days.flatMap((d) => (byDay.get(d) ?? []).filter((it) => !it.allDay && it.day === d))
  const allDay = days.map((d) => (byDay.get(d) ?? []).filter((it) => it.allDay))
  const hasAllDay = allDay.some((l) => l.length > 0)
  // 8 AM – 8 PM, stretched to whatever is booked outside it
  const startHour = Math.min(8, ...timed.map((it) => Math.floor(it.startMin / 60)))
  const endHour = Math.max(20, ...timed.map((it) => Math.ceil(it.endMin / 60)))
  const hours = Array.from({ length: endHour - startHour }, (_, i) => startHour + i)
  const height = hours.length * HOUR_PX
  const nowAt = zoned(now)
  const single = days.length === 1
  const cols = single ? 'grid-cols-[56px_1fr]' : 'grid-cols-[56px_repeat(7,minmax(0,1fr))]'

  return (
    <div className="overflow-x-auto rounded-xl border border-line bg-card">
      <div className={single ? '' : 'min-w-[760px]'}>
        {/* day headers */}
        <div className={`grid ${cols} border-b border-line bg-well`}>
          <div />
          {days.map((d) => (
            <button
              key={d}
              type="button"
              onClick={() => onOpenDay(d)}
              disabled={single}
              className="flex flex-col items-center gap-0.5 border-l border-line px-1 py-1.5 disabled:cursor-default"
            >
              <span
                className={`text-[11px] font-semibold uppercase tracking-wide ${d === today ? 'text-[color:var(--brand-ink)]' : 'text-text-muted'}`}
              >
                {f.weekday(dayStamp(d), 'UTC')}
              </span>
              <span
                className={`grid h-7 min-w-7 place-items-center rounded-full px-1 text-[14px] font-semibold tabular-nums ${
                  d === today ? 'bg-primary text-on-primary' : 'text-text-heading'
                }`}
              >
                {Number(d.slice(8))}
              </span>
            </button>
          ))}
        </div>

        {hasAllDay && (
          <div className={`grid ${cols} border-b border-line`}>
            <div className="px-1 py-1 text-right text-[10.5px] text-text-muted">{t('bookings.calendar.allDay')}</div>
            {days.map((d, i) => (
              <div key={d} className="flex min-w-0 flex-col gap-0.5 border-l border-line p-1">
                {allDay[i].map((it) => (
                  <Chip key={it.key} it={it} time={() => t('bookings.calendar.allDay')} onOpen={onOpen} />
                ))}
              </div>
            ))}
          </div>
        )}

        <div className={`grid ${cols}`}>
          {/* hour labels */}
          <div className="relative" style={{ height }}>
            {hours.map((h, i) => (
              <span
                key={h}
                className="absolute right-1.5 -translate-y-1/2 text-[10.5px] tabular-nums text-text-muted"
                style={{ top: i * HOUR_PX }}
              >
                {i === 0 ? '' : f.hour(Date.UTC(2020, 0, 1, h, 0), 'UTC')}
              </span>
            ))}
          </div>
          {days.map((d) => {
            const list = (byDay.get(d) ?? []).filter((it) => !it.allDay && it.day === d)
            const cols2 = layout(list)
            return (
              <div key={d} className="relative border-l border-line" style={{ height }}>
                {hours.map((h, i) => (
                  <div
                    key={h}
                    aria-hidden
                    className="absolute inset-x-0 border-t border-line"
                    style={{ top: i * HOUR_PX }}
                  />
                ))}
                {d === today && nowAt.min >= startHour * 60 && nowAt.min <= endHour * 60 && (
                  <div
                    aria-hidden
                    className="absolute inset-x-0 z-[2] h-0.5 bg-[color:var(--red-ink)]"
                    style={{
                      top: ((nowAt.min - startHour * 60) / 60) * HOUR_PX,
                    }}
                  />
                )}
                {list.map((it) => {
                  const pos = cols2.get(it.key) ?? { col: 0, cols: 1 }
                  const top = ((it.startMin - startHour * 60) / 60) * HOUR_PX
                  const h = Math.max(((it.endMin - it.startMin) / 60) * HOUR_PX, 22)
                  const w = 100 / Math.max(pos.cols, 1)
                  return (
                    <button
                      key={it.key}
                      type="button"
                      onClick={() => onOpen(it)}
                      title={`${f.time(it.startMs)} – ${f.time(it.endMs)} · ${it.title}`}
                      className={`absolute z-[1] overflow-hidden rounded-md px-1.5 py-1 text-left text-[11.5px] leading-tight transition-opacity hover:opacity-85 ${TONE_CLASS[it.tone]}`}
                      style={{
                        top,
                        height: h,
                        left: `calc(${pos.col * w}% + 2px)`,
                        width: `calc(${w}% - 4px)`,
                      }}
                    >
                      <span className="block truncate font-semibold">
                        {it.title}
                        {/* too short for a second line: the start time beside the name */}
                        {h < 34 && <span className="font-normal opacity-80"> · {f.time(it.startMs)}</span>}
                      </span>
                      {h >= 34 && (
                        <span className="block truncate tabular-nums text-[10.5px] opacity-80">
                          {f.time(it.startMs)} – {f.time(it.endMs)}
                        </span>
                      )}
                    </button>
                  )
                })}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}

/** The card a click opens: a booking (open the chat, cancel) or a Google event. */
function ItemModal({
  item,
  onClose,
  time,
  canCancel,
  onCancel,
}: {
  item: Item | null
  onClose: () => void
  time: (it: Item) => string
  canCancel: boolean
  onCancel: (b: Booking) => void
}) {
  const t = useT()
  const f = useFormat()
  const b = item?.booking
  const e = item?.event
  const cancellable = !!b && canCancel && b.status === 'booked' && (instant(b.end) ?? 0) > Date.now()
  const link = b?.event_link || e?.link || ''
  const hasActions = !!b?.thread || !!link || cancellable
  return (
    <Modal
      open={!!item}
      onClose={onClose}
      size="sm"
      title={item?.title ?? ''}
      subtitle={
        item ? `${item.allDay ? f.date(dayStamp(item.day), 'UTC') : f.date(item.startMs)} · ${time(item)}` : undefined
      }
      footer={
        hasActions ? (
          <>
            {b?.thread && (
              <Link
                href={threadHref(b.thread, b.thread_id)}
                className="inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-[13px] font-semibold text-text-high hover:bg-fill-hover"
              >
                <MessageSquare size={14} aria-hidden /> {t('bookings.page.openConversation')}
              </Link>
            )}
            {link && (
              <a
                href={link}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-[13px] font-semibold text-text-high hover:bg-fill-hover"
              >
                <ExternalLink size={14} aria-hidden /> {t('bookings.calendar.openInGoogle')}
              </a>
            )}
            {cancellable && (
              <Button variant="danger" onClick={() => b && onCancel(b)}>
                {t('bookings.cancel.confirm')}
              </Button>
            )}
          </>
        ) : undefined
      }
    >
      {b && (
        <dl className="grid grid-cols-[110px_1fr] gap-x-3 gap-y-2 text-[12.5px]">
          <dt className="text-text-muted">{t('bookings.page.colWhen')}</dt>
          <dd className="text-text-high">
            {b.label}
            {b.timezone && <span className="text-text-muted"> · {b.timezone}</span>}
          </dd>
          <dt className="text-text-muted">{t('bookings.page.colContact')}</dt>
          <dd className="text-text-high">{b.contact_name || '—'}</dd>
          {b.title && b.title !== b.contact_name && (
            <>
              <dt className="text-text-muted">{t('bookings.grid.titleLabel')}</dt>
              <dd className="text-text-high">{b.title}</dd>
            </>
          )}
          <dt className="text-text-muted">{t('bookings.page.colCalendar')}</dt>
          <dd className="flex items-center gap-1.5 text-text-high">
            <CalendarMark source={b.source} size={12} /> {calendarName(t, b)}
          </dd>
          {b.meeting_url && (
            <>
              <dt className="text-text-muted">{t('bookings.meeting.label')}</dt>
              <dd className="flex min-w-0 flex-col items-start gap-0.5">
                <MeetingLink booking={b} className="max-w-full text-[12.5px]" />
                <span className="text-[11.5px] text-text-muted">
                  {t(b.meeting_sent_at ? 'bookings.meeting.sent' : 'bookings.meeting.notSent')}
                </span>
              </dd>
            </>
          )}
          <dt className="text-text-muted">{t('common.fields.status')}</dt>
          <dd className="flex flex-col items-start gap-1">
            <BookingStatusChip booking={b} />
            <span className="text-[11.5px] text-text-muted">
              {t(BOOKING_STATUS[b.status]?.hint ?? 'bookings.statusHint.booked')}
            </span>
          </dd>
          <dt className="text-text-muted">{t('bookings.page.colBookedBy')}</dt>
          <dd className="text-text-high">{bookedBy(b.created_by)}</dd>
        </dl>
      )}
      {e && (
        <div className="flex flex-col gap-2 text-[12.5px]">
          {e.location && (
            <span className="inline-flex items-center gap-1.5 text-text-mid">
              <MapPin size={12} aria-hidden /> {e.location}
            </span>
          )}
          <div className="flex flex-wrap gap-1.5">
            <span className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-[11px] font-semibold text-text-muted">
              <CalendarDays size={11} aria-hidden /> {t('bookings.grid.fromGoogle')}
            </span>
            {e.free && (
              <span className="rounded-full border border-line px-2 py-0.5 text-[11px] font-semibold text-text-muted">
                {t('bookings.calendar.free')}
              </span>
            )}
            {e.chatbranch && (
              <span className="rounded-full bg-brand-400/15 px-2 py-0.5 text-[11px] font-semibold text-[color:var(--brand-ink)]">
                {t('bookings.calendar.bookedHere')}
              </span>
            )}
          </div>
          <p className="text-[11.5px] text-text-muted">{t('bookings.grid.googleReadOnly')}</p>
        </div>
      )}
    </Modal>
  )
}
