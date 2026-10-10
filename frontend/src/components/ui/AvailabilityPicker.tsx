'use client'

/**
 * "Available to" for library items (quick replies, call scripts, knowledge,
 * AI skills, automation rules): ◉ Everyone (global) / ◯ Selected entities,
 * with a multi-tick entity tree under the second choice.
 *
 * The value is a list of entity slugs; `[]` means EVERYONE (organization-wide).
 * The server rule (ScopingService.LibraryVisible): each ticked entity AND its
 * branches can use the item; a sibling outside the ticks never sees it. Only
 * admins may pick Everyone (or narrow an organization-wide item) — for anyone
 * else that choice is disabled and the list may never become empty.
 *
 * Shown UP FRONT in every editor (never inside "Advanced settings"): who can
 * use a template is a first decision, not a tweak.
 */

import { useEffect, useMemo, useState } from 'react'
import { Globe2, Building2 } from 'lucide-react'
import { EntityMultiPicker } from './EntityPicker'
import { useEntity } from '@/lib/EntityContext'
import { useRole } from '@/lib/useRole'
import { useT } from '@/i18n'

/** An entity as library payloads name it (`entities: [{id, slug, name}]`). */
export interface EntityRefLite {
  id?: number
  slug: string
  name: string
}

/**
 * May the signed-in person make an item global? As on the server
 * (LibraryScope.MayGoGlobal): admins, and anyone NOT limited to particular
 * entities (no assigned entities = the tenant-wide view).
 */
export function useCanGlobal(): boolean {
  const { role } = useRole()
  const { entities } = useEntity()
  return role === 'admin' || role === 'superadmin' || entities.length === 0
}

/**
 * The default "Available to" for a NEW item: Everyone for an admin; for
 * anyone else their single ticked entity, else their first one.
 */
export function useDefaultAvailability(): string[] {
  const { role } = useRole()
  const { activeEntity, entities } = useEntity()
  const admin = role === 'admin' || role === 'superadmin'
  const slug = admin ? undefined : (activeEntity?.slug ?? entities[0]?.slug)
  // stable identity, so it can sit in an effect's dependency list
  return useMemo(() => (slug ? [slug] : []), [slug])
}

/** The slugs of a payload's `entities` (falls back to the legacy single `entity`). */
export function availabilityOf(item: { entities?: EntityRefLite[] | null; entity?: string | null }): string[] {
  if (Array.isArray(item.entities)) return item.entities.map((e) => e.slug)
  return item.entity ? [item.entity] : []
}

export function AvailabilityPicker({
  value,
  onChange,
  disabled,
  label,
  hint,
  className = '',
}: {
  /** Entity slugs; [] = everyone. */
  value: string[]
  onChange: (slugs: string[]) => void
  disabled?: boolean
  label?: string
  /** Extra line under the choice (e.g. what the item is). */
  hint?: string
  className?: string
}) {
  const t = useT()
  const canGlobal = useCanGlobal()
  const { activeEntity, entities } = useEntity()
  const isGlobal = value.length === 0
  // "Selected" with nothing ticked yet is a real (transient) state of the form
  const [mode, setMode] = useState<'global' | 'selected'>(isGlobal ? 'global' : 'selected')
  useEffect(() => {
    if (value.length > 0) setMode('selected')
  }, [value.length])
  const selected = mode === 'selected'

  const pickSelected = () => {
    setMode('selected')
    if (value.length === 0) {
      const seed = activeEntity?.slug ?? entities[0]?.slug
      if (seed) onChange([seed])
    }
  }
  const pickGlobal = () => {
    if (!canGlobal) return
    setMode('global')
    onChange([])
  }

  const radio = (on: boolean, off?: boolean) =>
    `flex flex-1 cursor-pointer items-start gap-2.5 rounded-lg border px-3 py-2.5 text-left transition-colors disabled:cursor-not-allowed disabled:opacity-55 ${
      on ? 'border-text-primary bg-fill-subtle' : 'border-line-strong bg-card hover:bg-fill-hover'
    } ${off ? 'opacity-55' : ''}`
  const dot = (on: boolean) =>
    `mt-0.5 grid h-4 w-4 shrink-0 place-items-center rounded-full border ${on ? 'border-text-primary' : 'border-line-strong'}`

  return (
    <div className={className} data-tour="availability">
      <span className="mb-1.5 block text-[12px] font-semibold text-text-high">{label ?? t('ui.availability.label')}</span>
      <div className="flex flex-col gap-2 sm:flex-row" role="radiogroup" aria-label={label ?? t('ui.availability.label')}>
        <button
          type="button"
          role="radio"
          aria-checked={!selected}
          disabled={disabled || !canGlobal}
          onClick={pickGlobal}
          title={!canGlobal ? t('ui.availability.adminOnly') : undefined}
          className={radio(!selected, !canGlobal)}
        >
          <span className={dot(!selected)}>{!selected && <span className="h-2 w-2 rounded-full bg-primary" />}</span>
          <span className="min-w-0">
            <span className="flex items-center gap-1.5 text-[12.5px] font-semibold text-text-heading">
              <Globe2 size={13} aria-hidden /> {t('ui.availability.everyone')}
            </span>
            <span className="mt-0.5 block text-[11.5px] leading-snug text-text-muted">
              {canGlobal ? t('ui.availability.everyoneHint') : t('ui.availability.adminOnly')}
            </span>
          </span>
        </button>
        <button
          type="button"
          role="radio"
          aria-checked={selected}
          disabled={disabled}
          onClick={pickSelected}
          className={radio(selected)}
        >
          <span className={dot(selected)}>{selected && <span className="h-2 w-2 rounded-full bg-primary" />}</span>
          <span className="min-w-0">
            <span className="flex items-center gap-1.5 text-[12.5px] font-semibold text-text-heading">
              <Building2 size={13} aria-hidden /> {t('ui.availability.selected')}
            </span>
            <span className="mt-0.5 block text-[11.5px] leading-snug text-text-muted">{t('ui.availability.selectedHint')}</span>
          </span>
        </button>
      </div>
      {selected && (
        <EntityMultiPicker
          className="mt-2"
          value={value}
          disabled={disabled}
          placeholder={t('ui.availability.placeholder')}
          ariaLabel={t('ui.availability.pickAria')}
          onChange={(slugs) => {
            // someone who may not go global keeps at least one tick
            if (slugs.length === 0 && !canGlobal) return
            if (slugs.length === 0) setMode('global')
            onChange(slugs)
          }}
        />
      )}
      {hint && <span className="mt-1 block text-[11.5px] text-text-muted">{hint}</span>}
    </div>
  )
}

/** List chips naming who can use an item: "Everyone", or entity names (2 shown, "+N"). */
export function AvailabilityChips({
  entities,
  global,
  max = 2,
}: {
  entities?: EntityRefLite[] | null
  /** Defaults to "no entities". */
  global?: boolean
  max?: number
}) {
  const t = useT()
  const list = entities ?? []
  const isGlobal = global ?? list.length === 0
  if (isGlobal)
    return (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-md bg-fill px-1.5 py-0.5 text-[11px] font-semibold text-text-muted">
        <Globe2 size={11} aria-hidden /> {t('ui.availability.chipEveryone')}
      </span>
    )
  const shown = list.slice(0, max)
  const rest = list.length - shown.length
  return (
    <span className="inline-flex flex-wrap items-center gap-1" title={list.map((e) => e.name || e.slug).join(', ')}>
      {shown.map((e) => (
        <span
          key={e.slug}
          className="whitespace-nowrap rounded-md bg-brand-500/15 px-1.5 py-0.5 text-[11px] font-semibold text-[var(--brand-ink)]"
        >
          {e.name || e.slug}
        </span>
      ))}
      {rest > 0 && (
        <span className="whitespace-nowrap rounded-md bg-fill px-1.5 py-0.5 text-[11px] font-semibold text-text-muted">
          {t('ui.availability.more', { count: rest })}
        </span>
      )}
    </span>
  )
}
