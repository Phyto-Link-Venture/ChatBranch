'use client'

import { Suspense, useEffect } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { SETTINGS_TABS } from '@/components/settings/SettingsNav'

/**
 * /settings is a redirect: every tab is its own route (/settings/<id>) so a
 * refresh keeps your place. Pre-route `?tab=` links (backend notification links,
 * old bookmarks) land here and are mapped through — including legacy tab ids
 * that predate the current tab list.
 */
const LEGACY_TABS: Record<string, string> = {
  whatsapp: 'channels', // pre-consolidation WhatsApp Accounts tab
  voice: 'ai',          // pre-consolidation Voice Persona tab
}

function Redirector() {
  const router = useRouter()
  const params = useSearchParams()

  useEffect(() => {
    const q = (params.get('tab') || '').toLowerCase()
    const mapped = LEGACY_TABS[q] || q
    const valid = SETTINGS_TABS.some((t) => t.id === mapped)
    router.replace(`/settings/${valid ? mapped : SETTINGS_TABS[0].id}`)
  }, [params, router])

  return null
}

export default function SettingsPage() {
  return (
    <Suspense fallback={null}>
      <Redirector />
    </Suspense>
  )
}
