/**
 * Shared types + pure helpers for the two-level Permissions redesign
 * (upgrade §B). The page owns the network + working matrix; these keep the
 * card-grid components presentational and the status math in one place.
 */

export interface CatalogEntry {
  capability: string
  area: string
  label: string
}

/** role slug → { capability code → granted }. */
export type Matrix = Record<string, Record<string, boolean>>

export interface PermsResponse {
  catalog: CatalogEntry[]
  roles: string[]
  matrix: Matrix
}

/** /api/auth/roles row — label / is_system / user_count for the role list. */
export interface RoleRow {
  slug: string
  label: string
  is_system: boolean
  user_count: number
}

export const BUILTIN_ROLES = ['superadmin', 'admin', 'agent']

const FALLBACK_ROLE_LABEL: Record<string, string> = {
  superadmin: 'Super admin',
  admin: 'Admin',
  agent: 'Agent',
}

/** Title-case a slug for a readable label when the roles endpoint (which
 *  carries the real label + user_count) is unavailable. */
export const humanize = (slug: string) =>
  FALLBACK_ROLE_LABEL[slug] ||
  slug.replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())

/** rbac.manage can never be turned off for superadmin (anti-lockout, spec §3). */
export const isLocked = (role: string, cap: string) =>
  role === 'superadmin' && cap === 'rbac.manage'

/** Deep-clone a plain boolean matrix without depending on structuredClone. */
export const cloneMatrix = (m: Matrix): Matrix =>
  JSON.parse(JSON.stringify(m || {})) as Matrix

/** Effective on/off for a cap in a role — locked caps read as always granted. */
export const capOn = (
  role: string,
  cap: string,
  roleCaps: Record<string, boolean>,
) => (isLocked(role, cap) ? true : !!roleCaps?.[cap])

export interface AreaGroup {
  area: string
  caps: CatalogEntry[]
}

/** Group a flat catalog by `area`, preserving first-seen order. */
export function groupByArea(cat: CatalogEntry[]): AreaGroup[] {
  const order: string[] = []
  const byArea: Record<string, CatalogEntry[]> = {}
  for (const c of cat) {
    if (!byArea[c.area]) {
      byArea[c.area] = []
      order.push(c.area)
    }
    byArea[c.area].push(c)
  }
  return order.map((area) => ({ area, caps: byArea[area] }))
}

export type AreaStatus = 'none' | 'partial' | 'full'

export interface AreaSummary {
  total: number
  granted: number
  status: AreaStatus
}

/** k/N + No/Partial/Full for one area, honouring locked caps. */
export function areaSummary(
  role: string,
  caps: CatalogEntry[],
  roleCaps: Record<string, boolean>,
): AreaSummary {
  const total = caps.length
  const granted = caps.reduce(
    (n, c) => n + (capOn(role, c.capability, roleCaps) ? 1 : 0),
    0,
  )
  const status: AreaStatus =
    granted === 0 ? 'none' : granted === total ? 'full' : 'partial'
  return { total, granted, status }
}

/** Visual tokens per status — pill text uses the theme-flipping ink vars,
 *  the card's left border tints to match (spec §B). */
export const STATUS_STYLE: Record<
  AreaStatus,
  { label: (s: AreaSummary) => string; ink: string; pillBg: string; border: string }
> = {
  none: {
    label: () => 'No access',
    ink: 'var(--text-muted)',
    pillBg: 'var(--fill)',
    border: 'var(--line-strong)',
  },
  partial: {
    label: (s) => `Partial (${s.granted}/${s.total})`,
    ink: 'var(--amber-ink)',
    pillBg: 'rgba(200,137,26,0.14)',
    border: 'var(--amber-ink)',
  },
  full: {
    label: (s) => `Full access (${s.total}/${s.total})`,
    ink: 'var(--green-ink)',
    pillBg: 'rgba(47,125,79,0.14)',
    border: 'var(--green-ink)',
  },
}
