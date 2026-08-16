'use client'

import { useCallback, useEffect, useState } from 'react'
import { Loader2, Plus, X } from 'lucide-react'
import { authHeaders } from '@/lib/auth'
import { getRole } from '@/lib/role'
import { useEntity } from '@/lib/EntityContext'
import { useApp } from '@/lib/AppContext'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

interface EntityRef {
  slug: string
  name: string
}

/**
 * Per-brand membership editor on the contact detail page. A contact profile is
 * shared tenant-wide; this shows which entities it is active on and lets an admin
 * link/unlink ONE entity at a time — WITHOUT deleting the shared profile (plain
 * contact delete removes it from every entity). Backed by
 * GET /v1/contacts/<id> (entities) + POST/DELETE /v1/contacts/<id>/entities.
 */
export default function ContactEntitiesCard({ contactId }: { contactId: number }) {
  const { entities: allEntities } = useEntity()
  const { reloadContacts } = useApp()
  const [linked, setLinked] = useState<EntityRef[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [canEdit, setCanEdit] = useState(false)

  useEffect(() => setCanEdit(['admin', 'superadmin'].includes(getRole())), [])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const res = await fetch(`${API}/v1/contacts/${contactId}`, {
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
      })
      const data = await res.json().catch(() => ({}))
      if (res.ok && Array.isArray(data.entities)) setLinked(data.entities as EntityRef[])
    } finally {
      setLoading(false)
    }
  }, [contactId])

  useEffect(() => {
    load()
  }, [load])

  const mutate = async (method: 'POST' | 'DELETE', slug: string) => {
    setBusy(slug)
    setError(null)
    try {
      const res = await fetch(`${API}/v1/contacts/${contactId}/entities`, {
        method,
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ entity: slug }),
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) {
        throw new Error((data as { error?: { message?: string } }).error?.message || `HTTP ${res.status}`)
      }
      setAdding(false)
      await load()
      await reloadContacts()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const linkedSlugs = new Set(linked.map((b) => b.slug))
  const available = allEntities.filter((b) => !linkedSlugs.has(b.slug))

  return (
    <div className="rounded-2xl border border-line bg-card p-4">
      <div className="mb-3 text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">Entities</div>

      {loading ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-1.5">
            {linked.length === 0 && (
              <span className="text-[11.5px] text-text-muted">Not linked to any entity.</span>
            )}
            {linked.map((b) => (
              <span
                key={b.slug}
                className="flex items-center gap-1 rounded-full bg-brand-400/10 px-2.5 py-1 text-[11px] font-semibold text-brand-300"
              >
                {b.name}
                {canEdit && (
                  <button
                    onClick={() => mutate('DELETE', b.slug)}
                    disabled={busy === b.slug}
                    title={`Remove from ${b.name}`}
                    aria-label={`Remove from ${b.name}`}
                    className="cursor-pointer text-brand-300/70 transition-colors hover:text-red-300 disabled:opacity-50"
                  >
                    {busy === b.slug ? <Loader2 size={11} className="animate-spin" /> : <X size={12} />}
                  </button>
                )}
              </span>
            ))}

            {canEdit && available.length > 0 && !adding && (
              <button
                onClick={() => setAdding(true)}
                className="flex items-center gap-1 rounded-full border border-dashed border-line-strong px-2.5 py-1 text-[11px] font-semibold text-text-muted transition-colors hover:bg-fill-hover"
              >
                <Plus size={12} /> Add entity
              </button>
            )}
          </div>

          {canEdit && adding && (
            <div className="mt-2 flex items-center gap-2">
              <select
                defaultValue=""
                onChange={(e) => e.target.value && mutate('POST', e.target.value)}
                disabled={!!busy}
                className="rounded-lg border border-line-strong bg-card px-2 py-1.5 text-[11.5px] text-text-high outline-none focus:border-brand-400"
              >
                <option value="" disabled>
                  Select an entity…
                </option>
                {available.map((b) => (
                  <option key={b.slug} value={b.slug}>
                    {b.name}
                  </option>
                ))}
              </select>
              <button
                onClick={() => setAdding(false)}
                className="text-[11px] font-semibold text-text-muted hover:text-text-high"
              >
                Cancel
              </button>
            </div>
          )}

          {error && <div className="mt-2 text-[11px] font-semibold text-red-300">{error}</div>}
          {canEdit && (
            <p className="mt-2.5 text-[10.5px] leading-[1.5] text-text-faint">
              Removing an entity unlinks this contact from it only — the shared profile and its
              other entities stay. Deleting the contact removes it from every entity.
            </p>
          )}
        </>
      )}
    </div>
  )
}
