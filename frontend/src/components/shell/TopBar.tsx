'use client'

import { useEffect, useState } from 'react'
import { usePathname } from 'next/navigation'
import { Menu, Search } from 'lucide-react'
import type { AuthUser } from '@/lib/auth'
import { EntitySwitcher } from './EntitySwitcher'
import { NotificationsBell } from './NotificationsBell'
import { LiveChip } from './LiveChip'
import { NAV_GROUPS, SETTINGS_NAV, isActive } from './nav'
import { useT, type TFunction } from '@/i18n'

/** The page's name for the phone header (the rail is hidden there). */
function pageTitle(pathname: string, t: TFunction): string {
  for (const g of NAV_GROUPS) for (const n of g.items) if (isActive(pathname, n)) return t(n.lbl)
  if (isActive(pathname, SETTINGS_NAV)) return t(SETTINGS_NAV.lbl)
  return 'ChatBranch'
}

/**
 * Top bar, flush on the paper with a hairline under it: the search field
 * (opens the command palette — so does Ctrl/⌘+K anywhere), the Live chip, the
 * entity switcher and the notification bell. On a phone: menu button, the
 * page title in the display face, Live, a search button and the bell.
 */
export function TopBar({
  onOpenMobileNav,
  onOpenSearch,
}: {
  user?: AuthUser | null
  onOpenMobileNav?: () => void
  onOpenSearch: () => void
}) {
  const pathname = usePathname() || '/'
  const t = useT()
  // ⌘ on Apple keyboards, Ctrl elsewhere — resolved after mount (SSR-safe).
  const [mod, setMod] = useState('Ctrl')
  useEffect(() => {
    if (/Mac|iPhone|iPad/i.test(navigator.platform || navigator.userAgent)) setMod('⌘')
  }, [])

  return (
    <header className="flex h-[60px] shrink-0 items-center gap-2 border-b border-line px-3 sm:gap-3 sm:px-5 lg:px-7">
      <button
        onClick={onOpenMobileNav}
        aria-label="Open menu"
        className="-ml-1 grid h-10 w-10 shrink-0 place-items-center rounded-lg text-text-high transition-colors hover:bg-fill-hover lg:hidden"
      >
        <Menu size={20} strokeWidth={1.6} />
      </button>
      <span className="display min-w-0 truncate text-[22px] leading-none text-text-heading md:hidden">
        {pageTitle(pathname, t)}
      </span>

      {/* Search: a button that looks like a field — typing happens in the palette. */}
      <button
        type="button"
        onClick={onOpenSearch}
        aria-label="Search"
        aria-keyshortcuts="Control+K Meta+K"
        className="hidden h-[38px] min-w-0 max-w-[520px] flex-1 cursor-text items-center gap-2.5 rounded-lg border border-line-strong bg-card px-3 text-left transition-colors hover:border-text-faint md:flex"
      >
        <Search size={16} strokeWidth={1.6} className="shrink-0 text-text-muted" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-[13.5px] text-text-faint">
          Search contacts, chats, flows, templates…
        </span>
        <kbd className="shrink-0 rounded border border-line px-1.5 py-px font-sans text-[11px] font-semibold text-text-muted">
          {mod} K
        </kbd>
      </button>

      <div className="ml-auto flex shrink-0 items-center gap-1.5 sm:gap-2.5">
        <LiveChip />
        <button
          type="button"
          onClick={onOpenSearch}
          aria-label="Search"
          className="grid h-10 w-10 place-items-center rounded-lg text-text-high transition-colors hover:bg-fill-hover md:hidden"
        >
          <Search size={18} strokeWidth={1.6} />
        </button>
        <div className="hidden sm:block">
          <EntitySwitcher />
        </div>
        <NotificationsBell />
      </div>
    </header>
  )
}
