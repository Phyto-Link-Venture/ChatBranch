// English source for the `assistant` namespace (owner: channels-review): the
// Ask ChatBranch dock's chrome and history. The assistant's replies, step
// titles and action summaries are data. Other locales mirror it key for key.
export const assistant = {
  dock: {
    name: 'Ask ChatBranch',
    tagline: 'Acts as you · changes wait for your OK',
    sandbox: 'Sandbox',
    sandboxHint: 'No AI model is connected, so I understand simple requests and the product guide.',
    sandboxNoteAdmin:
      'Answering without an AI model — none is connected, or it didn’t respond. Add a provider key in {link}; the next message uses it.',
    sandboxNoteAsk:
      'Answering without an AI model — none is connected, or it didn’t respond. An admin connects one in Settings → AI keys & models.',
    aiKeysPage: 'Settings → AI keys & models',
    history: 'History',
    historySub: 'Your chats — only you can see them',
    backToChat: 'Back to the chat',
    newChat: 'New chat',
    close: 'Close assistant',
    opening: 'Opening your chat…',
    openFailed: 'Couldn’t open that chat — {error}',
    unreachable: 'I couldn’t reach the assistant — {error}',
    expiredAskAgain: 'This expired — ask again.',
    helpTitle: 'How can I help?',
    helpBody:
      'Ask how to do something, look up a contact or a conversation, or have me prepare a change — I’ll show it to you before anything happens.',
    pastChats: 'Your past chats',
    thinking: 'Thinking',
    placeholder: 'Ask anything about ChatBranch…',
    messageAria: 'Message the assistant',
    keys: 'Enter to send · Shift+Enter for a new line · Esc to close',
    hints: {
      waiting: 'How many chats are waiting for a reply?',
      findContact: 'Find contact…',
      activity: 'Show recent activity',
      connectWhatsapp: 'How do I connect WhatsApp?',
      analytics: 'Go to analytics',
    },
  },
  action: {
    needsConfirm: 'Needs your confirmation',
    details: 'Details',
    done: 'Done',
    failed: 'Didn’t happen',
    cancelled: 'Cancelled',
    expired: 'Expired',
  },
  history: {
    yourChats: 'Your chats',
    yourChatsCount: 'Your chats · {n}',
    clearAll: 'Clear all',
    loading: 'Loading your chats…',
    loadFailed: 'Couldn’t load your chats — {error}',
    deleteFailed: 'Couldn’t delete that chat — {error}',
    renameFailed: 'Couldn’t rename that chat — {error}',
    clearFailed: 'Couldn’t clear your chats — {error}',
    emptyTitle: 'No chats yet',
    emptyBody: 'Questions you ask are kept here, for you only, so you can pick a chat up again later.',
    titleAria: 'Chat title',
    saveTitle: 'Save title',
    cancelRename: 'Cancel rename',
    keep: 'Keep',
    rename: 'Rename',
    renameAria: 'Rename “{title}”',
    deleteAria: 'Delete “{title}”',
    older: 'Show older chats',
    clearTitle: 'Clear all chats?',
    clearBody_one:
      'This deletes your chat with Ask ChatBranch. It can’t be undone, and any change still waiting for your confirmation is cancelled. Other people’s chats aren’t affected.',
    clearBody_other:
      'This deletes all {count} of your chats with Ask ChatBranch. It can’t be undone, and any change still waiting for your confirmation is cancelled. Other people’s chats aren’t affected.',
  },
} as const
