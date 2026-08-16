'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/** AI Skills moved to its own sidebar route — it is content, not config. */
export default function SkillsSettingsRedirect() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/skills')
  }, [router])
  return null
}
