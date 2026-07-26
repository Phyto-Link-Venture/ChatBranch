'use client'

import { useCallback, useEffect, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { X } from 'lucide-react'

/**
 * Reusable glass modal (upgrade §2). A centered `bg-surface` card over the
 * frosted `backdrop-blur-sm` scrim, matching the CampaignModal look but generic:
 *  - Esc and scrim-click close (scrim-click can be disabled per-call)
 *  - focus trap (Tab cycles inside; focus restored to the opener on close)
 *  - `role="dialog"` + `aria-modal` + labelled title
 *  - body scroll-lock while open
 *  - sizes sm / md / lg, theme-aware via semantic tokens
 *
 * Rendered through a portal so it escapes any `overflow-hidden`/transform
 * ancestor (the shell panels clip their content).
 */

type Size = 'sm' | 'md' | 'lg'

const MAX_W: Record<Size, string> = {
  sm: 'max-w-[440px]',
  md: 'max-w-[560px]',
  lg: 'max-w-[760px]',
}

const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

export function Modal({
  open,
  onClose,
  title,
  subtitle,
  size = 'md',
  children,
  footer,
  closeOnScrim = true,
  labelledById = 'modal-title',
}: {
  open: boolean
  onClose: () => void
  title: ReactNode
  subtitle?: ReactNode
  size?: Size
  children: ReactNode
  footer?: ReactNode
  closeOnScrim?: boolean
  labelledById?: string
}) {
  const cardRef = useRef<HTMLDivElement>(null)
  const restoreRef = useRef<HTMLElement | null>(null)

  // Esc to close + focus trap on Tab.
  const onKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        onClose()
        return
      }
      if (e.key !== 'Tab' || !cardRef.current) return
      const items = Array.from(
        cardRef.current.querySelectorAll<HTMLElement>(FOCUSABLE),
      ).filter((el) => el.offsetParent !== null || el === document.activeElement)
      if (items.length === 0) {
        e.preventDefault()
        return
      }
      const first = items[0]
      const last = items[items.length - 1]
      const active = document.activeElement as HTMLElement | null
      if (e.shiftKey && active === first) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && active === last) {
        e.preventDefault()
        first.focus()
      }
    },
    [onClose],
  )

  // Scroll-lock + initial focus + focus restore.
  useEffect(() => {
    if (!open) return
    restoreRef.current = (document.activeElement as HTMLElement) || null
    const prevOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    // Focus the first field (or the card) once painted.
    const t = window.setTimeout(() => {
      const el =
        cardRef.current?.querySelector<HTMLElement>(FOCUSABLE) || cardRef.current
      el?.focus()
    }, 0)
    return () => {
      window.clearTimeout(t)
      document.body.style.overflow = prevOverflow
      restoreRef.current?.focus?.()
    }
  }, [open])

  if (!open || typeof document === 'undefined') return null

  return createPortal(
    <div
      // scrim
      onMouseDown={(e) => {
        if (closeOnScrim && e.target === e.currentTarget) onClose()
      }}
      onKeyDown={onKeyDown}
      className="fixed inset-0 z-[60] flex animate-fadeIn items-center justify-center p-4 sm:p-6 backdrop-blur-sm"
      style={{ background: 'rgba(4,8,18,0.72)' }}
    >
      <div
        ref={cardRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={labelledById}
        tabIndex={-1}
        className={`flex max-h-[88vh] w-full ${MAX_W[size]} flex-col overflow-hidden rounded-2xl border border-line-strong bg-surface outline-none`}
        style={{ boxShadow: '0 24px 60px rgba(0,0,0,0.5)' }}
      >
        {/* Header */}
        <div className="flex items-start justify-between gap-3 border-b border-line px-[22px] py-[16px]">
          <div className="min-w-0">
            <div id={labelledById} className="text-[15px] font-extrabold tracking-[-0.4px] text-text-heading">
              {title}
            </div>
            {subtitle && <div className="mt-0.5 text-[11.5px] leading-relaxed text-text-muted">{subtitle}</div>}
          </div>
          <button
            onClick={onClose}
            aria-label="Close"
            className="grid h-[30px] w-[30px] shrink-0 place-items-center rounded-lg border border-line bg-fill text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
          >
            <X size={15} strokeWidth={1.75} aria-hidden />
          </button>
        </div>

        {/* Body */}
        <div className="min-h-0 flex-1 overflow-y-auto px-[22px] py-[18px]">{children}</div>

        {/* Footer (optional) */}
        {footer && (
          <div className="flex items-center justify-end gap-2 border-t border-line px-[22px] py-3.5">{footer}</div>
        )}
      </div>
    </div>,
    document.body,
  )
}
