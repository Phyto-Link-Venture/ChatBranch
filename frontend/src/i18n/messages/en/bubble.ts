// English source for the `bubble` namespace (owner: work). Other locales mirror it key for key.
// One chat bubble's extras in /chats: quoted replies, emoji reactions, the
// message action bar (reply · react · copy), the composer's emoji picker and
// the location / contact-card / sticker renderings.
export const bubble = {
  actions: {
    aria: 'Message actions',
    reply: 'Reply',
    copy: 'Copy text',
    copied: 'Copied',
    react: 'React',
    moreEmoji: 'More emoji',
    close: 'Close',
  },
  reactions: {
    you: 'You',
    customer: 'Customer',
    by: '{names} reacted with {emoji}',
    removeYours: 'Click to remove your reaction',
    addSame: 'Click to react with {emoji}',
  },
  quote: {
    you: 'You',
    ai: 'AI',
    customer: 'Customer',
    missing: 'An earlier message',
    replyingTo: 'Replying to {who}',
    cancel: 'Cancel reply',
    jump: 'Show the original message',
    media: {
      image: 'Photo',
      video: 'Video',
      audio: 'Audio',
      document: 'Document',
    },
  },
  location: {
    title: 'Shared location',
    open: 'Open in Maps',
  },
  contactCard: {
    title: 'Contact card',
    add: 'Add as contact',
    adding: 'Adding…',
    added: 'Contact added',
    open: 'Open contact',
  },
  sticker: 'Sticker',
  emoji: {
    open: 'Emoji',
    search: 'Search emoji',
    recent: 'Recently used',
    noResults: 'No emoji found',
    close: 'Close',
    categories: {
      smileys: 'Smileys',
      people: 'People & hands',
      hearts: 'Hearts & symbols',
      nature: 'Animals & nature',
      food: 'Food & drink',
      activities: 'Activities & travel',
      objects: 'Objects',
    },
  },
} as const
