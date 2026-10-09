'use client'

import { useCallback, useEffect, useState } from 'react'

/**
 * Where Ask ChatBranch sits: docked to the right edge (the default) or a
 * floating window the person drags anywhere. Desktop only — below lg it is
 * always the full-width panel. Remembered per browser (a convenience: blocked
 * storage simply means docked).
 */
export interface AssistantLayout {
  mode: 'docked' | 'floating'
  /** Top-left of the floating window, in CSS pixels. */
  x: number
  y: number
}

const KEY = 'assistant-layout'
const DEFAULT: AssistantLayout = { mode: 'docked', x: -1, y: -1 }

export const FLOAT_W = 400
export const FLOAT_H = 620

function read(): AssistantLayout {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return DEFAULT
    const v = JSON.parse(raw) as Partial<AssistantLayout>
    return {
      mode: v.mode === 'floating' ? 'floating' : 'docked',
      x: typeof v.x === 'number' ? v.x : -1,
      y: typeof v.y === 'number' ? v.y : -1,
    }
  } catch {
    return DEFAULT
  }
}

/** Keep the window on screen (a smaller window or a zoom change can strand it). */
export function clampPos(x: number, y: number, w = FLOAT_W, h = FLOAT_H): { x: number; y: number } {
  const vw = window.innerWidth
  const vh = window.innerHeight
  const height = Math.min(h, vh - 24)
  if (x < 0 || y < 0) return { x: Math.max(12, vw - w - 24), y: Math.max(12, vh - height - 24) }
  return {
    x: Math.min(Math.max(8, x), Math.max(8, vw - w - 8)),
    y: Math.min(Math.max(8, y), Math.max(8, vh - 56)),
  }
}

export function useAssistantLayout() {
  const [layout, setLayout] = useState<AssistantLayout>(DEFAULT)
  const [desktop, setDesktop] = useState(false)

  useEffect(() => {
    setLayout(read())
    const mq = window.matchMedia('(min-width: 1024px)')
    const on = () => setDesktop(mq.matches)
    on()
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])

  const update = useCallback((patch: Partial<AssistantLayout>) => {
    setLayout((prev) => {
      const next = { ...prev, ...patch }
      try {
        localStorage.setItem(KEY, JSON.stringify(next))
      } catch {
        /* storage blocked — the layout lasts this visit only */
      }
      return next
    })
  }, [])

  return { layout, update, floating: desktop && layout.mode === 'floating', desktop }
}
