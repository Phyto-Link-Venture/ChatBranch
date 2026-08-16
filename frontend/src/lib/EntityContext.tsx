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

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const ACTIVE_KEY = 'chatbranch-active-entity'
const ALL = '__all__' // sentinel: view across every entity

export interface Entity {
  slug: string
  name: string
  tenant: string
  /** Hierarchy (GET /api/v1/entities): null/absent = a top-level entity. */
  parent?: string | null
  parent_name?: string
}

interface EntityState {
  tenant: string | null
  entities: Entity[]
  /** The working entity, or null when "All entities" is selected. */
  activeEntity: Entity | null
  /** True when viewing across all entities (activeEntity is null by choice). */
  allEntities: boolean
  setActiveEntity: (slug: string | null) => void
  /** Re-fetch the entity list (call after creating/deleting an entity). */
  reload: () => void
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
  // activeSlug: a slug, or ALL sentinel for "all entities". Starts as ALL.
  const [activeSlug, setActiveSlug] = useState<string>(ALL)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    if (!getAuth()) {
      setLoading(false)
      return
    }
    try {
      const [who, list] = await Promise.all([
        v1<{ tenant: string | null; entities: string[] }>('whoami'),
        v1<{ entities: Entity[] }>('entities'),
      ])
      // only entities the user may see (whoami.entities), keep the ordering stable
      const visible = who.entities.length
        ? list.entities.filter((b) => who.entities.includes(b.slug))
        : list.entities
      setTenant(who.tenant)
      setEntities(visible)
      setActiveSlug((cur) => {
        // keep the current pick if still valid; else restore stored; else:
        // agents (single entity) pin to it, multi-entity users default to All.
        if (cur !== ALL && visible.some((b) => b.slug === cur)) return cur
        const stored = (() => {
          try {
            return localStorage.getItem(ACTIVE_KEY)
          } catch {
            return null
          }
        })()
        if (stored === ALL) return ALL
        if (stored && visible.some((b) => b.slug === stored)) return stored
        return visible.length === 1 ? visible[0].slug : ALL
      })
    } catch {
      /* API down / not authed — switcher renders in a neutral state */
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const setActiveEntity = useCallback((slug: string | null) => {
    const next = slug ?? ALL
    setActiveSlug(next)
    try {
      localStorage.setItem(ACTIVE_KEY, next)
    } catch {
      /* ignore private-mode storage errors */
    }
  }, [])

  const activeEntity = useMemo(
    () => (activeSlug === ALL ? null : entities.find((b) => b.slug === activeSlug) ?? null),
    [entities, activeSlug]
  )

  const value = useMemo<EntityState>(
    () => ({
      tenant,
      entities,
      activeEntity,
      allEntities: activeSlug === ALL,
      setActiveEntity,
      reload: () => void load(),
      loading,
    }),
    [tenant, entities, activeEntity, activeSlug, setActiveEntity, load, loading]
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useEntity(): EntityState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useEntity must be used within EntityProvider')
  return v
}

/**
 * The `?entity=<slug>` suffix for the active scope — "" when viewing All
 * entities (no narrowing). Every entity-aware page builds its URLs with this
 * so the parameter name and the all-entities rule live in ONE place.
 *
 * Use it INSIDE the dependency array of the fetching effect: the string
 * changes when the operator switches outlets, which is what makes the page
 * re-fetch. Reading `activeEntity` without depending on this is the bug that
 * leaves a page showing the previous outlet's data after a switch.
 *
 *   const q = useEntityQuery()
 *   useEffect(() => { fetch(`${API}/analytics/summary?days=30${q && `&${q}`}`) }, [q])
 */
export function useEntityQuery(): string {
  const { activeEntity } = useEntity()
  return activeEntity ? `entity=${encodeURIComponent(activeEntity.slug)}` : ''
}

/** Same scope as `useEntityQuery`, ready to append to a URL that may or may
 * not already carry a query string. Returns "" for All entities. */
export function useEntityParam(url: string): string {
  const q = useEntityQuery()
  if (!q) return url
  return url.includes('?') ? `${url}&${q}` : `${url}?${q}`
}
