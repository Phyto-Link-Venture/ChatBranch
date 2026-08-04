'use client'

import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'

/**
 * Settings dirty-state (design.md "commit or discard before you leave").
 * A tab that has stage-then-save controls calls `setDirty(true)` on edit and
 * `register({ save, discard })` so the shared header's Save/Discard buttons act
 * on it. Live instant-save controls (STT/LLM selects) and self-saving tabs
 * (Users/Entities) simply never mark dirty, so the header stays hidden for them.
 */
interface Handlers {
  save: () => Promise<void> | void
  discard: () => void
}

interface DirtyState {
  dirty: boolean
  saving: boolean
  saved: boolean
  setDirty: (b: boolean) => void
  register: (h: Handlers | null) => void
  save: () => Promise<void>
  discard: () => void
}

const Ctx = createContext<DirtyState | null>(null)

export function SettingsDirtyProvider({ children }: { children: ReactNode }) {
  const [dirty, setDirty] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const handlersRef = useRef<Handlers | null>(null)

  const register = useCallback((h: Handlers | null) => {
    handlersRef.current = h
  }, [])

  const save = useCallback(async () => {
    setSaving(true)
    try {
      await handlersRef.current?.save()
      setDirty(false)
      setSaved(true)
      setTimeout(() => setSaved(false), 2200)
    } finally {
      setSaving(false)
    }
  }, [])

  const discard = useCallback(() => {
    handlersRef.current?.discard()
    setDirty(false)
  }, [])

  const value = useMemo<DirtyState>(
    () => ({ dirty, saving, saved, setDirty, register, save, discard }),
    [dirty, saving, saved, register, save, discard],
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useSettingsDirty(): DirtyState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useSettingsDirty must be used within SettingsDirtyProvider')
  return v
}
