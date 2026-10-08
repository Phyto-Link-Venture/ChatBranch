import type { ButtonHTMLAttributes, CSSProperties, ReactNode } from 'react'

type Variant = 'primary' | 'secondary' | 'accent' | 'danger' | 'success' | 'ghost'

// Theme-aware: primary = the theme's primary action (ink on Paper & Ink,
// the accent on Graphite/Soft); accent = the decorative accent fill.
const VARIANTS: Record<Variant, string> = {
  primary: 'bg-primary text-on-primary border border-transparent hover:opacity-90',
  secondary: 'bg-card text-text-high border border-line-strong hover:bg-fill-hover',
  accent: 'bg-accent text-[color:var(--on-accent)] border border-transparent hover:opacity-90',
  danger: 'bg-transparent text-[color:var(--red-ink)] border border-red/40 hover:bg-red/10',
  success: 'bg-transparent text-[color:var(--green-ink)] border border-green/40 hover:bg-green/10',
  ghost: 'bg-transparent text-text-mid border border-transparent hover:bg-fill-hover hover:text-text-high',
}

const SIZES = {
  sm: 'h-8 px-3 text-[12.5px]',
  md: 'h-9 px-4 text-[13px]',
} as const

interface Props extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
  size?: keyof typeof SIZES
  children: ReactNode
  style?: CSSProperties
}

export function Button({ variant = 'primary', size = 'md', children, className = '', ...rest }: Props) {
  return (
    <button
      className={`inline-flex cursor-pointer items-center justify-center gap-1.5 whitespace-nowrap rounded-lg font-semibold transition-[background,opacity,color] disabled:cursor-not-allowed disabled:opacity-50 ${VARIANTS[variant]} ${SIZES[size]} ${className}`}
      {...rest}
    >
      {children}
    </button>
  )
}
