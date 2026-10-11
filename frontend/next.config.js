/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  // A self-contained server (.next/standalone: server.js + only the runtime
  // node_modules it traces) — what the Dockerfile ships, without src/ or dev deps.
  output: 'standalone',
  env: {
    NEXT_PUBLIC_API_URL: process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api',
  },
  // The Dashboard is the home page; it used to be called Analytics, so old
  // /analytics links move on to it (Next keeps the query string). Redirecting
  // on the server keeps these out of the client render (see app/page.tsx).
  async redirects() {
    return [
      { source: '/', destination: '/dashboard', permanent: false },
      { source: '/analytics', destination: '/dashboard', permanent: false },
    ]
  },
  // Security headers (docs/security.md). The CSP is what Next 14's app router
  // really needs: its inline bootstrap scripts (and the pre-paint theme script)
  // need 'unsafe-inline' — no nonce plumbing — and 'unsafe-eval' only in `next
  // dev` (React Refresh). Data goes to this origin and the API's (absolute
  // NEXT_PUBLIC_API_URL); media/images may come from the API's signed links.
  // Every page refuses to be framed except the manual's embed view
  // (/help/manual?embed=1), which anyone may embed; the microphone is only
  // for /persona (recording a voice to clone).
  async headers() {
    const dev = process.env.NODE_ENV !== 'production'
    let apiOrigin = ''
    try {
      const api = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'
      if (/^https?:\/\//i.test(api)) apiOrigin = new URL(api).origin
    } catch {
      apiOrigin = ''
    }
    const csp = (frameAncestors) =>
      [
        "default-src 'self'",
        `script-src 'self' 'unsafe-inline'${dev ? " 'unsafe-eval'" : ''}`,
        "style-src 'self' 'unsafe-inline'",
        `img-src 'self' data: blob: https:${apiOrigin ? ` ${apiOrigin}` : ''}`,
        `media-src 'self' data: blob:${apiOrigin ? ` ${apiOrigin}` : ''}`,
        "font-src 'self' data:",
        `connect-src 'self'${apiOrigin ? ` ${apiOrigin}` : ''}${dev ? ' ws: wss:' : ''}`,
        // the document designer's PDF preview (an auth-fetched blob) and the custom-layout srcdoc
        "frame-src 'self' blob: data:",
        "worker-src 'self' blob:",
        "manifest-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        `frame-ancestors ${frameAncestors}`,
      ].join('; ')
    const permissions = (mic) =>
      `camera=(), microphone=${mic}, geolocation=(), payment=(), usb=(), serial=(), midi=()`
    const embed = [{ type: 'query', key: 'embed' }]
    return [
      {
        source: '/:path*',
        headers: [
          { key: 'X-Content-Type-Options', value: 'nosniff' },
          { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
          { key: 'Permissions-Policy', value: permissions('()') },
          { key: 'Strict-Transport-Security', value: 'max-age=31536000' },
        ],
      },
      {
        source: '/:path*',
        missing: embed,
        headers: [
          { key: 'Content-Security-Policy', value: csp("'none'") },
          { key: 'X-Frame-Options', value: 'DENY' },
        ],
      },
      // ?embed on any other page still can't be framed (no X-Frame-Options here, so the
      // manual's entry below can open frame-ancestors for itself)
      { source: '/:path*', has: embed, headers: [{ key: 'Content-Security-Policy', value: csp("'none'") }] },
      { source: '/help/manual', has: embed, headers: [{ key: 'Content-Security-Policy', value: csp('*') }] },
      { source: '/persona', headers: [{ key: 'Permissions-Policy', value: permissions('(self)') }] },
    ]
  },
}

module.exports = nextConfig
