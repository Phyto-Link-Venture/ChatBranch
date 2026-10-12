'use client'

/**
 * Bookings — every appointment the AI (through a "Connected app" calendar
 * skill) or the team booked, on Google Calendar or kept in ChatBranch.
 * Upcoming (soonest first) and Past (newest first) tabs, filters for status,
 * calendar and contact, a link into each conversation, and an operator cancel.
 * The Calendar tab shows the connected Google Calendar itself, week by week
 * (GoogleCalendarView — read-only; ChatBranch's bookings marked).
 *
 * Reads GET /api/bookings (chats.view, scoped like conversations; the top
 * bar's entity narrows it via useEntityQuery). Refreshes live off the inbox
 * stream — a booking is made the moment the AI's reply is delivered. Fails
 * soft: no endpoint yet reads as an empty list with a quiet note.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { Bot, CalendarCheck, ExternalLink, Loader2, MessageSquare, RefreshCw, X } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { Select } from '@/components/ui/Select'
import {
  BookingStatusChip,
  CalendarMark,
  CancelBookingModal,
  calendarName,
  useBookingLabel,
} from '@/components/bookings/BookingBits'
import { GoogleCalendarView } from '@/components/bookings/GoogleCalendarView'
import { Trans, useFormat, useT, type MessageKey } from '@/i18n'
import { useAccess } from '@/lib/useAccess'
import { useEntityQuery } from '@/lib/EntityContext'
import { subscribeChatStream } from '@/lib/chatStream'
import {
  ACTIVE_STATUSES,
  BOOKING_STATUS,
  BookingsError,
  bookedBy,
  instant,
  isQuietError,
  listBookings,
  threadHref,
  type Booking,
  type BookingStatus,
} from '@/lib/bookings'

type Tab = 'upcoming' | 'past' | 'calendar'
const TABS: { id: Tab; label: MessageKey }[] = [
  { id: 'upcoming', label: 'bookings.page.upcoming' },
  { id: 'past', label: 'bookings.page.past' },
  { id: 'calendar', label: 'bookings.page.calendarTab' },
]

const STATUS_OPTIONS: { value: string; label: MessageKey }[] = [
  { value: 'active', label: 'bookings.page.statusActive' },
  { value: 'all', label: 'bookings.page.statusAll' },
  ...(Object.keys(BOOKING_STATUS) as BookingStatus[]).map((s) => ({ value: s, label: BOOKING_STATUS[s].label })),
]

const defaultStatus = (t: Tab) => (t === 'upcoming' ? 'active' : 'all')

export default function BookingsPage() {
  const t = useT()
  const f = useFormat()
  const bookingLabel = useBookingLabel()
  const entityQ = useEntityQuery()
  const { ready, can } = useAccess()
  const [tab, setTab] = useState<Tab>('upcoming')
  const [status, setStatus] = useState('active')
  const [calendar, setCalendar] = useState('')
  const [contactId, setContactId] = useState<number | null>(null)
  const [rows, setRows] = useState<Booking[] | null>(null)
  const [loadNote, setLoadNote] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [confirm, setConfirm] = useState<Booking | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // ?tab= and ?contact= after mount (window — no Suspense boundary needed)
  useEffect(() => {
    const p = new URLSearchParams(window.location.search)
    const fromUrl = p.get('tab') as Tab | null
    if (fromUrl && TABS.some((x) => x.id === fromUrl)) {
      setTab(fromUrl)
      setStatus(defaultStatus(fromUrl))
    }
    const c = Number(p.get('contact'))
    if (Number.isFinite(c) && c > 0) setContactId(c)
  }, [])

  const syncUrl = (t: Tab, contact: number | null) => {
    const url = new URL(window.location.href)
    if (t === 'upcoming') url.searchParams.delete('tab')
    else url.searchParams.set('tab', t)
    if (contact) url.searchParams.set('contact', String(contact))
    else url.searchParams.delete('contact')
    window.history.replaceState(null, '', url.toString())
  }

  const allowed = ready && can('chats.view')

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const list = await listBookings({
        upcoming: tab === 'upcoming',
        to: tab === 'past' ? new Date().toISOString() : undefined,
        contact_id: contactId ?? undefined,
        entityQuery: entityQ,
      })
      setRows(list)
      setLoadNote(null)
    } catch (e) {
      setRows([])
      setLoadNote(
        isQuietError(e) && (e as BookingsError).status === 404
          ? t('bookings.page.notAvailable')
          : t('bookings.page.loadFailed', { error: e instanceof Error ? e.message : String(e) }),
      )
    } finally {
      setLoading(false)
    }
  }, [tab, contactId, entityQ, t])

  // the Calendar tab reads Google, not the bookings list
  const listTab = tab !== 'calendar'

  useEffect(() => {
    if (!allowed || !listTab) return
    setRows(null)
    void load()
  }, [allowed, listTab, load])

  // live: the inbox stream bumps whenever a conversation moves (a reply that
  // books or cancels is a new message) — debounced refetch
  useEffect(() => {
    if (!allowed || !listTab) return
    let timer: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => void load(), 800)
    })
    return () => {
      unsub()
      window.clearTimeout(timer)
    }
  }, [allowed, listTab, load])

  const calendars = useMemo(
    () => Array.from(new Set((rows ?? []).map((b) => calendarName(t, b)))).sort(),
    [rows, t],
  )

  const visible = useMemo(() => {
    const now = Date.now()
    return (rows ?? []).filter((b) => {
      if (status === 'active' && !ACTIVE_STATUSES.includes(b.status)) return false
      if (status !== 'active' && status !== 'all' && b.status !== status) return false
      if (calendar && calendarName(t, b) !== calendar) return false
      // belt and braces for the tab split (the server already filters)
      const at = instant(b.start)
      if (at != null && tab === 'past' && at > now) return false
      return true
    })
  }, [rows, status, calendar, tab, t])

  const contactName = contactId != null ? (rows ?? []).find((b) => b.contact_id === contactId)?.contact_name : undefined
  const canCancel = ready && can('chats.send')

  const columns: Column<Booking>[] = [
    {
      key: 'when',
      header: t('bookings.page.colWhen'),
      sortable: true,
      value: (b) => b.start,
      render: (b) => (
        <div className="min-w-[150px]">
          <div className="font-semibold text-text-heading">{bookingLabel(b)}</div>
          <div className="text-[11.5px] text-text-muted">
            {b.timezone
              ? t('bookings.page.whenTz', { relative: f.relative(instant(b.start)), tz: b.timezone })
              : f.relative(instant(b.start))}
          </div>
          {/* on a phone the Status column is off to the side — repeat it here */}
          <div className="mt-1 sm:hidden">
            <BookingStatusChip booking={b} />
          </div>
        </div>
      ),
    },
    {
      key: 'contact',
      header: t('bookings.page.colContact'),
      sortable: true,
      value: (b) => `${b.contact_name} ${b.title}`,
      render: (b) => (
        <div className="min-w-[150px] max-w-[260px]">
          {b.contact_id != null ? (
            <Link href={`/contacts/${b.contact_id}`} className="font-semibold text-text-high hover:underline">
              {b.contact_name || `#${b.contact_id}`}
            </Link>
          ) : (
            <span className="font-semibold text-text-high">{b.contact_name || '—'}</span>
          )}
          {b.title && <div className="truncate text-[11.5px] text-text-muted">{b.title}</div>}
        </div>
      ),
    },
    {
      key: 'calendar',
      header: t('bookings.page.colCalendar'),
      sortable: true,
      value: (b) => calendarName(t, b),
      render: (b) => (
        <span className="inline-flex min-w-[130px] items-center gap-1.5 text-[12.5px] text-text-mid">
          <CalendarMark source={b.source} />
          <span className="truncate">{calendarName(t, b)}</span>
        </span>
      ),
    },
    {
      key: 'status',
      header: t('common.fields.status'),
      sortable: true,
      value: (b) => (BOOKING_STATUS[b.status] ? t(BOOKING_STATUS[b.status].label) : b.status),
      render: (b) => (
        <div className="flex flex-col items-start gap-0.5">
          <BookingStatusChip booking={b} />
          {b.detail && (b.status === 'failed' || b.status === 'released') && (
            <span className="max-w-[220px] truncate text-[11px] text-text-muted" title={b.detail}>
              {b.detail}
            </span>
          )}
        </div>
      ),
    },
    {
      key: 'by',
      header: t('bookings.page.colBookedBy'),
      sortable: true,
      value: (b) => bookedBy(b.created_by),
      render: (b) => (
        <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12.5px] text-text-mid">
          {bookedBy(b.created_by) === 'AI' && <Bot size={12} className="text-text-muted" aria-hidden />}
          {bookedBy(b.created_by)}
        </span>
      ),
    },
    {
      key: 'actions',
      header: t('bookings.page.colActions'),
      srHeader: true,
      align: 'right',
      // fixed slots so the icons line up row to row whichever ones a row has
      render: (b) => (
        <div className="ml-auto grid w-[148px] grid-cols-[32px_32px_76px] items-center justify-items-end gap-1">
          {!b.thread ? (
            <span aria-hidden />
          ) : (
            <Link
              href={threadHref(b.thread, b.thread_id)}
              title={t('bookings.page.openConversation')}
              aria-label={t('bookings.page.openConversationWith', { name: b.contact_name || t('bookings.page.thisContact') })}
              className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
            >
              <MessageSquare size={14} aria-hidden />
            </Link>
          )}
          {!b.event_link ? (
            <span aria-hidden />
          ) : (
            <a
              href={b.event_link}
              target="_blank"
              rel="noreferrer"
              title={t('bookings.page.openEvent')}
              aria-label={t('bookings.page.openEvent')}
              className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
            >
              <ExternalLink size={14} aria-hidden />
            </a>
          )}
          {canCancel && b.status === 'booked' && tab === 'upcoming' ? (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setNotice(null)
                setConfirm(b)
              }}
            >
              {t('common.actions.cancel')}
            </Button>
          ) : (
            <span aria-hidden />
          )}
        </div>
      ),
    },
  ]

  const choose = (next: Tab) => {
    setTab(next)
    setStatus(defaultStatus(next))
    setNotice(null)
    syncUrl(next, contactId)
  }

  return (
    <AreaPage
      title={t('shell.nav.bookings')}
      eyebrow={t('shell.groups.work')}
      Icon={CalendarCheck}
      subtitle={t('bookings.page.subtitle')}
    >
      {ready && !allowed ? (
        <Card className="px-5 py-6 text-[13px] text-text-muted">
          {t('bookings.page.noAccess')}
        </Card>
      ) : (
        <div className="flex flex-col gap-4 pb-10">
          <div className="-mx-1 overflow-x-auto px-1">
            <div data-tour="bookings-tabs" role="tablist" aria-label={t('shell.nav.bookings')} className="flex min-w-max gap-5 border-b border-line">
              {TABS.map((x) => {
                const active = tab === x.id
                return (
                  <button
                    key={x.id}
                    role="tab"
                    aria-selected={active}
                    onClick={() => choose(x.id)}
                    className={`-mb-px border-b-2 pb-2 pt-1 text-[13.5px] font-semibold transition-colors ${
                      active ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
                    }`}
                  >
                    {t(x.label)}
                  </button>
                )
              })}
            </div>
          </div>

          {tab === 'calendar' ? (
            <GoogleCalendarView entityQuery={entityQ} />
          ) : (
            <>
            {contactId != null && (
              <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-text-mid">
                <span className="inline-flex items-center gap-1.5 rounded-full border border-line-strong bg-card py-0.5 pl-2.5 pr-1">
                  <Trans
                    k="bookings.page.contactChip"
                    values={{ name: <b className="font-semibold text-text-high">{contactName || `#${contactId}`}</b> }}
                  />
                  <button
                    type="button"
                    onClick={() => {
                      setContactId(null)
                      syncUrl(tab, null)
                    }}
                    aria-label={t('bookings.page.showAll')}
                    className="grid h-5 w-5 place-items-center rounded-full text-text-muted hover:bg-fill-hover hover:text-text-high"
                  >
                    <X size={11} aria-hidden />
                  </button>
                </span>
              </div>
            )}

            {notice && (
              <div role="status" className="rounded-lg border border-green/30 bg-green/10 px-3 py-2 text-[12.5px] text-[color:var(--green-ink)]">
                {notice}
              </div>
            )}
            {loadNote && <div className="rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12.5px] text-text-muted">{loadNote}</div>}

            {rows === null ? (
              <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
                <Loader2 size={14} className="animate-spin" aria-hidden /> {t('bookings.page.loading')}
              </div>
            ) : (
              <DataTable
                data-tour="bookings-table"
                key={tab}
                columns={columns}
                rows={visible}
                rowKey={(b) => b.id}
                searchKeys={['contact']}
                searchPlaceholder={t('bookings.page.searchPlaceholder')}
                pageSize={20}
                initialSort={{ key: 'when', dir: tab === 'upcoming' ? 'asc' : 'desc' }}
                toolbarRight={
                  <>
                    <Select
                      value={status}
                      onChange={setStatus}
                      options={STATUS_OPTIONS.map((o) => ({ value: o.value, label: t(o.label) }))}
                      ariaLabel={t('common.fields.status')}
                      className="w-[138px] sm:w-[160px]"
                    />
                    <Select
                      value={calendar}
                      onChange={setCalendar}
                      options={[{ value: '', label: t('bookings.page.allCalendars') }, ...calendars.map((c) => ({ value: c, label: c }))]}
                      ariaLabel={t('bookings.page.calendarAria')}
                      className="w-[148px] sm:w-[170px]"
                    />
                    <Button data-tour="bookings-refresh" variant="ghost" size="sm" onClick={() => void load()} disabled={loading} aria-label={t('common.actions.refresh')}>
                      <RefreshCw size={13} className={loading ? 'animate-spin' : ''} aria-hidden />
                    </Button>
                  </>
                }
                empty={
                  <span className="inline-flex flex-col items-center gap-1.5">
                    <CalendarCheck size={20} className="text-text-faint" aria-hidden />
                    <span>
                      {t(
                        (rows ?? []).length > 0
                          ? 'bookings.page.emptyFiltered'
                          : tab === 'upcoming'
                            ? 'bookings.page.emptyUpcoming'
                            : 'bookings.page.emptyPast',
                      )}
                    </span>
                    {(rows ?? []).length === 0 && (
                      <span className="max-w-[440px] text-[12px] text-text-faint">
                        <Trans
                          k="bookings.page.emptyHint"
                          values={{
                            link: (
                              <Link href="/skills" className="font-semibold text-text-muted underline-offset-2 hover:underline">
                                {t('shell.nav.skills')}
                              </Link>
                            ),
                          }}
                        />
                      </span>
                    )}
                  </span>
                }
              />
            )}
            </>
          )}
        </div>
      )}

      <CancelBookingModal
        booking={confirm}
        onClose={() => setConfirm(null)}
        onCancelled={(b) => {
          setConfirm(null)
          setNotice(
            b.contact_name
              ? t('bookings.page.cancelledWith', { label: bookingLabel(b), name: b.contact_name })
              : t('bookings.card.cancelled', { label: bookingLabel(b) }),
          )
          void load()
        }}
      />
    </AreaPage>
  )
}
