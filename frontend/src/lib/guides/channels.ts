import type { GuideDef } from './types'

/** How-to guides in the "channels" group — words in i18n/messages/<locale>/manual/channels.ts. */
export const channelsGuides: GuideDef[] = [
  { id: 'connectFirstChannel', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'whatsappCloud', group: 'channels', href: '/channels', tour: 'channels', steps: 10, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'whatsappQr', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'websiteWidget', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'emailMailbox', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'telegramBot', group: 'channels', href: '/channels', tour: 'channels', steps: 8, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'messengerInstagram', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'otherChannels', group: 'channels', href: '/channels', tour: 'channels', steps: 8, tips: 4, before: true, trouble: true, who: 'admin' },
  { id: 'whoAnswers', group: 'channels', href: '/channels', tour: 'channels', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'channelHealth', group: 'channels', href: '/channels', tour: 'channels', steps: 8, tips: 3, trouble: true, who: 'admin' },
]
