'use client'

import { useEffect, useRef, useState } from 'react'
import { Check, ChevronDown, CornerDownRight } from 'lucide-react'
import { useEntity, type Entity } from '@/lib/EntityContext'

/**
 * Topbar ENTITY switcher chip: logo square + current entity name + chevron.
 * Opens the tree of entities the signed-in user may see (from EntityContext ->
 * /api/v1/entities ∩ /api/v1/whoami), parents with their children indented
 * beneath. Picking one sets the active entity that entity-scoped screens filter
 * by. A user with a single entity gets a static chip (no menu).
 *
 * The TENANT is deliberately not shown anywhere here. Operators think in
 * entities — the tenant is an internal grouping they never pick, so surfacing
 * it ("DEMO") was noise. It still exists server-side.
 */
/**
 * Parents first, each followed by its children (depth-first), so the menu shows
 * the tree rather than a flat alphabetical list. An entity whose parent is not
 * visible to this user is treated as a root — it must never disappear.
 */
function orderByHierarchy(entities: Entity[]): { entity: Entity; depth: number }[] {
  const visible = new Set(entities.map((e) => e.slug))
  const childrenOf = new Map<string | null, Entity[]>()
  for (const e of entities) {
    const parent = e.parent && visible.has(e.parent) ? e.parent : null
    childrenOf.set(parent, [...(childrenOf.get(parent) ?? []), e])
  }
  for (const list of childrenOf.values()) list.sort((a, b) => a.name.localeCompare(b.name))
  const out: { entity: Entity; depth: number }[] = []
  const walk = (parent: string | null, depth: number) => {
    for (const entity of childrenOf.get(parent) ?? []) {
      if (out.some((o) => o.entity.slug === entity.slug)) continue // cyclic data guard
      out.push({ entity, depth })
      walk(entity.slug, depth + 1)
    }
  }
  walk(null, 0)
  for (const e of entities) if (!out.some((o) => o.entity.slug === e.slug)) out.push({ entity: e, depth: 0 })
  return out
}

export function EntitySwitcher() {
  const { entities, activeEntity, allEntities, setActiveEntity, loading } = useEntity()
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [open])

  const label = loading
    ? 'Loading…'
    : allEntities
      ? 'All entities'
      : (activeEntity?.name ?? 'All entities')
  const initials = (activeEntity?.name ?? 'CB').slice(0, 2).toUpperCase()
  const canSwitch = entities.length > 1
  const ordered = orderByHierarchy(entities)

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => canSwitch && setOpen((v) => !v)}
        disabled={!canSwitch}
        title={canSwitch ? 'Switch entity' : label}
        className="flex h-[34px] items-center gap-2 rounded-xl border border-line bg-fill px-2.5 text-text-high transition-colors hover:bg-fill-hover disabled:cursor-default disabled:hover:bg-fill"
      >
        <span
          className="grid h-[22px] w-[22px] shrink-0 place-items-center rounded-lg text-[9px] font-extrabold text-white"
          style={{ background: 'linear-gradient(135deg,#3B82F6,#2563EB)' }}
        >
          {initials}
        </span>
        <span className="max-w-[130px] truncate text-[12.5px] font-semibold">{label}</span>
        {canSwitch && <ChevronDown size={14} strokeWidth={2} className="shrink-0 text-text-muted" />}
      </button>

      {open && canSwitch && (
        <div
          className="absolute right-0 z-50 mt-1.5 w-[220px] animate-slideDown overflow-hidden rounded-xl border border-line bg-card p-1 shadow-2xl"
          role="menu"
        >
          <button
            role="menuitemradio"
            aria-checked={allEntities}
            onClick={() => {
              setActiveEntity(null)
              setOpen(false)
            }}
            className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-[12.5px] text-text-high transition-colors hover:bg-fill-hover"
          >
            <span className="min-w-0 flex-1 truncate font-semibold">All entities</span>
            {allEntities && <Check size={14} strokeWidth={2.25} className="shrink-0 text-brand-400" />}
          </button>
          <div className="my-1 h-px bg-line" />
          {ordered.map(({ entity: b, depth }) => {
            const active = b.slug === activeEntity?.slug
            return (
              <button
                key={b.slug}
                role="menuitemradio"
                aria-checked={active}
                onClick={() => {
                  setActiveEntity(b.slug)
                  setOpen(false)
                }}
                className="flex w-full items-center gap-1.5 rounded-lg py-2 pr-2.5 text-left text-[12.5px] text-text-high transition-colors hover:bg-fill-hover"
                style={{ paddingLeft: 10 + depth * 14 }}
              >
                {/* A child reads as a child: indented, with a branch mark and
                    lighter weight than the top-level entity it hangs under. */}
                {depth > 0 && (
                  <CornerDownRight size={11} strokeWidth={2} className="shrink-0 text-text-faint" aria-hidden />
                )}
                <span
                  className={`min-w-0 flex-1 truncate ${
                    depth > 0 ? 'font-normal text-text-mid' : 'font-bold'
                  }`}
                >
                  {b.name}
                </span>
                {active && <Check size={14} strokeWidth={2.25} className="shrink-0 text-brand-400" />}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}
