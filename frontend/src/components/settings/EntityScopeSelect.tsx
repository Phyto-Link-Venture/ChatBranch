'use client'

/**
 * The one control for "who does this belong to?" — used wherever a resource
 * can be bound to an outlet or left organization-wide.
 *
 * The binding rule it expresses (the same one crm/scoping.py enforces):
 * a resource bound to an entity is that outlet's; an UNBOUND one is the
 * organization's, and acts as the fallback for every outlet that has none of
 * its own. So the empty option is never "none" — it is a real, meaningful
 * choice, and it is labelled that way.
 */

import { useEntity } from '@/lib/EntityContext'

export function EntityScopeSelect({
  value,
  onChange,
  disabled,
  globalLabel = 'Organization-wide (shared by all entities)',
  className = '',
}: {
  /** Entity slug, or null for organization-wide. */
  value: string | null
  onChange: (slug: string | null) => void
  disabled?: boolean
  globalLabel?: string
  className?: string
}) {
  const { entities } = useEntity()

  return (
    <select
      value={value ?? ''}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value || null)}
      className={`rounded-lg border border-line bg-fill-subtle px-2.5 py-1.5 text-[12px] text-text-high outline-none disabled:opacity-50 ${className}`}
    >
      <option value="">{globalLabel}</option>
      {entities.map((b) => (
        <option key={b.slug} value={b.slug}>
          {b.name}
        </option>
      ))}
    </select>
  )
}

/** Small badge naming a resource's scope — inherited vs this outlet's own. */
export function ScopeBadge({
  scope,
  entityName = '',
}: {
  scope: 'global' | 'entity' | string
  entityName?: string
}) {
  const own = scope === 'entity'
  return (
    <span
      className={`rounded-md px-1.5 py-0.5 text-[10px] font-semibold ${
        own
          ? 'bg-brand-500/15 text-[var(--brand-ink)]'
          : 'bg-fill text-text-muted'
      }`}
      title={
        own
          ? `Belongs to ${entityName || 'this entity'} only`
          : 'Set organization-wide — every entity inherits this'
      }
    >
      {own ? entityName || 'This entity' : 'Inherited'}
    </span>
  )
}
