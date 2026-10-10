import type { CSSProperties, ReactNode } from 'react'

/** A paper card: hairline rule, the theme's card surface, theme radius.
 *  `ui-card` lets Settings → Appearance scale its padding (--card-pad) and
 *  swap the hairline for a stronger rule or a shadow (globals.css). */
export function Card({
  children,
  className = '',
  style,
  'data-tour': tour,
}: {
  children: ReactNode
  className?: string
  style?: CSSProperties
  /** A page-guide target (lib/tours.ts). */
  'data-tour'?: string
}) {
  return (
    <div data-tour={tour} className={`ui-card rounded-xl border border-line bg-card ${className}`} style={style}>
      {children}
    </div>
  )
}

/** Card section heading in the display face, with an optional right slot. */
export function CardTitle({ children, right, className = '' }: { children: ReactNode; right?: ReactNode; className?: string }) {
  return (
    <div className={`flex items-baseline justify-between gap-3 ${className}`}>
      <h2 className="display text-[19px] leading-tight text-text-heading">{children}</h2>
      {right && <div className="shrink-0 text-[12px]">{right}</div>}
    </div>
  )
}

// Uppercase section label (eyebrow).
export function SectionLabel({
  children,
  className = '',
}: {
  children: ReactNode
  className?: string
}) {
  return <div className={`eyebrow ${className}`}>{children}</div>
}
