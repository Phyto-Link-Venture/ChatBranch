'use client'

/**
 * The Bookings page's Calendar tab: the connected Google Calendar's own events,
 * READ-ONLY, a week at a time — everything on the calendar (meetings, leave,
 * holidays, appointments made outside ChatBranch), with the bookings ChatBranch
 * made marked and linked to their conversation.
 *
 * Which calendars: your own (Settings → My connections), plus the shared ones
 * (an outlet's or the organization's, from Integrations) when you can see
 * integrations — never another person's personal calendar. Reads
 * GET /api/bookings/calendars + /api/bookings/calendar-events.
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
import { Card } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { BookingStatusChip } from '@/components/bookings/BookingBits'
import { Trans, useFormat, useT } from '@/i18n'
import { useAccess } from '@/lib/useAccess'
import {
  addDays,
  calendarEvents,
  dayStamp,
  instant,
  listCalendarConnections,
  mondayOf,
  threadHref,
  todayYmd,
  type CalendarConnection,
  type CalendarEvent,
  type CalendarEventsResponse,
} from '@/lib/bookings'

export function GoogleCalendarView({ entityQuery }: { entityQuery: string }) {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const [connections, setConnections] = useState<CalendarConnection[] | null>(null)
  const [connection, setConnection] = useState<number | null>(null)
  const [calendar, setCalendar] = useState('')
  const [week, setWeek] = useState(() => mondayOf(todayYmd()))
  const [data, setData] = useState<CalendarEventsResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  // the calendars you may read (re-read when the top bar's entity changes)
  useEffect(() => {
    let live = true
    setConnections(null)
    listCalendarConnections(entityQuery)
      .then((list) => {
        if (!live) return
        setConnections(list)
        setConnection((cur) => (cur != null && list.some((c) => c.id === cur) ? cur : (list[0]?.id ?? null)))
      })
      .catch((e) => {
        if (!live) return
        setConnections([])
        setFailure(
          t('bookings.calendar.loadFailed', {
            error: e instanceof Error ? e.message : String(e),
          }),
        )
      })
    return () => {
      live = false
    }
  }, [entityQuery, t])

  const load = useCallback(async () => {
    if (connection == null) return
    setLoading(true)
    setFailure(null)
    try {
      setData(
        await calendarEvents({
          connection,
          calendar: calendar || undefined,
          from: week,
          to: addDays(week, 6),
          entityQuery,
        }),
      )
    } catch (e) {
      setData(null)
      setFailure(
        t('bookings.calendar.loadFailed', {
          error: e instanceof Error ? e.message : String(e),
        }),
      )
    } finally {
      setLoading(false)
    }
  }, [connection, calendar, week, entityQuery, t])

  useEffect(() => {
    void load()
  }, [load])

  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => addDays(week, i)), [week])
  const byDay = useMemo(() => {
    const map = new Map<string, CalendarEvent[]>()
    for (const e of data?.events ?? []) map.set(e.day, [...(map.get(e.day) ?? []), e])
    // all-day first, then by start
    for (const list of map.values())
      list.sort((a, b) => Number(b.all_day) - Number(a.all_day) || (instant(a.start) ?? 0) - (instant(b.start) ?? 0))
    return map
  }, [data])

  const tz = data?.timezone || undefined
  const today = todayYmd()
  // a short name: "Your calendar" / "Shared · Bangsar" / "Shared · Organization"
  const connectionName = (c: CalendarConnection) =>
    c.scope === 'personal'
      ? t('bookings.calendar.yours')
      : c.scope === 'entity'
        ? t('bookings.calendar.sharedEntity', { name: c.entity_name ?? '' })
        : t('bookings.calendar.sharedOrg')

  if (connections === null)
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('bookings.calendar.loading')}
      </div>
    )

  if (connections.length === 0)
    return (
      <Card className="flex flex-col items-center gap-2 px-5 py-8 text-center text-[13px] text-text-muted">
        <CalendarDays size={22} className="text-text-faint" aria-hidden />
        <span className="font-semibold text-text-high">{t('bookings.calendar.noneTitle')}</span>
        <span className="max-w-[460px]">
          <Trans
            k={
              ready && (can('integrations.view') || can('integrations.manage'))
                ? 'bookings.calendar.noneAdmin'
                : 'bookings.calendar.none'
            }
            values={{
              mine: (
                <Link
                  href="/settings/connections"
                  className="font-semibold text-text-high underline-offset-2 hover:underline"
                >
                  {t('bookings.calendar.myConnections')}
                </Link>
              ),
              integrations: (
                <Link href="/integrations" className="font-semibold text-text-high underline-offset-2 hover:underline">
                  {t('shell.nav.integrations')}
                </Link>
              ),
            }}
          />
        </span>
        {failure && <span className="text-[12px] text-[color:var(--red-ink)]">{failure}</span>}
      </Card>
    )

  const calendars = data?.calendars ?? []
  return (
    <div data-tour="bookings-calendar" className="flex flex-col gap-3">
      {/* which calendar, which week */}
      <div className="flex flex-wrap items-center gap-2">
        <Select
          value={connection != null ? String(connection) : ''}
          onChange={(v) => {
            setConnection(Number(v))
            setCalendar('')
          }}
          options={connections.map((c) => ({
            value: String(c.id),
            label: connectionName(c),
          }))}
          ariaLabel={t('bookings.calendar.connectionAria')}
          className="w-full sm:w-[280px]"
        />
        {calendars.length > 1 && (
          <Select
            value={calendar || data?.calendar_id || ''}
            onChange={setCalendar}
            options={calendars.map((c) => ({
              value: c.id,
              label: c.name,
              hint: c.primary ? t('bookings.calendar.primary') : undefined,
            }))}
            ariaLabel={t('bookings.calendar.calendarAria')}
            className="w-full sm:w-[220px]"
          />
        )}
        <div className="flex items-center gap-1 sm:ml-auto">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setWeek(addDays(week, -7))}
            aria-label={t('bookings.calendar.prevWeek')}
          >
            <ChevronLeft size={14} aria-hidden />
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setWeek(mondayOf(todayYmd()))}>
            {t('bookings.calendar.thisWeek')}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setWeek(addDays(week, 7))}
            aria-label={t('bookings.calendar.nextWeek')}
          >
            <ChevronRight size={14} aria-hidden />
          </Button>
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
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h3 className="text-[14px] font-semibold text-text-heading">
          {t('bookings.calendar.weekOf', {
            from: f.dayMonth(dayStamp(days[0]), 'UTC'),
            to: f.date(dayStamp(days[6]), 'UTC'),
          })}
        </h3>
        {tz && <span className="text-[11.5px] text-text-muted">{t('bookings.calendar.timesIn', { tz })}</span>}
      </div>

      {failure && (
        <div
          role="alert"
          className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          {failure}
        </div>
      )}
      {data && !data.ok && (
        <div
          role="alert"
          className="rounded-lg border border-amber/40 bg-amber/10 px-3 py-2 text-[12.5px] text-text-high"
        >
          {data.error === 'not_connected' ? (
            <Trans
              k="bookings.calendar.reconnect"
              values={{
                link: (
                  <Link
                    href={data.connection?.scope === 'personal' ? '/settings/connections' : '/integrations'}
                    className="font-semibold underline-offset-2 hover:underline"
                  >
                    {data.connection?.scope === 'personal'
                      ? t('bookings.calendar.myConnections')
                      : t('shell.nav.integrations')}
                  </Link>
                ),
              }}
            />
          ) : (
            t('bookings.calendar.googleFailed', { error: data.detail ?? '' })
          )}
        </div>
      )}
      {data?.truncated && (
        <div className="rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12px] text-text-muted">
          {t('bookings.calendar.truncated')}
        </div>
      )}

      <Card className="divide-y divide-line p-0">
        {days.map((day) => {
          const events = byDay.get(day) ?? []
          const isToday = day === today
          return (
            <section
              key={day}
              aria-label={f.date(dayStamp(day), 'UTC')}
              className="flex flex-col gap-2 px-4 py-3 sm:flex-row sm:gap-4"
            >
              <div className="flex shrink-0 items-baseline gap-2 sm:w-[112px] sm:flex-col sm:gap-0">
                <span
                  className={`text-[12px] font-semibold uppercase tracking-wide ${isToday ? 'text-[color:var(--brand-ink)]' : 'text-text-muted'}`}
                >
                  {f.weekday(dayStamp(day), 'UTC')}
                </span>
                <span className="text-[14px] font-semibold text-text-heading">{f.dayMonth(dayStamp(day), 'UTC')}</span>
                {isToday && (
                  <span className="text-[11px] font-semibold text-[color:var(--brand-ink)]">
                    {t('bookings.calendar.today')}
                  </span>
                )}
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                {loading && !data ? (
                  <span className="text-[12px] text-text-faint">…</span>
                ) : events.length === 0 ? (
                  <span className="py-1 text-[12.5px] text-text-faint">{t('bookings.calendar.noEvents')}</span>
                ) : (
                  events.map((e) => <EventRow key={e.id} event={e} tz={tz} />)
                )}
              </div>
            </section>
          )
        })}
      </Card>
      <p className="text-[11.5px] text-text-muted">{t('bookings.calendar.readOnly')}</p>
    </div>
  )
}

function EventRow({ event: e, tz }: { event: CalendarEvent; tz?: string }) {
  const t = useT()
  const f = useFormat()
  // an all-day event's end is the day AFTER its last day
  const lastDay = e.all_day && e.end ? addDays(e.end, -1) : null
  const when = e.all_day
    ? lastDay && lastDay !== e.start
      ? t('bookings.calendar.allDayUntil', {
          date: f.dayMonth(dayStamp(lastDay), 'UTC'),
        })
      : t('bookings.calendar.allDay')
    : `${f.time(instant(e.start), tz)}${e.end ? ` – ${f.time(instant(e.end), tz)}` : ''}`
  return (
    <div
      className={`flex items-start gap-3 rounded-lg border px-3 py-2 ${
        e.chatbranch ? 'border-brand-400/40 bg-brand-400/5' : 'border-line bg-card'
      }`}
    >
      {/* phones: the time above the title; wider: a time column */}
      <div className="flex min-w-0 flex-1 flex-col gap-1 sm:flex-row sm:gap-3">
        <span
          className={`shrink-0 text-[12.5px] font-semibold text-text-high sm:w-[160px] sm:whitespace-nowrap ${e.all_day ? '' : 'tabular-nums'}`}
        >
          {when}
        </span>
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className={`break-words text-[13px] ${e.free ? 'text-text-mid' : 'font-semibold text-text-heading'}`}>
            {e.title || t('bookings.calendar.noTitle')}
          </span>
          {e.location && (
            <span className="inline-flex items-center gap-1 truncate text-[11.5px] text-text-muted">
              <MapPin size={11} aria-hidden /> {e.location}
            </span>
          )}
          <div className="flex flex-wrap items-center gap-1.5">
            {e.free && (
              <span className="rounded-full border border-line px-1.5 py-px text-[10.5px] font-semibold text-text-muted">
                {t('bookings.calendar.free')}
              </span>
            )}
            {e.chatbranch && (
              <span className="max-w-full truncate rounded-full bg-brand-400/15 px-1.5 py-px text-[10.5px] font-semibold text-[color:var(--brand-ink)]">
                {e.booking?.contact_name
                  ? t('bookings.calendar.bookedWith', {
                      name: e.booking.contact_name,
                    })
                  : t('bookings.calendar.bookedHere')}
              </span>
            )}
            {e.booking && <BookingStatusChip booking={e.booking} />}
          </div>
        </div>
      </div>
      <div className="flex shrink-0 items-center gap-1">
        {e.booking?.thread && (
          <Link
            href={threadHref(e.booking.thread, e.booking.thread_id)}
            title={t('bookings.page.openConversation')}
            aria-label={t('bookings.page.openConversation')}
            className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <MessageSquare size={14} aria-hidden />
          </Link>
        )}
        {e.link && (
          <a
            href={e.link}
            target="_blank"
            rel="noreferrer"
            title={t('bookings.calendar.openInGoogle')}
            aria-label={t('bookings.calendar.openInGoogle')}
            className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <ExternalLink size={14} aria-hidden />
          </a>
        )}
      </div>
    </div>
  )
}
