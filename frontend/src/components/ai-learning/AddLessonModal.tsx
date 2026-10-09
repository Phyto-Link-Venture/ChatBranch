'use client'

/**
 * "+ Add a rule" — a lesson written by hand: a general RULE ("never promise
 * same-day delivery") or a good-answer EXAMPLE (question → how to answer it).
 * Scoped like knowledge: organization-wide (every entity inherits it) or one
 * entity's own; optionally limited to some channels. Errors render inside the
 * modal.
 */

import { useEffect, useState } from 'react'
import { Loader2, Plus } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { aiChannelLabel, lessonsApi, type LessonKind } from '@/lib/aiLearning'
import { useT } from '@/i18n'

const CHANNELS = ['whatsapp', 'telegram', 'facebook', 'instagram', 'email', 'website']

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

export function AddLessonModal({
  open,
  onClose,
  defaultEntity,
  onCreated,
}: {
  open: boolean
  onClose: () => void
  defaultEntity: string | null
  onCreated: (text: string) => void
}) {
  const t = useT()
  const [kind, setKind] = useState<Extract<LessonKind, 'rule' | 'example'>>('rule')
  const [question, setQuestion] = useState('')
  const [answer, setAnswer] = useState('')
  const [note, setNote] = useState('')
  const [entity, setEntity] = useState<string | null>(defaultEntity)
  const [channels, setChannels] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setKind('rule')
    setQuestion('')
    setAnswer('')
    setNote('')
    setEntity(defaultEntity)
    setChannels([])
    setError(null)
  }, [open, defaultEntity])

  const valid = answer.trim().length > 0 && (kind === 'rule' || question.trim().length > 0)

  const create = async () => {
    if (!valid || busy) return
    setBusy(true)
    setError(null)
    try {
      await lessonsApi.create({
        kind,
        status: 'active',
        question: kind === 'rule' ? '' : question.trim(),
        answer: answer.trim(),
        note: note.trim(),
        channels,
        entity,
      })
      onCreated(kind === 'rule' ? t('ai.addLesson.ruleAdded') : t('ai.addLesson.exampleAdded'))
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('ai.addLesson.title')}
      subtitle={t('ai.addLesson.subtitle')}
      labelledById="add-lesson-title"
      footer={
        <>
          <Button size="sm" variant="secondary" onClick={onClose} disabled={busy}>
            {t('common.actions.cancel')}
          </Button>
          <Button size="sm" variant="primary" onClick={() => void create()} disabled={!valid || busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Plus size={13} aria-hidden />}
            {kind === 'rule' ? t('ai.addLesson.addRule') : t('ai.addLesson.addExample')}
          </Button>
        </>
      }
    >
      {error && (
        <div role="alert" className="mb-3 rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}
      <div className="flex flex-col gap-3.5">
        <div
          className="inline-flex w-fit rounded-lg border border-line-strong bg-card p-0.5"
          role="radiogroup"
          aria-label={t('ai.learning.kindOfLesson')}
        >
          {(
            [
              { id: 'rule', label: t('ai.addLesson.kindRule') },
              { id: 'example', label: t('ai.addLesson.kindExample') },
            ] as const
          ).map((k) => (
            <button
              key={k.id}
              type="button"
              role="radio"
              aria-checked={kind === k.id}
              onClick={() => setKind(k.id)}
              className={`rounded-md px-3 py-1 text-[12.5px] font-semibold transition-colors ${
                kind === k.id ? 'bg-primary text-on-primary' : 'text-text-mid hover:bg-fill-hover'
              }`}
            >
              {k.label}
            </button>
          ))}
        </div>

        {kind === 'example' && (
          <label className="block">
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.lesson.whenAsksLike')}</span>
            <textarea
              rows={2}
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder={t('ai.addLesson.questionPlaceholder')}
              className={`${fieldCls} resize-y`}
            />
          </label>
        )}
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">
            {kind === 'rule' ? t('ai.lesson.theRule') : t('ai.addLesson.answerLikeThis')}
          </span>
          <textarea
            rows={3}
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            placeholder={kind === 'rule' ? t('ai.addLesson.rulePlaceholder') : t('ai.addLesson.examplePlaceholder')}
            className={`${fieldCls} resize-y`}
          />
        </label>

        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.common.appliesTo')}</span>
          <EntityPicker
            ariaLabel={t('ai.common.appliesTo')}
            value={entity}
            onChange={setEntity}
            globalLabel={t('ai.common.allEntitiesOrg')}
            className="w-full"
          />
          <span className="mt-1 block text-[11.5px] text-text-muted">
            {entity ? t('ai.addLesson.onlyThisEntity') : t('ai.addLesson.everyInherits')}
          </span>
        </label>

        <div>
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.common.channelsNoneEvery')}</span>
          <div className="flex flex-wrap gap-1.5">
            {CHANNELS.map((c) => {
              const on = channels.includes(c)
              return (
                <button
                  key={c}
                  type="button"
                  aria-pressed={on}
                  onClick={() => setChannels((cur) => (on ? cur.filter((x) => x !== c) : [...cur, c]))}
                  className={`rounded-full border px-2.5 py-1 text-[12px] transition-colors ${
                    on ? 'border-text-primary bg-fill-hover font-semibold text-text-heading' : 'border-line text-text-mid hover:bg-fill'
                  }`}
                >
                  {aiChannelLabel(c, t)}
                </button>
              )
            })}
          </div>
        </div>

        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('ai.common.noteTeamOnly')}</span>
          <input value={note} onChange={(e) => setNote(e.target.value)} placeholder={t('ai.addLesson.whyRule')} className={fieldCls} />
        </label>
      </div>
    </Modal>
  )
}
