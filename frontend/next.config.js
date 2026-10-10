/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
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
}

module.exports = nextConfig
