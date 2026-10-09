'use client'

import { useMemo, useState } from 'react'
import { AlertCircle, ArrowLeft, Check, CheckCheck, Loader2, Search } from 'lucide-react'
import { useT } from '@/i18n'
import { capabilityArea } from '@/lib/auth'
import { AreaCard } from './AreaCard'
import { ManageAreaModal } from './ManageAreaModal'
import { groupByArea, isLocked, type CatalogEntry } from './types'

/**
 * Level 2 (spec §B): for the selected role, one resource card per capability
 * area. Toolbar = Search (filter cards by area) · Check All (grant everything) ·
 * Save/Discard. Per-area checkbox and the Manage modal both edit the role's
 * working cap map (lifted to the page via `onChangeRoleCaps`); locked caps
 * (superadmin rbac.manage) stay on through every bulk op.
 */
export function RoleCardGrid({
  role,
  roleLabel,
  isSystem,
  catalog,
  roleCaps,
  onChangeRoleCaps,
  onBack,
  dirty,
  saving,
  onSave,
  onDiscard,
  saveMsg,
}: {
  role: string
  roleLabel: string
  isSystem: boolean
  catalog: CatalogEntry[]
  roleCaps: Record<string, boolean>
  onChangeRoleCaps: (next: Record<string, boolean>) => void
  onBack: () => void
  dirty: boolean
  saving: boolean
  onSave: () => void
  onDiscard: () => void
  saveMsg: { ok: boolean; text: string } | null
}) {
  const t = useT()
  const [query, setQuery] = useState('')
  const [manageArea, setManageArea] = useState<string | null>(null)

  const groups = useMemo(() => groupByArea(catalog), [catalog])
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return q
      ? groups.filter((g) => g.area.toLowerCase().includes(q) || capabilityArea(t, g.area).toLowerCase().includes(q))
      : groups
  }, [groups, query, t])

  const active = manageArea ? groups.find((g) => g.area === manageArea) : null

  // Set every cap in one area to `value` (locked caps stay on).
  const toggleArea = (caps: CatalogEntry[], value: boolean) => {
    const next = { ...roleCaps }
    for (const c of caps) next[c.capability] = isLocked(role, c.capability) ? true : value
    onChangeRoleCaps(next)
  }

  // Grant EVERYTHING to this role.
  const checkAll = () => {
    const next = { ...roleCaps }
    for (const c of catalog) next[c.capability] = true
    onChangeRoleCaps(next)
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3.5">
      {/* Sub-header: back + role identity */}
      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          onClick={onBack}
          className="inline-flex items-center gap-1.5 rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          <ArrowLeft size={14} strokeWidth={2} aria-hidden /> {t('admin.permissions.grid.roles')}
        </button>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h2 className="truncate text-[16px] font-extrabold tracking-[-0.4px] text-text-heading">
              {roleLabel}
            </h2>
            <span
              className={`rounded-full px-2 py-0.5 text-[9.5px] font-bold uppercase tracking-[0.04em] ${
                isSystem ? 'bg-fill text-text-muted' : 'bg-brand-400/10 text-brand-300'
              }`}
            >
              {isSystem ? t('admin.permissions.system') : t('admin.permissions.custom')}
            </span>
          </div>
          <div className="mt-0.5 text-[11.5px] text-text-muted">
            {t('admin.permissions.grid.hint')}
          </div>
        </div>

        <div className="ml-auto flex items-center gap-2">
          {saveMsg && !dirty && (
            <span
              className={`flex items-center gap-1.5 text-[12px] font-semibold ${
                saveMsg.ok ? 'text-brand-300' : 'text-red-300'
              }`}
            >
              {saveMsg.ok ? <Check size={14} strokeWidth={2.25} /> : <AlertCircle size={13} />}{' '}
              {saveMsg.text}
            </span>
          )}
          {(dirty || saving) && (
            <>
              <span className="hidden text-[12px] font-semibold text-amber-300 sm:inline">
                {t('settings.common.unsavedChanges')}
              </span>
              <button
                onClick={onDiscard}
                disabled={saving}
                className="cursor-pointer rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-muted transition-colors hover:bg-fill-hover disabled:opacity-50"
              >
                {t('common.actions.discard')}
              </button>
              <button
                onClick={onSave}
                disabled={saving}
                className="flex cursor-pointer items-center gap-1.5 rounded-xl px-3.5 py-1.5 text-[12px] font-bold text-white disabled:opacity-60"
                style={{ background: 'var(--primary)' }}
              >
                {saving && <Loader2 size={13} className="animate-spin" aria-hidden />}
                {t('settings.common.saveChanges')}
              </button>
            </>
          )}
        </div>
      </div>

      {/* Toolbar: search + check all */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-[180px] flex-1">
          <Search
            size={14}
            className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-faint"
            aria-hidden
          />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('admin.permissions.grid.searchPlaceholder')}
            className="w-full rounded-xl border border-line-strong bg-card py-2 pl-9 pr-3 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
          />
        </div>
        <button
          type="button"
          onClick={checkAll}
          className="inline-flex items-center gap-1.5 rounded-xl border border-line bg-fill px-3 py-2 text-[12px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
        >
          <CheckCheck size={14} strokeWidth={2} aria-hidden /> {t('admin.permissions.grid.checkAll')}
        </button>
      </div>

      {/* Card grid — ~4 cols, responsive down to 1 */}
      <div className="min-h-0 flex-1 overflow-auto">
        {filtered.length === 0 ? (
          <div className="py-10 text-center text-[12.5px] text-text-muted">
            {t('admin.permissions.grid.noMatch', { query })}
          </div>
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {filtered.map((g) => (
              <AreaCard
                key={g.area}
                area={g.area}
                caps={g.caps}
                role={role}
                roleCaps={roleCaps}
                onToggleAll={(v) => toggleArea(g.caps, v)}
                onManage={() => setManageArea(g.area)}
              />
            ))}
          </div>
        )}
      </div>

      {active && (
        <ManageAreaModal
          open={!!manageArea}
          onClose={() => setManageArea(null)}
          area={active.area}
          caps={active.caps}
          role={role}
          roleLabel={roleLabel}
          roleCaps={roleCaps}
          onSave={(areaCaps) => onChangeRoleCaps({ ...roleCaps, ...areaCaps })}
        />
      )}
    </div>
  )
}
