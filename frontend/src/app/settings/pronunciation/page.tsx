'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/** Pronunciation moved to the Voice & Calls page (/persona). Keep the old route as a redirect. */
export default function PronunciationSettingsPage() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/persona#pronunciation')
  }, [router])
  return null
}
