'use client'

/**
 * Small pieces shared by the bookings card (chat info panel, contact page) and
 * the /bookings page: the status chip, the calendar mark and the operator's
 * cancel confirmation. Token-only colours.
 */

import { useState } from 'react'
import { CalendarCheck, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { Modal } from '@/components/ui/Modal'
import { BOOKING_STATUS, bookingLabel, cancelBooking, type Booking } from '@/lib/bookings'

const TONE: Record<string, string> = {
  green: 'bg-green/10 text-green-300',
  amber: 'bg-amber/15 text-amber-300',
  red: 'bg-red/10 text-red-300',
  muted: 'bg-fill text-text-muted',
}

export function BookingStatusChip({ booking: b }: { booking: Pick<Booking, 'status' | 'detail'> }) {
  const m = BOOKING_STATUS[b.status] ?? { label: b.status, tone: 'muted', hint: '' }
  return (
    <span
      title={b.detail ? `${m.hint} ${b.detail}`.trim() : m.hint}
      className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${TONE[m.tone]}`}
    >
      {m.label}
    </span>
  )
}

/** Google Calendar's mark, or ChatBranch's own bookings glyph. */
export function CalendarMark({ source, size = 13 }: { source: string; size?: number }) {
  return source === 'google' ? (
    <ChannelLogo channel="google_calendar" size={size} title="" />
  ) : (
    <CalendarCheck size={size} className="shrink-0 text-text-muted" aria-hidden />
  )
}

/** Confirm + run an operator cancel. Errors render inside the modal. */
export function CancelBookingModal({
  booking,
  onClose,
  onCancelled,
}: {
  booking: Booking | null
  onClose: () => void
  onCancelled: (b: Booking) => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const close = () => {
    if (busy) return
    setError(null)
    onClose()
  }

  const run = async () => {
    if (!booking) return
    setBusy(true)
    setError(null)
    try {
      const r = await cancelBooking(booking.id)
      if (r && r.ok === false) throw new Error(r.detail || 'The booking could not be cancelled.')
      onCancelled(booking)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={!!booking}
      onClose={close}
      title="Cancel this booking?"
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={busy}>
            Keep it
          </Button>
          <Button variant="danger" onClick={run} disabled={busy}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Cancel booking
          </Button>
        </>
      }
    >
      {error && (
        <div
          role="alert"
          className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          {error}
        </div>
      )}
      {booking && (
        <div className="flex flex-col gap-3">
          <div className="rounded-lg border border-line bg-well px-3 py-2.5">
            <div className="text-[13.5px] font-semibold text-text-heading">{bookingLabel(booking)}</div>
            <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-[12px] text-text-muted">
              <CalendarMark source={booking.source} size={12} />
              {booking.calendar_label || (booking.source === 'google' ? 'Google Calendar' : 'ChatBranch bookings')}
              {booking.timezone && <span>· {booking.timezone}</span>}
            </div>
            {(booking.contact_name || booking.title) && (
              <div className="mt-1 truncate text-[12px] text-text-mid">{booking.title || booking.contact_name}</div>
            )}
          </div>
          <p className="text-[12.5px] leading-relaxed text-text-mid">
            {booking.source === 'google' ? 'The event is removed from Google Calendar' : 'The time is freed'} and a
            note is added to the conversation. The customer isn’t messaged automatically — let them know in the chat.
          </p>
        </div>
      )}
    </Modal>
  )
}
