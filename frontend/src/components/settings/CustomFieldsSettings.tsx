'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle, ChevronDown, ChevronUp, Loader2, Plus, Tags, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { CustomFieldsInputs } from '@/components/CustomFieldsInputs'
import { hasCap } from '@/lib/auth'
import {
  EMPTY_SCHEMA,
  KIND_LABELS,
  fetchCustomFields,
  saveCustomFields,
  type CustomFieldsSchema,
  type FieldDef,
  type FieldKind,
  type FieldTarget,
} from '@/lib/customFields'

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const TARGET_META: Record<FieldTarget, { title: string; blurb: string; example: string }> = {
  customer: {
    title: 'Customers',
    blurb: 'Labels on your contacts.',
    example: 'e.g. Grade (dropdown), Preferred day, Needs transport (yes/no)',
  },
}

const NEEDS_OPTIONS: FieldKind[] = ['select', 'multiselect']

/**
 * Settings → Custom Fields: define the per-vertical labels that appear on the
 * Customer form. This is CONFIGURATION (what fields exist); the contacts
 * themselves are managed from the sidebar. Values land in each contact's
 * `attributes` JSON, which the AI already reads — so a "Grade: 5" label on a
 * customer reaches the bot's context. Fields that only make sense for one
 * kind of contact (a driver's licence number) belong on a TAG instead.
 */
export function CustomFieldsSettings() {
  // read after mount (localStorage) — same capability the PUT checks
  const [canEdit, setCanEdit] = useState(false)
  useEffect(() => setCanEdit(hasCap('settings.custom_fields.manage')), [])

  const [schema, setSchema] = useState<CustomFieldsSchema | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [saving, setSaving] = useState<FieldTarget | null>(null)
  const [dirty, setDirty] = useState<Record<FieldTarget, boolean>>({ customer: false })
  // Live preview values (never saved) so admins can see how the form will look.
  const [preview, setPreview] = useState<Record<FieldTarget, Record<string, unknown>>>({
    customer: {},
  })

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      setSchema(await fetchCustomFields())
    } catch (e) {
      setSchema(EMPTY_SCHEMA)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const fieldsOf = (t: FieldTarget): FieldDef[] => (schema ? schema[t] : [])

  const update = (t: FieldTarget, next: FieldDef[]) => {
    if (!schema) return
    setSchema({ ...schema, [t]: next.map((f, i) => ({ ...f, position: i })) })
    setDirty((d) => ({ ...d, [t]: true }))
    setNotice(null)
  }

  const addField = (t: FieldTarget) =>
    update(t, [
      ...fieldsOf(t),
      { key: '', label: '', kind: 'text', options: [], required: false, position: fieldsOf(t).length },
    ])

  const patchField = (t: FieldTarget, i: number, patch: Partial<FieldDef>) =>
    update(
      t,
      fieldsOf(t).map((f, idx) => (idx === i ? { ...f, ...patch } : f)),
    )

  const removeField = (t: FieldTarget, i: number) =>
    update(
      t,
      fieldsOf(t).filter((_, idx) => idx !== i),
    )

  const move = (t: FieldTarget, i: number, dir: -1 | 1) => {
    const arr = [...fieldsOf(t)]
    const j = i + dir
    if (j < 0 || j >= arr.length) return
    ;[arr[i], arr[j]] = [arr[j], arr[i]]
    update(t, arr)
  }

  const save = async (t: FieldTarget) => {
    if (!schema) return
    setSaving(t)
    setError(null)
    setNotice(null)
    try {
      const next = await saveCustomFields({ [t]: fieldsOf(t) })
      setSchema(next)
      setDirty((d) => ({ ...d, [t]: false }))
      setNotice(`${TARGET_META[t].title} fields saved.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(null)
    }
  }

  if (schema === null) {
    return (
      <div className="flex items-center gap-2 py-6 text-[12px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> Loading custom fields…
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <div>
        <div className="flex items-center gap-2">
          <Tags size={15} className="text-brand-300" aria-hidden />
          <span className="text-[13px] font-bold text-text-high">Custom Fields</span>
        </div>
        <p className="mt-1 max-w-[640px] text-[11.5px] leading-relaxed text-text-muted">
          Define the labels that appear on the Customer form — so the app fits your vertical instead of the
          other way round. Values are visible to the AI, so a customer&apos;s <b>Grade</b> informs its replies.
          Details that only apply to some contacts (a driver&apos;s licence number, an outside company&apos;s
          contract end) belong on a <Link href="/tags" className="font-semibold text-brand-300 hover:underline">tag</Link>{' '}
          with attributes instead.
        </p>
      </div>

      {loadError && (
        <div className="flex items-start gap-2 rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-[12px] text-amber-200">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
          Couldn&apos;t load saved fields ({loadError}) — showing an empty schema.
        </div>
      )}
      {error && (
        <div className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px] text-red-200">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
          {error}
        </div>
      )}
      {notice && (
        <div className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px] text-brand-200">
          <CheckCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
          {notice}
        </div>
      )}

      {schema.targets.map((t) => {
        const fields = fieldsOf(t)
        const meta = TARGET_META[t]
        return (
          <section key={t} className="rounded-xl border border-line bg-card p-4">
            <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
              <div>
                <h3 className="text-[13px] font-bold text-text-high">{meta.title}</h3>
                <p className="text-[11px] text-text-muted">{meta.blurb}</p>
                <p className="mt-0.5 text-[10.5px] text-text-faint">{meta.example}</p>
              </div>
              {canEdit && (
                <div className="flex items-center gap-2">
                  <Button variant="secondary" onClick={() => addField(t)}>
                    <Plus size={13} aria-hidden /> Add field
                  </Button>
                  <Button onClick={() => save(t)} disabled={!dirty[t] || saving === t}>
                    {saving === t ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null}
                    Save
                  </Button>
                </div>
              )}
            </div>

            {fields.length === 0 ? (
              <div className="rounded-lg border border-dashed border-line bg-fill-subtle px-4 py-6 text-center text-[12px] text-text-muted">
                No custom fields yet{canEdit ? ' — add one above.' : '.'}
              </div>
            ) : (
              <div className="flex flex-col gap-2">
                {fields.map((f, i) => (
                  <div key={i} className="rounded-lg border border-line bg-fill-subtle p-3">
                    <div className="grid gap-2 md:grid-cols-[1fr_1fr_auto]">
                      <label className="block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">Label</span>
                        <input
                          value={f.label}
                          disabled={!canEdit}
                          placeholder="e.g. Language"
                          onChange={(e) => patchField(t, i, { label: e.target.value })}
                          className={inputCls}
                        />
                      </label>
                      <label className="block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">Type</span>
                        <Select
                          value={f.kind}
                          disabled={!canEdit}
                          onChange={(v) => patchField(t, i, { kind: v as FieldKind })}
                          options={schema.kinds.map((k) => ({ value: k, label: KIND_LABELS[k] }))}
                          size="md"
                        />
                      </label>
                      <div className="flex items-end gap-1 pb-0.5">
                        <label className="mr-1 flex cursor-pointer items-center gap-1.5 text-[11px] text-text-muted">
                          <input
                            type="checkbox"
                            checked={f.required}
                            disabled={!canEdit}
                            onChange={(e) => patchField(t, i, { required: e.target.checked })}
                            className="h-3.5 w-3.5 accent-[color:var(--primary)]"
                          />
                          Required
                        </label>
                        {canEdit && (
                          <>
                            <button
                              type="button"
                              title="Move up"
                              aria-label="Move up"
                              onClick={() => move(t, i, -1)}
                              disabled={i === 0}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover disabled:opacity-30"
                            >
                              <ChevronUp size={14} aria-hidden />
                            </button>
                            <button
                              type="button"
                              title="Move down"
                              aria-label="Move down"
                              onClick={() => move(t, i, 1)}
                              disabled={i === fields.length - 1}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover disabled:opacity-30"
                            >
                              <ChevronDown size={14} aria-hidden />
                            </button>
                            <button
                              type="button"
                              title="Remove field"
                              aria-label="Remove field"
                              onClick={() => removeField(t, i)}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-red-500/10 hover:text-red-300"
                            >
                              <Trash2 size={14} aria-hidden />
                            </button>
                          </>
                        )}
                      </div>
                    </div>
                    {NEEDS_OPTIONS.includes(f.kind) && (
                      <label className="mt-2 block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                          Options{' '}
                          <span className="font-normal text-text-muted">(type one, press Enter to add)</span>
                        </span>
                        <TagListInput
                          value={f.options}
                          disabled={!canEdit}
                          placeholder="e.g. English"
                          addLabel="Add option"
                          onChange={(options) => patchField(t, i, { options })}
                        />
                      </label>
                    )}
                  </div>
                ))}
              </div>
            )}

            {fields.length > 0 && (
              <div className="mt-4 rounded-lg border border-dashed border-line p-3">
                <div className="mb-2 text-[10.5px] font-bold uppercase tracking-[0.06em] text-text-faint">
                  Preview — how it appears on the {meta.title.toLowerCase()} form
                </div>
                <CustomFieldsInputs
                  fields={fields.filter((f) => f.label.trim())}
                  values={preview[t]}
                  onChange={(v) => setPreview((p) => ({ ...p, [t]: v }))}
                  columns={2}
                />
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}
