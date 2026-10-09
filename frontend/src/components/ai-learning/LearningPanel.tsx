'use client'

/**
 * AI monitor → Learning. What the AI has learned from people's feedback and
 * how it finds things to say:
 *  - the learning mode (review first / learn from admins / off), saved as the
 *    entity-scoped `ai_learning_mode` behaviour setting;
 *  - retrieval status: which embedding provider powers semantic search, how
 *    much of the knowledge base is indexed, and a Re-index button;
 *  - the lessons: suggestions waiting for a person (approve / edit / dismiss,
 *    with the ratings behind each and links to their conversations), the
 *    active ones (search, filter, edit, archive, turn into knowledge) and the
 *    archived / dismissed ones; "+ Add a rule" writes one by hand.
 * Reads need `ai.monitor`; every change needs `ai.behaviour.manage`.
 */

import Link from 'next/link'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AlertCircle, Brain, GraduationCap, Loader2, Plus, RefreshCw, Search, Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card, CardTitle } from '@/components/ui/Card'
import { Select } from '@/components/ui/Select'
import { useAccess } from '@/lib/useAccess'
import { ApiError, aiApi, type BehaviourField, type BehaviourPayload } from '@/lib/aiMonitor'
import {
  LEARNING_MODES,
  LESSON_KIND_LABEL,
  LESSON_STATUS_LABEL,
  embeddingProviderLabel,
  learningApi,
  lessonsApi,
  type LearningStatus,
  type Lesson,
  type LessonKind,
  type LessonList,
  type LessonStatus,
  type LessonWrite,
} from '@/lib/aiLearning'
import { LessonCard, type LessonAction } from './LessonCard'
import { AddLessonModal } from './AddLessonModal'

const STATUSES: LessonStatus[] = ['suggested', 'active', 'archived', 'dismissed']

const searchCls =
  'h-8 w-full rounded-lg border border-line-strong bg-card pl-8 pr-3 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/* ---------- learning mode ---------- */

function ModeCard({
  entityQuery,
  entityName,
  canManage,
  statusMode,
}: {
  entityQuery: string
  entityName: string | null
  canManage: boolean
  statusMode: string | null
}) {
  const [field, setField] = useState<BehaviourField | null>(null)
  const [canWrite, setCanWrite] = useState(false)
  const [saving, setSaving] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const p = await aiApi<BehaviourPayload>(`ai/behaviour${entityQuery ? `?${entityQuery}` : ''}`)
      setField(p.fields.find((f) => f.key === 'ai_learning_mode') ?? null)
      setCanWrite(!!p.can_manage)
    } catch {
      setField(null)
    }
  }, [entityQuery])

  useEffect(() => {
    setMsg(null)
    void load()
  }, [load])

  const mode = field?.value || statusMode || ''
  const editable = !!field && canWrite && canManage

  const choose = async (next: string) => {
    if (!editable || next === mode || saving) return
    setSaving(next)
    setMsg(null)
    try {
      const p = await aiApi<BehaviourPayload>(`ai/behaviour${entityQuery ? `?${entityQuery}` : ''}`, {
        method: 'PUT',
        body: JSON.stringify({ values: { ai_learning_mode: next } }),
      })
      setField(p.fields.find((f) => f.key === 'ai_learning_mode') ?? null)
      setMsg({ ok: true, text: entityName ? `Saved for ${entityName}.` : 'Saved for the whole organization.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setSaving(null)
    }
  }

  const current = LEARNING_MODES.find((m) => m.id === mode)
  return (
    <Card className="px-5 py-4">
      <CardTitle className="mb-1">How the AI learns</CardTitle>
      <p className="mb-3 text-[12.5px] leading-relaxed text-text-muted">
        Every thumbs-up, thumbs-down, edit and rejection is recorded. Lessons drawn from them are added to the AI’s instructions as examples
        from past conversations — your hard rules still win.
      </p>
      <div
        role="radiogroup"
        aria-label="Learning mode"
        className="grid grid-cols-3 gap-0.5 rounded-lg border border-line-strong bg-card p-0.5"
      >
        {LEARNING_MODES.map((m) => {
          const on = m.id === mode
          return (
            <button
              key={m.id}
              type="button"
              role="radio"
              aria-checked={on}
              disabled={!editable && !on}
              onClick={() => void choose(m.id)}
              className={`flex items-center justify-center gap-1 rounded-md px-2 py-1.5 text-[12.5px] font-semibold transition-colors ${
                on ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover disabled:cursor-default disabled:hover:bg-transparent'
              }`}
            >
              {saving === m.id && <Loader2 size={12} className="animate-spin" aria-hidden />}
              {m.label}
            </button>
          )
        })}
      </div>
      <p className="mt-2 min-h-[36px] text-[12.5px] leading-relaxed text-text-mid">
        {current ? current.help : 'The learning mode isn’t available on this server yet.'}
      </p>
      <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
        {field && (
          <span>
            {field.scope === 'entity'
              ? `Set for ${entityName ?? 'this entity'}`
              : field.scope === 'global'
                ? entityName
                  ? 'Inherited from the organization'
                  : 'Organization default'
                : 'Built-in default'}
          </span>
        )}
        {!editable && field && <span>Changing it needs the “Edit AI behaviour settings” permission.</span>}
        {msg && (
          <span role={msg.ok ? 'status' : 'alert'} className={msg.ok ? 'text-[color:var(--green-ink)]' : 'text-[color:var(--red-ink)]'}>
            {msg.text}
          </span>
        )}
      </div>
    </Card>
  )
}

/* ---------- retrieval status ---------- */

function RetrievalCard({
  status,
  canManage,
  onReindexed,
}: {
  status: LearningStatus | null | undefined
  canManage: boolean
  onReindexed: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const e = status?.embedding

  const reindex = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const r = await learningApi.reindex()
      setMsg({
        ok: true,
        text: r.detail || (typeof r.queued === 'number' ? `Re-indexing ${r.queued} item${r.queued === 1 ? '' : 's'} in the background.` : 'Re-indexing in the background.'),
      })
      onReindexed()
    } catch (err) {
      setMsg({ ok: false, text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  const semantic = !!e?.configured
  const total = (e?.indexed ?? 0) + (e?.pending ?? 0)
  const pctDone = total ? Math.round(((e?.indexed ?? 0) / total) * 100) : 100

  return (
    <Card className="px-5 py-4">
      <CardTitle
        className="mb-1"
        right={
          <Link href="/settings/ai-providers" className="font-semibold text-text-mid hover:text-text-high hover:underline">
            Change provider
          </Link>
        }
      >
        Finding answers
      </CardTitle>
      {status === undefined ? (
        <div className="flex items-center gap-2 py-3 text-[12.5px] text-text-muted">
          <Loader2 size={13} className="animate-spin" aria-hidden /> Checking…
        </div>
      ) : status === null ? (
        <p className="text-[12.5px] leading-relaxed text-text-muted">
          Retrieval status isn’t available on this server yet. The AI matches knowledge by keywords.
        </p>
      ) : (
        <>
          <div className="mb-2 flex flex-wrap items-center gap-2">
            <span
              className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11.5px] font-semibold ${
                semantic ? 'bg-green/10 text-green-300' : 'bg-fill text-text-muted'
              }`}
            >
              {semantic ? <Sparkles size={11} aria-hidden /> : <Search size={11} aria-hidden />}
              {semantic ? 'Semantic + keyword search' : 'Keyword search only'}
            </span>
            {semantic && (
              <span className="text-[12px] text-text-muted">
                {embeddingProviderLabel(e?.provider ?? '')}
                {e?.model ? ` · ${e.model}` : ''}
              </span>
            )}
          </div>
          <p className="text-[12.5px] leading-relaxed text-text-muted">
            {semantic
              ? 'Knowledge and lessons are matched by meaning, so a reworded question still finds the right answer.'
              : 'Add an OpenAI, Gemini or local embedding provider in Settings → AI keys & models to match by meaning, not just words.'}
          </p>
          {semantic && (
            <div className="mt-3">
              <div className="mb-1 flex items-baseline justify-between gap-2 text-[12px]">
                <span className="text-text-mid">
                  <span className="font-semibold tabular-nums text-text-heading">{(e?.indexed ?? 0).toLocaleString()}</span> pieces
                  indexed
                  {(e?.pending ?? 0) > 0 && (
                    <>
                      {' · '}
                      <span className="font-semibold tabular-nums text-text-heading">{(e?.pending ?? 0).toLocaleString()}</span> waiting
                    </>
                  )}
                </span>
                <span className="tabular-nums text-text-faint">{pctDone}%</span>
              </div>
              <div className="h-1.5 w-full rounded-full bg-fill" role="progressbar" aria-valuenow={pctDone} aria-valuemin={0} aria-valuemax={100} aria-label="Indexed">
                <div className="h-1.5 rounded-full bg-primary" style={{ width: `${pctDone}%` }} />
              </div>
            </div>
          )}
          {e?.last_error && (
            <p role="alert" className="mt-2 flex items-start gap-1.5 text-[12px] text-[color:var(--red-ink)]">
              <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> Last indexing error: {e.last_error}
            </p>
          )}
          {canManage && semantic && (
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <Button size="sm" variant="secondary" disabled={busy} onClick={() => void reindex()}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <RefreshCw size={13} aria-hidden />} Re-index
                everything
              </Button>
              {msg && (
                <span role={msg.ok ? 'status' : 'alert'} className={`text-[12px] ${msg.ok ? 'text-text-mid' : 'text-[color:var(--red-ink)]'}`}>
                  {msg.text}
                </span>
              )}
            </div>
          )}
        </>
      )}
    </Card>
  )
}

/* ---------- the panel ---------- */

export function LearningPanel({
  entityQuery,
  entitySlug,
  entityName,
}: {
  entityQuery: string
  entitySlug: string | null
  entityName: string | null
}) {
  const { ready, can, role } = useAccess()
  const canManage = ready && can('ai.behaviour.manage')
  const canDelete = canManage && (role === 'admin' || role === 'superadmin')

  const [status, setStatus] = useState<LessonStatus>('suggested')
  const [kind, setKind] = useState('')
  const [q, setQ] = useState('')
  const [query, setQuery] = useState('')
  const [data, setData] = useState<LessonList | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<number | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string; href?: string; hrefLabel?: string } | null>(null)
  const [adding, setAdding] = useState(false)
  const [learning, setLearning] = useState<LearningStatus | null | undefined>(undefined)
  const [focusId, setFocusId] = useState<number | null>(null)
  const focused = useRef(false)

  // ?lesson=<id> (from a draft's "Based on" line) opens the active list on it
  useEffect(() => {
    const id = Number(new URLSearchParams(window.location.search).get('lesson'))
    if (Number.isFinite(id) && id > 0) {
      setFocusId(id)
      setStatus('active')
    }
  }, [])

  useEffect(() => {
    const t = window.setTimeout(() => setQuery(q.trim()), 300)
    return () => window.clearTimeout(t)
  }, [q])

  const loadStatus = useCallback(async () => {
    setLearning(await learningApi.status(entityQuery))
  }, [entityQuery])

  useEffect(() => {
    void loadStatus()
  }, [loadStatus])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await lessonsApi.list({ status, kind, q: query }, entityQuery))
      setUnavailable(false)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && (e.status === 404 || e.status === 405)) setUnavailable(true)
      else setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [status, kind, query, entityQuery])

  useEffect(() => {
    void load()
  }, [load])

  // scroll the deep-linked lesson into view once
  useEffect(() => {
    if (!focusId || focused.current || !data) return
    const el = document.getElementById(`lesson-${focusId}`)
    if (el) {
      focused.current = true
      el.scrollIntoView({ block: 'center', behavior: 'smooth' })
      window.setTimeout(() => setFocusId(null), 4000)
    }
  }, [data, focusId])

  const counts = data?.counts
  const lessons = useMemo(() => data?.lessons ?? [], [data])

  const act = async (l: Lesson, a: LessonAction) => {
    if (busy) return
    if (a === 'delete' && !window.confirm(`Delete lesson #${l.id} for good? This can’t be undone.`)) return
    setBusy(l.id)
    setNotice(null)
    try {
      if (a === 'knowledge') {
        const r = await lessonsApi.toKnowledge(l.id)
        setNotice({
          ok: true,
          text: `Lesson #${l.id} is now a knowledge base FAQ${r.knowledge_id ? ` (#${r.knowledge_id})` : ''}.`,
          href: '/knowledge',
          hrefLabel: 'Open the knowledge base',
        })
      } else if (a === 'delete') {
        await lessonsApi.remove(l.id)
        setNotice({ ok: true, text: `Deleted lesson #${l.id}.` })
      } else {
        const next: Record<Exclude<LessonAction, 'knowledge' | 'delete'>, LessonStatus> = {
          approve: 'active',
          dismiss: 'dismissed',
          archive: 'archived',
          restore: 'active',
        }
        await lessonsApi.update(l.id, { status: next[a] })
        setNotice({
          ok: true,
          text:
            a === 'approve' || a === 'restore'
              ? `Lesson #${l.id} is live — the AI uses it from the next reply.`
              : a === 'dismiss'
                ? `Dismissed lesson #${l.id}.`
                : `Archived lesson #${l.id} — the AI no longer uses it.`,
        })
      }
      await load()
    } catch (e) {
      setNotice({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(null)
    }
  }

  const saveLesson = async (l: Lesson, patch: LessonWrite) => {
    await lessonsApi.update(l.id, patch)
    setNotice({
      ok: true,
      text: patch.status === 'active' ? `Lesson #${l.id} saved and live.` : `Lesson #${l.id} saved.`,
    })
    await load()
  }

  const kindOptions = [
    { value: '', label: 'Any kind' },
    ...(Object.keys(LESSON_KIND_LABEL) as LessonKind[]).map((k) => ({ value: k, label: LESSON_KIND_LABEL[k] })),
  ]

  return (
    <div className="flex flex-col gap-4">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1.25fr)_minmax(0,1fr)]">
        <ModeCard entityQuery={entityQuery} entityName={entityName} canManage={canManage} statusMode={learning?.mode ?? null} />
        <RetrievalCard status={learning} canManage={canManage} onReindexed={() => void loadStatus()} />
      </div>

      {unavailable ? (
        <Card className="flex flex-col items-start gap-2 px-5 py-8">
          <GraduationCap size={22} className="text-text-faint" aria-hidden />
          <h3 className="display text-[19px] text-text-heading">Learning isn’t switched on here yet</h3>
          <p className="max-w-[560px] text-[13px] leading-relaxed text-text-muted">
            This server doesn’t keep lessons yet. Once it does, ratings on AI replies and the edits reviewers make turn into
            lessons that show up here.
          </p>
        </Card>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <div className="-mx-1 max-w-full overflow-x-auto px-1">
              <div className="inline-flex min-w-max rounded-lg border border-line-strong bg-card p-0.5" role="tablist" aria-label="Lesson status">
                {STATUSES.map((s) => (
                  <button
                    key={s}
                    type="button"
                    role="tab"
                    aria-selected={status === s}
                    onClick={() => setStatus(s)}
                    className={`flex items-center gap-1.5 rounded-md px-3 py-1 text-[12.5px] font-semibold transition-colors ${
                      status === s ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
                    }`}
                  >
                    {LESSON_STATUS_LABEL[s]}
                    {counts && counts[s] != null && (
                      <span
                        className={`rounded-full px-1.5 text-[11px] leading-[17px] tabular-nums ${
                          s === 'suggested' && counts[s] > 0 && status !== s
                            ? 'bg-accent font-bold text-[color:var(--on-accent)]'
                            : 'opacity-75'
                        }`}
                      >
                        {counts[s]}
                      </span>
                    )}
                  </button>
                ))}
              </div>
            </div>
            <div className="relative min-w-[180px] flex-1 basis-[200px]">
              <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-text-faint" aria-hidden />
              <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search lessons" className={searchCls} aria-label="Search lessons" />
            </div>
            <Select value={kind} onChange={setKind} options={kindOptions} ariaLabel="Kind of lesson" className="w-[150px]" />
            {loading && <Loader2 size={14} className="animate-spin text-text-faint" aria-label="Loading" />}
            {canManage && (
              <Button
                size="sm"
                variant="primary"
                className="ml-auto"
                onClick={() => {
                  setNotice(null)
                  setAdding(true)
                }}
              >
                <Plus size={13} aria-hidden /> Add a rule
              </Button>
            )}
          </div>

          {notice && (
            <div
              role={notice.ok ? 'status' : 'alert'}
              className={`rounded-lg border px-3 py-2 text-[12.5px] ${
                notice.ok ? 'border-line bg-well text-text-high' : 'border-red/30 bg-red/10 text-[color:var(--red-ink)]'
              }`}
            >
              {notice.text}
              {notice.href && (
                <Link href={notice.href} className="ml-2 font-semibold underline underline-offset-2">
                  {notice.hrefLabel}
                </Link>
              )}
            </div>
          )}
          {error && (
            <div role="alert" className="flex flex-wrap items-center gap-2 text-[12.5px] text-[color:var(--red-ink)]">
              Couldn’t load the lessons: {error}
              <Button size="sm" variant="ghost" onClick={() => void load()}>
                <RefreshCw size={13} aria-hidden /> Retry
              </Button>
            </div>
          )}

          {!data && !error && (
            <div className="flex items-center gap-2 p-4 text-[13px] text-text-muted">
              <Loader2 size={15} className="animate-spin" aria-hidden /> Loading lessons…
            </div>
          )}

          {data && lessons.length === 0 && (
            <Card className="flex flex-col items-start gap-2 px-5 py-8">
              <Brain size={22} className="text-text-faint" aria-hidden />
              <h3 className="display text-[19px] text-text-heading">
                {query || kind
                  ? 'No lessons match'
                  : status === 'suggested'
                    ? 'No suggestions waiting'
                    : status === 'active'
                      ? 'The AI hasn’t learned anything yet'
                      : `Nothing ${LESSON_STATUS_LABEL[status].toLowerCase()}`}
              </h3>
              <p className="max-w-[560px] text-[13px] leading-relaxed text-text-muted">
                {query || kind
                  ? 'Try another word or kind.'
                  : status === 'suggested'
                    ? 'Rate AI replies with the thumbs in Chats or here in the AI monitor — a thumbs-down with a correction, a reply a reviewer edited, or a thumbs-up on a good answer becomes a suggested lesson.'
                    : status === 'active'
                      ? 'Approve a suggestion or add a rule by hand. Active lessons are added to the AI’s instructions when a customer asks something similar.'
                      : 'Lessons you archive or dismiss stay here so you can bring them back.'}
              </p>
              {status === 'suggested' && !query && !kind && (counts?.active ?? 0) > 0 && (
                <Button size="sm" variant="secondary" onClick={() => setStatus('active')}>
                  See {counts?.active} active lesson{counts?.active === 1 ? '' : 's'}
                </Button>
              )}
            </Card>
          )}

          <div className={`flex flex-col gap-3 transition-opacity ${loading && data ? 'opacity-70' : ''}`}>
            {lessons.map((l) => (
              <LessonCard
                key={`${l.id}:${l.updated_at}`}
                lesson={l}
                canManage={canManage}
                canDelete={canDelete}
                busy={busy === l.id}
                highlight={focusId === l.id}
                onAction={(x, a) => void act(x, a)}
                onSave={saveLesson}
              />
            ))}
          </div>
        </>
      )}

      <AddLessonModal
        open={adding}
        onClose={() => setAdding(false)}
        defaultEntity={entitySlug}
        onCreated={(text) => {
          setNotice({ ok: true, text })
          if (status !== 'active') setStatus('active')
          else void load()
        }}
      />
    </div>
  )
}
