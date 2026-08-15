'use client'

import { useState, type KeyboardEvent } from 'react'
import { Plus, X } from 'lucide-react'

/**
 * Add-one-at-a-time list editor: the user types a single value and presses
 * Enter (or the "+" button) to turn it into a chip. Replaces the old
 * "comma-separated" text inputs, which normalised the value on every keystroke
 * and made it impossible to type a second entry.
 */
export function TagListInput({
  value,
  onChange,
  placeholder,
  disabled,
  addLabel = 'Add',
}: {
  value: string[]
  onChange: (next: string[]) => void
  placeholder?: string
  disabled?: boolean
  addLabel?: string
}) {
  const [draft, setDraft] = useState('')

  const commit = () => {
    const v = draft.trim()
    if (!v) return
    if (!value.includes(v)) onChange([...value, v])
    setDraft('')
  }
  const remove = (idx: number) => onChange(value.filter((_, i) => i !== idx))

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      commit()
    } else if (e.key === 'Backspace' && !draft && value.length) {
      e.preventDefault()
      remove(value.length - 1)
    }
  }

  return (
    <div
      className={`rounded-lg border border-line-strong bg-card px-2 py-1.5 transition-colors focus-within:border-brand-400 ${
        disabled ? 'opacity-60' : ''
      }`}
    >
      {value.length > 0 && (
        <div className="mb-1.5 flex flex-wrap gap-1">
          {value.map((t, i) => (
            <span
              key={`${t}-${i}`}
              className="inline-flex items-center gap-1 rounded-full bg-brand-400/10 px-2 py-0.5 text-[11px] font-semibold text-brand-300"
            >
              {t}
              {!disabled && (
                <button
                  type="button"
                  aria-label={`Remove ${t}`}
                  title="Remove"
                  onClick={() => remove(i)}
                  className="grid h-3.5 w-3.5 place-items-center rounded-full hover:bg-brand-400/20"
                >
                  <X size={10} aria-hidden />
                </button>
              )}
            </span>
          ))}
        </div>
      )}
      <div className="flex items-center gap-1.5">
        <input
          value={draft}
          disabled={disabled}
          placeholder={placeholder}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={onKeyDown}
          onBlur={commit}
          className="min-w-0 flex-1 bg-transparent px-1 py-0.5 text-[12.5px] text-text-primary outline-none placeholder:text-text-faint"
        />
        <button
          type="button"
          onClick={commit}
          disabled={disabled || !draft.trim()}
          className="inline-flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-[11px] font-semibold text-brand-300 hover:bg-brand-400/10 disabled:opacity-40"
        >
          <Plus size={12} aria-hidden />
          {addLabel}
        </button>
      </div>
    </div>
  )
}
