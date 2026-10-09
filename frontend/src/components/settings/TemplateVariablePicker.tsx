'use client'

import { useEffect, useState } from 'react'
import { useT } from '@/i18n'
import { fetchCustomFields, type CustomFieldsSchema, type FieldDef } from '@/lib/customFields'
import { fetchTags, tagColor, type TagDef } from '@/lib/tags'

/**
 * The {{variable}} chip row shared by Message Templates and Script Templates.
 *
 * Three groups: the built-in CRM variables, the operator's own **Custom
 * Fields** (Settings → Custom Fields) and the **attributes of registry tags**
 * (Library → Tags), which is what makes a template fit a vertical without a
 * code change. Both operator groups are namespaced — `{{cf_<key>}}` for a
 * customer field, `{{tag_<tagslug>_<key>}}` for a tag attribute (dashes in the
 * slug become underscores) — because a field labelled "Company" or "Email"
 * would otherwise shadow a built-in of the same name. The backend renders all
 * of them in `FlowEngine.VariablesFor`; a contact without the tag renders "".
 */

/** Built-in CRM variables the backend's VariablesFor always produces. */
export const BUILTIN_VARIABLES = [
  'name',
  'first_name',
  'company',
  'record_title',
  'record_status',
  'record_id',
  'due_date',
  'amount',
  'link',
  'best_contact_time',
] as const

function Chip({
  token,
  label,
  dot,
  onInsert,
}: {
  token: string
  label?: string
  dot?: string
  onInsert: (token: string) => void
}) {
  const t = useT()
  return (
    <button
      type="button"
      onClick={() => onInsert(token)}
      title={
        label
          ? t('library.variables.insertsLabelled', { label, token: `{{${token}}}` })
          : t('library.variables.inserts', { token: `{{${token}}}` })
      }
      className="inline-flex cursor-pointer items-center gap-1 rounded-full border border-line bg-fill px-2 py-0.5 font-mono text-[10.5px] text-text-mid transition-colors hover:border-line-strong hover:text-text-high"
    >
      {dot && <span className="h-1.5 w-1.5 shrink-0 rounded-full" style={{ background: dot }} aria-hidden />}
      {`{{${token}}}`}
    </button>
  )
}

export function TemplateVariablePicker({ onInsert }: { onInsert: (token: string) => void }) {
  const t = useT()
  const [schema, setSchema] = useState<CustomFieldsSchema | null>(null)
  const [tags, setTags] = useState<TagDef[]>([])

  useEffect(() => {
    // best-effort — the built-in chips still work if either source is
    // unreachable, so a failure is silent rather than an error banner
    fetchCustomFields()
      .then(setSchema)
      .catch(() => setSchema(null))
    fetchTags()
      .then((d) => setTags(d.tags.filter((tg) => tg.applies_to !== 'conversation' && tg.attributes.length > 0)))
      .catch(() => setTags([]))
  }, [])

  const custom: { token: string; def: FieldDef }[] = (schema?.customer ?? [])
    .map((def) => ({ token: `cf_${def.key}`, def }))
    .filter((c) => c.def.key)

  return (
    <div className="flex flex-col gap-2">
      <div>
        <span className="mb-1 block text-[10.5px] text-text-muted">{t('library.variables.builtins')}</span>
        <div className="flex flex-wrap gap-1.5">
          {BUILTIN_VARIABLES.map((v) => (
            <Chip key={v} token={v} onInsert={onInsert} />
          ))}
        </div>
      </div>
      {custom.length > 0 && (
        <div>
          <span className="mb-1 block text-[10.5px] text-text-muted">{t('library.variables.customFields')}</span>
          <div className="flex flex-wrap gap-1.5">
            {custom.map(({ token, def }) => (
              <Chip key={token} token={token} label={def.label || def.key} onInsert={onInsert} />
            ))}
          </div>
        </div>
      )}
      {tags.length > 0 && (
        <div>
          <span className="mb-1 block text-[10.5px] text-text-muted">{t('library.variables.tags')}</span>
          <div className="flex flex-col gap-1.5">
            {tags.map((tg) => (
              <div key={tg.id} className="flex flex-wrap items-center gap-1.5">
                <span className="mr-0.5 text-[10.5px] font-semibold" style={{ color: tagColor(tg.color).ink }}>
                  {tg.name}
                </span>
                {tg.attributes.map((a) => (
                  <Chip
                    key={a.key}
                    token={`${tg.variable_prefix || `tag_${tg.slug.replace(/-/g, '_')}_`}${a.key}`}
                    label={`${tg.name} · ${a.label}`}
                    dot={tagColor(tg.color).dot}
                    onInsert={onInsert}
                  />
                ))}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
