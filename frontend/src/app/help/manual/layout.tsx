import type { Metadata } from 'next'
import type { ReactNode } from 'react'
import { SITE_ORIGIN, jsonLd } from '@/lib/publicSite'

/** Search/AI metadata for the public manual. The page itself is a client
 *  component, so its metadata and JSON-LD live here. */
export const metadata: Metadata = {
  title: 'ChatBranch User Manual — Step-by-Step Guide to Every Feature',
  description:
    'The complete ChatBranch user manual: connect WhatsApp, email and social channels, set up the AI persona, knowledge base and house rules, bookings, journeys, automations, teams and troubleshooting.',
  alternates: { canonical: '/help/manual' },
  openGraph: {
    title: 'ChatBranch User Manual',
    description: 'A practical guide to every part of ChatBranch.',
    url: '/help/manual',
    type: 'article',
  },
}

const manualLd = {
  '@context': 'https://schema.org',
  '@type': 'TechArticle',
  headline: 'ChatBranch user manual',
  description: 'A practical guide to every part of ChatBranch.',
  url: `${SITE_ORIGIN}/help/manual`,
  inLanguage: ['en', 'ms', 'zh'],
  about: {
    '@type': 'SoftwareApplication',
    name: 'ChatBranch',
    applicationCategory: 'BusinessApplication',
    url: 'https://phytolink-venture.com/chatbranch',
  },
  publisher: { '@type': 'Organization', name: 'Phyto Link Venture', url: 'https://phytolink-venture.com' },
}

export default function ManualLayout({ children }: { children: ReactNode }) {
  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={jsonLd(manualLd)} />
      {children}
    </>
  )
}
