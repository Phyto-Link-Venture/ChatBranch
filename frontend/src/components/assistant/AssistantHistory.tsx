'use client'

import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { Check, Loader2, MessageSquare, Pencil, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { relTime } from '@/lib/channelHealth'
import { assistantApi, type ConversationSummary } from './assistantApi'

/**
 * The dock's "History" view: the signed-in user's own past chats, newest first
 * (GET /api/assistant/conversations, paged by `next_before`). A row resumes the
 * chat; each can be renamed or deleted, and "Clear all" (behind a confirm)
 * removes every chat of this user. Errors render inside the dock / the modal.
 */
export function AssistantHistory({
  currentId,
  onOpen,
  onDeleted,
  onCleared,
  onRenamed,
}: {
  currentId: number | null
  onOpen: (id: number) => void
  onDeleted: (id: number) => void
  onCleared: () => void
  onRenamed: (id: number, title: string) => void
}) {
  const [items, setItems] = useState<ConversationSummary[]>([])
  const [total, setTotal] = useState(0)
  const [nextBefore, setNextBefore] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<number | null>(null)
  const [editing, setEditing] = useState<{ id: number; title: string } | null>(null)
  const [clearOpen, setClearOpen] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [clearError, setClearError] = useState<string | null>(null)
  const alive = useRef(true)

  const load = useCallback(async (before: string | null) => {
    setLoading(true)
    setError(null)
    try {
      const page = await assistantApi.list(before)
      if (!alive.current) return
      setItems((prev) => (before ? [...prev, ...page.conversations] : page.conversations))
      setTotal(page.total)
      setNextBefore(page.has_more ? page.next_before : null)
    } catch (e) {
      if (alive.current) setError(`Couldn’t load your chats — ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      if (alive.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    alive.current = true
    void load(null)
    return () => {
      alive.current = false
    }
  }, [load])

  const remove = async (id: number) => {
    setBusyId(id)
    setError(null)
    try {
      await assistantApi.remove(id)
      setItems((xs) => xs.filter((x) => x.id !== id))
      setTotal((t) => Math.max(0, t - 1))
      onDeleted(id)
    } catch (e) {
      setError(`Couldn’t delete that chat — ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusyId(null)
      setConfirmDelete(null)
    }
  }

  const saveTitle = async () => {
    if (!editing) return
    const title = editing.title.replace(/\s+/g, ' ').trim()
    const before = items.find((x) => x.id === editing.id)
    if (!title || title === before?.title) {
      setEditing(null)
      return
    }
    setBusyId(editing.id)
    setError(null)
    try {
      const saved = await assistantApi.rename(editing.id, title)
      setItems((xs) => xs.map((x) => (x.id === saved.id ? { ...x, title: saved.title } : x)))
      onRenamed(saved.id, saved.title)
      setEditing(null)
    } catch (e) {
      setError(`Couldn’t rename that chat — ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setBusyId(null)
    }
  }

  const clearAll = async () => {
    setClearing(true)
    setClearError(null)
    try {
      await assistantApi.clearAll()
      setItems([])
      setTotal(0)
      setNextBefore(null)
      setClearOpen(false)
      onCleared()
    } catch (e) {
      setClearError(`Couldn’t clear your chats — ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setClearing(false)
    }
  }

  const onTitleKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      void saveTitle()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      setEditing(null)
    }
  }

  const iconBtn =
    'grid h-7 w-7 place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high disabled:opacity-40'

  return (
    <div className="flex flex-col gap-3">
      {error && (
        <div
          role="alert"
          className="rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
        >
          {error}
        </div>
      )}

      <div className="flex items-center justify-between gap-2">
        <div className="eyebrow">Your chats{total > 0 ? ` · ${total}` : ''}</div>
        {items.length > 0 && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setClearError(null)
              setClearOpen(true)
            }}
          >
            <Trash2 size={13} aria-hidden /> Clear all
          </Button>
        )}
      </div>

      {loading && items.length === 0 ? (
        <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading your chats…
        </div>
      ) : items.length === 0 ? (
        !error && (
          <div className="flex flex-col items-start gap-1.5 rounded-lg border border-dashed border-line-strong px-4 py-6">
            <MessageSquare size={18} className="text-text-faint" aria-hidden />
            <div className="text-[13px] font-semibold text-text-high">No chats yet</div>
            <p className="text-[12.5px] leading-relaxed text-text-muted">
              Questions you ask are kept here, for you only, so you can pick a chat up again later.
            </p>
          </div>
        )
      ) : (
        <ul className="flex flex-col gap-1">
          {items.map((c) => {
            const current = c.id === currentId
            const isEditing = editing?.id === c.id
            const asking = confirmDelete === c.id
            const busy = busyId === c.id
            return (
              <li
                key={c.id}
                className={`rounded-lg border transition-colors ${
                  current ? 'border-line-strong bg-well' : 'border-transparent hover:bg-fill-hover'
                }`}
              >
                <div className="flex items-start gap-1.5 py-2 pl-3 pr-1.5">
                  {isEditing ? (
                    <input
                      autoFocus
                      value={editing.title}
                      maxLength={120}
                      onChange={(e) => setEditing({ id: c.id, title: e.target.value })}
                      onKeyDown={onTitleKey}
                      aria-label="Chat title"
                      className="h-8 min-w-0 flex-1 rounded-md border border-line-strong bg-card px-2 text-[13px] text-text-primary outline-none focus:border-text-muted"
                    />
                  ) : (
                    <button
                      type="button"
                      onClick={() => onOpen(c.id)}
                      aria-current={current ? 'true' : undefined}
                      className="min-w-0 flex-1 text-left"
                    >
                      <span className="flex items-baseline gap-2">
                        <span className="truncate text-[13px] font-semibold text-text-high">{c.title}</span>
                        <span className="ml-auto shrink-0 text-[11px] text-text-faint">{relTime(c.updated_at)}</span>
                      </span>
                      {c.snippet && (
                        <span className="mt-0.5 line-clamp-2 break-words text-[12px] leading-snug text-text-muted">
                          {c.snippet}
                        </span>
                      )}
                    </button>
                  )}

                  <div className="flex shrink-0 items-center gap-0.5">
                    {isEditing ? (
                      <>
                        <button
                          type="button"
                          onClick={() => void saveTitle()}
                          disabled={busy}
                          aria-label="Save title"
                          className={iconBtn}
                        >
                          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}
                        </button>
                        <button type="button" onClick={() => setEditing(null)} aria-label="Cancel rename" className={iconBtn}>
                          <X size={13} aria-hidden />
                        </button>
                      </>
                    ) : asking ? (
                      <>
                        <Button variant="danger" size="sm" disabled={busy} onClick={() => void remove(c.id)}>
                          {busy && <Loader2 size={12} className="animate-spin" aria-hidden />}
                          Delete
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => setConfirmDelete(null)}>
                          Keep
                        </Button>
                      </>
                    ) : (
                      <>
                        <button
                          type="button"
                          onClick={() => {
                            setConfirmDelete(null)
                            setEditing({ id: c.id, title: c.title })
                          }}
                          aria-label={`Rename “${c.title}”`}
                          title="Rename"
                          className={iconBtn}
                        >
                          <Pencil size={13} aria-hidden />
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setEditing(null)
                            setConfirmDelete(c.id)
                          }}
                          aria-label={`Delete “${c.title}”`}
                          title="Delete"
                          className={iconBtn}
                        >
                          <Trash2 size={13} aria-hidden />
                        </button>
                      </>
                    )}
                  </div>
                </div>
              </li>
            )
          })}
        </ul>
      )}

      {nextBefore && (
        <Button variant="secondary" size="sm" disabled={loading} onClick={() => void load(nextBefore)} className="self-center">
          {loading && <Loader2 size={12} className="animate-spin" aria-hidden />}
          Show older chats
        </Button>
      )}

      <Modal
        open={clearOpen}
        onClose={() => !clearing && setClearOpen(false)}
        title="Clear all chats?"
        size="sm"
        labelledById="assistant-clear-title"
        footer={
          <>
            <Button variant="ghost" onClick={() => setClearOpen(false)} disabled={clearing}>
              Cancel
            </Button>
            <Button variant="danger" onClick={() => void clearAll()} disabled={clearing}>
              {clearing && <Loader2 size={13} className="animate-spin" aria-hidden />}
              Clear all
            </Button>
          </>
        }
      >
        {clearError && (
          <div
            role="alert"
            className="mb-3 rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
          >
            {clearError}
          </div>
        )}
        <p className="text-[13px] leading-relaxed text-text-mid">
          This deletes all {total === 1 ? 'your chat' : `${total} of your chats`} with Ask ChatBranch. It can’t be
          undone, and any change still waiting for your confirmation is cancelled. Other people’s chats aren’t
          affected.
        </p>
      </Modal>
    </div>
  )
}
