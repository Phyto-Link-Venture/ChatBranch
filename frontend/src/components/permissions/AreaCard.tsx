'use client'

import { useEffect, useRef } from 'react'
import { SlidersHorizontal } from 'lucide-react'
import { useT } from '@/i18n'
import { capabilityArea } from '@/lib/auth'
import { areaSummary, STATUS_STYLE, type CatalogEntry } from './types'

/**
 * One resource card per capability area (spec §B): area name, a No/Partial/Full
 * status pill, a left-border tint by status, a checkbox that grants/revokes ALL
 * caps in the area (indeterminate on partial), and a Manage button opening the
 * per-capability modal.
 */
export function AreaCard({
  area,
  caps,
  role,
  roleCaps,
  onToggleAll,
  onManage,
}: {
  area: string
  caps: CatalogEntry[]
  role: string
  roleCaps: Record<string, boolean>
  onToggleAll: (value: boolean) => void
  onManage: () => void
}) {
  const t = useT()
  const areaName = capabilityArea(t, area)
  const sum = areaSummary(role, caps, roleCaps)
  const style = STATUS_STYLE[sum.status]
  const boxRef = useRef<HTMLInputElement>(null)

  // Native indeterminate can only be set via the DOM node.
  useEffect(() => {
    if (boxRef.current) boxRef.current.indeterminate = sum.status === 'partial'
  }, [sum.status])

  return (
    <div
      className="flex flex-col gap-3 rounded-2xl border border-line bg-card p-4"
      style={{ borderLeftWidth: 3, borderLeftColor: style.border }}
    >
      <div className="flex items-start justify-between gap-2">
        <label className="flex cursor-pointer items-start gap-2.5">
          <input
            ref={boxRef}
            type="checkbox"
            checked={sum.status === 'full'}
            onChange={(e) => onToggleAll(e.target.checked)}
            aria-label={t('admin.permissions.area.grantAllAria', { area: areaName })}
            className="mt-0.5 h-4 w-4 shrink-0 cursor-pointer accent-brand"
          />
          <span className="min-w-0">
            <span className="block text-[13.5px] font-bold tracking-[-0.2px] text-text-heading">
              {areaName}
            </span>
            <span className="mt-0.5 block text-[11px] text-text-muted">
              {t('admin.permissions.area.capCount', { count: caps.length })}
            </span>
          </span>
        </label>
      </div>

      <span
        className="inline-flex w-fit items-center rounded-full px-2.5 py-1 text-[11px] font-semibold"
        style={{ color: style.ink, background: style.pillBg }}
      >
        {style.label(t, sum)}
      </span>

      <button
        type="button"
        onClick={onManage}
        className="mt-auto inline-flex items-center justify-center gap-1.5 rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
      >
        <SlidersHorizontal size={13} strokeWidth={2} aria-hidden /> {t('admin.permissions.area.manage')}
      </button>
    </div>
  )
}
