'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, Search, Square, SquareCheck } from 'lucide-react'
import { useEntity } from '@/lib/EntityContext'
import { entityTree } from '@/components/ui/EntityPicker'
import { useT } from '@/i18n'

/**
 * Top-bar ENTITY switcher: the working entity ▾ (or "N entities" / "All
 * entities"). Opens the tree of entities the signed-in user may see — main
 * entities with a MAIN badge, branches indented beneath them (the shared
 * `entityTree` order, same as every EntityPicker). Clicking a name shows only
 * that entity; its checkbox adds it to (or removes it from) the view, so
 * several can be seen together. A ticked entity includes its branches, so a
 * branch under it shows as included. A user with a single entity gets a static
 * chip.
 *
 * The TENANT is deliberately not shown: operators think in entities.
 */
export function EntitySwitcher({ block = false }: { block?: boolean }) {
  const t = useT()
  const { entities, activeEntity, selectedEntities, allEntities, setActiveEntity, toggleEntity, loading } = useEntity()
  const [open, setOpen] = useState(false)
  const [q, setQ] = useState('')
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    setQ('')
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const allLabel = t('ui.entityPicker.allEntities')
  const label = loading
    ? t('common.status.loading')
    : allEntities
      ? allLabel
      : activeEntity
        ? activeEntity.name
        : t('shell.entitySwitcher.selected', { count: selectedEntities.length })
  const ticked = useMemo(() => new Set(selectedEntities.map((e) => e.slug)), [selectedEntities])
  // the ticked ancestor that already covers a branch (its parent chain, nearest first)
  const coveredBy = useMemo(() => {
    const bySlug = new Map(entities.map((e) => [e.slug, e]))
    const out = new Map<string, string>()
    for (const e of entities) {
      let p = e.parent ? bySlug.get(e.parent) : undefined
      for (let d = 0; p && d < 32; d++) {
        if (ticked.has(p.slug)) {
          out.set(e.slug, p.name)
          break
        }
        p = p.parent ? bySlug.get(p.parent) : undefined
      }
    }
    return out
  }, [entities, ticked])
  const canSwitch = entities.length > 1
  const tree = useMemo(() => entityTree(entities), [entities])
  const shown = q.trim()
    ? tree.filter(({ entity }) => entity.name.toLowerCase().includes(q.trim().toLowerCase()))
    : tree

  const pick = (slug: string | null) => {
    setActiveEntity(slug)
    setOpen(false)
  }

  return (
    <div ref={ref} className={`relative ${block ? 'w-full' : ''}`}>
      <button
        onClick={() => canSwitch && setOpen((v) => !v)}
        disabled={!canSwitch}
        aria-haspopup="true"
        aria-expanded={open}
        title={canSwitch ? (selectedEntities.length > 1 ? selectedEntities.map((e) => e.name).join(', ') : t('shell.entitySwitcher.switch')) : label}
        className={`flex h-9 items-center gap-2 rounded-lg border border-line bg-card px-3 text-[13px] font-medium text-text-high transition-colors hover:border-line-strong disabled:cursor-default disabled:hover:border-line ${
          block ? 'w-full' : ''
        }`}
      >
        <span className={`truncate ${block ? 'flex-1 text-left' : 'max-w-[160px]'}`}>{label}</span>
        {canSwitch && (
          <ChevronDown
            size={14}
            strokeWidth={1.75}
            className={`shrink-0 text-text-muted transition-transform ${open ? 'rotate-180' : ''}`}
            aria-hidden
          />
        )}
      </button>

      {open && canSwitch && (
        <div
          className={`absolute z-50 mt-1.5 animate-slideDown overflow-hidden rounded-xl border border-line bg-card shadow-pop ${
            block ? 'left-0 right-0' : 'right-0 w-[300px]'
          }`}
        >
          {entities.length > 6 && (
            <label className="flex items-center gap-2 border-b border-line px-3 py-2">
              <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
              <input
                autoFocus
                value={q}
                onChange={(e) => setQ(e.target.value)}
                placeholder={t('shell.entitySwitcher.find')}
                aria-label={t('shell.entitySwitcher.find')}
                className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
              />
            </label>
          )}
          <div role="group" aria-label={t('shell.nav.entities')} className="max-h-[320px] overflow-y-auto p-1">
            {!q.trim() && (
              <>
                <button
                  type="button"
                  aria-pressed={allEntities}
                  onClick={() => pick(null)}
                  className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-[13px] text-text-high transition-colors hover:bg-fill-hover"
                >
                  <span className="min-w-0 flex-1 truncate font-semibold">{allLabel}</span>
                  {allEntities && <Check size={14} strokeWidth={2} className="shrink-0" aria-hidden />}
                </button>
                <div className="mx-2 my-1 h-px bg-line" />
              </>
            )}
            {shown.map(({ entity: e, depth }) => {
              const active = ticked.has(e.slug)
              const parentName = coveredBy.get(e.slug)
              const Box = active || parentName ? SquareCheck : Square
              return (
                <div
                  key={e.slug}
                  className="flex w-full items-center gap-1 rounded-lg pr-2 text-[13px] transition-colors hover:bg-fill-hover"
                  style={{ paddingLeft: 4 + depth * 16 }}
                >
                  <button
                    type="button"
                    onClick={() => toggleEntity(e.slug)}
                    disabled={Boolean(parentName)}
                    aria-label={t(active ? 'shell.entitySwitcher.untick' : 'shell.entitySwitcher.tick', { name: e.name })}
                    aria-pressed={active || Boolean(parentName)}
                    title={parentName ? t('shell.entitySwitcher.included', { parent: parentName }) : undefined}
                    className={`grid h-8 w-7 shrink-0 place-items-center rounded-md ${
                      parentName ? 'cursor-default text-text-faint' : active ? 'text-text-heading' : 'text-text-muted hover:text-text-high'
                    }`}
                  >
                    <Box size={15} strokeWidth={1.8} aria-hidden />
                  </button>
                  <button
                    type="button"
                    onClick={() => pick(e.slug)}
                    title={t('shell.entitySwitcher.onlyThis', { name: e.name })}
                    className="flex min-w-0 flex-1 items-center gap-1.5 py-2 text-left"
                  >
                    <span className={`min-w-0 flex-1 truncate ${depth > 0 ? 'text-text-mid' : 'font-semibold text-text-high'}`}>
                      {e.name}
                    </span>
                    {depth === 0 && (
                      <span className="shrink-0 rounded border border-line px-1 text-[9.5px] font-bold uppercase tracking-[0.06em] text-text-muted">
                        {t('ui.entityPicker.main')}
                      </span>
                    )}
                    {activeEntity?.slug === e.slug && <Check size={14} strokeWidth={2} className="shrink-0 text-text-high" aria-hidden />}
                  </button>
                </div>
              )
            })}
            {shown.length === 0 && <div className="px-3 py-3 text-[12.5px] text-text-muted">{t('shell.entitySwitcher.noMatch')}</div>}
          </div>
          <p className="border-t border-line px-3 py-2 text-[11.5px] leading-snug text-text-muted">{t('shell.entitySwitcher.hint')}</p>
        </div>
      )}
    </div>
  )
}
