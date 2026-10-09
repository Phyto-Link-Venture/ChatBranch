import { authHeaders, expireSessionOn401 } from '@/lib/auth'

/**
 * Types + calls for "Ask ChatBranch" (POST /api/assistant/chat|confirm|cancel
 * and the per-user history under /api/assistant/conversations). The server is
 * the source of truth for the conversation; the dock only renders it.
 */

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

export interface Step {
  tool: string
  title: string
  ok: boolean
  summary: string
}

export type ActionState = 'pending' | 'running' | 'done' | 'failed' | 'cancelled' | 'expired'

export interface PendingAction {
  id: string
  tool: string
  title: string
  args: Record<string, unknown>
  summary: string
  expires_at: string
  state?: ActionState
  message?: string
}

export interface NavChip {
  label: string
  href: string
}

export interface ChatMsg {
  id: string
  role: 'user' | 'assistant'
  content: string
  steps?: Step[]
  pending?: PendingAction[]
  navigation?: NavChip[]
  /** A local-only failure bubble (never stored). */
  error?: boolean
}

export interface ChatReply {
  reply: string
  steps: Step[]
  pending_actions: PendingAction[]
  navigation: NavChip[]
  mode: string
  conversation_id: number | null
  title: string | null
  message_id: number | null
}

export interface ConversationSummary {
  id: number
  title: string
  entity_id: number | null
  created_at: string
  updated_at: string
  message_count: number
  snippet: string
}

export interface ConversationPage {
  conversations: ConversationSummary[]
  total: number
  has_more: boolean
  next_before: string | null
}

interface StoredMessage {
  id: number
  role: 'user' | 'assistant'
  text: string
  created_at: string
  payload: {
    steps?: Step[]
    pending_actions?: PendingAction[]
    navigation?: NavChip[]
    mode?: string
  }
}

export interface Conversation {
  id: number
  title: string
  entity_id: number | null
  created_at: string
  updated_at: string
  messages: StoredMessage[]
}

/** An HTTP error that keeps the status (404 = the chat is gone) and the server's body. */
export class AssistantError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: Record<string, unknown> = {},
  ) {
    super(message)
  }
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    method,
    headers: { ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...authHeaders() },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (expireSessionOn401(res)) throw new AssistantError('Your session expired.', 401)
  const data = (await res.json().catch(() => ({}))) as Record<string, unknown>
  if (!res.ok) throw new AssistantError((data.detail as string) || `HTTP ${res.status}`, res.status, data)
  return data as T
}

export const assistantApi = {
  chat: (body: { message: string; conversation_id: number | null; context: { path: string; entity: string | null } }) =>
    call<ChatReply>('POST', 'assistant/chat', body),
  confirm: (id: string) =>
    call<{ ok: boolean; status: ActionState; message?: string; navigation?: NavChip[] }>('POST', 'assistant/confirm', {
      id,
    }),
  cancel: (id: string) => call<{ ok: boolean; status: ActionState }>('POST', 'assistant/cancel', { id }),
  list: (before?: string | null, limit = 30) =>
    call<ConversationPage>(
      'GET',
      `assistant/conversations?limit=${limit}${before ? `&before=${encodeURIComponent(before)}` : ''}`,
    ),
  get: (id: number) => call<Conversation>('GET', `assistant/conversations/${id}`),
  rename: (id: number, title: string) =>
    call<ConversationSummary>('PATCH', `assistant/conversations/${id}`, { title }),
  remove: (id: number) => call<{ ok: boolean }>('DELETE', `assistant/conversations/${id}`),
  clearAll: () => call<{ ok: boolean; deleted: number }>('DELETE', 'assistant/conversations'),
}

/** A stored conversation as the dock's message list. */
export function toChatMessages(c: Conversation): { messages: ChatMsg[]; mode: string } {
  let mode = ''
  const messages = c.messages.map((m): ChatMsg => {
    if (m.role === 'user') return { id: `m${m.id}`, role: 'user', content: m.text }
    if (m.payload.mode) mode = m.payload.mode
    return {
      id: `m${m.id}`,
      role: 'assistant',
      content: m.text,
      steps: m.payload.steps ?? [],
      pending: m.payload.pending_actions ?? [],
      navigation: m.payload.navigation ?? [],
    }
  })
  return { messages, mode }
}
