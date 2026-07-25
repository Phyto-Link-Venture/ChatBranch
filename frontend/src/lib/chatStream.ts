/** Shared SSE subscription to the unified-inbox change stream
 * (GET /api/whatsapp/stream — covers ALL chat channels, see whatsapp/views.py).
 *
 * Replaces interval polling on /chats: the backend emits `event: change`
 * whenever any thread/message changes and listeners re-fetch only then, so an
 * idle dashboard stops flooding the dev-server log with threads GETs.
 *
 * Implemented with fetch + ReadableStream instead of EventSource because the
 * API wants the Authorization header, which EventSource can't send. One
 * connection is shared by all listeners (ref-counted): it opens with the
 * first subscriber and aborts with the last. The server bounds each
 * connection to ~5 min; the loop here reconnects (and re-notifies, so
 * anything missed while offline is caught up).
 */
import { authHeaders, expireSessionOn401 } from './auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'
const RETRY_MS = 4000

type Listener = () => void

const listeners = new Set<Listener>()
let ctrl: AbortController | null = null
let running = false

function notify() {
  listeners.forEach((fn) => fn())
}

async function run() {
  running = true
  while (listeners.size) {
    ctrl = new AbortController()
    try {
      const res = await fetch(`${API}/whatsapp/stream`, {
        headers: authHeaders(),
        signal: ctrl.signal,
        cache: 'no-store',
      })
      if (expireSessionOn401(res)) {
        running = false
        return // stale token — bouncing to /login, don't retry
      }
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)
      // The server sends an initial `change` frame on connect, so listeners
      // refresh once per (re)connect without an extra nudge here.
      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buf = ''
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break // bounded connection ended — reconnect
        buf += decoder.decode(value, { stream: true })
        let sep
        while ((sep = buf.indexOf('\n\n')) !== -1) {
          const frame = buf.slice(0, sep)
          buf = buf.slice(sep + 2)
          if (frame.split('\n').some((l) => l.startsWith('event: change'))) notify()
        }
      }
    } catch {
      /* aborted or API down — retry below keeps working as the fallback */
    }
    if (listeners.size) await new Promise((r) => setTimeout(r, RETRY_MS))
  }
  running = false
  // a subscriber may have arrived between the loop's last check and here
  // (StrictMode's unmount/remount does this) — restart for them
  if (listeners.size) void run()
}

/** Subscribe to inbox change events. Returns the unsubscribe function. */
export function subscribeChatStream(listener: Listener): () => void {
  listeners.add(listener)
  if (!running) void run()
  return () => {
    listeners.delete(listener)
    if (!listeners.size) ctrl?.abort()
  }
}
