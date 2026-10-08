'use client'

/**
 * Bindings, done the way the Permissions page does capabilities — the ONE
 * pattern for "what is bound to this thing?" across the app:
 *
 *   Level 1  a grid of cards, one per group (an entity tree under a main
 *            entity, a team, a tag group, a channel…). Each card shows a
 *            None / k of N / All N status pill, a tri-state checkbox that
 *            binds or unbinds the whole group, and a Manage button.
 *   Level 2  Manage opens a modal listing the group's members, each with an
 *            on/off switch (searchable). Apply merges the draft back.
 *   Save     nothing is persisted until the page's Save bar — the parent
 *            owns `value` and decides what Save sends (usually a diff).
 *
 * Generic over plain string ids so callers bind users↔entities,
 * teams↔members, contacts↔entities, tags↔contacts, accounts↔entities… with
 * the same component.
 */

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Search, SlidersHorizontal } from 'lucide-react'
import { Button } from './Button'
import { Modal } from './Modal'
import { Toggle } from './Toggle'

export interface BindingItem {
  id: string
  label: string
  hint?: string
  /** Indent level inside the group (e.g. sub-entities). */
  depth?: number
  /** Cannot be changed (shown as locked-on when bound). */
  locked?: boolean
}

export interface BindingGroup {
  id: string
  label: string
  sub?: string
  badge?: string
  items: BindingItem[]
}

type Status = 'none' | 'partial' | 'full'

/** entity → entities, member → members */
const plural = (w: string) => (/[^aeiou]y$/.test(w) ? `${w.slice(0, -1)}ies` : `${w}s`)

const PILL: Record<Status, string> = {
  none: 'border border-line-strong text-text-mid',
  partial: 'bg-amber/15 text-[color:var(--amber-ink)]',
  full: 'bg-green/15 text-[color:var(--green-ink)]',
}

function summary(g: BindingGroup, value: Set<string>) {
  const k = g.items.filter((i) => value.has(i.id)).length
  const n = g.items.length
  const status: Status = k === 0 ? 'none' : k === n ? 'full' : 'partial'
  const label = status === 'none' ? 'None' : status === 'full' ? (n === 1 ? 'Bound' : `All ${n}`) : `${k} of ${n}`
  return { k, n, status, label }
}

export function BindingGrid({
  groups,
  value,
  onChange,
  disabled,
  searchPlaceholder = 'Search…',
  itemNoun = 'item',
  manageTitle,
  emptyText = 'Nothing to bind yet.',
  columns = 3,
}: {
  groups: BindingGroup[]
  value: Set<string>
  onChange: (next: Set<string>) => void
  disabled?: boolean
  searchPlaceholder?: string
  /** "entity", "member", "contact"… — used in counts and modal copy. */
  itemNoun?: string
  manageTitle?: (g: BindingGroup) => ReactNode
  emptyText?: ReactNode
  columns?: 2 | 3 | 4
}) {
  const [query, setQuery] = useState('')
  const [managing, setManaging] = useState<BindingGroup | null>(null)

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return groups
    return groups.filter(
      (g) =>
        g.label.toLowerCase().includes(q) ||
        (g.sub ?? '').toLowerCase().includes(q) ||
        g.items.some((i) => i.label.toLowerCase().includes(q)),
    )
  }, [groups, query])

  const setGroup = (g: BindingGroup, on: boolean) => {
    const next = new Set(value)
    for (const i of g.items) {
      if (i.locked) continue
      if (on) next.add(i.id)
      else next.delete(i.id)
    }
    onChange(next)
  }

  const allOn = groups.length > 0 && groups.every((g) => summary(g, value).status === 'full')
  const colCls = columns === 2 ? 'sm:grid-cols-2' : columns === 4 ? 'sm:grid-cols-2 xl:grid-cols-4' : 'sm:grid-cols-2 xl:grid-cols-3'

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <label className="flex h-9 min-w-[200px] flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
          <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={searchPlaceholder}
            aria-label={searchPlaceholder}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none"
          />
        </label>
        <Button
          variant="secondary"
          size="sm"
          disabled={disabled || groups.length === 0}
          onClick={() => {
            const next = new Set(value)
            for (const g of groups) for (const i of g.items) if (!i.locked) (allOn ? next.delete(i.id) : next.add(i.id))
            onChange(next)
          }}
        >
          {allOn ? 'Clear all' : 'Check all'}
        </Button>
      </div>

      {shown.length === 0 ? (
        <div className="rounded-xl border border-dashed border-line-strong px-4 py-8 text-center text-[13px] text-text-muted">
          {query ? `No matches for “${query}”.` : emptyText}
        </div>
      ) : (
        <div className={`grid grid-cols-1 gap-3 ${colCls}`}>
          {shown.map((g) => (
            <GroupCard
              key={g.id}
              group={g}
              value={value}
              disabled={disabled}
              itemNoun={itemNoun}
              onToggleAll={(on) => setGroup(g, on)}
              onManage={() => setManaging(g)}
            />
          ))}
        </div>
      )}

      <ManageModal
        group={managing}
        value={value}
        itemNoun={itemNoun}
        disabled={disabled}
        title={managing ? manageTitle?.(managing) ?? managing.label : ''}
        onClose={() => setManaging(null)}
        onApply={(ids, on) => {
          const next = new Set(value)
          for (const id of ids) (on.has(id) ? next.add(id) : next.delete(id))
          onChange(next)
        }}
      />
    </div>
  )
}

function GroupCard({
  group,
  value,
  disabled,
  itemNoun,
  onToggleAll,
  onManage,
}: {
  group: BindingGroup
  value: Set<string>
  disabled?: boolean
  itemNoun: string
  onToggleAll: (on: boolean) => void
  onManage: () => void
}) {
  const sum = summary(group, value)
  const boxRef = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (boxRef.current) boxRef.current.indeterminate = sum.status === 'partial'
  }, [sum.status])

  return (
    <div className="flex flex-col gap-2.5 rounded-xl border border-line bg-card p-3.5">
      <div className="flex items-start gap-2.5">
        <input
          ref={boxRef}
          type="checkbox"
          checked={sum.status === 'full'}
          disabled={disabled || group.items.length === 0}
          onChange={(e) => onToggleAll(e.target.checked)}
          aria-label={`Bind all of ${group.label}`}
          className="mt-[3px] h-4 w-4 shrink-0 cursor-pointer accent-[color:var(--primary)]"
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1.5">
            {group.badge && (
              <span className="shrink-0 rounded bg-primary px-1 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] text-on-primary">
                {group.badge}
              </span>
            )}
            <span className="truncate text-[14px] font-bold text-text-heading">{group.label}</span>
          </div>
          <div className="mt-0.5 truncate text-[12px] text-text-muted">
            {group.sub ?? `${sum.n} ${sum.n === 1 ? itemNoun : plural(itemNoun)}`}
          </div>
        </div>
        <span className={`shrink-0 rounded-full px-2.5 py-0.5 text-[11.5px] font-semibold ${PILL[sum.status]}`}>{sum.label}</span>
      </div>
      {group.items.length > 1 && (
        <Button variant="secondary" size="sm" className="self-start" onClick={onManage}>
          <SlidersHorizontal size={13} aria-hidden /> Manage
        </Button>
      )}
    </div>
  )
}

function ManageModal({
  group,
  value,
  itemNoun,
  disabled,
  title,
  onClose,
  onApply,
}: {
  group: BindingGroup | null
  value: Set<string>
  itemNoun: string
  disabled?: boolean
  title: ReactNode
  onClose: () => void
  onApply: (ids: string[], on: Set<string>) => void
}) {
  const [draft, setDraft] = useState<Set<string>>(new Set())
  const [query, setQuery] = useState('')

  useEffect(() => {
    if (!group) return
    setDraft(new Set(group.items.filter((i) => value.has(i.id)).map((i) => i.id)))
    setQuery('')
  }, [group, value])

  if (!group) return null
  const q = query.trim().toLowerCase()
  const items = q ? group.items.filter((i) => `${i.label} ${i.hint ?? ''}`.toLowerCase().includes(q)) : group.items

  return (
    <Modal
      open={!!group}
      onClose={onClose}
      title={title}
      subtitle={`Switch each ${itemNoun} on or off, then apply. Nothing is saved until you press Save on the page.`}
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            disabled={disabled}
            onClick={() => {
              onApply(group.items.map((i) => i.id), draft)
              onClose()
            }}
          >
            Apply
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-2">
        {group.items.length > 8 && (
          <label className="flex h-9 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
            <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={`Search ${plural(itemNoun)}…`}
              aria-label={`Search ${plural(itemNoun)}`}
              className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none"
            />
          </label>
        )}
        <div className="flex items-center justify-between text-[12px] text-text-muted">
          <span>
            {draft.size} of {group.items.length} on
          </span>
          <span className="flex gap-3">
            <button type="button" className="font-semibold hover:text-text-high" onClick={() => setDraft(new Set(group.items.map((i) => i.id)))}>
              All on
            </button>
            <button
              type="button"
              className="font-semibold hover:text-text-high"
              onClick={() => setDraft(new Set(group.items.filter((i) => i.locked && draft.has(i.id)).map((i) => i.id)))}
            >
              All off
            </button>
          </span>
        </div>
        {items.map((i) => {
          const on = draft.has(i.id)
          return (
            <div
              key={i.id}
              className="flex items-center justify-between gap-3 rounded-lg border border-line bg-card px-3 py-2.5"
              style={{ marginLeft: (i.depth ?? 0) * 16 }}
            >
              <div className="min-w-0">
                <div className="truncate text-[13px] font-semibold text-text-high">
                  {(i.depth ?? 0) > 0 && <span className="mr-1 text-text-faint">└</span>}
                  {i.label}
                </div>
                {i.hint && <div className="truncate text-[11.5px] text-text-muted">{i.hint}</div>}
              </div>
              <Toggle
                on={on}
                locked={i.locked && on}
                disabled={disabled}
                label={`${i.label}`}
                onChange={(v) =>
                  setDraft((d) => {
                    const n = new Set(d)
                    if (v) n.add(i.id)
                    else n.delete(i.id)
                    return n
                  })
                }
              />
            </div>
          )
        })}
      </div>
    </Modal>
  )
}

/** Sticky Save / Discard bar for pages built on BindingGrid. */
export function SaveBar({
  dirty,
  saving,
  onSave,
  onDiscard,
  summary: text,
}: {
  dirty: boolean
  saving?: boolean
  onSave: () => void
  onDiscard: () => void
  summary?: ReactNode
}) {
  if (!dirty) return null
  return (
    <div className="sticky bottom-0 z-10 -mx-1 flex flex-wrap items-center gap-2 rounded-xl border border-line-strong bg-card px-3.5 py-2.5 shadow-pop">
      <span className="text-[12.5px] font-semibold text-[color:var(--amber-ink)]">Unsaved changes</span>
      {text && <span className="text-[12.5px] text-text-muted">{text}</span>}
      <span className="ml-auto flex gap-2">
        <Button variant="ghost" size="sm" onClick={onDiscard} disabled={saving}>
          Discard
        </Button>
        <Button variant="primary" size="sm" onClick={onSave} disabled={saving}>
          {saving ? 'Saving…' : 'Save changes'}
        </Button>
      </span>
    </div>
  )
}
