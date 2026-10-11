'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { CornerDownRight, Loader2, Pencil, Plus, ShieldAlert, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Toggle } from '@/components/ui/Toggle'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { Advanced } from '@/components/ui/Advanced'
import { TagChip } from '@/components/tags/TagChip'
import { TagAssignEditor, type TagValueItem } from '@/components/tags/TagAssignEditor'
import { orderEntityTree } from '@/components/bindings/groups'
import { authHeaders, hasCap } from '@/lib/auth'
import { useAccess } from '@/lib/useAccess'
import { useEntity, type Entity } from '@/lib/EntityContext'
import { fetchTagAssignments, fetchTags, saveEntityTags, type TagDef } from '@/lib/tags'
import { Trans, useFormat, useT } from '@/i18n'

/**
 * Entities (RBAC entities model, /api/v1/entities + /api/v1/whoami).
 * Entities form a TREE: a main (top-level) entity with sub-entities under it.
 * Creating one asks only for its parent — the tenant is inherited from the
 * parent (or the caller's own), never typed in by hand. The table reads as the
 * tree: main entities carry a MAIN badge, sub-entities are indented under
 * their parent. Entity tags (inner company / outside company…) show as chips
 * and are edited in the entity modal.
 */

interface EntityRow {
  id: number
  name: string
  slug: string
  tenant: string
  /** null = a main (top-level) entity; otherwise the entity it sits under. */
  parent_id: number | null
  parent?: string | null
  parent_name?: string
  is_active: boolean
  created_at: string
}

const slugify = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)

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

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

function MainBadge() {
  const t = useT()
  return (
    <span className="shrink-0 rounded-sm bg-primary px-1 py-px text-[9px] font-bold uppercase tracking-[0.05em] text-on-primary">
      {t('admin.bindings.groups.main')}
    </span>
  )
}

export function EntitiesSettings() {
  const t = useT()
  const f = useFormat()
  const { ready, can } = useAccess()
  const { reload: reloadGlobalEntities } = useEntity()
  const [entities, setEntities] = useState<EntityRow[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // entity tags (registry) + who carries them
  const [entityTags, setEntityTags] = useState<TagDef[] | null>(null)
  const [assigned, setAssigned] = useState<Map<number, TagValueItem[]>>(new Map())
  // create/edit modal — `editing` null = create, a row = edit
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<EntityRow | null>(null)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [parentId, setParentId] = useState<number | null>(null)
  const [active, setActive] = useState(true)
  const [tagDraft, setTagDraft] = useState<TagValueItem[]>([])
  // entities.manage gates create/edit/delete; tags.manage the tag editor
  const [canManage, setCanManage] = useState(false)
  const [canTag, setCanTag] = useState(false)
  useEffect(() => {
    setCanManage(hasCap('entities.manage'))
    setCanTag(hasCap('tags.manage'))
  }, [])

  const loadTags = useCallback(async () => {
    try {
      const [defs, rows] = await Promise.all([fetchTags('entity'), fetchTagAssignments('entity')])
      setEntityTags(defs.tags)
      const map = new Map<number, TagValueItem[]>()
      for (const a of rows.assignments)
        map.set(a.target_id, [...(map.get(a.target_id) ?? []), { tag_id: a.tag_id, values: a.values ?? {} }])
      setAssigned(map)
    } catch {
      setEntityTags(null) // no tags.view — the column and editor simply hide
    }
  }, [])

  const refresh = useCallback(async () => {
    try {
      const b = await v1<{ entities: EntityRow[] }>('entities')
      setEntities(b.entities)
      // keep the global topbar switcher in step — a newly created/deleted
      // entity shows up there without a page refresh
      reloadGlobalEntities()
    } catch {
      setEntities(null)
    }
    void loadTags()
  }, [reloadGlobalEntities, loadTags])

  useEffect(() => {
    refresh()
  }, [refresh])

  const ordered = useMemo(() => orderEntityTree(entities ?? []), [entities])
  const depthOf = useMemo(() => new Map(ordered.map((r) => [r.row.id, r.depth])), [ordered])
  const tagById = useMemo(() => new Map((entityTags ?? []).map((tg) => [tg.id, tg])), [entityTags])

  if (!ready) return null // access resolves after mount — don't flash the denial

  if (!can('entities.view') && !can('entities.manage')) {
    return (
      <div className="flex items-center gap-2 py-6 text-[13px] text-text-muted">
        <ShieldAlert size={15} className="text-[color:var(--amber-ink)]" aria-hidden />
        {t('admin.entities.adminsOnly')}
      </div>
    )
  }

  if (entities === null) {
    return <div className="py-6 text-[13px] text-text-muted">{t('admin.entities.unavailable')}</div>
  }

  // the parent picker: the shared tree picker over THIS page's rows, minus the
  // entity being edited and everything beneath it (no loops)
  const blocked = editing ? descendantIds(entities, editing.id) : new Set<number>()
  const bySlugOf = new Map(entities.map((e) => [e.id, e.slug]))
  const parentChoices: Entity[] = entities
    .filter((e) => !blocked.has(e.id))
    .map((e) => ({
      slug: e.slug,
      name: e.name,
      tenant: e.tenant,
      parent: e.parent_id !== null ? bySlugOf.get(e.parent_id) ?? null : null,
    }))

  const openCreate = () => {
    setEditing(null)
    setName('')
    setSlug('')
    setSlugTouched(false)
    // default under the first main entity — the common case is adding a branch
    setParentId(entities.find((e) => e.parent_id === null)?.id ?? null)
    setActive(true)
    setTagDraft([])
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
    setTagDraft(assigned.get(b.id) ?? [])
    setMsg(null)
    setOpen(true)
  }

  const saveEntity = async () => {
    setBusy(true)
    setMsg(null)
    try {
      let id: number
      if (editing) {
        await v1(`entities/${editing.id}`, {
          method: 'PATCH',
          body: JSON.stringify({
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            parent_id: parentId ?? 0, // 0 = promote to a main entity
            is_active: active,
          }),
        })
        id = editing.id
      } else {
        const created = await v1<EntityRow>('entities', {
          method: 'POST',
          body: JSON.stringify({
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            // The tenant comes from the parent server-side (or the caller's own
            // for a main entity) — it is never chosen here.
            parent_id: parentId ?? 0,
          }),
        })
        id = created.id
      }
      const before = JSON.stringify(editing ? assigned.get(editing.id) ?? [] : [])
      if (canTag && entityTags && JSON.stringify(tagDraft) !== before) {
        try {
          await saveEntityTags(id, tagDraft)
        } catch (e) {
          // the entity itself saved — say so, and keep the modal for the tags
          setMsg({ ok: false, text: t('admin.entities.tagsNotSaved', { error: e instanceof Error ? e.message : String(e) }) })
          // the entity exists now — keep the modal open on it, in edit mode
          setEditing({
            id,
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            tenant: editing?.tenant ?? '',
            parent_id: parentId,
            is_active: active,
            created_at: editing?.created_at ?? new Date().toISOString(),
          })
          await refresh()
          return
        }
      }
      setMsg({
        ok: true,
        text: editing
          ? t('admin.entities.updated', { name: name.trim() })
          : t('admin.entities.created', { name: name.trim() }),
      })
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
    if (!window.confirm(t('admin.entities.confirmDelete', { name: b.name }))) return
    setBusy(true)
    setMsg(null)
    try {
      // a 409 (entity still in use) carries the reason in the v1 error envelope
      await v1(`entities/${b.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: t('admin.entities.deleted', { name: b.name }) })
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
      header: t('common.fields.entity'),
      sortable: true,
      value: (b) => `${b.name} ${b.slug}`,
      render: (b) => {
        const depth = depthOf.get(b.id) ?? 0
        return (
          <div className="flex min-w-0 items-center gap-2" style={{ paddingLeft: depth * 20 }}>
            {depth > 0 ? (
              <CornerDownRight size={13} className="shrink-0 text-text-faint" aria-hidden />
            ) : (
              <MainBadge />
            )}
            <span className={`truncate text-[13.5px] ${depth === 0 ? 'font-bold text-text-heading' : 'font-semibold text-text-high'}`}>
              {b.name}
            </span>
            <code className="shrink-0 rounded bg-fill px-1.5 py-px text-[11px] text-text-muted">{b.slug}</code>
            {!b.is_active && (
              <span className="shrink-0 rounded-full bg-red/10 px-2 py-px text-[10.5px] font-semibold text-[color:var(--red-ink)]">
                {t('common.status.inactive')}
              </span>
            )}
          </div>
        )
      },
    },
    {
      key: 'parent',
      header: t('admin.entities.parent'),
      sortable: true,
      width: 200,
      value: (b) => b.parent_name || b.parent || '',
      render: (b) =>
        b.parent_id === null ? (
          <span className="text-[12.5px] text-text-muted">{t('admin.entities.isMain')}</span>
        ) : (
          <span className="text-[12.5px] text-text-mid">{b.parent_name || b.parent}</span>
        ),
    },
    ...(entityTags
      ? [
          {
            key: 'tags',
            header: t('common.fields.tags'),
            width: 230,
            value: (b: EntityRow) =>
              (assigned.get(b.id) ?? []).map((a) => tagById.get(a.tag_id)?.name ?? '').join(' '),
            render: (b: EntityRow) => {
              const items = (assigned.get(b.id) ?? []).filter((a) => tagById.has(a.tag_id))
              return items.length === 0 ? (
                <span className="text-[12px] text-text-faint">—</span>
              ) : (
                <div className="flex flex-wrap gap-1">
                  {items.map((a) => {
                    const tg = tagById.get(a.tag_id)!
                    return <TagChip key={a.tag_id} name={tg.name} color={tg.color} size="sm" />
                  })}
                </div>
              )
            },
          },
        ]
      : []),
    {
      key: 'created_at',
      header: t('common.fields.created'),
      sortable: true,
      width: 120,
      value: (b) => b.created_at,
      render: (b) => <span className="text-[12px] text-text-muted">{f.date(b.created_at)}</span>,
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: t('admin.users.col.actions'),
            srHeader: true,
            align: 'right' as const,
            width: 90,
            render: (b: EntityRow) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(b)}
                  disabled={busy}
                  title={t('admin.entities.editTitle', { name: b.name })}
                  aria-label={t('admin.entities.editAria', { name: b.name })}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Pencil size={14} aria-hidden />
                </button>
                <button
                  onClick={() => deleteEntity(b)}
                  disabled={busy}
                  title={t('admin.entities.deleteTitle', { name: b.name })}
                  aria-label={t('admin.entities.deleteAria', { name: b.name })}
                  className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/10 hover:text-[color:var(--red-ink)] disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Trash2 size={14} aria-hidden />
                </button>
              </div>
            ),
          },
        ]
      : []),
  ]

  const mains = entities.filter((e) => e.parent_id === null).length

  return (
    <div className="flex flex-col gap-5 pt-4">
      <p data-tour="entities-intro" className="max-w-[760px] text-[13px] leading-relaxed text-text-mid">
        <Trans
          k="admin.entities.intro"
          values={{
            main: <b>{t('admin.entities.introMain')}</b>,
            beneath: <b>{t('admin.entities.introBeneath')}</b>,
          }}
        />
      </p>

      <div data-tour="entities-counts" className="flex flex-wrap gap-x-6 gap-y-1 text-[12.5px] text-text-muted">
        <span>
          <b className="text-text-heading">{f.number(mains)}</b> {t('admin.entities.mainCount', { count: mains })}
        </span>
        <span>
          <b className="text-text-heading">{f.number(entities.length - mains)}</b>{' '}
          {t('admin.entities.subCount', { count: entities.length - mains })}
        </span>
      </div>

      {/* Success (and failures raised outside the modal) — the modal renders
          its own copy of failures so they aren't lost behind the scrim. */}
      {msg && (!open || msg.ok) && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`text-[12.5px] font-semibold ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
        >
          {msg.text}
        </div>
      )}

      <DataTable
        data-tour="entities-table"
        columns={columns}
        rows={ordered.map((o) => o.row)}
        rowKey={(b) => b.id}
        searchKeys={['name', 'parent', 'tags']}
        searchPlaceholder={t('admin.bindings.entityGrid.search')}
        empty={t('admin.entities.empty')}
        pageSize={25}
        toolbarRight={
          canManage ? (
            <Button data-tour="entities-add" onClick={openCreate}>
              <Plus size={14} aria-hidden /> {t('admin.entities.add')}
            </Button>
          ) : undefined
        }
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? t('admin.entities.editTitle', { name: editing.name }) : t('admin.entities.add')}
        subtitle={t('admin.entities.modalSubtitle')}
        size="lg"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={saveEntity} disabled={busy || !name.trim()}>
              {busy ? (
                <Loader2 size={13} className="animate-spin" aria-hidden />
              ) : editing ? (
                <Pencil size={13} aria-hidden />
              ) : (
                <Plus size={13} aria-hidden />
              )}{' '}
              {editing ? t('settings.common.saveChanges') : t('admin.entities.add')}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          {msg && !msg.ok && (
            <div
              role="alert"
              className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]"
            >
              {msg.text}
            </div>
          )}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block sm:col-span-2">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.entities.name')}</span>
              <input
                type="text"
                value={name}
                onChange={(e) => {
                  setName(e.target.value)
                  if (!slugTouched) setSlug(slugify(e.target.value))
                }}
                placeholder={t('admin.entities.namePlaceholder')}
                className={inputCls}
              />
            </label>
            <div className="sm:col-span-2">
              <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.entities.sitsUnder')}</span>
              <EntityPicker
                value={parentId === null ? null : (bySlugOf.get(parentId) ?? null)}
                onChange={(s) => setParentId(s ? (entities.find((e) => e.slug === s)?.id ?? null) : null)}
                entities={parentChoices}
                globalLabel={t('admin.entities.noParent')}
                ariaLabel={t('admin.entities.parentAria')}
              />
              <span className="mt-1 block text-[11px] leading-relaxed text-text-muted">{t('admin.entities.parentHint')}</span>
            </div>
            {editing && (
              <div className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2.5 sm:col-span-2">
                <div>
                  <div className="text-[13px] font-semibold text-text-high">{t('common.status.active')}</div>
                  <div className="text-[11.5px] text-text-muted">{t('admin.entities.activeHint')}</div>
                </div>
                <Toggle on={active} onChange={setActive} label={t('common.status.active')} />
              </div>
            )}
          </div>

          {(() => {
            // the slug follows the name (or keeps the stored one) and tags are
            // optional — open when the slug was changed by hand, a tag is
            // picked, or the last save failed
            const slugCustom = !!slug && slug !== (editing ? editing.slug : slugify(name))
            const advancedCount = [slugCustom || (!!name.trim() && !slug), tagDraft.length > 0].filter(Boolean).length
            return (
              <Advanced
                key={editing?.id ?? 'new'}
                count={advancedCount}
                forceOpen={advancedCount > 0 || (!!msg && !msg.ok)}
              >
                <label className="block sm:max-w-[50%]">
                  <span className="mb-1.5 block text-[11.5px] font-bold text-text-high">{t('admin.entities.slug')}</span>
                  <input
                    type="text"
                    value={slug}
                    onChange={(e) => {
                      setSlugTouched(true)
                      setSlug(slugify(e.target.value))
                    }}
                    placeholder={t('admin.entities.slugPlaceholder')}
                    className={inputCls}
                  />
                  <span className="mt-1 block text-[11px] leading-relaxed text-text-muted">{t('admin.entities.slugHint')}</span>
                </label>
                {entityTags && (
                  <div>
                    <span className="block text-[11.5px] font-bold text-text-high">{t('common.fields.tags')}</span>
                    <span className="mb-2.5 mt-0.5 block text-[11px] leading-relaxed text-text-muted">
                      {t('admin.entities.tagsHint')}
                    </span>
                    {entityTags.length === 0 ? (
                      <p className="text-[12px] text-text-muted">{t('admin.entities.noTags')}</p>
                    ) : (
                      <TagAssignEditor
                        available={entityTags}
                        value={tagDraft}
                        onChange={setTagDraft}
                        disabled={!canTag || busy}
                        noun="entity"
                      />
                    )}
                  </div>
                )}
              </Advanced>
            )
          })()}
        </div>
      </Modal>
    </div>
  )
}
