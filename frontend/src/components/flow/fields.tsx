'use client'

import type { ReactNode } from 'react'

/**
 * Small form primitives for the step editor — token-styled, theme-safe, no
 * native <select> (dropdowns use components/ui/Select).
 */

export function Field({
  label,
  help,
  children,
  htmlFor,
}: {
  label: ReactNode
  help?: ReactNode
  children: ReactNode
  htmlFor?: string
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={htmlFor} className="text-[12px] font-semibold text-text-mid">
        {label}
      </label>
      {children}
      {help && <div className="text-[11.5px] leading-snug text-text-muted">{help}</div>}
    </div>
  )
}

export const inputCls =
  'h-9 w-full min-w-0 rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-high outline-none placeholder:text-text-faint focus:border-text-primary'

export const textareaCls =
  'w-full min-w-0 rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-high outline-none placeholder:text-text-faint focus:border-text-primary'

export function TextInput({
  value,
  onChange,
  placeholder,
  id,
  type = 'text',
  ariaLabel,
}: {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  id?: string
  type?: 'text' | 'time' | 'number'
  ariaLabel?: string
}) {
  return (
    <input
      id={id}
      type={type}
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      onChange={(e) => onChange(e.target.value)}
      className={inputCls}
    />
  )
}

export function NumberInput({
  value,
  onChange,
  min = 0,
  step = 1,
  ariaLabel,
  className = '',
}: {
  value: number | string | undefined
  onChange: (v: number | '') => void
  min?: number
  step?: number
  ariaLabel?: string
  className?: string
}) {
  return (
    <input
      type="number"
      inputMode="decimal"
      min={min}
      step={step}
      value={value === undefined ? '' : String(value)}
      aria-label={ariaLabel}
      onChange={(e) => onChange(e.target.value === '' ? '' : Number(e.target.value))}
      className={`${inputCls} ${className}`}
    />
  )
}

/** A row of mutually exclusive choices (2–4 options). */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  ariaLabel,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string; hint?: string }[]
  ariaLabel: string
}) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="grid gap-1.5" style={{ gridTemplateColumns: `repeat(${options.length}, minmax(0, 1fr))` }}>
      {options.map((o) => {
        const on = o.value === value
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={on}
            onClick={() => onChange(o.value)}
            className={`flex min-w-0 flex-col items-start rounded-lg border px-2.5 py-2 text-left transition-colors ${
              on ? 'border-text-primary bg-fill-hover' : 'border-line-strong bg-card hover:bg-fill'
            }`}
          >
            <span className="text-[12.5px] font-semibold text-text-high">{o.label}</span>
            {o.hint && <span className="mt-0.5 text-[11px] leading-snug text-text-muted">{o.hint}</span>}
          </button>
        )
      })}
    </div>
  )
}

/** A labelled on/off row (wraps components/ui/Toggle visually). */
export function SwitchRow({
  label,
  help,
  on,
  onChange,
  locked,
}: {
  label: string
  help?: string
  on: boolean
  onChange: (v: boolean) => void
  locked?: boolean
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <div className="text-[12.5px] font-semibold text-text-high">{label}</div>
        {help && <div className="mt-0.5 text-[11.5px] leading-snug text-text-muted">{help}</div>}
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={on}
        aria-label={label}
        disabled={locked}
        onClick={() => onChange(!on)}
        className={`relative mt-0.5 h-[22px] w-[38px] shrink-0 rounded-full border transition-colors ${
          on ? 'border-primary bg-primary' : 'border-line-strong bg-fill-hover'
        } ${locked ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'}`}
      >
        <span
          className={`absolute top-[2px] h-4 w-4 rounded-full shadow-sm transition-all ${on ? 'bg-accent' : 'bg-card'}`}
          style={{ left: on ? 18 : 2 }}
        />
      </button>
    </div>
  )
}

export function Notice({ tone = 'muted', children }: { tone?: 'muted' | 'amber' | 'red'; children: ReactNode }) {
  const cls =
    tone === 'amber'
      ? 'border-amber/40 bg-amber/10 text-[color:var(--amber-ink)]'
      : tone === 'red'
        ? 'border-red/40 bg-red/10 text-[color:var(--red-ink)]'
        : 'border-line bg-fill-subtle text-text-mid'
  return <div className={`rounded-lg border px-3 py-2 text-[12px] leading-snug ${cls}`}>{children}</div>
}
