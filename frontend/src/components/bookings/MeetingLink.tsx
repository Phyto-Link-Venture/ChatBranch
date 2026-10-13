'use client'

import { Video } from 'lucide-react'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useT, type TFunction } from '@/i18n'
import type { Booking } from '@/lib/bookings'

/** "Zoom" / "Google Meet" (brand names) or the translated "meeting" for the skill's own link. */
export function meetingName(t: TFunction, provider: string | null | undefined): string {
  if (provider === 'zoom') return 'Zoom'
  if (provider === 'google_meet') return 'Google Meet'
  return t('bookings.meeting.generic')
}

/**
 * A booking's video meeting (made with the booking: a Zoom meeting, a Google Meet or the calendar
 * skill's own link) as a "Join …" link in a new tab. Nothing when the booking has none.
 */
export function MeetingLink({ booking, className = '' }: { booking: Booking; className?: string }) {
  const t = useT()
  if (!booking.meeting_url) return null
  return (
    <a
      href={booking.meeting_url}
      target="_blank"
      rel="noreferrer"
      title={booking.meeting_url}
      className={`inline-flex min-w-0 items-center gap-1 text-[11.5px] font-semibold text-text-mid hover:text-text-high hover:underline ${className}`}
    >
      {booking.meeting_provider === 'zoom' ? (
        <ChannelLogo channel="zoom" size={12} title="" />
      ) : (
        <Video size={12} className="shrink-0" aria-hidden />
      )}
      <span className="truncate">{t('bookings.meeting.join', { name: meetingName(t, booking.meeting_provider) })}</span>
    </a>
  )
}
