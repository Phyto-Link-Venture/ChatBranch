'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/** Knowledge Base moved to its own sidebar route — it is content, not config. */
export default function KnowledgeSettingsRedirect() {
  const router = useRouter()
  useEffect(() => {
    router.replace('/knowledge')
  }, [router])
  return null
}
