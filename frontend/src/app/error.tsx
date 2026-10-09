'use client'

import { CrashPanel } from '@/components/ui/CrashPanel'

/**
 * A page that throws renders this inside the shell (sidebar and top bar stay
 * usable) instead of Next.js's bare "Application error".
 */
export default function PageError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return <CrashPanel error={error} reset={reset} />
}
