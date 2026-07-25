'use client'

import { useRef, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { VARIABLE_GROUPS, VARIABLE_COUNT } from '@/lib/flow/variables'

/**
 * A textarea with the full {{variable}} catalog underneath.
 * Clicking a variable inserts it at the cursor, so the client never
 * has to remember (or guess) what the orchestrator can fill in.
 * Used for workflow script fields and agent-skill instructions alike.
 */
export default function VariableTextarea({
  label,
  help,
  rows = 4,
  value,
  onChange,
  defaultOpen = true,
}: {
  label: string
  help?: string
  rows?: number
  value: string
  onChange: (value: string) => void
  defaultOpen?: boolean
}) {
  const ref = useRef<HTMLTextAreaElement>(null)
  const [open, setOpen] = useState(defaultOpen)

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

  return (
    <div className="config-field">
      <span>{label}</span>
      <textarea ref={ref} rows={rows} value={value} onChange={(e) => onChange(e.target.value)} />
      {help && <small>{help}</small>}

      <div className="var-picker">
        <button
          type="button"
          className="var-toggle"
          aria-expanded={open}
          onClick={() => setOpen(!open)}
        >
          <ChevronRight size={12} className={`var-caret${open ? ' open' : ''}`} aria-hidden />
          Insert a variable{open ? '' : ` (${VARIABLE_COUNT})`}
        </button>
        {open &&
          VARIABLE_GROUPS.map((g) => (
            <div key={g.group} className="var-group">
              <div className="var-group-label">{g.group}</div>
              {g.items.map((v) => (
                <button
                  key={v.key}
                  type="button"
                  className="var-row"
                  title={`e.g. ${v.example}`}
                  onClick={() => insert(`{{${v.key}}}`)}
                >
                  <code className="var-token">{`{{${v.key}}}`}</code>
                  <span className="var-desc">{v.label}</span>
                </button>
              ))}
            </div>
          ))}
      </div>
    </div>
  )
}
