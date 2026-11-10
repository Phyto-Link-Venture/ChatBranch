// Manglish overlay for the `readySkills` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const readySkills: Overlay<Messages['readySkills']> = {
  gallery: {
    intro: 'Real capabilities you just switch on — no code needed. Add one, fill in a few details, settle.',
    addedNotice: '“{name}” added liao — check its settings, then can use already.',
    templatesIntro: 'Starting points for how the AI should behave. Add one, then tweak the words to suit your business.',
    builtinNote: 'Always on one: the AI also got a calculator (exact sums, tax and discounts) and date tools.',
    codeLimit: 'Code skills run in a sandbox — no internet, no files. Want a PDF, QR code, payment link or sheet row? Use these ready-made apps instead.',
  },
  apps: {
    documents: {
      blurb: 'The AI makes PDF invoices, quotations, receipts and letters on your letterhead and sends the file to the customer. The server does all the sums and numbers each document for you.',
    },
    qr: {
      blurb: 'The AI turns a link or short text (a payment link, a location, a voucher code) into a QR code picture and sends it with its reply.',
    },
    payments: {
      blurb: 'The AI makes a secure Stripe payment link for the amount you agreed, so the customer can pay online straight away.',
    },
    sheets: {
      blurb: 'The AI logs a row — a lead, an order, a request — into a Google Sheet once its reply goes out.',
    },
  },
  playground: {
    wouldSend: 'Would send {label} after the reply (just a preview — nothing kept)',
  },
  form: {
    previewing: 'Working on it, wait ah…',
    previewOpened: 'Opened in a new tab liao',
    taxHint: '0 = no tax line. The tax goes on top of the subtotal — the server will do the maths for you.',
    logoHint: 'Max 1 MB. It gets printed at the top left.',
    numberingHint: 'Each type counts up on its own. If a reply never goes out, its number is voided — so no secret gaps in between.',
    cjkMissing: 'This server cannot print Chinese characters (no CJK font installed) — so the AI is told to write documents in English or Malay.',
    sheetColumnsHint: 'Time, the customer’s name, phone and email go in front automatically — no need to add them.',
    connectAt: 'Connect {provider} at Apps first — until then the AI will say it cannot do this.',
    problems: {
      currency: 'The currency must be a 3-letter code, like MYR.',
      taxRate: 'Tax rate must be between 0 and 100 ah.',
      pickType: 'Pick at least one document type lah.',
      successUrl: 'The page after paying must start with https://, okay?',
      maxAmount: 'The largest amount cannot be less than 1.',
      columns: 'Name at least one column first.',
    },
  },
  designer: {
    subtitle: 'How your invoices, quotations, receipts and letters look. Nothing is saved until you apply and save the skill, so can play around.',
    lookHint: 'Pick a style, upload a PDF you already use, or just ask AI to change it.',
    uploadHint: 'A PDF, PNG or JPEG of an invoice or letterhead you already use — we copy its colours, layout and fixed words into the template.',
    analyzing: 'Reading your design, wait ah…',
    analyzedAi: 'The AI has read your design liao.',
    analyzedHeuristic: 'Read without an AI model — better check the colours yourself.',
    filledIn: 'Filled in from your design already: {fields}.',
    askSandbox: '(No AI model set up yet — the built-in rules made this change for you.)',
    editingTypeNote: 'Changes here only apply to the {type}. Page size, margins and the background are still shared by all.',
    sharedOnly: 'Every document type shares this one — switch “Editing” to All documents if you want to change it.',
    sectionOrderHint: 'The order of the blocks under the header. Letters only use the info block and their own text.',
    accentHint: 'Leave empty = use the accent picked on the skill.',
    hexInvalid: 'Must be a hex colour, like #1E3A8A.',
    extraLinesHint: 'One per line, max 6 (opening hours, another branch…).',
    billToHint: 'Leave empty = BILL TO / RECEIVED FROM / TO in the document’s language.',
    discountHint: 'The discount column only shows up when a line got discount.',
    footerTextHint: 'Leave empty = use the footer note set on the skill.',
    letterheadIntro: 'A PDF whose first page gets printed under every page — your own letterhead, pixel for pixel. Use a blank letterhead (no sample invoice on it) and set the margins so got space for the content.',
    previewing: 'Hang on, making the preview…',
    previewFailed: 'Aiyo, cannot make the preview.',
    previewIosHint: 'On iPhone or iPad the preview here may show one page only — open it to see the whole PDF.',
  },
}
