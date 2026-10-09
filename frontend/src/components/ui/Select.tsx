'use client'

import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Check, ChevronDown, Search, X } from 'lucide-react'

export interface SelectOption {
  value: string
  label: string
  /** Muted suffix, e.g. a role or phone number. */
  hint?: string
  /** Options sharing a group render under one heading (in first-seen order). */
  group?: string
  /** Tree depth: 0 = top level; children indent with a branch marker. */
  depth?: number
  /** Small leading badge, e.g. "MAIN" for a top-level entity. */
  badge?: string
  disabled?: boolean
}

/**
 * The one dropdown used across the app — replaces native <select>, whose popup
 * is OS-drawn and ignores the theme. Features:
 *  - search box (automatic past 8 options, or `searchable`)
 *  - full keyboard: ↑/↓/Home/End move, Enter picks, Esc closes, typing searches
 *  - grouped options and tree indentation (entities: main vs sub)
 *  - single (`Select`) and multi (`MultiSelect`) flavours sharing one list
 *  - the list is portalled with fixed positioning, so a modal body or a
 *    scrolling card never clips it, and it flips upward near the bottom edge
 */
export function Select({
  value,
  onChange,
  options,
  placeholder = 'Select…',
  disabled = false,
  size = 'sm',
  ariaLabel,
  className = '',
  searchable,
  clearable = false,
}: {
  value: string
  onChange: (value: string) => void
  options: SelectOption[]
  placeholder?: string
  disabled?: boolean
  /** sm = 32px (dense chrome), md = 38px (forms). */
  size?: 'sm' | 'md'
  ariaLabel?: string
  className?: string
  searchable?: boolean
  /** Show an × that resets to '' (only when '' is not itself an option). */
  clearable?: boolean
}) {
  const selected = options.find((o) => o.value === value)
  return (
    <Dropdown
      options={options}
      isSelected={(v) => v === value}
      onPick={(v) => onChange(v)}
      closeOnPick
      disabled={disabled}
      size={size}
      ariaLabel={ariaLabel}
      className={className}
      searchable={searchable}
      onClear={clearable && value && !options.some((o) => o.value === '') ? () => onChange('') : undefined}
      trigger={
        selected ? (
          <span className="truncate font-medium text-text-high">
            {selected.label}
            {selected.hint && <span className="ml-1 font-normal text-text-muted">{selected.hint}</span>}
          </span>
        ) : (
          <span className="truncate text-text-faint">{placeholder}</span>
        )
      }
    />
  )
}

/** Multi-pick flavour: checkboxes in the list, chips (or a count) in the trigger. */
export function MultiSelect({
  value,
  onChange,
  options,
  placeholder = 'Any',
  disabled = false,
  size = 'sm',
  ariaLabel,
  className = '',
  searchable,
  maxChips = 2,
}: {
  value: string[]
  onChange: (value: string[]) => void
  options: SelectOption[]
  placeholder?: string
  disabled?: boolean
  size?: 'sm' | 'md'
  ariaLabel?: string
  className?: string
  searchable?: boolean
  maxChips?: number
}) {
  const set = useMemo(() => new Set(value), [value])
  const picked = options.filter((o) => set.has(o.value))
  return (
    <Dropdown
      multi
      options={options}
      isSelected={(v) => set.has(v)}
      onPick={(v) => onChange(set.has(v) ? value.filter((x) => x !== v) : [...value, v])}
      disabled={disabled}
      size={size}
      ariaLabel={ariaLabel}
      className={className}
      searchable={searchable}
      onClear={value.length ? () => onChange([]) : undefined}
      footer={(visible) => (
        <div className="flex items-center justify-between gap-2 border-t border-line px-2.5 py-1.5 text-[11.5px]">
          <button
            type="button"
            className="font-semibold text-text-muted hover:text-text-high"
            onClick={() => onChange(Array.from(new Set([...value, ...visible.filter((o) => !o.disabled).map((o) => o.value)])))}
          >
            Select shown
          </button>
          <button type="button" className="font-semibold text-text-muted hover:text-text-high" onClick={() => onChange([])}>
            Clear
          </button>
        </div>
      )}
      trigger={
        picked.length === 0 ? (
          <span className="truncate text-text-faint">{placeholder}</span>
        ) : (
          <span className="flex min-w-0 items-center gap-1 overflow-hidden">
            {picked.slice(0, maxChips).map((o) => (
              <span key={o.value} className="truncate rounded-md bg-fill-hover px-1.5 py-px text-[11.5px] font-medium text-text-high">
                {o.label}
              </span>
            ))}
            {picked.length > maxChips && (
              <span className="shrink-0 text-[11.5px] font-semibold text-text-muted">+{picked.length - maxChips}</span>
            )}
          </span>
        )
      }
    />
  )
}

// ── shared engine ────────────────────────────────────────────────────────────

function Dropdown({
  options,
  isSelected,
  onPick,
  closeOnPick = false,
  multi = false,
  disabled,
  size,
  ariaLabel,
  className,
  searchable,
  trigger,
  onClear,
  footer,
}: {
  options: SelectOption[]
  isSelected: (v: string) => boolean
  onPick: (v: string) => void
  closeOnPick?: boolean
  multi?: boolean
  disabled: boolean
  size: 'sm' | 'md'
  ariaLabel?: string
  className: string
  searchable?: boolean
  trigger: ReactNode
  onClear?: () => void
  footer?: (visible: SelectOption[]) => ReactNode
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [pos, setPos] = useState<{ left: number; width: number; top?: number; bottom?: number; maxH: number } | null>(null)
  const btnRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const listId = useId()
  const showSearch = searchable ?? options.length > 8

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return options
    return options.filter((o) => `${o.label} ${o.hint ?? ''} ${o.group ?? ''}`.toLowerCase().includes(q))
  }, [options, query])

  const place = useCallback(() => {
    const r = btnRef.current?.getBoundingClientRect()
    if (!r) return
    const below = window.innerHeight - r.bottom - 8
    const above = r.top - 8
    const width = Math.max(r.width, 220)
    const left = Math.min(r.left, window.innerWidth - width - 8)
    if (below < 240 && above > below) setPos({ left, width, bottom: window.innerHeight - r.top + 4, maxH: Math.min(320, above) })
    else setPos({ left, width, top: r.bottom + 4, maxH: Math.min(320, below) })
  }, [])

  useLayoutEffect(() => {
    if (!open) return
    place()
    const onMove = () => place()
    window.addEventListener('resize', onMove)
    window.addEventListener('scroll', onMove, true)
    return () => {
      window.removeEventListener('resize', onMove)
      window.removeEventListener('scroll', onMove, true)
    }
  }, [open, place])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      const t = e.target as Node
      if (!btnRef.current?.contains(t) && !listRef.current?.contains(t)) setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  // Opening: reset the search and start on the current pick.
  useEffect(() => {
    if (!open) return
    setQuery('')
    const idx = options.findIndex((o) => isSelected(o.value))
    setActive(idx >= 0 ? idx : 0)
    if (showSearch) window.setTimeout(() => searchRef.current?.focus(), 0)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  // Keep the highlighted row in view.
  useEffect(() => {
    if (!open) return
    listRef.current?.querySelector<HTMLElement>(`[data-idx="${active}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [active, open])

  const move = (delta: number) => {
    if (visible.length === 0) return
    let i = active
    for (let n = 0; n < visible.length; n++) {
      i = (i + delta + visible.length) % visible.length
      if (!visible[i].disabled) break
    }
    setActive(i)
  }

  const pick = (o: SelectOption | undefined) => {
    if (!o || o.disabled) return
    onPick(o.value)
    if (closeOnPick) {
      setOpen(false)
      btnRef.current?.focus()
    }
  }

  const onKey = (e: React.KeyboardEvent) => {
    if (!open) {
      if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) {
        e.preventDefault()
        setOpen(true)
      }
      return
    }
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1) }
    else if (e.key === 'Home') { e.preventDefault(); setActive(0) }
    else if (e.key === 'End') { e.preventDefault(); setActive(Math.max(0, visible.length - 1)) }
    else if (e.key === 'Enter') { e.preventDefault(); pick(visible[active]) }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setOpen(false); btnRef.current?.focus() }
    else if (e.key === 'Tab') setOpen(false)
    else if (!showSearch && e.key.length === 1) {
      // type-ahead without a search box: jump to the next label starting with the key
      const k = e.key.toLowerCase()
      const from = (active + 1) % Math.max(1, visible.length)
      const order = [...visible.slice(from), ...visible.slice(0, from)]
      const hit = order.find((o) => o.label.toLowerCase().startsWith(k))
      if (hit) setActive(visible.indexOf(hit))
    }
  }

  const sizeCls = size === 'sm' ? 'h-8 text-[12.5px]' : 'h-[38px] text-[13px]'
  let lastGroup: string | undefined

  return (
    <div className={`relative ${className}`}>
      <button
        ref={btnRef}
        type="button"
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onKey}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-label={ariaLabel}
        className={`flex w-full cursor-pointer items-center justify-between gap-2 rounded-lg border bg-card px-2.5 text-left outline-none transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${sizeCls} ${
          open ? 'border-text-primary' : 'border-line-strong hover:border-text-muted'
        }`}
      >
        <span className="flex min-w-0 flex-1 items-center">{trigger}</span>
        {onClear && !disabled && (
          <span
            role="button"
            tabIndex={-1}
            aria-label="Clear"
            onClick={(e) => {
              e.stopPropagation()
              onClear()
            }}
            className="grid h-4 w-4 shrink-0 place-items-center rounded text-text-muted hover:bg-fill-hover hover:text-text-high"
          >
            <X size={11} aria-hidden />
          </span>
        )}
        <ChevronDown size={14} className={`shrink-0 text-text-muted transition-transform ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>

      {open && pos && typeof document !== 'undefined' &&
        createPortal(
          <div
            ref={listRef}
            onKeyDown={onKey}
            className="fixed z-[80] flex flex-col overflow-hidden rounded-xl border border-line-strong bg-card shadow-pop"
            style={{ left: pos.left, width: pos.width, top: pos.top, bottom: pos.bottom, maxHeight: pos.maxH }}
          >
            {showSearch && (
              <label className="flex shrink-0 items-center gap-2 border-b border-line px-2.5 py-2">
                <Search size={13} className="shrink-0 text-text-muted" aria-hidden />
                <input
                  ref={searchRef}
                  value={query}
                  onChange={(e) => {
                    setQuery(e.target.value)
                    setActive(0)
                  }}
                  placeholder="Search…"
                  aria-label="Search options"
                  className="min-w-0 flex-1 bg-transparent text-[12.5px] text-text-primary outline-none"
                />
              </label>
            )}
            <div id={listId} role="listbox" aria-multiselectable={multi || undefined} className="min-h-0 flex-1 overflow-y-auto p-1">
              {visible.length === 0 && <div className="px-2.5 py-2 text-[12px] text-text-muted">No matches</div>}
              {visible.map((o, i) => {
                const sel = isSelected(o.value)
                const header = o.group && o.group !== lastGroup ? o.group : null
                lastGroup = o.group
                const depth = o.depth ?? 0
                return (
                  <div key={`${o.group ?? ''}:${o.value}`}>
                    {header && (
                      <div className="px-2 pb-1 pt-2 text-[10.5px] font-bold uppercase tracking-[0.08em] text-text-muted">{header}</div>
                    )}
                    <div
                      role="option"
                      aria-selected={sel}
                      aria-disabled={o.disabled || undefined}
                      data-idx={i}
                      onMouseEnter={() => setActive(i)}
                      onMouseDown={(e) => e.preventDefault()}
                      onClick={() => pick(o)}
                      className={`flex cursor-pointer items-center gap-2 rounded-md py-1.5 pr-2 text-[12.5px] ${
                        i === active ? 'bg-fill-hover' : ''
                      } ${o.disabled ? 'cursor-not-allowed opacity-40' : ''} ${sel ? 'font-semibold text-text-heading' : 'text-text-mid'}`}
                      style={{ paddingLeft: 8 + depth * 16 }}
                    >
                      {multi && (
                        <span
                          className={`grid h-4 w-4 shrink-0 place-items-center rounded border ${
                            sel ? 'border-primary bg-primary text-on-primary' : 'border-line-strong'
                          }`}
                          aria-hidden
                        >
                          {sel && <Check size={11} strokeWidth={3} />}
                        </span>
                      )}
                      {depth > 0 && <span className="shrink-0 text-text-faint" aria-hidden>└</span>}
                      {o.badge && (
                        <span className="shrink-0 rounded bg-primary px-1 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] text-on-primary">
                          {o.badge}
                        </span>
                      )}
                      <span className="min-w-0 flex-1 truncate">
                        {o.label}
                        {o.hint && <span className="ml-1.5 font-normal text-text-muted">{o.hint}</span>}
                      </span>
                      {!multi && sel && <Check size={13} className="shrink-0 text-brand-300" aria-hidden />}
                    </div>
                  </div>
                )
              })}
            </div>
            {footer?.(visible)}
          </div>,
          document.body,
        )}
    </div>
  )
}
