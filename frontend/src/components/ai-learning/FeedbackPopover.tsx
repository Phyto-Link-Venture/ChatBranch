'use client'

/**
 * The 👎 popover: why the reply was wrong (multi-select reason chips), what it
 * should have said (the correction the AI can learn from) and a private note.
 * Portalled to <body> so a scrolling chat column or a card never clips it;
 * a bottom sheet on phones. Keyboard: focus starts on the first reason, Tab
 * stays inside, Esc closes and focus returns to the 👎 button. Errors render
 * INSIDE the popover (it sits over the page, so a page-level message would be
 * invisible).
 */

import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Loader2, ThumbsDown, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { FEEDBACK_REASONS, type MyFeedback } from '@/lib/aiLearning'

export interface DownFields {
  reasons: string[]
  note: string
  correction: string
}

const FOCUSABLE = 'button:not([disabled]), textarea:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])'

const fieldCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] leading-relaxed text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-brand-400'

export function FeedbackPopover({
  anchor,
  initial,
  aiText,
  onSave,
  onRemove,
  onClose,
}: {
  anchor: HTMLElement
  /** The caller's existing 👎, to edit. */
  initial: MyFeedback | null
  /** The AI's reply, shown faintly so the correction can be written against it. */
  aiText?: string
  onSave: (f: DownFields) => Promise<void>
  /** Present when there is a saved rating the caller may remove. */
  onRemove?: () => Promise<void>
  onClose: () => void
}) {
  const [reasons, setReasons] = useState<string[]>(initial?.reasons ?? [])
  const [note, setNote] = useState(initial?.note ?? '')
  const [correction, setCorrection] = useState(initial?.correction ?? '')
  const [busy, setBusy] = useState<'save' | 'remove' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [pos, setPos] = useState<{ top: number; left: number; width: number } | null>(null)
  const [sheet, setSheet] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)

  // place next to the 👎 (flip above near the bottom edge); a sheet on phones
  useLayoutEffect(() => {
    const place = () => {
      const vw = window.innerWidth
      const vh = window.innerHeight
      if (vw < 640) {
        setSheet(true)
        setPos(null)
        return
      }
      setSheet(false)
      const r = anchor.getBoundingClientRect()
      const width = Math.min(360, vw - 32)
      const h = boxRef.current?.offsetHeight ?? 380
      const left = Math.min(Math.max(16, r.left), vw - width - 16)
      const below = r.bottom + 6
      const top = below + h > vh - 12 && r.top - 6 - h > 12 ? r.top - 6 - h : Math.max(12, Math.min(below, vh - h - 12))
      setPos({ top, left, width })
    }
    place()
    window.addEventListener('resize', place)
    window.addEventListener('scroll', place, true)
    return () => {
      window.removeEventListener('resize', place)
      window.removeEventListener('scroll', place, true)
    }
  }, [anchor])

  // initial focus + restore focus to the 👎 on close
  useEffect(() => {
    const t = window.setTimeout(() => boxRef.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus(), 0)
    return () => {
      window.clearTimeout(t)
      anchor.focus?.()
    }
  }, [anchor])

  // click outside closes (the anchor itself toggles via its own handler)
  useEffect(() => {
    const onDown = (e: MouseEvent | TouchEvent) => {
      const t = e.target as Node
      if (boxRef.current?.contains(t) || anchor.contains(t)) return
      onClose()
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('touchstart', onDown)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('touchstart', onDown)
    }
  }, [anchor, onClose])

  const onKeyDown = (e: React.KeyboardEvent) => {
    // keys typed here never reach page shortcuts (the review queue's A / R…)
    e.stopPropagation()
    if (e.key === 'Escape') {
      onClose()
      return
    }
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
      e.preventDefault()
      void save()
      return
    }
    if (e.key !== 'Tab' || !boxRef.current) return
    const items = Array.from(boxRef.current.querySelectorAll<HTMLElement>(FOCUSABLE))
    if (!items.length) return
    const first = items[0]
    const last = items[items.length - 1]
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault()
      last.focus()
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault()
      first.focus()
    }
  }

  const toggle = (id: string) => setReasons((cur) => (cur.includes(id) ? cur.filter((r) => r !== id) : [...cur, id]))

  const save = async () => {
    if (busy) return
    setBusy('save')
    setError(null)
    try {
      await onSave({ reasons, note: note.trim(), correction: correction.trim() })
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  const remove = async () => {
    if (!onRemove || busy) return
    setBusy('remove')
    setError(null)
    try {
      await onRemove()
      onClose()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  if (typeof document === 'undefined') return null

  const body = (
    <div
      ref={boxRef}
      role="dialog"
      aria-modal="false"
      aria-labelledby="fb-pop-title"
      onKeyDown={onKeyDown}
      className={
        sheet
          ? 'fixed inset-x-0 bottom-0 z-[70] max-h-[88vh] overflow-y-auto rounded-t-2xl border border-line-strong bg-card px-4 pb-5 pt-3 shadow-pop'
          : 'fixed z-[70] max-h-[80vh] overflow-y-auto rounded-xl border border-line-strong bg-card p-3.5 shadow-pop'
      }
      style={sheet ? undefined : pos ? { top: pos.top, left: pos.left, width: pos.width } : { visibility: 'hidden', top: 0, left: 0 }}
    >
      {sheet && <div className="mx-auto mb-3 h-1 w-10 rounded-full bg-line-strong" aria-hidden />}
      {error && (
        <div role="alert" className="mb-2.5 rounded-lg border border-red/30 bg-red/10 px-2.5 py-1.5 text-[12px] text-[color:var(--red-ink)]">
          {error}
        </div>
      )}
      <div className="mb-2 flex items-center gap-2">
        <ThumbsDown size={14} className="text-text-muted" aria-hidden />
        <span id="fb-pop-title" className="text-[13px] font-semibold text-text-heading">
          What was wrong with this reply?
        </span>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close"
          className="ml-auto grid h-7 w-7 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          <X size={14} aria-hidden />
        </button>
      </div>

      <div className="mb-3 flex flex-wrap gap-1.5" role="group" aria-label="Reasons">
        {FEEDBACK_REASONS.map((r) => {
          const on = reasons.includes(r.id)
          return (
            <button
              key={r.id}
              type="button"
              aria-pressed={on}
              onClick={() => toggle(r.id)}
              className={`rounded-full border px-2.5 py-1 text-[12px] transition-colors ${
                on ? 'border-text-primary bg-fill-hover font-semibold text-text-heading' : 'border-line text-text-mid hover:bg-fill'
              }`}
            >
              {r.label}
            </button>
          )
        })}
      </div>

      <label className="block">
        <span className="mb-1 block text-[12px] font-semibold text-text-high">What should it have said?</span>
        <textarea
          rows={3}
          value={correction}
          onChange={(e) => setCorrection(e.target.value)}
          placeholder={aiText ? 'Write the reply you would have sent' : 'The better answer (optional)'}
          className={`${fieldCls} resize-y`}
        />
        <span className="mt-1 block text-[11px] leading-snug text-text-muted">
          Optional. A correction becomes a lesson the AI can learn from once it is approved.
        </span>
      </label>

      <label className="mt-2.5 block">
        <span className="mb-1 block text-[12px] font-semibold text-text-high">Note for your team</span>
        <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="Optional" className={fieldCls} />
      </label>

      <p className="mt-2 text-[11px] text-text-faint">The customer never sees any of this.</p>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        {onRemove && (
          <Button size="sm" variant="ghost" disabled={!!busy} onClick={() => void remove()}>
            {busy === 'remove' && <Loader2 size={13} className="animate-spin" aria-hidden />}
            Remove my rating
          </Button>
        )}
        <span className="ml-auto flex gap-2">
          <Button size="sm" variant="secondary" disabled={!!busy} onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" variant="primary" disabled={!!busy} onClick={() => void save()}>
            {busy === 'save' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <ThumbsDown size={13} aria-hidden />}
            {initial ? 'Update' : 'Send feedback'}
          </Button>
        </span>
      </div>
    </div>
  )

  return createPortal(
    sheet ? (
      <>
        <div className="fixed inset-0 z-[69]" style={{ background: 'var(--scrim)' }} aria-hidden />
        {body}
      </>
    ) : (
      body
    ),
    document.body,
  )
}
