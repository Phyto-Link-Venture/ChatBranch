// Manglish overlay for the `bubble` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const bubble: Overlay<Messages['bubble']> = {
  actions: {
    copied: 'Copied liao',
  },
  reactions: {
    removeYours: 'Click to take back your reaction',
    addSame: 'Click to react with {emoji} too',
  },
  quote: {
    jump: 'Bring me to the original message',
  },
  contactCard: {
    added: 'Done, contact added',
  },
  emoji: {
    noResults: 'No emoji found leh',
  },
}
