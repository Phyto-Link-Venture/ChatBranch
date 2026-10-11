'use client'

/**
 * Tenant + entity context for the topbar switcher (design.md: brand/tenant
 * switcher chip). Loads the REAL data the signed-in user may see:
 *   /api/v1/whoami  -> { tenant, entities: string[] }  (their tenant + entity slugs)
 *   /api/v1/entities  -> { entities: [{ slug, name, tenant }] } (names for the slugs)
 *
 * The operator ticks one or more working entities (remembered per browser;
 * the first visit starts on their first company, not "All"). superadmin/admin
 * can switch between the entities in their tenant; an agent sees only their
 * assigned ones. `activeEntity` is the single ticked entity (null for All or
 * several). Screens that
 * are brand-scoped read `activeEntity` to filter what they show — server-side
 * RBAC still bounds the set, this narrows within it.
 */

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { authHeaders, getAuth } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const ACTIVE_KEY = 'chatbranch-active-entity' // legacy single pick (read once, then migrated)
const SELECTED_KEY = 'chatbranch-entities' // JSON: string[] of slugs, or ["__all__"]
const ALL = '__all__' // sentinel: view across every entity

export interface Entity {
  /** Row id (GET /api/v1/entities sends it). */
  id?: number
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
  /** The working entity when exactly ONE is ticked; null for "All entities" or several. */
  activeEntity: Entity | null
  /** Every ticked entity (each brings its branches); empty = All entities. */
  selectedEntities: Entity[]
  /** True when viewing across all entities. */
  allEntities: boolean
  /** True when several entities are ticked (pages that edit ONE level ask for one). */
  multipleEntities: boolean
  /**
   * The signed-in person is BOUND to assigned entities (whoami `bound`: a company
   * admin, an agent or a custom role with entities). A bound person never views
   * "All entities" and never acts at the organization level — every level picker
   * hides "Organization" for them. False until whoami answers (read after mount).
   */
  bound: boolean
  /**
   * The ONE level a settings page reads / edits: the single ticked entity; else
   * null (the organization) — except for a bound person, who gets their first
   * ticked (else first visible) entity, never the organization.
   */
  levelEntity: Entity | null
  /** Pick exactly one entity (null = All entities). */
  setActiveEntity: (slug: string | null) => void
  /** Tick / untick one entity, keeping the others. */
  toggleEntity: (slug: string) => void
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

function readStored(): string[] | null {
  try {
    const raw = localStorage.getItem(SELECTED_KEY)
    if (raw) {
      const v = JSON.parse(raw)
      if (Array.isArray(v)) return v.filter((x): x is string => typeof x === 'string')
    }
    const legacy = localStorage.getItem(ACTIVE_KEY)
    return legacy ? [legacy] : null
  } catch {
    return null
  }
}

function store(slugs: string[]) {
  try {
    localStorage.setItem(SELECTED_KEY, JSON.stringify(slugs.length ? slugs : [ALL]))
  } catch {
    /* ignore private-mode storage errors */
  }
}

/** A ticked entity already covers its branches: drop picks whose ancestor is also picked. */
function normalize(slugs: string[], entities: Entity[]): string[] {
  const parentOf = new Map(entities.map((e) => [e.slug, e.parent ?? null]))
  const set = new Set(slugs)
  return slugs.filter((slug, i) => {
    if (slugs.indexOf(slug) !== i) return false
    let p = parentOf.get(slug) ?? null
    for (let d = 0; p && d < 32; d++) {
      if (set.has(p)) return false
      p = parentOf.get(p) ?? null
    }
    return true
  })
}

export function EntityProvider({ children }: { children: ReactNode }) {
  const [tenant, setTenant] = useState<string | null>(null)
  const [entities, setEntities] = useState<Entity[]>([])
  // the ticked slugs; [] = All entities
  const [selected, setSelected] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [bound, setBound] = useState(false)
  // read inside callbacks (storage events, toggles) without re-binding them
  const boundRef = useRef(false)

  const load = useCallback(async () => {
    if (!getAuth()) {
      setLoading(false)
      return
    }
    try {
      const [who, list] = await Promise.all([
        v1<{ tenant: string | null; entities: string[]; bound?: boolean }>('whoami'),
        v1<{ entities: Entity[] }>('entities'),
      ])
      // only entities the user may see (whoami.entities), keep the ordering stable
      const visible = who.entities.length
        ? list.entities.filter((b) => who.entities.includes(b.slug))
        : list.entities
      const isBound = Boolean(who.bound)
      boundRef.current = isBound
      setBound(isBound)
      setTenant(who.tenant)
      setEntities(visible)
      setSelected((cur) => {
        const known = (xs: string[]) => normalize(xs.filter((s) => visible.some((b) => b.slug === s)), visible)
        // keep the current pick if still valid; else the last pick on this browser;
        // else the first company — never everything at once by default
        const still = known(cur)
        if (cur.length > 0 && still.length > 0) return still
        const stored = readStored()
        // the stored pick is per BROWSER: another person's "All" must never put a
        // bound person on every entity
        if (stored?.includes(ALL) && !isBound) return []
        const restored = stored ? known(stored) : []
        if (restored.length > 0) return restored
        const first = visible.find((b) => !b.parent || !visible.some((x) => x.slug === b.parent)) ?? visible[0]
        return first ? [first.slug] : []
      })
    } catch {
      /* API down / not authed — switcher renders in a neutral state */
    } finally {
      setLoading(false)
    }
  }, [])

  // Load on mount AND whenever the signed-in session changes: the provider
  // mounts on /login (no token yet), so without this the switcher stayed on a
  // disabled "All entities" until a full reload. A session swap in another
  // tab arrives as a `storage` event.
  useEffect(() => {
    let lastToken = getAuth()?.token ?? null
    void load()
    const onAuth = () => {
      const token = getAuth()?.token ?? null
      if (token === lastToken) return
      lastToken = token
      if (!token) {
        setTenant(null)
        setEntities([])
        setSelected([])
        boundRef.current = false
        setBound(false)
        return
      }
      setLoading(true)
      void load()
    }
    const onStorage = (e: StorageEvent) => {
      if (e.key === null || e.key === 'chatbranch-auth') onAuth()
      else if (e.key === SELECTED_KEY) {
        const stored = readStored()
        // a bound person keeps their own pick rather than follow another tab to "All"
        if (stored && !(boundRef.current && (stored.includes(ALL) || stored.length === 0)))
          setSelected(stored.includes(ALL) ? [] : stored)
      }
    }
    window.addEventListener('auth-change', onAuth)
    window.addEventListener('storage', onStorage)
    return () => {
      window.removeEventListener('auth-change', onAuth)
      window.removeEventListener('storage', onStorage)
    }
  }, [load])

  const setActiveEntity = useCallback((slug: string | null) => {
    // a bound person has no "All entities" view
    if (!slug && boundRef.current) return
    const next = slug ? [slug] : []
    setSelected(next)
    store(next)
  }, [])

  const toggleEntity = useCallback(
    (slug: string) => {
      setSelected((cur) => {
        const next = cur.includes(slug) ? cur.filter((s) => s !== slug) : normalize([...cur, slug], entities)
        // unticking a bound person's last entity keeps it (never "All entities")
        if (next.length === 0 && boundRef.current) return cur
        store(next)
        return next
      })
    },
    [entities]
  )

  const selectedEntities = useMemo(
    () => selected.map((s) => entities.find((b) => b.slug === s)).filter((b): b is Entity => Boolean(b)),
    [entities, selected]
  )
  const activeEntity = selectedEntities.length === 1 ? selectedEntities[0] : null
  const levelEntity = activeEntity ?? (bound ? (selectedEntities[0] ?? entities[0] ?? null) : null)

  // Stable identity: consumers put reload in effect deps (the Entities page
  // reloads the switcher after an edit). A fresh arrow per render made that
  // effect re-fire on every context update — an endless fetch loop.
  const reload = useCallback(() => {
    void load()
  }, [load])

  const value = useMemo<EntityState>(
    () => ({
      tenant,
      entities,
      activeEntity,
      selectedEntities,
      allEntities: selectedEntities.length === 0,
      multipleEntities: selectedEntities.length > 1,
      bound,
      levelEntity,
      setActiveEntity,
      toggleEntity,
      reload,
      loading,
    }),
    [tenant, entities, activeEntity, selectedEntities, bound, levelEntity, setActiveEntity, toggleEntity, reload, loading]
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useEntity(): EntityState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useEntity must be used within EntityProvider')
  return v
}

/**
 * The `?entity=<slug>[,<slug>…]` suffix for the active scope — every ticked
 * entity (each with its branches), "" when viewing All entities (no narrowing). Every entity-aware page builds its URLs with this
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
  const { selectedEntities } = useEntity()
  return selectedEntities.length ? `entity=${selectedEntities.map((e) => encodeURIComponent(e.slug)).join(',')}` : ''
}

/**
 * The `?entity=<slug>` for pages that read or EDIT one level of the tree
 * (persona, AI behaviour, the AI switch…): the single ticked entity, else ""
 * (the organization level). Several ticked entities never reach a settings
 * endpoint — pair it with `<MultiEntityNotice />` so the page says so. A
 * person BOUND to entities never gets the organization level: their first
 * ticked (else first) entity instead (`levelEntity`).
 */
export function useEntityLevelQuery(): string {
  const { levelEntity } = useEntity()
  return levelEntity ? `entity=${encodeURIComponent(levelEntity.slug)}` : ''
}

/** Same scope as `useEntityQuery`, ready to append to a URL that may or may
 * not already carry a query string. Returns "" for All entities. */
export function useEntityParam(url: string): string {
  const q = useEntityQuery()
  if (!q) return url
  return url.includes('?') ? `${url}&${q}` : `${url}?${q}`
}
