/**
 * The AI playground's browser cache key (`playground:<username>`, localStorage)
 * and its sweep. Kept free of imports so lib/auth can clear it on sign-out,
 * session expiry and every login without an import cycle — the same rule as
 * the assistant's `assistant:*` keys (lib/assistantCache.ts).
 */

export const PLAYGROUND_PREFIX = 'playground:'

/** Remove every playground key (any user). */
export function clearPlaygroundCache(): void {
  if (typeof window === 'undefined') return
  try {
    const doomed: string[] = []
    for (let i = 0; i < window.localStorage.length; i++) {
      const k = window.localStorage.key(i)
      if (k && k.startsWith(PLAYGROUND_PREFIX)) doomed.push(k)
    }
    doomed.forEach((k) => window.localStorage.removeItem(k))
  } catch {
    /* storage blocked — nothing cached */
  }
}
