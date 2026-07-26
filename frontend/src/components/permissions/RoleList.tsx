'use client'

import { ChevronRight, Pencil, Plus, Trash2, Users } from 'lucide-react'
import { areaSummary, groupByArea, humanize, type CatalogEntry, type Matrix, type RoleRow } from './types'

/**
 * Level 1 (spec §B): the roles grid. Each card shows label, a system/custom
 * badge, user_count, and a live "k/N areas granted" hint. Selecting a card →
 * Level 2. Custom roles carry rename + delete; system roles are protected.
 * Delete is disabled while the role is assigned (user_count > 0) — the page
 * still handles a 409 as a fallback.
 */
export function RoleList({
  roles,
  roleMeta,
  catalog,
  matrix,
  labelFor,
  isSystemRole,
  onSelect,
  onAdd,
  onRename,
  onDelete,
}: {
  roles: string[]
  roleMeta: Record<string, RoleRow>
  catalog: CatalogEntry[]
  matrix: Matrix
  labelFor: (slug: string) => string
  isSystemRole: (slug: string) => boolean
  onSelect: (slug: string) => void
  onAdd: () => void
  onRename: (slug: string) => void
  onDelete: (slug: string) => void
}) {
  const groups = groupByArea(catalog)
  const areaCount = groups.length

  // How many areas this role has any access to — a quick scannable summary.
  const grantedAreas = (slug: string) =>
    groups.reduce((n, g) => (areaSummary(slug, g.caps, matrix[slug] || {}).granted > 0 ? n + 1 : n), 0)

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3.5">
      <div className="flex items-center justify-between gap-2">
        <div className="text-[12px] text-text-muted">
          {roles.length} role{roles.length === 1 ? '' : 's'} · select one to manage its access
        </div>
        <button
          type="button"
          onClick={onAdd}
          className="inline-flex items-center gap-1.5 rounded-xl border border-line-strong bg-fill px-3 py-2 text-[12px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
        >
          <Plus size={13} strokeWidth={2.25} aria-hidden /> Add role
        </button>
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {roles.map((slug) => {
          const system = isSystemRole(slug)
          const meta = roleMeta[slug]
          const count = meta?.user_count
          const assigned = typeof count === 'number' && count > 0
          return (
            <div
              key={slug}
              className="group flex flex-col gap-3 rounded-2xl border border-line bg-card p-4 transition-colors hover:border-line-strong"
            >
              <div className="flex items-start justify-between gap-2">
                <button
                  type="button"
                  onClick={() => onSelect(slug)}
                  className="min-w-0 flex-1 text-left"
                >
                  <div className="flex items-center gap-2">
                    <span className="truncate text-[14.5px] font-bold tracking-[-0.3px] text-text-heading">
                      {labelFor(slug)}
                    </span>
                    <span
                      className={`shrink-0 rounded-full px-2 py-0.5 text-[9.5px] font-bold uppercase tracking-[0.04em] ${
                        system ? 'bg-fill text-text-muted' : 'bg-brand-400/10 text-brand-300'
                      }`}
                    >
                      {system ? 'System' : 'Custom'}
                    </span>
                  </div>
                  <code className="mt-0.5 block text-[10.5px] text-text-faint">{slug}</code>
                </button>

                {!system && (
                  <div className="flex shrink-0 items-center gap-0.5">
                    <button
                      type="button"
                      onClick={() => onRename(slug)}
                      title={`Rename “${labelFor(slug)}”`}
                      aria-label={`Rename role ${labelFor(slug)}`}
                      className="grid h-7 w-7 place-items-center rounded-lg text-text-faint transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <Pencil size={13} aria-hidden />
                    </button>
                    <button
                      type="button"
                      onClick={() => onDelete(slug)}
                      disabled={assigned}
                      title={
                        assigned
                          ? `Assigned to ${count} user${count === 1 ? '' : 's'} — reassign before deleting`
                          : `Delete “${labelFor(slug)}”`
                      }
                      aria-label={`Delete role ${labelFor(slug)}`}
                      className="grid h-7 w-7 place-items-center rounded-lg text-text-faint transition-colors hover:bg-red/15 hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-text-faint"
                    >
                      <Trash2 size={13} aria-hidden />
                    </button>
                  </div>
                )}
              </div>

              <div className="flex items-center gap-3 text-[11px] text-text-muted">
                <span className="inline-flex items-center gap-1">
                  <Users size={12} aria-hidden />
                  {typeof count === 'number' ? `${count} user${count === 1 ? '' : 's'}` : '—'}
                </span>
                <span aria-hidden>·</span>
                <span>
                  {grantedAreas(slug)}/{areaCount} areas
                </span>
              </div>

              <button
                type="button"
                onClick={() => onSelect(slug)}
                className="mt-auto inline-flex items-center justify-center gap-1.5 rounded-xl border border-line bg-fill px-3 py-1.5 text-[12px] font-semibold text-text-high transition-colors hover:bg-fill-hover"
              >
                Manage access <ChevronRight size={14} strokeWidth={2} aria-hidden />
              </button>
            </div>
          )
        })}
      </div>
    </div>
  )
}
