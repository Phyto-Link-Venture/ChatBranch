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
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401, hasAppSession } from './auth'

const RETRY_MS = 4000

type Listener = () => void

const listeners = new Set<Listener>()
let ctrl: AbortController | null = null
let running = false

/** Connection state for the top bar's Live dot: connected (green),
 *  reconnecting (amber — retrying after a drop or still dialling), offline
 *  (grey — no subscriber, browser offline, or several failures in a row). */
export type StreamStatus = 'connected' | 'reconnecting' | 'offline'
let status: StreamStatus = 'offline'
let failures = 0
const statusListeners = new Set<(s: StreamStatus) => void>()
function setStatus(s: StreamStatus) {
  if (s === status) return
  status = s
  statusListeners.forEach((fn) => fn(s))
}

function notify() {
  listeners.forEach((fn) => fn())
}

async function run() {
  running = true
  setStatus('reconnecting') // dialling
  while (listeners.size) {
    if (!hasAppSession()) {
      // signed out, or held on the account-setup screen: the API would refuse — look again later
      setStatus('offline')
      await new Promise((r) => setTimeout(r, RETRY_MS))
      continue
    }
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
      failures = 0
      setStatus('connected')
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
      failures++
    }
    if (listeners.size) {
      const offline = typeof navigator !== 'undefined' && navigator.onLine === false
      setStatus(offline || failures >= 3 ? 'offline' : 'reconnecting')
      await new Promise((r) => setTimeout(r, RETRY_MS))
    }
  }
  running = false
  setStatus('offline')
  // a subscriber may have arrived between the loop's last check and here
  // (StrictMode's unmount/remount does this) — restart for them
  if (listeners.size) void run()
}

// Grace period before the last unsubscribe closes the connection. Pages
// mount and unmount their subscribers on navigation (and effects re-run when
// their deps change); closing on every momentary zero dropped the stream and
// showed "reconnecting" for a whole retry interval on each page load.
const CLOSE_GRACE_MS = 2000
let closeTimer: ReturnType<typeof setTimeout> | null = null

/** Subscribe to inbox change events. Returns the unsubscribe function. */
export function subscribeChatStream(listener: Listener): () => void {
  listeners.add(listener)
  if (closeTimer) {
    clearTimeout(closeTimer)
    closeTimer = null
  }
  if (!running) void run()
  return () => {
    listeners.delete(listener)
    if (listeners.size || closeTimer) return
    closeTimer = setTimeout(() => {
      closeTimer = null
      if (!listeners.size) ctrl?.abort()
    }, CLOSE_GRACE_MS)
  }
}

/** Current connection state (see StreamStatus). */
export function getStreamStatus(): StreamStatus {
  return status
}

/** Watch the connection state. Does NOT open the stream by itself — pair it
 *  with subscribeChatStream. Returns the unsubscribe function. */
export function subscribeStreamStatus(fn: (s: StreamStatus) => void): () => void {
  statusListeners.add(fn)
  fn(status)
  return () => {
    statusListeners.delete(fn)
  }
}
