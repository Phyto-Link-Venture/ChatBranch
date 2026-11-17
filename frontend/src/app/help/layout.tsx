import type { Metadata } from 'next'
import { INDEXABLE, SITE_ORIGIN } from '@/lib/publicSite'
import type { ReactNode } from 'react'

/**
 * The Help Center is PUBLIC — no auth, no operator chrome. It renders outside
 * the dashboard shell (see Shell.tsx PUBLIC_ROUTES) so a customer reading the
 * refund policy is never redirected to a login page.
 */
export const metadata: Metadata = {
  metadataBase: new URL(SITE_ORIGIN),
  title: 'ChatBranch Help Center',
  description:
    'Help for ChatBranch, the shared inbox for WhatsApp, email, social and website chat with an AI that answers customers: guides, troubleshooting and the full user manual.',
  alternates: { canonical: '/help' },
  robots: INDEXABLE ? undefined : { index: false, follow: false },
}

export default function HelpLayout({ children }: { children: ReactNode }) {
  return <div className="min-h-screen bg-app text-text-primary">{children}</div>
}
