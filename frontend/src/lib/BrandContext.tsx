'use client'

/**
 * Tenant + brand context for the topbar switcher (design.md: brand/tenant
 * switcher chip). Loads the REAL data the signed-in user may see:
 *   /api/v1/whoami  -> { tenant, brands: string[] }  (their tenant + brand slugs)
 *   /api/v1/brands  -> { brands: [{ slug, name, tenant }] } (names for the slugs)
 *
 * `activeBrand` is the operator's current working brand (persisted to
 * localStorage). superadmin/admin can switch between the brands in their
 * tenant; an agent is pinned to their single assigned brand. Screens that
 * are brand-scoped read `activeBrand` to filter what they show — server-side
 * RBAC still bounds the set, this narrows within it.
 */

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authHeaders, getAuth } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'
const ACTIVE_KEY = 'chatbranch-active-brand'

export interface Brand {
  slug: string
  name: string
  tenant: string
}

interface BrandState {
  tenant: string | null
  brands: Brand[]
  activeBrand: Brand | null
  setActiveBrand: (slug: string | null) => void
  loading: boolean
}

const Ctx = createContext<BrandState | null>(null)

async function v1<T>(path: string): Promise<T> {
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json() as Promise<T>
}

export function BrandProvider({ children }: { children: ReactNode }) {
  const [tenant, setTenant] = useState<string | null>(null)
  const [brands, setBrands] = useState<Brand[]>([])
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
          v1<{ tenant: string | null; brands: string[] }>('whoami'),
          v1<{ brands: Brand[] }>('brands'),
        ])
        if (!alive) return
        // only brands the user may see (whoami.brands), keep the ordering stable
        const visible = who.brands.length
          ? list.brands.filter((b) => who.brands.includes(b.slug))
          : list.brands
        setTenant(who.tenant)
        setBrands(visible)
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

  const setActiveBrand = useCallback((slug: string | null) => {
    setActiveSlug(slug)
    try {
      if (slug) localStorage.setItem(ACTIVE_KEY, slug)
      else localStorage.removeItem(ACTIVE_KEY)
    } catch {
      /* ignore private-mode storage errors */
    }
  }, [])

  const activeBrand = useMemo(
    () => brands.find((b) => b.slug === activeSlug) ?? null,
    [brands, activeSlug]
  )

  const value = useMemo<BrandState>(
    () => ({ tenant, brands, activeBrand, setActiveBrand, loading }),
    [tenant, brands, activeBrand, setActiveBrand, loading]
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useBrand(): BrandState {
  const v = useContext(Ctx)
  if (!v) throw new Error('useBrand must be used within BrandProvider')
  return v
}
