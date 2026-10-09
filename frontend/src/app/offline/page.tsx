import type { Metadata } from 'next'
import { OfflineCard } from './OfflineCard'

// The tab title is static (server-rendered, cached by the worker) and stays English.
export const metadata: Metadata = { title: 'Offline · ChatBranch' }

/**
 * The service worker's navigation fallback (public/sw.js precaches it). Static
 * and self-contained: rendered outside the operator shell (Shell PUBLIC_ROUTES)
 * so it needs no session and no API. The worker redirects a failed navigation
 * here with ?from=<where you were going>; Try again — or the browser coming
 * back online — returns there. The copy is translated client-side (OfflineCard).
 */
export default function OfflinePage() {
  return <OfflineCard />
}
