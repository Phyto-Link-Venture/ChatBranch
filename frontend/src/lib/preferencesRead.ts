import { API } from '@/lib/apiBase'
import { authHeaders } from './auth'

/**
 * GET /api/auth/preferences, shared by the once-per-sign-in syncs the Shell
 * starts together (appearance, language, time zone, page guides): they used to
 * send four identical requests in the same tick. Only a read still IN FLIGHT is
 * shared — a later call (after a save) always asks the server again.
 *
 * Resolves to the parsed body, `null` for a non-2xx answer; rejects on a
 * network / JSON failure (each caller already catches that). Callers must
 * treat the object as read-only — it is the same one for all of them.
 */
let inflight: { key: string; p: Promise<Record<string, unknown> | null> } | null = null

export function readPreferences(): Promise<Record<string, unknown> | null> {
  const headers = authHeaders()
  const key = JSON.stringify(headers)
  if (inflight && inflight.key === key) return inflight.p
  const p = fetch(`${API}/auth/preferences`, { headers }).then((res) =>
    res.ok ? (res.json() as Promise<Record<string, unknown>>) : null,
  )
  const entry = { key, p }
  inflight = entry
  const done = () => {
    if (inflight === entry) inflight = null
  }
  p.then(done, done)
  return p
}
