import type { GuideDef } from './types'

/** How-to guides in the "start" group — words in i18n/messages/<locale>/manual/start.ts. */
export const startGuides: GuideDef[] = [
  { id: 'firstSignIn', group: 'start', href: '/dashboard', tour: 'analytics', steps: 9, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'findYourWay', group: 'start', href: '/dashboard', tour: 'shell', steps: 9, tips: 3, trouble: true, who: 'everyone' },
  { id: 'pickEntity', group: 'start', href: '/dashboard', tour: 'shell', steps: 8, tips: 3, trouble: true, who: 'everyone' },
  { id: 'languageAndLook', group: 'start', href: '/settings/appearance', tour: 'settings', steps: 9, tips: 3, trouble: true, who: 'everyone' },
  { id: 'myNotifications', group: 'start', href: '/notifications', steps: 9, tips: 3, trouble: true, who: 'everyone' },
  { id: 'askChatBranch', group: 'start', href: '/dashboard', tour: 'shell', steps: 9, tips: 3, trouble: true, who: 'everyone' },
  { id: 'myAccount', group: 'start', href: '/settings/account', tour: 'account', steps: 6, tips: 2, trouble: true, who: 'everyone' },
  { id: 'twoStepSignIn', group: 'start', href: '/settings/account', tour: 'account', steps: 6, tips: 2, before: true, trouble: true, who: 'everyone' },
  { id: 'myConnections', group: 'start', href: '/settings/connections', tour: 'settings', steps: 7, tips: 3, before: true, trouble: true, who: 'everyone' },
]
