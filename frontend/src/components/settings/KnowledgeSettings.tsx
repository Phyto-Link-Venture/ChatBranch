'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  BookOpen,
  CheckCircle,
  FileText,
  Loader2,
  Lock,
  Plus,
  RefreshCw,
  Trash2,
  X,
} from 'lucide-react'
import { Advanced } from '@/components/ui/Advanced'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { TagListInput } from '@/components/ui/TagListInput'
import { roleHeaders } from '@/lib/role'
import { useAccess } from '@/lib/useAccess'
import { useEntity, useEntityQuery } from '@/lib/EntityContext'
import {
  AvailabilityChips,
  AvailabilityPicker,
  availabilityOf,
  useDefaultAvailability,
  type EntityRefLite,
} from '@/components/ui/AvailabilityPicker'
import { expireSessionOn401 } from '@/lib/auth'
import { KnowledgeUploadButton } from '@/components/ai-learning/KnowledgeUpload'
import { RetrievalTester } from '@/components/ai-learning/RetrievalTester'
import { aiChannelLabel } from '@/lib/aiLearning'
import { Trans, useT, type MessageKey } from '@/i18n'

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
  /** Help Center: readable by the public at /help (distinct from `active`). */
  published: boolean
  slug: string
  category: string
  entity_id: number | null
  /** Owning outlet slug; null = organization-wide (inherited by every entity). */
  entity: string | null
  entity_name: string
  /** "global" = inherited from the organization, "entity" = this outlet's own. */
  scope: 'global' | 'entity'
  /** "Available to": every entity it was ticked for (primary first); [] = everyone. */
  entities?: EntityRefLite[]
  global?: boolean
  starts_at: string | null
  ends_at: string | null
  live: boolean
  expired: boolean
  scheduled: boolean
  created_by: string
  updated_at: string | null
  /** Set when the item was created by uploading a document (its file name). */
  source_file?: string
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

/** Knowledge kinds by id — the server's English labels are shown translated. */
const KIND_LABEL: Record<string, MessageKey> = {
  fact: 'ai.knowledge.kinds.fact',
  policy: 'ai.knowledge.kinds.policy',
  faq: 'ai.knowledge.kinds.faq',
  event: 'ai.knowledge.kinds.event',
  notice: 'ai.knowledge.kinds.notice',
}

const KIND_PLURAL: Record<string, MessageKey> = {
  fact: 'ai.knowledge.kindsPlural.fact',
  policy: 'ai.knowledge.kindsPlural.policy',
  faq: 'ai.knowledge.kindsPlural.faq',
  event: 'ai.knowledge.kindsPlural.event',
  notice: 'ai.knowledge.kindsPlural.notice',
}

const KIND_SHORT: Record<string, MessageKey> = {
  fact: 'ai.knowledge.kindsShort.fact',
  policy: 'ai.knowledge.kindsShort.policy',
  faq: 'ai.knowledge.kindsShort.faq',
  event: 'ai.knowledge.kindsShort.event',
  notice: 'ai.knowledge.kindsShort.notice',
}

const CHANNELS = ['whatsapp', 'email', 'telegram', 'facebook', 'instagram', 'voice']

/** Notices get a distinct amber treatment; expired items grey out rather than
 *  disappearing, so they can be re-dated and reused. */
function KindChip({ kind }: { kind: string }) {
  const t = useT()
  const amber = kind === 'notice'
  return (
    <span
      className="shrink-0 rounded-full px-2 py-px text-[10px] font-bold uppercase tracking-[0.04em]"
      style={
        amber
          ? { background: 'rgba(200,137,26,0.14)', color: 'var(--amber-ink)' }
          : { background: 'var(--fill)', color: 'var(--text-muted)' }
      }
    >
      {KIND_SHORT[kind] ? t(KIND_SHORT[kind]) : kind}
    </span>
  )
}

function StateChip({ item }: { item: Item }) {
  const t = useT()
  if (!item.active) return <span className="shrink-0 text-[10px] font-bold text-text-muted">{t('common.status.off')}</span>
  if (item.expired)
    return (
      <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--text-muted)' }}>
        {t('ai.knowledge.expired')}
      </span>
    )
  if (item.scheduled)
    return (
      <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--amber-ink)' }}>
        {t('ai.knowledge.scheduled')}
      </span>
    )
  return (
    <span className="shrink-0 text-[10px] font-bold" style={{ color: 'var(--green-ink)' }}>
      {t('ai.knowledge.live')}
    </span>
  )
}

/** `pick` = the "Available to" slugs being edited ([] = everyone). */
type Draft = Partial<Item> & { id?: number; pick?: string[] }

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
 * channels and live phone calls with no per-channel code. Standing notices,
 * events and facts always go in; FAQs, policies and uploaded documents are
 * retrieved per message (by meaning when an embedding provider is set in
 * Settings → AI keys & models, else by keywords). "Upload document" turns a
 * PDF / text / Markdown / CSV file into an item; "Test retrieval" shows what
 * the AI would be handed for a given customer message.
 */
export function KnowledgeSettings() {
  const t = useT()
  // Entity scope. With an outlet selected the list shows the organization's
  // knowledge PLUS that outlet's own, each labelled with who can use it. New
  // items start "Available to: Everyone" for an admin, else the caller's entity.
  const entityQ = useEntityQuery()
  const { activeEntity } = useEntity()
  const defaultAvailability = useDefaultAvailability()
  const { ready, can } = useAccess()
  const canManage = can('settings.ai.manage')

  const [data, setData] = useState<ListPayload | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [kind, setKind] = useState<string>('')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)

  // the server's kinds, named in the dashboard's language (its own label for an unknown kind)
  const kinds = useMemo(
    () => (data?.kinds ?? []).map((k) => ({ id: k.id, label: KIND_LABEL[k.id] ? t(KIND_LABEL[k.id]) : k.label })),
    [data, t],
  )

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

  /** Advanced values this entry holds that differ from a new entry's defaults
   *  (kind only counts on an existing item — a new one takes the filter's kind). */
  const advCount = draft
    ? [
        !!draft.id && (draft.kind || 'fact') !== 'fact',
        (draft.priority ?? 0) !== 0,
        (draft.tags?.length ?? 0) > 0,
        !!draft.published,
        (draft.channels?.length ?? 0) > 0,
        !!draft.starts_at,
        !!draft.ends_at,
      ].filter(Boolean).length
    : 0

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
        published: draft.published ?? false,
        category: draft.category || '',
        starts_at: draft.starts_at || '',
        ends_at: draft.ends_at || '',
        // "Available to": [] = organization-wide (every outlet inherits it)
        entities: draft.pick ?? availabilityOf(draft),
      }
      if (draft.id) {
        await api(`knowledge/${draft.id}`, { method: 'PATCH', body: JSON.stringify(body) })
        setNotice(t('ai.knowledge.updated'))
      } else {
        await api('knowledge', { method: 'POST', body: JSON.stringify(body) })
        setNotice(t('ai.knowledge.added'))
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
    if (!window.confirm(t('ai.knowledge.deleteConfirm', { title: item.title }))) return
    try {
      await api(`knowledge/${item.id}`, { method: 'DELETE' })
      setNotice(t('ai.knowledge.deleted', { title: item.title }))
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // ?add=faq&question=… (a knowledge gap's "Add answer" → the full editor) opens a prefilled draft once
  const deepLinked = useRef(false)
  useEffect(() => {
    if (deepLinked.current || !ready || !canManage || !data) return
    deepLinked.current = true
    const sp = new URLSearchParams(window.location.search)
    const add = sp.get('add')
    if (!add) return
    const question = sp.get('question') ?? ''
    setDraft({
      kind: data.kinds.some((k) => k.id === add) ? add : 'faq',
      question,
      title: question.slice(0, 80),
      active: true,
      priority: 0,
      pick: defaultAvailability,
    })
  }, [ready, canManage, data, defaultAvailability])

  if (!ready || (data === null && !loadError)) {
    return (
      <div className="flex items-center gap-1.5 py-6 text-[12px] text-text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden /> {t('ai.knowledge.loading')}
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
          <span>{t('ai.knowledge.loadError', { error: loadError ?? '' })}</span>
        </div>
        <Button variant="ghost" onClick={load} style={{ marginTop: 10 }}>
          <RefreshCw size={13} aria-hidden /> {t('common.actions.retry')}
        </Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 pt-3.5">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-start">
        <p className="min-w-0 max-w-[720px] flex-1 text-[11.5px] leading-relaxed text-text-mid">
          <Trans
            k="ai.knowledge.intro"
            values={{
              events: <span className="font-bold text-text-high">{t('ai.knowledge.introEvents')}</span>,
              notices: <span className="font-bold text-text-high">{t('ai.knowledge.introNotices')}</span>,
              stop: <span className="font-bold text-text-high">{t('ai.knowledge.introStop')}</span>,
            }}
          />
        </p>
        {canManage && (
          <div data-tour="knowledge-add" className="flex shrink-0 flex-wrap gap-2 sm:justify-end">
            <KnowledgeUploadButton
              kinds={kinds}
              defaultEntities={defaultAvailability}
              // the modal itself confirms the upload; the list just refreshes underneath
              onUploaded={() => void load()}
            />
            <Button onClick={() => setDraft({ kind: kind || 'fact', active: true, priority: 0, pick: defaultAvailability })}>
              <Plus size={13} aria-hidden /> {t('ai.knowledge.newEntry')}
            </Button>
          </div>
        )}
      </div>

      {!canManage && (
        <div className="flex items-center gap-1.5 text-[11px] font-semibold text-text-muted">
          <Lock size={11} aria-hidden /> {t('ai.knowledge.readOnly')}
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
      <div data-tour="knowledge-kinds" className="flex flex-wrap gap-1.5">
        <button
          onClick={() => setKind('')}
          className="cursor-pointer rounded-full border px-2.5 py-1 text-[11px] font-semibold transition-colors"
          style={{
            borderColor: kind === '' ? 'var(--brand-400, rgb(var(--c-brand)))' : 'var(--line)',
            color: kind === '' ? 'var(--brand-ink)' : 'var(--text-muted)',
          }}
        >
          {t('ai.knowledge.allCount', { count: data.items.length })}
        </button>
        {kinds.map((k) => (
          <button
            key={k.id}
            onClick={() => setKind(k.id)}
            className="cursor-pointer rounded-full border px-2.5 py-1 text-[11px] font-semibold transition-colors"
            style={{
              borderColor: kind === k.id ? 'var(--brand-400, rgb(var(--c-brand)))' : 'var(--line)',
              color: kind === k.id ? 'var(--brand-ink)' : 'var(--text-muted)',
            }}
          >
            {t('ai.knowledge.kindCount', {
              label: KIND_PLURAL[k.id] ? t(KIND_PLURAL[k.id]) : k.label,
              count: data.counts[k.id] ?? 0,
            })}
          </button>
        ))}
      </div>

      {/* editor */}
      {draft && (
        <div className="rounded-xl border border-line bg-fill-subtle p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-[12.5px] font-bold text-text-high">
              {draft.id ? t('ai.knowledge.editTitle', { title: draft.title ?? '' }) : t('ai.knowledge.newTitle')}
            </span>
            <button
              onClick={() => setDraft(null)}
              aria-label={t('ai.knowledge.closeForm')}
              className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={14} aria-hidden />
            </button>
          </div>

          <label className="block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('common.fields.title')}</span>
            <input
              value={draft.title || ''}
              placeholder={t('ai.knowledge.titlePlaceholder')}
              onChange={(e) => setDraft({ ...draft, title: e.target.value })}
              className={inputCls}
            />
          </label>

          {draft.kind === 'faq' && (
            <label className="mt-3 block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.question')}</span>
              <input
                value={draft.question || ''}
                placeholder={t('ai.knowledge.questionPlaceholder')}
                onChange={(e) => setDraft({ ...draft, question: e.target.value })}
                className={inputCls}
              />
            </label>
          )}

          <label className="mt-3 block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.content')}</span>
            <textarea
              rows={4}
              value={draft.content || ''}
              placeholder={t('ai.knowledge.contentPlaceholder')}
              onChange={(e) => setDraft({ ...draft, content: e.target.value })}
              className={`${inputCls} resize-y leading-[1.6]`}
            />
          </label>

          {/* Scope: organization-wide knowledge every outlet inherits, or one
              outlet's own. A branch-specific notice ("Studio 2 closed") must
              never reach another branch's customers. */}
          <AvailabilityPicker
            className="mt-3"
            value={draft.pick ?? availabilityOf(draft)}
            onChange={(pick) => setDraft({ ...draft, pick })}
          />

          <Advanced key={draft.id ?? 'new'} className="mt-3" count={advCount} forceOpen={advCount > 0}>
            <div className="grid gap-3 md:grid-cols-2">
              <label className="block">
                <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('common.fields.type')}</span>
                <Select
                  value={draft.kind || 'fact'}
                  onChange={(v) => setDraft({ ...draft, kind: v })}
                  options={kinds.map((k) => ({ value: k.id, label: k.label }))}
                  size="md"
                  ariaLabel={t('common.fields.type')}
                  className="w-full"
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.priority')}</span>
                <input
                  type="number"
                  value={draft.priority ?? 0}
                  onChange={(e) => setDraft({ ...draft, priority: Number(e.target.value) })}
                  className={inputCls}
                />
              </label>
            </div>

            <label className="block">
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">
                {t('common.fields.tags')}{' '}
                <span className="font-normal text-text-muted">{t('ai.knowledge.tagsHint')}</span>
              </span>
              <TagListInput
                value={draft.tags || []}
                placeholder={t('ai.knowledge.tagsPlaceholder')}
                addLabel={t('ai.knowledge.addTag')}
                onChange={(tags) => setDraft({ ...draft, tags })}
              />
            </label>

            {/* Help Center: publishing is SEPARATE from active — an internal note
                stays active and unpublished; a public FAQ is both. */}
            <div className="rounded-lg border border-line bg-fill-subtle p-3">
              <label className="flex cursor-pointer items-center gap-2">
                <input
                  type="checkbox"
                  checked={draft.published ?? false}
                  onChange={(e) => setDraft({ ...draft, published: e.target.checked })}
                  className="h-3.5 w-3.5 accent-emerald-500"
                />
                <span className="text-[12px] text-text-primary">
                  {t('ai.knowledge.publish')}
                  <span className="ml-1 text-[10.5px] text-text-muted">{t('ai.knowledge.publishHint')}</span>
                </span>
              </label>
              {draft.published && (
                <label className="mt-2 block">
                  <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.category')}</span>
                  <input
                    value={draft.category || ''}
                    placeholder={t('ai.knowledge.categoryPlaceholder')}
                    onChange={(e) => setDraft({ ...draft, category: e.target.value })}
                    className={inputCls}
                  />
                  {draft.slug && (
                    <span className="mt-1 block text-[10.5px] text-text-muted">
                      {t('ai.knowledge.publicLink')} <code className="rounded bg-fill px-1">/help/{draft.slug}</code>
                    </span>
                  )}
                </label>
              )}
            </div>

            <div>
              <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.common.channelsNoneEvery')}</span>
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
                        borderColor: on ? 'var(--brand-400, rgb(var(--c-brand)))' : 'var(--line)',
                        color: on ? 'var(--brand-ink)' : 'var(--text-muted)',
                      }}
                    >
                      {aiChannelLabel(c, t)}
                    </button>
                  )
                })}
              </div>
            </div>

            <div className="grid gap-3 md:grid-cols-2">
              <label className="block">
                <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.starts')}</span>
                <input
                  type="datetime-local"
                  value={toLocalInput(draft.starts_at)}
                  onChange={(e) => setDraft({ ...draft, starts_at: fromLocalInput(e.target.value) })}
                  className={inputCls}
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[10.5px] font-bold text-text-high">{t('ai.knowledge.ends')}</span>
                <input
                  type="datetime-local"
                  value={toLocalInput(draft.ends_at)}
                  onChange={(e) => setDraft({ ...draft, ends_at: fromLocalInput(e.target.value) })}
                  className={inputCls}
                />
              </label>
            </div>

          </Advanced>

          <label className="mt-3 flex items-center gap-2 text-[11.5px] text-text-mid">
            <input
              type="checkbox"
              checked={draft.active ?? true}
              onChange={(e) => setDraft({ ...draft, active: e.target.checked })}
            />
            {t('common.status.active')}
          </label>

          <div className="mt-3 flex items-center gap-2">
            <Button onClick={submit} disabled={busy || !(draft.title || '').trim() || !(draft.content || '').trim()}>
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
              {draft.id ? t('ai.knowledge.saveChanges') : t('ai.knowledge.addEntry')}
            </Button>
            <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </div>
      )}

      {/* list */}
      <div data-tour="knowledge-list" className="flex flex-col gap-1.5">
        {items.length === 0 && (
          <div className="rounded-xl border border-dashed border-line-strong px-4 py-6 text-center text-[12px] text-text-muted">
            {t('ai.knowledge.emptyList')}
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
              {item.source_file && (
                <span className="mt-0.5 flex min-w-0 items-center gap-1 text-[10px] font-semibold text-text-muted sm:hidden">
                  <FileText size={10} className="shrink-0" aria-hidden />
                  <span className="truncate">{t('ai.knowledge.source', { file: item.source_file })}</span>
                </span>
              )}
            </button>
            {/* Only meaningful while viewing one outlet: says whether this row
                is inherited from the organization or belongs to this outlet. */}
            <AvailabilityChips entities={item.entities} global={item.global ?? item.entity_id === null} />

            {item.source_file && (
              <span
                className="hidden min-w-0 max-w-[180px] shrink items-center gap-1 rounded-full bg-fill px-2 py-px text-[9.5px] font-bold text-text-muted sm:inline-flex"
                title={t('ai.knowledge.uploadedFrom', { file: item.source_file })}
              >
                <FileText size={10} className="shrink-0" aria-hidden />
                <span className="truncate">{t('ai.knowledge.source', { file: item.source_file })}</span>
              </span>
            )}
            {item.published && (
              <span
                className="shrink-0 rounded-full bg-brand-400/15 px-2 py-px text-[9.5px] font-bold text-brand-300"
                title={t('ai.knowledge.publishedTitle')}
              >
                {t('ai.knowledge.published')}
              </span>
            )}
            <StateChip item={item} />
            {canManage && (
              <>
                <button
                  onClick={() => toggleActive(item)}
                  title={item.active ? t('ai.knowledge.turnOff') : t('ai.knowledge.turnOn')}
                  aria-label={
                    item.active
                      ? t('ai.knowledge.deactivate', { title: item.title })
                      : t('ai.knowledge.activate', { title: item.title })
                  }
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                >
                  <BookOpen size={13} aria-hidden />
                </button>
                <button
                  onClick={() => remove(item)}
                  title={t('ai.knowledge.deleteItem', { title: item.title })}
                  aria-label={t('ai.knowledge.deleteItem', { title: item.title })}
                  className="grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted transition-colors hover:bg-red/15 hover:text-red-300"
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              </>
            )}
          </div>
        ))}
      </div>

      <RetrievalTester entity={activeEntity?.slug ?? null} />
    </div>
  )
}
