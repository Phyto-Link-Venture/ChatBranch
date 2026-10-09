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
import { intlTag, useI18n, useT, type TFunction } from '@/i18n'

/** `bookingLabel` in the current language — "Tue 14 Oct, 15:00" (the calendar's timezone). */
export function useBookingLabel() {
  const { locale } = useI18n()
  const intl = intlTag(locale)
  return (b: Pick<Booking, 'label' | 'start' | 'timezone'>) => bookingLabel(b, intl)
}

/** The calendar's own name, else Google Calendar / ChatBranch bookings. */
export const calendarName = (t: TFunction, b: Pick<Booking, 'calendar_label' | 'source'>) =>
  b.calendar_label || (b.source === 'google' ? 'Google Calendar' : t('bookings.chatbranchCalendar'))

const TONE: Record<string, string> = {
  green: 'bg-green/10 text-green-300',
  amber: 'bg-amber/15 text-amber-300',
  red: 'bg-red/10 text-red-300',
  muted: 'bg-fill text-text-muted',
}

export function BookingStatusChip({ booking: b }: { booking: Pick<Booking, 'status' | 'detail'> }) {
  const t = useT()
  const m = BOOKING_STATUS[b.status]
  const label = m ? t(m.label) : b.status
  const hint = m ? t(m.hint) : ''
  return (
    <span
      title={b.detail ? `${hint} ${b.detail}`.trim() : hint}
      className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${TONE[m?.tone ?? 'muted']}`}
    >
      {label}
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
  const t = useT()
  const label = useBookingLabel()
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
      if (r && r.ok === false) throw new Error(r.detail || t('bookings.cancel.failed'))
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
      title={t('bookings.cancel.title')}
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={busy}>
            {t('bookings.cancel.keep')}
          </Button>
          <Button variant="danger" onClick={run} disabled={busy}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('bookings.cancel.confirm')}
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
            <div className="text-[13.5px] font-semibold text-text-heading">{label(booking)}</div>
            <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-[12px] text-text-muted">
              <CalendarMark source={booking.source} size={12} />
              {calendarName(t, booking)}
              {booking.timezone && <span>· {booking.timezone}</span>}
            </div>
            {(booking.contact_name || booking.title) && (
              <div className="mt-1 truncate text-[12px] text-text-mid">{booking.title || booking.contact_name}</div>
            )}
          </div>
          <p className="text-[12.5px] leading-relaxed text-text-mid">
            {t(booking.source === 'google' ? 'bookings.cancel.googleNote' : 'bookings.cancel.localNote')}
          </p>
        </div>
      )}
    </Modal>
  )
}
