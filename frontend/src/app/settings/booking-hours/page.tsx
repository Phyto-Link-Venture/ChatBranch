'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/**
 * The old global "Booking hours" setting was read by nothing once booking
 * moved into AI skills: each calendar booking skill (Connected app → Calendar
 * on /skills) carries its own working hours, notice, buffer and slot length.
 * Old links land there.
 */
export default function BookingHoursSettingsPage() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/skills')
  }, [router])
  return null
}
