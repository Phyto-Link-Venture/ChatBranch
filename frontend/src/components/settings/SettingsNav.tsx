'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Receipt,
  BellOff,
  BellRing,
  Check,
  ChevronDown,
  Cpu,
  KeyRound,
  Link2,
  ListPlus,
  Loader2,
  Lock,
  Palette,
  type LucideIcon,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useAccess } from '@/lib/useAccess'
import { Trans, useT, type MessageKey } from '@/i18n'
import { SettingsDirtyProvider, useSettingsDirty } from './SettingsDirtyContext'

/**
 * Settings = SECTIONS of sub-pages. Every sub-page is its own route
 * (/settings/<id>, flat so the long-standing ids and bookmarks keep working)
 * and carries its OWN capability — the nav shows only what the caller may
 * open, and the page body refuses the rest. Personal pages need none.
 *
 * Add a page = an entry below + an app/settings/<id>/page.tsx (+ the
 * capability in AdminEndpoints.cs and lib/auth.ts when it is gated, and the
 * server-side check on its write endpoint).
 *
 * Moved out over time (their old routes redirect): Channels, Integrations,
 * Users, Entities, Templates, Script Templates, Knowledge, Skills (sidebar);
 * Account assignment (/assignments, a Work module); Booking hours (Google
 * Calendar integration); Pronunciation (Voice & Calls); Persona (retired).
 */
export interface SettingsPageDef {
  id: string
  /** Translation keys — render with `t(p.lbl)` / `t(p.blurb)`. */
  lbl: MessageKey
  blurb: MessageKey
  Icon: LucideIcon
  /** Capability that opens this page; omitted = every signed-in user. */
  cap?: string
}

export interface SettingsSectionDef {
  id: string
  /** Translation keys — render with `t(s.lbl)` / `t(s.blurb)`. */
  lbl: MessageKey
  blurb: MessageKey
  pages: SettingsPageDef[]
}

export const SETTINGS_SECTIONS: SettingsSectionDef[] = [
  {
    id: 'personal',
    lbl: 'settings.nav.sections.personal.lbl',
    blurb: 'settings.nav.sections.personal.blurb',
    pages: [
      {
        id: 'appearance',
        lbl: 'settings.nav.pages.appearance.lbl',
        blurb: 'settings.nav.pages.appearance.blurb',
        Icon: Palette,
      },
      {
        id: 'my-notifications',
        lbl: 'settings.nav.pages.myNotifications.lbl',
        blurb: 'settings.nav.pages.myNotifications.blurb',
        Icon: BellRing,
      },
      {
        id: 'connections',
        lbl: 'settings.nav.pages.connections.lbl',
        blurb: 'settings.nav.pages.connections.blurb',
        Icon: Link2,
      },
    ],
  },
  {
    id: 'organization',
    lbl: 'settings.nav.sections.organization.lbl',
    blurb: 'settings.nav.sections.organization.blurb',
    pages: [
      {
        id: 'notifications',
        lbl: 'settings.nav.pages.notifications.lbl',
        blurb: 'settings.nav.pages.notifications.blurb',
        Icon: BellOff,
        cap: 'notifications.manage',
      },
      {
        id: 'custom-fields',
        lbl: 'settings.nav.pages.customFields.lbl',
        blurb: 'settings.nav.pages.customFields.blurb',
        Icon: ListPlus,
        cap: 'settings.custom_fields.manage',
      },
      {
        id: 'appkeys',
        lbl: 'settings.nav.pages.appkeys.lbl',
        blurb: 'settings.nav.pages.appkeys.blurb',
        Icon: KeyRound,
        cap: 'app_keys.manage',
      },
      {
        id: 'billing',
        lbl: 'settings.nav.pages.billing.lbl',
        blurb: 'settings.nav.pages.billing.blurb',
        Icon: Receipt,
        cap: 'billing.manage',
      },
    ],
  },
  {
    id: 'ai',
    lbl: 'settings.nav.sections.ai.lbl',
    blurb: 'settings.nav.sections.ai.blurb',
    pages: [
      {
        id: 'ai-providers',
        lbl: 'settings.nav.pages.aiProviders.lbl',
        blurb: 'settings.nav.pages.aiProviders.blurb',
        Icon: Cpu,
        cap: 'settings.ai_keys.manage',
      },
      // External MCP servers live on /developers (with API keys and
      // ChatBranch's own MCP server); /settings/mcp redirects there.
    ],
  },
]

/** Flat list (back-compat for the /settings redirect and old imports). */
export const SETTINGS_TABS: { id: string; lbl: MessageKey; cap?: string; section: string }[] =
  SETTINGS_SECTIONS.flatMap((s) => s.pages.map((p) => ({ id: p.id, lbl: p.lbl, cap: p.cap, section: s.id })))

export function findSettingsPage(id: string): { section: SettingsSectionDef; page: SettingsPageDef } | null {
  for (const section of SETTINGS_SECTIONS) {
    const page = section.pages.find((p) => p.id === id)
    if (page) return { section, page }
  }
  return null
}

export function SettingsShell({ children }: { children: ReactNode }) {
  return (
    <SettingsDirtyProvider>
      <SettingsShellInner>{children}</SettingsShellInner>
    </SettingsDirtyProvider>
  )
}

/** Discard + Save cluster for pages that stage edits (SettingsDirtyContext). */
function DirtyControls() {
  const t = useT()
  const { dirty, saving, saved, save, discard } = useSettingsDirty()
  if (dirty || saving) {
    return (
      <div className="flex items-center gap-2">
        <span className="hidden text-[12px] font-semibold text-[color:var(--amber-ink)] sm:inline">{t('settings.common.unsavedChanges')}</span>
        <Button variant="ghost" size="sm" onClick={discard} disabled={saving}>
          {t('common.actions.discard')}
        </Button>
        <Button variant="primary" size="sm" onClick={() => void save()} disabled={saving}>
          {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
          {t('settings.common.saveChanges')}
        </Button>
      </div>
    )
  }
  if (saved) {
    return (
      <span className="flex items-center gap-1.5 text-[12px] font-semibold text-[color:var(--green-ink)]">
        <Check size={14} strokeWidth={2.25} aria-hidden /> {t('common.status.saved')}
      </span>
    )
  }
  return null
}

const COLLAPSE_KEY = 'settings-nav-collapsed'

function SettingsShellInner({ children }: { children: ReactNode }) {
  const t = useT()
  const pathname = usePathname() || '/settings'
  const { ready, can } = useAccess()
  const currentId = pathname.replace(/^\/settings\/?/, '').split('/')[0] || ''
  const current = findSettingsPage(currentId)

  // Sections the caller can open at least one page of. Personal pages are
  // ungated, so the first paint (before the session is read) already shows them.
  const sections = useMemo(
    () =>
      SETTINGS_SECTIONS.map((s) => ({ ...s, pages: s.pages.filter((p) => !p.cap || (ready && can(p.cap))) })).filter(
        (s) => s.pages.length > 0,
      ),
    [ready, can],
  )

  // Desktop section collapse, remembered per browser (never for the open page's section).
  const [collapsed, setCollapsed] = useState<string[]>([])
  useEffect(() => {
    try {
      const raw = localStorage.getItem(COLLAPSE_KEY)
      if (raw) setCollapsed(JSON.parse(raw))
    } catch {
      /* storage blocked: everything stays expanded */
    }
  }, [])
  const toggleSection = (id: string) =>
    setCollapsed((prev) => {
      const next = prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]
      try {
        localStorage.setItem(COLLAPSE_KEY, JSON.stringify(next))
      } catch {
        /* ignore */
      }
      return next
    })

  const mobileSection = sections.find((s) => s.id === current?.section.id) ?? sections[0]
  const allowed = !current?.page.cap || can(current.page.cap)

  return (
    // Phone: the whole page scrolls as one. md+: the section rail and the page
    // scroll independently under a fixed masthead.
    <div className="flex h-full min-h-0 flex-col overflow-y-auto bg-panel md:overflow-hidden">
      {/* Masthead */}
      {/* Phones: the shell's top bar already titles the page, so only the
          Save/Discard cluster remains here. */}
      <div className="shrink-0 px-4 pt-3 sm:px-6 md:pt-5">
        <div className="flex flex-wrap items-end gap-x-4 gap-y-2 md:border-b md:border-text-primary/80 md:pb-3">
          <div className="hidden min-w-0 md:block">
            <div className="eyebrow">{t('settings.shell.eyebrow')}</div>
            <h1 className="display mt-0.5 text-[30px] leading-[1.05] text-text-heading sm:text-[36px]">{t('shell.nav.settings')}</h1>
            <div className="mt-1 text-[13px] text-text-muted">{t('settings.shell.subtitle')}</div>
          </div>
          <div className="ml-auto">
            <DirtyControls />
          </div>
        </div>
      </div>

      {/* Mobile navigation: section tabs, then the section's pages as chips. */}
      <nav className="shrink-0 px-4 pt-3 sm:px-6 md:hidden" aria-label={t('settings.shell.sectionsAria')}>
        <div className="flex gap-5 border-b border-line">
          {sections.map((s) => {
            const active = s.id === mobileSection?.id
            return (
              <Link
                key={s.id}
                href={`/settings/${s.pages[0].id}`}
                aria-current={active ? 'true' : undefined}
                className={`-mb-px border-b-2 pb-2 text-[13px] transition-colors ${
                  active
                    ? 'border-text-heading font-semibold text-text-heading'
                    : 'border-transparent text-text-muted hover:text-text-high'
                }`}
              >
                {t(s.lbl)}
              </Link>
            )
          })}
        </div>
        {mobileSection && (
          <div className="flex flex-wrap gap-1.5 py-3">
            {mobileSection.pages.map((p) => {
              const active = p.id === currentId
              return (
                <Link
                  key={p.id}
                  href={`/settings/${p.id}`}
                  aria-current={active ? 'page' : undefined}
                  className={`inline-flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-[12.5px] transition-colors ${
                    active
                      ? 'border-transparent bg-primary font-semibold text-on-primary'
                      : 'border-line-strong bg-card text-text-mid hover:text-text-high'
                  }`}
                >
                  <p.Icon size={13} aria-hidden />
                  {t(p.lbl)}
                </Link>
              )
            })}
          </div>
        )}
      </nav>

      <div className="flex md:min-h-0 md:flex-1">
        {/* Desktop navigation: collapsible sections. */}
        <nav
          data-tour="settings-nav"
          className="hidden w-[244px] shrink-0 overflow-y-auto border-r border-line px-3 py-5 md:block"
          aria-label={t('settings.shell.sectionsAria')}
        >
          {sections.map((s) => {
            const holdsCurrent = s.id === current?.section.id
            const open = holdsCurrent || !collapsed.includes(s.id)
            return (
              <div key={s.id} className="mb-4">
                <button
                  type="button"
                  onClick={() => toggleSection(s.id)}
                  disabled={holdsCurrent}
                  aria-expanded={open}
                  title={t(s.blurb)}
                  className="flex w-full items-center gap-2 px-2 pb-1.5 text-left disabled:cursor-default"
                >
                  <span className="eyebrow">{t(s.lbl)}</span>
                  <span className="truncate text-[11px] text-text-faint">· {t(s.blurb)}</span>
                  <ChevronDown
                    size={13}
                    aria-hidden
                    className={`ml-auto shrink-0 text-text-faint transition-transform ${open ? '' : '-rotate-90'} ${
                      holdsCurrent ? 'opacity-0' : ''
                    }`}
                  />
                </button>
                {open && (
                  <ul className="flex flex-col gap-0.5 border-t border-line pt-1.5">
                    {s.pages.map((p) => {
                      const active = p.id === currentId
                      return (
                        <li key={p.id}>
                          <Link
                            href={`/settings/${p.id}`}
                            aria-current={active ? 'page' : undefined}
                            className={`flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-[13px] transition-colors ${
                              active ? 'font-semibold' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
                            }`}
                            style={
                              active ? { background: 'var(--nav-active-bg)', color: 'var(--nav-active-text)' } : undefined
                            }
                          >
                            <p.Icon size={15} strokeWidth={1.8} aria-hidden className="shrink-0" />
                            <span className="truncate">{t(p.lbl)}</span>
                            {p.cap && <Lock size={11} aria-hidden className="ml-auto shrink-0 opacity-40" />}
                          </Link>
                        </li>
                      )
                    })}
                  </ul>
                )}
              </div>
            )
          })}
          {sections.some((s) => s.pages.some((p) => p.cap)) && (
            <p className="px-2 text-[11px] leading-relaxed text-text-faint">
              {t('settings.shell.lockNote')}
            </p>
          )}
        </nav>

        {/* Page */}
        <div className="min-w-0 flex-1 px-4 pb-8 pt-2 sm:px-6 md:min-h-0 md:overflow-y-auto md:px-8 md:pt-6">
          <div className="mx-auto w-full max-w-[920px]">
            {current && (
              <header className="mb-5 border-b border-line pb-4">
                <div className="eyebrow">{t(current.section.lbl)}</div>
                <h2 className="display mt-0.5 text-[24px] leading-tight text-text-heading">{t(current.page.lbl)}</h2>
                <p className="mt-1 max-w-[640px] text-[13px] leading-relaxed text-text-muted">{t(current.page.blurb)}</p>
              </header>
            )}
            {current?.page.cap && !ready ? null : allowed ? (
              children
            ) : (
              <NoAccess label={current ? t(current.page.lbl) : t('settings.shell.thisPage')} cap={current?.page.cap ?? ''} />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function NoAccess({ label, cap }: { label: string; cap: string }) {
  const t = useT()
  return (
    <div className="rounded-xl border border-dashed border-line-strong bg-card px-5 py-8 text-center">
      <Lock size={18} className="mx-auto text-text-muted" aria-hidden />
      <div className="display mt-2 text-[18px] text-text-heading">{t('settings.shell.noAccessTitle', { page: label })}</div>
      <p className="mx-auto mt-1 max-w-[420px] text-[12.5px] leading-relaxed text-text-muted">
        <Trans k="settings.shell.noAccessBody" values={{ cap: <code className="rounded bg-fill px-1">{cap}</code> }} />
      </p>
      <Link href="/settings/appearance" className="mt-4 inline-block text-[12.5px] font-semibold text-text-high underline underline-offset-2">
        {t('settings.shell.backToOwn')}
      </Link>
    </div>
  )
}
