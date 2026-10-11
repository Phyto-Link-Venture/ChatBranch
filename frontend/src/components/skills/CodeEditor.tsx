'use client'

import { useMemo, useRef, type ReactNode, type Ref } from 'react'
import { useT } from '@/i18n'

/**
 * A plain monospace code box with a line-number gutter. Deliberately a
 * textarea (no editor dependency): skills are short, and Tab keeps moving
 * focus so the field stays keyboard-accessible inside a modal.
 *
 * `highlight="html"` paints HTML / Handlebars colours UNDER a transparent
 * textarea (the custom document layout's editor) — the textarea still does all
 * the editing, selection and accessibility. `errorLine` marks a line in the
 * gutter; `textareaRef` lets a caller insert at the cursor.
 */
export function CodeEditor({
  value,
  onChange,
  readOnly = false,
  minRows = 12,
  maxRows = 26,
  ariaLabel,
  invalid = false,
  highlight,
  errorLine,
  textareaRef,
}: {
  value: string
  onChange: (v: string) => void
  readOnly?: boolean
  minRows?: number
  maxRows?: number
  ariaLabel?: string
  invalid?: boolean
  highlight?: 'html'
  errorLine?: number | null
  textareaRef?: Ref<HTMLTextAreaElement>
}) {
  const t = useT()
  const gutter = useRef<HTMLDivElement>(null)
  const paint = useRef<HTMLPreElement>(null)
  const lines = value.split('\n').length
  const rows = Math.min(Math.max(lines + 1, minRows), maxRows)
  const painted = useMemo(() => (highlight === 'html' ? highlightHtml(value) : null), [highlight, value])
  const text = 'px-3 py-2.5 font-mono text-[12px] leading-[1.65]'
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
          <div key={i} className={errorLine === i + 1 ? 'font-bold text-[color:var(--red-ink)]' : undefined}>
            {i + 1}
          </div>
        ))}
      </div>
      <div className="relative min-w-0 flex-1">
        {painted && (
          <pre
            ref={paint}
            aria-hidden
            className={`pointer-events-none absolute inset-0 m-0 overflow-hidden whitespace-pre text-text-primary ${text}`}
          >
            {painted}
            {'\n '}
          </pre>
        )}
        <textarea
          ref={textareaRef}
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
            if (paint.current) {
              paint.current.scrollTop = e.currentTarget.scrollTop
              paint.current.scrollLeft = e.currentTarget.scrollLeft
            }
          }}
          className={`relative block w-full resize-y bg-transparent outline-none ${text} ${
            painted ? 'text-transparent caret-text-primary selection:bg-brand-400/25' : 'text-text-primary'
          }`}
        />
      </div>
    </div>
  )
}

// ---- a small HTML + Handlebars highlighter (colours only; the text is untouched) ----------

const TOKENS =
  /(\{\{\{?[\s\S]*?\}\}\}?)|(<!--[\s\S]*?-->)|(<\/?[a-zA-Z][\w:-]*)|(\s[\w:@-]+(?==))|("[^"\n]*"|'[^'\n]*')|(\/?>)/g

const CLASSES = [
  'text-[color:var(--purple-ink)] font-semibold', // {{handlebars}}
  'text-text-faint italic', // <!-- comment -->
  'text-[color:var(--blue-ink)]', // <tag
  'text-[color:var(--cyan-ink)]', // attr=
  'text-[color:var(--green-ink)]', // "string"
  'text-[color:var(--blue-ink)]', // >
]

function highlightHtml(src: string): ReactNode[] {
  const out: ReactNode[] = []
  let last = 0
  let n = 0
  for (const m of src.matchAll(TOKENS)) {
    const at = m.index ?? 0
    if (at > last) out.push(src.slice(last, at))
    const group = m.slice(1).findIndex((g) => g !== undefined)
    out.push(
      <span key={n++} className={CLASSES[group] ?? ''}>
        {m[0]}
      </span>,
    )
    last = at + m[0].length
  }
  if (last < src.length) out.push(src.slice(last))
  return out
}
