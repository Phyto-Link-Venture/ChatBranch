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

import { EntityPicker } from '@/components/ui/EntityPicker'
import { useT } from '@/i18n'

/** Thin alias kept for the existing call sites; the tree-aware picker lives
 *  in components/ui/EntityPicker. */
export function EntityScopeSelect({
  value,
  onChange,
  disabled,
  globalLabel,
  className = '',
}: {
  /** Entity slug, or null for organization-wide. */
  value: string | null
  onChange: (slug: string | null) => void
  disabled?: boolean
  globalLabel?: string
  className?: string
}) {
  const t = useT()
  return (
    <EntityPicker
      value={value}
      onChange={onChange}
      disabled={disabled}
      globalLabel={globalLabel ?? t('settings.scope.globalLabel')}
      size="sm"
      className={className || 'min-w-[200px]'}
    />
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
  const t = useT()
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
          ? entityName
            ? t('settings.scope.ownTitle', { entity: entityName })
            : t('settings.scope.ownTitleThis')
          : t('settings.scope.inheritedTitle')
      }
    >
      {own ? entityName || t('settings.scope.thisEntity') : t('settings.scope.inherited')}
    </span>
  )
}
