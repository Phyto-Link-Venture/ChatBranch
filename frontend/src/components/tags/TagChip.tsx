'use client'

import type { ReactNode } from 'react'
import { X } from 'lucide-react'
import { tint } from '@/lib/color'
import { tagColor } from '@/lib/tags'

/**
 * A registry tag as a chip: a coloured dot, the name in the tag's ink, a faint
 * tint behind. Colours come from the palette KEY (tokens), so the chip flips
 * with the theme. `onRemove` adds an × button.
 */
export function TagChip({
  name,
  color,
  size = 'md',
  onRemove,
  removeLabel,
  disabled,
  children,
  title,
}: {
  name: string
  color: string
  size?: 'sm' | 'md'
  onRemove?: () => void
  removeLabel?: string
  disabled?: boolean
  children?: ReactNode
  title?: string
}) {
  const c = tagColor(color)
  return (
    <span
      title={title}
      className={`inline-flex max-w-full items-center gap-1.5 rounded-full border font-semibold ${
        size === 'sm' ? 'px-2 py-px text-[10.5px]' : 'px-2.5 py-0.5 text-[12px]'
      }`}
      style={{ color: c.ink, background: tint(c.dot, 0.1), borderColor: tint(c.dot, 0.32) }}
    >
      <span className="h-1.5 w-1.5 shrink-0 rounded-full" style={{ background: c.dot }} aria-hidden />
      <span className="truncate">{name}</span>
      {children}
      {onRemove && (
        <button
          type="button"
          onClick={onRemove}
          disabled={disabled}
          aria-label={removeLabel ?? `Remove ${name}`}
          title={removeLabel ?? `Remove ${name}`}
          className="-mr-1 grid h-4 w-4 shrink-0 cursor-pointer place-items-center rounded-full opacity-70 transition-opacity hover:opacity-100 disabled:cursor-not-allowed disabled:opacity-40"
        >
          <X size={11} strokeWidth={2.25} aria-hidden />
        </button>
      )}
    </span>
  )
}

/** A colour swatch for the palette picker. */
export function ColorDot({ color, size = 10 }: { color: string; size?: number }) {
  return (
    <span
      className="inline-block shrink-0 rounded-full"
      style={{ width: size, height: size, background: tagColor(color).dot }}
      aria-hidden
    />
  )
}
