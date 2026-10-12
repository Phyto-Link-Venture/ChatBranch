// Manglish overlay for the `docLayout` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const docLayout: Overlay<Messages['docLayout']> = {
  mode: {
    designerHint: 'Pick a look, then tweak it with simple settings.',
    htmlHint: 'Want it to look exactly like your own document? Use your own HTML template, with variables for the parts that change.',
  },
  look: {
    customHint_one: 'Printed using your own HTML template, plus {count} custom field.',
    customHint_other: 'Printed using your own HTML template, plus {count} custom fields.',
  },
  renderer: {
    missing:
      'This server cannot print custom layouts yet (no HTML renderer installed). You can still edit and preview the layout, but documents will kena refused until Chromium is installed — or just switch back to the Designer.',
  },
  start: {
    hint: 'Pick a ready-made layout and it replaces your HTML and fields (don’t worry, Undo brings yours back).',
    starters: {
      cleanModern: { description: 'Nice and airy, in your accent colour, with a totals card.' },
    },
  },
  upload: {
    busy: 'Reading your design, wait ah…',
    hint: 'Upload a PDF or a picture of the document you’re using now. The AI rebuilds it as a template; if no AI model, you get the closest ready-made layout, filled in with what the PDF says.',
    doneAi: 'Done — your design is now a template.',
    doneStarter: 'Okay, started you off from a ready-made layout.',
    filledIn: 'Took these from your design: {fields}.',
  },
  ask: {
    sandbox: 'No AI model set up yet, so the layout stays the same.',
  },
  editor: {
    checking: 'Checking, hold on…',
  },
  fields: {
    intro: 'Extra values this layout prints. The AI will ask the customer for them when needed, and the create form in a chat also shows them.',
    none: 'No custom fields yet leh.',
    max: 'Max 20 fields only.',
    problems: {
      key: 'Field {n}: the key must start with a small letter and only use a–z, 0–9 and _.',
      reserved: 'Field {n}: “{key}” is a built-in name already — pick another one lah.',
      duplicate: 'Field {n}: “{key}” got used twice.',
    },
  },
  words: {
    termsHint: 'The layout will list them out, e.g. as a numbered NOTE list.',
    bankHint: 'Printed wherever the layout puts the bank details (e.g. the last NOTE).',
    marginsHint: 'Only used if the template got no @page rule of its own.',
    letterheadHint: 'Upload a PDF and its first page gets printed under your layout.',
  },
  reference: {
    intro: 'Click anything and it goes in at the cursor. Values are printed safely (escaped); only the logo goes in raw.',
    helperHelp: {
      money: 'An amount like 13,256.00 — can put a currency word in front also.',
      inc: 'A number plus one — handy for row numbers starting from 1.',
      eq: 'Only show this part when two values are the same.',
      ne: 'Only show this part when two values are different.',
      gt: 'Only show this part when a number is bigger.',
      nl2br: 'Keeps the text’s line breaks as they are.',
    },
  },
  preview: {
    pdfBusy: 'Printing, hold on…',
    failed: 'Aiyo, the preview didn’t work.',
    empty: 'Start from a ready-made layout, or upload your own design lah.',
  },
  chat: {
    none: 'No documents in this conversation yet leh.',
    loadFailed: 'Aiyo, cannot load the documents.',
    noSkill: 'Add the Documents app on the Skills page first, then only can make invoices and quotations here.',
  },
  create: {
    subtitle: 'Made with this conversation’s Documents skill — the server does all the sums and numbering for you.',
    customerHint: 'Leave empty to use the contact’s name.',
    letterTextHint: 'Leave one blank line to start a new paragraph.',
    creating: 'Making it now…',
    created: '{label} {number} created liao.',
    sent: '{label} {number} sent to the customer already.',
    rendererMissing: 'This skill uses a custom layout, but this server cannot print it yet — ask an admin to install the HTML renderer.',
    problems: {
      items: 'Add at least one item with a description and unit price first.',
      letter: 'A letter needs a subject and some text lah.',
      required: '“{field}” must fill in ah.',
    },
  },
}
