// Manglish overlay for the `assistant` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const assistant: Overlay<Messages['assistant']> = {
  dock: {
    tagline: 'Works as you · nothing changes until you say OK',
    sandboxHint: 'No AI model connected yet, so I can only handle simple requests and the product guide.',
    sandboxNoteAdmin:
      'Answering without an AI model — either none is connected, or it never respond. Add a provider key in {link}; your next message will use it.',
    sandboxNoteAsk:
      'Answering without an AI model — either none is connected, or it never respond. Ask an admin to connect one in Settings → AI keys & models.',
    historySub: 'Your chats — private, for your eyes only',
    float: 'Float — can move it anywhere',
    opening: 'Opening your chat, hold on…',
    openFailed: 'Cannot open that chat leh — {error}',
    unreachable: 'Aiyo, I cannot reach the assistant — {error}',
    expiredAskAgain: 'This one expired already — ask again can?',
    helpTitle: 'Yes boss, how can I help?',
    helpBody:
      'Ask me how to do something, look up a contact or a conversation, or let me prepare a change for you — I’ll show it to you first before anything happens.',
    thinking: 'Let me think ah',
    placeholder: 'Ask me anything about ChatBranch…',
  },
  history: {
    loading: 'Loading your chats, wait ah…',
    loadFailed: 'Cannot load your chats — {error}',
    deleteFailed: 'Cannot delete that chat — {error}',
    renameFailed: 'Cannot rename that chat — {error}',
    clearFailed: 'Cannot clear your chats leh — {error}',
    emptyTitle: 'No chats yet leh',
    emptyBody: 'Whatever you ask is kept here, only for you, so you can continue a chat again later.',
    clearTitle: 'Clear all chats ah?',
    clearBody_one:
      'This deletes your chat with Ask ChatBranch. Cannot undo, and any change still waiting for your confirmation will be cancelled. Other people’s chats won’t be affected.',
    clearBody_other:
      'This deletes all {count} of your chats with Ask ChatBranch. Cannot undo, and any change still waiting for your confirmation will be cancelled. Other people’s chats won’t be affected.',
  },
}
