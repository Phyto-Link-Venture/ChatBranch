'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertCircle, CheckCircle, ChevronDown, ChevronUp, Loader2, Plus, Tags, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { CustomFieldsInputs } from '@/components/CustomFieldsInputs'
import { hasCap } from '@/lib/auth'
import { useEntity } from '@/lib/EntityContext'
import {
  EMPTY_SCHEMA,
  JSON_MAX_CHARS,
  KIND_LABELS,
  NEEDS_OPTIONS,
  fetchCustomFields,
  saveCustomFields,
  type CustomFieldsSchema,
  type FieldDef,
  type FieldKind,
  type FieldTarget,
} from '@/lib/customFields'
import { Trans, useT, type MessageKey } from '@/i18n'

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const TARGET_META: Record<FieldTarget, { title: MessageKey; blurb: MessageKey; example: MessageKey; saved: MessageKey; preview: MessageKey }> = {
  customer: {
    title: 'settings.customFields.customer.title',
    blurb: 'settings.customFields.customer.blurb',
    example: 'settings.customFields.customer.example',
    saved: 'settings.customFields.customer.saved',
    preview: 'settings.customFields.customer.preview',
  },
}

/**
 * Settings → Custom Fields: define the per-vertical labels that appear on the
 * Customer form. This is CONFIGURATION (what fields exist); the contacts
 * themselves are managed from the sidebar. Values land in each contact's
 * `attributes` JSON, which the AI already reads — so a "Grade: 5" label on a
 * customer reaches the bot's context. Fields that only make sense for one
 * kind of contact (a driver's licence number) belong on a TAG instead.
 */
export function CustomFieldsSettings() {
  const t = useT()
  // a field may belong to one company: only that company's AI sees its value
  const { entities } = useEntity()
  const companies = entities.filter((e) => !e.parent && e.id != null)
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

  const fieldsOf = (tg: FieldTarget): FieldDef[] => (schema ? schema[tg] : [])

  const update = (tg: FieldTarget, next: FieldDef[]) => {
    if (!schema) return
    setSchema({ ...schema, [tg]: next.map((f, i) => ({ ...f, position: i })) })
    setDirty((d) => ({ ...d, [tg]: true }))
    setNotice(null)
  }

  const addField = (tg: FieldTarget) =>
    update(tg, [
      ...fieldsOf(tg),
      {
        key: '',
        label: '',
        kind: 'text',
        options: [],
        required: false,
        show_to_ai: false,
        entity_id: null,
        position: fieldsOf(tg).length,
      },
    ])

  const patchField = (tg: FieldTarget, i: number, patch: Partial<FieldDef>) =>
    update(
      tg,
      fieldsOf(tg).map((f, idx) => (idx === i ? { ...f, ...patch } : f)),
    )

  const removeField = (tg: FieldTarget, i: number) =>
    update(
      tg,
      fieldsOf(tg).filter((_, idx) => idx !== i),
    )

  const move = (tg: FieldTarget, i: number, dir: -1 | 1) => {
    const arr = [...fieldsOf(tg)]
    const j = i + dir
    if (j < 0 || j >= arr.length) return
    ;[arr[i], arr[j]] = [arr[j], arr[i]]
    update(tg, arr)
  }

  const save = async (tg: FieldTarget) => {
    if (!schema) return
    setSaving(tg)
    setError(null)
    setNotice(null)
    try {
      const next = await saveCustomFields({ [tg]: fieldsOf(tg) })
      setSchema(next)
      setDirty((d) => ({ ...d, [tg]: false }))
      setNotice(t(TARGET_META[tg].saved))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(null)
    }
  }

  if (schema === null) {
    return (
      <div className="flex items-center gap-2 py-6 text-[12px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('settings.customFields.loading')}
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <div>
        <div className="flex items-center gap-2">
          <Tags size={15} className="text-brand-300" aria-hidden />
          <span className="text-[13px] font-bold text-text-high">{t('settings.nav.pages.customFields.lbl')}</span>
        </div>
        <p className="mt-1 max-w-[640px] text-[11.5px] leading-relaxed text-text-muted">
          <Trans
            k="settings.customFields.intro"
            values={{
              grade: <b>{t('settings.customFields.introGrade')}</b>,
              tag: (
                <Link href="/tags" className="font-semibold text-brand-300 hover:underline">
                  {t('settings.customFields.introTag')}
                </Link>
              ),
            }}
          />
        </p>
      </div>

      {loadError && (
        <div className="flex items-start gap-2 rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-[12px] text-amber-200">
          <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />
          {t('settings.customFields.loadError', { error: loadError })}
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

      {schema.targets.map((tg) => {
        const fields = fieldsOf(tg)
        const meta = TARGET_META[tg]
        return (
          <section key={tg} className="rounded-xl border border-line bg-card p-4">
            <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
              <div>
                <h3 className="text-[13px] font-bold text-text-high">{t(meta.title)}</h3>
                <p className="text-[11px] text-text-muted">{t(meta.blurb)}</p>
                <p className="mt-0.5 text-[10.5px] text-text-faint">{t(meta.example)}</p>
              </div>
              {canEdit && (
                <div className="flex items-center gap-2">
                  <Button variant="secondary" onClick={() => addField(tg)}>
                    <Plus size={13} aria-hidden /> {t('admin.tags.editor.addField')}
                  </Button>
                  <Button onClick={() => save(tg)} disabled={!dirty[tg] || saving === tg}>
                    {saving === tg ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null}
                    {t('common.actions.save')}
                  </Button>
                </div>
              )}
            </div>

            {fields.length === 0 ? (
              <div className="rounded-lg border border-dashed border-line bg-fill-subtle px-4 py-6 text-center text-[12px] text-text-muted">
                {canEdit ? t('settings.customFields.emptyEdit') : t('settings.customFields.empty')}
              </div>
            ) : (
              <div className="flex flex-col gap-2">
                {fields.map((f, i) => (
                  <div key={i} className="rounded-lg border border-line bg-fill-subtle p-3">
                    <div className="grid gap-2 md:grid-cols-[1fr_1fr_auto]">
                      <label className="block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('admin.tags.editor.fieldLabel')}</span>
                        <input
                          value={f.label}
                          disabled={!canEdit}
                          placeholder={t('settings.customFields.labelPlaceholder')}
                          onChange={(e) => patchField(tg, i, { label: e.target.value })}
                          className={inputCls}
                        />
                      </label>
                      <label className="block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('common.fields.type')}</span>
                        <Select
                          value={f.kind}
                          disabled={!canEdit}
                          onChange={(v) => patchField(tg, i, { kind: v as FieldKind })}
                          options={schema.kinds.map((k) => ({ value: k, label: KIND_LABELS[k] ? t(KIND_LABELS[k]) : k }))}
                          size="md"
                          ariaLabel={t('common.fields.type')}
                        />
                      </label>
                      <div className="flex items-end gap-1 pb-0.5">
                        <label className="mr-1 flex cursor-pointer items-center gap-1.5 text-[11px] text-text-muted">
                          <input
                            type="checkbox"
                            checked={f.required}
                            disabled={!canEdit}
                            onChange={(e) => patchField(tg, i, { required: e.target.checked })}
                            className="h-3.5 w-3.5 accent-[color:var(--primary)]"
                          />
                          {t('common.status.required')}
                        </label>
                        <label
                          className="mr-1 flex cursor-pointer items-center gap-1.5 text-[11px] text-text-muted"
                          title={t('settings.customFields.showToAiHint')}
                        >
                          <input
                            type="checkbox"
                            checked={!!f.show_to_ai}
                            disabled={!canEdit}
                            onChange={(e) => patchField(tg, i, { show_to_ai: e.target.checked })}
                            className="h-3.5 w-3.5 accent-[color:var(--primary)]"
                          />
                          {t('settings.customFields.showToAi')}
                        </label>
                        {canEdit && (
                          <>
                            <button
                              type="button"
                              title={t('admin.tags.editor.moveUp')}
                              aria-label={t('admin.tags.editor.moveUp')}
                              onClick={() => move(tg, i, -1)}
                              disabled={i === 0}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover disabled:opacity-30"
                            >
                              <ChevronUp size={14} aria-hidden />
                            </button>
                            <button
                              type="button"
                              title={t('admin.tags.editor.moveDown')}
                              aria-label={t('admin.tags.editor.moveDown')}
                              onClick={() => move(tg, i, 1)}
                              disabled={i === fields.length - 1}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-fill-hover disabled:opacity-30"
                            >
                              <ChevronDown size={14} aria-hidden />
                            </button>
                            <button
                              type="button"
                              title={t('admin.tags.editor.removeField')}
                              aria-label={t('admin.tags.editor.removeField')}
                              onClick={() => removeField(tg, i)}
                              className="grid h-7 w-7 place-items-center rounded-md text-text-muted hover:bg-red-500/10 hover:text-red-300"
                            >
                              <Trash2 size={14} aria-hidden />
                            </button>
                          </>
                        )}
                      </div>
                    </div>
                    {companies.length > 1 && (
                      <label className="mt-2 block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                          {t('settings.customFields.scope')}{' '}
                          <span className="font-normal text-text-muted">{t('settings.customFields.scopeHint')}</span>
                        </span>
                        <Select
                          value={f.entity_id == null ? '' : String(f.entity_id)}
                          disabled={!canEdit}
                          onChange={(v) => patchField(tg, i, { entity_id: v === '' ? null : Number(v) })}
                          options={[
                            { value: '', label: t('settings.scope.globalLabel') },
                            ...companies.map((e) => ({ value: String(e.id), label: e.name })),
                          ]}
                          size="md"
                          ariaLabel={t('settings.customFields.scope')}
                        />
                      </label>
                    )}
                    {f.kind === 'date' && (
                      <p className="mt-2 text-[10.5px] text-text-faint">{t('settings.customFields.dateHint')}</p>
                    )}
                    {f.kind === 'json' && (
                      <p className="mt-2 text-[10.5px] text-text-faint">
                        {t('settings.customFields.jsonKindHint', { max: JSON_MAX_CHARS })}
                      </p>
                    )}
                    {!!f.show_to_ai && (
                      <p className="mt-1 text-[10.5px] text-brand-200">{t('settings.customFields.showToAiOn')}</p>
                    )}
                    {NEEDS_OPTIONS.includes(f.kind) && (
                      <label className="mt-2 block">
                        <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                          {t('admin.tags.editor.options')}{' '}
                          <span className="font-normal text-text-muted">{t('settings.customFields.optionsHint')}</span>
                        </span>
                        <TagListInput
                          value={f.options}
                          disabled={!canEdit}
                          placeholder={t('settings.customFields.optionPlaceholder')}
                          addLabel={t('admin.tags.editor.addOption')}
                          onChange={(options) => patchField(tg, i, { options })}
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
                  {t(meta.preview)}
                </div>
                <CustomFieldsInputs
                  fields={fields.filter((f) => f.label.trim())}
                  values={preview[tg]}
                  onChange={(v) => setPreview((p) => ({ ...p, [tg]: v }))}
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
