import { SITE_ORIGIN } from '@/lib/publicSite'
import { faq, glossary } from '@/i18n/messages/en/manual/help'

/**
 * llms.txt (https://llmstxt.org): a plain-Markdown summary of ChatBranch for AI
 * assistants, built from the manual's own English words so it never drifts.
 */
export const dynamic = 'force-static'

export function GET() {
  const body = `# ChatBranch

> ChatBranch is a multi-channel customer conversation platform: one shared inbox for WhatsApp, email, Facebook Messenger, Instagram, Telegram, LINE, Viber, SMS, WeChat, Zalo and website chat, with an AI that answers customers, journeys and automations, bookings, contacts, teams, analytics and AI voice calls. Made by Phyto Link Venture in Malaysia.

- [User manual](${SITE_ORIGIN}/help/manual): every page and feature, step by step, with troubleshooting and a glossary
- [Help Center](${SITE_ORIGIN}/help): help articles
- [Product overview, pricing and demo](https://phytolink-venture.com/chatbranch)
- [Company summary](https://phytolink-venture.com/llms.txt)

## Troubleshooting

${Object.values(faq).map((f) => `### ${f.q}\n\n${f.a}`).join('\n\n')}

## Glossary

${Object.values(glossary).map((g) => `- **${g.term}**: ${g.def}`).join('\n')}
`
  return new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } })
}
