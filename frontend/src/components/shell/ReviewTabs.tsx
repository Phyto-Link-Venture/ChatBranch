'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'

/**
 * Shared pill-tab row for the three "Review" screens (design.md): Analytics /
 * Call Logs / Activity all sit under one tab strip. Active tab =
 * solid emerald fill. Each tab is its own route; this renders at the top of
 * each of those pages so switching feels like one section.
 */
const REVIEW_TABS = [
  { href: '/analytics', lbl: 'Analytics' },
  { href: '/call-logs', lbl: 'Call Logs' },
  { href: '/activity', lbl: 'Activity' },
]

export function ReviewTabs() {
  const pathname = usePathname() || ''
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {REVIEW_TABS.map((t) => {
        const active = pathname === t.href || pathname.startsWith(t.href + '/')
        return (
          <Link
            key={t.href}
            href={t.href}
            aria-current={active ? 'page' : undefined}
            className="rounded-full px-4 py-1.5 text-[12.5px] font-semibold transition-colors"
            style={
              active
                ? { background: 'linear-gradient(135deg,#10B981,#0D9668)', color: '#ffffff' }
                : { background: 'var(--fill)', color: 'var(--text-muted)' }
            }
          >
            {t.lbl}
          </Link>
        )
      })}
    </div>
  )
}
