'use client'

import { Select } from '@/components/ui/Select'
import { Toggle } from '@/components/ui/Toggle'
import type { TagAttribute } from '@/lib/tags'

const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[12.5px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-primary disabled:opacity-60'

/**
 * The inputs for a tag's attributes (its field definitions), editing a plain
 * values map. One place per field type, shared by the contact and entity tag
 * editors. The server validates again (required, numbers, dates, options).
 */
export function TagValuesForm({
  attributes,
  values,
  onChange,
  disabled,
  idPrefix = 'tv',
  columns = 2,
}: {
  attributes: TagAttribute[]
  values: Record<string, unknown>
  onChange: (next: Record<string, unknown>) => void
  disabled?: boolean
  idPrefix?: string
  /** 1 for narrow columns (the contact page side card). */
  columns?: 1 | 2
}) {
  if (attributes.length === 0) return null
  const set = (key: string, v: unknown) => onChange({ ...values, [key]: v })

  return (
    <div className={`grid grid-cols-1 gap-2.5 ${columns === 2 ? 'sm:grid-cols-2' : ''}`}>
      {attributes.map((a) => {
        const id = `${idPrefix}-${a.key}`
        const raw = values[a.key]
        const text = raw === null || raw === undefined ? '' : String(raw)
        const label = (
          <label htmlFor={id} className="mb-1 block text-[11px] font-semibold text-text-high">
            {a.label}
            {a.required && <span className="ml-0.5 text-[color:var(--red-ink)]">*</span>}
          </label>
        )
        switch (a.type) {
          case 'boolean':
            return (
              <div key={a.key} className="flex items-center justify-between gap-3 rounded-lg border border-line px-3 py-2">
                <span className="text-[12px] font-semibold text-text-high">
                  {a.label}
                  {a.required && <span className="ml-0.5 text-[color:var(--red-ink)]">*</span>}
                </span>
                <Toggle on={raw === true || raw === 'true'} onChange={(v) => set(a.key, v)} label={a.label} disabled={disabled} />
              </div>
            )
          case 'select':
            return (
              <div key={a.key}>
                {label}
                <Select
                  value={text}
                  onChange={(v) => set(a.key, v)}
                  size="md"
                  ariaLabel={a.label}
                  disabled={disabled}
                  options={[{ value: '', label: '—' }, ...a.options.map((o) => ({ value: o, label: o }))]}
                />
              </div>
            )
          default:
            return (
              <div key={a.key}>
                {label}
                <input
                  id={id}
                  type={a.type === 'number' ? 'number' : a.type === 'date' ? 'date' : a.type === 'email' ? 'email' : a.type === 'phone' ? 'tel' : 'text'}
                  inputMode={a.type === 'number' ? 'decimal' : a.type === 'phone' ? 'tel' : undefined}
                  value={text}
                  disabled={disabled}
                  onChange={(e) => set(a.key, e.target.value)}
                  placeholder={a.type === 'phone' ? '+60 12-345 6789' : a.type === 'email' ? 'name@example.com' : ''}
                  className={inputCls}
                />
              </div>
            )
        }
      })}
    </div>
  )
}
