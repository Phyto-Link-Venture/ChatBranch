'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'

/**
 * Client redirect used by the retired Settings sub-routes (upgrade §1): the
 * area moved to a top-level route, but old `/settings/<id>` links still land
 * here and bounce forward.
 */
export function LegacyRedirect({ to }: { to: string }) {
  const router = useRouter()
  useEffect(() => {
    // Preserve query/hash so e.g. an OAuth callback landing on the old
    // /settings/integrations?connected=google URL keeps its params.
    const suffix =
      typeof window !== 'undefined' ? window.location.search + window.location.hash : ''
    router.replace(to + suffix)
  }, [router, to])
  return null
}
