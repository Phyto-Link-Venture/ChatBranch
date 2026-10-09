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
import { channelLabel } from '@/components/ui/ChannelLogo'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { lessonsApi, type LessonKind } from '@/lib/aiLearning'

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
      onCreated(kind === 'rule' ? 'Rule added — the AI follows it from the next reply.' : 'Example added — the AI uses it from the next reply.')
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
      title="Teach the AI"
      subtitle="Rules are always in the AI’s instructions. Examples are used when a customer asks something similar."
      labelledById="add-lesson-title"
      footer={
        <>
          <Button size="sm" variant="secondary" onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button size="sm" variant="primary" onClick={() => void create()} disabled={!valid || busy}>
            {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Plus size={13} aria-hidden />}
            {kind === 'rule' ? 'Add rule' : 'Add example'}
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
        <div className="inline-flex w-fit rounded-lg border border-line-strong bg-card p-0.5" role="radiogroup" aria-label="Kind of lesson">
          {(
            [
              { id: 'rule', label: 'A rule' },
              { id: 'example', label: 'A good answer' },
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
            <span className="mb-1 block text-[12px] font-semibold text-text-high">When a customer asks something like</span>
            <textarea
              rows={2}
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder="Can I change my delivery address after ordering?"
              className={`${fieldCls} resize-y`}
            />
          </label>
        )}
        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">{kind === 'rule' ? 'The rule' : 'Answer like this'}</span>
          <textarea
            rows={3}
            value={answer}
            onChange={(e) => setAnswer(e.target.value)}
            placeholder={
              kind === 'rule'
                ? 'Never promise same-day delivery — say “we’ll confirm the delivery date shortly”.'
                : 'Yes — reply with the new address within 2 hours of ordering and we’ll update it.'
            }
            className={`${fieldCls} resize-y`}
          />
        </label>

        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Applies to</span>
          <EntityPicker ariaLabel="Applies to" value={entity} onChange={setEntity} globalLabel="All entities (organization-wide)" className="w-full" />
          <span className="mt-1 block text-[11.5px] text-text-muted">
            {entity ? 'Only this entity’s conversations use it.' : 'Every entity inherits it.'}
          </span>
        </label>

        <div>
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Channels (none selected = every channel)</span>
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
                  {channelLabel(c)}
                </button>
              )
            })}
          </div>
        </div>

        <label className="block">
          <span className="mb-1 block text-[12px] font-semibold text-text-high">Note (only your team sees it)</span>
          <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="Why this rule exists" className={fieldCls} />
        </label>
      </div>
    </Modal>
  )
}
