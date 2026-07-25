'use client'

import { useCallback, useEffect, useState } from 'react'
import { Building2, Loader2, Plus, ShieldAlert } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { authHeaders } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

/**
 * Brands & tenants (RBAC brands model, /api/v1/brands + /api/v1/whoami).
 * A Tenant is the parent organization; a Brand is a business unit or client
 * it serves. Access is brand-level: admins see every brand in their tenant,
 * agents only their assigned brands (managed in Users & Roles).
 * Superadmins create brands in any tenant; admins in their own.
 */

interface BrandRow {
  id: number
  name: string
  slug: string
  tenant: string
  is_active: boolean
  created_at: string
}

interface WhoAmI {
  username: string
  role: string
  tenant: string | null
  brands: string[]
}

const slugify = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)

async function v1<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/v1/${path}`, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const body = data as { error?: { message?: string }; detail?: string }
    throw new Error(body.error?.message || body.detail || `HTTP ${res.status}`)
  }
  return data as T
}

export function BrandsSettings() {
  const { role, ready } = useRole()
  const [brands, setBrands] = useState<BrandRow[] | null>(null)
  const [me, setMe] = useState<WhoAmI | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  // add form
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [tenant, setTenant] = useState('')

  const refresh = useCallback(async () => {
    try {
      const [b, w] = await Promise.all([
        v1<{ brands: BrandRow[] }>('brands'),
        v1<WhoAmI>('whoami'),
      ])
      setBrands(b.brands)
      setMe(w)
      setTenant((t) => t || w.tenant || b.brands[0]?.tenant || '')
    } catch {
      setBrands(null)
    }
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (role === 'agent') {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <ShieldAlert size={15} className="text-amber-300" aria-hidden />
        Brands are managed by admins.
      </div>
    )
  }

  if (brands === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Brand management unavailable — is the backend API running on port 8000?
      </div>
    )
  }

  const isSuperadmin = me?.role === 'superadmin'
  const tenants = [...new Set(brands.map((b) => b.tenant))].sort()

  const addBrand = async () => {
    setBusy(true)
    setMsg(null)
    try {
      await v1('brands', {
        method: 'POST',
        body: JSON.stringify({
          name: name.trim(),
          slug: slug.trim() || slugify(name),
          // admins are pinned to their tenant server-side; only superadmin's
          // choice is honoured
          tenant: tenant.trim(),
        }),
      })
      setName('')
      setSlug('')
      setSlugTouched(false)
      setMsg({ ok: true, text: `Brand ${name.trim()} created.` })
      await refresh()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        A <b>tenant</b> is the parent organization; a <b>brand</b> is a business unit or client it
        serves. Contacts are shared across a tenant&apos;s brands. Admins see every brand in their
        tenant; agents only the brands assigned to them (set per user under Users &amp;
        Roles).
        {me?.tenant && (
          <>
            {' '}
            You are in tenant <b>{me.tenant}</b>.
          </>
        )}
      </p>

      {/* brand list, grouped by tenant */}
      {tenants.length === 0 && (
        <div className="text-[12px] text-text-muted">No brands yet — add the first one below.</div>
      )}
      {tenants.map((t) => (
        <div key={t}>
          <div className="mb-1.5 flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
            <Building2 size={12} aria-hidden /> {t}
          </div>
          <div className="flex flex-col">
            {brands
              .filter((b) => b.tenant === t)
              .map((b) => (
                <div key={b.id} className="flex items-center gap-3 border-b border-line py-2.5">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="text-[13px] font-semibold text-text-high">{b.name}</span>
                      <code className="rounded bg-fill-hover px-1.5 py-px text-[10.5px] text-text-muted">
                        {b.slug}
                      </code>
                      {!b.is_active && (
                        <span className="rounded-full bg-red/15 px-2 py-px text-[10px] font-bold text-red-300">
                          Inactive
                        </span>
                      )}
                    </div>
                  </div>
                  <span className="shrink-0 text-[10.5px] text-text-faint">
                    since {new Date(b.created_at).toLocaleDateString()}
                  </span>
                </div>
              ))}
          </div>
        </div>
      ))}

      {msg && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>
          {msg.text}
        </div>
      )}

      {/* add brand */}
      <div>
        <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted">
          Add brand
        </div>
        <div className="grid grid-cols-2 gap-3">
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Brand name</span>
            <input
              type="text"
              value={name}
              onChange={(e) => {
                setName(e.target.value)
                if (!slugTouched) setSlug(slugify(e.target.value))
              }}
              placeholder="e.g. Northwind Trading"
              className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Slug</span>
            <input
              type="text"
              value={slug}
              onChange={(e) => {
                setSlugTouched(true)
                setSlug(slugify(e.target.value))
              }}
              placeholder="auto-generated from the name"
              className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
            />
            <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
              Short unique id, used in APIs and reports. Unique per tenant.
            </span>
          </label>
          {isSuperadmin && (
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Tenant</span>
              <input
                type="text"
                value={tenant}
                onChange={(e) => setTenant(e.target.value)}
                list="brands-tenant-slugs"
                placeholder="tenant slug, e.g. northwind"
                className="w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400"
              />
              <datalist id="brands-tenant-slugs">
                {tenants.map((t) => (
                  <option key={t} value={t} />
                ))}
              </datalist>
              <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">
                Super admin only — admins always create in their own tenant.
              </span>
            </label>
          )}
        </div>
        <div className="mt-3">
          <Button
            variant="primary"
            onClick={addBrand}
            disabled={busy || !name.trim() || (isSuperadmin && !tenant.trim())}
          >
            {busy ? (
              <Loader2 size={13} className="animate-spin" aria-hidden />
            ) : (
              <Plus size={13} aria-hidden />
            )}{' '}
            Add brand
          </Button>
        </div>
      </div>
    </div>
  )
}
