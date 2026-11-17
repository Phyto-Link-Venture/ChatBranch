/**
 * The public origin of this install (e.g. https://chatbranch.example.com),
 * read from the build-time API URL so every install and the demo instance
 * name themselves — used by robots.txt, sitemap.xml, llms.txt and the Help
 * Center's canonical links and JSON-LD.
 */
function origin(): string {
  try {
    const u = new URL(process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api')
    return u.origin
  } catch {
    return 'http://localhost:3000'
  }
}

export const SITE_ORIGIN = origin()

/** Only a real, public, non-demo install should be indexed. A demo serves the
 *  same help content as production, so indexing it would split search ranking
 *  between two copies; localhost has nothing to index. */
export const INDEXABLE = (() => {
  const host = new URL(SITE_ORIGIN).hostname
  return !(host === 'localhost' || host.startsWith('127.') || host.startsWith('demo.'))
})()

export function jsonLd(data: unknown) {
  // Escape "<" so a value can never close the <script> tag.
  return { __html: JSON.stringify(data).replace(/</g, '\\u003c') }
}
