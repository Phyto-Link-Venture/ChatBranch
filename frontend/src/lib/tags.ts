/**
 * The tag registry client (backend: /api/tags, /api/contacts/<id>/tags,
 * /api/entities/<slug>/tags).
 *
 * One vocabulary for three kinds of target:
 *   contact       — boss / employee / driver…   (written through to Contact.tags)
 *   entity        — inner company / outside company…
 *   conversation  — follow-up / urgent / resolved…  (stored on Thread.labels as slugs)
 *
 * A tag's colour is a PALETTE KEY, never a hex: `tagColor(key)` maps it to
 * theme tokens so it flips with Paper & Ink / Graphite / Soft and light/dark.
 *
 * Compatibility: `CONVERSATION_TAGS` and `tagMeta()` keep their old shapes for
 * the inbox. `tagMeta()` prefers the live registry once any component has
 * mounted `useTags('conversation')`; the static list is only the offline
 * fallback.
 */
import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import type { MessageKey, TFunction } from '@/i18n'

export type TagAppliesTo = 'contact' | 'entity' | 'conversation'
export type TagColor = 'tangerine' | 'ink' | 'teal' | 'plum' | 'ochre' | 'blue' | 'green' | 'rose'
export type TagAttrType = 'text' | 'number' | 'date' | 'select' | 'boolean' | 'phone' | 'email'

export interface TagAttribute {
  key: string
  label: string
  type: TagAttrType
  options: string[]
  required: boolean
}

export interface TagDef {
  id: number
  name: string
  slug: string
  color: TagColor | string
  description: string
  applies_to: TagAppliesTo
  /** Exclusive group ('' = none): one tag per group per target. */
  group: string
  attributes: TagAttribute[]
  position: number
  /** Owning entity slug, null = organization-wide. */
  entity: string | null
  entity_id: number | null
  entity_name: string
  usage: number
  /** `tag_<slug>_` — prefix of this tag's template variables ('' for conversation tags). */
  variable_prefix: string
  created_at: string
  updated_at: string
}

/** One tag as it sits on a contact or entity, with its attribute values. */
export interface AssignedTag {
  tag_id: number
  name: string
  slug: string
  color: string
  group: string
  description: string
  attributes: TagAttribute[]
  values: Record<string, unknown>
  missing_required: string[]
  created_by: string
  created_at: string
}

// ── palette ──────────────────────────────────────────────────────────────────

/** `ink` = readable text colour, `dot` = the fill used for dots / tints,
 *  `label` = the colour's name as a translation key (render with `t(p.label)`). */
export const TAG_PALETTE: { key: TagColor; label: MessageKey; ink: string; dot: string }[] = [
  { key: 'tangerine', label: 'admin.tags.palette.tangerine', ink: 'var(--brand-ink)', dot: 'rgb(var(--c-brand))' },
  { key: 'ink', label: 'admin.tags.palette.ink', ink: 'var(--text-high)', dot: 'var(--text-mid)' },
  { key: 'teal', label: 'admin.tags.palette.teal', ink: 'var(--cyan-ink)', dot: 'rgb(var(--c-cyan))' },
  { key: 'plum', label: 'admin.tags.palette.plum', ink: 'var(--purple-ink)', dot: 'rgb(var(--c-purple))' },
  { key: 'ochre', label: 'admin.tags.palette.ochre', ink: 'var(--amber-ink)', dot: 'rgb(var(--c-amber))' },
  { key: 'blue', label: 'admin.tags.palette.blue', ink: 'var(--blue-ink)', dot: 'rgb(var(--c-blue))' },
  { key: 'green', label: 'admin.tags.palette.green', ink: 'var(--green-ink)', dot: 'rgb(var(--c-green))' },
  { key: 'rose', label: 'admin.tags.palette.rose', ink: 'var(--red-ink)', dot: 'rgb(var(--c-red))' },
]

const BY_COLOR = new Map(TAG_PALETTE.map((p) => [p.key as string, p]))

export function tagColor(key: string | null | undefined): { ink: string; dot: string; label: MessageKey } {
  return BY_COLOR.get(key ?? '') ?? BY_COLOR.get('ink')!
}

/** What each target type is called — translation keys (render with `t(…)`);
 *  `count` is a plural key: `t(meta.count, { count })` → "3 contacts". */
export const APPLIES_TO_META: Record<
  TagAppliesTo,
  { label: MessageKey; title: MessageKey; noun: MessageKey; plural: MessageKey; count: MessageKey; blurb: MessageKey }
> = {
  contact: {
    label: 'admin.tags.kinds.contact.label',
    title: 'admin.tags.kinds.contact.title',
    noun: 'admin.tags.kinds.contact.noun',
    plural: 'admin.tags.kinds.contact.plural',
    count: 'admin.tags.kinds.contact.count',
    blurb: 'admin.tags.kinds.contact.blurb',
  },
  entity: {
    label: 'admin.tags.kinds.entity.label',
    title: 'admin.tags.kinds.entity.title',
    noun: 'admin.tags.kinds.entity.noun',
    plural: 'admin.tags.kinds.entity.plural',
    count: 'admin.tags.kinds.entity.count',
    blurb: 'admin.tags.kinds.entity.blurb',
  },
  conversation: {
    label: 'admin.tags.kinds.conversation.label',
    title: 'admin.tags.kinds.conversation.title',
    noun: 'admin.tags.kinds.conversation.noun',
    plural: 'admin.tags.kinds.conversation.plural',
    count: 'admin.tags.kinds.conversation.count',
    blurb: 'admin.tags.kinds.conversation.blurb',
  },
}

/** Attribute field types → translation keys (the stored type stays the id). */
export const ATTR_TYPE_LABELS: Record<TagAttrType, MessageKey> = {
  text: 'admin.tags.attrTypes.text',
  number: 'admin.tags.attrTypes.number',
  date: 'admin.tags.attrTypes.date',
  select: 'admin.tags.attrTypes.select',
  boolean: 'admin.tags.attrTypes.boolean',
  phone: 'admin.tags.attrTypes.phone',
  email: 'admin.tags.attrTypes.email',
}

/** "Licence no." → "licence_no" — mirrors TagService.AttributeKey. */
export const attributeKey = (label: string) =>
  label
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 60)

/** A stored attribute value the way lists show it (pass `t` for a translated Yes / No). */
export function formatTagValue(attr: TagAttribute, value: unknown, t?: TFunction): string {
  if (value === null || value === undefined || value === '') return ''
  if (attr.type === 'boolean') {
    const yes = value === true || value === 'true'
    if (t) return yes ? t('common.status.yes') : t('common.status.no')
    return yes ? 'Yes' : 'No'
  }
  return String(value)
}

// ── API ──────────────────────────────────────────────────────────────────────

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    const err = new Error(d.detail || d.error?.message || `HTTP ${res.status}`) as Error & { status?: number }
    err.status = res.status
    throw err
  }
  return data as T
}

export interface TagWrite {
  name?: string
  color?: string
  description?: string
  applies_to?: TagAppliesTo
  group?: string
  attributes?: TagAttribute[]
  entity?: string | null
  position?: number
}

export const fetchTags = (appliesTo?: TagAppliesTo, entity?: string | null) => {
  const qs = new URLSearchParams()
  if (appliesTo) qs.set('applies_to', appliesTo)
  if (entity) qs.set('entity', entity)
  const q = qs.toString()
  return call<{ tags: TagDef[]; palette: string[]; attribute_types: string[] }>(`tags${q ? `?${q}` : ''}`)
}
export const createTag = (body: TagWrite) => call<TagDef>('tags', { method: 'POST', body: JSON.stringify(body) })
export const updateTag = (id: number, body: TagWrite) =>
  call<TagDef>(`tags/${id}`, { method: 'PATCH', body: JSON.stringify(body) })
export const deleteTag = (id: number, force = false) =>
  call<{ ok: boolean; removed_from: number }>(`tags/${id}${force ? '?force=1' : ''}`, { method: 'DELETE' })

export const fetchTagTargets = (id: number) =>
  call<{ tag: TagDef; target_type: TagAppliesTo; target_ids: number[]; items: { target_id: number; values: Record<string, unknown> }[] }>(
    `tags/${id}/targets`,
  )
export const applyTagTargets = (id: number, add: number[], remove: number[]) =>
  call<{ added: number; removed: number; target_ids: number[] }>(`tags/${id}/targets`, {
    method: 'POST',
    body: JSON.stringify({ add, remove }),
  })

export const fetchTagAssignments = (targetType: 'contact' | 'entity') =>
  call<{ assignments: { tag_id: number; target_id: number; values: Record<string, unknown> }[] }>(
    `tags/assignments?target_type=${targetType}`,
  )

export type TagItemWrite = { tag_id: number; values?: Record<string, unknown> }

export const fetchContactTags = (contactId: number) =>
  call<{ contact_id: number; tags: AssignedTag[] }>(`contacts/${contactId}/tags`)
export const saveContactTags = (contactId: number, tags: TagItemWrite[]) =>
  call<{ contact_id: number; tags: AssignedTag[] }>(`contacts/${contactId}/tags`, {
    method: 'PUT',
    body: JSON.stringify({ tags }),
  })

export const fetchEntityTags = (slugOrId: string | number) =>
  call<{ entity: string; entity_id: number; tags: AssignedTag[] }>(`entities/${slugOrId}/tags`)
export const saveEntityTags = (slugOrId: string | number, tags: TagItemWrite[]) =>
  call<{ entity: string; entity_id: number; tags: AssignedTag[] }>(`entities/${slugOrId}/tags`, {
    method: 'PUT',
    body: JSON.stringify({ tags }),
  })

// ── conversation tags (inbox compatibility) ──────────────────────────────────

export interface TagMeta {
  slug: string
  label: string
  /** A CSS colour (token-backed) — safe for `tint()` and inline backgrounds. */
  color: string
}

const metaOf = (slug: string, label: string, color: string): TagMeta => ({ slug, label, color: tagColor(color).dot })

/** Offline fallback — the registry seeds exactly these (TagService.ConversationDefaults). */
export const CONVERSATION_TAGS: TagMeta[] = [
  metaOf('follow-up', 'Follow-up needed', 'green'),
  metaOf('resolved', 'Resolved', 'green'),
  metaOf('in-progress', 'In progress', 'teal'),
  metaOf('urgent', 'Urgent', 'ochre'),
  metaOf('dispute', 'Dispute', 'rose'),
  metaOf('declined', 'Declined', 'rose'),
  metaOf('callback', 'Callback requested', 'tangerine'),
  metaOf('no-answer', 'No answer', 'ink'),
  metaOf('wrong-number', 'Wrong number', 'ink'),
  metaOf('escalate', 'Escalate', 'plum'),
]

const FALLBACK = new Map(CONVERSATION_TAGS.map((t) => [t.slug, t]))
/** Filled by useTags('conversation') — the live registry wins over the fallback. */
let liveConversation: Map<string, TagMeta> | null = null

export const toTagMeta = (t: Pick<TagDef, 'slug' | 'name' | 'color'>): TagMeta => metaOf(t.slug, t.name, t.color)

/** Registry lookup with a graceful fallback for any legacy free-text label. */
export const tagMeta = (slug: string): TagMeta =>
  liveConversation?.get(slug) ?? FALLBACK.get(slug) ?? { slug, label: slug, color: 'var(--text-muted)' }

/**
 * The registry for one target type (or all), loaded after mount.
 * `metas` is the conversation-compatible {slug,label,color} list — for
 * conversation tags it falls back to CONVERSATION_TAGS until the API answers
 * (or when it is unreachable), so the inbox never renders an empty tag row.
 */
export function useTags(appliesTo?: TagAppliesTo, entity?: string | null) {
  const [tags, setTags] = useState<TagDef[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      const d = await fetchTags(appliesTo, entity)
      setTags(d.tags)
      setError(null)
      if (appliesTo === 'conversation' && !entity)
        liveConversation = new Map(d.tags.map((t) => [t.slug, toTagMeta(t)]))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [appliesTo, entity])

  useEffect(() => {
    void reload()
  }, [reload])

  const metas = useMemo<TagMeta[]>(
    () => (tags ? tags.map(toTagMeta) : appliesTo === 'conversation' ? CONVERSATION_TAGS : []),
    [tags, appliesTo],
  )
  const lookup = useCallback(
    (slug: string) => metas.find((m) => m.slug === slug) ?? tagMeta(slug),
    [metas],
  )

  return { tags: tags ?? [], metas, tagMeta: lookup, loading: tags === null && !error, error, reload }
}
