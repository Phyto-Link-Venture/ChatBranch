'use client'

import { useCallback, useEffect, useState } from 'react'
import { Building2, CornerDownRight, Loader2, Pencil, Plus, ShieldAlert, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { authHeaders, hasCap } from '@/lib/auth'
import { useRole } from '@/lib/useRole'
import { useEntity } from '@/lib/EntityContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/**
 * Entities (RBAC entities model, /api/v1/entities + /api/v1/whoami).
 * Entities form a TREE: a top-level entity (e.g. "Main") with child entities
 * under it. Creating one asks only for its parent — the tenant is inherited
 * from the parent (or the caller's own), never typed in by hand.
 * Access: admins see every entity in their tenant, agents only their assigned
 * entities (managed in Users & Roles).
 */

interface EntityRow {
  id: number
  name: string
  slug: string
  tenant: string
  /** null = a top-level entity; otherwise the entity it sits under. */
  parent_id: number | null
  parent?: string | null
  parent_name?: string
  is_active: boolean
  created_at: string
}

interface WhoAmI {
  username: string
  role: string
  tenant: string | null
  entities: string[]
}

const slugify = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)

/**
 * Depth-first tree order (parents immediately followed by their children) with
 * a depth per row. Rows whose parent is missing from the visible set — an agent
 * or admin may only see part of the tree — are treated as roots so they never
 * vanish from the table.
 */
function orderTree(rows: EntityRow[]): { row: EntityRow; depth: number }[] {
  const visible = new Set(rows.map((r) => r.id))
  const byParent = new Map<number | null, EntityRow[]>()
  for (const r of rows) {
    const key = r.parent_id !== null && visible.has(r.parent_id) ? r.parent_id : null
    byParent.set(key, [...(byParent.get(key) ?? []), r])
  }
  for (const list of byParent.values()) list.sort((a, b) => a.name.localeCompare(b.name))
  const out: { row: EntityRow; depth: number }[] = []
  const walk = (parent: number | null, depth: number) => {
    for (const row of byParent.get(parent) ?? []) {
      if (out.some((o) => o.row.id === row.id)) continue // cyclic data guard
      out.push({ row, depth })
      walk(row.id, depth + 1)
    }
  }
  walk(null, 0)
  // anything unreachable (a cycle) still gets listed
  for (const r of rows) if (!out.some((o) => o.row.id === r.id)) out.push({ row: r, depth: 0 })
  return out
}

/** The id itself plus every entity beneath it — invalid parents when re-parenting. */
function descendantIds(rows: EntityRow[], id: number): Set<number> {
  const out = new Set([id])
  let grew = true
  while (grew) {
    grew = false
    for (const r of rows)
      if (r.parent_id !== null && out.has(r.parent_id) && !out.has(r.id)) {
        out.add(r.id)
        grew = true
      }
  }
  return out
}

async function v1<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const body = data as { error?: { message?: string }; detail?: string }
    throw new Error(body.error?.message || body.detail || `HTTP ${res.status}`)
  }
  return data as T
}

export function EntitiesSettings() {
  const { role, ready } = useRole()
  const { reload: reloadGlobalEntities } = useEntity()
  const [entities, setEntities] = useState<EntityRow[] | null>(null)
  const [me, setMe] = useState<WhoAmI | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // create/edit modal — `editing` null = create, a row = edit
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<EntityRow | null>(null)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [parentId, setParentId] = useState<number | null>(null)
  const [active, setActive] = useState(true)
  // entities.manage gates create/edit/delete (read after mount — localStorage)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('entities.manage')), [])

  const refresh = useCallback(async () => {
    try {
      const [b, w] = await Promise.all([
        v1<{ entities: EntityRow[] }>('entities'),
        v1<WhoAmI>('whoami'),
      ])
      setEntities(b.entities)
      setMe(w)
      // keep the global topbar switcher in step — a newly created/deleted
      // entity shows up there without a page refresh (#4).
      reloadGlobalEntities()
    } catch {
      setEntities(null)
    }
  }, [reloadGlobalEntities])

  useEffect(() => {
    refresh()
  }, [refresh])

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (role === 'agent') {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <ShieldAlert size={15} className="text-amber-300" aria-hidden />
        Entities are managed by admins.
      </div>
    )
  }

  if (entities === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Entity management unavailable — is the backend API running on port 8000?
      </div>
    )
  }

  // Depth-first order with a depth per row, so the table reads as a tree.
  const ordered = orderTree(entities)
  const depthOf = new Map(ordered.map((r) => [r.row.id, r.depth]))

  const openCreate = () => {
    setEditing(null)
    setName('')
    setSlug('')
    setSlugTouched(false)
    // default under the first top-level entity (usually "Main") — the common
    // case is adding a branch under the organization's root
    setParentId(entities.find((e) => e.parent_id === null)?.id ?? null)
    setActive(true)
    setMsg(null)
    setOpen(true)
  }

  const openEdit = (b: EntityRow) => {
    setEditing(b)
    setName(b.name)
    setSlug(b.slug)
    setSlugTouched(true)
    setParentId(b.parent_id)
    setActive(b.is_active)
    setMsg(null)
    setOpen(true)
  }

  const saveEntity = async () => {
    setBusy(true)
    setMsg(null)
    try {
      if (editing) {
        // PATCH /api/v1/entities/<id> — name / slug / is_active (backend landing
        // in parallel; a 404/405 surfaces as a clear message).
        await v1(`entities/${editing.id}`, {
          method: 'PATCH',
          body: JSON.stringify({
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            parent_id: parentId ?? 0, // 0 = promote to top level
            is_active: active,
          }),
        })
        setMsg({ ok: true, text: `Entity ${name.trim()} updated.` })
      } else {
        await v1('entities', {
          method: 'POST',
          body: JSON.stringify({
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            // The tenant comes from the parent server-side (or the caller's own
            // for a top-level entity) — it is never chosen here.
            parent_id: parentId ?? 0,
          }),
        })
        setMsg({ ok: true, text: `Entity ${name.trim()} created.` })
      }
      setName('')
      setSlug('')
      setSlugTouched(false)
      setEditing(null)
      setOpen(false)
      await refresh()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const deleteEntity = async (b: EntityRow) => {
    if (!window.confirm(`Delete entity "${b.name}"? Contacts and assignments tied to it may be affected.`)) return
    setBusy(true)
    setMsg(null)
    try {
      // DELETE /api/v1/entities/<id> — a 409 (entity still in use) carries the
      // reason in the v1 error envelope, surfaced verbatim below.
      await v1(`entities/${b.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `Entity ${b.name} deleted.` })
      await refresh()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const columns: Column<EntityRow>[] = [
    {
      key: 'name',
      header: 'Entity',
      sortable: true,
      value: (b) => `${b.name} ${b.slug}`,
      render: (b) => (
        <div className="flex items-center gap-2" style={{ paddingLeft: (depthOf.get(b.id) ?? 0) * 18 }}>
          {(depthOf.get(b.id) ?? 0) > 0 && (
            <CornerDownRight size={12} className="shrink-0 text-text-faint" aria-hidden />
          )}
          <span className="text-[13px] font-semibold text-text-high">{b.name}</span>
          <code className="rounded bg-fill-hover px-1.5 py-px text-[10.5px] text-text-muted">{b.slug}</code>
          {!b.is_active && (
            <span className="rounded-full bg-red/15 px-2 py-px text-[10px] font-bold text-red-300">Inactive</span>
          )}
        </div>
      ),
    },
    {
      key: 'parent',
      header: 'Parent',
      sortable: true,
      width: 220,
      value: (b) => b.parent_name || b.parent || '',
      render: (b) =>
        b.parent_id === null ? (
          <span className="flex items-center gap-1.5 text-[12px] text-text-muted">
            <Building2 size={12} aria-hidden /> Top level
          </span>
        ) : (
          <span className="flex items-center gap-1.5 text-[12px] text-text-mid">
            <CornerDownRight size={12} className="text-text-muted" aria-hidden />
            {b.parent_name || b.parent}
          </span>
        ),
    },
    {
      key: 'created_at',
      header: 'Created',
      sortable: true,
      width: 140,
      value: (b) => b.created_at,
      render: (b) => (
        <span className="text-[10.5px] text-text-faint">{new Date(b.created_at).toLocaleDateString()}</span>
      ),
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: 'Actions',
            align: 'right' as const,
            width: 100,
            render: (b: EntityRow) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(b)}
                  disabled={busy}
                  title={`Edit ${b.name}`}
                  aria-label={`Edit entity ${b.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => deleteEntity(b)}
                  disabled={busy}
                  title={`Delete ${b.name}`}
                  aria-label={`Delete entity ${b.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </div>
            ),
          },
        ]
      : []),
  ]

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        Entities form a tree: a <b>top-level</b> entity (your organization) with branches, outlets or client units
        underneath it. A child inherits everything from its parent — contacts are shared across the tree, and a new
        entity only needs a name and a parent. Admins see every entity in their organization; agents only the entities
        assigned to them (set per user under Users &amp; Roles).
      </p>

      {/* Success (and any failure raised outside the modal, e.g. delete) — the
          modal renders its own copy of failures so they aren't lost behind it. */}
      {msg && (!open || msg.ok) && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}

      <DataTable
        columns={columns}
        rows={ordered.map((o) => o.row)}
        rowKey={(b) => b.id}
        searchKeys={['name', 'slug']}
        searchPlaceholder="Search entities…"
        empty="No entities yet — add the first one."
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> Add entity
            </Button>
          ) : undefined
        }
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? `Edit ${editing.name}` : 'Add entity'}
        subtitle="An entity is a branch, outlet or client unit. Pick where it sits in the tree."
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={saveEntity}
              disabled={busy || !name.trim()}
            >
              {busy ? (
                <Loader2 size={13} className="animate-spin" aria-hidden />
              ) : editing ? (
                <Pencil size={13} aria-hidden />
              ) : (
                <Plus size={13} aria-hidden />
              )}{' '}
              {editing ? 'Save changes' : 'Add entity'}
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="sm:col-span-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[12px] font-semibold text-red-300"
            >
              {msg.text}
            </div>
          )}
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Entity name</span>
            <input
              type="text"
              value={name}
              onChange={(e) => {
                setName(e.target.value)
                if (!slugTouched) setSlug(slugify(e.target.value))
              }}
              placeholder="e.g. Northwind Trading"
              className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Slug</span>
            <input
              type="text"
              value={slug}
              onChange={(e) => {
                setSlugTouched(true)
                setSlug(slugify(e.target.value))
              }}
              placeholder="auto-generated from the name"
              className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
            />
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              Short unique id, used in APIs and reports. Unique across the organization.
            </span>
          </label>
          <label className="block sm:col-span-2">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Parent entity</span>
            <Select
              value={parentId === null ? '' : String(parentId)}
              onChange={(v) => setParentId(v === '' ? null : Number(v))}
              size="md"
              ariaLabel="Parent entity"
              options={[
                { value: '', label: 'Top level (no parent)' },
                ...ordered
                  // an entity can't sit under itself or its own descendants
                  .filter((o) => !editing || !descendantIds(entities, editing.id).has(o.row.id))
                  .map((o) => ({
                    value: String(o.row.id),
                    label: `${'\u2007'.repeat(o.depth * 2)}${o.depth > 0 ? '\u21b3 ' : ''}${o.row.name}`,
                    hint: o.row.slug,
                  })),
              ]}
            />
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              Everything is inherited from the parent, including which organization it belongs to. Leave this at
              &ldquo;Top level&rdquo; for the root of your tree.
            </span>
          </label>
          {editing && (
            <label className="flex cursor-pointer items-center gap-2 sm:col-span-2">
              <input
                type="checkbox"
                checked={active}
                onChange={(e) => setActive(e.target.checked)}
                style={{ accentColor: '#3B82F6' }}
              />
              <span className="text-[12px] text-text-primary">
                Active
                <span className="ml-1 text-[10.5px] text-text-muted">
                  — inactive entities stay on record but drop out of routing.
                </span>
              </span>
            </label>
          )}
        </div>
      </Modal>
    </div>
  )
}
