'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { TagAssignEditor, type TagValueItem } from '@/components/tags/TagAssignEditor'
import { hasCap } from '@/lib/auth'
import { useApp } from '@/lib/AppContext'
import { fetchContactTags, fetchTags, saveContactTags, type TagDef } from '@/lib/tags'

const sameItems = (a: TagValueItem[], b: TagValueItem[]) => JSON.stringify(a) === JSON.stringify(b)

/**
 * The contact's registry tags (boss / employee / driver…) with their attribute
 * values, on the contact detail page. Nothing is saved until Save — the PUT
 * replaces the contact's whole tag set, and the server writes the names back
 * onto Contact.tags for flows, automations and the AI.
 */
export default function ContactTagsCard({ contactId }: { contactId: number }) {
  const { reloadContacts } = useApp()
  const [available, setAvailable] = useState<TagDef[]>([])
  const [saved, setSaved] = useState<TagValueItem[]>([])
  const [draft, setDraft] = useState<TagValueItem[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('tags.manage')), [])

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [mine, all] = await Promise.all([fetchContactTags(contactId), fetchTags('contact')])
      const items = mine.tags.map((t) => ({ tag_id: t.tag_id, values: t.values ?? {} }))
      setSaved(items)
      setDraft(items)
      // a tag on the contact that the registry list did not return (another
      // entity's scope) still has to render — synthesize it from the row
      const known = new Set(all.tags.map((t) => t.id))
      const extra: TagDef[] = mine.tags
        .filter((t) => !known.has(t.tag_id))
        .map((t) => ({
          id: t.tag_id, name: t.name, slug: t.slug, color: t.color, description: t.description,
          applies_to: 'contact', group: t.group, attributes: t.attributes, position: 999, entity: null,
          entity_id: null, entity_name: '', usage: 0, variable_prefix: '', created_at: t.created_at, updated_at: t.created_at,
        }))
      setAvailable([...all.tags, ...extra])
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [contactId])

  useEffect(() => {
    void load()
  }, [load])

  const dirty = useMemo(() => !sameItems(saved, draft), [saved, draft])

  const save = async () => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const d = await saveContactTags(
        contactId,
        draft.map((i) => ({ tag_id: i.tag_id, values: i.values })),
      )
      const items = d.tags.map((t) => ({ tag_id: t.tag_id, values: t.values ?? {} }))
      setSaved(items)
      setDraft(items)
      setNotice('Tags saved.')
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
        <div className="text-[12px] font-bold uppercase tracking-[0.06em] text-text-mid">Tags</div>
        <Link href="/tags" className="text-[11px] font-semibold text-text-muted hover:text-text-high hover:underline">
          Manage tags
        </Link>
      </div>

      {loading ? (
        <div className="flex items-center gap-2 text-[12px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Loading…
        </div>
      ) : (
        <>
          <TagAssignEditor
            available={available}
            value={draft}
            onChange={(next) => {
              setDraft(next)
              setNotice(null)
            }}
            disabled={!canManage || busy}
            noun="contact"
            compact
          />
          {available.length === 0 && canManage && (
            <p className="mt-2 text-[11.5px] text-text-muted">
              No contact tags exist yet —{' '}
              <Link href="/tags" className="font-semibold text-text-high hover:underline">
                create one
              </Link>
              .
            </p>
          )}
          {error && (
            <div role="alert" className="mt-2.5 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12px] font-semibold text-[color:var(--red-ink)]">
              {error}
            </div>
          )}
          {notice && !dirty && <p className="mt-2 text-[12px] font-semibold text-[color:var(--green-ink)]">{notice}</p>}
          {canManage && dirty && (
            <div className="mt-3 flex items-center justify-end gap-2 border-t border-line pt-3">
              <span className="mr-auto text-[11.5px] font-semibold text-[color:var(--amber-ink)]">Unsaved</span>
              <Button variant="ghost" size="sm" onClick={() => setDraft(saved)} disabled={busy}>
                Discard
              </Button>
              <Button variant="primary" size="sm" onClick={save} disabled={busy}>
                {busy && <Loader2 size={12} className="animate-spin" aria-hidden />} Save tags
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
