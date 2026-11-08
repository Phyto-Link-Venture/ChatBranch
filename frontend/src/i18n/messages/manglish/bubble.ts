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
  sendSticker: {
    help: 'Any picture also can — it gets cropped to a 512×512 sticker and sent without the bubble. GIF or short clip becomes animated one.',
    libraryEmpty: 'No pictures in the media library yet. Upload one above, or go add some at Library → Media.',
    wrongType: 'Sticker must come from a picture — PNG, JPG, WebP, GIF or MP4 only.',
    tooBig: 'That picture too big lah. Pick one under 10 MB.',
    readFailed: 'Cannot read that file.',
  },
}
