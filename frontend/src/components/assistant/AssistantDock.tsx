'use client'

import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { usePathname, useRouter } from 'next/navigation'
import {
  ArrowLeft,
  ArrowUp,
  ArrowUpRight,
  Check,
  ChevronDown,
  Clock,
  History,
  Loader2,
  MessageSquarePlus,
  Sparkles,
  X,
} from 'lucide-react'
import { getAuth, hasCap } from '@/lib/auth'
import { dropForeignAssistantCache, readAssistantCurrent, writeAssistantCurrent } from '@/lib/assistantCache'
import { useEntity } from '@/lib/EntityContext'
import { BranchMark } from '@/components/shell/Logo'
import { AssistantHistory } from './AssistantHistory'
import {
  AssistantError,
  assistantApi,
  toChatMessages,
  type ActionState,
  type ChatMsg,
  type NavChip,
  type PendingAction,
} from './assistantApi'

/**
 * "Ask ChatBranch" — the operator assistant, docked bottom-right on every
 * signed-in page. It answers how-to questions, looks things up and prepares
 * changes AS the signed-in user (POST /api/assistant/chat). Anything that
 * changes data comes back as a confirm card and only runs when the person
 * presses Confirm (POST /api/assistant/confirm) — the model proposes, the
 * person decides.
 *
 * Chats are stored server-side, per user (/api/assistant/conversations): the
 * History view lists them and resumes one. The browser keeps only the open
 * chat's id and the unsent draft, keyed by username (lib/assistantCache), and
 * everything in memory is dropped the moment the signed-in user changes — a
 * different login in the same tab never sees the previous one's chat.
 */

const uid = () => Math.random().toString(36).slice(2, 10)

function suggestions(): string[] {
  const out: string[] = []
  if (hasCap('chats.view')) out.push('How many chats are waiting for a reply?')
  if (hasCap('contacts.view')) out.push('Find contact…')
  if (hasCap('activity.view')) out.push('Show recent activity')
  out.push('How do I connect WhatsApp?')
  if (hasCap('analytics.view')) out.push('Go to analytics')
  return out.slice(0, 5)
}

const SETTLED: ActionState[] = ['done', 'failed', 'cancelled', 'expired']

export function AssistantDock() {
  const pathname = usePathname() || '/'
  const router = useRouter()
  const { activeEntity } = useEntity()

  const [mounted, setMounted] = useState(false)
  const [username, setUsername] = useState<string | null>(null)
  /** Whose chat the in-memory state below is. Until it equals `username` the
   *  dock renders nothing and writes no cache (a user switch is mid-reset). */
  const [owner, setOwner] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [view, setView] = useState<'chat' | 'history'>('chat')
  const [conversationId, setConversationId] = useState<number | null>(null)
  const [title, setTitle] = useState('')
  const [messages, setMessages] = useState<ChatMsg[]>([])
  const [mode, setMode] = useState('')
  const [draft, setDraft] = useState('')
  const [sending, setSending] = useState(false)
  const [loadingChat, setLoadingChat] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hints, setHints] = useState<string[]>([])

  const inputRef = useRef<HTMLTextAreaElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  /** Bumped whenever the shown chat (or the user) changes — late answers for an
   *  older one are dropped instead of landing in the wrong chat. */
  const generation = useRef(0)
  /** The chat id whose turns are on screen (null = nothing loaded yet). */
  const loadedId = useRef<number | null>(null)

  // Who is signed in — re-read on navigation, on the shell's auth-change event
  // and when another tab signs in or out (storage event on the session key).
  useEffect(() => {
    const read = () => setUsername(getAuth()?.username ?? null)
    read()
    setMounted(true)
    const onStorage = (e: StorageEvent) => {
      if (e.key === null || e.key === 'chatbranch-auth') read()
    }
    window.addEventListener('auth-change', read)
    window.addEventListener('storage', onStorage)
    return () => {
      window.removeEventListener('auth-change', read)
      window.removeEventListener('storage', onStorage)
    }
  }, [])
  useEffect(() => setUsername(getAuth()?.username ?? null), [pathname])

  // A different (or no) user: nothing of the previous one may stay on screen.
  // Then restore THIS user's open chat id + draft (their own cache only).
  useEffect(() => {
    if (!mounted) return
    generation.current += 1
    loadedId.current = null
    setMessages([])
    setConversationId(null)
    setTitle('')
    setMode('')
    setView('chat')
    setError(null)
    setSending(false)
    setLoadingChat(false)
    setDraft('')
    setOwner(username)
    if (!username) {
      setOpen(false)
      return
    }
    dropForeignAssistantCache(username)
    const cached = readAssistantCurrent(username)
    setConversationId(cached.conversationId)
    setDraft(cached.draft)
    setHints(suggestions())
  }, [username, mounted])

  // Keep the per-user cache in step (open chat + unsent draft) — only once the
  // state is this user's, never the previous one's values under the new key.
  useEffect(() => {
    if (!username || owner !== username) return
    writeAssistantCurrent(username, { conversationId, draft })
  }, [username, owner, conversationId, draft])

  const openConversation = useCallback(async (id: number) => {
    const gen = ++generation.current
    setView('chat')
    setError(null)
    setLoadingChat(true)
    setMessages([])
    setConversationId(id)
    loadedId.current = id
    try {
      const c = await assistantApi.get(id)
      if (gen !== generation.current) return
      const { messages: ms, mode: m } = toChatMessages(c)
      setMessages(ms)
      setMode(m)
      setTitle(c.title)
    } catch (e) {
      if (gen !== generation.current) return
      if (e instanceof AssistantError && e.status === 404) {
        // deleted elsewhere (another tab, Clear all) — start fresh
        loadedId.current = null
        setConversationId(null)
        setTitle('')
      } else {
        setError(`Couldn’t open that chat — ${e instanceof Error ? e.message : String(e)}`)
      }
    } finally {
      if (gen === generation.current) setLoadingChat(false)
    }
  }, [])

  // The open chat's turns come from the server the first time the dock opens.
  useEffect(() => {
    if (!open || !username || owner !== username || conversationId == null) return
    if (loadedId.current === conversationId) return
    void openConversation(conversationId)
  }, [open, username, owner, conversationId, openConversation])

  /** Start over with an empty chat; `stayInHistory` when the open one was just deleted from the list. */
  const newChat = useCallback((stayInHistory = false) => {
    generation.current += 1
    loadedId.current = null
    setConversationId(null)
    setTitle('')
    setMessages([])
    setMode('')
    setError(null)
    setLoadingChat(false)
    setSending(false)
    if (stayInHistory) return
    setView('chat')
    window.setTimeout(() => inputRef.current?.focus(), 30)
  }, [])

  // focus the composer on open; Esc closes (the clear-all confirm handles its own Esc)
  useEffect(() => {
    if (!open) return
    const t = window.setTimeout(() => inputRef.current?.focus(), 30)
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented && !document.querySelector('[aria-modal="true"]')) setOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.clearTimeout(t)
      window.removeEventListener('keydown', onKey)
    }
  }, [open])

  useLayoutEffect(() => {
    const el = listRef.current
    if (el && view === 'chat') el.scrollTop = el.scrollHeight
  }, [messages, sending, open, view])

  // grow the composer with its content (up to ~5 lines)
  useLayoutEffect(() => {
    const el = inputRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 132)}px`
  }, [draft, open, view])

  const send = useCallback(
    async (text: string) => {
      const content = text.trim()
      if (!content || sending || !username || owner !== username) return
      const gen = generation.current
      const asked = conversationId
      setMessages((ms) => [...ms, { id: uid(), role: 'user', content }])
      setDraft('')
      setError(null)
      setSending(true)
      try {
        const d = await assistantApi.chat({
          message: content,
          conversation_id: asked,
          context: { path: pathname, entity: activeEntity?.slug ?? null },
        })
        if (gen !== generation.current) return
        setMode(d.mode)
        const reply: ChatMsg = {
          id: d.message_id != null ? `m${d.message_id}` : uid(),
          role: 'assistant',
          content: d.reply,
          steps: d.steps,
          pending: d.pending_actions.map((p) => ({ ...p, state: 'pending' })),
          navigation: d.navigation,
        }
        if (d.conversation_id != null && asked != null && d.conversation_id !== asked) {
          // the chat we were in is gone (deleted elsewhere): this turn opened a new one
          setMessages([{ id: uid(), role: 'user', content }, reply])
        } else {
          setMessages((ms) => [...ms, reply])
        }
        if (d.conversation_id != null) {
          loadedId.current = d.conversation_id
          setConversationId(d.conversation_id)
          setTitle(d.title ?? '')
        }
      } catch (e) {
        if (gen !== generation.current) return
        setMessages((ms) => [
          ...ms,
          {
            id: uid(),
            role: 'assistant',
            content: `I couldn’t reach the assistant — ${e instanceof Error ? e.message : String(e)}`,
            error: true,
          },
        ])
      } finally {
        if (gen === generation.current) setSending(false)
      }
    },
    [sending, username, owner, conversationId, pathname, activeEntity],
  )

  const patchAction = (msgId: string, actionId: string, patch: Partial<PendingAction>, nav?: NavChip[]) =>
    setMessages((ms) =>
      ms.map((m) =>
        m.id !== msgId
          ? m
          : {
              ...m,
              pending: m.pending?.map((p) => (p.id === actionId ? { ...p, ...patch } : p)),
              navigation: nav?.length
                ? [...(m.navigation ?? []), ...nav.filter((n) => !m.navigation?.some((x) => x.href === n.href))]
                : m.navigation,
            },
      ),
    )

  const decide = async (msgId: string, action: PendingAction, confirm: boolean) => {
    if (new Date(action.expires_at).getTime() < Date.now()) {
      patchAction(msgId, action.id, { state: 'expired', message: 'This expired — ask again.' })
      return
    }
    patchAction(msgId, action.id, { state: 'running' })
    try {
      if (confirm) {
        const d = await assistantApi.confirm(action.id)
        patchAction(msgId, action.id, { state: d.ok ? 'done' : 'failed', message: d.message }, d.navigation)
      } else {
        await assistantApi.cancel(action.id)
        patchAction(msgId, action.id, { state: 'cancelled' })
      }
    } catch (e) {
      const text = e instanceof Error ? e.message : String(e)
      // 409/410 say what really happened to it (already done, cancelled, expired)
      const known = e instanceof AssistantError ? (e.body.status as ActionState | undefined) : undefined
      patchAction(msgId, action.id, {
        state: known && SETTLED.includes(known) ? known : /expired/i.test(text) ? 'expired' : 'failed',
        message: text,
      })
    }
  }

  const go = (href: string) => {
    router.push(href)
    if (window.matchMedia('(max-width: 1023px)').matches) setOpen(false)
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void send(draft)
    }
  }

  if (!mounted || !username || owner !== username) return null

  // The inbox composer and the flow canvas own the bottom-right corner, so the
  // launcher shrinks to a round button and sits higher on those routes.
  const crowded = pathname.startsWith('/chats') || pathname.startsWith('/flow-builder')
  const headerBtn =
    'grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high disabled:opacity-40'
  const inChat = messages.length > 0 || conversationId != null

  return (
    <>
      {!open && (
        <button
          type="button"
          onClick={() => setOpen(true)}
          aria-label="Ask ChatBranch"
          title="Ask ChatBranch"
          className={`fixed right-4 z-50 inline-flex items-center gap-2 rounded-full bg-primary font-semibold text-on-primary shadow-pop transition-transform hover:-translate-y-0.5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--focus)] lg:right-6 ${
            crowded
              ? 'bottom-[calc(140px+env(safe-area-inset-bottom))] h-11 w-11 justify-center lg:bottom-[104px]'
              : 'bottom-[calc(76px+env(safe-area-inset-bottom))] h-11 px-4 text-[13px] lg:bottom-6'
          }`}
        >
          <Sparkles size={16} strokeWidth={2} aria-hidden />
          {!crowded && <span>Ask ChatBranch</span>}
        </button>
      )}

      {open && (
        <>
          <div
            className="fixed inset-0 z-[54] animate-fadeIn lg:hidden"
            style={{ background: 'var(--scrim)' }}
            onClick={() => setOpen(false)}
            aria-hidden
          />
          <aside
            role="dialog"
            aria-label="Ask ChatBranch"
            className="fixed inset-y-0 right-0 z-[55] flex w-full animate-fadeIn flex-col border-l border-line-strong bg-card shadow-pop sm:w-[420px]"
            style={{ paddingBottom: 'env(safe-area-inset-bottom)' }}
          >
            <header className="flex items-center gap-2 border-b border-line px-3 py-3 sm:px-4">
              {view === 'history' ? (
                <>
                  <button
                    type="button"
                    onClick={() => setView('chat')}
                    aria-label="Back to the chat"
                    title="Back to the chat"
                    className={headerBtn}
                  >
                    <ArrowLeft size={15} aria-hidden />
                  </button>
                  <div className="min-w-0 flex-1">
                    <div className="display text-[18px] leading-tight text-text-heading">History</div>
                    <div className="text-[11.5px] text-text-muted">Your chats — only you can see them</div>
                  </div>
                </>
              ) : (
                <>
                  <span className="shrink-0 text-text-heading">
                    <BranchMark size={22} />
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="display text-[18px] leading-tight text-text-heading">Ask ChatBranch</div>
                    <div className="flex min-w-0 items-center text-[11.5px] text-text-muted">
                      <span className="truncate" title={title || undefined}>
                        {title || 'Acts as you · changes wait for your OK'}
                      </span>
                      {mode === 'sandbox' && (
                        <span
                          className="ml-1.5 shrink-0 rounded-full bg-fill-hover px-1.5 py-px text-[10px] font-semibold text-text-mid"
                          title="No AI model is connected, so I understand simple requests and the product guide."
                        >
                          Sandbox
                        </span>
                      )}
                    </div>
                  </div>
                </>
              )}
              <button
                type="button"
                onClick={() => setView((v) => (v === 'history' ? 'chat' : 'history'))}
                disabled={sending}
                aria-pressed={view === 'history'}
                aria-label="History"
                title="History"
                className={`${headerBtn} ${view === 'history' ? 'bg-fill-hover text-text-high' : ''}`}
              >
                <History size={15} aria-hidden />
              </button>
              {(inChat || view === 'history') && (
                <button
                  type="button"
                  onClick={() => newChat()}
                  disabled={sending}
                  aria-label="New chat"
                  title="New chat"
                  className={headerBtn}
                >
                  <MessageSquarePlus size={15} aria-hidden />
                </button>
              )}
              <button type="button" onClick={() => setOpen(false)} aria-label="Close assistant" className={headerBtn}>
                <X size={15} aria-hidden />
              </button>
            </header>

            <div ref={listRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-4" aria-live="polite">
              {view === 'history' ? (
                <AssistantHistory
                  currentId={conversationId}
                  onOpen={(id) => void openConversation(id)}
                  onDeleted={(id) => {
                    if (id === conversationId) newChat(true)
                  }}
                  onCleared={() => newChat()}
                  onRenamed={(id, t) => {
                    if (id === conversationId) setTitle(t)
                  }}
                />
              ) : (
                <>
                  {error && (
                    <div
                      role="alert"
                      className="mb-3 rounded-lg border border-red/40 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]"
                    >
                      {error}
                    </div>
                  )}
                  {loadingChat ? (
                    <div className="flex items-center gap-2 pt-6 text-[12.5px] text-text-muted">
                      <Loader2 size={14} className="animate-spin" aria-hidden /> Opening your chat…
                    </div>
                  ) : messages.length === 0 ? (
                    <div className="flex flex-col gap-4 pt-6">
                      <div>
                        <div className="display text-[24px] leading-tight text-text-heading">How can I help?</div>
                        <p className="mt-1.5 text-[13px] leading-relaxed text-text-muted">
                          Ask how to do something, look up a contact or a conversation, or have me prepare a change —
                          I’ll show it to you before anything happens.
                        </p>
                      </div>
                      <div className="flex flex-col items-start gap-1.5">
                        {hints.map((h) => (
                          <button
                            key={h}
                            type="button"
                            onClick={() =>
                              h.endsWith('…') ? (setDraft(h.replace('…', ' ')), inputRef.current?.focus()) : send(h)
                            }
                            className="rounded-full border border-line-strong px-3 py-1.5 text-left text-[12.5px] text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
                          >
                            {h}
                          </button>
                        ))}
                      </div>
                      <button
                        type="button"
                        onClick={() => setView('history')}
                        className="inline-flex items-center gap-1.5 self-start text-[12px] font-semibold text-text-muted hover:text-text-high"
                      >
                        <History size={13} aria-hidden /> Your past chats
                      </button>
                    </div>
                  ) : (
                    <ol className="flex flex-col gap-4">
                      {messages.map((m) => (
                        <li key={m.id}>
                          {m.role === 'user' ? (
                            <div className="ml-10 flex justify-end">
                              <div className="whitespace-pre-wrap break-words rounded-xl rounded-br-sm border border-line bg-well px-3 py-2 text-[13px] text-text-primary">
                                {m.content}
                              </div>
                            </div>
                          ) : (
                            <AssistantMessage msg={m} onGo={go} onDecide={(a, ok) => decide(m.id, a, ok)} />
                          )}
                        </li>
                      ))}
                      {sending && (
                        <li className="flex items-center gap-1.5 text-text-muted" aria-label="Thinking">
                          {[0, 1, 2].map((i) => (
                            <span
                              key={i}
                              className="h-1.5 w-1.5 animate-[blink_1.2s_infinite] rounded-full bg-text-muted"
                              style={{ animationDelay: `${i * 0.2}s` }}
                            />
                          ))}
                        </li>
                      )}
                    </ol>
                  )}
                </>
              )}
            </div>

            {view === 'chat' && (
              <form
                className="border-t border-line px-3 pb-3 pt-2.5"
                onSubmit={(e) => {
                  e.preventDefault()
                  void send(draft)
                }}
              >
                <div className="flex items-end gap-2 rounded-xl border border-line-strong bg-card px-3 py-2 focus-within:border-text-muted">
                  <textarea
                    ref={inputRef}
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={onKeyDown}
                    rows={1}
                    placeholder="Ask anything about ChatBranch…"
                    aria-label="Message the assistant"
                    className="max-h-[132px] min-h-[22px] flex-1 resize-none bg-transparent text-[13.5px] leading-[1.45] text-text-primary outline-none placeholder:text-text-faint"
                  />
                  <button
                    type="submit"
                    disabled={!draft.trim() || sending || loadingChat}
                    aria-label="Send"
                    className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-primary text-on-primary transition-opacity disabled:opacity-35"
                  >
                    {sending ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <ArrowUp size={15} aria-hidden />}
                  </button>
                </div>
                <div className="mt-1.5 hidden px-1 text-[11px] text-text-faint sm:block">
                  Enter to send · Shift+Enter for a new line · Esc to close
                </div>
              </form>
            )}
          </aside>
        </>
      )}
    </>
  )
}

function AssistantMessage({
  msg,
  onGo,
  onDecide,
}: {
  msg: ChatMsg
  onGo: (href: string) => void
  onDecide: (a: PendingAction, confirm: boolean) => void
}) {
  return (
    <div className="mr-4 flex flex-col gap-2">
      {msg.steps && msg.steps.length > 0 && (
        <ul className="flex flex-col gap-1 border-l-2 border-line pl-2.5">
          {msg.steps.map((s, i) => (
            <li key={i} className="flex items-start gap-1.5 text-[11.5px] leading-snug text-text-muted">
              {s.summary === 'Waiting for your confirmation' ? (
                <Clock size={12} className="mt-px shrink-0 text-amber-300" aria-hidden />
              ) : s.ok ? (
                <Check size={12} className="mt-px shrink-0 text-green-300" aria-hidden />
              ) : (
                <X size={12} className="mt-px shrink-0 text-red-300" aria-hidden />
              )}
              <span className="min-w-0">
                <span className="font-semibold text-text-mid">{s.title}</span>
                {s.summary && s.summary !== s.title && <> — {s.summary}</>}
              </span>
            </li>
          ))}
        </ul>
      )}
      <div
        className={`whitespace-pre-wrap break-words text-[13.5px] leading-relaxed ${
          msg.error ? 'text-red-300' : 'text-text-primary'
        }`}
      >
        {msg.content}
      </div>
      {msg.pending?.map((a) => <ActionCard key={a.id} action={a} onDecide={onDecide} />)}
      {msg.navigation && msg.navigation.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {msg.navigation.map((n) => (
            <button
              key={n.href + n.label}
              type="button"
              onClick={() => onGo(n.href)}
              className="inline-flex items-center gap-1 rounded-full border border-line-strong px-2.5 py-1 text-[12px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
            >
              {n.label}
              <ArrowUpRight size={12} aria-hidden />
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

const STATE_LABEL: Record<Exclude<ActionState, 'pending' | 'running'>, { label: string; cls: string }> = {
  done: { label: 'Done', cls: 'bg-green/10 text-green-300' },
  failed: { label: 'Didn’t happen', cls: 'bg-red/10 text-red-300' },
  cancelled: { label: 'Cancelled', cls: 'bg-fill-hover text-text-muted' },
  expired: { label: 'Expired', cls: 'bg-amber/10 text-amber-300' },
}

function ActionCard({ action, onDecide }: { action: PendingAction; onDecide: (a: PendingAction, ok: boolean) => void }) {
  const [details, setDetails] = useState(false)
  const state = action.state ?? 'pending'
  const settled = state !== 'pending' && state !== 'running'
  const entries = Object.entries(action.args ?? {})
  return (
    <div className="rounded-lg border border-line-strong bg-well p-3">
      <div className="flex items-center gap-2">
        <span className="eyebrow">{settled ? action.title : 'Needs your confirmation'}</span>
        {settled && (
          <span className={`ml-auto rounded-full px-2 py-px text-[10.5px] font-semibold ${STATE_LABEL[state].cls}`}>
            {STATE_LABEL[state].label}
          </span>
        )}
      </div>
      <div className="mt-1 break-words text-[13px] font-semibold text-text-high">{action.summary}</div>
      {entries.length > 0 && (
        <button
          type="button"
          onClick={() => setDetails((v) => !v)}
          className="mt-1 inline-flex items-center gap-1 text-[11.5px] font-semibold text-text-muted hover:text-text-high"
          aria-expanded={details}
        >
          <ChevronDown size={12} className={`transition-transform ${details ? 'rotate-180' : ''}`} aria-hidden /> Details
        </button>
      )}
      {details && (
        <dl className="mt-1.5 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[11.5px]">
          {entries.map(([k, v]) => (
            <div key={k} className="contents">
              <dt className="font-mono text-text-muted">{k}</dt>
              <dd className="break-words text-text-primary">{typeof v === 'string' ? v : JSON.stringify(v)}</dd>
            </div>
          ))}
        </dl>
      )}
      {action.message && settled && <div className="mt-1.5 text-[12px] text-text-muted">{action.message}</div>}
      {!settled && (
        <div className="mt-2.5 flex gap-2">
          <button
            type="button"
            disabled={state === 'running'}
            onClick={() => onDecide(action, true)}
            className="inline-flex h-8 items-center gap-1.5 rounded-lg bg-primary px-3 text-[12.5px] font-semibold text-on-primary hover:opacity-90 disabled:opacity-50"
          >
            {state === 'running' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <Check size={13} aria-hidden />}
            Confirm
          </button>
          <button
            type="button"
            disabled={state === 'running'}
            onClick={() => onDecide(action, false)}
            className="inline-flex h-8 items-center rounded-lg px-3 text-[12.5px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high disabled:opacity-50"
          >
            Cancel
          </button>
        </div>
      )}
    </div>
  )
}
