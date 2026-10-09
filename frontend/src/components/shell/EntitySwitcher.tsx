'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, Search } from 'lucide-react'
import { useEntity } from '@/lib/EntityContext'
import { entityTree } from '@/components/ui/EntityPicker'
import { useT } from '@/i18n'

/**
 * Top-bar ENTITY switcher: "All entities ▾" (or the working entity). Opens the
 * tree of entities the signed-in user may see — main entities with a MAIN
 * badge, branches indented beneath them (the shared `entityTree` order, same as
 * every EntityPicker). Picking one scopes every entity-aware screen; a user
 * with a single entity gets a static chip.
 *
 * The TENANT is deliberately not shown: operators think in entities.
 */
export function EntitySwitcher({ block = false }: { block?: boolean }) {
  const t = useT()
  const { entities, activeEntity, allEntities, setActiveEntity, loading } = useEntity()
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
  const label = loading ? t('common.status.loading') : allEntities ? allLabel : (activeEntity?.name ?? allLabel)
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
        aria-haspopup="listbox"
        aria-expanded={open}
        title={canSwitch ? t('shell.entitySwitcher.switch') : label}
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
            block ? 'left-0 right-0' : 'right-0 w-[260px]'
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
          <div role="listbox" aria-label={t('shell.nav.entities')} className="max-h-[320px] overflow-y-auto p-1">
            {!q.trim() && (
              <>
                <button
                  role="option"
                  aria-selected={allEntities}
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
              const active = e.slug === activeEntity?.slug
              return (
                <button
                  key={e.slug}
                  role="option"
                  aria-selected={active}
                  onClick={() => pick(e.slug)}
                  className="flex w-full items-center gap-1.5 rounded-lg py-2 pr-2.5 text-left text-[13px] transition-colors hover:bg-fill-hover"
                  style={{ paddingLeft: 10 + depth * 16 }}
                >
                  {depth > 0 && (
                    <span className="shrink-0 text-text-faint" aria-hidden>
                      └
                    </span>
                  )}
                  <span className={`min-w-0 flex-1 truncate ${depth > 0 ? 'text-text-mid' : 'font-semibold text-text-high'}`}>
                    {e.name}
                  </span>
                  {depth === 0 && (
                    <span className="shrink-0 rounded border border-line px-1 text-[9.5px] font-bold uppercase tracking-[0.06em] text-text-muted">
                      {t('ui.entityPicker.main')}
                    </span>
                  )}
                  {active && <Check size={14} strokeWidth={2} className="shrink-0 text-text-high" aria-hidden />}
                </button>
              )
            })}
            {shown.length === 0 && <div className="px-3 py-3 text-[12.5px] text-text-muted">{t('shell.entitySwitcher.noMatch')}</div>}
          </div>
        </div>
      )}
    </div>
  )
}
