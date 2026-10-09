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

const pattern = (re: RegExp, hint: string) => (v: string) => (re.test(v) ? null : hint)

const e164 = pattern(/^\+[1-9]\d{6,14}$/, 'An E.164 number with the country code, like +60123456789.')

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
    {
      key: 'app_secret', label: 'App secret', placeholder: 'From App settings → Basic', secret: true,
      optional: true,
      help: 'Meta App Dashboard → App settings → Basic → App secret. Inbound messages are only accepted when Meta signs them with it, so set it before you go live.',
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
    {
      key: 'app_secret', label: 'App secret', placeholder: 'From App settings → Basic', secret: true,
      optional: true,
      help: 'Meta App Dashboard → App settings → Basic → App secret. Inbound messages are only accepted when Meta signs them with it, so set it before you go live.',
    },
  ],
  line: [
    {
      key: 'access_token', label: 'Channel access token', placeholder: 'Long-lived token', secret: true,
      help: 'LINE Developers console → your provider → the Messaging API channel → Messaging API tab → Channel access token (long-lived) → Issue.',
    },
    {
      key: 'channel_secret', label: 'Channel secret', placeholder: '32 hex characters', secret: true,
      help: 'Same channel → Basic settings tab → Channel secret. Every webhook is checked against it, so inbound is refused until it is set.',
      check: pattern(/^[0-9a-f]{32}$/i, 'A LINE channel secret is 32 hexadecimal characters.'),
    },
  ],
  viber: [
    {
      key: 'access_token', label: 'Authentication token', placeholder: '4453b6ac12345678-…', secret: true,
      help: 'Viber Partners → your bot → Info → Token. It also signs every webhook Viber sends.',
    },
    {
      key: 'sender_name', label: 'Sender name', placeholder: 'Your shop', optional: true,
      help: 'The name your messages show in Viber (28 characters at most). Blank = the inbox name.',
      check: (v) => (v.length <= 28 ? null : 'Viber allows 28 characters at most.'),
    },
  ],
  sms: [
    {
      key: 'account_sid', label: 'Account SID', placeholder: 'AC…', inputMode: 'text',
      help: 'Twilio Console → Account info (home page) → Account SID.',
      check: pattern(/^AC[0-9a-f]{32}$/i, 'An Account SID is AC followed by 32 hexadecimal characters.'),
    },
    {
      key: 'access_token', label: 'Auth token', placeholder: 'From the Twilio Console', secret: true,
      help: 'Same page → Auth token. It also verifies every incoming-message webhook (X-Twilio-Signature).',
    },
    {
      key: 'from_number', label: 'From number', placeholder: '+60123456789', half: true, optional: true,
      help: 'A Twilio number with SMS — Phone numbers → Manage → Active numbers.', check: e164,
    },
    {
      key: 'messaging_service_sid', label: 'Messaging Service SID', placeholder: 'MG…', half: true, optional: true,
      help: 'Or send through a Messaging Service (sender pools, A2P 10DLC). Set this or a From number.',
      check: pattern(/^MG[0-9a-f]{32}$/i, 'A Messaging Service SID is MG followed by 32 hexadecimal characters.'),
    },
  ],
  wechat: [
    {
      key: 'app_id', label: 'AppID', placeholder: 'wx…', half: true,
      help: 'WeChat Official Account platform → Settings and development → Basic configuration → AppID.',
      check: pattern(/^wx[0-9A-Za-z]{6,32}$/, 'An AppID starts with wx.'),
    },
    {
      key: 'app_secret', label: 'AppSecret', placeholder: 'Reset to view', secret: true, half: true,
      help: 'Same page → AppSecret (reset it to see it once). Add this server’s IP to the IP whitelist there too.',
    },
    {
      key: 'token', label: 'Server token', placeholder: '3–32 letters and digits', secret: true,
      help: 'Basic configuration → Server configuration → Token: make one up and enter the same value in both places.',
      check: pattern(/^[A-Za-z0-9]{3,32}$/, 'The Token is 3–32 letters and digits.'),
    },
    {
      key: 'encoding_aes_key', label: 'EncodingAESKey', placeholder: '43 characters (safe mode)', secret: true,
      optional: true,
      help: 'Server configuration → EncodingAESKey. Needed for “Safe mode” (recommended) — messages are encrypted and signed.',
      check: pattern(/^[A-Za-z0-9]{43}$/, 'The EncodingAESKey is exactly 43 letters and digits.'),
    },
    {
      key: 'external_id', label: 'Original ID', placeholder: 'gh_…', optional: true,
      help: 'Account info → Original ID. Optional — it is learned from the first message.',
    },
  ],
  tiktok: [
    {
      key: 'external_id', label: 'Business ID', placeholder: '7000000000000000000',
      help: 'TikTok for Business → your business account. Shown once TikTok approves your app for Business Messaging.',
    },
    {
      key: 'access_token', label: 'Business access token', placeholder: 'act.…', secret: true,
      help: 'Issued when the business account authorises your app (TikTok API for Business → Authorization).',
    },
    {
      key: 'app_id', label: 'App ID', placeholder: 'Your app’s ID', optional: true, half: true,
      help: 'TikTok API for Business → My apps → your app.',
    },
    {
      key: 'app_secret', label: 'App secret', placeholder: 'Signs the webhooks', secret: true, half: true,
      help: 'Same app → Secret. Webhooks are only accepted when TikTok signs them with it.',
    },
  ],
  douyin: [
    {
      key: 'client_key', label: 'Client key', placeholder: 'aw…', half: true,
      help: '抖音开放平台 → 管理中心 → your app → Client Key.',
    },
    {
      key: 'client_secret', label: 'Client secret', placeholder: 'Signs the webhooks', secret: true, half: true,
      help: 'Same app → Client Secret. Every webhook is signed with it (X-Douyin-Signature).',
    },
    {
      key: 'external_id', label: 'Enterprise account open_id', placeholder: 'open_id of the 企业号',
      help: 'The open_id of the enterprise account (企业号) that authorised your app.',
    },
    {
      key: 'access_token', label: 'Access token', placeholder: 'act.…', secret: true,
      help: 'The enterprise account’s access_token from the OAuth authorisation (needs the 私信 scopes).',
    },
    {
      key: 'refresh_token', label: 'Refresh token', placeholder: 'rft.…', secret: true, optional: true,
      help: 'Kept for renewing the access token.',
    },
  ],
  zalo: [
    {
      key: 'app_id', label: 'App ID', placeholder: '1234567890123456789', inputMode: 'numeric', half: true,
      help: 'developers.zalo.me → your app → App ID.', check: digits('The App ID'),
    },
    {
      key: 'oa_secret_key', label: 'OA secret key', placeholder: 'Signs the webhooks', secret: true, half: true,
      help: 'Your app → Webhook → OA Secret Key. Every webhook is checked against it (X-ZEvent-Signature).',
    },
    {
      key: 'access_token', label: 'OA access token', placeholder: 'From the API explorer or OAuth', secret: true,
      help: 'Your app → Official Account → OA access token (it lasts about 25 hours — add the refresh token below).',
    },
    {
      key: 'refresh_token', label: 'Refresh token', placeholder: 'Renews the access token', secret: true,
      optional: true, half: true,
      help: 'Issued with the access token. With the app secret key, ChatBranch renews the token itself.',
    },
    {
      key: 'app_secret', label: 'App secret key', placeholder: 'For token renewal', secret: true, optional: true,
      half: true, help: 'Your app → Settings → App secret key.',
    },
    {
      key: 'external_id', label: 'OA ID', placeholder: 'Learned on Test', optional: true,
      help: 'Optional — filled in when you run Test.',
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
  line: [
    'Create a Messaging API channel for your LINE Official Account in the LINE Developers console.',
    'Copy the channel secret (Basic settings) and issue a long-lived channel access token (Messaging API).',
    'Turn off “Auto-reply messages” in the LINE Official Account Manager so replies come from here.',
    'After setup, register the webhook from the last step (or paste the URL and switch on “Use webhook”).',
  ],
  viber: [
    'Create a bot at partners.viber.com (a commercial Viber bot).',
    'Copy its authentication token.',
    'After setup, register the webhook from the last step — Viber checks it straight away.',
    'Invite links like viber://pa?chatURI=yourbot&context=CODE pass the code to the first message.',
  ],
  sms: [
    'Buy or port an SMS-capable number in the Twilio Console (or create a Messaging Service).',
    'Copy the Account SID and Auth token from the Console home page.',
    'After setup, paste the webhook URL into the number’s Messaging → “A message comes in” (HTTP POST).',
    'Twilio answers STOP / START itself — the conversation’s opt-out follows it here.',
  ],
  wechat: [
    'Use a verified Service Account (服务号) — customer-service messages need it.',
    'Basic configuration: copy the AppID, reset the AppSecret, and add this server’s IP to the whitelist.',
    'Server configuration: choose a Token (and EncodingAESKey for safe mode), paste the URL from the last step, then Enable.',
    'Parametric QR codes (qrscene_CODE) pass the code to the first message.',
  ],
  tiktok: [
    'Business Messaging is a gated TikTok API — apply in TikTok API for Business and wait for approval.',
    'Have the business account authorise your app to get its access token and Business ID.',
    'Paste the webhook URL from the last step in your app’s webhook settings.',
    'Replies are allowed within 48 hours of the customer’s last message.',
  ],
  douyin: [
    'Private messages need an approved enterprise account (企业号) and the 私信 permission on your app.',
    'Copy the Client Key / Client Secret, and authorise the enterprise account to get its open_id and access token.',
    'Paste the webhook URL from the last step in 开放平台 → 开发 → Webhooks (the challenge is answered automatically).',
    'Replies quote the customer’s latest message, within 48 hours.',
  ],
  zalo: [
    'Link your Official Account to an app at developers.zalo.me.',
    'Copy the App ID, the OA secret key (Webhook page) and an OA access token + refresh token.',
    'Paste the webhook URL from the last step and subscribe to the user_send_* and follow events.',
    'Consultation replies are allowed within 7 days of the user’s last interaction.',
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
  line: 'LINE Official Account',
  viber: 'Viber bot',
  sms: 'SMS',
  wechat: 'WeChat Official Account',
  tiktok: 'TikTok',
  douyin: 'Douyin',
  zalo: 'Zalo OA',
}

/** Channel-wide rules on top of the per-field checks (step 2). */
export const ONE_OF: Partial<Record<InboxKind, { keys: string[]; message: string }>> = {
  sms: { keys: ['from_number', 'messaging_service_sid'], message: 'Set a From number or a Messaging Service SID.' },
}

/** Where the webhook URL goes on each platform (the test step shows it with a copy button). */
export const WEBHOOK_HELP: Partial<Record<InboxKind, string>> = {
  line: 'LINE Developers → the channel → Messaging API → Webhook URL, then switch on “Use webhook”. Or register it from here.',
  viber: 'Viber needs the webhook set through its API — register it from here (it checks the URL straight away).',
  sms: 'Twilio Console → Phone numbers → your number → Messaging configuration → “A message comes in”: Webhook, HTTP POST.',
  wechat: 'Official Account platform → Basic configuration → Server configuration → URL, with the same Token (and EncodingAESKey for safe mode).',
  tiktok: 'TikTok API for Business → your app → Webhooks (Business Messaging events).',
  douyin: '抖音开放平台 → your app → Webhooks. The verify_webhook challenge is answered automatically.',
  zalo: 'developers.zalo.me → your app → Webhook → URL, and enable the user_send_* and follow / unfollow events.',
}
