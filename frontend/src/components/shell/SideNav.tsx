'use client'

import Link from 'next/link'
import { usePathname, useRouter } from 'next/navigation'
import { useEffect, useState } from 'react'
import { getAuth, logout, type AuthUser } from '@/lib/auth'
import {
  Home,
  Workflow,
  Mic,
  Users,
  MessageSquare,
  Activity,
  PhoneCall,
  Settings,
  TrendingUp,
  LogOut,
  type LucideIcon,
} from 'lucide-react'
import { ThemeToggle } from './ThemeToggle'

export interface NavDef {
  href: string
  lbl: string
  Icon: LucideIcon
  badge?: string
  alsoActive?: string[]
  roles?: string[]
}

/**
 * Collapsible left sidebar (design.md): grouped nav, active = emerald tint +
 * emerald text. Width animates 208px (expanded) / 60px (collapsed, icons
 * only); labels fade out via the `.nav-collapsed` class. Groups follow the
 * operator workflow: overview -> work -> configure -> review -> settings.
 */
export const NAV_GROUPS: { label?: string; items: NavDef[] }[] = [
  {
    items: [{ href: '/dashboard', lbl: 'Dashboard', Icon: Home }],
  },
  {
    label: 'Work',
    items: [
      { href: '/contacts', lbl: 'Contacts', Icon: Users, alsoActive: ['/contacts/'] },
      { href: '/chats', lbl: 'Chats', Icon: MessageSquare },
    ],
  },
  {
    label: 'Configure',
    items: [
      { href: '/flow-builder', lbl: 'Flow Builder', Icon: Workflow, roles: ['superadmin', 'admin'] },
      { href: '/persona', lbl: 'Persona', Icon: Mic, roles: ['superadmin', 'admin'] },
    ],
  },
  {
    label: 'Review',
    items: [
      { href: '/analytics', lbl: 'Analytics', Icon: TrendingUp },
      { href: '/call-logs', lbl: 'Call Logs', Icon: PhoneCall, alsoActive: ['/call-logs/'] },
      { href: '/activity', lbl: 'Activity', Icon: Activity },
    ],
  },
  {
    items: [{ href: '/settings', lbl: 'Settings', Icon: Settings, alsoActive: ['/settings/'], roles: ['superadmin', 'admin'] }],
  },
]

const ROLE_LABEL: Record<string, string> = {
  superadmin: 'Super admin',
  admin: 'Admin',
  agent: 'Agent',
}

export function SideNav({ collapsed = false }: { collapsed?: boolean }) {
  const pathname = usePathname() || '/dashboard'
  const router = useRouter()
  const [user, setUser] = useState<AuthUser | null>(null)

  useEffect(() => setUser(getAuth()), [])

  const role = user?.role ?? 'agent'

  const isActive = (n: NavDef) =>
    pathname === n.href || (n.alsoActive?.some((p) => pathname.startsWith(p)) ?? false)

  const visibleGroups = NAV_GROUPS.map((g) => ({
    ...g,
    items: g.items.filter((n) => !n.roles || n.roles.includes(role)),
  })).filter((g) => g.items.length > 0)

  const signOut = async () => {
    await logout()
    router.replace('/login')
  }

  return (
    <aside
      className={`flex h-full shrink-0 flex-col rounded-[20px] border border-line bg-surface py-2 transition-[width] duration-200 ${
        collapsed ? 'nav-collapsed w-[60px] px-1.5' : 'w-[208px] px-2'
      }`}
    >
      <nav className="min-h-0 flex-1 overflow-y-auto no-scrollbar">
        {visibleGroups.map((group, gi) => (
          <div key={gi} className={gi > 0 ? 'mt-3' : ''}>
            {group.label && !collapsed && (
              <div className="nav-label px-2.5 pb-1 text-[10px] font-bold uppercase tracking-[0.1em] text-text-faint">
                {group.label}
              </div>
            )}
            {group.label && collapsed && gi > 0 && <div className="mx-2 mb-1.5 border-t border-line" />}
            {group.items.map((n) => {
              const active = isActive(n)
              return (
                <Link
                  key={n.href}
                  href={n.href}
                  aria-current={active ? 'page' : undefined}
                  title={collapsed ? n.lbl : undefined}
                  className={`mb-0.5 flex items-center gap-2.5 rounded-xl py-[8px] transition-colors ${
                    collapsed ? 'justify-center px-0' : 'px-2.5'
                  } ${active ? '' : 'nav-row'}`}
                  style={{
                    color: active ? 'var(--nav-active-text)' : 'var(--text-muted)',
                    background: active ? 'var(--nav-active-bg)' : 'transparent',
                  }}
                >
                  <n.Icon size={17} className="shrink-0" strokeWidth={1.85} />
                  {!collapsed && (
                    <span
                      className="nav-label min-w-0 flex-1 truncate text-[13px] tracking-[-0.1px]"
                      style={{ fontWeight: active ? 600 : 500 }}
                    >
                      {n.lbl}
                    </span>
                  )}
                  {!collapsed && n.badge && (
                    <span
                      className="nav-label flex h-[17px] min-w-[17px] shrink-0 items-center justify-center rounded-[9px] px-1 text-[10px] font-bold text-white"
                      style={{ background: 'rgba(239,68,68,0.85)' }}
                    >
                      {n.badge}
                    </span>
                  )}
                </Link>
              )
            })}
          </div>
        ))}
      </nav>

      {/* User footer */}
      <div
        className={`mt-2 flex shrink-0 items-center gap-2.5 border-t border-line pt-2.5 ${
          collapsed ? 'flex-col px-0' : 'px-1'
        }`}
      >
        <div
          className="grid h-[32px] w-[32px] shrink-0 place-items-center rounded-full text-[11px] font-extrabold tracking-[-0.5px] text-white"
          style={{ background: 'linear-gradient(135deg,#4F46E5,#7C3AED)' }}
          title={user?.name || 'Not signed in'}
        >
          {(user?.name || 'AI').slice(0, 2).toUpperCase()}
        </div>
        {!collapsed && (
          <div className="nav-label min-w-0 flex-1">
            <div className="truncate text-[12px] font-semibold text-text-high">
              {user?.name || 'Not signed in'}
            </div>
            <div className="truncate text-[10.5px] text-text-muted">{ROLE_LABEL[role] ?? role}</div>
          </div>
        )}
        <button
          onClick={signOut}
          title="Sign out"
          aria-label="Sign out"
          className="grid h-[30px] w-[30px] shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-red-300"
        >
          <LogOut size={14} strokeWidth={1.75} />
        </button>
        <ThemeToggle />
      </div>
    </aside>
  )
}
