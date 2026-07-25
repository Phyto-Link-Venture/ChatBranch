'use client'

import { PanelLeft, Search } from 'lucide-react'
import type { AuthUser } from '@/lib/auth'
import { QuickActions } from './QuickActions'
import { TenantSwitcher } from './TenantSwitcher'
import { NotificationsBell } from './NotificationsBell'

/**
 * Full-width topbar (design.md): collapse toggle · brand mark + wordmark ·
 * search · quick-action icon buttons · notifications + Live · brand/tenant
 * switcher · tenant avatar. Rounded 18px surface panel.
 */
export function TopBar({
  user,
  onToggleNav,
}: {
  user: AuthUser | null
  onToggleNav: () => void
}) {
  const role = user?.role ?? 'agent'
  return (
    <header className="flex h-[56px] shrink-0 items-center gap-3 rounded-[18px] border border-line bg-surface px-3">
      <button
        onClick={onToggleNav}
        aria-label="Toggle sidebar"
        title="Toggle sidebar"
        className="grid h-[34px] w-[34px] shrink-0 place-items-center rounded-xl text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
      >
        <PanelLeft size={17} strokeWidth={1.9} />
      </button>

      <div className="flex shrink-0 items-center gap-2">
        <div
          className="grid h-[30px] w-[30px] place-items-center rounded-[9px]"
          style={{
            background: 'linear-gradient(135deg,#3B82F6,#2563EB)',
            boxShadow: '0 4px 14px rgba(59,130,246,0.35)',
          }}
        >
          <span className="text-[11px] font-extrabold tracking-[-0.5px] text-white">CB</span>
        </div>
        <span className="hidden text-[15px] font-extrabold tracking-[-0.5px] text-text-heading sm:block">
          ChatBranch
        </span>
      </div>

      {/* Search */}
      <div className="relative mx-1 hidden min-w-0 flex-1 md:block">
        <Search
          size={15}
          strokeWidth={1.9}
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-faint"
        />
        <input
          placeholder="Search contacts, calls, chats…"
          className="h-[34px] w-full rounded-xl border border-line bg-fill pl-9 pr-3 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400"
        />
      </div>

      <div className="ml-auto flex shrink-0 items-center gap-2.5">
        <QuickActions role={role} />
        <div className="hidden items-center gap-1.5 rounded-xl border border-brand-400/20 bg-brand/10 px-2 py-1.5 lg:flex">
          <span className="h-[7px] w-[7px] shrink-0 animate-pulseRing rounded-full bg-brand-400" />
          <span className="text-[9px] font-bold uppercase tracking-[0.06em] text-brand-300">Live</span>
        </div>
        <NotificationsBell />
        <TenantSwitcher />
        <div
          className="grid h-[32px] w-[32px] shrink-0 place-items-center rounded-xl text-[11px] font-extrabold tracking-[-0.5px] text-white"
          style={{ background: 'linear-gradient(135deg,#4F46E5,#7C3AED)' }}
          title={user?.name || 'Not signed in'}
        >
          {(user?.name || 'CB').slice(0, 2).toUpperCase()}
        </div>
      </div>
    </header>
  )
}
