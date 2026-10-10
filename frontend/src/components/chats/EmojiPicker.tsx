'use client'

/**
 * A small built-in emoji picker (lib/emoji/data.ts — no package): category
 * tabs, search in English / Malay / Chinese, and "recently used" (per browser).
 * A popover anchored to its button on desktop; a bottom sheet on a phone.
 * `EmojiButton` is the composer's smiley button, `EmojiPicker` the panel (the
 * reaction bar's "+" opens it too).
 */
import { useEffect, useMemo, useRef, useState } from 'react'
import { Search, Smile, X } from 'lucide-react'
import { useT } from '@/i18n'
import {
  CATEGORY_ICON,
  EMOJI,
  EMOJI_CATEGORIES,
  rememberEmoji,
  recentEmoji,
  searchEmoji,
  type EmojiCategory,
} from '@/lib/emoji/data'
import './messageExtras.css'

type Tab = 'recent' | EmojiCategory

export function EmojiPicker({
  onPick,
  onClose,
  align = 'left',
  placement = 'above',
}: {
  onPick: (emoji: string) => void
  onClose: () => void
  /** which edge of the anchor the popover lines up with (desktop) */
  align?: 'left' | 'right'
  placement?: 'above' | 'below'
}) {
  const t = useT()
  const [query, setQuery] = useState('')
  const [recent, setRecent] = useState<string[]>([])
  const [tab, setTab] = useState<Tab>('smileys')
  const panelRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)

  // recents live in localStorage — read after mount (never during render / SSR)
  useEffect(() => {
    const r = recentEmoji()
    setRecent(r)
    if (r.length > 0) setTab('recent')
    // a phone's keyboard would cover the sheet — only focus search on a wide screen
    if (window.matchMedia('(min-width: 640px)').matches) searchRef.current?.focus()
  }, [])

  // Escape / a click outside closes it
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        onClose()
      }
    }
    const onDown = (e: PointerEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) onClose()
    }
    document.addEventListener('keydown', onKey, true)
    // next tick, so the click that opened it doesn't close it
    const id = window.setTimeout(() => document.addEventListener('pointerdown', onDown), 0)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      document.removeEventListener('pointerdown', onDown)
      window.clearTimeout(id)
    }
  }, [onClose])

  const list = useMemo(() => {
    if (query.trim()) return searchEmoji(query).map((e) => ({ char: e.char, name: e.name }))
    if (tab === 'recent') return recent.map((c) => ({ char: c, name: EMOJI.find((e) => e.char === c)?.name ?? c }))
    return EMOJI.filter((e) => e.category === tab).map((e) => ({ char: e.char, name: e.name }))
  }, [query, tab, recent])

  const pick = (c: string) => {
    rememberEmoji(c)
    onPick(c)
  }

  const tabs: Tab[] = recent.length > 0 ? ['recent', ...EMOJI_CATEGORIES] : [...EMOJI_CATEGORIES]
  const tabLabel = (k: Tab) => (k === 'recent' ? t('bubble.emoji.recent') : t(`bubble.emoji.categories.${k}`))

  return (
    <>
      {/* phone: a scrim behind the sheet */}
      <div className="cb-emoji-scrim sm:hidden" aria-hidden />
      <div
        ref={panelRef}
        role="dialog"
        aria-label={t('bubble.emoji.open')}
        className={`cb-emoji-panel ${align === 'right' ? 'cb-emoji-right' : 'cb-emoji-left'} ${
          placement === 'below' ? 'cb-emoji-below' : 'cb-emoji-above'
        }`}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-2 border-b border-line px-2.5 py-2">
          <Search size={13} className="shrink-0 text-text-muted" aria-hidden />
          <input
            ref={searchRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('bubble.emoji.search')}
            aria-label={t('bubble.emoji.search')}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
          />
          <button
            type="button"
            onClick={onClose}
            title={t('bubble.emoji.close')}
            aria-label={t('bubble.emoji.close')}
            className="grid h-6 w-6 shrink-0 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <X size={12} aria-hidden />
          </button>
        </div>
        {!query.trim() && (
          <div className="no-scrollbar flex gap-0.5 overflow-x-auto border-b border-line px-1.5 py-1" role="tablist">
            {tabs.map((k) => (
              <button
                key={k}
                type="button"
                role="tab"
                aria-selected={tab === k}
                title={tabLabel(k)}
                aria-label={tabLabel(k)}
                onClick={() => setTab(k)}
                className={`grid h-8 w-8 shrink-0 place-items-center rounded-md text-[17px] transition-colors ${
                  tab === k ? 'bg-fill' : 'opacity-70 hover:bg-fill-hover hover:opacity-100'
                }`}
              >
                {k === 'recent' ? '🕘' : CATEGORY_ICON[k]}
              </button>
            ))}
          </div>
        )}
        <div className="px-2.5 pb-1 pt-2 text-[10.5px] font-semibold uppercase tracking-wide text-text-muted">
          {query.trim() ? t('bubble.emoji.search') : tabLabel(tab)}
        </div>
        <div className="cb-emoji-grid">
          {list.length === 0 ? (
            <div className="col-span-full py-6 text-center text-[12.5px] text-text-muted">{t('bubble.emoji.noResults')}</div>
          ) : (
            list.map((e) => (
              <button
                key={e.char}
                type="button"
                title={e.name}
                aria-label={e.name}
                onClick={() => pick(e.char)}
                className="grid h-9 w-9 place-items-center rounded-md text-[21px] leading-none transition-colors hover:bg-fill-hover"
              >
                {e.char}
              </button>
            ))
          )}
        </div>
      </div>
    </>
  )
}

/** The composer's smiley button + its picker; `onPick` inserts at the cursor. */
export function EmojiButton({ onPick, disabled }: { onPick: (emoji: string) => void; disabled?: boolean }) {
  const t = useT()
  const [open, setOpen] = useState(false)
  return (
    <div className="relative shrink-0">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        disabled={disabled}
        title={t('bubble.emoji.open')}
        aria-label={t('bubble.emoji.open')}
        aria-expanded={open}
        data-tour="emoji-button"
        className="grid h-8 w-8 place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-35"
      >
        <Smile size={15} aria-hidden />
      </button>
      {open && <EmojiPicker onPick={onPick} onClose={() => setOpen(false)} />}
    </div>
  )
}

/**
 * Insert `text` into a textarea at its cursor (replacing a selection) through
 * the controlled value's setter, then put the cursor after it.
 */
export function insertAtCursor(el: HTMLTextAreaElement | null, value: string, text: string, set: (v: string) => void) {
  if (!el) {
    set(value + text)
    return
  }
  const start = el.selectionStart ?? value.length
  const end = el.selectionEnd ?? value.length
  set(value.slice(0, start) + text + value.slice(end))
  const caret = start + text.length
  window.requestAnimationFrame(() => {
    el.focus()
    el.setSelectionRange(caret, caret)
  })
}
