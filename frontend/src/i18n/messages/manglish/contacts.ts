// Manglish overlay for the `contacts` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const contacts: Overlay<Messages['contacts']> = {
  form: {
    subtitleEdit: 'Update this contact’s details here.',
    subtitleNew: 'Add someone to this entity’s contact list.',
    phoneOrEmail: 'phone or email — must have at least one',
    addToExisting: 'This phone or email already belongs to {name} — add {entity} to that contact instead?',
    customFieldsHint: 'Set up in Settings → Custom fields. The AI reads these when it talks to this contact.',
    entitiesHint: 'Tick which entities this contact belongs to — the same profile gets linked to each one.',
  },
  list: {
    confirmDelete: 'Delete contact “{name}” ah? Their conversations stay, but the record will be removed.',
    deleted: 'Contact {name} deleted liao.',
    emptyFiltered: 'No contacts match these filters leh.',
    emptyIn: 'Nothing in {entity} yet — add a contact, import a CSV, or switch the entity filter to All entities.',
    empty: 'No contacts yet — add one or import a CSV lah.',
  },
  detail: {
    notFound: 'This contact doesn’t exist, or it’s not in your scope.',
    loading: 'Loading contact, wait ah…',
    noSummary:
      'No summary yet — generate one and every call, chat and email with this contact gets squeezed into a short brief.',
    commsHint: 'Every call, chat and email with this contact, from all channels — newest on top.',
    nothingYet: 'Nothing here yet. Voice calls, chats and emails with {name} will show up here as they happen.',
  },
  import: {
    subtitle: 'Bring in a contact book you already got.',
    modes: {
      skipBody: 'Contacts you already have stay exactly as they are, no change.',
      updateEmptyBody: 'Keep what you have, just add the details that are missing (email, company…).',
      overwriteBody: 'The file wins: its values replace the ones you have now.',
    },
    tooLarge: 'That file is bigger than 10 MB — split it into smaller files lah.',
    reading: 'Reading the file, wait ah…',
    fetchingGoogle: 'Getting your Google contacts…',
    checking: 'Checking for duplicates…',
    uploadHint:
      'Excel (.xlsx), CSV, or a vCard (.vcf) exported from an iPhone or Android phone — all can. Up to 20,000 contacts / 10 MB.',
    connectGoogle: 'Need to connect Google first — open {link}, allow access to your contacts, then come back here.',
    rowsFound_one: '{count} row found. Check what each column holds — we only guessed from the headings.',
    rowsFound_other: '{count} rows found. Check what each column holds — we only guessed from the headings.',
    needReach: 'Map at least one phone or email column — must have some way to reach the contact.',
    noName: 'No name column — so contacts will be named after their phone or email.',
    matchedBy: 'We match them by phone (last 8 digits) or email.',
    willSkip_one: '{count} will be left alone.',
    willSkip_other: '{count} will be left alone.',
    willUpdate_one: '{count} will get updated.',
    willUpdate_other: '{count} will get updated.',
    willUpdateMatch_one: '{count} will get updated, {unchanged} already the same.',
    willUpdateMatch_other: '{count} will get updated, {unchanged} already the same.',
    findChannelsHint:
      'Register WhatsApp, voice and email for each contact; WhatsApp numbers get checked in the background through a connected QR number.',
    nothingToImport: 'Nothing to import — every row is already in your contacts with the same details.',
    importingRows_one: 'Importing {count} row, wait ah…',
    importingRows_other: 'Importing {count} rows, wait ah…',
    queuedChecks_one: '{count} WhatsApp number in the queue for checking.',
    queuedChecks_other: '{count} WhatsApp numbers in the queue for checking.',
    issueRows_one: '{count} row cannot be imported',
    issueRows_other: '{count} rows cannot be imported',
  },
  invite: {
    subtitle: 'Links that join {name}’s other apps to this contact. One code, 30 days, once per app.',
    copyFailed: 'Copy failed leh — select the text and copy it by hand.',
    intro:
      'Messenger, Instagram, Telegram and most chat apps don’t let anybody look up a customer by phone number. An invite link works the other way round: the customer opens it, says hi, and that conversation joins this contact automatically.',
    noAccountHint: 'An app with no connected account still gets a code — the customer can type it into their first message.',
    sendHint: 'Opens the chat with the invite in the composer — check it first, then send.',
  },
  createFromChat: {
    subtitle: 'The conversation gets linked to the new contact straight away.',
    needName: 'Give the contact a name first lah.',
  },
  tagsCard: {
    noneYet: 'No contact tags yet — {link}.',
    saved: 'Tags saved liao.',
  },
  aiMode: {
    followHint: 'The chat’s AI switch and the inbox default will decide.',
    neverHint: 'The AI won’t ever answer this person (VIPs, complaints).',
    alwaysHint: 'The AI answers even if the inbox default is off.',
    neverTag: 'Tagged “{tag}”, which is on the never-AI list — so the AI stays off for this person. {link}',
  },
  entities: {
    none: 'Not linked to any entity yet.',
    modalSubtitle: 'A contact on a sub-entity can be seen by its main entity. Unlinking won’t delete the contact, don’t worry.',
  },
  hub: {
    cloudNote:
      'The WhatsApp Cloud API cannot look up numbers — only a connected QR (WhatsApp Web) number can. Nobody ever gets a message just to test a number.',
    addHint:
      'WhatsApp numbers and emails can message straight away. Telegram / Messenger / Instagram IDs come from the platform — the customer must have messaged the bot or page at least once.',
    nothingNew: 'Nothing new leh',
    unverifiedNote: 'WhatsApp numbers stay unverified until you connect a QR number',
    loadFailed: 'Cannot load the channel info.',
    loading: 'Loading channels, hold on ah…',
    noChannels: 'Got no channels on record yet.',
    noLookupNote: '{apps} cannot look up by phone — send an invite link instead lah.',
  },
  campaign: {
    noneSelected: 'No contacts selected yet',
  },
  access: {
    subtitle: 'Requests to share one branch’s customer with another branch.',
    offer: 'This customer is already in another branch of {company}. Want to ask an admin to share them with {entity}?',
    sent: 'Request sent — an admin of {company} will review it. You’ll get a notification once they decide.',
    empty: 'No access requests waiting.',
    hidden: 'Cannot see the customer’s details yet — only after an admin approves.',
    approved: 'Settled — {name} is now shared with {entity}.',
    denied: 'Okay, request denied.',
    cancelled: 'Request cancelled already.',
  },
}
