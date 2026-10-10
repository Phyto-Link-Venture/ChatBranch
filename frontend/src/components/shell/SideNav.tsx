'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronDown, Download, LogOut, PanelLeftClose, PanelLeftOpen, X } from 'lucide-react'
import type { AuthUser } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import { BranchMark, Logo } from './Logo'
import { ThemeToggle } from './ThemeToggle'
import { EntitySwitcher } from './EntitySwitcher'
import { SETTINGS_NAV, isActive, visibleGroups, type NavDef } from './nav'
import { useT, type MessageKey } from '@/i18n'
import { useNotifications } from './notificationsStore'
import { useLive } from './liveStore'
import { useInstallPrompt } from './pwa'
import { SHOW_INSTALL_EVENT } from './InstallHint'

export { NAV_GROUPS, type NavDef } from './nav'

const ROLE_LABEL: Record<string, MessageKey> = {
  superadmin: 'shell.roles.superadmin',
  admin: 'shell.roles.admin',
  agent: 'shell.roles.agent',
}

/** Groups folded the first time a user opens the app (keyed by the group's
 *  label key); the group holding the current page always opens itself. */
const DEFAULT_FOLDED: MessageKey[] = ['shell.groups.library', 'shell.groups.channels', 'shell.groups.admin', 'shell.groups.review']

const foldKey = (user: AuthUser | null) => `nav-groups:${user?.username ?? 'anon'}`

/**
 * The sidebar rail (Paper & Ink): logo, the ungrouped head (Dashboard,
 * Notifications), then collapsible groups — each group title is a button that
 * folds its modules, remembered per user in localStorage — and a footer with
 * Settings, Install app, the signed-in user, the theme switch and sign out.
 *
 * Desktop (lg+): a flush rail that collapses to icons only (64px) — group
 * titles become hairline dividers and every icon gets a tooltip. Below lg it
 * is an off-canvas drawer (always expanded) opened from the top bar or the
 * phone tab bar's "More".
 */
export function SideNav({
  user,
  collapsed: collapsedProp = false,
  onToggle,
  mobileOpen = false,
  onCloseMobile,
  onSignOut,
}: {
  user: AuthUser | null
  collapsed?: boolean
  onToggle?: () => void
  mobileOpen?: boolean
  onCloseMobile?: () => void
  onSignOut: () => void
}) {
  const t = useT()
  const pathname = usePathname() || '/dashboard'
  const entityQ = useEntityQuery()
  const { unread } = useNotifications()
  const { data: live } = useLive(entityQ)
  const { canInstall, iosHint, install } = useInstallPrompt()

  // The icon rail is a DESKTOP affordance. Resolved after mount so the server
  // and first client render agree (expanded).
  const [isDesktop, setIsDesktop] = useState(false)
  useEffect(() => {
    const mq = window.matchMedia('(min-width: 1024px)')
    const sync = () => setIsDesktop(mq.matches)
    sync()
    mq.addEventListener('change', sync)
    return () => mq.removeEventListener('change', sync)
  }, [])
  const collapsed = isDesktop && collapsedProp

  const groups = useMemo(() => visibleGroups(user), [user])

  // ---- fold state, per user ------------------------------------------------
  const [folded, setFolded] = useState<Record<string, boolean>>({})
  const [foldReady, setFoldReady] = useState(false)
  useEffect(() => {
    let saved: Record<string, boolean> | null = null
    try {
      const raw = localStorage.getItem(foldKey(user))
      if (raw) saved = JSON.parse(raw)
    } catch {
      /* ignore */
    }
    setFolded(saved ?? Object.fromEntries(DEFAULT_FOLDED.map((g) => [g, true])))
    setFoldReady(true)
  }, [user])

  const persist = useCallback(
    (next: Record<string, boolean>) => {
      try {
        localStorage.setItem(foldKey(user), JSON.stringify(next))
      } catch {
        /* ignore */
      }
    },
    [user],
  )

  // The group containing the page you're on opens itself (and stays open).
  useEffect(() => {
    if (!foldReady) return
    const owner = groups.find((g) => g.label && g.items.some((n) => isActive(pathname, n)))
    if (owner?.label && folded[owner.label]) {
      const next = { ...folded, [owner.label]: false }
      setFolded(next)
      persist(next)
    }
  }, [pathname, groups, foldReady, folded, persist])

  const toggleGroup = (label: string) => {
    const next = { ...folded, [label]: !folded[label] }
    setFolded(next)
    persist(next)
  }

  // ---- rail tooltips (collapsed mode) ---------------------------------------
  // Positioned from offsetTop (layout px) rather than client rects, so the
  // shell's text-size zoom can't push them off their icon.
  const asideRef = useRef<HTMLElement>(null)
  const navRef = useRef<HTMLElement>(null)
  const [tip, setTip] = useState<{ label: string; top: number } | null>(null)
  const showTip = (el: HTMLElement, label: string) => {
    if (!collapsed) return
    let top = el.offsetHeight / 2
    let node: HTMLElement | null = el
    while (node && node !== asideRef.current) {
      top += node.offsetTop
      const parent = node.offsetParent as HTMLElement | null
      if (parent && parent === navRef.current) top -= parent.scrollTop
      node = parent
    }
    setTip({ label, top })
  }
  const hideTip = () => setTip(null)
  useEffect(() => setTip(null), [collapsed, pathname])

  const counter = (n: NavDef): number => {
    if (n.counter === 'notifications') return unread
    if (n.counter === 'waiting') return live?.waiting_count ?? 0
    return 0
  }

  const row = (n: NavDef) => {
    const active = isActive(pathname, n)
    const count = counter(n)
    const needsHuman = n.counter === 'waiting' ? live?.needs_human_count ?? 0 : 0
    const name = t(n.lbl)
    const label = count > 0 ? t('shell.sideNav.withCount', { name, count }) : name
    const humanTip = needsHuman > 0 ? t('shell.sideNav.needsHuman', { count: needsHuman }) : ''
    return (
      <Link
        key={n.href}
        href={n.href}
        onClick={onCloseMobile}
        aria-current={active ? 'page' : undefined}
        aria-label={collapsed ? label : undefined}
        title={!collapsed && humanTip ? humanTip : undefined}
        onMouseEnter={(e) => showTip(e.currentTarget, humanTip ? `${label} · ${humanTip}` : label)}
        onMouseLeave={hideTip}
        onFocus={(e) => showTip(e.currentTarget, label)}
        onBlur={hideTip}
        className={`group relative flex items-center gap-2.5 rounded-lg py-[7px] text-[13.5px] transition-colors ${
          collapsed ? 'justify-center px-0' : 'px-2.5'
        } ${
          active
            ? 'bg-[var(--nav-active-bg)] font-semibold text-[color:var(--nav-active-text)]'
            : 'text-text-mid hover:bg-fill-hover hover:text-text-heading'
        }`}
      >
        <n.Icon size={17} strokeWidth={1.7} className="shrink-0" aria-hidden />
        {!collapsed && <span className="min-w-0 flex-1 truncate">{name}</span>}
        {!collapsed && count > 0 && n.counter === 'waiting' && (
          <span className="shrink-0 rounded-full bg-primary px-[7px] py-px text-[11.5px] font-semibold tabular-nums text-on-primary">
            {count > 99 ? '99+' : count}
          </span>
        )}
        {!collapsed && count > 0 && n.counter === 'notifications' && (
          <span className="shrink-0 text-[12px] tabular-nums text-text-muted">{count > 99 ? '99+' : count}</span>
        )}
        {!collapsed && active && count === 0 && (
          <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-hidden />
        )}
        {collapsed && count > 0 && (
          <span className="absolute right-2.5 top-1.5 h-[7px] w-[7px] rounded-full bg-accent ring-2 ring-[var(--surface)]" aria-hidden />
        )}
      </Link>
    )
  }

  const initials = (user?.name || user?.username || 'CB').slice(0, 2).toUpperCase()
  const role = user?.role ?? 'agent'

  return (
    <aside
      ref={asideRef}
      className={`flex shrink-0 flex-col border-r border-line bg-surface
        fixed inset-y-0 left-0 z-50 w-[272px] shadow-pop transition-transform duration-200
        ${mobileOpen ? 'translate-x-0' : '-translate-x-full'}
        lg:relative lg:z-auto lg:h-full lg:translate-x-0 lg:shadow-none lg:transition-[width]
        ${collapsed ? 'lg:w-[64px]' : 'lg:w-[248px]'}`}
      aria-label={t('shell.sideNav.mainNavigation')}
      aria-hidden={!mobileOpen && !isDesktop ? true : undefined}
    >
      {/* Logo row */}
      <div
        className={`flex shrink-0 items-center border-b border-line ${
          collapsed ? 'flex-col gap-2 px-2 py-3' : 'gap-2 px-4 py-[13px]'
        }`}
      >
        <Link
          href="/dashboard"
          onClick={onCloseMobile}
          className="flex min-w-0 items-center rounded-md"
          aria-label={t('shell.sideNav.homeLink')}
        >
          {collapsed ? <BranchMark size={26} className="text-text-heading" /> : <Logo />}
        </Link>
        {onToggle && (
          <button
            onClick={onToggle}
            title={collapsed ? t('shell.sideNav.expand') : t('shell.sideNav.collapse')}
            aria-label={collapsed ? t('shell.sideNav.expand') : t('shell.sideNav.collapse')}
            className={`hidden h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high lg:grid ${
              collapsed ? '' : 'ml-auto'
            }`}
          >
            {collapsed ? <PanelLeftOpen size={16} strokeWidth={1.75} /> : <PanelLeftClose size={16} strokeWidth={1.75} />}
          </button>
        )}
        <button
          onClick={onCloseMobile}
          aria-label={t('shell.sideNav.closeMenu')}
          className="ml-auto grid h-9 w-9 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover lg:hidden"
        >
          <X size={18} strokeWidth={1.75} />
        </button>
      </div>

      {/* Phones: the top bar has no room for the entity switcher — it lives here. */}
      <div className="shrink-0 border-b border-line px-3 py-2.5 sm:hidden">
        <EntitySwitcher block />
      </div>

      <nav data-tour="nav"
        ref={navRef}
        onScroll={hideTip}
        className={`relative min-h-0 flex-1 overflow-y-auto overflow-x-hidden no-scrollbar py-3 ${collapsed ? 'px-2' : 'px-3'}`}
      >
        {groups.map((g, gi) => {
          const id = `nav-group-${(g.label ?? 'top').split('.').pop()}`
          const isFolded = !collapsed && !!g.label && !!folded[g.label]
          return (
            <div key={g.label ?? 'top'} className={gi > 0 ? (collapsed ? 'mt-2' : 'mt-3') : ''}>
              {g.label && collapsed && <div className="mx-2 mb-2 border-t border-line" aria-hidden />}
              {g.label && !collapsed && (
                <button
                  type="button"
                  onClick={() => toggleGroup(g.label!)}
                  aria-expanded={!isFolded}
                  aria-controls={id}
                  className="eyebrow flex w-full cursor-pointer items-center justify-between rounded-md px-2.5 py-1.5 transition-colors hover:text-text-high"
                >
                  {t(g.label!)}
                  <ChevronDown
                    size={13}
                    strokeWidth={2}
                    className={`transition-transform ${isFolded ? '-rotate-90' : ''}`}
                    aria-hidden
                  />
                </button>
              )}
              <div id={id} hidden={isFolded} className={`${isFolded ? 'hidden' : 'flex'} flex-col gap-0.5`}>
                {g.items.map(row)}
              </div>
            </div>
          )
        })}
      </nav>

      {/* Rail tooltip — outside the scrolling nav so it isn't clipped. */}
      {collapsed && tip && (
        <div
          role="tooltip"
          className="pointer-events-none absolute left-full z-50 ml-2 -translate-y-1/2 whitespace-nowrap rounded-md bg-primary px-2 py-1 text-[12px] font-medium text-on-primary shadow-pop"
          style={{ top: tip.top }}
        >
          {tip.label}
        </div>
      )}

      {/* Footer */}
      <div className={`shrink-0 border-t border-line ${collapsed ? 'px-2 py-2' : 'px-3 py-2.5'}`}>
        <div className="flex flex-col gap-0.5">
          {row(SETTINGS_NAV)}
          {(canInstall || iosHint) && (
            <button
              type="button"
              // iPhone/iPad can't be prompted — open the Share-sheet steps instead
              onClick={() => (canInstall ? void install() : window.dispatchEvent(new Event(SHOW_INSTALL_EVENT)))}
              aria-label={collapsed ? t('shell.sideNav.installApp') : undefined}
              onMouseEnter={(e) => showTip(e.currentTarget, t('shell.sideNav.installApp'))}
              onMouseLeave={hideTip}
              className={`flex cursor-pointer items-center gap-2.5 rounded-lg py-[7px] text-[13.5px] text-text-mid transition-colors hover:bg-fill-hover hover:text-text-heading ${
                collapsed ? 'justify-center px-0' : 'px-2.5'
              }`}
            >
              <Download size={17} strokeWidth={1.7} className="shrink-0" aria-hidden />
              {!collapsed && <span className="min-w-0 flex-1 truncate text-left">{t('shell.sideNav.installApp')}</span>}
            </button>
          )}
        </div>

        <div className={`mt-2 flex items-center gap-2 border-t border-line pt-2.5 ${collapsed ? 'flex-col' : ''}`}>
          <span
            className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-primary text-[11.5px] font-semibold text-on-primary"
            title={user?.name || user?.username || t('shell.sideNav.notSignedIn')}
            aria-hidden
          >
            {initials}
          </span>
          {!collapsed && (
            <span className="flex min-w-0 flex-1 flex-col leading-tight">
              <span className="truncate text-[13px] font-semibold text-text-high">
                {user?.name || user?.username || t('shell.sideNav.notSignedIn')}
              </span>
              <span className="truncate text-[11.5px] text-text-muted">{ROLE_LABEL[role] ? t(ROLE_LABEL[role]) : role}</span>
            </span>
          )}
          <ThemeToggle />
          <button
            onClick={onSignOut}
            title={t('common.actions.signOut')}
            aria-label={t('common.actions.signOut')}
            className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-[color:var(--red-ink)]"
          >
            <LogOut size={15} strokeWidth={1.75} />
          </button>
        </div>
      </div>
    </aside>
  )
}
