'use client'

import { Layers } from 'lucide-react'
import { useEntity } from '@/lib/EntityContext'
import { useT } from '@/i18n'

/**
 * Shown on pages that read or edit ONE level of the entity tree (persona, AI
 * behaviour, the AI switch…) while several entities are ticked in the switcher:
 * those pages fall back to the organization level (`useEntityLevelQuery`) — or,
 * for a person bound to entities, to their first ticked entity — and this says so.
 */
export function MultiEntityNotice({ className = '' }: { className?: string }) {
  const t = useT()
  const { multipleEntities, bound, levelEntity } = useEntity()
  if (!multipleEntities) return null
  return (
    <p
      role="status"
      className={`flex items-start gap-2 rounded-lg border border-line bg-fill-subtle px-3 py-2 text-[12.5px] text-text-mid ${className}`}
    >
      <Layers size={14} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />
      <span>
        {bound && levelEntity
          ? t('shell.entitySwitcher.levelNoticeBound', { name: levelEntity.name })
          : t('shell.entitySwitcher.levelNotice')}
      </span>
    </p>
  )
}
