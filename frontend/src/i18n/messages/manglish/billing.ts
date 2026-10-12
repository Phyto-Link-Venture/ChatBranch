// Manglish overlay for the `billing` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const billing: Overlay<Messages['billing']> = {
  subtitle: 'See how much each company kena pay for its AI usage, month by month',
  pdfFailed: 'Alamak, cannot make the PDF: {error}',
  managerLine: 'Cost {cost} · markup {markup}% · margin {margin}. Don’t worry, only billing managers can see this line.',
  unattributed: '{requests} requests ({cost}) don’t belong to any company — skill drafting, embeddings and the operator assistant. Nobody gets billed for these ones.',
  settings: {
    defaultsHelp: 'AI usage is costed in USD from the model price table. Each company pays that cost plus your markup, converted to your currency, plus a monthly fee. Companies only see the billed amounts, not your cost.',
    example: 'Say USD 10.00 of AI usage — that one gets billed as {currency} {amount} at the default {markup}% markup.',
    companiesHelp: 'Leave a box empty and it just follows the default. A company is a top-level entity — its branches all bill to it.',
    noCompanies: 'No companies yet leh — create a top-level entity first.',
    lockedNote: 'Changes kick in from this month’s bill onwards — earlier months keep their old terms, no need to worry.',
  },
  quotas: {
    help: 'Monthly caps on AI spending, counted the same way as the bill (same prices, markup and currency). At 80% the admins get a heads-up; at 100% the AI takes a break and chats go to a person until next month, or until someone raises the cap.',
    none: 'No caps set yet — AI usage got no limit.',
    invalid: 'Key in 0 or more, or just leave it empty for no cap.',
    entityHelp: 'Once this month’s AI usage hits the cap, the AI stops answering these chats and passes them to a person — until next month, or until you raise the cap. A company cap covers its branches also.',
    userHelp: 'Once this person’s AI usage hits the cap, Ask ChatBranch, the playground, skill drafting and summaries all stop for them — until next month, or until you raise it.',
  },
}
