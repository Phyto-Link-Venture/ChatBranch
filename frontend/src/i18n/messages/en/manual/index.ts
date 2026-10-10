import { start } from './start'
import { channels } from './channels'
import { work } from './work'
import { ai } from './ai'
import { automate } from './automate'
import { admin } from './admin'
import { faq, glossary } from './help'

/** The user manual's how-to guides, troubleshooting and glossary (lib/guides). */
export const manual = {
  ui: {
    searchPlaceholder: 'Search the manual — e.g. “WhatsApp”, “turn the AI off”',
    noResults: 'Nothing in the manual matches “{q}”.',
    sections: {
      quickStart: 'Your first day',
      guides: 'Step-by-step guides',
      tours: 'Tour of every page',
      faq: 'If something goes wrong',
      glossary: 'Words we use',
    },
    quickStartIntro: 'New to ChatBranch? Do these in order — about an hour from an empty account to an AI that answers your customers.',
    guidesIntro: 'Each guide walks one job from start to finish. Follow the numbered steps exactly; nothing here needs technical knowledge.',
    toursIntro: 'What every page shows and what each part does. Inside ChatBranch, press the ? button in the top bar to see it on the real page.',
    faqIntro: 'The common problems and exactly what to check.',
    glossaryIntro: 'The ChatBranch words, in plain language.',
    groups: {
      start: 'Getting started',
      channels: 'Connect your channels',
      work: 'Everyday work',
      ai: 'Set up the AI',
      automate: 'Automate',
      admin: 'Team & admin',
    },
    beforeYouStart: 'Before you start',
    steps: 'Steps',
    done: 'How you know it worked',
    tips: 'Tips',
    trouble: 'If something goes wrong',
    openPage: 'Open this page',
    showMe: 'Show me on the page',
    backToTop: 'Back to the top',
    minutes_one: 'About {count} minute',
    minutes_other: 'About {count} minutes',
    who: {
      everyone: 'Anyone on the team',
      admin: 'Admins',
      superadmin: 'The owner (superadmin)',
    },
    forThisPage: 'How-to guides for this page',
    allGuides: 'All step-by-step guides',
  },
  guides: {
    ...start,
    ...channels,
    ...work,
    ...ai,
    ...automate,
    ...admin,
  },
  faq,
  glossary,
}
