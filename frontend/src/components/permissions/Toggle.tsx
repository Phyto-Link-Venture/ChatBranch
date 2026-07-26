'use client'

import { Lock } from 'lucide-react'

/**
 * Small on/off switch (brand blue when on). `locked` renders it forced-on and
 * un-clickable with a padlock — used for superadmin's rbac.manage cap.
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
      className={`relative h-[22px] w-[38px] shrink-0 rounded-full transition-colors ${
        off ? 'cursor-not-allowed' : 'cursor-pointer'
      }`}
      style={{
        background: on ? '#3B82F6' : 'var(--fill-hover)',
        opacity: disabled && !locked ? 0.5 : 1,
      }}
    >
      <span
        className="absolute top-[3px] grid h-4 w-4 place-items-center rounded-full bg-white transition-all"
        style={{ left: on ? 18 : 3 }}
      >
        {locked && <Lock size={9} strokeWidth={2.5} color="#2563EB" aria-hidden />}
      </span>
    </button>
  )
}
