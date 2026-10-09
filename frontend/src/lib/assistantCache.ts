/**
 * The "Ask ChatBranch" dock's browser cache. The conversation itself lives on
 * the server (per user, /api/assistant/conversations); the browser keeps only
 * which chat is open and the unsent draft, under a key that names the signed-in
 * user — `assistant:<username>:current` (sessionStorage, this tab only).
 *
 * Nothing here may outlive a session: sign-out, an expired session and every
 * successful sign-in call `clearAssistantCache()` (lib/auth.ts), and the dock
 * drops any cache that belongs to someone else before reading its own. So
 * signing in as another user in the same tab never shows the previous user's
 * chat.
 */

const PREFIX = 'assistant:'
/** Where the dock kept the whole conversation before history moved server-side. */
const LEGACY_KEYS = ['chatbranch-assistant']

export interface AssistantCurrent {
  conversationId: number | null
  draft: string
}

const keyFor = (username: string) => `${PREFIX}${username}:current`

function stores(): Storage[] {
  const out: Storage[] = []
  try {
    out.push(window.sessionStorage)
  } catch {
    /* blocked */
  }
  try {
    out.push(window.localStorage)
  } catch {
    /* blocked */
  }
  return out
}

function removeWhere(match: (key: string) => boolean): void {
  if (typeof window === 'undefined') return
  for (const s of stores()) {
    try {
      const doomed: string[] = []
      for (let i = 0; i < s.length; i++) {
        const k = s.key(i)
        if (k && match(k)) doomed.push(k)
      }
      doomed.forEach((k) => s.removeItem(k))
    } catch {
      /* storage blocked — nothing cached there */
    }
  }
}

/** Remove every assistant key (any user) and the legacy whole-conversation key. */
export function clearAssistantCache(): void {
  removeWhere((k) => k.startsWith(PREFIX) || LEGACY_KEYS.includes(k))
}

/** Remove the assistant keys of anyone but `username` (and the legacy key). */
export function dropForeignAssistantCache(username: string): void {
  const mine = `${PREFIX}${username}:`
  removeWhere((k) => (k.startsWith(PREFIX) && !k.startsWith(mine)) || LEGACY_KEYS.includes(k))
}

export function readAssistantCurrent(username: string): AssistantCurrent {
  try {
    const raw = window.sessionStorage.getItem(keyFor(username))
    if (raw) {
      const d = JSON.parse(raw) as { conversation_id?: unknown; draft?: unknown }
      return {
        conversationId: typeof d.conversation_id === 'number' ? d.conversation_id : null,
        draft: typeof d.draft === 'string' ? d.draft : '',
      }
    }
  } catch {
    /* private mode / corrupt — start fresh */
  }
  return { conversationId: null, draft: '' }
}

export function writeAssistantCurrent(username: string, current: AssistantCurrent): void {
  try {
    if (current.conversationId == null && !current.draft) {
      window.sessionStorage.removeItem(keyFor(username))
      return
    }
    window.sessionStorage.setItem(
      keyFor(username),
      JSON.stringify({ conversation_id: current.conversationId, draft: current.draft.slice(0, 4000) }),
    )
  } catch {
    /* storage full / blocked — the dock still works in memory */
  }
}
