'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

// Script templates live in the Templates module now (Call scripts tab). Old
// links — and search results pointing at /script-templates?id=<id> — land on
// the tab with that script's editor open.
export default function ScriptTemplatesPage() {
  const router = useRouter()
  useEffect(() => {
    const id = new URLSearchParams(window.location.search).get('id')
    router.replace(`/templates?tab=scripts${id ? `&id=${encodeURIComponent(id)}` : ''}`)
  }, [router])
  return null
}
