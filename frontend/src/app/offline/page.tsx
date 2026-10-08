import type { Metadata } from 'next'
import { Logo } from '@/components/shell/Logo'
import { RetryButton } from './RetryButton'

export const metadata: Metadata = { title: 'Offline · ChatBranch' }

/**
 * The service worker's navigation fallback (public/sw.js precaches it). Static
 * and self-contained: rendered outside the operator shell (Shell PUBLIC_ROUTES)
 * so it needs no session and no API. The worker redirects a failed navigation
 * here with ?from=<where you were going>; Try again — or the browser coming
 * back online — returns there.
 */
export default function OfflinePage() {
  return (
    <div className="flex min-h-dvh items-center justify-center bg-app px-4 py-10">
      <div className="w-full max-w-[420px]">
        <div className="mb-8 flex justify-center">
          <Logo size={30} textClassName="text-[26px]" />
        </div>
        <div className="rounded-xl border border-line bg-card px-6 py-7 sm:px-8">
          <div className="eyebrow">No connection</div>
          <h1 className="display mt-1 text-[32px] leading-[1.05] text-text-heading">You&apos;re offline</h1>
          <div className="mt-4 border-t border-text-primary/80" />
          <p className="mt-4 text-[14px] leading-relaxed text-text-mid">
            ChatBranch needs the network to load this page. Check your connection — conversations keep
            arriving on the server and will be waiting when you&apos;re back.
          </p>
          <RetryButton />
        </div>
      </div>
    </div>
  )
}
