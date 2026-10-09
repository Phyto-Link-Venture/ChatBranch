'use client'

/**
 * "Add answer" for a knowledge gap: a compact knowledge-base FAQ editor,
 * prefilled with the customer's question, that writes straight to
 * /api/aicore/knowledge — so the AI can answer it next time. The full editor
 * (dates, tags, Help Center publishing…) is one link away on /knowledge,
 * which opens prefilled the same way. Errors render inside the modal.
 */

import Link from 'next/link'
import { useEffect, useState } from 'react'
import { BookOpen, ExternalLink, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { aiApi } from '@/lib/aiMonitor'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

/** /knowledge deep link that opens the full editor on a new FAQ. */
export function knowledgeAddHref(question: string): string {
  return `/knowledge?add=faq&question=${encodeURIComponent(question)}`
}

export function AddAnswerModal({
  question: initialQuestion,
  onClose,
  defaultEntity,
  onAdded,
}: {
  /** null = closed */
  question: string | null
  onClose: () => void
  defaultEntity: string | null
  onAdded: (title: string) => void
}) {
  const open = initialQuestion !== null
  const [question, setQuestion] = useState('')
  const [title, setTitle] = useState('')
  const [answer, setAnswer] = useState('')
  const [entity, setEntity] = useState<string | null>(defaultEntity)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (initialQuestion === null) return
    setQuestion(initialQuestion)
    setTitle('')
    setAnswer('')
    setEntity(defaultEntity)
    setError(null)
  }, [initialQuestion, defaultEntity])

  const save = async () => {
    if (!question.trim() || !answer.trim() || busy) return
    setBusy(true)
    setError(null)
    const t = title.trim() || question.trim().slice(0, 80)
    try {
      await aiApi('aicore/knowledge', {
        method: 'POST',
        body: JSON.stringify({
          kind: 'faq',
          title: t,
          question: question.trim(),
          content: answer.trim(),
          tags: [],
          channels: [],
          priority: 0,
          active: true,
          published: false,
          entity,
        }),
      })
      onAdded(t)
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
      title="Add an answer"
      subtitle="Saved to the knowledge base as an FAQ. The AI uses it from the next message."
      labelledById="add-answer-title"
      footer={
        <>
          <Link
            href={knowledgeAddHref(question)}
            className="mr-auto inline-flex items-center gap-1 text-[12.5px] font-semibold text-text-mid hover:text-text-high hover:underline"
          >
            Open the full editor <ExternalLink size={12} aria-hidden />
          </Link>
          <Button size="sm" variant="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button size="sm" variant="primary" onClick={() => void save()} disabled={busy || !question.trim() || !answer.trim()}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <BookOpen size={13} aria-hidden />} Add to knowledge
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
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">The question (how a customer asks it)</span>
          <textarea rows={2} value={question} onChange={(e) => setQuestion(e.target.value)} className={`${fieldCls} resize-y`} />
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">The answer</span>
          <textarea
            rows={4}
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            placeholder="What the AI should tell them"
            className={`${fieldCls} resize-y`}
          />
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Title (optional)</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder={question.slice(0, 80)} className={fieldCls} />
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Applies to</span>
          <EntityPicker ariaLabel="Applies to" value={entity} onChange={setEntity} globalLabel="All entities (organization-wide)" className="w-full" />
        </label>
      </div>
    </Modal>
  )
}
