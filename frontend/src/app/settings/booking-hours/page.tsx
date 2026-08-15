'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/**
 * Booking Hours moved into the Google Calendar integration — it configures
 * that integration (the slots the AI books against), not the app globally.
 * Keep this route as a redirect so old links / bookmarks still land somewhere
 * useful.
 */
export default function BookingHoursSettingsPage() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/settings/integrations?booking-hours=1')
  }, [router])
  return null
}
