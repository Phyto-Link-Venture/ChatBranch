'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Building2, CornerDownRight, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { EntityAccessGrid } from '@/components/bindings/EntityAccessGrid'
import { orderEntityTree, type TreeEntity } from '@/components/bindings/groups'
import { authHeaders, hasCap } from '@/lib/auth'
import { useApp } from '@/lib/AppContext'
import { useT } from '@/i18n'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new Error(d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

/**
 * Which entities this contact belongs to, on the contact detail page. A contact
 * profile is shared tenant-wide; linking/unlinking an entity never deletes it.
 * Editing uses the Permissions-page card pattern (a card per main entity with
 * its sub-entities) and saves a DIFF through POST /api/bindings/contact-entities.
 */
export default function ContactEntitiesCard({ contactId }: { contactId: number }) {
  const t = useT()
  const { reloadContacts } = useApp()
  const [pool, setPool] = useState<TreeEntity[]>([])
  const [linked, setLinked] = useState<number[]>([])
  const [draft, setDraft] = useState<number[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [canEdit, setCanEdit] = useState(false)
  useEffect(() => setCanEdit(hasCap('contacts.manage')), [])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [ents, members] = await Promise.all([
        api<{ entities: TreeEntity[] }>('v1/entities'),
        api<{ member_ids: number[] }>(`bindings/contact-entities?target=${contactId}`),
      ])
      setPool(ents.entities.map((e) => ({ id: e.id, name: e.name, parent_id: e.parent_id, slug: e.slug })))
      setLinked(members.member_ids)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [contactId])

  useEffect(() => {
    void load()
  }, [load])

  const ordered = useMemo(() => orderEntityTree(pool), [pool])
  const shown = ordered.filter((o) => linked.includes(o.row.id))
  const byId = new Map(pool.map((e) => [e.id, e]))

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const add = draft.filter((id) => !linked.includes(id))
      const remove = linked.filter((id) => !draft.includes(id))
      const d = await api<{ member_ids: number[] }>('bindings/contact-entities', {
        method: 'POST',
        body: JSON.stringify({ target: contactId, add, remove }),
      })
      setLinked(d.member_ids)
      setEditing(false)
      void reloadContacts()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="rounded-2xl border border-line bg-card p-4">
      <div className="mb-3 flex items-baseline justify-between gap-2">
        <div className="text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">{t('contacts.entities.title')}</div>
        {canEdit && !loading && (
          <button
            type="button"
            onClick={() => {
              setDraft(linked)
              setError(null)
              setEditing(true)
            }}
            className="text-[11px] font-semibold text-text-muted hover:text-text-high hover:underline"
          >
            {t('common.actions.edit')}
          </button>
        )}
      </div>

      {loading ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> {t('common.status.loading')}
        </div>
      ) : shown.length === 0 ? (
        <p className="text-[12px] text-text-muted">{t('contacts.entities.none')}</p>
      ) : (
        <ul className="flex flex-col gap-1.5">
          {shown.map(({ row, depth }) => {
            const parent = row.parent_id !== null ? byId.get(row.parent_id) : undefined
            return (
              <li key={row.id} className="flex min-w-0 items-center gap-1.5 text-[12.5px]">
                {depth === 0 ? (
                  <span className="shrink-0 rounded-sm bg-primary px-1 py-px text-[9px] font-bold uppercase tracking-[0.05em] text-on-primary">
                    {t('contacts.entities.main')}
                  </span>
                ) : (
                  <CornerDownRight size={12} className="shrink-0 text-text-faint" aria-hidden />
                )}
                <span className="truncate font-semibold text-text-high">{row.name}</span>
                {parent && (
                  <span className="truncate text-[11.5px] text-text-muted">{t('contacts.entities.under', { name: parent.name })}</span>
                )}
              </li>
            )
          })}
        </ul>
      )}
      {error && !editing && <div className="mt-2 text-[12px] font-semibold text-[color:var(--red-ink)]">{error}</div>}

      <Modal
        open={editing}
        onClose={() => setEditing(false)}
        title={t('contacts.entities.modalTitle')}
        subtitle={t('contacts.entities.modalSubtitle')}
        size="lg"
        footer={
          <>
            <span className="mr-auto text-[12px] text-text-muted">{t('contacts.entities.linked', { count: draft.length })}</span>
            <Button variant="ghost" onClick={() => setEditing(false)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="primary" onClick={save} disabled={busy}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Building2 size={13} aria-hidden />}{' '}
              {t('common.actions.save')}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          {error && (
            <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]">
              {error}
            </div>
          )}
          <EntityAccessGrid entities={pool} value={draft} onChange={setDraft} disabled={busy} />
        </div>
      </Modal>
    </div>
  )
}
