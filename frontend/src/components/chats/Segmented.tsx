'use client'

/**
 * The inbox's small segmented control ("Any · On · Off", "All · Contacts · Not in
 * contacts"): one radiogroup, ink on the picked segment. The filter modal shows it
 * under a visible label; the chat list's quick toggles keep the label for screen
 * readers only (`showLabel={false}`) and let long words take the room they need
 * (`fit`). A value matching no option checks nothing.
 */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  label,
  showLabel = true,
  fit = false,
  dataTour,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
  label: string
  showLabel?: boolean
  /** segments sized by their words (else equal widths) */
  fit?: boolean
  dataTour?: string
}) {
  return (
    <div data-tour={dataTour} className="flex min-w-0 flex-col gap-1.5">
      {showLabel && <span className="text-[12px] font-semibold text-text-mid">{label}</span>}
      <div role="radiogroup" aria-label={label} className="flex w-full rounded-lg border border-line-strong bg-card p-0.5">
        {options.map((o) => {
          const on = o.value === value
          return (
            <button
              key={o.value || 'any'}
              type="button"
              role="radio"
              aria-checked={on}
              title={fit ? o.label : undefined}
              onClick={() => onChange(o.value)}
              className={`min-w-0 ${fit ? 'flex-auto' : 'flex-1'} truncate rounded-md px-2 py-1 text-[12px] font-medium transition-colors ${
                on ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover hover:text-text-high'
              }`}
            >
              {o.label}
            </button>
          )
        })}
      </div>
    </div>
  )
}
