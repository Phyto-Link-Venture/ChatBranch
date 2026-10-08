'use client'

import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { usePathname, useRouter } from 'next/navigation'
import {
  ArrowUp,
  ArrowUpRight,
  Check,
  ChevronDown,
  Clock,
  Loader2,
  RotateCcw,
  Sparkles,
  X,
} from 'lucide-react'
import { authHeaders, expireSessionOn401, getAuth, hasCap } from '@/lib/auth'
import { useEntity } from '@/lib/EntityContext'
import { BranchMark } from '@/components/shell/Logo'

/**
 * "Ask ChatBranch" — the operator assistant, docked bottom-right on every
 * signed-in page. It answers how-to questions, looks things up and prepares
 * changes AS the signed-in user (POST /api/assistant/chat). Anything that
 * changes data comes back as a confirm card and only runs when the person
 * presses Confirm (POST /api/assistant/confirm) — the model proposes, the
 * person decides. The conversation lives in sessionStorage (this tab only).
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
const STORE_KEY = 'chatbranch-assistant'
const MAX_SENT = 20

interface Step {
  tool: string
  title: string
  ok: boolean
  summary: string
}

type ActionState = 'pending' | 'running' | 'done' | 'failed' | 'cancelled' | 'expired'

interface PendingAction {
  id: string
  tool: string
  title: string
  args: Record<string, unknown>
  summary: string
  expires_at: string
  state?: ActionState
  message?: string
}

interface NavChip {
  label: string
  href: string
}

interface ChatMsg {
  id: string
  role: 'user' | 'assistant'
  content: string
  steps?: Step[]
  pending?: PendingAction[]
  navigation?: NavChip[]
  error?: boolean
}

const uid = () => Math.random().toString(36).slice(2, 10)

function load(): { messages: ChatMsg[]; mode: string } {
  try {
    const raw = sessionStorage.getItem(STORE_KEY)
    if (raw) {
      const d = JSON.parse(raw)
      if (Array.isArray(d.messages)) return { messages: d.messages, mode: d.mode || '' }
    }
  } catch {
    /* private mode / corrupt — start fresh */
  }
  return { messages: [], mode: '' }
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(body),
  })
  if (expireSessionOn401(res)) throw new Error('Your session expired.')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

function suggestions(): string[] {
  const out: string[] = []
  if (hasCap('chats.view')) out.push('How many chats are waiting for a reply?')
  if (hasCap('contacts.view')) out.push('Find contact…')
  if (hasCap('activity.view')) out.push('Show recent activity')
  out.push('How do I connect WhatsApp?')
  if (hasCap('analytics.view')) out.push('Go to analytics')
  return out.slice(0, 5)
}

export function AssistantDock() {
  const pathname = usePathname() || '/'
  const router = useRouter()
  const { activeEntity } = useEntity()

  const [mounted, setMounted] = useState(false)
  const [open, setOpen] = useState(false)
  const [messages, setMessages] = useState<ChatMsg[]>([])
  const [mode, setMode] = useState('')
  const [draft, setDraft] = useState('')
  const [sending, setSending] = useState(false)
  const [hints, setHints] = useState<string[]>([])
  const [signedIn, setSignedIn] = useState(false)

  const inputRef = useRef<HTMLTextAreaElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const s = load()
    setMessages(s.messages)
    setMode(s.mode)
    setHints(suggestions())
    setMounted(true)
  }, [])

  // the shell re-renders on every navigation; re-read the session then
  useEffect(() => setSignedIn(!!getAuth()), [pathname])

  useEffect(() => {
    if (!mounted) return
    try {
      sessionStorage.setItem(STORE_KEY, JSON.stringify({ messages: messages.slice(-60), mode }))
    } catch {
      /* storage full / blocked — the chat still works in memory */
    }
  }, [messages, mode, mounted])

  // focus the composer on open; Esc closes
  useEffect(() => {
    if (!open) return
    const t = window.setTimeout(() => inputRef.current?.focus(), 30)
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.clearTimeout(t)
      window.removeEventListener('keydown', onKey)
    }
  }, [open])

  useLayoutEffect(() => {
    const el = listRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages, sending, open])

  // grow the composer with its content (up to ~5 lines)
  useLayoutEffect(() => {
    const el = inputRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 132)}px`
  }, [draft, open])

  const send = useCallback(
    async (text: string) => {
      const content = text.trim()
      if (!content || sending) return
      const mine: ChatMsg = { id: uid(), role: 'user', content }
      const history = [...messages, mine]
      setMessages(history)
      setDraft('')
      setSending(true)
      try {
        const d = await post<{
          reply: string
          steps: Step[]
          pending_actions: PendingAction[]
          navigation: NavChip[]
          mode: string
        }>('assistant/chat', {
          messages: history
            .filter((m) => !m.error)
            .slice(-MAX_SENT)
            .map((m) => ({ role: m.role, content: m.content })),
          context: { path: pathname, entity: activeEntity?.slug ?? null },
        })
        setMode(d.mode)
        setMessages((ms) => [
          ...ms,
          {
            id: uid(),
            role: 'assistant',
            content: d.reply,
            steps: d.steps,
            pending: d.pending_actions.map((p) => ({ ...p, state: 'pending' })),
            navigation: d.navigation,
          },
        ])
      } catch (e) {
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
        setSending(false)
      }
    },
    [messages, sending, pathname, activeEntity],
  )

  const patchAction = (msgId: string, actionId: string, patch: Partial<PendingAction>, nav?: NavChip[]) =>
    setMessages((ms) =>
      ms.map((m) =>
        m.id !== msgId
          ? m
          : {
              ...m,
              pending: m.pending?.map((p) => (p.id === actionId ? { ...p, ...patch } : p)),
              navigation: nav?.length ? [...(m.navigation ?? []), ...nav] : m.navigation,
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
        const d = await post<{ ok: boolean; message?: string; navigation?: NavChip[] }>('assistant/confirm', {
          id: action.id,
        })
        patchAction(msgId, action.id, { state: d.ok ? 'done' : 'failed', message: d.message }, d.navigation)
      } else {
        await post('assistant/cancel', { id: action.id })
        patchAction(msgId, action.id, { state: 'cancelled' })
      }
    } catch (e) {
      const text = e instanceof Error ? e.message : String(e)
      patchAction(msgId, action.id, {
        state: /expired/i.test(text) ? 'expired' : 'failed',
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

  if (!mounted || !signedIn) return null

  // The inbox composer and the flow canvas own the bottom-right corner, so the
  // launcher shrinks to a round button and sits higher on those routes.
  const crowded = pathname.startsWith('/chats') || pathname.startsWith('/flow-builder')

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
            <header className="flex items-center gap-2.5 border-b border-line px-4 py-3">
              <span className="text-text-heading">
                <BranchMark size={22} />
              </span>
              <div className="min-w-0 flex-1">
                <div className="display text-[18px] leading-tight text-text-heading">Ask ChatBranch</div>
                <div className="text-[11.5px] text-text-muted">
                  Acts as you · changes wait for your OK
                  {mode === 'sandbox' && (
                    <span
                      className="ml-1.5 rounded-full bg-fill-hover px-1.5 py-px text-[10px] font-semibold text-text-mid"
                      title="No AI model is connected, so I understand simple requests and the product guide."
                    >
                      Sandbox
                    </span>
                  )}
                </div>
              </div>
              {messages.length > 0 && (
                <button
                  type="button"
                  onClick={() => setMessages([])}
                  title="New conversation"
                  aria-label="New conversation"
                  className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
                >
                  <RotateCcw size={14} aria-hidden />
                </button>
              )}
              <button
                type="button"
                onClick={() => setOpen(false)}
                aria-label="Close assistant"
                className="grid h-8 w-8 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
              >
                <X size={15} aria-hidden />
              </button>
            </header>

            <div ref={listRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-4" aria-live="polite">
              {messages.length === 0 ? (
                <div className="flex flex-col gap-4 pt-6">
                  <div>
                    <div className="display text-[24px] leading-tight text-text-heading">How can I help?</div>
                    <p className="mt-1.5 text-[13px] leading-relaxed text-text-muted">
                      Ask how to do something, look up a contact or a conversation, or have me prepare a change — I’ll
                      show it to you before anything happens.
                    </p>
                  </div>
                  <div className="flex flex-col items-start gap-1.5">
                    {hints.map((h) => (
                      <button
                        key={h}
                        type="button"
                        onClick={() => (h.endsWith('…') ? (setDraft(h.replace('…', ' ')), inputRef.current?.focus()) : send(h))}
                        className="rounded-full border border-line-strong px-3 py-1.5 text-left text-[12.5px] text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high"
                      >
                        {h}
                      </button>
                    ))}
                  </div>
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
            </div>

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
                  disabled={!draft.trim() || sending}
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
