import type { GuideDef } from './types'

/** How-to guides in the "work" group — words in i18n/messages/<locale>/manual/work.ts. */
export const workGuides: GuideDef[] = [
  { id: 'replyToChat', group: 'work', href: '/chats', tour: 'chats', steps: 8, tips: 3, trouble: true, who: 'everyone' },
  { id: 'findChats', group: 'work', href: '/chats', tour: 'chats', steps: 10, tips: 4, who: 'everyone' },
  { id: 'takeOverFromAi', group: 'work', href: '/chats', tour: 'chats', steps: 8, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'assignChat', group: 'work', href: '/chats', tour: 'chats', steps: 6, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'quickReplies', group: 'work', href: '/chats', steps: 8, tips: 3, before: true, who: 'everyone' },
  { id: 'sendTemplate', group: 'work', href: '/chats', steps: 8, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'addContact', group: 'work', href: '/contacts', tour: 'contacts', steps: 9, tips: 3, before: true, who: 'admin' },
  { id: 'importContacts', group: 'work', href: '/contacts', tour: 'contacts', steps: 9, tips: 3, before: true, trouble: true, who: 'admin' },
  { id: 'linkUnknownChat', group: 'work', href: '/chats', tour: 'chats', steps: 7, tips: 3, before: true, who: 'everyone' },
  { id: 'contactProfile', group: 'work', href: '/contacts', tour: 'contact', steps: 10, tips: 3, who: 'everyone' },
  { id: 'voiceCall', group: 'work', href: '/chats', steps: 8, tips: 3, before: true, trouble: true, who: 'everyone' },
  { id: 'bookingsDaily', group: 'work', href: '/bookings', steps: 7, tips: 3, before: true, who: 'everyone' },
  { id: 'bulkActions', group: 'work', href: '/chats', tour: 'chats', steps: 7, tips: 3, before: true, who: 'admin' },
]
