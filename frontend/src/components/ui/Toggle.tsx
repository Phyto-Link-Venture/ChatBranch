'use client'

import { Lock } from 'lucide-react'

/**
 * On/off switch. `locked` renders it forced-on and un-clickable with a
 * padlock — used for superadmin's rbac.manage capability.
 */
export function Toggle({
  on,
  locked,
  disabled,
  onChange,
  label,
}: {
  on: boolean
  locked?: boolean
  disabled?: boolean
  onChange: (v: boolean) => void
  label: string
}) {
  const off = disabled || locked
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={label}
      disabled={off}
      onClick={() => onChange(!on)}
      className={`relative h-[22px] w-[38px] shrink-0 rounded-full border transition-colors ${
        on ? 'border-primary bg-primary' : 'border-line-strong bg-fill-hover'
      } ${off ? 'cursor-not-allowed' : 'cursor-pointer'}`}
      style={{ opacity: disabled && !locked ? 0.5 : 1 }}
    >
      <span
        className={`absolute top-[2px] grid h-4 w-4 place-items-center rounded-full transition-all ${on ? 'bg-accent' : 'bg-card'}`}
        style={{ left: on ? 18 : 2, boxShadow: '0 1px 2px rgba(0,0,0,0.2)' }}
      >
        {locked && <Lock size={9} strokeWidth={2.5} className="text-text-heading" aria-hidden />}
      </span>
    </button>
  )
}
