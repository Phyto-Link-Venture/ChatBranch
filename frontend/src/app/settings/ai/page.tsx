'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/**
 * AI Configuration was retired: quiet hours, send pacing and the voice-call
 * brain / STT engine are configured PER CHANNEL ACCOUNT (Channels → edit
 * account → Delivery & AI), so one WhatsApp number can pace and hush itself
 * independently of another. Kept as a redirect for old links / bookmarks.
 */
export default function AiSettingsPage() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/channels')
  }, [router])
  return null
}
