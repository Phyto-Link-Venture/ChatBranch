// Manglish overlay for the `ui` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const ui: Overlay<Messages['ui']> = {
  select: {
    noMatches: 'No match leh',
  },
  dataTable: {
    noResultsFor: 'Nothing found for “{query}” leh.',
  },
  bindingGrid: {
    noMatchesFor: 'Nothing matches “{query}” leh.',
    empty: 'Nothing to bind for now.',
    manageHint: 'Switch each one on or off, then apply. Nothing gets saved until you press Save on the page ah.',
  },
  saveBar: {
    unsaved: 'You got unsaved changes',
  },
  availability: {
    everyoneHint: 'Every entity and every branch can use it — open for all.',
    selectedHint: 'Only the entities you tick — and their branches — can use it.',
    adminOnly: 'You’re limited to certain entities only — to open it to everyone, need an admin lah.',
  },
  accountRole: {
    reminderHint: 'Reminder (business) — sends templates and passes contacts over to the conversation numbers',
    conversationHint: 'Conversation (personal) — AI chat, only when the customer messages first',
  },
}
