import type { MetadataRoute } from 'next'

/**
 * robots.txt: the operator app is never indexed — only the public Help Center
 * (and its manual) may be. Search engines that ignore this still get nothing:
 * every operator page needs a signed-in session, and the API answers
 * `X-Robots-Tag: noindex` (docs/security.md).
 */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: [{ userAgent: '*', allow: ['/help', '/help/'], disallow: '/' }],
  }
}
