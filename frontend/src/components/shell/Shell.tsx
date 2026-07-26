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

  useEffect(() => {
    setUser(getAuth())
    try {
      setCollapsed(localStorage.getItem('nav-collapsed') === '1')
    } catch {
      /* ignore */
    }
  }, [])

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
    <div className="flex h-screen flex-col gap-2.5 overflow-hidden bg-app p-2.5">
      <TopBar user={user} onToggleNav={toggleNav} />

      <div className="flex min-h-0 flex-1 gap-2.5">
        <SideNav collapsed={collapsed} onToggle={toggleNav} />

        <main className="flex min-w-0 flex-1 flex-col overflow-hidden rounded-[20px] border border-line bg-panel">
          {isFullBleed ? (
            // Chats / Flow Builder / Settings fill the whole panel height.
            <div className="min-h-0 flex-1">{children}</div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col gap-3.5 overflow-y-auto px-6 py-[22px]">
              {children}
            </div>
          )}
        </main>
      </div>

      <CampaignModal />
    </div>
  )
}
