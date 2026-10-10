'use client'

import { type ReactNode } from 'react'
import { Check, Loader2, type LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { PageHeader } from '@/components/ui/PageHeader'
import {
  SettingsDirtyProvider,
  useSettingsDirty,
} from '@/components/settings/SettingsDirtyContext'
import { useT } from '@/i18n'

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
  const t = useT()
  const { dirty, saving, saved, save, discard } = useSettingsDirty()
  if (dirty || saving) {
    return (
      <div className="flex items-center gap-2">
        <span className="hidden text-[12px] font-semibold text-amber-300 sm:inline">{t('ui.saveBar.unsaved')}</span>
        <Button variant="ghost" size="sm" onClick={discard} disabled={saving}>
          {t('common.actions.discard')}
        </Button>
        <Button variant="primary" size="sm" onClick={() => void save()} disabled={saving}>
          {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {t('ui.saveBar.saveChanges')}
        </Button>
      </div>
    )
  }
  if (saved) {
    return (
      <span className="flex items-center gap-1.5 text-[12px] font-semibold text-brand-300">
        <Check size={14} strokeWidth={2.25} /> {t('common.status.saved')}
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
  actions,
  children,
}: {
  title: string
  subtitle?: string
  Icon: LucideIcon
  /** Small label above the title (the sidebar group, e.g. "Library"). */
  eyebrow?: string
  /** Extra masthead actions (links, buttons), shown before Save / Discard. */
  actions?: ReactNode
  children: ReactNode
}) {
  const t = useT()
  return (
    <SettingsDirtyProvider>
      {/* below lg: natural height, scrolling with the page pane (a capped flex
          column made a tiny nested scroll box on phones and in landscape) */}
      <div className="flex shrink-0 flex-col gap-4 lg:min-h-0 lg:flex-1 lg:shrink">
        <PageHeader
          eyebrow={
            <span className="inline-flex items-center gap-1.5">
              <Icon size={12} strokeWidth={2.2} aria-hidden />
              {eyebrow ?? t('shell.area.workspace')}
            </span>
          }
          title={title}
          subtitle={subtitle}
          actions={
            <>
              {actions}
              <DirtyControls />
            </>
          }
        />

        {/* From lg: height-capped (flex-1) so tall content scrolls inside the page
            body while the masthead and its Save/Discard stay in view. */}
        <div className="lg:min-h-0 lg:flex-1 lg:overflow-y-auto">{children}</div>
      </div>
    </SettingsDirtyProvider>
  )
}
