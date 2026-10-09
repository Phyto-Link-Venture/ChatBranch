'use client'

/**
 * Bookings — every appointment the AI (through a "Connected app" calendar
 * skill) or the team booked, on Google Calendar or kept in ChatBranch.
 * Upcoming (soonest first) and Past (newest first) tabs, filters for status,
 * calendar and contact, a link into each conversation, and an operator cancel.
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
import { BookingStatusChip, CalendarMark, CancelBookingModal } from '@/components/bookings/BookingBits'
import { useAccess } from '@/lib/useAccess'
import { useEntityQuery } from '@/lib/EntityContext'
import { subscribeChatStream } from '@/lib/chatStream'
import {
  ACTIVE_STATUSES,
  BOOKING_STATUS,
  BookingsError,
  bookedBy,
  bookingLabel,
  isQuietError,
  listBookings,
  relative,
  threadHref,
  type Booking,
  type BookingStatus,
} from '@/lib/bookings'

type Tab = 'upcoming' | 'past'
const TABS: { id: Tab; label: string }[] = [
  { id: 'upcoming', label: 'Upcoming' },
  { id: 'past', label: 'Past' },
]

const STATUS_OPTIONS = [
  { value: 'active', label: 'Booked or held' },
  { value: 'all', label: 'All statuses' },
  ...(Object.keys(BOOKING_STATUS) as BookingStatus[]).map((s) => ({ value: s, label: BOOKING_STATUS[s].label })),
]

const defaultStatus = (t: Tab) => (t === 'upcoming' ? 'active' : 'all')

export default function BookingsPage() {
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
    const t = p.get('tab') as Tab | null
    if (t && TABS.some((x) => x.id === t)) {
      setTab(t)
      setStatus(defaultStatus(t))
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
          ? 'Bookings aren’t available on this server yet.'
          : `Bookings couldn’t be loaded — ${e instanceof Error ? e.message : String(e)}`,
      )
    } finally {
      setLoading(false)
    }
  }, [tab, contactId, entityQ])

  useEffect(() => {
    if (!allowed) return
    setRows(null)
    void load()
  }, [allowed, load])

  // live: the inbox stream bumps whenever a conversation moves (a reply that
  // books or cancels is a new message) — debounced refetch
  useEffect(() => {
    if (!allowed) return
    let t: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(t)
      t = window.setTimeout(() => void load(), 800)
    })
    return () => {
      unsub()
      window.clearTimeout(t)
    }
  }, [allowed, load])

  const calendars = useMemo(
    () =>
      Array.from(
        new Set((rows ?? []).map((b) => b.calendar_label || (b.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings'))),
      ).sort(),
    [rows],
  )

  const visible = useMemo(() => {
    const now = Date.now()
    return (rows ?? []).filter((b) => {
      if (status === 'active' && !ACTIVE_STATUSES.includes(b.status)) return false
      if (status !== 'active' && status !== 'all' && b.status !== status) return false
      const cal = b.calendar_label || (b.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings')
      if (calendar && cal !== calendar) return false
      // belt and braces for the tab split (the server already filters)
      const t = Date.parse(/[zZ]$|[+-]\d\d:\d\d$/.test(b.start) ? b.start : `${b.start}Z`)
      if (!Number.isNaN(t) && tab === 'past' && t > now) return false
      return true
    })
  }, [rows, status, calendar, tab])

  const contactName = contactId != null ? (rows ?? []).find((b) => b.contact_id === contactId)?.contact_name : undefined
  const canCancel = ready && can('chats.send')

  const columns: Column<Booking>[] = [
    {
      key: 'when',
      header: 'When',
      sortable: true,
      value: (b) => b.start,
      render: (b) => (
        <div className="min-w-[150px]">
          <div className="font-semibold text-text-heading">{bookingLabel(b)}</div>
          <div className="text-[11.5px] text-text-muted">
            {relative(b.start)}
            {b.timezone ? ` · ${b.timezone}` : ''}
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
      header: 'Contact',
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
      header: 'Calendar',
      sortable: true,
      value: (b) => b.calendar_label,
      render: (b) => (
        <span className="inline-flex min-w-[130px] items-center gap-1.5 text-[12.5px] text-text-mid">
          <CalendarMark source={b.source} />
          <span className="truncate">{b.calendar_label || (b.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings')}</span>
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      value: (b) => BOOKING_STATUS[b.status]?.label ?? b.status,
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
      header: 'Booked by',
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
      header: 'Actions',
      srHeader: true,
      align: 'right',
      // fixed slots so the icons line up row to row whichever ones a row has
      render: (b) => (
        <div className="ml-auto grid w-[148px] grid-cols-[32px_32px_76px] items-center justify-items-end gap-1">
          {!b.thread ? (
            <span aria-hidden />
          ) : (
            <Link
              href={threadHref(b.thread)}
              title="Open the conversation"
              aria-label={`Open the conversation with ${b.contact_name || 'this contact'}`}
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
              title="Open the calendar event"
              aria-label="Open the calendar event"
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
              Cancel
            </Button>
          ) : (
            <span aria-hidden />
          )}
        </div>
      ),
    },
  ]

  const choose = (t: Tab) => {
    setTab(t)
    setStatus(defaultStatus(t))
    setNotice(null)
    syncUrl(t, contactId)
  }

  return (
    <AreaPage
      title="Bookings"
      eyebrow="Work"
      Icon={CalendarCheck}
      subtitle="Appointments the AI and your team booked — on Google Calendar or kept in ChatBranch."
    >
      {ready && !allowed ? (
        <Card className="px-5 py-6 text-[13px] text-text-muted">
          You don’t have access to bookings. Ask an admin for the “View chats” permission.
        </Card>
      ) : (
        <div className="flex flex-col gap-4 pb-10">
          <div className="-mx-1 overflow-x-auto px-1">
            <div role="tablist" aria-label="Bookings" className="flex min-w-max gap-5 border-b border-line">
              {TABS.map((t) => {
                const active = tab === t.id
                return (
                  <button
                    key={t.id}
                    role="tab"
                    aria-selected={active}
                    onClick={() => choose(t.id)}
                    className={`-mb-px border-b-2 pb-2 pt-1 text-[13.5px] font-semibold transition-colors ${
                      active ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
                    }`}
                  >
                    {t.label}
                  </button>
                )
              })}
            </div>
          </div>

          {contactId != null && (
            <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-text-mid">
              <span className="inline-flex items-center gap-1.5 rounded-full border border-line-strong bg-card py-0.5 pl-2.5 pr-1">
                Contact: <b className="font-semibold text-text-high">{contactName || `#${contactId}`}</b>
                <button
                  type="button"
                  onClick={() => {
                    setContactId(null)
                    syncUrl(tab, null)
                  }}
                  aria-label="Show every contact’s bookings"
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
              <Loader2 size={14} className="animate-spin" aria-hidden /> Loading bookings…
            </div>
          ) : (
            <DataTable
              key={tab}
              columns={columns}
              rows={visible}
              rowKey={(b) => b.id}
              searchKeys={['contact']}
              searchPlaceholder="Search contact or title…"
              pageSize={20}
              initialSort={{ key: 'when', dir: tab === 'upcoming' ? 'asc' : 'desc' }}
              toolbarRight={
                <>
                  <Select
                    value={status}
                    onChange={setStatus}
                    options={STATUS_OPTIONS}
                    ariaLabel="Status"
                    className="w-[138px] sm:w-[160px]"
                  />
                  <Select
                    value={calendar}
                    onChange={setCalendar}
                    options={[{ value: '', label: 'All calendars' }, ...calendars.map((c) => ({ value: c, label: c }))]}
                    ariaLabel="Calendar"
                    className="w-[148px] sm:w-[170px]"
                  />
                  <Button variant="ghost" size="sm" onClick={() => void load()} disabled={loading} aria-label="Refresh">
                    <RefreshCw size={13} className={loading ? 'animate-spin' : ''} aria-hidden />
                  </Button>
                </>
              }
              empty={
                <span className="inline-flex flex-col items-center gap-1.5">
                  <CalendarCheck size={20} className="text-text-faint" aria-hidden />
                  <span>
                    {(rows ?? []).length > 0
                      ? 'No bookings match these filters.'
                      : tab === 'upcoming'
                        ? 'No upcoming bookings.'
                        : 'No past bookings.'}
                  </span>
                  {(rows ?? []).length === 0 && (
                    <span className="max-w-[440px] text-[12px] text-text-faint">
                      Give the AI a calendar on{' '}
                      <Link href="/skills" className="font-semibold text-text-muted underline-offset-2 hover:underline">
                        AI skills
                      </Link>{' '}
                      (a Connected app) and the appointments it books land here.
                    </span>
                  )}
                </span>
              }
            />
          )}
        </div>
      )}

      <CancelBookingModal
        booking={confirm}
        onClose={() => setConfirm(null)}
        onCancelled={(b) => {
          setConfirm(null)
          setNotice(`Cancelled ${bookingLabel(b)}${b.contact_name ? ` with ${b.contact_name}` : ''}.`)
          void load()
        }}
      />
    </AreaPage>
  )
}
