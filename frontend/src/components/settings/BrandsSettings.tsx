'use client'

import { useCallback, useEffect, useState } from 'react'
import { Building2, Loader2, Pencil, Plus, ShieldAlert, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { authHeaders, hasCap } from '@/lib/auth'
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
  // create/edit modal — `editing` null = create, a row = edit
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<BrandRow | null>(null)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [tenant, setTenant] = useState('')
  const [active, setActive] = useState(true)
  // brands.manage gates create/edit/delete (read after mount — localStorage)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('brands.manage')), [])

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

  const openCreate = () => {
    setEditing(null)
    setName('')
    setSlug('')
    setSlugTouched(false)
    setTenant(me?.tenant || brands[0]?.tenant || '')
    setActive(true)
    setMsg(null)
    setOpen(true)
  }

  const openEdit = (b: BrandRow) => {
    setEditing(b)
    setName(b.name)
    setSlug(b.slug)
    setSlugTouched(true)
    setTenant(b.tenant)
    setActive(b.is_active)
    setMsg(null)
    setOpen(true)
  }

  const saveBrand = async () => {
    setBusy(true)
    setMsg(null)
    try {
      if (editing) {
        // PATCH /api/v1/brands/<id> — name / slug / is_active (backend landing
        // in parallel; a 404/405 surfaces as a clear message).
        await v1(`brands/${editing.id}`, {
          method: 'PATCH',
          body: JSON.stringify({
            name: name.trim(),
            slug: slug.trim() || slugify(name),
            is_active: active,
          }),
        })
        setMsg({ ok: true, text: `Brand ${name.trim()} updated.` })
      } else {
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
        setMsg({ ok: true, text: `Brand ${name.trim()} created.` })
      }
      setName('')
      setSlug('')
      setSlugTouched(false)
      setEditing(null)
      setOpen(false)
      await refresh()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const deleteBrand = async (b: BrandRow) => {
    if (!window.confirm(`Delete brand "${b.name}"? Contacts and assignments tied to it may be affected.`)) return
    setBusy(true)
    setMsg(null)
    try {
      // DELETE /api/v1/brands/<id> — a 409 (brand still in use) carries the
      // reason in the v1 error envelope, surfaced verbatim below.
      await v1(`brands/${b.id}`, { method: 'DELETE' })
      setMsg({ ok: true, text: `Brand ${b.name} deleted.` })
      await refresh()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const columns: Column<BrandRow>[] = [
    {
      key: 'name',
      header: 'Brand',
      sortable: true,
      value: (b) => `${b.name} ${b.slug}`,
      render: (b) => (
        <div className="flex items-center gap-2">
          <span className="text-[13px] font-semibold text-text-high">{b.name}</span>
          <code className="rounded bg-fill-hover px-1.5 py-px text-[10.5px] text-text-muted">{b.slug}</code>
          {!b.is_active && (
            <span className="rounded-full bg-red/15 px-2 py-px text-[10px] font-bold text-red-300">Inactive</span>
          )}
        </div>
      ),
    },
    {
      key: 'tenant',
      header: 'Tenant',
      sortable: true,
      width: 220,
      value: (b) => b.tenant,
      render: (b) => (
        <span className="flex items-center gap-1.5 text-[12px] text-text-mid">
          <Building2 size={12} className="text-text-muted" aria-hidden /> {b.tenant}
        </span>
      ),
    },
    {
      key: 'created_at',
      header: 'Created',
      sortable: true,
      width: 140,
      value: (b) => b.created_at,
      render: (b) => (
        <span className="text-[10.5px] text-text-faint">{new Date(b.created_at).toLocaleDateString()}</span>
      ),
    },
    ...(canManage
      ? [
          {
            key: 'actions',
            header: 'Actions',
            align: 'right' as const,
            width: 100,
            render: (b: BrandRow) => (
              <div className="flex items-center justify-end gap-1">
                <button
                  onClick={() => openEdit(b)}
                  disabled={busy}
                  title={`Edit ${b.name}`}
                  aria-label={`Edit brand ${b.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Pencil size={13} aria-hidden />
                </button>
                <button
                  onClick={() => deleteBrand(b)}
                  disabled={busy}
                  title={`Delete ${b.name}`}
                  aria-label={`Delete brand ${b.name}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </div>
            ),
          },
        ]
      : []),
  ]

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <p className="text-[11.5px] leading-relaxed text-text-mid">
        A <b>tenant</b> is the parent organization; a <b>brand</b> is a business unit or client it serves. Contacts are
        shared across a tenant&apos;s brands. Admins see every brand in their tenant; agents only the brands assigned to
        them (set per user under Users &amp; Roles).
        {me?.tenant && (
          <>
            {' '}
            You are in tenant <b>{me.tenant}</b>.
          </>
        )}
      </p>

      {msg && (
        <div className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</div>
      )}

      <DataTable
        columns={columns}
        rows={brands}
        rowKey={(b) => b.id}
        searchKeys={['name', 'tenant']}
        searchPlaceholder="Search brands…"
        empty="No brands yet — add the first one."
        initialSort={{ key: 'tenant', dir: 'asc' }}
        toolbarRight={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> Add brand
            </Button>
          ) : undefined
        }
      />

      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title={editing ? `Edit ${editing.name}` : 'Add brand'}
        subtitle="A brand is a business unit or client served by a tenant."
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={saveBrand}
              disabled={busy || !name.trim() || (!editing && isSuperadmin && !tenant.trim())}
            >
              {busy ? (
                <Loader2 size={13} className="animate-spin" aria-hidden />
              ) : editing ? (
                <Pencil size={13} aria-hidden />
              ) : (
                <Plus size={13} aria-hidden />
              )}{' '}
              {editing ? 'Save changes' : 'Add brand'}
            </Button>
          </>
        }
      >
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
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
          {isSuperadmin && !editing && (
            <label className="block sm:col-span-2">
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
          {editing && (
            <label className="flex cursor-pointer items-center gap-2 sm:col-span-2">
              <input
                type="checkbox"
                checked={active}
                onChange={(e) => setActive(e.target.checked)}
                style={{ accentColor: '#3B82F6' }}
              />
              <span className="text-[12px] text-text-primary">
                Active
                <span className="ml-1 text-[10.5px] text-text-muted">
                  — inactive brands stay on record but drop out of routing.
                </span>
              </span>
            </label>
          )}
        </div>
      </Modal>
    </div>
  )
}
