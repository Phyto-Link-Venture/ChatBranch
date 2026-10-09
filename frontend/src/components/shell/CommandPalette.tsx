'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { usePathname, useRouter } from 'next/navigation'
import {
  ArrowRight,
  BookOpen,
  Building2,
  CornerDownLeft,
  FileText,
  History,
  Loader2,
  Search,
  UserCog,
  UserRound,
  Workflow,
  X,
  type LucideIcon,
} from 'lucide-react'
import { authHeaders, capabilitiesFor, type AuthUser } from '@/lib/auth'
import { useEntityQuery } from '@/lib/EntityContext'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { visiblePages } from './nav'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const MIN_CHARS = 2
const MAX_RECENT = 6

/** One row of GET /api/search. */
interface Hit {
  type: 'contact' | 'conversation' | 'template' | 'knowledge' | 'flow' | 'user' | 'entity'
  id: string
  title: string
  subtitle: string
  url: string
  channel?: string
}

interface Item {
  key: string
  group: string
  title: string
  subtitle?: string
  /** Navigate here… */
  url?: string
  /** …or fill the box with this query (recent searches). */
  fill?: string
  Icon?: LucideIcon
  channel?: string
  hint?: boolean
}

const GROUPS: { type: Hit['type']; lbl: MessageKey; Icon: LucideIcon }[] = [
  { type: 'contact', lbl: 'shell.nav.contacts', Icon: UserRound },
  { type: 'conversation', lbl: 'shell.palette.groups.conversations', Icon: UserRound },
  { type: 'template', lbl: 'shell.nav.templates', Icon: FileText },
  { type: 'knowledge', lbl: 'shell.nav.knowledge', Icon: BookOpen },
  { type: 'flow', lbl: 'shell.palette.groups.flows', Icon: Workflow },
  { type: 'user', lbl: 'shell.palette.groups.users', Icon: UserCog },
  { type: 'entity', lbl: 'shell.nav.entities', Icon: Building2 },
]

const recentKey = (u: AuthUser | null) => `cmdk-recent:${u?.username ?? 'anon'}`

function readRecent(u: AuthUser | null): string[] {
  try {
    const raw = localStorage.getItem(recentKey(u))
    const arr = raw ? JSON.parse(raw) : []
    return Array.isArray(arr) ? arr.filter((x) => typeof x === 'string').slice(0, MAX_RECENT) : []
  } catch {
    return []
  }
}

function pushRecent(u: AuthUser | null, q: string): void {
  const term = q.trim()
  if (term.length < MIN_CHARS) return
  try {
    const next = [term, ...readRecent(u).filter((x) => x.toLowerCase() !== term.toLowerCase())].slice(0, MAX_RECENT)
    localStorage.setItem(recentKey(u), JSON.stringify(next))
  } catch {
    /* ignore */
  }
}

/** Client-side page match: label first, then group and keywords. */
function matchPages(user: AuthUser | null, q: string, tr: TFunction) {
  const t = q.trim().toLowerCase()
  const pages = visiblePages(user)
  if (!t) return pages.slice(0, 6).map((p) => ({ p, score: 0 }))
  return pages
    .map((p) => {
      // the translated label first; the English keywords keep working in every language
      const lbl = tr(p.lbl).toLowerCase()
      const hay = `${p.group ? tr(p.group) : ''} ${p.keywords ?? ''} ${p.href}`.toLowerCase()
      const score = lbl.startsWith(t) ? 3 : lbl.includes(t) ? 2 : hay.split(/[\s/]+/).some((w) => w.startsWith(t)) ? 1 : 0
      return { p, score }
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score)
    .slice(0, 6)
}

/**
 * The command palette: the top-bar search field and Ctrl/⌘+K open it. Instant
 * results grouped by type — Pages (from the nav this user can see), Contacts,
 * Conversations (contact name, address and message text), Templates,
 * Knowledge, Flows, Users (admins), Entities — from GET /api/search, which
 * applies the same capability gates and entity scope as each page's list.
 * ↑/↓ moves, Enter opens, Esc closes; recent searches are remembered per user.
 */
export function CommandPalette({
  open,
  onClose,
  user,
}: {
  open: boolean
  onClose: () => void
  user: AuthUser | null
}) {
  const router = useRouter()
  const pathname = usePathname()
  const entityQ = useEntityQuery()
  const inputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<Hit[]>([])
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const [active, setActive] = useState(0)
  const [recent, setRecent] = useState<string[]>([])

  useEffect(() => {
    if (!open) return
    setQ('')
    setHits([])
    setActive(0)
    setRecent(readRecent(user))
    const t = window.setTimeout(() => inputRef.current?.focus(), 0)
    return () => window.clearTimeout(t)
  }, [open, user])

  // Debounced server search; each keystroke aborts the previous request.
  useEffect(() => {
    if (!open) return
    const term = q.trim()
    if (term.length < MIN_CHARS) {
      setHits([])
      setLoading(false)
      setFailed(false)
      return
    }
    const ctrl = new AbortController()
    setLoading(true)
    const t = window.setTimeout(async () => {
      try {
        const res = await fetch(
          `${API}/search?q=${encodeURIComponent(term)}&limit=8${entityQ ? `&${entityQ}` : ''}`,
          { headers: authHeaders(), signal: ctrl.signal },
        )
        if (!res.ok) throw new Error(`HTTP ${res.status}`)
        const data = (await res.json()) as { results?: Hit[] }
        setHits(data.results ?? [])
        setFailed(false)
      } catch (e) {
        if ((e as Error).name !== 'AbortError') {
          setHits([])
          setFailed(true)
        }
      } finally {
        if (!ctrl.signal.aborted) setLoading(false)
      }
    }, 150)
    return () => {
      ctrl.abort()
      window.clearTimeout(t)
    }
  }, [q, open, entityQ])

  const tr = useT()
  const caps = useMemo(() => capabilitiesFor(user), [user])

  const items = useMemo<Item[]>(() => {
    const term = q.trim()
    const out: Item[] = []
    if (!term && recent.length) {
      for (const r of recent)
        out.push({ key: `recent:${r}`, group: tr('shell.palette.recent'), title: r, fill: r, Icon: History })
    }
    for (const { p } of matchPages(user, term, tr)) {
      out.push({
        key: `page:${p.href}`,
        group: term ? tr('shell.palette.pages') : tr('shell.palette.jumpTo'),
        title: tr(p.lbl),
        subtitle: p.group ? tr(p.group) : undefined,
        url: p.href,
        Icon: p.Icon,
      })
    }
    if (term.length >= MIN_CHARS) {
      for (const g of GROUPS) {
        const rows = hits.filter((h) => h.type === g.type)
        for (const h of rows)
          out.push({
            key: `${h.type}:${h.id}`,
            group: tr(g.lbl),
            title: h.title,
            subtitle: h.subtitle,
            url: h.url,
            Icon: g.Icon,
            channel: h.channel,
          })
      }
      // "Search all" — hand the term to the full list screens, last so the
      // first real result stays the default.
      if (caps.includes('contacts.view'))
        out.push({
          key: 'all:contacts',
          group: tr('shell.palette.searchAll'),
          title: tr('shell.palette.searchAllContacts', { term }),
          url: `/contacts?q=${encodeURIComponent(term)}`,
          Icon: Search,
          hint: true,
        })
      if (caps.includes('chats.view'))
        out.push({
          key: 'all:chats',
          group: tr('shell.palette.searchAll'),
          title: tr('shell.palette.searchAllChats', { term }),
          url: `/chats?q=${encodeURIComponent(term)}`,
          Icon: Search,
          hint: true,
        })
    }
    return out
  }, [q, hits, recent, user, caps, tr])

  useEffect(() => setActive(0), [q, hits.length])

  // Keep the highlighted row in view.
  useEffect(() => {
    listRef.current
      ?.querySelector<HTMLElement>(`[data-idx="${active}"]`)
      ?.scrollIntoView({ block: 'nearest' })
  }, [active])

  const choose = useCallback(
    (it: Item | undefined) => {
      if (!it) return
      if (it.fill !== undefined) {
        setQ(it.fill)
        inputRef.current?.focus()
        return
      }
      if (!it.url) return
      pushRecent(user, q)
      onClose()
      // The inbox only reads its ?channel=&key= deep link on mount — jumping
      // to another chat while already on /chats needs a real navigation.
      if (pathname === '/chats' && it.url.startsWith('/chats?')) window.location.assign(it.url)
      else router.push(it.url)
    },
    [onClose, pathname, q, router, user],
  )

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault()
      onClose()
    } else if (e.key === 'ArrowDown') {
      e.preventDefault()
      setActive((i) => (items.length ? (i + 1) % items.length : 0))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((i) => (items.length ? (i - 1 + items.length) % items.length : 0))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      choose(items[active])
    } else if (e.key === 'Tab') {
      e.preventDefault() // focus stays in the field; arrows move the selection
    }
  }

  if (!open) return null

  const term = q.trim()
  const noResults = term.length >= MIN_CHARS && !loading && !failed && hits.length === 0
  let lastGroup = ''

  return (
    <div
      className="fixed inset-0 z-[70] flex animate-fadeIn items-start justify-center px-3 pt-[8vh] sm:px-6 sm:pt-[12vh]"
      style={{ background: 'var(--scrim)' }}
      onMouseDown={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label={tr('common.actions.search')}
        className="flex max-h-[76vh] w-full max-w-[640px] flex-col overflow-hidden rounded-2xl border border-line-strong bg-card shadow-pop"
        onKeyDown={onKeyDown}
      >
        <div className="flex items-center gap-3 border-b border-line px-4">
          {loading ? (
            <Loader2 size={18} className="shrink-0 animate-spin text-text-muted" aria-hidden />
          ) : (
            <Search size={18} strokeWidth={1.6} className="shrink-0 text-text-muted" aria-hidden />
          )}
          <input
            ref={inputRef}
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder={tr('shell.topBar.searchPlaceholder')}
            aria-label={tr('common.actions.search')}
            role="combobox"
            aria-expanded="true"
            aria-controls="cmdk-list"
            aria-activedescendant={items[active] ? `cmdk-${active}` : undefined}
            autoComplete="off"
            spellCheck={false}
            className="h-14 min-w-0 flex-1 bg-transparent text-[16px] text-text-primary outline-none placeholder:text-text-faint"
          />
          {q ? (
            <button
              type="button"
              onClick={() => {
                setQ('')
                inputRef.current?.focus()
              }}
              aria-label={tr('shell.palette.clear')}
              className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={15} aria-hidden />
            </button>
          ) : (
            <kbd className="hidden shrink-0 rounded border border-line px-1.5 py-px font-sans text-[11px] font-semibold text-text-muted sm:block">
              Esc
            </kbd>
          )}
        </div>

        <div ref={listRef} id="cmdk-list" role="listbox" aria-label={tr('shell.palette.results')} className="min-h-0 flex-1 overflow-y-auto py-1.5">
          {term.length > 0 && term.length < MIN_CHARS && (
            <div className="px-4 pb-1 pt-2 text-[12px] text-text-muted">{tr('shell.palette.oneMore')}</div>
          )}
          {failed && (
            <div className="px-4 pb-1 pt-2 text-[12px] text-[color:var(--red-ink)]">
              {tr('shell.palette.unavailable')}
            </div>
          )}
          {noResults && (
            <div className="px-4 pb-1 pt-3 text-[13px] text-text-muted">
              {tr('shell.palette.noRecords', { term })}
            </div>
          )}
          {items.map((it, idx) => {
            const header = it.group !== lastGroup ? it.group : null
            lastGroup = it.group
            const isActiveRow = idx === active
            return (
              <div key={it.key}>
                {header && <div className="eyebrow px-4 pb-1 pt-3">{header}</div>}
                <div
                  id={`cmdk-${idx}`}
                  data-idx={idx}
                  role="option"
                  aria-selected={isActiveRow}
                  onMouseMove={() => active !== idx && setActive(idx)}
                  onClick={() => choose(it)}
                  className={`mx-1.5 flex cursor-pointer items-center gap-3 rounded-lg px-2.5 py-2 ${
                    isActiveRow ? 'bg-fill-hover' : ''
                  }`}
                >
                  <span className="grid h-7 w-7 shrink-0 place-items-center rounded-md border border-line bg-surface text-text-mid">
                    {it.channel ? (
                      <ChannelLogo channel={it.channel} size={15} />
                    ) : it.Icon ? (
                      <it.Icon size={14} strokeWidth={1.75} aria-hidden />
                    ) : null}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className={`block truncate text-[13.5px] ${it.hint ? 'text-text-mid' : 'font-medium text-text-high'}`}>
                      {it.title}
                    </span>
                    {it.subtitle && <span className="block truncate text-[12px] text-text-muted">{it.subtitle}</span>}
                  </span>
                  {isActiveRow && (
                    <span className="shrink-0 text-text-muted" aria-hidden>
                      {it.fill !== undefined ? <ArrowRight size={14} /> : <CornerDownLeft size={14} />}
                    </span>
                  )}
                </div>
              </div>
            )
          })}
        </div>

        <div className="hidden items-center gap-4 border-t border-line bg-surface px-4 py-2 text-[11.5px] text-text-muted sm:flex">
          <span className="shrink-0">
            <Trans
              k="shell.palette.hintMove"
              values={{
                keys: (
                  <>
                    <kbd className="font-sans font-semibold">↑</kbd> <kbd className="font-sans font-semibold">↓</kbd>
                  </>
                ),
              }}
            />
          </span>
          <span className="shrink-0">
            <Trans k="shell.palette.hintOpen" values={{ key: <kbd className="font-sans font-semibold">↵</kbd> }} />
          </span>
          <span className="shrink-0">
            <Trans k="shell.palette.hintClose" values={{ key: <kbd className="font-sans font-semibold">Esc</kbd> }} />
          </span>
          <span className="ml-auto min-w-0 truncate">{tr('shell.palette.scopeNote')}</span>
        </div>
      </div>
    </div>
  )
}
