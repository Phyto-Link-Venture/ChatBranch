'use client'

/**
 * "Waiting for review" — the AI replies the review policy held back. One card
 * per draft: who it is for, what the customer said, the AI's proposed reply in
 * an editable box, why it was held, and the decision buttons. Keyboard:
 * J / K move between cards, A approves (or sends your edit), E edits,
 * R rejects. Decisions re-run every send-time guard on the server, so a
 * card can come back with "not sent" (opted out, window closed…) — that
 * message is shown on the card's feedback line rather than swallowed.
 */

import Link from 'next/link'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Check, ExternalLink, Inbox, Keyboard, Loader2, Pencil, Send, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { subscribeChatStream } from '@/lib/chatStream'
import {
  aiApi,
  decide,
  decisionFromError,
  type DecisionResult,
  type Draft,
  type DraftList,
} from '@/lib/aiMonitor'
import {
  ContextChips,
  ConversationSnippet,
  Countdown,
  DraftBookings,
  DraftHeader,
  HoldReasons,
  Kbd,
  chatHref,
  displayName,
} from './DraftBits'

const QUICK_REASONS = [
  'Wrong or made-up information',
  'Promised something we can’t do',
  'Wrong tone',
  'A person should answer this',
]

type Feedback = { ok: boolean; text: string; force?: boolean }

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none transition-colors focus:border-brand-400'

function isTyping(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false
  return el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName)
}

export function ReviewQueue({
  entityQuery,
  canDecide,
  modeLabel,
  onOpenSettings,
}: {
  entityQuery: string
  canDecide: boolean
  /** Plain-language current review mode, for the empty state. */
  modeLabel?: string
  onOpenSettings?: () => void
}) {
  const [drafts, setDrafts] = useState<Draft[] | null>(null)
  const [fetchedAt, setFetchedAt] = useState(() => Date.now())
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState(0)
  const [edits, setEdits] = useState<Record<number, string>>({})
  const [rejecting, setRejecting] = useState<number | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState<number | null>(null)
  const [feedback, setFeedback] = useState<Record<number, Feedback>>({})
  const [notice, setNotice] = useState<string | null>(null)
  const [checked, setChecked] = useState<Set<number>>(new Set())
  const [bulkBusy, setBulkBusy] = useState(false)
  const cardRefs = useRef<Record<number, HTMLElement | null>>({})
  const textRefs = useRef<Record<number, HTMLTextAreaElement | null>>({})

  const load = useCallback(async () => {
    try {
      const q = `ai/monitor?status=pending&sort=oldest&page_size=50${entityQuery ? `&${entityQuery}` : ''}`
      const d = await aiApi<DraftList>(q)
      setDrafts(d.drafts)
      setFetchedAt(Date.now())
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [entityQuery])

  useEffect(() => {
    void load()
    let t: number | undefined
    const unsub = subscribeChatStream(() => {
      window.clearTimeout(t)
      t = window.setTimeout(() => void load(), 400)
    })
    const poll = window.setInterval(() => void load(), 30000)
    return () => {
      unsub()
      window.clearTimeout(t)
      window.clearInterval(poll)
    }
  }, [load])

  const list = useMemo(() => drafts ?? [], [drafts])
  useEffect(() => {
    if (selected > list.length - 1) setSelected(Math.max(0, list.length - 1))
  }, [list.length, selected])

  const textFor = (d: Draft) => edits[d.id] ?? d.content
  const isEdited = (d: Draft) => textFor(d).trim() !== d.content.trim()

  const settle = (d: Draft, r: DecisionResult) => {
    const sentish = r.status === 'sent' || r.status === 'scheduled'
    if (sentish || r.status === 'rejected') {
      setDrafts((cur) => (cur ? cur.filter((x) => x.id !== d.id) : cur))
      setNotice(
        r.status === 'rejected'
          ? `Rejected the reply to ${displayName(d)}.`
          : r.status === 'scheduled'
            ? `Approved the reply to ${displayName(d)} — ${r.detail.charAt(0).toLowerCase()}${r.detail.slice(1)}`
            : `Sent to ${displayName(d)}.`,
      )
      setFeedback((f) => {
        const n = { ...f }
        delete n[d.id]
        return n
      })
      setChecked((c) => {
        const n = new Set(c)
        n.delete(d.id)
        return n
      })
      return
    }
    if (r.status === 'blocked') {
      // the draft is now "not sent" — drop it from the queue but say why
      setDrafts((cur) => (cur ? cur.filter((x) => x.id !== d.id) : cur))
      setNotice(`Not sent to ${displayName(d)}: ${r.detail}`)
      return
    }
    const superseded = /teammate replied/i.test(r.detail)
    setFeedback((f) => ({ ...f, [d.id]: { ok: false, text: r.detail, force: superseded } }))
  }

  const approve = async (d: Draft, force = false) => {
    if (!canDecide || busy) return
    setBusy(d.id)
    setNotice(null)
    try {
      const r = isEdited(d) ? await decide.send(d.id, textFor(d).trim(), force) : await decide.approve(d.id, force)
      settle(d, r)
    } catch (e) {
      const r = decisionFromError(e)
      if (r) settle(d, r)
      else setFeedback((f) => ({ ...f, [d.id]: { ok: false, text: e instanceof Error ? e.message : String(e) } }))
    } finally {
      setBusy(null)
    }
  }

  const reject = async (d: Draft) => {
    if (!canDecide || busy) return
    setBusy(d.id)
    setNotice(null)
    try {
      settle(d, await decide.reject(d.id, reason.trim()))
      setRejecting(null)
      setReason('')
    } catch (e) {
      const r = decisionFromError(e)
      if (r) settle(d, r)
      else setFeedback((f) => ({ ...f, [d.id]: { ok: false, text: e instanceof Error ? e.message : String(e) } }))
    } finally {
      setBusy(null)
    }
  }

  const bulk = async (action: 'approve' | 'reject') => {
    if (!checked.size || bulkBusy) return
    setBulkBusy(true)
    setNotice(null)
    try {
      const r = await aiApi<{ results: { id: number; ok: boolean; status: string; detail: string }[] }>(
        'ai/monitor/bulk',
        { method: 'POST', body: JSON.stringify({ ids: Array.from(checked), action }) },
      )
      const ok = r.results.filter((x) => x.ok).length
      const failed = r.results.filter((x) => !x.ok)
      setNotice(
        `${action === 'approve' ? 'Approved' : 'Rejected'} ${ok} of ${r.results.length}.` +
          (failed.length ? ` ${failed.length} need a look: ${failed[0].detail}` : ''),
      )
      setChecked(new Set())
      await load()
    } catch (e) {
      setNotice(e instanceof Error ? e.message : String(e))
    } finally {
      setBulkBusy(false)
    }
  }

  // ---- keyboard: J/K move, A approve, E edit, R reject ----------------------
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.metaKey || e.ctrlKey || e.altKey) return
      if (isTyping(e.target)) {
        if (e.key === 'Escape') (e.target as HTMLElement).blur()
        return
      }
      if (!list.length) return
      const key = e.key.toLowerCase()
      const d = list[Math.min(selected, list.length - 1)]
      if (key === 'j' || key === 'k') {
        e.preventDefault()
        const next = key === 'j' ? Math.min(list.length - 1, selected + 1) : Math.max(0, selected - 1)
        setSelected(next)
        cardRefs.current[list[next].id]?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
        cardRefs.current[list[next].id]?.focus({ preventScroll: true })
      } else if (key === 'a' && canDecide) {
        e.preventDefault()
        void approve(d)
      } else if (key === 'e' && canDecide) {
        e.preventDefault()
        textRefs.current[d.id]?.focus()
      } else if (key === 'r' && canDecide) {
        e.preventDefault()
        setRejecting(d.id)
        setReason('')
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // approve is recreated each render; the latest closure is what we want
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [list, selected, canDecide, edits, busy])

  if (error && drafts === null)
    return (
      <Card className="p-5 text-[13px] text-text-muted">
        Couldn’t load the review queue: <span className="text-[color:var(--red-ink)]">{error}</span>
      </Card>
    )
  if (drafts === null)
    return (
      <div className="flex items-center gap-2 p-6 text-[13px] text-text-muted">
        <Loader2 size={15} className="animate-spin" aria-hidden /> Loading…
      </div>
    )

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <p className="text-[12.5px] text-text-muted">
          {list.length === 0
            ? 'Nothing is waiting.'
            : `${list.length} repl${list.length === 1 ? 'y' : 'ies'} waiting, oldest first.`}
        </p>
        <span className="hidden items-center gap-1.5 text-[11.5px] text-text-faint md:inline-flex">
          <Keyboard size={13} aria-hidden />
          <Kbd>J</Kbd>/<Kbd>K</Kbd> move · <Kbd>A</Kbd> approve ·{' '}
          <Kbd>E</Kbd> edit · <Kbd>R</Kbd> reject
        </span>
        {canDecide && list.length > 1 && (
          <span className="ml-auto flex flex-wrap items-center gap-2">
            <label className="flex cursor-pointer items-center gap-1.5 text-[12px] text-text-mid">
              <input
                type="checkbox"
                className="accent-[color:var(--accent)]"
                checked={checked.size === list.length}
                onChange={(e) => setChecked(e.target.checked ? new Set(list.map((d) => d.id)) : new Set())}
              />
              Select all
            </label>
            {checked.size > 0 && (
              <>
                <Button size="sm" variant="secondary" disabled={bulkBusy} onClick={() => void bulk('approve')}>
                  <Check size={13} aria-hidden /> Approve {checked.size}
                </Button>
                <Button size="sm" variant="danger" disabled={bulkBusy} onClick={() => void bulk('reject')}>
                  <X size={13} aria-hidden /> Reject {checked.size}
                </Button>
              </>
            )}
          </span>
        )}
      </div>

      {notice && (
        <div role="status" className="rounded-lg border border-line bg-well px-3 py-2 text-[12.5px] text-text-high">
          {notice}
        </div>
      )}

      {list.length === 0 && (
        <Card className="flex flex-col items-start gap-2 px-5 py-8">
          <Inbox size={22} className="text-text-faint" aria-hidden />
          <h3 className="display text-[19px] text-text-heading">All caught up</h3>
          <p className="max-w-[520px] text-[13px] leading-relaxed text-text-muted">
            Replies the AI wants a person to check appear here before they reach the customer.
            {modeLabel ? ` Current rule: ${modeLabel}.` : ''}
          </p>
          {onOpenSettings && (
            <Button size="sm" variant="secondary" onClick={onOpenSettings}>
              Change what gets reviewed
            </Button>
          )}
        </Card>
      )}

      {list.map((d, i) => {
        const fb = feedback[d.id]
        const active = i === selected
        const edited = isEdited(d)
        const answering = new Set((d.context.answering ?? []).map((a) => a.id))
        return (
          <article
            key={d.id}
            ref={(el) => {
              cardRefs.current[d.id] = el
            }}
            tabIndex={-1}
            onFocus={() => setSelected(i)}
            onMouseDown={() => setSelected(i)}
            aria-label={`AI reply to ${displayName(d)}`}
            className={`rounded-xl border bg-card p-4 outline-none transition-colors sm:p-5 ${
              active ? 'border-text-muted' : 'border-line'
            }`}
          >
            <DraftHeader
              d={d}
              right={
                <>
                  <Countdown secondsLeft={d.seconds_left} fetchedAt={fetchedAt} />
                  {canDecide && list.length > 1 && (
                    <input
                      type="checkbox"
                      aria-label={`Select the reply to ${displayName(d)}`}
                      className="accent-[color:var(--accent)]"
                      checked={checked.has(d.id)}
                      onChange={(e) =>
                        setChecked((c) => {
                          const n = new Set(c)
                          if (e.target.checked) n.add(d.id)
                          else n.delete(d.id)
                          return n
                        })
                      }
                    />
                  )}
                </>
              }
            />

            <div className="mt-2">
              <HoldReasons reasons={d.reasons} />
            </div>

            <div className="mt-3 grid gap-4 md:grid-cols-2">
              <section aria-label="Conversation" className="min-w-0">
                <div className="eyebrow mb-1.5">Conversation</div>
                <div className="max-h-[260px] overflow-y-auto rounded-lg bg-well p-2.5">
                  <ConversationSnippet messages={d.recent ?? []} highlight={answering} />
                </div>
              </section>
              <section aria-label="Proposed reply" className="min-w-0">
                <div className="eyebrow mb-1.5 flex items-center gap-2">
                  AI proposes
                  {edited && <span className="normal-case tracking-normal text-[color:var(--amber-ink)]">· edited</span>}
                </div>
                <textarea
                  ref={(el) => {
                    textRefs.current[d.id] = el
                  }}
                  value={textFor(d)}
                  readOnly={!canDecide}
                  onChange={(e) => setEdits((cur) => ({ ...cur, [d.id]: e.target.value }))}
                  rows={Math.min(10, Math.max(3, Math.ceil(textFor(d).length / 52) + textFor(d).split('\n').length - 1))}
                  className={inputCls}
                  aria-label="Proposed reply (editable)"
                />
                {d.bookings && d.bookings.length > 0 && (
                  <div className="mt-2">
                    <DraftBookings d={d} />
                  </div>
                )}
                <div className="mt-2">
                  <ContextChips d={d} />
                </div>
              </section>
            </div>

            {d.superseded && !fb && (
              <p className="mt-3 text-[12.5px] text-[color:var(--amber-ink)]">
                A teammate already replied in this chat after the AI wrote this.
              </p>
            )}
            {fb && (
              <div role="alert" className="mt-3 flex flex-wrap items-center gap-2 text-[12.5px] text-[color:var(--red-ink)]">
                {fb.text}
                {fb.force && (
                  <Button size="sm" variant="secondary" onClick={() => void approve(d, true)}>
                    Send anyway
                  </Button>
                )}
              </div>
            )}

            {rejecting === d.id ? (
              <div className="mt-3 rounded-lg border border-line bg-well p-3">
                <div className="mb-2 text-[12.5px] font-semibold text-text-high">Why reject it? (optional)</div>
                <div className="mb-2 flex flex-wrap gap-1.5">
                  {QUICK_REASONS.map((q) => (
                    <button
                      key={q}
                      type="button"
                      onClick={() => setReason(q)}
                      className={`rounded-full border px-2.5 py-1 text-[12px] transition-colors ${
                        reason === q ? 'border-text-primary bg-fill-hover text-text-heading' : 'border-line text-text-mid hover:bg-fill'
                      }`}
                    >
                      {q}
                    </button>
                  ))}
                </div>
                <input
                  autoFocus
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') void reject(d)
                    if (e.key === 'Escape') setRejecting(null)
                  }}
                  placeholder="Your own words — it helps tune the AI"
                  className={inputCls}
                  aria-label="Reject reason"
                />
                <div className="mt-2 flex flex-wrap gap-2">
                  <Button size="sm" variant="danger" disabled={busy === d.id} onClick={() => void reject(d)}>
                    Reject
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => setRejecting(null)}>
                    Cancel
                  </Button>
                </div>
              </div>
            ) : (
              <div className="mt-4 flex flex-wrap items-center gap-2">
                {canDecide && (
                  <>
                    <Button size="sm" variant="primary" disabled={busy === d.id} onClick={() => void approve(d)}>
                      {busy === d.id ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Send size={13} aria-hidden />}
                      {edited ? 'Send edited reply' : 'Approve & send'}
                      <Kbd inverse className="hidden md:inline">A</Kbd>
                    </Button>
                    <Button size="sm" variant="secondary" onClick={() => textRefs.current[d.id]?.focus()}>
                      <Pencil size={13} aria-hidden /> Edit <Kbd className="hidden md:inline">E</Kbd>
                    </Button>
                    {edited && (
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() =>
                          setEdits((cur) => {
                            const n = { ...cur }
                            delete n[d.id]
                            return n
                          })
                        }
                      >
                        Undo edit
                      </Button>
                    )}
                    <Button
                      size="sm"
                      variant="danger"
                      onClick={() => {
                        setRejecting(d.id)
                        setReason('')
                      }}
                    >
                      <X size={13} aria-hidden /> Reject <Kbd className="hidden md:inline">R</Kbd>
                    </Button>
                  </>
                )}
                <Link
                  href={chatHref(d)}
                  className="ml-auto inline-flex items-center gap-1 text-[12.5px] font-semibold text-text-mid underline-offset-2 hover:text-text-high hover:underline"
                >
                  Open chat <ExternalLink size={12} aria-hidden />
                </Link>
              </div>
            )}
          </article>
        )
      })}
    </div>
  )
}
