'use client'

import { type ReactNode } from 'react'
import { Check, Loader2, type LucideIcon } from 'lucide-react'
import {
  SettingsDirtyProvider,
  useSettingsDirty,
} from '@/components/settings/SettingsDirtyContext'

/**
 * Chrome for the areas promoted out of Settings into their own top-level routes
 * (upgrade §1): Channels, Integrations, Users & Roles, Entities & Tenants, Message
 * Templates, Script Templates, Notifications. Gives each a page header (icon
 * chip + title + subtitle) matching the app's other pages, and — because a few
 * of the reused settings components (Integrations' OAuth keys) drive the shared
 * Save/Discard cluster through SettingsDirtyContext — hosts that provider and
 * surfaces its controls in the header. Components that never mark dirty simply
 * leave the header clean.
 */

/** Save/Discard cluster, identical to the Settings header's, wired to the
 *  dirty context so reused components keep working outside SettingsShell. */
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

export function AreaPage({
  title,
  subtitle,
  Icon,
  children,
}: {
  title: string
  subtitle?: string
  Icon: LucideIcon
  children: ReactNode
}) {
  return (
    <SettingsDirtyProvider>
      <div className="flex min-h-0 flex-1 flex-col gap-3.5">
        <div className="flex items-center gap-3">
          <div className="grid h-[38px] w-[38px] shrink-0 place-items-center rounded-xl border border-line bg-fill">
            {/* Tailwind text class (not a var() color= prop) keeps the icon theme-aware. */}
            <Icon size={18} strokeWidth={1.9} className="text-brand-300" aria-hidden />
          </div>
          <div className="min-w-0">
            <h1 className="text-[20px] font-extrabold tracking-[-0.5px] text-text-heading">{title}</h1>
            {subtitle && <div className="mt-0.5 text-[12px] text-text-muted">{subtitle}</div>}
          </div>
          <div className="ml-auto">
            <DirtyControls />
          </div>
        </div>

        {/* overflow-y-auto: the card is height-capped (flex-1), so tall content
            (e.g. an expanded Advanced section) must scroll INSIDE it, not spill
            out the bottom. This matches the old Settings shell's content card. */}
        <div className="min-h-0 flex-1 overflow-y-auto rounded-2xl border border-line bg-card px-5 pb-6 pt-2">
          {children}
        </div>
      </div>
    </SettingsDirtyProvider>
  )
}
