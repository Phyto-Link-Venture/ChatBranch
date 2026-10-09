'use client'

import { useMemo } from 'react'
import { BindingGrid } from '@/components/ui/BindingGrid'
import { useT } from '@/i18n'
import { entityTreeGroups, type TreeEntity } from './groups'

/**
 * Entity access as cards: one card per MAIN entity holding it and its
 * sub-entities (indented), the Permissions-page pattern. Ids in, ids out — the
 * caller owns saving.
 */
export function EntityAccessGrid({
  entities,
  value,
  onChange,
  disabled,
  emptyText,
}: {
  entities: TreeEntity[]
  value: number[]
  onChange: (ids: number[]) => void
  disabled?: boolean
  emptyText?: string
}) {
  const t = useT()
  const groups = useMemo(() => entityTreeGroups(t, entities), [t, entities])
  const set = useMemo(() => new Set(value.map(String)), [value])
  return (
    <BindingGrid
      groups={groups}
      value={set}
      onChange={(next) => onChange([...next].map(Number))}
      disabled={disabled}
      itemNoun="entity"
      searchPlaceholder={t('admin.bindings.entityGrid.search')}
      columns={2}
      emptyText={emptyText ?? t('admin.bindings.entityGrid.empty')}
    />
  )
}
