'use client'

import { useEffect, useId, useRef, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { VARIABLE_GROUPS, VARIABLE_COUNT, type VariableGroup } from '@/lib/flow/variables'
import { fetchCustomFields } from '@/lib/customFields'
import { textareaCls } from './fields'

/** Customer custom fields as {{cf_<key>}} — fetched once per page visit. */
let customGroupCache: Promise<VariableGroup | null> | null = null
function customFieldsGroup(): Promise<VariableGroup | null> {
  customGroupCache ??= fetchCustomFields()
    .then((s) =>
      s.customer.length
        ? {
            group: 'Custom fields',
            items: s.customer.map((f) => ({ key: `cf_${f.key}`, label: f.label || f.key, example: '' })),
          }
        : null,
    )
    .catch(() => null)
  return customGroupCache
}

/**
 * A textarea with the {{variable}} catalog underneath: click a variable to
 * insert it at the cursor, so nobody has to remember what the engine can fill.
 */
export default function VariableTextarea({
  label,
  help,
  rows = 4,
  value,
  onChange,
  placeholder,
  defaultOpen = false,
}: {
  label: string
  help?: string
  rows?: number
  value: string
  onChange: (value: string) => void
  placeholder?: string
  defaultOpen?: boolean
}) {
  const id = useId()
  const ref = useRef<HTMLTextAreaElement>(null)
  const [open, setOpen] = useState(defaultOpen)
  const [custom, setCustom] = useState<VariableGroup | null>(null)

  useEffect(() => {
    if (!open) return
    let alive = true
    customFieldsGroup().then((g) => alive && setCustom(g))
    return () => {
      alive = false
    }
  }, [open])

  const insert = (token: string) => {
    const el = ref.current
    const start = el?.selectionStart ?? value.length
    const end = el?.selectionEnd ?? value.length
    onChange(value.slice(0, start) + token + value.slice(end))
    requestAnimationFrame(() => {
      if (!el) return
      el.focus()
      const pos = start + token.length
      el.setSelectionRange(pos, pos)
    })
  }

  const groups = custom ? [...VARIABLE_GROUPS, custom] : VARIABLE_GROUPS

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[12px] font-semibold text-text-mid">
        {label}
      </label>
      <textarea
        id={id}
        ref={ref}
        rows={rows}
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className={textareaCls}
      />
      {help && <div className="text-[11.5px] leading-snug text-text-muted">{help}</div>}
      <div>
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen(!open)}
          className="inline-flex items-center gap-1 text-[11.5px] font-semibold text-text-muted hover:text-text-high"
        >
          <ChevronRight size={12} className={`transition-transform ${open ? 'rotate-90' : ''}`} aria-hidden />
          Insert a variable{open ? '' : ` (${VARIABLE_COUNT}+)`}
        </button>
        {open && (
          <div className="mt-2 flex max-h-[260px] flex-col gap-2.5 overflow-y-auto rounded-lg border border-line bg-well p-2.5">
            {groups.map((g) => (
              <div key={g.group}>
                <div className="eyebrow mb-1">{g.group}</div>
                <div className="flex flex-wrap gap-1">
                  {g.items.map((v) => (
                    <button
                      key={v.key}
                      type="button"
                      title={v.example ? `${v.label} — e.g. ${v.example}` : v.label}
                      onClick={() => insert(`{{${v.key}}}`)}
                      className="rounded-md border border-line bg-card px-1.5 py-0.5 text-[11.5px] text-text-mid hover:border-line-strong hover:text-text-high"
                    >
                      {v.label}
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
