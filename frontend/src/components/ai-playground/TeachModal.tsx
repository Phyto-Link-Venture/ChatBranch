'use client'

/**
 * Teach the AI from a playground turn, through the lessons API: "This should
 * have said…" makes a CORRECTION (the customer's question, what the AI said,
 * what it should say); 👍 keeps the reply as an EXAMPLE. The lesson belongs to
 * the playground's outlet and follows its learning mode — waiting for review
 * unless the mode is auto and the teacher an admin. Errors render inside.
 */

import { useEffect, useState } from 'react'
import { GraduationCap, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { lessonsApi } from '@/lib/aiLearning'
import { LESSON_NOTE } from '@/lib/aiPlayground'
import { useT } from '@/i18n'
import { fieldCls } from './SetupPane'

export interface TeachTarget {
  kind: 'good' | 'bad'
  question: string
  said: string
}

export function TeachModal({
  target,
  onClose,
  entity,
  entityName,
  status,
  learningMode,
  onSaved,
}: {
  target: TeachTarget | null
  onClose: () => void
  entity: string | null
  entityName: string | null
  status: 'active' | 'suggested'
  learningMode: string
  onSaved: (status: 'active' | 'suggested') => void
}) {
  const t = useT()
  const [question, setQuestion] = useState('')
  const [answer, setAnswer] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const good = target?.kind === 'good'

  useEffect(() => {
    if (!target) return
    setQuestion(target.question)
    setAnswer(target.kind === 'good' ? target.said : '')
    setError(null)
  }, [target])

  const valid = question.trim().length > 0 && answer.trim().length > 0

  const save = async () => {
    if (!target || !valid || busy) return
    setBusy(true)
    setError(null)
    try {
      await lessonsApi.create({
        kind: good ? 'example' : 'correction',
        question: question.trim().slice(0, 1000),
        answer: answer.trim().slice(0, 2000),
        avoid: good ? '' : target.said.slice(0, 2000),
        note: LESSON_NOTE,
        channels: [],
        entity,
        status,
      })
      onSaved(status)
      onClose()
    } catch (e) {
      setError(t('playground.errors.teach', { error: e instanceof Error ? e.message : String(e) }))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      open={target != null}
      onClose={onClose}
      title={good ? t('playground.teach.exampleTitle') : t('playground.teach.title')}
      subtitle={t('playground.teach.intro', { outlet: entityName ?? t('playground.teach.orgWide') })}
      labelledById="pg-teach-title"
      footer={
        <>
          <Button size="sm" variant="secondary" onClick={onClose} disabled={busy}>
            {t('common.actions.cancel')}
          </Button>
          <Button size="sm" variant="primary" onClick={() => void save()} disabled={!valid || busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <GraduationCap size={13} aria-hidden />}
            {t('playground.teach.save')}
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
        {learningMode === 'off' && (
          <p className="rounded-lg bg-amber/15 px-3 py-2 text-[12px] text-amber-300">{t('playground.teach.learningOff')}</p>
        )}
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('playground.teach.question')}</span>
          <textarea rows={2} value={question} onChange={(e) => setQuestion(e.target.value)} maxLength={1000}
            className={`${fieldCls} resize-y`} />
        </label>
        {!good && target && (
          <div>
            <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('playground.teach.said')}</span>
            <p className="max-h-32 overflow-y-auto whitespace-pre-wrap rounded-lg bg-well px-3 py-2 text-[12.5px] text-text-mid">
              {target.said}
            </p>
          </div>
        )}
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('playground.teach.answer')}</span>
          <textarea rows={4} autoFocus value={answer} onChange={(e) => setAnswer(e.target.value)} maxLength={2000}
            className={`${fieldCls} resize-y`} />
        </label>
      </div>
    </Modal>
  )
}
