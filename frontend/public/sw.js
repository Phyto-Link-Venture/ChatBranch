/* ChatBranch service worker — hand-written, no build step.
 *
 * Strategy
 *   - precache: the /offline page (plus the /_next/static CSS/JS it references)
 *     and the app icons, so the fallback renders styled with no network.
 *   - /_next/static/*, fonts, /icons/*: cache-first (content-hashed, immutable).
 *   - navigations: network-first; when the network fails, the /offline page.
 *   - NEVER cached: /api/* and the SSE inbox stream (text/event-stream), and
 *     anything cross-origin (the API runs on its own origin in most setups).
 *
 * Bump VERSION to roll every cache: old caches are deleted on activate.
 */
const VERSION = 'cb-v1'
const PRECACHE = `${VERSION}-precache`
const RUNTIME = `${VERSION}-runtime`
const OFFLINE_URL = '/offline'

const PRECACHE_URLS = [
  OFFLINE_URL,
  '/manifest.webmanifest',
  '/icons/icon-192.png',
  '/icons/icon-512.png',
  '/icons/maskable-512.png',
  '/icons/apple-touch-icon.png',
  '/icons/favicon-32.png',
  '/icons/favicon.svg',
]

async function precache() {
  const cache = await caches.open(PRECACHE)
  // One by one: a missing icon must not abort the whole install.
  await Promise.all(
    PRECACHE_URLS.map((url) =>
      fetch(url, { cache: 'reload' })
        .then((res) => (res.ok ? cache.put(url, res) : undefined))
        .catch(() => undefined),
    ),
  )
  // The offline page's own static assets, so it renders styled offline.
  try {
    const res = await cache.match(OFFLINE_URL)
    const html = res ? await res.clone().text() : ''
    // <script>/<link> tags carry /_next/static/…; client components the page
    // needs ride in the flight data as bare "static/chunks/…" — take both.
    const found = html.match(/(?:\/_next\/)?static\/(?:chunks|css)\/[^"'\s)\\]+?\.(?:css|js)/g) || []
    const assets = new Set(found.map((p) => (p.startsWith('/_next/') ? p : `/_next/${p}`)))
    const runtime = await caches.open(RUNTIME)
    await Promise.all(
      [...assets].map((url) =>
        fetch(url)
          .then((r) => (r.ok ? runtime.put(url, r) : undefined))
          .catch(() => undefined),
      ),
    )
  } catch {
    /* the fallback still works, just unstyled */
  }
}

self.addEventListener('install', (event) => {
  event.waitUntil(precache().then(() => self.skipWaiting()))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const keys = await caches.keys()
      await Promise.all(keys.filter((k) => !k.startsWith(VERSION)).map((k) => caches.delete(k)))
      if (self.registration.navigationPreload) await self.registration.navigationPreload.enable()
      await self.clients.claim()
    })(),
  )
})

self.addEventListener('message', (event) => {
  if (event.data === 'SKIP_WAITING') self.skipWaiting()
})

function neverCache(request, url) {
  if (url.origin !== self.location.origin) return true
  if (url.pathname.startsWith('/api/')) return true
  if (url.pathname.endsWith('/stream')) return true
  const accept = request.headers.get('accept') || ''
  return accept.includes('text/event-stream')
}

function isStaticAsset(request, url) {
  return (
    url.pathname.startsWith('/_next/static/') ||
    url.pathname.startsWith('/icons/') ||
    request.destination === 'font' ||
    /\.(?:woff2?|ttf|otf)$/.test(url.pathname)
  )
}

async function cacheFirst(request) {
  const cache = await caches.open(RUNTIME)
  const hit = await cache.match(request)
  if (hit) return hit
  const res = await fetch(request)
  if (res.ok && res.type === 'basic') cache.put(request, res.clone())
  return res
}

async function networkFirstNavigation(event) {
  try {
    const preloaded = await event.preloadResponse
    if (preloaded) return preloaded
    return await fetch(event.request)
  } catch {
    const url = new URL(event.request.url)
    const cache = await caches.open(PRECACHE)
    const offline = await cache.match(OFFLINE_URL, { ignoreSearch: true })
    if (!offline) return new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } })
    if (url.pathname === OFFLINE_URL) return offline
    // Redirect rather than answer in place: the page must load under its own
    // URL or the app router hydrates /offline's HTML as the requested route.
    // ?from= lets it return to where the user was going once back online.
    return Response.redirect(`${OFFLINE_URL}?from=${encodeURIComponent(url.pathname + url.search)}`, 302)
  }
}

self.addEventListener('fetch', (event) => {
  const { request } = event
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (neverCache(request, url)) return // straight to the network, untouched

  if (request.mode === 'navigate') {
    event.respondWith(networkFirstNavigation(event))
    return
  }
  if (isStaticAsset(request, url)) {
    event.respondWith(cacheFirst(request))
  }
})
