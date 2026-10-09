/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  env: {
    NEXT_PUBLIC_API_URL: process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api',
  },
  // Analytics is the home page; /dashboard was folded into it. Redirecting on
  // the server keeps these out of the client render (see app/page.tsx).
  async redirects() {
    return [
      { source: '/', destination: '/analytics', permanent: false },
      { source: '/dashboard', destination: '/analytics', permanent: false },
    ]
  },
}

module.exports = nextConfig
