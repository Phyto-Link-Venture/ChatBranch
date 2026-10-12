// Manglish overlay for the `notifications` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const notifications: Overlay<Messages['notifications']> = {
  page: {
    subtitle: 'Everything that needs your attention — filter, open, then tidy up lah.',
  },
  categories: {
    messagesHint: 'A customer messaged in, on any chat channel',
    accountHealthHint: 'An account went down or got degraded, plus routing failover and recovery prompts',
    systemHint: 'Everything else the platform wants you to know about',
  },
  inbox: {
    noMatchTitle: 'Nothing matches leh',
    noMatchBody: 'No notifications match these filters. Clear them and you’ll see everything.',
    emptyTitle: 'All quiet for now',
    emptyBody: 'Customer messages, channel alerts and flow handovers will all come in here.',
    everything: 'That’s all liao.',
    deleteReadTitle: 'Delete all the read notifications ah?',
    deleteSelectedTitle_one: 'Delete {count} notification ah?',
    deleteSelectedTitle_other: 'Delete {count} notifications ah?',
    deleteSubtitle:
      'Once deleted, the notifications are gone for good. The conversations and records they point to won’t be touched.',
    deleteReadBody: 'Every notification you already read will be cleared from your inbox. The unread ones stay.',
    deleteSelectedBody: 'The ones you selected will be cleared from your inbox.',
  },
}
