'use client'

import { useEffect, useId, useState, type ReactNode } from 'react'
import { ChevronDown, SlidersHorizontal } from 'lucide-react'
import { useT } from '@/i18n'

/**
 * "Advanced settings" — the ONE way a form tucks away what a first-time user
 * doesn't need. Closed by default, so a form opens with only its essential
 * fields; every field inside must already have a sensible default.
 *
 * - `forceOpen` opens it when something inside needs attention (a value that
 *   differs from the default, a validation error) — never hide a problem.
 * - `count` shows how many advanced values are set ("Advanced settings · 2").
 * - `label` / `hint` override the default words for a specific form.
 * - `variant="card"` = a bordered card section (page level); default = an
 *   inline row inside a form, drawer or modal.
 */
export function Advanced({
  children,
  label,
  hint,
  count = 0,
  forceOpen = false,
  defaultOpen = false,
  variant = 'inline',
  className = '',
  'data-tour': tour,
}: {
  children: ReactNode
  label?: string
  hint?: string
  count?: number
  forceOpen?: boolean
  defaultOpen?: boolean
  variant?: 'inline' | 'card'
  className?: string
  /** A page-guide target (lib/tours.ts). */
  'data-tour'?: string
}) {
  const t = useT()
  const id = useId()
  const [open, setOpen] = useState(defaultOpen || forceOpen)
  useEffect(() => {
    if (forceOpen) setOpen(true)
  }, [forceOpen])

  const title = label ?? t('common.advanced.title')
  const sub = hint ?? t('common.advanced.hint')
  const card = variant === 'card'

  return (
    <div data-tour={tour} className={`${card ? 'rounded-xl border border-line bg-card' : 'rounded-lg border border-dashed border-line'} ${className}`}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls={id}
        className={`flex w-full items-center gap-2 text-left ${card ? 'px-4 py-3.5 sm:px-5' : 'px-3 py-2.5'}`}
      >
        <SlidersHorizontal size={14} aria-hidden className="shrink-0 text-text-muted" />
        <span className="min-w-0 flex-1">
          <span className="block text-[13px] font-semibold text-text-high">
            {title}
            {count > 0 && (
              <span className="ml-1.5 rounded-full bg-fill px-1.5 py-0.5 text-[10.5px] font-semibold text-text-mid">
                {count}
              </span>
            )}
          </span>
          {!open && <span className="block text-[11.5px] text-text-muted">{sub}</span>}
        </span>
        <span className="shrink-0 text-[12px] font-medium text-text-muted">
          {open ? t('common.advanced.hide') : t('common.advanced.show')}
        </span>
        <ChevronDown
          size={15}
          aria-hidden
          className="shrink-0 text-text-muted transition-transform"
          style={{ transform: open ? 'none' : 'rotate(-90deg)' }}
        />
      </button>
      {open && (
        <div id={id} className={`flex flex-col gap-4 border-t border-line ${card ? 'px-4 py-5 sm:px-5' : 'px-3 py-3'}`}>
          {children}
        </div>
      )}
    </div>
  )
}
