'use client'

/**
 * One lesson the AI learned (or may learn): the question it applies to, the
 * good answer / the correction / the rule, where it came from (the ratings
 * behind it, each with a link to its conversation) and the actions its status
 * allows — approve / edit / dismiss a suggestion; edit / archive / turn an
 * active one into knowledge; restore or delete an archived one.
 */

import Link from 'next/link'
import { useState } from 'react'
import { Archive, BookOpen, Check, ExternalLink, Loader2, Pencil, RotateCcw, ThumbsDown, ThumbsUp, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { channelLabel } from '@/components/ui/ChannelLogo'
import {
  LESSON_KIND_LABEL,
  sinceLabel,
  threadHref,
  type Lesson,
  type LessonWrite,
} from '@/lib/aiLearning'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const KIND_TONE: Record<string, string> = {
  example: 'bg-green/10 text-green-300',
  correction: 'bg-amber/15 text-amber-300',
  rule: 'bg-brand-400/10 text-brand-300',
}

const SOURCE_KIND: Record<string, string> = {
  explicit: 'rated',
  approved: 'approved as-is',
  edited: 'edited before sending',
  rejected: 'rejected',
}

export type LessonAction = 'approve' | 'dismiss' | 'archive' | 'restore' | 'delete' | 'knowledge'

function Quote({ label, text, tone = 'plain' }: { label: string; text: string; tone?: 'plain' | 'avoid' | 'good' }) {
  if (!text) return null
  return (
    <div className="min-w-0">
      <div
        className={`mb-0.5 text-[10.5px] font-semibold uppercase tracking-wide ${
          tone === 'avoid' ? 'text-[color:var(--red-ink)]' : tone === 'good' ? 'text-[color:var(--green-ink)]' : 'text-text-faint'
        }`}
      >
        {label}
      </div>
      <p
        className={`whitespace-pre-wrap break-words rounded-lg px-3 py-2 text-[13px] leading-relaxed ${
          tone === 'avoid' ? 'border border-dashed border-line-strong text-text-muted' : 'border border-line bg-card text-text-high'
        }`}
      >
        {text}
      </p>
    </div>
  )
}

export function LessonCard({
  lesson: l,
  canManage,
  canDelete,
  busy,
  highlight,
  onAction,
  onSave,
}: {
  lesson: Lesson
  canManage: boolean
  canDelete: boolean
  busy: boolean
  highlight?: boolean
  onAction: (l: Lesson, a: LessonAction) => void
  /** Save edits; `approve` also makes a suggestion live. Throws on failure. */
  onSave: (l: Lesson, patch: LessonWrite) => Promise<void>
}) {
  const [editing, setEditing] = useState(false)
  const [q, setQ] = useState(l.question)
  const [a, setA] = useState(l.answer)
  const [avoid, setAvoid] = useState(l.avoid)
  const [note, setNote] = useState(l.note)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const startEdit = () => {
    setQ(l.question)
    setA(l.answer)
    setAvoid(l.avoid)
    setNote(l.note)
    setError(null)
    setEditing(true)
  }

  const save = async (approve: boolean) => {
    setSaving(true)
    setError(null)
    try {
      const patch: LessonWrite = { question: q.trim(), answer: a.trim(), note: note.trim() }
      if (l.kind === 'correction') patch.avoid = avoid.trim()
      if (approve) patch.status = 'active'
      await onSave(l, patch)
      setEditing(false)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const rule = l.kind === 'rule'
  const answerLabel = rule ? 'Rule' : l.kind === 'correction' ? 'Say instead' : 'Answer like'

  return (
    <article
      id={`lesson-${l.id}`}
      aria-label={`${LESSON_KIND_LABEL[l.kind] ?? l.kind} lesson #${l.id}`}
      className={`rounded-xl border bg-card p-4 transition-colors sm:p-5 ${highlight ? 'border-text-muted ring-2 ring-brand-400/40' : 'border-line'}`}
    >
      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
        <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${KIND_TONE[l.kind] ?? 'bg-fill text-text-muted'}`}>
          {LESSON_KIND_LABEL[l.kind] ?? l.kind}
        </span>
        <span className="text-[12px] font-semibold text-text-faint">#{l.id}</span>
        <span className="text-[12px] text-text-muted">
          {l.entity_name || 'Organization-wide'}
          {l.channels?.length ? ` · ${l.channels.map(channelLabel).join(', ')}` : ''}
        </span>
        <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
          {l.source_count > 1 && (
            <span title="How many ratings or edits pointed at this lesson">from {l.source_count} ratings</span>
          )}
          {l.status === 'active' && (
            <span title={l.last_used_at ? `Last used ${sinceLabel(l.last_used_at)}` : 'Not used in a reply yet'}>
              used {l.uses} time{l.uses === 1 ? '' : 's'}
              {l.last_used_at ? ` · ${sinceLabel(l.last_used_at)}` : ''}
            </span>
          )}
          <span title={l.created_at}>{sinceLabel(l.created_at)}</span>
        </span>
      </div>

      {editing ? (
        <div className="mt-3 flex flex-col gap-2.5">
          {error && (
            <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-1.5 text-[12.5px] text-[color:var(--red-ink)]">
              {error}
            </div>
          )}
          {!rule && (
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">When a customer asks something like</span>
              <textarea rows={2} value={q} onChange={(e) => setQ(e.target.value)} className={`${fieldCls} resize-y`} />
            </label>
          )}
          {l.kind === 'correction' && (
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">Avoid saying</span>
              <textarea rows={2} value={avoid} onChange={(e) => setAvoid(e.target.value)} className={`${fieldCls} resize-y`} />
            </label>
          )}
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{rule ? 'The rule' : answerLabel}</span>
            <textarea rows={3} value={a} onChange={(e) => setA(e.target.value)} className={`${fieldCls} resize-y`} />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">Note (only your team sees it)</span>
            <input value={note} onChange={(e) => setNote(e.target.value)} className={fieldCls} />
          </label>
          <div className="flex flex-wrap gap-2">
            {l.status === 'suggested' ? (
              <>
                <Button size="sm" variant="primary" disabled={saving || !a.trim()} onClick={() => void save(true)}>
                  {saving ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />} Save &amp; approve
                </Button>
                <Button size="sm" variant="secondary" disabled={saving || !a.trim()} onClick={() => void save(false)}>
                  Save as suggestion
                </Button>
              </>
            ) : (
              <Button size="sm" variant="primary" disabled={saving || !a.trim()} onClick={() => void save(false)}>
                {saving ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />} Save
              </Button>
            )}
            <Button size="sm" variant="ghost" disabled={saving} onClick={() => setEditing(false)}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <div className={`mt-3 grid grid-cols-1 gap-3 ${rule ? '' : l.kind === 'correction' ? 'lg:grid-cols-3' : 'md:grid-cols-2'}`}>
          {!rule && <Quote label="When asked something like" text={l.question} />}
          {l.kind === 'correction' && <Quote label="Avoid" text={l.avoid} tone="avoid" />}
          <Quote label={answerLabel} text={l.answer} tone={l.kind === 'correction' ? 'good' : 'plain'} />
        </div>
      )}

      {!editing && l.note && <p className="mt-2 text-[12px] italic text-text-muted">Note: {l.note}</p>}

      {l.feedback?.length > 0 && (
        <div className="mt-3">
          <div className="eyebrow mb-1">Where it came from</div>
          <ul className="flex flex-col gap-1">
            {l.feedback.map((f) => {
              const href = threadHref(f.thread)
              const Icon = f.rating < 0 ? ThumbsDown : ThumbsUp
              return (
                <li key={f.id} className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px] text-text-mid">
                  <Icon size={12} className="shrink-0 text-text-muted" aria-label={f.rating < 0 ? 'Not helpful' : 'Helpful'} />
                  <span className="font-semibold text-text-high">{f.user || 'System'}</span>
                  <span className="text-text-muted">{SOURCE_KIND[f.kind] ?? f.kind}</span>
                  <span className="text-text-faint">{sinceLabel(f.created_at)}</span>
                  {href && (
                    <Link href={href} className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high hover:underline">
                      Open conversation <ExternalLink size={11} aria-hidden />
                    </Link>
                  )}
                </li>
              )
            })}
          </ul>
        </div>
      )}

      {!editing && (canManage || canDelete) && (
        <div className="mt-4 flex flex-wrap items-center gap-2">
          {canManage && l.status === 'suggested' && (
            <>
              <Button size="sm" variant="primary" disabled={busy} onClick={() => onAction(l, 'approve')}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />} Approve
              </Button>
              <Button size="sm" variant="secondary" disabled={busy} onClick={startEdit}>
                <Pencil size={13} aria-hidden /> Edit
              </Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => onAction(l, 'dismiss')}>
                <X size={13} aria-hidden /> Dismiss
              </Button>
            </>
          )}
          {canManage && l.status === 'active' && (
            <>
              <Button size="sm" variant="secondary" disabled={busy} onClick={startEdit}>
                <Pencil size={13} aria-hidden /> Edit
              </Button>
              {!rule && (
                <Button size="sm" variant="secondary" disabled={busy} onClick={() => onAction(l, 'knowledge')}>
                  <BookOpen size={13} aria-hidden /> Turn into knowledge
                </Button>
              )}
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => onAction(l, 'archive')}>
                <Archive size={13} aria-hidden /> Archive
              </Button>
            </>
          )}
          {canManage && l.status === 'archived' && (
            <Button size="sm" variant="secondary" disabled={busy} onClick={() => onAction(l, 'restore')}>
              <RotateCcw size={13} aria-hidden /> Make active again
            </Button>
          )}
          {canDelete && (l.status === 'archived' || l.status === 'dismissed') && (
            <Button size="sm" variant="danger" disabled={busy} onClick={() => onAction(l, 'delete')}>
              <Trash2 size={13} aria-hidden /> Delete
            </Button>
          )}
          {l.approved_by && l.status === 'active' && (
            <span className="ml-auto text-[11.5px] text-text-faint">Approved by {l.approved_by}</span>
          )}
        </div>
      )}
    </article>
  )
}
