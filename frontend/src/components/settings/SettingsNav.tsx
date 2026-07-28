'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useState, type ReactNode } from 'react'
import { Check, Loader2 } from 'lucide-react'
import { getRole, type Role } from '@/lib/role'
import { SettingsDirtyProvider, useSettingsDirty } from './SettingsDirtyContext'

/**
 * Settings section chrome: each tab is its own ROUTE (/settings/<id>) so a
 * refresh or a shared link lands back on the same tab. The old single-page
 * ?tab= form still works — app/settings/page.tsx redirects it here.
 * Add a tab = add the entry below + an app/settings/<id>/page.tsx.
 *
 * Settings is now AI/config only (upgrade §1): Channels, Integrations,
 * Users & Roles, Brands & Tenants, Message Templates, Script Templates and
 * Notifications moved to top-level sidebar routes. The retired
 * /settings/<id> routes redirect (see MOVED_AREA_ROUTES).
 */
export const SETTINGS_TABS: { id: string; lbl: string; superadminOnly?: boolean }[] = [
  { id: 'ai', lbl: 'AI Configuration' },
  { id: 'ai-providers', lbl: 'AI & Providers' }, // GET any role (read-only); PUT superadmin
  { id: 'persona', lbl: 'AI Persona' },
  { id: 'knowledge', lbl: 'Knowledge Base' },
  { id: 'skills', lbl: 'AI Skills' },
  { id: 'assignments', lbl: 'Account Assignment' },
  { id: 'appkeys', lbl: 'App Keys', superadminOnly: true },
  { id: 'pronunciation', lbl: 'Pronunciation' },
  { id: 'booking-hours', lbl: 'Booking Hours' },
]

export function SettingsShell({ children }: { children: ReactNode }) {
  return (
    <SettingsDirtyProvider>
      <SettingsShellInner>{children}</SettingsShellInner>
    </SettingsDirtyProvider>
  )
}

/** Right-aligned dirty-state cluster in the header: shows Discard + Save
 * changes once a tab marks itself dirty, or a brief "Saved" confirmation. */
function DirtyControls() {
  const { dirty, saving, saved, save, discard } = useSettingsDirty()
  if (dirty || saving) {
    return (
      <div className="flex items-center gap-2">
        <span className="hidden text-[12px] font-semibold text-amber-300 sm:inline">Unsaved changes</span>
        <button
          onClick={discard}
          disabled={saving}
          className="cursor-pointer rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-muted transition-colors hover:bg-fill-hover disabled:opacity-50"
        >
          Discard
        </button>
        <button
          onClick={() => void save()}
          disabled={saving}
          className="flex cursor-pointer items-center gap-1.5 rounded-xl px-3.5 py-1.5 text-[12px] font-bold text-white disabled:opacity-60"
          style={{ background: 'linear-gradient(135deg,#3B82F6,#2563EB)' }}
        >
          {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
          Save changes
        </button>
      </div>
    )
  }
  if (saved) {
    return (
      <span className="flex items-center gap-1.5 text-[12px] font-semibold text-brand-300">
        <Check size={14} strokeWidth={2.25} /> Saved
      </span>
    )
  }
  return null
}

function SettingsShellInner({ children }: { children: ReactNode }) {
  const pathname = usePathname() || '/settings'
  const [role, setRoleState] = useState<Role>('admin')

  // role read after mount (localStorage) — super-admin-only tabs hide for admins
  useEffect(() => setRoleState(getRole()), [])
  const visibleTabs = SETTINGS_TABS.filter((t) => !t.superadminOnly || role === 'superadmin')

  return (
    /* Full-page screen (design.md): Settings breaks the shell for more room.
       Tabs with stage-then-save controls drive the header's Save/Discard via
       SettingsDirtyContext; live instant-save tabs never mark dirty, so the
       header stays clean and Back just returns to the app. */
    <div className="flex h-full flex-col bg-app">
      {/* Header bar: title/subtitle + dirty controls (the app sidebar/topbar
          now provide navigation, so no Back button here). */}
      <header className="flex h-[56px] shrink-0 items-center gap-3 border-b border-line bg-surface px-4">
        <div className="min-w-0">
          <div className="text-[15px] font-extrabold tracking-[-0.4px] text-text-heading">Settings</div>
          <div className="truncate text-[11px] text-text-muted">Providers &amp; configuration</div>
        </div>
        <div className="ml-auto">
          <DirtyControls />
        </div>
      </header>

      <div className="flex min-h-0 flex-1 gap-4 p-4">
        {/* Tab rail */}
        <div className="flex w-[200px] shrink-0 flex-col gap-0.5 overflow-y-auto no-scrollbar">
          {visibleTabs.map((t) => {
            const href = `/settings/${t.id}`
            const active = pathname === href
            return (
              <Link
                key={t.id}
                href={href}
                aria-current={active ? 'page' : undefined}
                className="cursor-pointer rounded-xl px-3 py-[9px] text-[13px] transition-colors"
                style={{
                  fontWeight: active ? 600 : 500,
                  background: active ? 'var(--nav-active-bg)' : 'transparent',
                  color: active ? 'var(--nav-active-text)' : 'var(--text-muted)',
                }}
              >
                {t.lbl}
              </Link>
            )
          })}
        </div>

        {/* Content */}
        <div className="min-h-0 flex-1 overflow-y-auto rounded-2xl border border-line bg-card px-6 pb-6 pt-4">
          {children}
        </div>
      </div>
    </div>
  )
}
