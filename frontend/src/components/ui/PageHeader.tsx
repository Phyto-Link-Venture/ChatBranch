import type { ReactNode } from 'react'

/**
 * Page masthead: small eyebrow, the title in the theme's display face, an
 * optional subtitle, and a right-hand action slot. A single ink rule under it
 * separates the masthead from the page body (Paper & Ink's newspaper rule).
 */
export function PageHeader({
  eyebrow,
  title,
  subtitle,
  actions,
  className = '',
}: {
  eyebrow?: ReactNode
  title: ReactNode
  subtitle?: ReactNode
  actions?: ReactNode
  className?: string
}) {
  return (
    <div className={`flex flex-wrap items-end gap-x-4 gap-y-2 border-b border-text-primary/80 pb-3 ${className}`}>
      <div className="min-w-0">
        {eyebrow && <div className="eyebrow">{eyebrow}</div>}
        <h1 className="display mt-0.5 text-[30px] leading-[1.05] text-text-heading sm:text-[36px]">{title}</h1>
        {subtitle && <div className="mt-1 text-[13px] text-text-muted">{subtitle}</div>}
      </div>
      {actions && <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}
