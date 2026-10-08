'use client'

import { useEffect, useMemo, useState } from 'react'
import { ArrowDown, ArrowUp, Loader2, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import { TagListInput } from '@/components/ui/TagListInput'
import { EntityPicker } from '@/components/ui/EntityPicker'
import {
  APPLIES_TO_META,
  ATTR_TYPE_LABELS,
  TAG_PALETTE,
  attributeKey,
  createTag,
  tagColor,
  updateTag,
  type TagAppliesTo,
  type TagAttrType,
  type TagAttribute,
  type TagDef,
} from '@/lib/tags'
import { TagChip } from './TagChip'

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary'

const slugify = (s: string) =>
  s
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 60)

function FieldLabel({ children, hint }: { children: React.ReactNode; hint?: React.ReactNode }) {
  return (
    <div className="mb-1.5">
      <span className="block text-[11.5px] font-bold text-text-high">{children}</span>
      {hint && <span className="mt-0.5 block text-[11px] leading-relaxed text-text-muted">{hint}</span>}
    </div>
  )
}

/**
 * Create / edit one registry tag: name, colour (a palette key), what it
 * applies to, an optional exclusive group, the entity that owns it, and the
 * attribute fields its assignments carry. Errors render inside the modal.
 */
export function TagEditorModal({
  open,
  tag,
  defaultAppliesTo = 'contact',
  groups,
  onClose,
  onSaved,
}: {
  open: boolean
  /** null = create */
  tag: TagDef | null
  defaultAppliesTo?: TagAppliesTo
  /** Existing group names per target type — offered as one-click suggestions. */
  groups: Record<TagAppliesTo, string[]>
  onClose: () => void
  onSaved: (tag: TagDef) => void
}) {
  const [name, setName] = useState('')
  const [color, setColor] = useState('ink')
  const [appliesTo, setAppliesTo] = useState<TagAppliesTo>('contact')
  const [group, setGroup] = useState('')
  const [description, setDescription] = useState('')
  const [entity, setEntity] = useState<string | null>(null)
  const [attrs, setAttrs] = useState<TagAttribute[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setName(tag?.name ?? '')
    setColor(tag?.color ?? TAG_PALETTE[Math.floor(Math.random() * TAG_PALETTE.length)].key)
    setAppliesTo(tag?.applies_to ?? defaultAppliesTo)
    setGroup(tag?.group ?? '')
    setDescription(tag?.description ?? '')
    setEntity(tag?.entity ?? null)
    setAttrs(tag?.attributes.map((a) => ({ ...a, options: [...a.options] })) ?? [])
    setError(null)
  }, [open, tag, defaultAppliesTo])

  const locked = !!tag && tag.usage > 0
  const slug = tag?.slug ?? slugify(name)
  const varPrefix = `tag_${(slug || 'tag').replace(/-/g, '_')}_`
  const suggestions = useMemo(
    () => (groups[appliesTo] ?? []).filter((g) => g && g.toLowerCase() !== group.trim().toLowerCase()),
    [groups, appliesTo, group],
  )

  const patchAttr = (i: number, patch: Partial<TagAttribute>) =>
    setAttrs((cur) => cur.map((a, idx) => (idx === i ? { ...a, ...patch } : a)))
  const move = (i: number, dir: -1 | 1) =>
    setAttrs((cur) => {
      const j = i + dir
      if (j < 0 || j >= cur.length) return cur
      const next = [...cur]
      ;[next[i], next[j]] = [next[j], next[i]]
      return next
    })

  const save = async () => {
    setError(null)
    if (!name.trim()) {
      setError('Give the tag a name.')
      return
    }
    const cleaned = appliesTo === 'conversation' ? [] : attrs.map((a) => ({ ...a, label: a.label.trim(), key: a.key || attributeKey(a.label) }))
    const blank = cleaned.findIndex((a) => !a.label)
    if (blank >= 0) {
      setError(`Field #${blank + 1} needs a label.`)
      return
    }
    setBusy(true)
    try {
      const body = {
        name: name.trim(),
        color,
        applies_to: appliesTo,
        group: group.trim(),
        description: description.trim(),
        entity: entity ?? '',
        attributes: cleaned,
      }
      const saved = tag ? await updateTag(tag.id, body) : await createTag(body)
      onSaved(saved)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tag ? `Edit ${tag.name}` : 'New tag'}
      subtitle="A tag says what something is. Give it fields when each use needs details of its own."
      size="lg"
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button variant="primary" onClick={save} disabled={busy}>
            {busy && <Loader2 size={13} className="animate-spin" aria-hidden />}
            {tag ? 'Save tag' : 'Create tag'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {error && (
          <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]">
            {error}
          </div>
        )}

        {/* preview */}
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-dashed border-line-strong bg-well px-3.5 py-3">
          <span className="eyebrow">Preview</span>
          <TagChip name={name.trim() || 'Tag name'} color={color} />
          {group.trim() && <span className="text-[11.5px] text-text-muted">in group “{group.trim()}”</span>}
          {slug && <code className="ml-auto rounded bg-fill px-1.5 py-0.5 text-[11px] text-text-muted">{slug}</code>}
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <label className="block">
            <FieldLabel>Name</FieldLabel>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Driver"
              maxLength={80}
              className={inputCls}
            />
          </label>
          <div>
            <FieldLabel hint={locked ? `In use on ${tag!.usage} — remove it from them to change this.` : undefined}>
              Applies to
            </FieldLabel>
            <div role="radiogroup" aria-label="Applies to" className="flex rounded-lg border border-line-strong bg-card p-0.5">
              {(Object.keys(APPLIES_TO_META) as TagAppliesTo[]).map((k) => (
                <button
                  key={k}
                  type="button"
                  role="radio"
                  aria-checked={appliesTo === k}
                  disabled={locked}
                  onClick={() => setAppliesTo(k)}
                  className={`h-8 flex-1 rounded-md px-2 text-[12px] font-semibold transition-colors disabled:cursor-not-allowed ${
                    appliesTo === k ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover disabled:opacity-50'
                  }`}
                >
                  {APPLIES_TO_META[k].label}
                </button>
              ))}
            </div>
          </div>
        </div>

        <div>
          <FieldLabel>Colour</FieldLabel>
          <div role="radiogroup" aria-label="Colour" className="flex flex-wrap gap-2">
            {TAG_PALETTE.map((p) => {
              const on = color === p.key
              return (
                <button
                  key={p.key}
                  type="button"
                  role="radio"
                  aria-checked={on}
                  aria-label={p.label}
                  title={p.label}
                  onClick={() => setColor(p.key)}
                  className={`flex h-8 items-center gap-1.5 rounded-full border px-2.5 text-[11.5px] font-semibold transition-colors ${
                    on ? 'border-text-primary bg-fill-hover text-text-high' : 'border-line text-text-muted hover:border-line-strong'
                  }`}
                >
                  <span className="h-3 w-3 rounded-full" style={{ background: tagColor(p.key).dot }} aria-hidden />
                  {p.label}
                </button>
              )
            })}
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div>
            <FieldLabel hint="Tags in one group are exclusive: a target holds one of them (e.g. “Company type”: Inner / Outside).">
              Group <span className="font-normal text-text-muted">(optional)</span>
            </FieldLabel>
            <input
              value={group}
              onChange={(e) => setGroup(e.target.value)}
              placeholder="e.g. Role"
              maxLength={60}
              className={inputCls}
            />
            {suggestions.length > 0 && (
              <div className="mt-1.5 flex flex-wrap gap-1">
                {suggestions.slice(0, 6).map((g) => (
                  <button
                    key={g}
                    type="button"
                    onClick={() => setGroup(g)}
                    className="rounded-full border border-line px-2 py-0.5 text-[11px] font-semibold text-text-mid hover:border-line-strong hover:text-text-high"
                  >
                    {g}
                  </button>
                ))}
              </div>
            )}
          </div>
          <div>
            <FieldLabel hint="An entity's own tag is offered only there; organization-wide tags are offered everywhere.">
              Belongs to
            </FieldLabel>
            <EntityPicker value={entity} onChange={setEntity} globalLabel="Organization-wide" ariaLabel="Tag scope" />
          </div>
        </div>

        <label className="block">
          <FieldLabel>Description</FieldLabel>
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={2}
            placeholder="When to use this tag"
            className="w-full resize-y rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] text-text-primary outline-none placeholder:text-text-faint focus:border-text-primary"
          />
        </label>

        {appliesTo !== 'conversation' && (
          <section className="border-t border-line pt-4">
            <div className="mb-2 flex flex-wrap items-end justify-between gap-2">
              <div>
                <h3 className="display text-[17px] leading-tight text-text-heading">Fields</h3>
                <p className="mt-0.5 text-[11.5px] text-text-muted">
                  Details each {APPLIES_TO_META[appliesTo].noun} with this tag carries — usable in templates as{' '}
                  <code className="text-text-mid">{`{{${varPrefix}<field>}}`}</code>.
                </p>
              </div>
              <Button
                variant="secondary"
                size="sm"
                onClick={() => setAttrs((cur) => [...cur, { key: '', label: '', type: 'text', options: [], required: false }])}
              >
                <Plus size={13} aria-hidden /> Add field
              </Button>
            </div>
            {attrs.length === 0 ? (
              <p className="rounded-lg border border-dashed border-line px-3 py-4 text-center text-[12px] text-text-muted">
                No fields — the tag is just a label.
              </p>
            ) : (
              <ol className="flex flex-col gap-2">
                {attrs.map((a, i) => (
                  <li key={i} className="rounded-lg border border-line bg-card p-2.5">
                    <div className="grid grid-cols-1 items-end gap-2 sm:grid-cols-[1fr_150px_auto]">
                      <label className="block">
                        <span className="mb-1 block text-[11px] font-semibold text-text-mid">Label</span>
                        <input
                          value={a.label}
                          onChange={(e) => patchAttr(i, { label: e.target.value, key: tag?.attributes.some((x) => x.key === a.key) ? a.key : '' })}
                          placeholder="e.g. Licence no."
                          className={inputCls}
                        />
                      </label>
                      <label className="block">
                        <span className="mb-1 block text-[11px] font-semibold text-text-mid">Type</span>
                        <Select
                          value={a.type}
                          onChange={(v) => patchAttr(i, { type: v as TagAttrType })}
                          size="md"
                          ariaLabel={`Type of ${a.label || `field ${i + 1}`}`}
                          options={(Object.keys(ATTR_TYPE_LABELS) as TagAttrType[]).map((t) => ({ value: t, label: ATTR_TYPE_LABELS[t] }))}
                        />
                      </label>
                      <div className="flex items-center gap-1 pb-0.5">
                        <span className="mr-1 flex items-center gap-1.5 text-[11.5px] text-text-mid">
                          <Toggle on={a.required} onChange={(v) => patchAttr(i, { required: v })} label={`${a.label || 'Field'} required`} />
                          Required
                        </span>
                        <button
                          type="button"
                          onClick={() => move(i, -1)}
                          disabled={i === 0}
                          aria-label="Move up"
                          title="Move up"
                          className="grid h-8 w-8 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
                        >
                          <ArrowUp size={14} aria-hidden />
                        </button>
                        <button
                          type="button"
                          onClick={() => move(i, 1)}
                          disabled={i === attrs.length - 1}
                          aria-label="Move down"
                          title="Move down"
                          className="grid h-8 w-8 place-items-center rounded-md text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-30"
                        >
                          <ArrowDown size={14} aria-hidden />
                        </button>
                        <button
                          type="button"
                          onClick={() => setAttrs((cur) => cur.filter((_, idx) => idx !== i))}
                          aria-label="Remove field"
                          title="Remove field"
                          className="grid h-8 w-8 place-items-center rounded-md text-text-muted hover:bg-red/10 hover:text-[color:var(--red-ink)]"
                        >
                          <Trash2 size={14} aria-hidden />
                        </button>
                      </div>
                    </div>
                    {a.type === 'select' && (
                      <div className="mt-2">
                        <span className="mb-1 block text-[11px] font-semibold text-text-mid">Options</span>
                        <TagListInput
                          value={a.options}
                          onChange={(options) => patchAttr(i, { options })}
                          placeholder="Type an option, press Enter"
                          addLabel="Add option"
                        />
                      </div>
                    )}
                    {a.label.trim() && (
                      <code className="mt-1.5 block truncate text-[10.5px] text-text-faint">
                        {`{{${varPrefix}${a.key || attributeKey(a.label)}}}`}
                      </code>
                    )}
                  </li>
                ))}
              </ol>
            )}
          </section>
        )}
      </div>
    </Modal>
  )
}
