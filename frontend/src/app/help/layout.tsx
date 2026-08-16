import type { ReactNode } from 'react'

/**
 * The Help Center is PUBLIC — no auth, no operator chrome. It renders outside
 * the dashboard shell (see Shell.tsx PUBLIC_ROUTES) so a customer reading the
 * refund policy is never redirected to a login page.
 */
export const metadata = {
  title: 'Help Center',
  description: 'Answers to common questions.',
}

export default function HelpLayout({ children }: { children: ReactNode }) {
  return <div className="min-h-screen bg-app text-text-primary">{children}</div>
}
