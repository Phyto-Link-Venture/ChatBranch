'use client'

import { useCallback, useEffect, useState } from 'react'
import { Briefcase, Loader2, Plus, X } from 'lucide-react'
import Link from 'next/link'
import { authHeaders } from '@/lib/auth'
import { getRole } from '@/lib/role'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface BoundProvider {
  link_id: number
  resource_id: number
  name: string
  active: boolean
  resource_active: boolean
  relationship: string
  since: string | null
  attributes: Record<string, unknown>
}

interface ProviderOption {
  id: number
  name: string
  active: boolean
  attributes?: Record<string, unknown>
}

/** The provider's "Type" custom field (Teacher / Supplier…) if set — the
 *  out-of-the-box label that tells providers apart. */
const typeOf = (attrs?: Record<string, unknown>) => {
  const t = attrs?.type
  return typeof t === 'string' && t ? t : ''
}

/**
 * Service Providers bound to this contact (the contact ↔ provider binding seen
 * from the CONTACT side — student ↔ teacher, client ↔ supplier). The Service
 * Providers page shows the same crm.ResourceLink rows from the other direction.
 * Backed by GET /contacts/<id>/resources (reverse lookup) +
 * POST/DELETE /resources/<id>/links (the existing bind/unbind API).
 */
export default function ContactProvidersCard({ contactId }: { contactId: number }) {
  const [bound, setBound] = useState<BoundProvider[]>([])
  const [all, setAll] = useState<ProviderOption[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [pick, setPick] = useState('')
  const [relationship, setRelationship] = useState('')
  const [canEdit, setCanEdit] = useState(false)

  useEffect(() => setCanEdit(['admin', 'superadmin'].includes(getRole())), [])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [mine, everyone] = await Promise.all([
        fetch(`${API}/contacts/${contactId}/resources`, { headers: authHeaders() }),
        fetch(`${API}/resources`, { headers: authHeaders() }),
      ])
      const m = await mine.json().catch(() => ({}))
      const e = await everyone.json().catch(() => ({}))
      if (mine.ok && Array.isArray(m.resources)) setBound(m.resources as BoundProvider[])
      const list = Array.isArray(e.resources) ? e.resources : Array.isArray(e) ? e : []
      if (everyone.ok) setAll(list as ProviderOption[])
    } finally {
      setLoading(false)
    }
  }, [contactId])

  useEffect(() => {
    load()
  }, [load])

  const bind = async () => {
    const resourceId = Number(pick)
    if (!resourceId) return
    setBusy(resourceId)
    setError(null)
    try {
      const res = await fetch(`${API}/resources/${resourceId}/links`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ contact_id: contactId, relationship: relationship.trim() }),
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
      setAdding(false)
      setPick('')
      setRelationship('')
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const unbind = async (b: BoundProvider) => {
    setBusy(b.resource_id)
    setError(null)
    try {
      const res = await fetch(`${API}/resources/${b.resource_id}/links/${b.link_id}`, {
        method: 'DELETE',
        headers: authHeaders(),
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const boundIds = new Set(bound.map((b) => b.resource_id))
  const available = all.filter((p) => !boundIds.has(p.id))

  return (
    <div className="rounded-2xl border border-line bg-card p-4">
      <div className="mb-3 flex items-center justify-between gap-2">
        <div className="text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">Service Providers</div>
        <Link href="/providers" className="text-[10.5px] font-semibold text-brand-300 hover:underline">
          Manage
        </Link>
      </div>

      {loading ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-1.5">
            {bound.length === 0 && (
              <span className="text-[11.5px] text-text-muted">Not bound to any provider.</span>
            )}
            {bound.map((b) => {
              const t = typeOf(b.attributes)
              return (
                <span
                  key={b.link_id}
                  title={[t, b.relationship, b.since ? `since ${b.since}` : ''].filter(Boolean).join(' · ') || undefined}
                  className={`flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] font-semibold ${
                    b.active && b.resource_active
                      ? 'bg-brand-400/10 text-brand-300'
                      : 'bg-fill text-text-muted line-through'
                  }`}
                >
                  <Briefcase size={11} aria-hidden />
                  {b.name}
                  {t && <span className="rounded bg-black/15 px-1 text-[9.5px] font-bold uppercase tracking-wide">{t}</span>}
                  {b.relationship && <span className="font-normal opacity-80">· {b.relationship}</span>}
                  {canEdit && (
                    <button
                      onClick={() => unbind(b)}
                      disabled={busy === b.resource_id}
                      title={`Unbind ${b.name}`}
                      aria-label={`Unbind ${b.name}`}
                      className="cursor-pointer text-brand-300/70 transition-colors hover:text-red-300 disabled:opacity-50"
                    >
                      {busy === b.resource_id ? <Loader2 size={11} className="animate-spin" /> : <X size={12} />}
                    </button>
                  )}
                </span>
              )
            })}

            {canEdit && available.length > 0 && !adding && (
              <button
                onClick={() => setAdding(true)}
                className="flex items-center gap-1 rounded-full border border-dashed border-line-strong px-2.5 py-1 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover"
              >
                <Plus size={12} /> Bind provider
              </button>
            )}
          </div>

          {canEdit && adding && (
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <select
                value={pick}
                onChange={(e) => setPick(e.target.value)}
                disabled={!!busy}
                className="rounded-lg border border-line-strong bg-card px-2 py-1.5 text-[11.5px] text-text-high outline-none focus:border-brand-400"
              >
                <option value="" disabled>
                  Select a provider…
                </option>
                {available.map((p) => {
                  const t = typeOf(p.attributes)
                  return (
                    <option key={p.id} value={p.id}>
                      {p.name}
                      {t ? ` (${t})` : ''}
                      {p.active ? '' : ' — inactive'}
                    </option>
                  )
                })}
              </select>
              <input
                value={relationship}
                onChange={(e) => setRelationship(e.target.value)}
                placeholder="Relationship (e.g. student, client)"
                disabled={!!busy}
                className="w-[200px] rounded-lg border border-line-strong bg-card px-2 py-1.5 text-[11.5px] text-text-high outline-none placeholder:text-text-faint focus:border-brand-400"
              />
              <button
                onClick={bind}
                disabled={!pick || !!busy}
                className="rounded-lg bg-brand-500 px-2.5 py-1.5 text-[11px] font-bold text-white disabled:opacity-50"
              >
                {busy ? <Loader2 size={11} className="animate-spin" /> : 'Bind'}
              </button>
              <button
                onClick={() => {
                  setAdding(false)
                  setPick('')
                  setRelationship('')
                }}
                className="text-[11px] font-semibold text-text-muted hover:text-text-high"
              >
                Cancel
              </button>
            </div>
          )}

          {error && <div className="mt-2 text-[11px] font-semibold text-red-300">{error}</div>}
          {canEdit && all.length === 0 && (
            <p className="mt-2.5 text-[10.5px] leading-[1.5] text-text-faint">
              No providers yet — add teachers, suppliers, etc. on the{' '}
              <Link href="/providers" className="text-brand-300 hover:underline">
                Service Providers
              </Link>{' '}
              page, then bind them here.
            </p>
          )}
        </>
      )}
    </div>
  )
}
