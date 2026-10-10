'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useMemo, useRef, useState } from 'react'
import { ChevronDown, LogOut } from 'lucide-react'
import type { AuthUser } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import { useT } from '@/i18n'
import { Logo } from './Logo'
import { ThemeToggle } from './ThemeToggle'
import { SETTINGS_NAV, isActive, visibleGroups, type NavDef } from './nav'
import { useLive } from './liveStore'

/**
 * The "top navigation" layout (Settings → Appearance → Layout): on desktop the
 * sidebar gives way to this bar — the logo, the ungrouped pages as links, each
 * sidebar group as a menu, then Settings, the light/dark switch and sign out.
 * Built from the same `nav.ts` groups (and `canSee` gating) as the sidebar.
 *
 * Always rendered, shown only by CSS (`html[data-nav='top'] .top-nav` at lg+,
 * globals.css), so the layout applies before first paint and phones keep the
 * drawer + bottom tab bar.
 */
export function TopNav({ user, onSignOut }: { user: AuthUser | null; onSignOut: () => void }) {
  const t = useT()
  const pathname = usePathname() || '/dashboard'
  const entityQ = useEntityQuery()
  const { data: live } = useLive(entityQ)
  const groups = useMemo(() => visibleGroups(user), [user])
  const [open, setOpen] = useState<string | null>(null)
  const ref = useRef<HTMLElement>(null)

  useEffect(() => setOpen(null), [pathname])
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(null)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(null)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const waiting = live?.waiting_count ?? 0
  const head = groups.filter((g) => !g.label).flatMap((g) => g.items)
  const menus = groups.filter((g) => g.label)

  const link = (n: NavDef, inMenu: boolean) => {
    const active = isActive(pathname, n)
    const count = n.counter === 'waiting' ? waiting : 0
    return (
      <Link
        key={n.href}
        href={n.href}
        role={inMenu ? 'menuitem' : undefined}
        aria-current={active ? 'page' : undefined}
        className={`flex items-center gap-2 whitespace-nowrap rounded-lg text-[13.5px] transition-colors ${
          inMenu ? 'px-2.5 py-2' : 'h-9 px-2.5'
        } ${
          active
            ? 'bg-[var(--nav-active-bg)] font-semibold text-[color:var(--nav-active-text)]'
            : 'text-text-mid hover:bg-fill-hover hover:text-text-heading'
        }`}
      >
        <n.Icon size={16} strokeWidth={1.7} className="shrink-0" aria-hidden />
        <span className={inMenu ? 'min-w-0 flex-1 truncate' : 'hidden xl:inline'}>{t(n.lbl)}</span>
        {count > 0 && (
          <span className="shrink-0 rounded-full bg-primary px-[7px] py-px text-[11px] font-semibold tabular-nums text-on-primary">
            {count > 99 ? '99+' : count}
          </span>
        )}
      </Link>
    )
  }

  return (
    <nav
      ref={ref}
      aria-label={t('shell.sideNav.mainNavigation')}
      className="top-nav relative z-30 h-[52px] shrink-0 items-center gap-1 border-b border-line bg-surface px-4"
    >
      <Link href="/dashboard" className="mr-3 flex shrink-0 items-center rounded-md" aria-label={t('shell.sideNav.homeLink')}>
        <Logo />
      </Link>
      {head.map((n) => link(n, false))}
      {menus.map((g) => {
        const id = g.label!
        const isOpen = open === id
        const active = g.items.some((n) => isActive(pathname, n))
        const menuId = `top-nav-${id.split('.').pop()}`
        return (
          <div key={id} className="relative">
            <button
              type="button"
              aria-haspopup="menu"
              aria-expanded={isOpen}
              aria-controls={menuId}
              onClick={() => setOpen(isOpen ? null : id)}
              className={`flex h-9 items-center gap-1 whitespace-nowrap rounded-lg px-2.5 text-[13.5px] transition-colors ${
                active || isOpen ? 'font-semibold text-text-heading' : 'text-text-mid hover:text-text-heading'
              } ${isOpen ? 'bg-fill-hover' : 'hover:bg-fill-hover'}`}
            >
              {t(id)}
              {active && <span className="h-1.5 w-1.5 rounded-full bg-accent" aria-hidden />}
              <ChevronDown size={13} strokeWidth={2} className={`transition-transform ${isOpen ? 'rotate-180' : ''}`} aria-hidden />
            </button>
            {isOpen && (
              <div
                id={menuId}
                role="menu"
                className="absolute left-0 top-full z-50 mt-1 flex w-[240px] flex-col gap-0.5 rounded-xl border border-line-strong bg-card p-1.5 shadow-pop"
              >
                {g.items.map((n) => link(n, true))}
              </div>
            )}
          </div>
        )
      })}
      <div className="ml-auto flex shrink-0 items-center gap-1">
        <Link
          href={SETTINGS_NAV.href}
          aria-current={isActive(pathname, SETTINGS_NAV) ? 'page' : undefined}
          title={t(SETTINGS_NAV.lbl)}
          aria-label={t(SETTINGS_NAV.lbl)}
          className={`grid h-8 w-8 place-items-center rounded-lg transition-colors ${
            isActive(pathname, SETTINGS_NAV)
              ? 'bg-[var(--nav-active-bg)] text-[color:var(--nav-active-text)]'
              : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
          }`}
        >
          <SETTINGS_NAV.Icon size={16} strokeWidth={1.75} aria-hidden />
        </Link>
        <ThemeToggle />
        <button
          type="button"
          onClick={onSignOut}
          title={t('common.actions.signOut')}
          aria-label={t('common.actions.signOut')}
          className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-[color:var(--red-ink)]"
        >
          <LogOut size={15} strokeWidth={1.75} />
        </button>
      </div>
    </nav>
  )
}
