'use client'

/**
 * 👍 / 👎 on the AI-written bubbles of /chats.
 *
 * `useAiFeedback(messages)` loads the caller's own ratings for the AI messages
 * on screen with ONE `GET /api/ai/feedback?message_ids=` (re-asked only for
 * ids it has not seen), and stays dormant — rendering the plain "AI assistant"
 * label — when the caller lacks `ai.monitor` or the server has no feedback
 * endpoint. `<AiBubbleFeedback>` replaces that label inside the bubble:
 *  - desktop: a small pill to the bubble's left, shown on hover / keyboard focus;
 *  - phones (no hover): tap the "AI assistant" label to reveal the buttons
 *    inside the bubble.
 * The label carries a filled thumb once you have rated, so the state is
 * visible without hovering.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ThumbsDown, ThumbsUp } from 'lucide-react'
import { useAccess } from '@/lib/useAccess'
import { feedbackApi, messagePk, type MyFeedback } from '@/lib/aiLearning'
import { ThumbButtons, useFeedbackController } from './FeedbackThumbs'

interface MsgLike {
  id: string
  dir: string
  source: string
  text?: string
  private?: boolean
}

export interface AiFeedbackStore {
  enabled: boolean
  ratings: Record<number, MyFeedback>
  set: (pk: number, v: MyFeedback | null) => void
  revealed: number | null
  toggleReveal: (pk: number) => void
}

export function useAiFeedback(messages: MsgLike[]): AiFeedbackStore {
  const { ready, can } = useAccess()
  const allowed = ready && can('ai.monitor')
  const ids = useMemo(() => {
    const s = new Set<number>()
    for (const m of messages) {
      if (m.dir !== 'out' || m.source !== 'ai' || m.private) continue
      const pk = messagePk(m as MsgLike & { pk?: unknown })
      if (pk != null) s.add(pk)
    }
    return Array.from(s).sort((a, b) => a - b)
  }, [messages])
  const key = ids.join(',')

  const [ratings, setRatings] = useState<Record<number, MyFeedback>>({})
  const [available, setAvailable] = useState(false)
  const [revealed, setRevealed] = useState<number | null>(null)
  const asked = useRef<Set<number>>(new Set())

  useEffect(() => {
    if (!allowed || !key) return
    const need = key
      .split(',')
      .map(Number)
      .filter((id) => !asked.current.has(id))
    if (!need.length) return
    let alive = true
    need.forEach((id) => asked.current.add(id))
    void feedbackApi.mine(need).then((r) => {
      if (!alive) return
      if (r === null) {
        // no endpoint / refused: forget them so a later change asks again
        need.forEach((id) => asked.current.delete(id))
        return
      }
      setAvailable(true)
      setRatings((cur) => ({ ...cur, ...r }))
    })
    return () => {
      alive = false
    }
  }, [allowed, key])

  const set = useCallback((pk: number, v: MyFeedback | null) => {
    setRatings((cur) => {
      const next = { ...cur }
      if (v) next[pk] = v
      else delete next[pk]
      return next
    })
  }, [])

  const toggleReveal = useCallback((pk: number) => setRevealed((cur) => (cur === pk ? null : pk)), [])

  return { enabled: allowed && available, ratings, set, revealed, toggleReveal }
}

/** Drop-in for the bubble's "AI assistant" source label. */
export function AiBubbleFeedback({ m, store }: { m: MsgLike; store: AiFeedbackStore }) {
  const pk = messagePk(m as MsgLike & { pk?: unknown })
  if (!store.enabled || pk == null) return <span className="cb-bubble-source">AI assistant</span>
  return <Rated pk={pk} m={m} store={store} />
}

function Rated({ pk, m, store }: { pk: number; m: MsgLike; store: AiFeedbackStore }) {
  const value = store.ratings[pk] ?? null
  const onChange = useCallback((v: MyFeedback | null) => store.set(pk, v), [store, pk])
  const target = useMemo(() => ({ message_id: pk }), [pk])
  const c = useFeedbackController({ target, value, onChange, aiText: m.text })
  const revealed = store.revealed === pk
  const pinned = revealed || c.popoverOpen

  return (
    <>
      <span className="cb-bubble-source">
        <button
          type="button"
          onClick={() => store.toggleReveal(pk)}
          aria-expanded={revealed}
          aria-label={`AI assistant${value ? (value.rating === 1 ? ' — you rated this helpful' : ' — you rated this not helpful') : ''}. Rate this reply`}
          className="inline-flex cursor-pointer items-center gap-1 uppercase underline-offset-2 hover:underline"
        >
          AI assistant
          {value?.rating === 1 && <ThumbsUp size={9} strokeWidth={2.4} fill="currentColor" aria-hidden />}
          {value?.rating === -1 && <ThumbsDown size={9} strokeWidth={2.4} fill="currentColor" aria-hidden />}
        </button>
      </span>
      {/* desktop: hover pill to the bubble's left (the wrapper's padding bridges the gap) */}
      <span
        className={`absolute bottom-0 right-full hidden pr-1.5 transition-opacity sm:flex ${
          pinned
            ? 'pointer-events-auto opacity-100'
            : 'pointer-events-none opacity-0 [.cb-bubble:focus-within_&]:pointer-events-auto [.cb-bubble:focus-within_&]:opacity-100 [.cb-bubble:hover_&]:pointer-events-auto [.cb-bubble:hover_&]:opacity-100'
        }`}
        style={{ whiteSpace: 'nowrap' }}
      >
        <span className="flex items-center rounded-full border border-line bg-card p-0.5 text-text-muted shadow-pop">
          <ThumbButtons c={c} size="sm" />
        </span>
      </span>
      {/* phones: tap the label, the buttons appear inside the bubble */}
      {revealed && (
        <span className="mb-1 mt-0.5 flex items-center gap-2 sm:hidden" style={{ whiteSpace: 'normal' }}>
          <span className="text-[11px] opacity-75">Rate this reply</span>
          <ThumbButtons c={c} tone="bubble" />
        </span>
      )}
      {c.saved && (
        <span role="status" className="sr-only">
          {c.saved}
        </span>
      )}
      {c.popover}
    </>
  )
}
