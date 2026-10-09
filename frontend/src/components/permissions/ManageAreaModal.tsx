'use client'

import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { useT } from '@/i18n'
import { capabilityArea, capabilityLabel } from '@/lib/auth'
import { Toggle } from './Toggle'
import { capOn, isLocked, type CatalogEntry } from './types'

/**
 * Level-2 detail modal (spec §B): lists one area's individual capabilities —
 * label + capability code — each with a toggle. Edits a LOCAL draft so
 * Cancel discards; Save merges just this area's caps back into the role's map.
 */
export function ManageAreaModal({
  open,
  onClose,
  area,
  caps,
  role,
  roleLabel,
  roleCaps,
  onSave,
}: {
  open: boolean
  onClose: () => void
  area: string
  caps: CatalogEntry[]
  role: string
  roleLabel: string
  roleCaps: Record<string, boolean>
  onSave: (next: Record<string, boolean>) => void
}) {
  const t = useT()
  const areaName = capabilityArea(t, area)
  // Local draft, seeded from the live role map each time the modal opens.
  const [draft, setDraft] = useState<Record<string, boolean>>({})

  useEffect(() => {
    if (!open) return
    const seed: Record<string, boolean> = {}
    for (const c of caps) seed[c.capability] = capOn(role, c.capability, roleCaps)
    setDraft(seed)
  }, [open, area, caps, role, roleCaps])

  const commit = () => {
    // Only carry this area's caps back; locked caps stay on.
    const next: Record<string, boolean> = {}
    for (const c of caps) {
      next[c.capability] = isLocked(role, c.capability) ? true : !!draft[c.capability]
    }
    onSave(next)
    onClose()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={areaName}
      subtitle={t('admin.permissions.manage.subtitle', { area: areaName, role: roleLabel })}
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button variant="primary" onClick={commit}>
            {t('common.actions.apply')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-1.5">
        {caps.map((c) => {
          const locked = isLocked(role, c.capability)
          const on = locked ? true : !!draft[c.capability]
          return (
            <div
              key={c.capability}
              className="flex items-center justify-between gap-3 rounded-xl border border-line bg-card px-3 py-2.5"
            >
              <div className="min-w-0">
                <div className="text-[12.5px] font-semibold text-text-high">{capabilityLabel(t, c)}</div>
                <code className="text-[10.5px] text-text-faint">{c.capability}</code>
              </div>
              <Toggle
                on={on}
                locked={locked}
                onChange={(v) => setDraft((d) => ({ ...d, [c.capability]: v }))}
                label={`${roleLabel} — ${capabilityLabel(t, c)}`}
              />
            </div>
          )
        })}
      </div>
    </Modal>
  )
}
