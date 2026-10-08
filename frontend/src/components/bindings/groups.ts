/**
 * Group builders for BindingGrid — how each kind of candidate is laid out as
 * cards. Shared by the Bindings page, the Users "Entity access" modal, the
 * Teams member picker, the contact page and the tag binding view, so "a main
 * entity card with its sub-entities inside" means the same thing everywhere.
 */
import type { ReactNode } from 'react'
import type { BindingGroup, BindingItem } from '@/components/ui/BindingGrid'
import { channelLabel } from '@/components/ui/ChannelLogo'

export interface TreeEntity {
  id: number
  name: string
  parent_id: number | null
  slug?: string
}

/**
 * Depth-first entity order with a depth per row. An entity whose parent is not
 * in the visible set (an agent may see only part of the tree) is treated as a
 * root, so it never vanishes.
 */
export function orderEntityTree<T extends TreeEntity>(rows: T[]): { row: T; depth: number; root: T }[] {
  const visible = new Set(rows.map((r) => r.id))
  const kids = new Map<number | null, T[]>()
  for (const r of rows) {
    const key = r.parent_id !== null && visible.has(r.parent_id) ? r.parent_id : null
    kids.set(key, [...(kids.get(key) ?? []), r])
  }
  for (const list of kids.values()) list.sort((a, b) => a.name.localeCompare(b.name))
  const out: { row: T; depth: number; root: T }[] = []
  const seen = new Set<number>()
  const walk = (parent: number | null, depth: number, root: T | null) => {
    for (const row of kids.get(parent) ?? []) {
      if (seen.has(row.id) || depth > 8) continue
      seen.add(row.id)
      const r = root ?? row
      out.push({ row, depth, root: r })
      walk(row.id, depth + 1, r)
    }
  }
  walk(null, 0, null)
  for (const r of rows) if (!seen.has(r.id)) out.push({ row: r, depth: 0, root: r })
  return out
}

/** Groups = main entities; items = the main entity itself and every entity under it. */
export function entityTreeGroups(entities: TreeEntity[], hint?: (e: TreeEntity) => string | undefined): BindingGroup[] {
  const ordered = orderEntityTree(entities)
  const groups = new Map<number, BindingGroup>()
  for (const { row, depth, root } of ordered) {
    let g = groups.get(root.id)
    if (!g) {
      g = { id: `e${root.id}`, label: root.name, badge: 'Main', items: [] }
      groups.set(root.id, g)
    }
    g.items.push({
      id: String(row.id),
      label: row.name,
      depth,
      hint: hint?.(row) ?? (depth === 0 ? 'Main entity — includes everything beneath it' : undefined),
    })
  }
  for (const g of groups.values()) {
    const subs = g.items.length - 1
    g.sub = subs === 0 ? 'No sub-entities' : `Main + ${subs} sub-entit${subs === 1 ? 'y' : 'ies'}`
  }
  return [...groups.values()]
}

/**
 * Groups = every entity in tree order (main entities badged, sub-entities
 * labelled with their parent); items = the records linked directly to that
 * entity. Records linked to nothing land in a trailing "No entity" card. A
 * record linked to two entities appears in both cards — the grid's value is a
 * set of ids, so ticking it in one ticks it in both.
 */
export function itemsByEntityGroups(
  entities: TreeEntity[],
  items: { id: number; label: string; hint?: string; entity_ids: number[] }[],
  noun = 'contact',
): BindingGroup[] {
  const ordered = orderEntityTree(entities)
  const byId = new Map(entities.map((e) => [e.id, e]))
  const out: BindingGroup[] = []
  for (const { row, depth } of ordered) {
    const members: BindingItem[] = items
      .filter((i) => i.entity_ids.includes(row.id))
      .map((i) => ({ id: String(i.id), label: i.label, hint: i.hint }))
    if (members.length === 0) continue
    const parent = row.parent_id !== null ? byId.get(row.parent_id) : undefined
    out.push({
      id: `e${row.id}`,
      label: row.name,
      badge: depth === 0 ? 'Main' : undefined,
      sub: `${depth > 0 && parent ? `Under ${parent.name} · ` : ''}${members.length} ${noun}${members.length === 1 ? '' : 's'}`,
      items: members,
    })
  }
  const loose = items.filter((i) => !i.entity_ids.some((id) => byId.has(id)))
  if (loose.length > 0)
    out.push({
      id: 'none',
      label: 'No entity',
      sub: `${loose.length} ${noun}${loose.length === 1 ? '' : 's'} not linked to an entity`,
      items: loose.map((i) => ({ id: String(i.id), label: i.label, hint: i.hint })),
    })
  return out
}

const ROLE_LABEL: Record<string, string> = { superadmin: 'Super admins', admin: 'Admins', agent: 'Agents' }
const ROLE_ORDER = ['superadmin', 'admin', 'agent']

/** Users grouped by role (built-ins first, then custom roles). */
export function usersByRoleGroups(users: { id: number; label: string; role: string }[]): BindingGroup[] {
  const roles = [...new Set(users.map((u) => u.role))].sort(
    (a, b) => (ROLE_ORDER.indexOf(a) + 1 || 99) - (ROLE_ORDER.indexOf(b) + 1 || 99) || a.localeCompare(b),
  )
  return roles.map((role) => {
    const members = users.filter((u) => u.role === role)
    return {
      id: `r-${role}`,
      label: ROLE_LABEL[role] ?? role.replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()),
      sub: `${members.length} user${members.length === 1 ? '' : 's'}`,
      items: members.map((u) => ({ id: String(u.id), label: u.label })),
    }
  })
}

/** Channel accounts grouped by channel (label + optional logo per channel). */
export function accountsByChannelGroups(
  accounts: { id: number; label: string; channel: string; sub?: string }[],
  icon?: (channel: string) => ReactNode,
): BindingGroup[] {
  const channels = [...new Set(accounts.map((a) => a.channel))].sort()
  return channels.map((ch) => {
    const members = accounts.filter((a) => a.channel === ch)
    return {
      id: `c-${ch}`,
      label: channelLabel(ch),
      icon: icon?.(ch),
      sub: `${members.length} account${members.length === 1 ? '' : 's'}`,
      items: members.map((a) => ({ id: String(a.id), label: a.label, hint: a.sub })),
    }
  })
}

/** The add/remove diff between a saved and a working set of string ids. */
export function idDiff(saved: Set<string>, draft: Set<string>): { add: number[]; remove: number[] } {
  return {
    add: [...draft].filter((id) => !saved.has(id)).map(Number),
    remove: [...saved].filter((id) => !draft.has(id)).map(Number),
  }
}

export const sameSet = (a: Set<string>, b: Set<string>) => a.size === b.size && [...a].every((x) => b.has(x))
