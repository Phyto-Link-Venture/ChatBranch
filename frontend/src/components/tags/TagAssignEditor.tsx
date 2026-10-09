'use client'

import { useMemo, useState } from 'react'
import { ChevronDown, ChevronUp, Info } from 'lucide-react'
import { Select, type SelectOption } from '@/components/ui/Select'
import { formatTagValue, type TagDef } from '@/lib/tags'
import { useT } from '@/i18n'
import { TagChip } from './TagChip'
import { TagValuesForm } from './TagValuesForm'

export interface TagValueItem {
  tag_id: number
  values: Record<string, unknown>
}

/**
 * Edit the tag set of ONE target (a contact or an entity) — the working copy
 * only; the parent decides when to save (PUT …/tags). Group exclusivity is
 * applied as you pick: choosing "Employee" drops "Boss" when both sit in the
 * "Role" group, and the picker says so before you choose. Tags that carry
 * attributes open an inline form for their values.
 */
export function TagAssignEditor({
  available,
  value,
  onChange,
  disabled,
  noun = 'contact',
  compact = false,
}: {
  available: TagDef[]
  value: TagValueItem[]
  onChange: (next: TagValueItem[]) => void
  disabled?: boolean
  /** What the target is: 'contact' (default) or 'entity' — picks the copy. */
  noun?: string
  /** Narrow column: one field per row. */
  compact?: boolean
}) {
  const t = useT()
  const isEntity = noun === 'entity'
  const [open, setOpen] = useState<number | null>(null)
  const byId = useMemo(() => new Map(available.map((t) => [t.id, t])), [available])
  const assigned = value.map((v) => ({ item: v, tag: byId.get(v.tag_id) })).filter((x) => x.tag) as {
    item: TagValueItem
    tag: TagDef
  }[]
  const groupHolder = (group: string) =>
    group ? assigned.find((a) => a.tag.group.toLowerCase() === group.toLowerCase())?.tag : undefined

  const options: SelectOption[] = available
    .filter((tg) => !value.some((v) => v.tag_id === tg.id))
    .sort((a, b) => (a.group || '~').localeCompare(b.group || '~') || a.position - b.position || a.name.localeCompare(b.name))
    .map((tg) => {
      const holder = groupHolder(tg.group)
      return {
        value: String(tg.id),
        label: tg.name,
        group: tg.group ? t('admin.tags.assign.groupPickOne', { group: tg.group }) : t('admin.bindings.ungrouped'),
        hint: holder
          ? t('admin.tags.assign.replaces', { name: holder.name })
          : tg.attributes.length
            ? t('admin.tags.assign.fieldCount', { count: tg.attributes.length })
            : undefined,
      }
    })

  const add = (id: string) => {
    const tag = byId.get(Number(id))
    if (!tag) return
    const kept = value.filter((v) => {
      const other = byId.get(v.tag_id)
      return !(tag.group && other && other.group.toLowerCase() === tag.group.toLowerCase())
    })
    onChange([...kept, { tag_id: tag.id, values: {} }])
    if (tag.attributes.length > 0) setOpen(tag.id)
  }

  const remove = (id: number) => onChange(value.filter((v) => v.tag_id !== id))
  const setValues = (id: number, values: Record<string, unknown>) =>
    onChange(value.map((v) => (v.tag_id === id ? { ...v, values } : v)))

  const hasGroups = available.some((tg) => tg.group)

  return (
    <div className="flex flex-col gap-2.5">
      {assigned.length === 0 ? (
        <p className="text-[12px] text-text-muted">
          {isEntity ? t('admin.tags.assign.noneEntity') : t('admin.tags.assign.noneContact')}
        </p>
      ) : (
        <>
        {/* plain tags (no group, no fields) read best as one row of chips */}
        {assigned.some(({ tag }) => !tag.group && tag.attributes.length === 0) && (
          <div className="flex flex-wrap gap-1.5">
            {assigned
              .filter(({ tag }) => !tag.group && tag.attributes.length === 0)
              .map(({ tag }) => (
                <TagChip
                  key={tag.id}
                  name={tag.name}
                  color={tag.color}
                  onRemove={disabled ? undefined : () => remove(tag.id)}
                  disabled={disabled}
                />
              ))}
          </div>
        )}
        <ul className="flex flex-col gap-2 empty:hidden">
          {assigned.filter(({ tag }) => tag.group || tag.attributes.length > 0).map(({ item, tag }) => {
            const missing = tag.attributes.filter((a) => a.required && formatTagValue(a, item.values[a.key]) === '')
            const filled = tag.attributes.filter((a) => formatTagValue(a, item.values[a.key]) !== '')
            const isOpen = open === tag.id
            return (
              <li key={tag.id} className="rounded-lg border border-line bg-card px-2.5 py-2">
                <div className="flex flex-wrap items-center gap-2">
                  <TagChip
                    name={tag.name}
                    color={tag.color}
                    onRemove={disabled ? undefined : () => remove(tag.id)}
                    disabled={disabled}
                  />
                  {tag.group && <span className="text-[11px] text-text-muted">{tag.group}</span>}
                  {tag.attributes.length > 0 && (
                    <button
                      type="button"
                      onClick={() => setOpen(isOpen ? null : tag.id)}
                      className="ml-auto inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11.5px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
                      aria-expanded={isOpen}
                    >
                      {isOpen ? t('common.actions.done') : disabled ? t('admin.tags.assign.details') : t('admin.tags.assign.editDetails')}
                      {isOpen ? <ChevronUp size={12} aria-hidden /> : <ChevronDown size={12} aria-hidden />}
                    </button>
                  )}
                </div>
                {!isOpen && filled.length > 0 && (
                  <dl className="mt-1.5 flex flex-wrap gap-x-3 gap-y-0.5 text-[11.5px]">
                    {filled.map((a) => (
                      <div key={a.key} className="flex min-w-0 gap-1">
                        <dt className="shrink-0 text-text-muted">{a.label}</dt>
                        <dd className="truncate font-medium text-text-high">{formatTagValue(a, item.values[a.key], t)}</dd>
                      </div>
                    ))}
                  </dl>
                )}
                {!isOpen && missing.length > 0 && (
                  <p className="mt-1 text-[11px] font-semibold text-[color:var(--amber-ink)]">
                    {t('admin.tags.assign.missing', { fields: missing.map((a) => a.label).join(', ') })}
                  </p>
                )}
                {isOpen && (
                  <div className="mt-2.5">
                    <TagValuesForm
                      attributes={tag.attributes}
                      values={item.values}
                      onChange={(v) => setValues(tag.id, v)}
                      disabled={disabled}
                      idPrefix={`t${tag.id}`}
                      columns={compact ? 1 : 2}
                    />
                  </div>
                )}
              </li>
            )
          })}
        </ul>
        </>
      )}

      {!disabled && (
        <Select
          value=""
          onChange={add}
          options={options}
          size="md"
          ariaLabel={isEntity ? t('admin.tags.assign.addAriaEntity') : t('admin.tags.assign.addAriaContact')}
          placeholder={options.length === 0 ? t('admin.tags.assign.allApplied') : t('admin.tags.assign.addPlaceholder')}
          disabled={options.length === 0}
          searchable={options.length > 8}
        />
      )}

      {hasGroups && (
        <p className="flex items-start gap-1.5 text-[11px] leading-relaxed text-text-muted">
          <Info size={12} className="mt-[2px] shrink-0" aria-hidden />
          {isEntity ? t('admin.tags.assign.exclusiveEntity') : t('admin.tags.assign.exclusiveContact')}
        </p>
      )}
    </div>
  )
}
