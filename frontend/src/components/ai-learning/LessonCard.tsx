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
import { LESSON_KIND_LABEL, aiChannelLabel, threadHref, type Lesson, type LessonWrite } from '@/lib/aiLearning'
import { parseUtc } from '@/lib/aiMonitor'
import { useFormat, useT, type MessageKey } from '@/i18n'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

const KIND_TONE: Record<string, string> = {
  example: 'bg-green/10 text-green-300',
  correction: 'bg-amber/15 text-amber-300',
  rule: 'bg-brand-400/10 text-brand-300',
}

const SOURCE_KIND: Record<string, MessageKey> = {
  explicit: 'ai.lesson.sourceKind.explicit',
  approved: 'ai.lesson.sourceKind.approved',
  edited: 'ai.lesson.sourceKind.edited',
  rejected: 'ai.lesson.sourceKind.rejected',
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
  const t = useT()
  const f = useFormat()
  const since = (iso: string | null | undefined) => f.relative(parseUtc(iso))
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
  const answerLabel = rule
    ? t('ai.lessonKind.rule')
    : l.kind === 'correction'
      ? t('ai.lesson.sayInstead')
      : t('ai.lesson.answerLike')
  const kindName = LESSON_KIND_LABEL[l.kind] ? t(LESSON_KIND_LABEL[l.kind]) : l.kind

  return (
    <article
      id={`lesson-${l.id}`}
      aria-label={t('ai.lesson.ariaLabel', { kind: kindName, id: String(l.id) })}
      className={`rounded-xl border bg-card p-4 transition-colors sm:p-5 ${highlight ? 'border-text-muted ring-2 ring-brand-400/40' : 'border-line'}`}
    >
      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
        <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${KIND_TONE[l.kind] ?? 'bg-fill text-text-muted'}`}>
          {kindName}
        </span>
        <span className="text-[12px] font-semibold text-text-faint">#{l.id}</span>
        <span className="text-[12px] text-text-muted">
          {l.entity_name || t('ai.lesson.orgWide')}
          {l.channels?.length ? ` · ${l.channels.map((c) => aiChannelLabel(c, t)).join(', ')}` : ''}
        </span>
        <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-text-muted">
          {l.source_count > 1 && (
            <span title={t('ai.lesson.fromRatingsTitle')}>{t('ai.lesson.fromRatings', { count: l.source_count })}</span>
          )}
          {l.status === 'active' && (
            <span title={l.last_used_at ? t('ai.lesson.lastUsed', { when: since(l.last_used_at) }) : t('ai.lesson.notUsed')}>
              {t('ai.lesson.used', { count: l.uses })}
              {l.last_used_at ? ` · ${since(l.last_used_at)}` : ''}
            </span>
          )}
          <span title={f.dateTime(parseUtc(l.created_at))}>{since(l.created_at)}</span>
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
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.lesson.whenAsksLike')}</span>
              <textarea rows={2} value={q} onChange={(e) => setQ(e.target.value)} className={`${fieldCls} resize-y`} />
            </label>
          )}
          {l.kind === 'correction' && (
            <label className="block">
              <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.lesson.avoidSaying')}</span>
              <textarea rows={2} value={avoid} onChange={(e) => setAvoid(e.target.value)} className={`${fieldCls} resize-y`} />
            </label>
          )}
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{rule ? t('ai.lesson.theRule') : answerLabel}</span>
            <textarea rows={3} value={a} onChange={(e) => setA(e.target.value)} className={`${fieldCls} resize-y`} />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.common.noteTeamOnly')}</span>
            <input value={note} onChange={(e) => setNote(e.target.value)} className={fieldCls} />
          </label>
          <div className="flex flex-wrap gap-2">
            {l.status === 'suggested' ? (
              <>
                <Button size="sm" variant="primary" disabled={saving || !a.trim()} onClick={() => void save(true)}>
                  {saving ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}{' '}
                  {t('ai.lesson.saveApprove')}
                </Button>
                <Button size="sm" variant="secondary" disabled={saving || !a.trim()} onClick={() => void save(false)}>
                  {t('ai.lesson.saveSuggestion')}
                </Button>
              </>
            ) : (
              <Button size="sm" variant="primary" disabled={saving || !a.trim()} onClick={() => void save(false)}>
                {saving ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}{' '}
                {t('common.actions.save')}
              </Button>
            )}
            <Button size="sm" variant="ghost" disabled={saving} onClick={() => setEditing(false)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </div>
      ) : (
        <div className={`mt-3 grid grid-cols-1 gap-3 ${rule ? '' : l.kind === 'correction' ? 'lg:grid-cols-3' : 'md:grid-cols-2'}`}>
          {!rule && <Quote label={t('ai.lesson.whenAsked')} text={l.question} />}
          {l.kind === 'correction' && <Quote label={t('ai.lesson.avoid')} text={l.avoid} tone="avoid" />}
          <Quote label={answerLabel} text={l.answer} tone={l.kind === 'correction' ? 'good' : 'plain'} />
        </div>
      )}

      {!editing && l.note && <p className="mt-2 text-[12px] italic text-text-muted">{t('ai.lesson.note', { note: l.note })}</p>}

      {l.feedback?.length > 0 && (
        <div className="mt-3">
          <div className="eyebrow mb-1">{t('ai.lesson.whereFrom')}</div>
          <ul className="flex flex-col gap-1">
            {l.feedback.map((fb) => {
              const href = threadHref(fb.thread, fb.thread_id)
              const Icon = fb.rating < 0 ? ThumbsDown : ThumbsUp
              return (
                <li key={fb.id} className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px] text-text-mid">
                  <Icon
                    size={12}
                    className="shrink-0 text-text-muted"
                    aria-label={fb.rating < 0 ? t('ai.lesson.notHelpful') : t('ai.lesson.helpful')}
                  />
                  <span className="font-semibold text-text-high">{fb.user || t('ai.lesson.system')}</span>
                  <span className="text-text-muted">{SOURCE_KIND[fb.kind] ? t(SOURCE_KIND[fb.kind]) : fb.kind}</span>
                  <span className="text-text-faint">{since(fb.created_at)}</span>
                  {href && (
                    <Link href={href} className="inline-flex items-center gap-1 font-semibold text-text-mid hover:text-text-high hover:underline">
                      {t('ai.lesson.openConversation')} <ExternalLink size={11} aria-hidden />
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
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}{' '}
                {t('ai.lesson.approve')}
              </Button>
              <Button size="sm" variant="secondary" disabled={busy} onClick={startEdit}>
                <Pencil size={13} aria-hidden /> {t('common.actions.edit')}
              </Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => onAction(l, 'dismiss')}>
                <X size={13} aria-hidden /> {t('ai.lesson.dismiss')}
              </Button>
            </>
          )}
          {canManage && l.status === 'active' && (
            <>
              <Button size="sm" variant="secondary" disabled={busy} onClick={startEdit}>
                <Pencil size={13} aria-hidden /> {t('common.actions.edit')}
              </Button>
              {!rule && (
                <Button size="sm" variant="secondary" disabled={busy} onClick={() => onAction(l, 'knowledge')}>
                  <BookOpen size={13} aria-hidden /> {t('ai.lesson.toKnowledge')}
                </Button>
              )}
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => onAction(l, 'archive')}>
                <Archive size={13} aria-hidden /> {t('ai.lesson.archive')}
              </Button>
            </>
          )}
          {canManage && l.status === 'archived' && (
            <Button size="sm" variant="secondary" disabled={busy} onClick={() => onAction(l, 'restore')}>
              <RotateCcw size={13} aria-hidden /> {t('ai.lesson.makeActive')}
            </Button>
          )}
          {canDelete && (l.status === 'archived' || l.status === 'dismissed') && (
            <Button size="sm" variant="danger" disabled={busy} onClick={() => onAction(l, 'delete')}>
              <Trash2 size={13} aria-hidden /> {t('common.actions.delete')}
            </Button>
          )}
          {l.approved_by && l.status === 'active' && (
            <span className="ml-auto text-[11.5px] text-text-faint">{t('ai.lesson.approvedBy', { name: l.approved_by })}</span>
          )}
        </div>
      )}
    </article>
  )
}
