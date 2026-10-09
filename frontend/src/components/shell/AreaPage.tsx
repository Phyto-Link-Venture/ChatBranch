'use client'

import { type ReactNode } from 'react'
import { Check, Loader2, type LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { PageHeader } from '@/components/ui/PageHeader'
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
        <Button variant="ghost" size="sm" onClick={discard} disabled={saving}>
          Discard
        </Button>
        <Button variant="primary" size="sm" onClick={() => void save()} disabled={saving}>
          {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
          Save changes
        </Button>
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
  eyebrow,
  children,
}: {
  title: string
  subtitle?: string
  Icon: LucideIcon
  /** Small label above the title (the sidebar group, e.g. "Library"). */
  eyebrow?: string
  children: ReactNode
}) {
  return (
    <SettingsDirtyProvider>
      <div className="flex min-h-0 flex-1 flex-col gap-4">
        <PageHeader
          eyebrow={
            <span className="inline-flex items-center gap-1.5">
              <Icon size={12} strokeWidth={2.2} aria-hidden />
              {eyebrow ?? 'Workspace'}
            </span>
          }
          title={title}
          subtitle={subtitle}
          actions={<DirtyControls />}
        />

        {/* Height-capped (flex-1) so tall content scrolls inside the page body
            while the masthead and its Save/Discard stay in view. */}
        <div className="min-h-0 flex-1 overflow-y-auto">{children}</div>
      </div>
    </SettingsDirtyProvider>
  )
}
