'use client'

import { useEffect, useRef, useState } from 'react'
import { Check, ChevronDown } from 'lucide-react'

export interface SelectOption {
  value: string
  label: string
  /** Muted suffix, e.g. a role or phone number. */
  hint?: string
}

/**
 * Themed dropdown — replaces native <select>, whose popup list is OS-drawn
 * and ignores the app theme (white panel / blue highlight on Windows dark
 * mode). Trigger + listbox both use the semantic tokens so they flip with
 * light/dark, with the blue brand as the selection accent.
 */
export function Select({
  value,
  onChange,
  options,
  placeholder = 'Select…',
  disabled = false,
  size = 'sm',
  ariaLabel,
  className = '',
}: {
  value: string
  onChange: (value: string) => void
  options: SelectOption[]
  placeholder?: string
  disabled?: boolean
  /** sm = 32px (chat chrome), md = 37px (settings forms). */
  size?: 'sm' | 'md'
  ariaLabel?: string
  className?: string
}) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const selected = options.find((o) => o.value === value)
  const sizeCls = size === 'sm' ? 'h-8 text-[12px]' : 'h-[37px] text-[12.5px]'

  return (
    <div ref={ref} className={`relative ${className}`}>
      <button
        type="button"
        onClick={() => setOpen(!open)}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={ariaLabel}
        className={`flex w-full cursor-pointer items-center justify-between gap-2 rounded-lg border bg-fill px-2.5 text-left outline-none transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${sizeCls} ${
          open ? 'border-brand-400' : 'border-line hover:border-line-strong'
        }`}
      >
        <span className={`truncate font-semibold ${selected ? 'text-text-high' : 'font-normal text-text-faint'}`}>
          {selected ? (
            <>
              {selected.label}
              {selected.hint && <span className="ml-1 font-normal text-text-muted">{selected.hint}</span>}
            </>
          ) : (
            placeholder
          )}
        </span>
        <ChevronDown
          size={13}
          className={`shrink-0 text-text-muted transition-transform ${open ? 'rotate-180' : ''}`}
          aria-hidden
        />
      </button>

      {open && (
        <div
          role="listbox"
          className="absolute left-0 right-0 top-full z-30 mt-1 max-h-56 overflow-y-auto rounded-lg border border-line-strong bg-surface py-1"
          style={{ boxShadow: '0 12px 32px rgba(0,0,0,0.35)' }}
        >
          {options.length === 0 && (
            <div className="px-2.5 py-2 text-[11.5px] text-text-muted">No options</div>
          )}
          {options.map((o) => {
            const isSel = o.value === value
            return (
              <button
                key={o.value}
                type="button"
                role="option"
                aria-selected={isSel}
                onClick={() => {
                  onChange(o.value)
                  setOpen(false)
                }}
                className={`flex w-full cursor-pointer items-center justify-between gap-2 px-2.5 py-1.5 text-left text-[12px] transition-colors hover:bg-fill ${
                  isSel ? 'font-semibold text-text-high' : 'text-text-mid'
                }`}
              >
                <span className="truncate">
                  {o.label}
                  {o.hint && <span className="ml-1 font-normal text-text-muted">{o.hint}</span>}
                </span>
                {isSel && <Check size={12} className="shrink-0" style={{ color: 'var(--brand-ink)' }} aria-hidden />}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}
