'use client'

/**
 * The one control for picking entities, everywhere one is picked (teams,
 * channels, knowledge, widgets, automations, contacts, users…).
 *
 * Entities are a tree (a main/top-level entity with branches under it), so the
 * list is ordered depth-first: every main entity carries a MAIN badge and its
 * branches sit indented beneath it — the "which one is the sub-entity?"
 * confusion the old flat <select> caused.
 *
 * Single mode binds a resource to one entity or leaves it organization-wide
 * (the empty value is a real, labelled choice — see crm scoping rules: an
 * unbound resource is the fallback every entity inherits). Multi mode picks a
 * set of entities.
 */

import { useMemo } from 'react'
import { useEntity, type Entity } from '@/lib/EntityContext'
import { MultiSelect, Select, type SelectOption } from './Select'
import { useT, type TFunction } from '@/i18n'

/** Depth-first order with each entity's depth, parents before their children. */
export function entityTree(entities: Entity[]): { entity: Entity; depth: number }[] {
  const bySlug = new Map(entities.map((e) => [e.slug, e]))
  const kids = new Map<string, Entity[]>()
  const roots: Entity[] = []
  for (const e of entities) {
    if (e.parent && bySlug.has(e.parent)) {
      const arr = kids.get(e.parent) ?? []
      arr.push(e)
      kids.set(e.parent, arr)
    } else roots.push(e)
  }
  const byName = (a: Entity, b: Entity) => a.name.localeCompare(b.name)
  const out: { entity: Entity; depth: number }[] = []
  const seen = new Set<string>()
  const walk = (e: Entity, depth: number) => {
    if (seen.has(e.slug) || depth > 8) return
    seen.add(e.slug)
    out.push({ entity: e, depth })
    for (const c of (kids.get(e.slug) ?? []).sort(byName)) walk(c, depth + 1)
  }
  roots.sort(byName).forEach((r) => walk(r, 0))
  return out
}

/** Select options for the entity tree; `t` translates the top-level badge ("Main"). */
export function entityOptions(entities: Entity[], t: TFunction): SelectOption[] {
  const main = t('ui.entityPicker.main')
  return entityTree(entities).map(({ entity, depth }) => ({
    value: entity.slug,
    label: entity.name,
    depth,
    badge: depth === 0 ? main : undefined,
  }))
}

export function EntityPicker({
  value,
  onChange,
  disabled,
  globalLabel: globalLabelProp,
  allowGlobal = true,
  size = 'md',
  className = '',
  ariaLabel,
  entities: override,
}: {
  /** Entity slug, or null for organization-wide. */
  value: string | null
  onChange: (slug: string | null) => void
  disabled?: boolean
  globalLabel?: string
  /** false = an entity must be chosen (no organization-wide option). */
  allowGlobal?: boolean
  size?: 'sm' | 'md'
  className?: string
  ariaLabel?: string
  /** Pick from this list instead of the signed-in user's entities. */
  entities?: Entity[]
}) {
  const t = useT()
  const globalLabel = globalLabelProp ?? t('ui.entityPicker.orgWide')
  const { entities: mine } = useEntity()
  const entities = override ?? mine
  const options = useMemo(() => {
    const tree = entityOptions(entities, t)
    return allowGlobal ? [{ value: '', label: globalLabel }, ...tree] : tree
  }, [entities, allowGlobal, globalLabel, t])
  return (
    <Select
      value={value ?? ''}
      onChange={(v) => onChange(v || null)}
      options={options}
      disabled={disabled}
      size={size}
      className={className}
      ariaLabel={ariaLabel ?? t('common.fields.entity')}
      placeholder={allowGlobal ? globalLabel : t('ui.entityPicker.choose')}
      searchable={entities.length > 6}
    />
  )
}

export function EntityMultiPicker({
  value,
  onChange,
  disabled,
  placeholder,
  size = 'md',
  className = '',
  ariaLabel,
  entities: override,
}: {
  value: string[]
  onChange: (slugs: string[]) => void
  disabled?: boolean
  placeholder?: string
  size?: 'sm' | 'md'
  className?: string
  ariaLabel?: string
  entities?: Entity[]
}) {
  const t = useT()
  const { entities: mine } = useEntity()
  const entities = override ?? mine
  const options = useMemo(() => entityOptions(entities, t), [entities, t])
  return (
    <MultiSelect
      value={value}
      onChange={onChange}
      options={options}
      disabled={disabled}
      size={size}
      className={className}
      ariaLabel={ariaLabel ?? t('shell.nav.entities')}
      placeholder={placeholder ?? t('ui.entityPicker.allEntities')}
      searchable={entities.length > 6}
    />
  )
}
