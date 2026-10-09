'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import { Layers, Loader2, Pencil, Plus, Search, Trash2, Users } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { hasCap } from '@/lib/auth'
import { Trans, useT } from '@/i18n'
import {
  APPLIES_TO_META,
  ATTR_TYPE_LABELS,
  deleteTag,
  fetchTags,
  type TagAppliesTo,
  type TagDef,
} from '@/lib/tags'
import { TagChip } from './TagChip'
import { TagEditorModal } from './TagEditorModal'
import { TagBindingView } from './TagBindingView'

type Filter = 'all' | TagAppliesTo
const KINDS: TagAppliesTo[] = ['contact', 'entity', 'conversation']

/**
 * Library → Tags. The registry grouped by what a tag applies to, then by its
 * exclusive group; each row shows the chip, its fields, its scope and how many
 * targets carry it. "Tagged" opens the binding view for that tag.
 */
export function TagsManager() {
  const t = useT()
  const [tags, setTags] = useState<TagDef[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [filter, setFilter] = useState<Filter>('all')
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<TagDef | null>(null)
  const [creating, setCreating] = useState<TagAppliesTo | null>(null)
  const [viewing, setViewing] = useState<number | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState<number | null>(null)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('tags.manage')), [])

  const load = useCallback(async () => {
    try {
      const d = await fetchTags()
      setTags(d.tags)
      setLoadError(null)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
      setTags((cur) => cur ?? [])
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // deep link: /tags?tag=<slug> opens that tag's "Tagged" view (first load only)
  const deepLinked = useRef(false)
  useEffect(() => {
    if (!tags || deepLinked.current) return
    deepLinked.current = true
    const slug = new URLSearchParams(window.location.search).get('tag')
    const hit = slug ? tags.find((x) => x.slug === slug) : undefined
    if (hit && hit.applies_to !== 'conversation') setViewing(hit.id)
  }, [tags])

  const openView = (tag: TagDef | null) => {
    setViewing(tag?.id ?? null)
    try {
      const url = new URL(window.location.href)
      if (tag) url.searchParams.set('tag', tag.slug)
      else url.searchParams.delete('tag')
      window.history.replaceState(null, '', url.toString())
    } catch {
      /* ignore */
    }
  }

  const groupsByKind = useMemo(() => {
    const out: Record<TagAppliesTo, string[]> = { contact: [], entity: [], conversation: [] }
    for (const tg of tags ?? []) if (tg.group && !out[tg.applies_to].includes(tg.group)) out[tg.applies_to].push(tg.group)
    return out
  }, [tags])

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (tags ?? []).filter(
      (tg) =>
        (filter === 'all' || tg.applies_to === filter) &&
        (!q || `${tg.name} ${tg.group} ${tg.description} ${tg.slug}`.toLowerCase().includes(q)),
    )
  }, [tags, filter, query])

  const remove = async (tag: TagDef) => {
    if (!window.confirm(t('admin.tags.manager.confirmDelete', { name: tag.name }))) return
    setBusy(tag.id)
    setMsg(null)
    try {
      try {
        await deleteTag(tag.id)
      } catch (e) {
        const status = (e as { status?: number }).status
        if (status !== 409) throw e
        if (!window.confirm(t('admin.tags.manager.confirmForce', { reason: (e as Error).message }))) return
        await deleteTag(tag.id, true)
      }
      setMsg({ ok: true, text: t('admin.tags.manager.deleted', { name: tag.name }) })
      await load()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  const viewed = viewing != null ? tags?.find((tg) => tg.id === viewing) : undefined
  if (viewed) {
    return (
      <div className="pt-4">
        <TagBindingView tag={viewed} canManage={canManage} onBack={() => openView(null)} onChanged={() => void load()} />
      </div>
    )
  }

  const counts = (k: Filter) => (k === 'all' ? tags?.length ?? 0 : (tags ?? []).filter((tg) => tg.applies_to === k).length)

  return (
    <div className="flex flex-col gap-6 pt-4">
      <p className="max-w-[760px] text-[13px] leading-relaxed text-text-mid">
        <Trans
          k="admin.tags.manager.intro"
          values={{
            is: <i>{t('admin.tags.manager.introIs')}</i>,
            driver: <b>{t('admin.tags.manager.introDriver')}</b>,
            outside: <b>{t('admin.tags.manager.introOutside')}</b>,
            followUp: <b>{t('admin.tags.manager.introFollowUp')}</b>,
            group: <b>{t('admin.tags.manager.introGroup')}</b>,
            fields: <b>{t('admin.tags.manager.introFields')}</b>,
          }}
        />
      </p>

      {/* toolbar */}
      <div className="flex flex-wrap items-center gap-2">
        <div role="tablist" aria-label={t('admin.tags.manager.show')} className="flex flex-wrap rounded-lg border border-line-strong bg-card p-0.5">
          {(['all', ...KINDS] as Filter[]).map((k) => (
            <button
              key={k}
              role="tab"
              aria-selected={filter === k}
              onClick={() => setFilter(k)}
              className={`flex h-8 items-center gap-1.5 rounded-md px-3 text-[12.5px] font-semibold transition-colors ${
                filter === k ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
              }`}
            >
              {k === 'all' ? t('common.status.all') : t(APPLIES_TO_META[k].label)}
              <span className={`text-[11px] ${filter === k ? 'opacity-80' : 'text-text-faint'}`}>{counts(k)}</span>
            </button>
          ))}
        </div>
        <label className="flex h-9 min-w-[180px] flex-1 items-center gap-2 rounded-lg border border-line-strong bg-card px-2.5">
          <Search size={14} className="shrink-0 text-text-muted" aria-hidden />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('admin.tags.manager.searchPlaceholder')}
            aria-label={t('admin.tags.manager.searchAria')}
            className="min-w-0 flex-1 bg-transparent text-[13px] text-text-primary outline-none placeholder:text-text-faint"
          />
        </label>
        {canManage && (
          <Button onClick={() => setCreating(filter === 'all' ? 'contact' : filter)}>
            <Plus size={14} aria-hidden /> {t('admin.tags.manager.newTag')}
          </Button>
        )}
      </div>

      {msg && (
        <div
          role={msg.ok ? 'status' : 'alert'}
          className={`text-[12.5px] font-semibold ${msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}`}
        >
          {msg.text}
        </div>
      )}
      {loadError && (
        <div className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]">
          {t('admin.tags.manager.loadError', { error: loadError })}
        </div>
      )}

      {tags === null ? (
        <div className="flex items-center gap-2 py-8 text-[13px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('admin.tags.manager.loading')}
        </div>
      ) : (
        KINDS.filter((k) => filter === 'all' || filter === k).map((k) => (
          <KindSection
            key={k}
            kind={k}
            tags={shown.filter((tg) => tg.applies_to === k)}
            total={counts(k)}
            searching={!!query.trim()}
            canManage={canManage}
            busy={busy}
            onCreate={() => setCreating(k)}
            onEdit={setEditing}
            onDelete={remove}
            onView={openView}
          />
        ))
      )}

      <TagEditorModal
        open={creating !== null || editing !== null}
        tag={editing}
        defaultAppliesTo={creating ?? 'contact'}
        groups={groupsByKind}
        onClose={() => {
          setCreating(null)
          setEditing(null)
        }}
        onSaved={(tg) => {
          setMsg({
            ok: true,
            text: editing ? t('admin.tags.manager.savedTag', { name: tg.name }) : t('admin.tags.manager.createdTag', { name: tg.name }),
          })
          setCreating(null)
          setEditing(null)
          void load()
        }}
      />
    </div>
  )
}

function KindSection({
  kind,
  tags,
  total,
  searching,
  canManage,
  busy,
  onCreate,
  onEdit,
  onDelete,
  onView,
}: {
  kind: TagAppliesTo
  tags: TagDef[]
  total: number
  searching: boolean
  canManage: boolean
  busy: number | null
  onCreate: () => void
  onEdit: (t: TagDef) => void
  onDelete: (t: TagDef) => void
  onView: (t: TagDef) => void
}) {
  const t = useT()
  const meta = APPLIES_TO_META[kind]
  // exclusive groups first (alphabetical), loose tags last
  const groups = useMemo(() => {
    const names = [...new Set(tags.map((tg) => tg.group))].sort((a, b) => (a ? (b ? a.localeCompare(b) : -1) : 1))
    return names.map((g) => ({ name: g, tags: tags.filter((tg) => tg.group === g) }))
  }, [tags])

  return (
    <section aria-labelledby={`tags-${kind}`} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end justify-between gap-2 border-b border-line pb-2">
        <div className="min-w-0">
          <h2 id={`tags-${kind}`} className="display text-[22px] leading-tight text-text-heading">
            {t(meta.title)}
          </h2>
          <p className="mt-0.5 text-[12.5px] text-text-muted">{t(meta.blurb)}</p>
        </div>
        <span className="eyebrow">{t('admin.tags.manager.tagCount', { count: total })}</span>
      </div>

      {tags.length === 0 ? (
        <div className="rounded-xl border border-dashed border-line-strong px-4 py-7 text-center text-[13px] text-text-muted">
          {searching ? (
            t('admin.tags.manager.noMatch')
          ) : canManage ? (
            <Trans
              k="admin.tags.manager.noneYetCreate"
              values={{
                noun: t(meta.noun),
                create: (
                  <button type="button" onClick={onCreate} className="font-semibold text-text-high hover:underline">
                    {t('admin.tags.manager.createOne')}
                  </button>
                ),
              }}
            />
          ) : (
            t('admin.tags.manager.noneYet', { noun: t(meta.noun) })
          )}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-3">
          {groups.map((g) => (
            <Card key={g.name || '__loose'} className="overflow-hidden">
              <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3.5 py-2">
                {g.name ? (
                  <>
                    <Layers size={13} className="text-text-muted" aria-hidden />
                    <span className="text-[13px] font-bold text-text-heading">{g.name}</span>
                    <span className="rounded-full border border-line-strong px-2 py-px text-[10.5px] font-semibold text-text-mid">
                      {t('admin.tags.manager.pickOne')}
                    </span>
                  </>
                ) : (
                  <span className="text-[12px] font-semibold text-text-muted">{groups.length > 1 ? t('admin.bindings.ungrouped') : t('common.fields.tags')}</span>
                )}
                <span className="ml-auto text-[11.5px] text-text-muted">{g.tags.length}</span>
              </div>
              <ul>
                {g.tags.map((tg) => (
                  <TagRow
                    key={tg.id}
                    tag={tg}
                    canManage={canManage}
                    busy={busy === tg.id}
                    onEdit={() => onEdit(tg)}
                    onDelete={() => onDelete(tg)}
                    onView={() => onView(tg)}
                  />
                ))}
              </ul>
            </Card>
          ))}
        </div>
      )}
    </section>
  )
}

function TagRow({
  tag,
  canManage,
  busy,
  onEdit,
  onDelete,
  onView,
}: {
  tag: TagDef
  canManage: boolean
  busy: boolean
  onEdit: () => void
  onDelete: () => void
  onView: () => void
}) {
  const t = useT()
  const meta = APPLIES_TO_META[tag.applies_to]
  const usage = t(meta.count, { count: tag.usage })
  return (
    <li className="grid grid-cols-1 gap-2 border-b border-line px-3.5 py-3 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_150px_minmax(190px,auto)] md:items-center md:gap-4">
      <div className="min-w-0">
        <TagChip name={tag.name} color={tag.color} />
        {tag.description && <p className="mt-1 line-clamp-2 text-[12px] text-text-muted">{tag.description}</p>}
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-1.5">
        {tag.attributes.length === 0 ? (
          <span className="text-[11.5px] text-text-faint">{t('admin.tags.manager.noFields')}</span>
        ) : (
          tag.attributes.slice(0, 4).map((a) => (
            <span
              key={a.key}
              title={
                ATTR_TYPE_LABELS[a.type]
                  ? a.required
                    ? t('admin.tags.manager.fieldTypeRequired', { type: t(ATTR_TYPE_LABELS[a.type]) })
                    : t(ATTR_TYPE_LABELS[a.type])
                  : a.type
              }
              className="rounded-md border border-line bg-well px-1.5 py-px text-[11px] text-text-mid"
            >
              {a.label}
              {a.required && <span className="text-[color:var(--red-ink)]">*</span>}
            </span>
          ))
        )}
        {tag.attributes.length > 4 && <span className="text-[11px] text-text-faint">+{tag.attributes.length - 4}</span>}
      </div>
      <span className="truncate text-[12px] text-text-mid" title={tag.entity_name || t('admin.bindings.orgWide')}>
        {tag.entity_name || t('admin.bindings.orgWide')}
      </span>
      <div className="flex shrink-0 items-center gap-1 md:justify-end">
        {tag.applies_to === 'conversation' ? (
          <Link
            href="/chats"
            className="inline-flex h-8 items-center gap-1.5 rounded-lg px-2 text-[12px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
            title={t('admin.tags.manager.conversationHint')}
          >
            {usage}
          </Link>
        ) : (
          <Button variant="secondary" size="sm" onClick={onView} title={t('admin.tags.manager.seeWhich', { things: t(meta.plural) })}>
            <Users size={13} aria-hidden /> {usage}
          </Button>
        )}
        {canManage && (
          <>
            <button
              type="button"
              onClick={onEdit}
              disabled={busy}
              aria-label={t('admin.tags.manager.editAria', { name: tag.name })}
              title={t('common.actions.edit')}
              className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-40"
            >
              <Pencil size={14} aria-hidden />
            </button>
            <button
              type="button"
              onClick={onDelete}
              disabled={busy}
              aria-label={t('admin.tags.manager.deleteAria', { name: tag.name })}
              title={t('common.actions.delete')}
              className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-red/10 hover:text-[color:var(--red-ink)] disabled:opacity-40"
            >
              {busy ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <Trash2 size={14} aria-hidden />}
            </button>
          </>
        )}
      </div>
    </li>
  )
}
