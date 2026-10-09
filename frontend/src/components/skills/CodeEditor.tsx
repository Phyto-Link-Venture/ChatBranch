'use client'

import { useRef } from 'react'
import { useT } from '@/i18n'

/**
 * A plain monospace code box with a line-number gutter. Deliberately a
 * textarea (no editor dependency): skills are short, and Tab keeps moving
 * focus so the field stays keyboard-accessible inside a modal.
 */
export function CodeEditor({
  value,
  onChange,
  readOnly = false,
  minRows = 12,
  maxRows = 26,
  ariaLabel,
  invalid = false,
}: {
  value: string
  onChange: (v: string) => void
  readOnly?: boolean
  minRows?: number
  maxRows?: number
  ariaLabel?: string
  invalid?: boolean
}) {
  const t = useT()
  const gutter = useRef<HTMLDivElement>(null)
  const lines = value.split('\n').length
  const rows = Math.min(Math.max(lines + 1, minRows), maxRows)
  return (
    <div
      className={`flex overflow-hidden rounded-lg border bg-well transition-colors focus-within:border-brand-400 ${
        invalid ? 'border-red/60' : 'border-line-strong'
      }`}
    >
      <div
        ref={gutter}
        aria-hidden
        className="w-10 shrink-0 select-none overflow-hidden border-r border-line bg-fill-subtle py-2.5 pr-2 text-right font-mono text-[12px] leading-[1.65] text-text-faint"
      >
        {Array.from({ length: Math.max(lines, rows) }, (_, i) => (
          <div key={i}>{i + 1}</div>
        ))}
      </div>
      <textarea
        value={value}
        readOnly={readOnly}
        aria-label={ariaLabel ?? t('ai.wizard.code')}
        spellCheck={false}
        autoCapitalize="off"
        autoCorrect="off"
        wrap="off"
        rows={rows}
        onChange={(e) => onChange(e.target.value)}
        onScroll={(e) => {
          if (gutter.current) gutter.current.scrollTop = e.currentTarget.scrollTop
        }}
        className="min-w-0 flex-1 resize-y bg-transparent px-3 py-2.5 font-mono text-[12px] leading-[1.65] text-text-primary outline-none"
      />
    </div>
  )
}
