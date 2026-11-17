import type { MetadataRoute } from 'next'
import { INDEXABLE, SITE_ORIGIN } from '@/lib/publicSite'

/** sitemap.xml: the public Help Center only (robots.ts keeps everything else out).
 *  Published help articles are read from the API at request time, hourly. */
export const revalidate = 3600

interface ArticleSummary {
  slug: string
}

async function articleSlugs(): Promise<string[]> {
  try {
    const r = await fetch(`${process.env.NEXT_PUBLIC_API_URL}/help/articles`, {
      next: { revalidate: 3600 },
      signal: AbortSignal.timeout(5000),
    })
    if (!r.ok) return []
    const d = (await r.json()) as { categories?: { articles?: ArticleSummary[] }[] }
    return (d.categories ?? []).flatMap((c) => (c.articles ?? []).map((a) => a.slug))
  } catch {
    return []
  }
}

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  if (!INDEXABLE) return []
  const slugs = await articleSlugs()
  return [
    { url: `${SITE_ORIGIN}/help/manual`, priority: 1 },
    { url: `${SITE_ORIGIN}/help`, priority: 0.8 },
    ...slugs.map((s) => ({ url: `${SITE_ORIGIN}/help/${encodeURIComponent(s)}`, priority: 0.6 })),
  ]
}
