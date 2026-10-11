import { API } from '@/lib/apiBase'
import { authHeaders, getAuth } from './auth'

const reported = new Set<string>()

/**
 * Send a client-side crash to the API log (POST /api/client-errors) so the
 * server keeps the stack trace of what a user saw. Once per message per tab,
 * signed-in sessions only; never throws.
 */
export function reportClientError(error: unknown, extra?: { digest?: string; where?: string }): void {
  try {
    if (typeof window === 'undefined' || !getAuth()) return
    const e = error instanceof Error ? error : new Error(String(error))
    const key = `${extra?.where ?? ''}|${e.message}`
    if (reported.has(key)) return
    reported.add(key)
    void fetch(`${API}/client-errors`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({
        message: `${extra?.where ? `[${extra.where}] ` : ''}${e.name}: ${e.message}`,
        stack: e.stack ?? '',
        path: window.location.pathname + window.location.search,
        digest: extra?.digest ?? '',
        user_agent: navigator.userAgent,
      }),
      keepalive: true,
    }).catch(() => {})
  } catch {
    /* reporting must never cause a second failure */
  }
}

/** A deploy replaced the build under an open tab: its old code asked for a chunk that no longer exists. */
export function isStaleBuildError(error: unknown): boolean {
  const e = error as { name?: string; message?: string } | null
  return (
    e?.name === 'ChunkLoadError' ||
    /Loading (?:CSS )?chunk [\w-]+ failed|Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i.test(
      e?.message ?? '',
    )
  )
}

const RELOAD_KEY = 'cb-stale-build-reload'

/** Reload once (not in a loop) to pick up the new build. True when a reload was started. */
export function reloadForNewBuild(): boolean {
  try {
    const last = Number(sessionStorage.getItem(RELOAD_KEY) || 0)
    if (Date.now() - last < 60_000) return false // already tried in the last minute — show the error instead
    sessionStorage.setItem(RELOAD_KEY, String(Date.now()))
  } catch {
    /* storage blocked: still reload once */
  }
  window.location.reload()
  return true
}
