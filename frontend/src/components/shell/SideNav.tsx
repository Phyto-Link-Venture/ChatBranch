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
  UserCog,
  MessageSquare,
  Activity,
  PhoneCall,
  Settings,
  TrendingUp,
  Radio,
  Plug,
  Building2,
  ShieldCheck,
  FileText,
  ScrollText,
  BookOpen,
  Sparkles,
  Bell,
  Briefcase,
  LogOut,
  ChevronsLeft,
  ChevronsRight,
  type LucideIcon,
} from 'lucide-react'
import { ThemeToggle } from './ThemeToggle'

export interface NavDef {
  href: string
  lbl: string
  Icon: LucideIcon
  badge?: string
  alsoActive?: string[]
  /** Capability that unlocks this item (dynamic RBAC, upgrade §3). Preferred
   *  gate when the session carries a resolved capability list. */
  cap?: string
  /** Role fallback used when the session has no capability list (older cached
   *  session, or a backend that predates the matrix). */
  roles?: string[]
}

/**
 * Collapsible left sidebar (design.md): grouped nav showing icon + text label,
 * active = blue tint + light-blue text (--nav-active-bg / --nav-active-text).
 * Width animates 240px (expanded) / 64px (collapsed, icons only with tooltips);
 * labels + group headers hide via the `.nav-collapsed` class. A chevron toggle
 * in the rail collapses/expands it (the TopBar PanelLeft button does the same);
 * the collapsed/expanded choice is persisted + hydration-safely restored by the
 * Shell (localStorage 'nav-collapsed').
 *
 * Groups follow the operator workflow (upgrade §1): Overview → Work → Automate
 * → Channels → Admin → Library → Review → Settings. Seven areas were promoted
 * out of Settings into their own routes here; each item is gated by a capability
 * (with a role fallback).
 */
export const NAV_GROUPS: { label?: string; items: NavDef[] }[] = [
  {
    items: [{ href: '/dashboard', lbl: 'Dashboard', Icon: Home }],
  },
  {
    label: 'Work',
    items: [
      { href: '/contacts', lbl: 'Contacts', Icon: Users, alsoActive: ['/contacts/'], cap: 'contacts.view' },
      // Service Providers = operational data (the delivery side), managed
      // day-to-day like Contacts — a Work area, not a Settings tab.
      { href: '/providers', lbl: 'Service Providers', Icon: Briefcase, cap: 'contacts.view' },
      { href: '/chats', lbl: 'Chats', Icon: MessageSquare, cap: 'chats.view' },
    ],
  },
  {
    label: 'Automate',
    items: [
      { href: '/flow-builder', lbl: 'Flow Builder', Icon: Workflow, cap: 'flows.manage', roles: ['superadmin', 'admin'] },
      // Voice/call persona (voice picker, previews, opening line) — distinct from
      // Settings → Chat Persona, which is the text-AI system prompt.
      { href: '/persona', lbl: 'Voice & Calls', Icon: Mic, cap: 'settings.ai.manage', roles: ['superadmin', 'admin'] },
    ],
  },
  {
    label: 'Channels',
    items: [
      { href: '/channels', lbl: 'Channels', Icon: Radio, cap: 'channels.view', roles: ['superadmin', 'admin'] },
      { href: '/integrations', lbl: 'Integrations', Icon: Plug, cap: 'integrations.view', roles: ['superadmin', 'admin'] },
    ],
  },
  {
    label: 'Admin',
    items: [
      { href: '/users', lbl: 'Users & Roles', Icon: UserCog, cap: 'users.view', roles: ['superadmin', 'admin'] },
      { href: '/entities', lbl: 'Entities', Icon: Building2, cap: 'entities.view', roles: ['superadmin', 'admin'] },
      { href: '/permissions', lbl: 'Permissions', Icon: ShieldCheck, cap: 'rbac.manage', roles: ['superadmin'] },
    ],
  },
  {
    label: 'Library',
    items: [
      { href: '/knowledge', lbl: 'Knowledge Base', Icon: BookOpen, cap: 'settings.ai.manage', roles: ['superadmin', 'admin'] },
      { href: '/skills', lbl: 'AI Skills', Icon: Sparkles, cap: 'settings.ai.manage', roles: ['superadmin', 'admin'] },
      { href: '/templates', lbl: 'Message Templates', Icon: FileText, cap: 'templates.manage', roles: ['superadmin', 'admin'] },
      { href: '/script-templates', lbl: 'Script Templates', Icon: ScrollText, cap: 'script_templates.manage', roles: ['superadmin', 'admin'] },
    ],
  },
  {
    label: 'Review',
    items: [
      { href: '/analytics', lbl: 'Analytics', Icon: TrendingUp, cap: 'analytics.view' },
      { href: '/call-logs', lbl: 'Call Logs', Icon: PhoneCall, alsoActive: ['/call-logs/'], cap: 'call_logs.view' },
      { href: '/activity', lbl: 'Activity', Icon: Activity, cap: 'activity.view' },
      { href: '/notifications', lbl: 'Notifications', Icon: Bell, cap: 'notifications.manage', roles: ['superadmin', 'admin'] },
    ],
  },
  {
    items: [
      {
        href: '/settings',
        lbl: 'Settings',
        Icon: Settings,
        alsoActive: ['/settings/'],
        cap: 'settings.ai.manage',
        roles: ['superadmin', 'admin'],
      },
    ],
  },
]

const ROLE_LABEL: Record<string, string> = {
  superadmin: 'Super admin',
  admin: 'Admin',
  agent: 'Agent',
}

export function SideNav({
  collapsed: collapsedProp = false,
  onToggle,
  mobileOpen = false,
  onCloseMobile,
}: {
  collapsed?: boolean
  onToggle?: () => void
  /** Off-canvas drawer open state (mobile/tablet, below lg). */
  mobileOpen?: boolean
  onCloseMobile?: () => void
}) {
  const pathname = usePathname() || '/dashboard'
  const router = useRouter()
  const [user, setUser] = useState<AuthUser | null>(null)

  // The icon-rail collapse is a DESKTOP affordance only. On mobile the nav is a
  // full-width drawer, so we ignore `collapsed` there (labels always shown).
  // Resolved after mount → server + first client render both see expanded, so
  // no hydration mismatch.
  const [isDesktop, setIsDesktop] = useState(false)
  useEffect(() => {
    const mq = window.matchMedia('(min-width: 1024px)')
    const sync = () => setIsDesktop(mq.matches)
    sync()
    mq.addEventListener('change', sync)
    return () => mq.removeEventListener('change', sync)
  }, [])
  const collapsed = isDesktop && collapsedProp

  useEffect(() => setUser(getAuth()), [])

  const role = user?.role ?? 'agent'
  // Prefer the server-resolved capability list; fall back to role gates when a
  // (possibly older) session doesn't carry one.
  const hasExplicitCaps = Array.isArray(user?.capabilities) && user!.capabilities!.length > 0

  // Provider login ("their own world"): a user bound to a Service Provider gets
  // a trimmed nav — their customers, their chats, their calendar/integrations.
  // Row-level scoping is enforced server-side (ProviderScope); this just hides
  // the admin surface that would only ever come back empty or 403.
  const isProvider = typeof user?.resource_id === 'number'
  const PROVIDER_NAV = new Set(['/dashboard', '/contacts', '/chats', '/integrations', '/call-logs'])

  const canSee = (n: NavDef) => {
    if (isProvider && !PROVIDER_NAV.has(n.href)) return false
    if (!n.cap && !n.roles) return true
    if (hasExplicitCaps && n.cap) return user!.capabilities!.includes(n.cap)
    return !n.roles || n.roles.includes(role)
  }

  const isActive = (n: NavDef) =>
    pathname === n.href || (n.alsoActive?.some((p) => pathname.startsWith(p)) ?? false)

  const visibleGroups = NAV_GROUPS.map((g) => ({
    ...g,
    items: g.items.filter(canSee),
  })).filter((g) => g.items.length > 0)

  const signOut = async () => {
    await logout()
    router.replace('/login')
  }

  return (
    <aside
      className={`flex shrink-0 flex-col rounded-[20px] border border-line bg-surface py-2 duration-200
        fixed inset-y-2.5 left-2.5 z-50 shadow-2xl transition-transform
        ${mobileOpen ? 'translate-x-0' : '-translate-x-[calc(100%+1rem)]'}
        lg:static lg:inset-auto lg:z-auto lg:h-full lg:translate-x-0 lg:shadow-none lg:transition-[width]
        ${collapsed ? 'nav-collapsed w-[64px] px-1.5' : 'w-[240px] px-2'}`}
      aria-hidden={!mobileOpen && !isDesktop}
    >
      {onToggle && (
        <div className={`mb-1 hidden lg:flex ${collapsed ? 'justify-center' : 'justify-end pr-0.5'}`}>
          <button
            onClick={onToggle}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            {collapsed ? (
              <ChevronsRight size={16} strokeWidth={1.9} />
            ) : (
              <ChevronsLeft size={16} strokeWidth={1.9} />
            )}
          </button>
        </div>
      )}

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
                  onClick={onCloseMobile}
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
          {(user?.name || 'CB').slice(0, 2).toUpperCase()}
        </div>
        {!collapsed && (
          <div className="nav-label min-w-0 flex-1">
            <div className="truncate text-[12px] font-semibold text-text-high">
              {user?.name || 'Not signed in'}
            </div>
            <div className="truncate text-[10.5px] text-text-muted">
              {ROLE_LABEL[role] ?? role}
              {isProvider && <span className="ml-1 rounded bg-brand-400/15 px-1 text-[9px] font-bold uppercase text-brand-300">Provider</span>}
            </div>
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
