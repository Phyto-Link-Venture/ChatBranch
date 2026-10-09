'use client'

import Link from 'next/link'
import { usePathname, useRouter } from 'next/navigation'
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Menu, MessageSquare, TrendingUp, Users } from 'lucide-react'
import { getAuth, logout, refreshIdentity, type AuthUser } from '@/lib/auth'
import { syncAppearanceFromServer } from '@/lib/theme'
import { useEntityQuery } from '@/lib/EntityContext'
import { SideNav } from './SideNav'
import { TopBar } from './TopBar'
import { CommandPalette } from './CommandPalette'
import { HOME, canSee, firstVisibleHref, isActive, NAV_GROUPS, routeDef } from './nav'
import { RouteGuard } from './RouteGuard'
import { syncLocaleFromServer, useT, type MessageKey } from '@/i18n'
import { resetNotifications } from './notificationsStore'
import { useLive } from './liveStore'
import { CampaignModal } from '@/components/CampaignModal'
import { AssistantDock } from '@/components/assistant/AssistantDock'

/**
 * App chrome, Paper & Ink: a flush layout — the sidebar rail (surface, hairline
 * on its right), the top bar (hairline under it) and the content straight on
 * the paper. No floating panels, no gaps.
 *
 * The root applies the user's text-size setting: `zoom: var(--ui-zoom)` (set
 * by lib/theme.ts) with the height divided by the same factor, so the shell
 * still fills exactly one viewport at any size.
 *
 * Below lg the rail becomes an off-canvas drawer and a bottom tab bar (Home ·
 * Chats · Contacts · More) takes over primary navigation, as on the phone
 * board. A few routes render full-bleed (they own the whole content height):
 * Chats, Flow Builder and Settings. Login, the Help Center and the offline
 * page render bare.
 *
 * Auth guard: every non-public route requires a signed-in user.
 */

const FULL_BLEED: string[] = ['/chats', '/flow-builder', '/settings']

/** Routes anyone may read, signed in or not, outside the operator shell. */
const PUBLIC_ROUTES = ['/help', '/offline']

/** Token the appearance/identity sync last ran for — once per sign-in. */
let syncedToken: string | null = null

export function Shell({ children }: { children: ReactNode }) {
  const pathname = usePathname() || HOME
  const router = useRouter()
  const isLogin = pathname === '/login'
  const isPublic = PUBLIC_ROUTES.some((p) => pathname === p || pathname.startsWith(p + '/'))
  const isFullBleed = FULL_BLEED.some((p) => pathname === p || pathname.startsWith(p + '/'))

  const [user, setUser] = useState<AuthUser | null>(null)
  // false until the session has been read on the client — pages wait for it
  const [authReady, setAuthReady] = useState(false)
  const [collapsed, setCollapsed] = useState(false)
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)

  useEffect(() => {
    try {
      setCollapsed(localStorage.getItem('nav-collapsed') === '1')
    } catch {
      /* ignore */
    }
    const onAuth = () => setUser(getAuth())
    // The browser chrome (installed app title bar, mobile address bar) takes
    // the paper colour of the theme the user actually chose, not the OS mode.
    const tintChrome = () => {
      const paper = getComputedStyle(document.documentElement).getPropertyValue('--app').trim()
      if (paper) document.querySelectorAll('meta[name="theme-color"]').forEach((m) => m.setAttribute('content', paper))
    }
    tintChrome()
    window.addEventListener('auth-change', onAuth)
    window.addEventListener('appearance-change', tintChrome)
    return () => {
      window.removeEventListener('auth-change', onAuth)
      window.removeEventListener('appearance-change', tintChrome)
    }
  }, [])

  // Re-read the session on every navigation (login → home lands here with a
  // fresh token) and, once per sign-in, pull the user's saved look and their
  // current capabilities from the server.
  useEffect(() => {
    const u = getAuth()
    setUser(u)
    setAuthReady(true)
    if (u && u.token !== syncedToken) {
      syncedToken = u.token
      void syncAppearanceFromServer() // a 404 on an older API is harmless
      void syncLocaleFromServer() // the user's own language, else the organization default
      void refreshIdentity()
    }
  }, [pathname])

  useEffect(() => {
    setMobileNavOpen(false)
    setSearchOpen(false)
  }, [pathname])

  useEffect(() => {
    if (!isLogin && !isPublic && !getAuth()) router.replace('/login')
  }, [isLogin, isPublic, pathname, router])

  // Home (where sign-in lands) is Analytics; someone without it starts on
  // the first page they can open instead of a "no access" panel.
  useEffect(() => {
    if (!authReady || !user || pathname !== HOME) return
    const def = routeDef(HOME)
    if (def && !canSee(user, def)) {
      const next = firstVisibleHref(user)
      if (next !== HOME) router.replace(next)
    }
  }, [authReady, user, pathname, router])

  // Ctrl/⌘+K anywhere opens the command palette.
  useEffect(() => {
    if (isLogin || isPublic) return
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setSearchOpen((v) => !v)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [isLogin, isPublic])

  const toggleNav = () => {
    setCollapsed((c) => {
      const next = !c
      try {
        localStorage.setItem('nav-collapsed', next ? '1' : '0')
      } catch {
        /* ignore */
      }
      return next
    })
  }

  const signOut = useCallback(async () => {
    await logout()
    syncedToken = null
    resetNotifications()
    setUser(null)
    router.replace('/login')
  }, [router])

  if (isLogin || isPublic) return <>{children}</>

  return (
    <div
      className="shell-root flex overflow-hidden bg-app text-text-primary"
      style={{ zoom: 'var(--ui-zoom)', height: 'calc(100dvh / var(--ui-zoom))' } as React.CSSProperties}
    >
      {mobileNavOpen && (
        <div
          className="fixed inset-0 z-40 animate-fadeIn lg:hidden"
          style={{ background: 'var(--scrim)' }}
          onClick={() => setMobileNavOpen(false)}
          aria-hidden="true"
        />
      )}
      <SideNav
        user={user}
        collapsed={collapsed}
        onToggle={toggleNav}
        mobileOpen={mobileNavOpen}
        onCloseMobile={() => setMobileNavOpen(false)}
        onSignOut={() => void signOut()}
      />

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar user={user} onOpenMobileNav={() => setMobileNavOpen(true)} onOpenSearch={() => setSearchOpen(true)} />

        <main className="flex min-h-0 flex-1 flex-col overflow-hidden">
          {isFullBleed ? (
            <div className="min-h-0 flex-1">
              <RouteGuard user={user} ready={authReady} pathname={pathname}>
                {children}
              </RouteGuard>
            </div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-5 sm:px-7 sm:py-7">
              <RouteGuard user={user} ready={authReady} pathname={pathname}>
                {children}
              </RouteGuard>
            </div>
          )}
        </main>

        <BottomTabs user={user} pathname={pathname} onMore={() => setMobileNavOpen(true)} />
      </div>

      <CommandPalette open={searchOpen} onClose={() => setSearchOpen(false)} user={user} />
      <CampaignModal />
      <AssistantDock />
    </div>
  )
}

/** Phone tab bar (below lg): Home · Chats · Contacts · More (the drawer). */
function BottomTabs({ user, pathname, onMore }: { user: AuthUser | null; pathname: string; onMore: () => void }) {
  const entityQ = useEntityQuery()
  const { data: live } = useLive(entityQ)
  const tr = useT()
  const byHref = (href: string) => NAV_GROUPS.flatMap((g) => g.items).find((n) => n.href === href)
  const allTabs: { href: string; lbl: MessageKey; Icon: typeof Users; badge?: number }[] = [
    { href: HOME, lbl: 'shell.nav.home', Icon: TrendingUp },
    { href: '/chats', lbl: 'shell.nav.chats', Icon: MessageSquare, badge: live?.waiting_count ?? 0 },
    { href: '/contacts', lbl: 'shell.nav.contacts', Icon: Users },
  ]
  const tabs = allTabs.filter((t) => {
    const def = byHref(t.href)
    return !def || canSee(user, def)
  })

  return (
    <nav
      aria-label={tr('shell.sideNav.primaryTabs')}
      className="grid shrink-0 border-t border-line bg-surface pb-[env(safe-area-inset-bottom)] lg:hidden"
      style={{ gridTemplateColumns: `repeat(${tabs.length + 1}, minmax(0, 1fr))` }}
    >
      {tabs.map((t) => {
        const def = byHref(t.href)
        const active = def ? isActive(pathname, def) : pathname === t.href
        return (
          <Link
            key={t.href}
            href={t.href}
            aria-current={active ? 'page' : undefined}
            className={`relative flex flex-col items-center gap-1 border-t-2 pb-2.5 pt-2 text-[11px] ${
              active ? 'border-accent font-bold text-text-heading' : 'border-transparent text-text-mid'
            }`}
          >
            <span className="relative">
              <t.Icon size={19} strokeWidth={active ? 1.9 : 1.6} aria-hidden />
              {!!t.badge && (
                <span className="absolute -right-2.5 -top-1.5 min-w-[16px] rounded-full bg-primary px-1 text-center text-[9.5px] font-bold leading-[15px] text-on-primary">
                  {t.badge > 99 ? '99+' : t.badge}
                </span>
              )}
            </span>
            <span className="max-w-full truncate px-0.5">{tr(t.lbl)}</span>
          </Link>
        )
      })}
      <button
        type="button"
        onClick={onMore}
        className="flex flex-col items-center gap-1 border-t-2 border-transparent pb-2.5 pt-2 text-[11px] text-text-mid"
      >
        <Menu size={19} strokeWidth={1.6} aria-hidden />
        <span className="max-w-full truncate px-0.5">{tr('shell.nav.more')}</span>
      </button>
    </nav>
  )
}
