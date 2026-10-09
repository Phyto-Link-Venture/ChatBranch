'use client'

/**
 * "Bookings" — a contact's upcoming appointments, in the chat info panel and on
 * the contact page (one component, two densities). Lists what the AI (or a
 * teammate) booked or is holding for them; a booked one can be cancelled
 * (chats.send) after a confirmation. Fails soft: no API / no access reads as
 * "no upcoming bookings".
 */

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { ExternalLink, Loader2 } from 'lucide-react'
import { useAccess } from '@/lib/useAccess'
import { bookingLabel, isQuietError, listBookings, type Booking } from '@/lib/bookings'
import { BookingStatusChip, CalendarMark, CancelBookingModal } from './BookingBits'

export function BookingsCard({
  contactId,
  refreshToken,
  compact = false,
}: {
  contactId: number
  /** Any value that changes when the conversation updates (refetch trigger). */
  refreshToken?: unknown
  /** The chat info panel's tighter card. */
  compact?: boolean
}) {
  const { ready, can } = useAccess()
  const [rows, setRows] = useState<Booking[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [confirm, setConfirm] = useState<Booking | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setRows(await listBookings({ contact_id: contactId, upcoming: true }))
      setFailed(false)
    } catch (e) {
      setRows([])
      setFailed(!isQuietError(e))
    }
  }, [contactId])

  useEffect(() => {
    setNotice(null)
    void load()
  }, [load, refreshToken])

  const canCancel = ready && can('chats.send')
  const shown = (rows ?? []).filter((b) => b.status !== 'released')

  return (
    <section className={`rounded-xl border border-line bg-card ${compact ? 'p-3.5' : 'p-4'}`} aria-label="Bookings">
      <div className={`flex items-center justify-between gap-2 ${compact ? 'mb-2' : 'mb-3'}`}>
        <h3 className="eyebrow">Bookings</h3>
        <Link
          href={`/bookings?contact=${contactId}`}
          className="text-[11px] font-semibold text-text-muted hover:text-text-high hover:underline"
        >
          All bookings
        </Link>
      </div>

      {rows === null ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
        </div>
      ) : shown.length === 0 ? (
        <p className="text-[12px] leading-relaxed text-text-muted">
          {failed ? 'Bookings couldn’t be loaded right now.' : 'No upcoming bookings.'}
          {!failed && !compact && ' When the AI books an appointment with this contact, it shows here.'}
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-line">
          {shown.map((b) => (
            <li key={b.id} className="py-2 first:pt-0 last:pb-0">
              <div className="flex items-start justify-between gap-2">
                <span className="min-w-0 text-[12.5px] font-semibold leading-snug text-text-high">{bookingLabel(b)}</span>
                <BookingStatusChip booking={b} />
              </div>
              <div className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[11.5px] text-text-muted">
                <CalendarMark source={b.source} size={11} />
                <span className="truncate">
                  {b.calendar_label || (b.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings')}
                  {b.timezone ? ` · ${b.timezone}` : ''}
                </span>
              </div>
              {(b.event_link || (canCancel && b.status === 'booked')) && (
                <div className="mt-1 flex items-center gap-3">
                  {b.event_link && (
                    <a
                      href={b.event_link}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex items-center gap-1 text-[11px] font-semibold text-text-muted hover:text-text-high hover:underline"
                    >
                      Open event <ExternalLink size={10} aria-hidden />
                    </a>
                  )}
                  {canCancel && b.status === 'booked' && (
                    <button
                      type="button"
                      onClick={() => {
                        setNotice(null)
                        setConfirm(b)
                      }}
                      className="text-[11px] font-semibold text-text-muted transition-colors hover:text-[color:var(--red-ink)] hover:underline"
                    >
                      Cancel
                    </button>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      {notice && !confirm && (
        <p role="status" className="mt-2 text-[11.5px] font-semibold text-[color:var(--green-ink)]">
          {notice}
        </p>
      )}

      <CancelBookingModal
        booking={confirm}
        onClose={() => setConfirm(null)}
        onCancelled={(b) => {
          setConfirm(null)
          setNotice(`Cancelled ${bookingLabel(b)}.`)
          void load()
        }}
      />
    </section>
  )
}
