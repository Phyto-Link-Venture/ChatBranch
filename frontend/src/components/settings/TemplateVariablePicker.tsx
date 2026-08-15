'use client'

import { useEffect, useState } from 'react'
import { fetchCustomFields, type CustomFieldsSchema, type FieldDef } from '@/lib/customFields'

/**
 * The {{variable}} chip row shared by Message Templates and Script Templates.
 *
 * Two groups: the built-in CRM variables, and the operator's own **Custom
 * Fields** (Settings → Custom Fields), which is what makes a template fit a
 * vertical without a code change. Custom fields are namespaced —
 * `{{cf_<key>}}` for a customer field, `{{provider_<key>}}` for the service
 * provider bound to the contact — because a field labelled "Company" or
 * "Email" would otherwise shadow a built-in of the same name. The backend
 * renders both in `FlowEngine.VariablesFor`.
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
  'days_overdue',
  'amount',
  'link',
  'best_contact_time',
] as const

function Chip({
  token,
  label,
  onInsert,
}: {
  token: string
  label?: string
  onInsert: (token: string) => void
}) {
  return (
    <button
      type="button"
      onClick={() => onInsert(token)}
      title={label ? `${label} — inserts {{${token}}}` : `Insert {{${token}}}`}
      className="cursor-pointer rounded-full border border-line bg-fill px-2 py-0.5 font-mono text-[10.5px] text-text-mid transition-colors hover:border-brand-400/40 hover:text-text-high"
    >
      {`{{${token}}}`}
    </button>
  )
}

export function TemplateVariablePicker({ onInsert }: { onInsert: (token: string) => void }) {
  const [schema, setSchema] = useState<CustomFieldsSchema | null>(null)

  useEffect(() => {
    // best-effort — the built-in chips still work if custom fields are
    // unreachable, so a failure is silent rather than an error banner
    fetchCustomFields()
      .then(setSchema)
      .catch(() => setSchema(null))
  }, [])

  const custom: { token: string; def: FieldDef }[] = [
    ...(schema?.customer ?? []).map((def) => ({ token: `cf_${def.key}`, def })),
    ...(schema?.provider ?? []).map((def) => ({ token: `provider_${def.key}`, def })),
  ].filter((c) => c.def.key)

  return (
    <div className="flex flex-col gap-2">
      <div>
        <span className="mb-1 block text-[10.5px] text-text-muted">
          Click to insert a variable — filled from the contact&apos;s CRM record when sent:
        </span>
        <div className="flex flex-wrap gap-1.5">
          {BUILTIN_VARIABLES.map((v) => (
            <Chip key={v} token={v} onInsert={onInsert} />
          ))}
        </div>
      </div>
      {custom.length > 0 && (
        <div>
          <span className="mb-1 block text-[10.5px] text-text-muted">
            Your custom fields (Settings → Custom Fields) — <span className="font-mono">cf_</span> is a customer field,{' '}
            <span className="font-mono">provider_</span> the bound service provider&apos;s:
          </span>
          <div className="flex flex-wrap gap-1.5">
            {custom.map(({ token, def }) => (
              <Chip key={token} token={token} label={def.label || def.key} onInsert={onInsert} />
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
