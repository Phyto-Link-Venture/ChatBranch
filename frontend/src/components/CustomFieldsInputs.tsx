'use client'

import { Select } from '@/components/ui/Select'
import type { FieldDef } from '@/lib/customFields'

/**
 * Renders the inputs for a list of Custom Field definitions and edits a
 * `values` map (the row's `attributes` JSON). Shared by the Service Provider
 * form, the Contact form and the Settings → Custom Fields live preview, so
 * every field kind is handled in exactly one place.
 */

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

export type FieldValues = Record<string, unknown>

export function CustomFieldsInputs({
  fields,
  values,
  onChange,
  disabled = false,
  columns = 2,
}: {
  fields: FieldDef[]
  values: FieldValues
  onChange: (next: FieldValues) => void
  disabled?: boolean
  /** grid columns on md+ (forms use 2; the settings preview uses 1) */
  columns?: 1 | 2
}) {
  if (fields.length === 0) return null
  const set = (key: string, v: unknown) => onChange({ ...values, [key]: v })
  const grid = columns === 2 ? 'grid gap-3 md:grid-cols-2' : 'grid gap-3'

  return (
    <div className={grid}>
      {fields.map((f) => {
        const label = (
          <span className="mb-1 block text-[10.5px] font-bold text-text-high">
            {f.label}
            {f.required && <span className="ml-0.5 text-red-300">*</span>}
          </span>
        )
        const v = values[f.key]

        switch (f.kind) {
          case 'select':
            return (
              <label key={f.key} className="block">
                {label}
                <Select
                  value={typeof v === 'string' ? v : ''}
                  onChange={(nv) => set(f.key, nv)}
                  options={[{ value: '', label: '—' }, ...f.options.map((o) => ({ value: o, label: o }))]}
                  disabled={disabled}
                />
              </label>
            )
          case 'multiselect': {
            const arr = Array.isArray(v) ? (v as string[]) : []
            return (
              <div key={f.key} className="block">
                {label}
                <div className="flex flex-wrap gap-1.5">
                  {f.options.map((o) => {
                    const on = arr.includes(o)
                    return (
                      <button
                        key={o}
                        type="button"
                        disabled={disabled}
                        aria-pressed={on}
                        onClick={() => set(f.key, on ? arr.filter((x) => x !== o) : [...arr, o])}
                        className={`rounded-full border px-2.5 py-1 text-[11.5px] font-semibold transition-colors disabled:opacity-60 ${
                          on
                            ? 'border-brand-400/40 bg-brand-400/15 text-brand-200'
                            : 'border-line bg-fill text-text-muted hover:bg-fill-hover'
                        }`}
                      >
                        {o}
                      </button>
                    )
                  })}
                </div>
              </div>
            )
          }
          case 'number':
            return (
              <label key={f.key} className="block">
                {label}
                <input
                  type="number"
                  inputMode="decimal"
                  value={v === null || v === undefined ? '' : String(v)}
                  disabled={disabled}
                  onChange={(e) => set(f.key, e.target.value === '' ? null : Number(e.target.value))}
                  className={inputCls}
                />
              </label>
            )
          case 'boolean':
            return (
              <label key={f.key} className="flex cursor-pointer items-center gap-2 pt-5">
                <input
                  type="checkbox"
                  checked={!!v}
                  disabled={disabled}
                  onChange={(e) => set(f.key, e.target.checked)}
                  className="h-4 w-4 accent-emerald-500"
                />
                <span className="text-[12px] font-semibold text-text-high">
                  {f.label}
                  {f.required && <span className="ml-0.5 text-red-300">*</span>}
                </span>
              </label>
            )
          case 'text':
          default:
            return (
              <label key={f.key} className="block">
                {label}
                <input
                  value={typeof v === 'string' ? v : v == null ? '' : String(v)}
                  disabled={disabled}
                  onChange={(e) => set(f.key, e.target.value)}
                  className={inputCls}
                />
              </label>
            )
        }
      })}
    </div>
  )
}
