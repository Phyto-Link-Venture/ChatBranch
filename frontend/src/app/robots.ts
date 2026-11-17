import type { MetadataRoute } from 'next'
import { INDEXABLE, SITE_ORIGIN } from '@/lib/publicSite'

/**
 * robots.txt: the operator app is never indexed — only the public Help Center
 * (and its manual) may be, plus the files that describe it (sitemap, llms.txt).
 * Search engines that ignore this still get nothing: every operator page needs
 * a signed-in session, and the API answers `X-Robots-Tag: noindex`
 * (docs/security.md). Demo and local installs are not indexed at all (see
 * lib/publicSite.ts). AI crawlers (GPTBot, ClaudeBot, PerplexityBot, ...) fall
 * under `*` on purpose: the Help Center is meant to be quoted.
 */
export default function robots(): MetadataRoute.Robots {
  if (!INDEXABLE) return { rules: [{ userAgent: '*', disallow: '/' }] }
  return {
    rules: [
      {
        userAgent: '*',
        allow: ['/help', '/help/', '/llms.txt', '/sitemap.xml'],
        disallow: '/',
      },
    ],
    sitemap: `${SITE_ORIGIN}/sitemap.xml`,
  }
}
