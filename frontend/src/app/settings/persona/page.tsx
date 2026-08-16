'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/**
 * The Chat Persona tab was removed: the assistant ships with a safe default
 * persona and the operator shapes its behaviour through AI Skills and the
 * Knowledge Base instead. Old links land on Settings.
 */
export default function PersonaSettingsRedirect() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/settings')
  }, [router])
  return null
}
