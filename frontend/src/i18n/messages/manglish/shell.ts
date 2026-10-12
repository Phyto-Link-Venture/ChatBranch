// Manglish overlay for the `shell` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const shell: Overlay<Messages['shell']> = {
  guard: {
    title: 'Sorry ah, {page} is not for your account',
    body: 'Your role doesn’t have the permission this page needs ({caps}). Ask an admin to give it to you under Users & roles.',
  },
  language: {
    hint: 'The dashboard’s language. Saved to your profile, so it follows you to whatever device you use.',
    noOrgDefault: 'No organization default — each browser just uses its own language.',
    saved: 'Language saved liao.',
    orgSaved: 'Organization default updated — all settled.',
    reviewNote: 'Translations are machine-assisted — if any word sounds funny, tell us ya.',
  },
  install: {
    body: 'Add it to your home screen: it opens full screen like an app, faster, and got notifications too.',
    iosBody: 'Add it to your home screen — it opens full screen, just like an app.',
    iosStep1: 'First, tap the Share button in Safari’s toolbar',
    iosStep2: 'Scroll down a bit and tap “Add to Home Screen”',
    iosStep3: 'Tap “Add” — done, ChatBranch appears on your home screen',
  },
  entitySwitcher: {
    noMatch: 'No entity match leh.',
    hint: 'Click a name to show only that one, or tick a few to see them together. An entity always comes with its branches.',
    levelNotice: 'A few entities are ticked, so this page shows the organization level. Pick just one entity in the switcher to see or edit its own settings.',
    levelNoticeBound: 'A few entities are ticked, so this page shows {name}. Pick just one entity in the switcher to see or edit another one.',
  },
  palette: {
    oneMore: 'Type one more character to search records ah.',
    unavailable: 'Search not working right now leh — pages still match below.',
    noRecords: 'No records match “{term}” leh.',
    scopeNote: 'Results follow your own entity and permissions',
  },
  live: {
    connected: 'Live — connected liao',
    reconnecting: 'Live — reconnecting, wait ah',
    offline: 'Offline — updates on hold for now',
    noCalls: 'No calls going on now.',
    nobodyWaiting: 'Nobody waiting — every conversation got its answer liao.',
    nothingYet: 'Nothing yet leh.',
    footer: 'Updates on the spot',
  },
  bell: {
    empty: 'Quiet for now — customer replies and system events will pop up here.',
  },
  offline: {
    title: 'Alamak, you’re offline',
    body: 'ChatBranch needs the network to load this page. Check your connection — conversations still come in on the server, and they’ll be waiting for you when you’re back.',
  },
}
