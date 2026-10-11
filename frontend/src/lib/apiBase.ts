/**
 * The API base — the ONE place the dashboard learns where the C# API lives.
 *
 * `NEXT_PUBLIC_API_URL` is baked into the bundle at BUILD time and may be
 *   - absolute: `http://localhost:8090/api` (the default — dev, `npm run dev:all`)
 *     or `https://chat.example.com/api` (a server built for its own domain), or
 *   - RELATIVE: `/api` — what a release image is built with. One image then works
 *     on ANY domain behind a single-origin proxy (`/api/*` → the API, everything
 *     else → the dashboard), e.g. `deploy/customer/`.
 *
 * `fetch()`, `EventSource`, `<img src>` and `<a href>` take either form, so they
 * use `API` / `apiResource()` as they are. Anything SHOWN to a person or HANDED to
 * another site (the MCP URL on /developers, the widget embed snippet, webhook
 * callback URLs, a link opened in another tab) must be absolute: use
 * `absoluteApiUrl()` / `absoluteApiResource()`, which resolve a relative base
 * against the page's own origin. They read `window`, so call them in the browser
 * (an effect, a handler, or a render that only happens after the session is
 * read) — during server rendering they return the relative form unchanged.
 *
 * Nothing in the dashboard calls the API from the Next.js server (no route
 * handlers, no server-side fetches), so no server-only base is needed. A future
 * one would need its own runtime variable (e.g. `API_INTERNAL_URL`), because a
 * relative URL means nothing inside the container.
 */

const DEFAULT_API = 'http://localhost:8090/api'

/** The API base as built, without a trailing slash: `http://localhost:8090/api`, `/api`, … */
export const API: string = (process.env.NEXT_PUBLIC_API_URL || DEFAULT_API).replace(/\/+$/, '')

/**
 * The base minus its trailing `/api` — where the API's ROOT paths live (the
 * server hands out paths like `/api/media/s/…` and `/api/messaging/webhook/…`).
 * `''` for the release build's `/api`: root paths then hit the page's own origin.
 */
export const API_ROOT: string = API.replace(/\/api$/, '')

/** `http:`, `https:`, `blob:`, `data:` … — a URL that needs no base. */
const HAS_SCHEME = /^[a-z][a-z0-9+.-]*:/i

/**
 * A URL or path the API returned, made fetchable: a full URL is kept, a root path
 * (`/api/…`) is put under the API's root, anything else under the API base.
 * Relative when the base is relative — fine for fetch / src / href.
 */
export function apiResource(u: string): string {
  if (!u) return API_ROOT
  if (HAS_SCHEME.test(u)) return u
  return u.startsWith('/') ? `${API_ROOT}${u}` : `${API}/${u}`
}

/** Resolve a URL or path against the page's origin (browser only; unchanged on the server). */
export function absoluteUrl(u: string): string {
  if (HAS_SCHEME.test(u) || typeof window === 'undefined') return u
  try {
    return new URL(u, window.location.origin).toString()
  } catch {
    return u
  }
}

/**
 * The API base, or a path under it, as an ABSOLUTE URL — for anything shown to a
 * person or pasted somewhere else: `absoluteApiUrl('mcp')` →
 * `https://chat.example.com/api/mcp`, `absoluteApiUrl('widget.js')`.
 */
export function absoluteApiUrl(path = ''): string {
  const p = path.replace(/^\/+/, '')
  return absoluteUrl(p ? `${API}/${p}` : API)
}

/** `apiResource()` as an ABSOLUTE URL (webhook URLs to paste into a provider's console, a tab to open). */
export function absoluteApiResource(u: string): string {
  return absoluteUrl(apiResource(u))
}
