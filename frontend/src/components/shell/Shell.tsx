'use client'

import { usePathname, useRouter } from 'next/navigation'
import { useEffect, useState, type ReactNode } from 'react'
import { getAuth, type AuthUser } from '@/lib/auth'
import { SideNav } from './SideNav'
import { TopBar } from './TopBar'
import { CampaignModal } from '@/components/CampaignModal'

/**
 * App chrome (design.md "Navy + Emerald"): a full-width topbar over a row of
 * [collapsible sidebar | main panel]. EVERY signed-in route keeps the sidebar +
 * topbar. A few routes render full-bleed inside the main panel (they own the
 * whole panel height rather than the padded content well): Chats, Flow Builder
 * and Settings. Login is the only shell-less screen.
 *
 * Auth guard: every route except /login requires a signed-in user; otherwise
 * redirect to /login.
 */

// Routes that fill the whole main panel (no content padding) rather than the
// standard padded content well.
const FULL_BLEED: string[] = ['/chats', '/flow-builder', '/settings']

export function Shell({ children }: { children: ReactNode }) {
  const pathname = usePathname() || '/dashboard'
  const router = useRouter()
  const isLogin = pathname === '/login'
  const isFullBleed = FULL_BLEED.some((p) => pathname === p || pathname.startsWith(p + '/'))

  const [user, setUser] = useState<AuthUser | null>(null)
  const [collapsed, setCollapsed] = useState(false)
  // Mobile-only off-canvas drawer (separate from the desktop icon-rail collapse).
  const [mobileNavOpen, setMobileNavOpen] = useState(false)

  useEffect(() => {
    setUser(getAuth())
    try {
      setCollapsed(localStorage.getItem('nav-collapsed') === '1')
    } catch {
      /* ignore */
    }
  }, [])

  // Close the mobile drawer whenever the route changes.
  useEffect(() => {
    setMobileNavOpen(false)
  }, [pathname])

  // Auth guard — client-side redirect (POC; server APIs enforce roles too).
  useEffect(() => {
    if (!isLogin && !getAuth()) router.replace('/login')
  }, [isLogin, pathname, router])

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

  if (isLogin) return <>{children}</>

  return (
    <div className="flex h-[100dvh] flex-col gap-2.5 overflow-hidden bg-app p-2.5">
      <TopBar
        user={user}
        onToggleNav={toggleNav}
        onOpenMobileNav={() => setMobileNavOpen(true)}
      />

      <div className="flex min-h-0 flex-1 gap-2.5">
        {/* Backdrop for the mobile drawer (below lg only). */}
        {mobileNavOpen && (
          <div
            className="fixed inset-0 z-40 bg-black/50 backdrop-blur-sm lg:hidden"
            onClick={() => setMobileNavOpen(false)}
            aria-hidden="true"
          />
        )}
        <SideNav
          collapsed={collapsed}
          onToggle={toggleNav}
          mobileOpen={mobileNavOpen}
          onCloseMobile={() => setMobileNavOpen(false)}
        />

        <main className="flex min-w-0 flex-1 flex-col overflow-hidden rounded-[20px] border border-line bg-panel">
          {isFullBleed ? (
            // Chats / Flow Builder / Settings fill the whole panel height.
            <div className="min-h-0 flex-1">{children}</div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col gap-3.5 overflow-y-auto px-4 py-4 sm:px-6 sm:py-[22px]">
              {children}
            </div>
          )}
        </main>
      </div>

      <CampaignModal />
    </div>
  )
}
