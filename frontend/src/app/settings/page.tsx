'use client'

import { Suspense, useEffect } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { SETTINGS_TABS } from '@/components/settings/SettingsNav'
import { MOVED_AREA_ROUTES } from '@/components/settings/movedAreas'

/**
 * /settings is a redirect: every remaining tab is its own route (/settings/<id>)
 * so a refresh keeps your place. Pre-route `?tab=` links (backend notification
 * links, old bookmarks) land here and are mapped through — including areas that
 * were promoted out of Settings to top-level routes (upgrade §1) and a couple
 * of legacy tab ids.
 */
const LEGACY_TABS: Record<string, string> = {
  voice: 'ai', // pre-consolidation Voice Persona tab
}

function Redirector() {
  const router = useRouter()
  const params = useSearchParams()

  useEffect(() => {
    const q = (params.get('tab') || '').toLowerCase()
    // Areas moved to top-level routes win first.
    if (MOVED_AREA_ROUTES[q]) {
      router.replace(MOVED_AREA_ROUTES[q])
      return
    }
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
