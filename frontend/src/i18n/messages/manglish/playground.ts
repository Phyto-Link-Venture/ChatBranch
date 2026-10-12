// Manglish overlay for the `playground` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const playground: Overlay<Messages['playground']> = {
  subtitle: 'Chat with the AI like a customer, see why it answered like that, try changes — nothing gets sent, stored or booked.',
  safe: 'Test only — no customer sees any of this',
  actions: {
    resetConfirm: 'Clear the chat ah? Your setup stays.',
  },
  sandbox: {
    noteAdmin: 'No AI model connected yet, so the replies are just the sandbox echo. Add a provider key in {link} to try the real one.',
    noteAsk: 'No AI model connected yet, so the replies are just the sandbox echo. Ask an admin to connect one in Settings → AI keys & models.',
  },
  setup: {
    outletHint: 'Its knowledge, skills, persona and review policy all apply.',
    noContacts: 'No contacts match leh.',
    contactsUnavailable: 'You cannot read contacts here — just use a made-up customer lah.',
    realHint: 'Copies its latest messages into this chat. The real one won’t change one bit.',
  },
  changes: {
    hint: 'Only for this playground — nothing gets saved.',
    personaUnsaved: 'Not saved yet — only this playground uses it. The safety rules always stay on top one.',
    personaSavedHint: 'This is the saved persona — type here to try a change, then hit “Save as persona”. House rules you edit on the Persona & rules page.',
    personaSavedOk: 'Saved liao — every conversation for this outlet now uses this persona.',
    touchesHint: 'Only for this run — the saved settings are in AI monitor → Settings → Human-like.',
    adminOnly: 'Want to try another model, a persona draft or switch parts off? Need the “Change AI behaviour” permission first.',
  },
  chat: {
    empty: 'Type something as the customer to start — or start from a real conversation also can.',
    writing: 'The AI is typing, wait ah…',
    editHint: 'Everything after this message will be thrown away.',
    noText: 'The AI wrote nothing leh.',
    reactionOnly: 'No text — the AI would just react {emoji} only.',
    wouldReact: 'Would react {emoji} (not sent lah — this is the playground)',
    wouldQuote: 'Would quote this message (not sent — playground only)',
    failed: 'Aiyo, no reply: {error}',
    interrupted: 'The page closed before the reply could come back.',
    limit: 'Max {count} messages only — reset the chat to carry on.',
  },
  verdict: {
    shadow: 'Shadow mode — recorded only, never sent out',
  },
  inspector: {
    empty: 'Pick an AI reply to see why it answered like that.',
    actions: {
      handover: 'Its checks said need a person',
    },
    noGrounding: 'Didn’t use anything from the knowledge base or the learned lessons.',
    noTools: 'No tools called this time.',
    wouldBook: 'Would book {label} ({timezone}) — but nothing actually booked.',
    wouldCancel: 'Would cancel {label} ({timezone}) — but nothing actually cancelled.',
    noChecks: 'No reply checks ran this round.',
    providerError: 'The provider failed ({error}) — so the sandbox answered instead.',
    promptHint: 'Switch on “Return the full prompt” under Try changes first, then regenerate.',
  },
  teach: {
    intro: 'This gets saved as a lesson for {outlet}. The learning mode decides whether it goes live straight away or not.',
    savedReview: 'Saved liao — now waiting for review in {link}.',
    savedLive: 'Saved — the AI will use it starting from its next reply.',
    learningOff: 'Learning is off for this outlet, so the lesson will wait for review first.',
  },
  errors: {
    options: 'Aiyo, the playground cannot load: {error}',
    import: 'Cannot copy that conversation leh: {error}',
    teach: 'Got problem saving the lesson: {error}',
    noConversations: 'Nothing found — no conversations match.',
  },
  import: {
    note: 'Its contact, outlet, channel and AI switch all come along. The real conversation won’t change.',
  },
}
