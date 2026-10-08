'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  BellOff,
  BellRing,
  Check,
  ChevronDown,
  Cpu,
  KeyRound,
  ListPlus,
  Loader2,
  Lock,
  Palette,
  Server,
  type LucideIcon,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { useAccess } from '@/lib/useAccess'
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
  lbl: string
  blurb: string
  Icon: LucideIcon
  /** Capability that opens this page; omitted = every signed-in user. */
  cap?: string
}

export interface SettingsSectionDef {
  id: string
  lbl: string
  blurb: string
  pages: SettingsPageDef[]
}

export const SETTINGS_SECTIONS: SettingsSectionDef[] = [
  {
    id: 'personal',
    lbl: 'Personal',
    blurb: 'Just for you',
    pages: [
      {
        id: 'appearance',
        lbl: 'Appearance',
        blurb: 'Theme, colour mode, accent, text size and corners. Saved to your profile, so it follows you to any device.',
        Icon: Palette,
      },
      {
        id: 'my-notifications',
        lbl: 'My notifications',
        blurb: 'Choose which alerts reach your bell and your notification inbox.',
        Icon: BellRing,
      },
    ],
  },
  {
    id: 'organization',
    lbl: 'Organization',
    blurb: 'Applies to everyone',
    pages: [
      {
        id: 'notifications',
        lbl: 'Notification defaults',
        blurb: 'Organization-wide switches. A muted category reaches nobody, whatever their own choice.',
        Icon: BellOff,
        cap: 'notifications.manage',
      },
      {
        id: 'custom-fields',
        lbl: 'Custom fields',
        blurb: 'Extra fields on customers, usable everywhere as template variables.',
        Icon: ListPlus,
        cap: 'settings.custom_fields.manage',
      },
      {
        id: 'appkeys',
        lbl: 'App & provider keys',
        blurb: 'The webhook verify token and the integration provider credentials.',
        Icon: KeyRound,
        cap: 'app_keys.manage',
      },
    ],
  },
  {
    id: 'ai',
    lbl: 'AI',
    blurb: 'Models and tools',
    pages: [
      {
        id: 'ai-providers',
        lbl: 'AI keys & models',
        blurb: 'Provider keys, the chat model and its fallbacks.',
        Icon: Cpu,
        cap: 'settings.ai_keys.manage',
      },
      {
        id: 'mcp',
        lbl: 'MCP servers',
        blurb: 'External tool servers the assistant may call.',
        Icon: Server,
        cap: 'api_keys.manage', // "Manage API keys & MCP" (platform's capability)
      },
    ],
  },
]

/** Flat list (back-compat for the /settings redirect and old imports). */
export const SETTINGS_TABS: { id: string; lbl: string; cap?: string; section: string }[] =
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
  const { dirty, saving, saved, save, discard } = useSettingsDirty()
  if (dirty || saving) {
    return (
      <div className="flex items-center gap-2">
        <span className="hidden text-[12px] font-semibold text-[color:var(--amber-ink)] sm:inline">Unsaved changes</span>
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
      <span className="flex items-center gap-1.5 text-[12px] font-semibold text-[color:var(--green-ink)]">
        <Check size={14} strokeWidth={2.25} aria-hidden /> Saved
      </span>
    )
  }
  return null
}

const COLLAPSE_KEY = 'settings-nav-collapsed'

function SettingsShellInner({ children }: { children: ReactNode }) {
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
            <div className="eyebrow">Workspace</div>
            <h1 className="display mt-0.5 text-[30px] leading-[1.05] text-text-heading sm:text-[36px]">Settings</h1>
            <div className="mt-1 text-[13px] text-text-muted">Your own preferences, and how the organization is configured.</div>
          </div>
          <div className="ml-auto">
            <DirtyControls />
          </div>
        </div>
      </div>

      {/* Mobile navigation: section tabs, then the section's pages as chips. */}
      <nav className="shrink-0 px-4 pt-3 sm:px-6 md:hidden" aria-label="Settings sections">
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
                {s.lbl}
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
                  {p.lbl}
                </Link>
              )
            })}
          </div>
        )}
      </nav>

      <div className="flex md:min-h-0 md:flex-1">
        {/* Desktop navigation: collapsible sections. */}
        <nav
          className="hidden w-[244px] shrink-0 overflow-y-auto border-r border-line px-3 py-5 md:block"
          aria-label="Settings sections"
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
                  title={s.blurb}
                  className="flex w-full items-center gap-2 px-2 pb-1.5 text-left disabled:cursor-default"
                >
                  <span className="eyebrow">{s.lbl}</span>
                  <span className="truncate text-[11px] text-text-faint">· {s.blurb}</span>
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
                            <span className="truncate">{p.lbl}</span>
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
              Pages marked with a lock need a permission; you only see the ones your role holds.
            </p>
          )}
        </nav>

        {/* Page */}
        <div className="min-w-0 flex-1 px-4 pb-8 pt-2 sm:px-6 md:min-h-0 md:overflow-y-auto md:px-8 md:pt-6">
          <div className="mx-auto w-full max-w-[920px]">
            {current && (
              <header className="mb-5 border-b border-line pb-4">
                <div className="eyebrow">{current.section.lbl}</div>
                <h2 className="display mt-0.5 text-[24px] leading-tight text-text-heading">{current.page.lbl}</h2>
                <p className="mt-1 max-w-[640px] text-[13px] leading-relaxed text-text-muted">{current.page.blurb}</p>
              </header>
            )}
            {current?.page.cap && !ready ? null : allowed ? (
              children
            ) : (
              <NoAccess label={current?.page.lbl ?? 'this page'} cap={current?.page.cap ?? ''} />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function NoAccess({ label, cap }: { label: string; cap: string }) {
  return (
    <div className="rounded-xl border border-dashed border-line-strong bg-card px-5 py-8 text-center">
      <Lock size={18} className="mx-auto text-text-muted" aria-hidden />
      <div className="display mt-2 text-[18px] text-text-heading">No access to {label}</div>
      <p className="mx-auto mt-1 max-w-[420px] text-[12.5px] leading-relaxed text-text-muted">
        Your role does not hold the <code className="rounded bg-fill px-1">{cap}</code> permission. Ask an admin to grant
        it on the Permissions page.
      </p>
      <Link href="/settings/appearance" className="mt-4 inline-block text-[12.5px] font-semibold text-text-high underline underline-offset-2">
        Back to your own settings
      </Link>
    </div>
  )
}
