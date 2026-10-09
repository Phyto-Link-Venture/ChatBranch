'use client'

/**
 * 👍 / 👎 on one AI-written reply — a chat message or an AI-monitor draft.
 * 👍 rates at once (click again to take it back); 👎 opens the popover for
 * reasons, a correction and a note. One rating per person per reply: rating
 * again replaces it (the server keeps one row). Optimistic, with the previous
 * state restored and the reason shown if the server refuses.
 *
 * `useFeedbackController` holds the state so one reply can render its thumbs
 * in two places (the chat's hover pill on desktop, the inline row on phones)
 * and share one popover.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Loader2, ThumbsDown, ThumbsUp } from 'lucide-react'
import { feedbackApi, type FeedbackTarget, type LessonStatus, type MyFeedback } from '@/lib/aiLearning'
import { FeedbackPopover, type DownFields } from './FeedbackPopover'

export interface FeedbackController {
  value: MyFeedback | null
  busy: boolean
  error: string | null
  /** Short confirmation after a save ("Lesson suggested for review"). */
  saved: string | null
  popoverOpen: boolean
  up: () => void
  /** Toggle the 👎 popover anchored on `el`. */
  down: (el: HTMLElement) => void
  popover: React.ReactNode
}

function savedLine(status: LessonStatus | null | undefined): string {
  if (status === 'active') return 'Thanks — the AI uses this from now on.'
  if (status === 'suggested') return 'Thanks — a lesson is waiting for review.'
  return 'Thanks for the feedback.'
}

export function useFeedbackController({
  target,
  value,
  onChange,
  aiText,
}: {
  target: FeedbackTarget | null
  value: MyFeedback | null
  onChange: (v: MyFeedback | null) => void
  aiText?: string
}): FeedbackController {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const savedTimer = useRef<number | undefined>(undefined)

  useEffect(() => () => window.clearTimeout(savedTimer.current), [])

  const flash = useCallback((text: string) => {
    setSaved(text)
    window.clearTimeout(savedTimer.current)
    savedTimer.current = window.setTimeout(() => setSaved(null), 4000)
  }, [])

  const up = useCallback(() => {
    if (!target || busy) return
    const prev = value
    setError(null)
    // clicking an active 👍 takes it back (when we know which row to delete)
    if (prev?.rating === 1) {
      if (!prev.id) return
      setBusy(true)
      onChange(null)
      feedbackApi
        .remove(prev.id)
        .catch((e) => {
          onChange(prev)
          setError(e instanceof Error ? e.message : String(e))
        })
        .finally(() => setBusy(false))
      return
    }
    setBusy(true)
    onChange({ rating: 1, reasons: [], note: '', correction: '', id: prev?.id })
    feedbackApi
      .submit(target, { rating: 1 })
      .then((r) => {
        onChange({ id: r.id, rating: 1, reasons: r.reasons ?? [], note: r.note ?? '', correction: r.correction ?? '' })
        flash(savedLine(r.lesson_status))
      })
      .catch((e) => {
        onChange(prev)
        setError(e instanceof Error ? e.message : String(e))
      })
      .finally(() => setBusy(false))
  }, [target, busy, value, onChange, flash])

  const down = useCallback((el: HTMLElement) => {
    setError(null)
    setAnchor((cur) => (cur ? null : el))
  }, [])

  const saveDown = useCallback(
    async (f: DownFields) => {
      if (!target) return
      const r = await feedbackApi.submit(target, { rating: -1, reasons: f.reasons, note: f.note, correction: f.correction })
      onChange({
        id: r.id,
        rating: -1,
        reasons: r.reasons ?? f.reasons,
        note: r.note ?? f.note,
        correction: r.correction ?? f.correction,
      })
      flash(savedLine(r.lesson_status))
    },
    [target, onChange, flash],
  )

  const removeDown = useCallback(async () => {
    if (!value?.id) return
    await feedbackApi.remove(value.id)
    onChange(null)
  }, [value, onChange])

  const close = useCallback(() => setAnchor(null), [])

  const popover = anchor ? (
    <FeedbackPopover
      anchor={anchor}
      initial={value?.rating === -1 ? value : null}
      aiText={aiText}
      onSave={saveDown}
      onRemove={value?.rating === -1 && value.id ? removeDown : undefined}
      onClose={close}
    />
  ) : null

  return { value, busy, error, saved, popoverOpen: !!anchor, up, down, popover }
}

/** The two buttons. `tone="bubble"` sits on the ink of an outgoing chat bubble. */
export function ThumbButtons({
  c,
  size = 'md',
  tone = 'card',
  label = 'this reply',
}: {
  c: FeedbackController
  size?: 'sm' | 'md'
  tone?: 'card' | 'bubble'
  label?: string
}) {
  const rated = c.value?.rating
  const box = size === 'sm' ? 'h-7 w-7' : 'h-8 w-8'
  const icon = size === 'sm' ? 13 : 14
  const base =
    tone === 'bubble'
      ? 'grid place-items-center rounded-full transition-colors disabled:opacity-50'
      : 'grid place-items-center rounded-md transition-colors disabled:opacity-50'
  const idle = tone === 'bubble' ? 'opacity-80 hover:opacity-100' : 'text-text-muted hover:bg-fill-hover hover:text-text-high'
  const on = tone === 'bubble' ? 'opacity-100' : 'bg-fill text-text-heading'
  const bubbleBg = (active: boolean) =>
    tone === 'bubble' ? { background: `color-mix(in srgb, currentColor ${active ? 26 : 12}%, transparent)` } : undefined
  return (
    <span className="inline-flex items-center gap-1">
      <button
        type="button"
        onClick={c.up}
        disabled={c.busy}
        aria-pressed={rated === 1}
        aria-label={rated === 1 ? `You rated ${label} helpful — click to take it back` : `Rate ${label} helpful`}
        title={rated === 1 ? 'You said this was a good reply' : 'Good reply'}
        className={`${base} ${box} ${rated === 1 ? on : idle}`}
        style={bubbleBg(rated === 1)}
      >
        <ThumbsUp size={icon} strokeWidth={2} fill={rated === 1 ? 'currentColor' : 'none'} aria-hidden />
      </button>
      <button
        type="button"
        onClick={(e) => c.down(e.currentTarget)}
        disabled={c.busy}
        aria-pressed={rated === -1}
        aria-haspopup="dialog"
        aria-expanded={c.popoverOpen}
        aria-label={rated === -1 ? `You rated ${label} not helpful — edit your feedback` : `Rate ${label} not helpful`}
        title={rated === -1 ? 'You said this reply was wrong — click to edit' : 'Bad reply — tell the AI why'}
        className={`${base} ${box} ${rated === -1 || c.popoverOpen ? on : idle}`}
        style={bubbleBg(rated === -1 || c.popoverOpen)}
      >
        <ThumbsDown size={icon} strokeWidth={2} fill={rated === -1 ? 'currentColor' : 'none'} aria-hidden />
      </button>
      {c.busy && <Loader2 size={12} className="animate-spin opacity-60" aria-label="Saving" />}
      {c.error && !c.busy && (
        <span title={c.error} role="alert" aria-label={`Feedback not saved: ${c.error}`} className="text-[color:var(--red-ink)]">
          <AlertCircle size={13} aria-hidden />
        </span>
      )}
    </span>
  )
}
