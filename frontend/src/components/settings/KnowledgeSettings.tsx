'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AlertCircle,
  BookOpen,
  CheckCircle,
  Eye,
  Loader2,
  Lock,
  Plus,
  RefreshCw,
  Trash2,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { TagListInput } from '@/components/ui/TagListInput'
import { roleHeaders } from '@/lib/role'
import { useRole } from '@/lib/useRole'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import { EntityScopeSelect, ScopeBadge } from './EntityScopeSelect'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

interface Item {
  id: number
  kind: string
  title: string
  question: string
  content: string
  tags: string[]
  channels: string[]
  priority: number
  active: boolean
  entity_id: number | null
  /** Owning outlet slug; null = organization-wide (inherited by every entity). */
  entity: string | null
  entity_name: string
  /** "global" = inherited from the organization, "entity" = this outlet's own. */
  scope: 'global' | 'entity'
  starts_at: string | null
  ends_at: string | null
  live: boolean
  expired: boolean
  scheduled: boolean
  created_by: string
  updated_at: string | null
}

interface ListPayload {
  items: Item[]
  counts: Record<string, number>
  kinds: { id: string; label: string }[]
  budget: number
  entity?: string | null
  entity_name?: string
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/aicore/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const KIND_LABELS: Record<string, string> = {
  fact: 'Facts',
  policy: 'Policies',
  faq: 'FAQs',
  event: 'Events',
  notice: 'Notices',
}

const CHANNELS = ['whatsapp', 'email', 'telegram', 'facebook', 'instagram', 'voice']

/** Notices get a distinct amber treatment; expired items grey out rather than
 *  disappearing, so they can be re-dated and reused. */
function KindChip({ kind }: { kind: string }) {
  const amber = kind === 'notice'
  return (
    <span
      className="shrink-0 rounded-full px-2 py-px text-[10px] font-bold uppercase tracking-[0.04em]"
      style={
        amber
          ? { background: 'rgba(245,158,11,0.14)', color: 'var(--amber-ink)' }
          : { background: 'var(--fill)', color: 'var(--text-muted)' }
      }
    >
      {kind}
    </span>
  )
}

function StateChip({ item }: { item: Item }) {
  if (!item.active) return <span className="shrink-0 text-[10px] font-bold text-text-muted">Off</span>
  if (item.expired)
    return (
      <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--text-muted)' }}>
        Expired
      </span>
    )
  if (item.scheduled)
    return (
      <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--amber-ink)' }}>
        Scheduled
      </span>
    )
  return (
    <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--green-ink)' }}>
      Live
    </span>
  )
}

type Draft = Partial<Item> & { id?: number }

/** ISO <-> the value a datetime-local input wants. */
const toLocalInput = (iso: string | null | undefined) => (iso ? iso.slice(0, 16) : '')
const fromLocalInput = (v: string) => (v ? new Date(v).toISOString() : '')

/**
 * Settings → Knowledge Base. Everything a business needs to teach the AI is
 * typed here: opening hours, a return policy, a promo running 1–15 Aug,
 * "warehouse flooded, deliveries delayed 2 days".
 *
 * The block this produces is injected at the same two seams as contact memory
 * (chat system prompt + voice dispatch metadata), so it reaches all six
 * channels and live phone calls with no per-channel code. Retrieval is
 * keyword + recency + priority — no embeddings, on purpose.
 */
export function KnowledgeSettings() {
  // Entity scope. With an outlet selected the list shows the organization's
  // knowledge PLUS that outlet's own, each labelled — and new items default to
  // that outlet unless the scope picker says otherwise.
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const { role, ready } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'

  const [data, setData] = useState<ListPayload | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [kind, setKind] = useState<string>('')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)

  const [previewText, setPreviewText] = useState('')
  const [preview, setPreview] = useState<{ block: string; chars: number; budget: number } | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api<ListPayload>(`knowledge${entityQ && `?${entityQ}`}`)
      setData(d)
      setLoadError(null)
    } catch (e) {
      setData(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQ])

  useEffect(() => {
    load()
  }, [load])

  const items = useMemo(() => {
    const all = data?.items ?? []
    const filtered = kind ? all.filter((i) => i.kind === kind) : all
    // notices sort to the top; expired items sink
    const rank = (i: Item) => (i.expired ? 2 : i.kind === 'notice' ? 0 : 1)
    return [...filtered].sort((a, b) => rank(a) - rank(b) || b.priority - a.priority)
  }, [data, kind])

  const submit = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const body = {
        kind: draft.kind || 'fact',
        title: draft.title || '',
        question: draft.question || '',
        content: draft.content || '',
        tags: draft.tags || [],
        channels: draft.channels || [],
        priority: draft.priority ?? 0,
        active: draft.active ?? true,
        starts_at: draft.starts_at || '',
        ends_at: draft.ends_at || '',
        // Explicit scope: null = organization-wide (every outlet inherits it).
        entity: draft.entity ?? null,
      }
      if (draft.id) {
        await api(`knowledge/${draft.id}`, { method: 'PATCH', body: JSON.stringify(body) })
        setNotice('Knowledge updated — the AI uses it on the next message.')
      } else {
        await api('knowledge', { method: 'POST', body: JSON.stringify(body) })
        setNotice('Knowledge added — the AI uses it on the next message.')
      }
      setDraft(null)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const toggleActive = async (item: Item) => {
    try {
      await api(`knowledge/${item.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ active: !item.active }),
      })
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const remove = async (item: Item) => {
    if (!window.confirm(`Delete "${item.title}"? The AI stops mentioning it immediately.`)) return
    try {
      await api(`knowledge/${item.id}`, { method: 'DELETE' })
      setNotice(`"${item.title}" deleted.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const runPreview = async () => {
    try {
      const d = await api<{ block: string; chars: number; budget: number }>('knowledge/preview', {
        method: 'POST',
        body: JSON.stringify({ text: previewText, channel: '' }),
      })
      setPreview(d)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (!ready || (data === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> Loading knowledge base…
      </div>
    )
  }

  if (data === null) {
    return (
      <div className="py-6 text-[12px]">
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden />
          <span>Couldn&apos;t load the knowledge base — {loadError}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> Retry
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex items-start gap-2">
        <p className="min-w-0 max-w-[720px] flex-1 text-[11.5px] leading-relaxed text-text-mid">
          Everything the AI should know about your business, typed by you — no code, no redeploy. Facts and policies
          are always available; <span className="font-bold text-text-high">events</span> and{' '}
          <span className="font-bold text-text-high">notices</span> carry a date range and{' '}
          <span className="font-bold text-text-high">stop being mentioned on their own</span> when it ends. This
          reaches every channel and every voice call at once.
        </p>
        {canManage && (
          <div className="shrink-0">
            <Button onClick={() => setDraft({ kind: kind || 'fact', active: true, priority: 0, entity: activeEntity?.slug ?? null })}>
              <Plus size={13} aria-hidden /> New entry
            </Button>
          </div>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
          <Lock size={11} aria-hidden /> Read-only — only an admin can edit the knowledge base.
        </div>
      )}
      {error && (
        <div
          className="flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div
          className="flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]"
          style={{ color: 'var(--green-ink)' }}
        >
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {/* kind rail */}
      <div className="flex flex-wrap gap-1.5">
        <button
          onClick={() => setKind('')}
          className="cursor-pointer rounded-full border px-2.5 py-1 text-[11px] font-semibold transition-colors"
          style={{
            borderColor: kind === '' ? 'var(--brand-400, #3B82F6)' : 'var(--line)',
            color: kind === '' ? 'var(--brand-ink)' : 'var(--text-muted)',
          }}
        >
          All ({data.items.length})
        </button>
        {data.kinds.map((k) => (
          <button
            key={k.id}
            onClick={() => setKind(k.id)}
            className="cursor-pointer rounded-full border px-2.5 py-1 text-[11px] font-semibold transition-colors"
            style={{
              borderColor: kind === k.id ? 'var(--brand-400, #3B82F6)' : 'var(--line)',
              color: kind === k.id ? 'var(--brand-ink)' : 'var(--text-muted)',
            }}
          >
            {KIND_LABELS[k.id] || k.label} ({data.counts[k.id] ?? 0})
          </button>
        ))}
      </div>

      {/* editor */}
      {draft && (
        <div className="rounded-xl border border-line bg-fill-subtle p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-[12.5px] font-bold text-text-high">
              {draft.id ? `Edit "${draft.title}"` : 'New knowledge entry'}
            </span>
            <button
              onClick={() => setDraft(null)}
              aria-label="Close form"
              className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={14} aria-hidden />
            </button>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Type</span>
              <select
                value={draft.kind || 'fact'}
                onChange={(e) => setDraft({ ...draft, kind: e.target.value })}
                className={inputCls}
              >
                {data.kinds.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Priority</span>
              <input
                type="number"
                value={draft.priority ?? 0}
                onChange={(e) => setDraft({ ...draft, priority: Number(e.target.value) })}
                className={inputCls}
              />
            </label>
          </div>

          {/* Scope: organization-wide knowledge every outlet inherits, or one
              outlet's own. A branch-specific notice ("Studio 2 closed") must
              never reach another branch's customers. */}
          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Applies to</span>
            <EntityScopeSelect
              value={draft.entity ?? null}
              onChange={(slug) => setDraft({ ...draft, entity: slug })}
              globalLabel="All entities (organization-wide)"
              className="w-full"
            />
            <span className="mt-1 block text-[10.5px] text-text-muted">
              {draft.entity
                ? 'Only customers of this entity will hear it.'
                : 'Every entity inherits this.'}
            </span>
          </label>

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Title</span>
            <input
              value={draft.title || ''}
              placeholder="Opening hours"
              onChange={(e) => setDraft({ ...draft, title: e.target.value })}
              className={inputCls}
            />
          </label>

          {draft.kind === 'faq' && (
            <label className="mt-3 block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                Question (how a contact would ask it)
              </span>
              <input
                value={draft.question || ''}
                placeholder="How long does a refund take?"
                onChange={(e) => setDraft({ ...draft, question: e.target.value })}
                className={inputCls}
              />
            </label>
          )}

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Content</span>
            <textarea
              rows={4}
              value={draft.content || ''}
              placeholder="We're open 9am to 6pm, Monday to Saturday."
              onChange={(e) => setDraft({ ...draft, content: e.target.value })}
              className={`${inputCls} resize-y leading-[1.6]`}
            />
          </label>

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              Tags{' '}
              <span className="font-normal text-text-muted">(matching hints — type one, press Enter to add)</span>
            </span>
            <TagListInput
              value={draft.tags || []}
              placeholder="e.g. refund"
              addLabel="Add tag"
              onChange={(tags) => setDraft({ ...draft, tags })}
            />
          </label>

          <div className="mt-3">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">
              Channels (none selected = every channel)
            </span>
            <div className="flex flex-wrap gap-1.5">
              {CHANNELS.map((c) => {
                const on = (draft.channels || []).includes(c)
                return (
                  <button
                    key={c}
                    onClick={() =>
                      setDraft({
                        ...draft,
                        channels: on
                          ? (draft.channels || []).filter((x) => x !== c)
                          : [...(draft.channels || []), c],
                      })
                    }
                    className="cursor-pointer rounded-full border px-2 py-0.5 text-[10.5px] font-semibold transition-colors"
                    style={{
                      borderColor: on ? 'var(--brand-400, #3B82F6)' : 'var(--line)',
                      color: on ? 'var(--brand-ink)' : 'var(--text-muted)',
                    }}
                  >
                    {c}
                  </button>
                )
              })}
            </div>
          </div>

          <div className="mt-3 grid gap-3 md:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">Starts (optional)</span>
              <input
                type="datetime-local"
                value={toLocalInput(draft.starts_at)}
                onChange={(e) => setDraft({ ...draft, starts_at: fromLocalInput(e.target.value) })}
                className={inputCls}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                Ends (optional — it expires itself)
              </span>
              <input
                type="datetime-local"
                value={toLocalInput(draft.ends_at)}
                onChange={(e) => setDraft({ ...draft, ends_at: fromLocalInput(e.target.value) })}
                className={inputCls}
              />
            </label>
          </div>

          <label className="mt-3 flex items-center gap-2 text-[11.5px] text-text-mid">
            <input
              type="checkbox"
              checked={draft.active ?? true}
              onChange={(e) => setDraft({ ...draft, active: e.target.checked })}
            />
            Active
          </label>

          <div className="mt-3 flex items-center gap-2">
            <Button onClick={submit} disabled={busy || !(draft.title || '').trim() || !(draft.content || '').trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
              {draft.id ? 'Save changes' : 'Add entry'}
            </Button>
            <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {/* list */}
      <div className="flex flex-col gap-1.5">
        {items.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            Nothing here yet — add your opening hours, your return policy, or a notice about a delay.
          </div>
        )}
        {items.map((item) => (
          <div
            key={item.id}
            className="flex items-center gap-2.5 rounded-xl border border-line bg-card px-4 py-2.5"
            style={item.expired ? { opacity: 0.55 } : undefined}
          >
            <KindChip kind={item.kind} />
            <button
              onClick={() => canManage && setDraft({ ...item })}
              className="min-w-0 flex-1 cursor-pointer text-left"
            >
              <span className="block truncate text-[12px] font-bold text-text-high">{item.title}</span>
              <span className="block truncate text-[11px] text-text-muted">
                {item.question || item.content}
              </span>
            </button>
            {/* Only meaningful while viewing one outlet: says whether this row
                is inherited from the organization or belongs to this outlet. */}
            {activeEntity && (
              <ScopeBadge scope={item.scope} entityName={item.entity_name || activeEntity.name} />
            )}
            <StateChip item={item} />
            {canManage && (
              <>
                <button
                  onClick={() => toggleActive(item)}
                  title={item.active ? 'Turn off' : 'Turn on'}
                  aria-label={`${item.active ? 'Deactivate' : 'Activate'} ${item.title}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <BookOpen size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(item)}
                  title={`Delete ${item.title}`}
                  aria-label={`Delete ${item.title}`}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </>
            )}
          </div>
        ))}
      </div>

      {/* preview */}
      <div className="rounded-xl border border-line bg-card p-4">
        <div className="mb-1 text-[12.5px] font-bold text-text-high">Preview context</div>
        <p className="mb-3 max-w-[640px] text-[11px] leading-relaxed text-text-muted">
          The exact block the AI is handed before it answers. Type a message a contact might send — FAQs and policies
          only appear when their keywords match, which is why &ldquo;why did the bot say that?&rdquo; is a button here
          rather than a code investigation.
        </p>
        <div className="flex gap-2">
          <input
            value={previewText}
            placeholder="how long does a refund take?"
            onChange={(e) => setPreviewText(e.target.value)}
            className={inputCls}
          />
          <Button variant="secondary" onClick={runPreview}>
            <Eye size={13} aria-hidden /> Preview
          </Button>
        </div>
        {preview && (
          <div className="mt-3">
            <pre className="max-h-[260px] overflow-auto whitespace-pre-wrap rounded-lg border border-line bg-fill-subtle p-3 text-[11.5px] leading-[1.6] text-text-mid">
              {preview.block || '(nothing — the AI answers with no company knowledge for this message)'}
            </pre>
            <div className="mt-1 text-[10.5px] text-text-muted">
              {preview.chars.toLocaleString()} / {preview.budget.toLocaleString()} characters of the budget used
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
