import type { CSSProperties, ReactNode } from 'react'

// Card = bg #111D35, border rgba(255,255,255,0.07), radius 12.
export function Card({
  children,
  className = '',
  style,
}: {
  children: ReactNode
  className?: string
  style?: CSSProperties
}) {
  return (
    <div
      className={`rounded-2xl border border-line bg-card ${className}`}
      style={style}
    >
      {children}
    </div>
  )
}

// Uppercase section label (10–11px caps muted).
export function SectionLabel({
  children,
  className = '',
}: {
  children: ReactNode
  className?: string
}) {
  return (
    <div
      className={`text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted ${className}`}
    >
      {children}
    </div>
  )
}
