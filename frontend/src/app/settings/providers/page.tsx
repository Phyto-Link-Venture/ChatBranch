'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/** Service Providers moved to the sidebar (/providers). Keep the old route as a redirect. */
export default function ProvidersSettingsPage() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/providers')
  }, [router])
  return null
}
