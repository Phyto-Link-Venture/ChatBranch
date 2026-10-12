// Manglish overlay for the `chats` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const chats: Overlay<Messages['chats']> = {
  list: {
    modifiedHint: 'Filters changed already since you saved this view',
    unsavedChanges: '“{name}” got filter changes not saved yet',
    loading: 'Loading conversations, wait ah…',
    emptyFiltered: 'No conversations match these filters leh.',
    empty: 'No conversations yet — once customers message in, they’ll pop up here.',
    waitingDot: 'Customer still waiting for our reply',
    customerTime: 'When the customer last messaged in',
    noConversationYet: 'No conversation yet leh',
  },
  filterModal: {
    savedView: 'Saved “{name}” liao — you can find it as a tab above the list.',
    waitingHint: '(the customer was the last one to message)',
    noContactTags: 'No contact tags yet leh',
  },
  savedViews: {
    subtitle: 'Your own tabs above the chat list — only you can see them, nobody else.',
    empty: 'No saved views yet. Set some filters first, then just click {button} in the filter pop-up.',
    confirmDelete: 'Confirm want to delete the view “{name}”?',
  },
  bulk: {
    max1000: 'Bulk actions can only take up to 1000 conversations — narrow down the filter first lah.',
  },
  handover: {
    aiHandedOver: 'The AI passed this one to a person',
    yours: ' — yours now ah; once you reply, it’s closed.',
    letAiContinueHint: 'Close the handover and let the chatbot take over this chat again',
    willAnswer: 'Okay, the AI will reply the last message ya.',
  },
  identity: {
    asking: 'Unknown customer — asking them who they are first',
    askingN: 'Unknown customer — asking them who they are first (asked {count} of {max})',
    ambiguous: 'Unknown customer matches a few contacts — link the correct one lah',
    ambiguousSaid: 'Unknown customer ({said}) matches a few contacts — link the correct one lah',
    needsContact: 'Unknown customer — cannot find any matching contact',
    needsContactSaid: 'Unknown customer: {said} — cannot find any matching contact',
    gaveUp: 'Unknown customer never say who they are',
    gaveUpSaid: 'Unknown customer never say who they are ({said})',
  },
  company: {
    rowHint: '{name}’s conversation — each customer gets one conversation per company',
    pickTitle: 'Which company’s conversation ah?',
    pickHint:
      'This customer got conversations with more than one company. Pick the one you want to open — they’re kept separate one.',
  },
  draft: {
    title: 'AI reply waiting for you to review',
    couldNotUpdate: 'Cannot update the draft leh',
  },
  templatePicker: {
    subtitle: 'Approved templates can open back a closed 24h conversation window.',
    loading: 'Loading templates, hold on…',
    empty: 'This account got no templates yet — create one in Templates (live ones need Meta to approve first).',
    autoFilledHint: 'Filled in from the contact record — can edit if you need to',
  },
  parts: {
    betaHint: 'Not tested against the live provider yet',
    mediaLoading: '{kind} — downloading, wait ah…',
    player: {
      unsupported: 'This browser cannot play this audio format — download it first, then listen.',
      failed: 'Cannot play this audio leh — download it to listen.',
      videoFailed: 'This video won’t play — download it and watch can?',
    },
  },
  info: {
    contactNoteLinked: 'All the channels this contact uses come together in the “All channels” view above the chat.',
    contactNoteUnlinked:
      'Link it and this chat joins up with the same person’s other channels (WhatsApp links by phone automatically, email by address).',
    conversationNote: 'Once the customer messages again, a resolved or snoozed conversation opens back up.',
    sendingAccountNote:
      'Switch and the customer will see a brand-new chat on their side — free text only unlocks after they reply to the new number (use a template to open it).',
    emailTwoWay: 'Two-way email — sending goes through SMTP, and replies get pulled from the IMAP inbox about once a minute.',
    emailOutbound: 'Outbound-only — replies land in your mailbox, not here. Set the IMAP host in Channels to pull them in lah.',
    sandbox: 'Sandbox — nothing goes out for real.',
  },
  call: {
    ringingPhone: 'Ringing — go answer the phone',
    scriptCallHint: 'Script call — just reads out a fixed script, then hangs up',
    steerHuman: 'Type what you want the agent to say, word for word…',
    steer: 'Steer the AI (Guide) or say it word for word (Takeover)…',
    waitingAgent: 'Waiting for the agent to start talking…',
    connectingCall: 'Connecting the call, wait ah…',
    dialHint: 'Calls the contact’s real number and runs the AI voice agent — once connected, you can watch it live above.',
  },
  page: {
    pickConversation: 'Pick a conversation first',
    notFoundHint: 'This link goes to a conversation that doesn’t exist, or one you cannot see. Pick one from the list instead.',
    placeholdersLeft: 'This message still got placeholders not filled in — replace them first before sending.',
    placeholdersNoContact:
      'This message still got placeholders, and this chat has no linked contact to fill them — replace them yourself before sending.',
    pickHint:
      'One inbox for WhatsApp, Messenger, Instagram, Telegram, email and your website — use Filter to narrow things down, and save the filters you always use as a view.',
    optedOutHint: 'Customer already opted out of messaging — to send, you need a manual override',
    aiNeedsContactHint:
      'The AI only answers chats linked to a contact. Link this chat to one (or create a new one) in the details panel, then it’ll start replying.',
    basicHint: 'No AI key set up yet — so this is just a basic summary',
    mayBeOutdated: 'maybe outdated already · {ago}',
    generatingFirst: 'Making the first summary, hold on ah…',
    noSummary: 'No summary yet — just press refresh.',
    steerWa: 'Steer the AI (Guide) or type what it should say word for word (Takeover)…',
    noMessagesEmail: 'No messages yet — write the first email below.',
    noMessages: 'No messages yet — say hi below lah.',
    combinedReadOnly: 'This one combines every channel — to reply, pick a channel above first.',
    attachWindowClosed: 'Window closed already — media can only go out while the 24h window is open',
    attachSocialClosed: 'Reply window closed — wait for the customer to message again first',
    phNote: 'Write a private note — only agents see it, never sent out',
    phWindowClosed: '24h window closed — send a template to open it back',
    windowClosedNote: '24h window closed already — send a template to open the conversation back.',
    socialClosedNote:
      '{channel} only takes replies within 7 days of the customer’s last message — wait for them to message again lah.',
    socialLateNote:
      'Already outside {channel}’s 24h window — your reply will go out as a human-agent message; the AI and automations cannot send until the customer messages again.',
    platformClosedNote: '{channel}’s reply window closed liao — nothing can go out until the customer messages again.',
    unsupportedFile: 'Cannot send this file type — use jpg, png, webp, pdf, mp4, ogg or mp3.',
    fileTooLarge: 'File too big leh — attachments max 16 MB only.',
    confirmOptedOut: 'Customer opted out already — confirm want to send anyway as a manual override?',
    switchOtherContact:
      'Now chatting with this contact’s other contact — {channel} {address}. For the customer, this one is a separate chat with no shared history.',
    switchBaileysSeen:
      'Sender number switched — now going via {account} (personal number, no need template). This number chatted with the customer before already.',
    switchBaileysCold:
      'Sender number switched — now going via {account} (personal number, no need template). Messaging cold from this number can kena banned — safest is let the customer reply first.',
    switchSeen:
      'Sender number switched — now going via {account}. The customer knows this number; if they haven’t replied to it within 24h, open with an approved template first.',
    switchCold:
      'Sender number switched — now going via {account}. This number is new to the customer (they’ll see a new chat) — open with an approved template, or get them to reply first.',
  },
  aiControls: {
    unavailable: 'AI switch not available right now',
    subOff: 'No AI replies, follow-ups or AI flow messages at all in this scope.',
    subOn: 'The AI answers here — per-chat switches, contact settings and inbox defaults still count.',
    subSchedule: 'The AI only answers {window} ({tz}).',
    subScheduleNext: 'The AI only answers {window} ({tz}) — {next}.',
    followingOrg: 'Just following the organization-wide switch.',
    switchedOn: 'AI switched on for {scope} liao.',
    switchedOff: 'OK, AI switched off for {scope}.',
    followsParent: '{scope} is back to following its parent’s switch.',
    insideHint: 'Like an after-hours bot: the AI only answers inside these windows.',
    outsideHint: 'Like people during office hours: the AI only answers outside these windows.',
    approvedStillGoes: 'If someone approves a draft later, it still goes out.',
    noContactTags: 'No contact tags yet leh',
    saved: 'Saved liao.',
    needsPermission: 'To change these, you need the “Edit AI behaviour settings” permission.',
    editor: {
      badTime: 'Key in each time as HH:MM, between 00:00 and 23:59 — if a time cannot be read, the window cannot save.',
    },
    stats: {
      intro:
        'Conversations passed to a human by the AI — or a rule, a flow or a person — and how fast somebody took them up.',
      noHandovers: 'Steady — no handovers in this period.',
    },
  },
}
