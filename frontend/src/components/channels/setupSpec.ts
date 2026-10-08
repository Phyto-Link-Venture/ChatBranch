/**
 * Per-channel setup content for the Inboxes drawer: the credential fields of
 * step 2 (with "where do I find this?" help and inline validation) and the
 * short step-by-step guide shown under them. Pure data — the drawer renders it.
 */

import type { InboxKind } from './inboxes'

export interface FieldSpec {
  key: string
  label: string
  placeholder?: string
  /** Where to find the value — always shown under the field. */
  help: string
  secret?: boolean
  optional?: boolean
  inputMode?: 'numeric' | 'email' | 'url' | 'text'
  /** Format check; return a message, or null when fine. Only runs on non-empty values. */
  check?: (v: string) => string | null
  /** Narrow fields pair up two-per-row on wider screens. */
  half?: boolean
}

const digits = (what: string) => (v: string) => (/^\d{5,}$/.test(v) ? null : `${what} is a long number — digits only.`)

const email = (v: string) => (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v) ? null : 'That does not look like an email address.')

const host = (v: string) =>
  /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/i.test(v)
    ? null
    : 'Just the host name, e.g. smtp.gmail.com — no https:// or spaces.'

export const CRED_FIELDS: Partial<Record<InboxKind, FieldSpec[]>> = {
  wa_cloud: [
    {
      key: 'phone_number_id', label: 'Phone number ID', placeholder: '123456789012345', inputMode: 'numeric',
      help: 'Meta App Dashboard → WhatsApp → API Setup. The long ID under the phone number — not the number itself.',
      check: digits('The phone number ID'),
    },
    {
      key: 'waba_id', label: 'WhatsApp Business Account ID', placeholder: '102938475610293', inputMode: 'numeric',
      optional: true,
      help: 'Same API Setup page, above the number. Needed to list and send Meta-approved templates.',
      check: digits('The WABA ID'),
    },
    {
      key: 'access_token', label: 'Access token', placeholder: 'EAAG…', secret: true,
      help: 'Business Settings → Users → System users → Generate token. Dashboard tokens expire after 24 hours.',
    },
    {
      key: 'app_secret', label: 'App secret', placeholder: 'Enables webhook signature checks', secret: true,
      optional: true,
      help: 'App Dashboard → App settings → Basic → App secret. Required before you go live.',
    },
  ],
  email: [
    {
      key: 'from_email', label: 'From address', placeholder: 'support@yourco.com', inputMode: 'email',
      help: 'The address customers see and reply to.', check: email,
    },
    {
      key: 'host', label: 'SMTP host', placeholder: 'smtp.gmail.com', half: true,
      help: 'Your provider’s outgoing server — Gmail smtp.gmail.com, Outlook smtp.office365.com.', check: host,
    },
    {
      key: 'port', label: 'Port', placeholder: '587', inputMode: 'numeric', half: true, optional: true,
      help: '587 = STARTTLS (most providers), 465 = implicit TLS.',
      check: (v) => (/^\d+$/.test(v) && Number(v) > 0 && Number(v) < 65536 ? null : 'A port number, like 587.'),
    },
    {
      key: 'username', label: 'Username', placeholder: 'support@yourco.com',
      help: 'Usually the full email address — Gmail needs name@domain, not just the name.',
    },
    {
      key: 'password', label: 'Password', placeholder: 'App password', secret: true,
      help: 'An app password, not your login — Google Account → Security → App passwords.',
    },
    {
      key: 'imap_host', label: 'IMAP host (replies)', placeholder: 'imap.gmail.com', half: true, optional: true,
      help: 'Where customer replies are read from, with the same login. Blank = the default under Advanced.',
      check: host,
    },
    {
      key: 'imap_port', label: 'IMAP port', placeholder: '993', inputMode: 'numeric', half: true, optional: true,
      help: '993 = implicit TLS (most providers), 143 = STARTTLS.',
      check: (v) => (/^\d+$/.test(v) && Number(v) > 0 && Number(v) < 65536 ? null : 'A port number, like 993.'),
    },
  ],
  telegram: [
    {
      key: 'access_token', label: 'Bot token', placeholder: '123456789:AAH…', secret: true,
      help: 'In Telegram, message @BotFather → /newbot (or /token for an existing bot) and paste what it sends.',
      check: (v) => (/^\d+:[\w-]{10,}$/.test(v) ? null : 'A bot token looks like 123456789:AAH… (digits, colon, key).'),
    },
  ],
  messenger: [
    {
      key: 'external_id', label: 'Facebook Page ID', placeholder: '1029384756', inputMode: 'numeric',
      help: 'Your page → About → Page transparency, or Meta Business Suite → Settings → Page info.',
      check: digits('The Page ID'),
    },
    {
      key: 'access_token', label: 'Page access token', placeholder: 'EAAG…', secret: true,
      help: 'Meta App Dashboard → Messenger → Settings → Access tokens → Generate for this page.',
    },
  ],
  instagram: [
    {
      key: 'external_id', label: 'Instagram account ID', placeholder: '17841400000000000', inputMode: 'numeric',
      help: 'Meta App Dashboard → Instagram → API setup — the professional account linked to your page.',
      check: digits('The Instagram account ID'),
    },
    {
      key: 'access_token', label: 'Page access token', placeholder: 'EAAG…', secret: true,
      help: 'The token of the Facebook page the Instagram account is linked to.',
    },
  ],
  voice: [
    {
      key: 'sip_username', label: 'SIP username', placeholder: 'your Twilio SIP username',
      help: 'Twilio Console → Elastic SIP Trunking → your trunk → Termination → Credential lists.',
    },
    {
      key: 'sip_password', label: 'SIP password', placeholder: 'your Twilio SIP password', secret: true,
      help: 'The password of that credential list entry.',
    },
    {
      key: 'termination_uri', label: 'Termination URI', placeholder: 'your-trunk.pstn.twilio.com',
      help: 'Twilio Console → Elastic SIP Trunking → Termination → Termination SIP URI.', check: host,
    },
  ],
}

/** "Where do I find these?" — the short walk-through per channel. */
export const GUIDES: Partial<Record<InboxKind, string[]>> = {
  wa_cloud: [
    'Open developers.facebook.com → My Apps → your app → WhatsApp → API Setup.',
    'Copy the Phone number ID and the WhatsApp Business Account ID shown there.',
    'In Business Settings → System users, generate a permanent token with whatsapp_business_messaging.',
    'After setup, paste the callback URL and verify token from the last step into WhatsApp → Configuration.',
  ],
  email: [
    'Use a mailbox your team already reads (support@, hello@…).',
    'Find the SMTP server in your provider’s help pages (“SMTP settings”).',
    'Create an app password if the account has two-step sign-in.',
    'Replies from known contacts are pulled in over IMAP — this mailbox’s IMAP host, or the default under Advanced.',
  ],
  telegram: [
    'Open a chat with @BotFather in Telegram.',
    'Send /newbot, pick a name and a username ending in “bot”.',
    'Paste the token BotFather replies with.',
    'After setup, register the webhook from the last step.',
  ],
  messenger: [
    'Your Facebook page must be connected to a Meta app with the Messenger product.',
    'Generate a page access token in Messenger → Settings.',
    'Subscribe the app to the page’s messages webhook (URL in the last step).',
  ],
  instagram: [
    'The Instagram account must be a professional account linked to a Facebook page.',
    'Add the Instagram product to your Meta app and find the account ID under API setup.',
    'Use the linked page’s access token; subscribe to messages (URL in the last step).',
  ],
  voice: [
    'Create an Elastic SIP trunk in the Twilio Console.',
    'Add a credential list under Termination and note the username and password.',
    'Copy the termination SIP URI.',
    'Run `npm --prefix backend/voice_agent run setup-trunk` once and put the printed SIP_OUTBOUND_TRUNK_ID in backend/.env (with LIVEKIT_URL / LIVEKIT_API_KEY / LIVEKIT_API_SECRET); start the worker with `npm run dev:agent`.',
  ],
}

/** A sensible starting name for a new inbox (editable in step 3). */
export const DEFAULT_NAME: Record<InboxKind, string> = {
  wa_cloud: 'WhatsApp Business',
  wa_qr: 'WhatsApp (QR)',
  email: 'Support mailbox',
  telegram: 'Telegram bot',
  messenger: 'Facebook page',
  instagram: 'Instagram',
  website: 'Website chat',
  voice: 'Voice trunk',
}
