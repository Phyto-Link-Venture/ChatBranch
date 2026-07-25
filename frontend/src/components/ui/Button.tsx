import type { ButtonHTMLAttributes, CSSProperties, ReactNode } from 'react'

type Variant = 'primary' | 'secondary' | 'danger' | 'success' | 'ghost'

const VARIANTS: Record<Variant, CSSProperties> = {
  primary: { background: 'linear-gradient(135deg,#3B82F6,#2563EB)', color: 'white', border: 'none' },
  secondary: { background: 'var(--fill)', color: 'var(--text-high)', border: '1px solid var(--line-strong)' },
  danger: { background: 'rgba(239,68,68,0.12)', color: '#FCA5A5', border: '1px solid rgba(239,68,68,0.2)' },
  success: { background: 'rgba(16,185,129,0.12)', color: '#6EE7B7', border: '1px solid rgba(16,185,129,0.2)' },
  ghost: { background: 'transparent', color: 'var(--text-muted)', border: '1px solid var(--line)' },
}

interface Props extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
  children: ReactNode
  style?: CSSProperties
}

export function Button({ variant = 'primary', children, style, ...rest }: Props) {
  return (
    <button
      className="inline-flex cursor-pointer items-center gap-1.5 rounded-xl px-4 py-2 text-[13px] font-semibold tracking-[-0.2px] disabled:cursor-not-allowed disabled:opacity-50"
      style={{ ...VARIANTS[variant], ...style }}
      {...rest}
    >
      {children}
    </button>
  )
}
