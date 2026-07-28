'use client'

/**
 * Tenant + entity context for the topbar switcher (design.md: brand/tenant
 * switcher chip). Loads the REAL data the signed-in user may see:
 *   /api/v1/whoami  -> { tenant, entities: string[] }  (their tenant + entity slugs)
 *   /api/v1/entities  -> { entities: [{ slug, name, tenant }] } (names for the slugs)
 *
 * `activeEntity` is the operator's current working entity (persisted to
 * localStorage). superadmin/admin can switch between the entities in their
 * tenant; an agent is pinned to their single assigned entity. Screens that
 * are brand-scoped read `activeEntity` to filter what they show — server-side
 * RBAC still bounds the set, this narrows within it.
 */

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authHeaders, getAuth } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'
const ACTIVE_KEY = 'chatbranch-active-brand'

export interface Entity {
  slug: string
  name: string
  tenant: string
}

interface EntityState {
  tenant: string | null
  entities: Entity[]
  activeEntity: Entity | null
  setActiveEntity: (slug: string | null) => void
  loading: boolean
}

const Ctx = createContext<EntityState | null>(null)

async function v1<T>(path: string): Promise<T> {
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json() as Promise<T>
}

export function EntityProvider({ children }: { children: ReactNode }) {
  const [tenant, setTenant] = useState<string | null>(null)
  const [entities, setEntities] = useState<Entity[]>([])
  const [activeSlug, setActiveSlug] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    if (!getAuth()) {
      setLoading(false)
      return
    }
    let alive = true
    ;(async () => {
      try {
        const [who, list] = await Promise.all([
          v1<{ tenant: string | null; entities: string[] }>('whoami'),
          v1<{ entities: Entity[] }>('entities'),
        ])
        if (!alive) return
        // only entities the user may see (whoami.entities), keep the ordering stable
        const visible = who.entities.length
          ? list.entities.filter((b) => who.entities.includes(b.slug))
          : list.entities
        setTenant(who.tenant)
        setEntities(visible)
        const stored = (() => {
          try {
            return localStorage.getItem(ACTIVE_KEY)
          } catch {
            return null
          }
        })()
        const initial = visible.find((b) => b.slug === stored) ?? visible[0] ?? null
        setActiveSlug(initial?.slug ?? null)
      } catch {
        /* API down / not authed — switcher renders in a neutral state */
      } finally {
        if (alive) setLoading(false)
      }
    })()
    return () => {
      alive = false
    }
  }, [])

  const setActiveEntity = useCallback((slug: string | null) => {
    setActiveSlug(slug)
    try {
      if (slug) localStorage.setItem(ACTIVE_KEY, slug)
      else localStorage.removeItem(ACTIVE_KEY)
    } catch {
      /* ignore private-mode storage errors */
    }
  }, [])

  const activeEntity = useMemo(
    () => entities.find((b) => b.slug === activeSlug) ?? null,
    [entities, activeSlug]
  )

  const value = useMemo<EntityState>(
    () => ({ tenant, entities, activeEntity, setActiveEntity, loading }),
    [tenant, entities, activeEntity, setActiveEntity, loading]
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useEntity(): EntityState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useEntity must be used within EntityProvider')
  return v
}
