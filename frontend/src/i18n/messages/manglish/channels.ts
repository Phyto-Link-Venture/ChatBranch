// Manglish overlay for the `channels` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const channels: Overlay<Messages['channels']> = {
  page: {
    subtitle: 'All the numbers, mailboxes and widgets your team answers from — in one place.',
    loadFailed: 'Cannot load {sources} — is the API running or not? The rest is shown below.',
    loading: 'Loading inboxes, wait ah…',
    emptyTitle: 'No inboxes yet leh',
    emptyBody:
      'Pick a channel below to connect your first number, mailbox or website chat. Until then everything runs in sandbox — messages are kept locally and nothing goes out.',
    noneOfKind: 'No {channel} inboxes here yet.',
    addHint: 'Pick a block — the setup will guide you step by step.',
    addNeedsPermission: 'Sorry ah, you need the “Manage channel accounts” permission to add inboxes.',
    qrWarning: 'Unofficial — this one breaks WhatsApp’s terms. Use a spare number, OK?',
    voiceConnected: 'Already connected — this opens its settings',
    removed: '{name} removed liao.',
    deleteTitle: 'Confirm delete {name}?',
    deleteTitleGeneric: 'Confirm delete this inbox?',
    deleteVoice: 'This clears the SIP credentials saved here. Whatever is in backend/.env still stays in force.',
    deleteWebsite: 'The embed on your site will stop working, but its conversations stay in the inbox.',
    deleteQr: 'The phone gets unpaired and the inbox removed. Its conversations still stay — back in sandbox.',
    deleteDefault: 'The inbox goes away, but its conversations stay — back in sandbox.',
  },
  chips: {
    approvalTitle: 'The platform must approve your app first, then only its messaging API works',
    demoTitle: 'Unofficial transport — for demos only, don’t go live with it',
    betaTitle: 'Not tested against the live provider yet',
  },
  test: {
    paired: 'Paired and connected, steady.',
    pairedAs: 'Paired and connected, steady · {number}.',
    notPaired: 'Not connected yet ({state}) — scan the QR to pair it.',
    credentials: 'Credentials for {name} all there.',
    credentialsFor: 'Credentials for {name} all there · {number}.',
    accountState: 'Account is {state} — better check the token.',
    widgetSilent: 'The widget never answer (HTTP {status}).',
    widgetAnswers: 'Widget is answering — it opens as “{title}”.',
    sipSet: 'SIP trunk creds all set liao.',
    connected: 'Connected — all good.',
    connectedAs: 'Connected as {name} — all good.',
  },
  drawer: {
    required: '{field} cannot be empty.',
    ackRequired: 'Tick the box first to confirm you understand the risk.',
    nameRequired: 'Give the inbox a name your team can easily recognise.',
    chooseFirst: 'Choose a channel first lah.',
    trunkNotSaved: 'Alamak, the trunk never save — check the fields.',
    connectedNotice: '{name} saved liao — run the test to see if it connects.',
    changesSaved: 'Changes all saved.',
    testEmailSent: 'Test email sent to {to} — go check the inbox.',
    httpsRequired: 'Must use the public https:// address that reaches this API (e.g. your tunnel URL).',
    webhookRegistered: 'Webhook registered, settled.',
    webhookRegisteredAt: 'Webhook all registered: {url}',
    rotateConfirm: 'Confirm rotate the widget token? Any page still using the old snippet will stop working until you update it.',
    tokenRotated: 'Token rotated — now update the snippet on your site.',
    keepCurrent: 'Leave blank and the current one stays',
    chooseIntro: 'So, which channel should this inbox answer?',
    qr: {
      warning:
        '{b} WhatsApp by QR uses the unofficial WhatsApp Web protocol. No need Meta approval, but it breaks WhatsApp’s terms of service and the number can kena banned. Use a spare number — don’t ever use your main business line.',
      needsGateway: 'The WhatsApp gateway must be running next to the API ({cmd}).',
      textAndCalls: 'Can do text messages and voice calls; Meta templates still stay on WhatsApp Business numbers.',
      noWindow: 'No 24-hour window — but no Meta compliance also.',
    },
    websiteIntro:
      'A chat bubble for your site — just one script tag, no build step needed. Visitor chats land in the same inbox as WhatsApp and email, with the same AI, teams and automations.',
    approvalNote:
      '{b} {platform} only opens its messaging API to approved apps — you only get the credentials below after the platform approves yours. Until then, the inbox stays in sandbox.',
    voiceLocked: 'The super admin manages the voice trunk credentials — you can still look through them here.',
    useTlsHint: 'Just leave it on unless your provider says otherwise.',
    who: {
      voiceIntro:
        'The AI voice agent answers calls on the SIP trunk. Who to dial — and with which voice and script — is decided per call, from a conversation, a campaign or a flow step.',
      voiceNothing: 'Nothing to set here lah. Continue to check the configuration.',
      nameHelp: 'This is what your team sees in the inbox and on reports.',
      entityHelp:
        'The entity that owns this inbox — conversations coming in here get filed under it. Organization-wide inboxes are the fallback every entity shares.',
      noTeams: 'No teams yet leh — create some under Teams to route this inbox’s conversations to a group.',
      teamHelp: 'New conversations here go to this team first, taking turns among its members (round-robin).',
      roleReminderHelp:
        'Sends approved templates straight away, then passes contacts to a conversation number. Chatbot is off by default.',
      roleConversationHelp:
        'This one handles the AI conversations that customers start. Sends go slow-slow like a human, to protect the number.',
      aiHelpDefault:
        'Whether the assistant answers new conversations here or not — “Default” means on. Agents can still switch it per chat, and their choice always wins.',
      aiHelpReminder:
        'Whether the assistant answers new conversations here or not — “By role” means off on reminder numbers (unless the routing rules turn it on). Agents can still switch it per chat, and their choice always wins.',
      aiHelpConversation:
        'Whether the assistant answers new conversations here or not — “By role” means on for conversation numbers. Agents can still switch it per chat, and their choice always wins.',
      reviewHelp: 'Held replies sit in the AI monitor (and on the chat) until somebody approves, edits or rejects them.',
    },
    test: {
      voiceNotSet: 'Voice trunk not set up yet — ask the super admin to add its credentials, can?',
      saveFirst: 'Save the inbox first lah.',
      sendToPlaceholder: 'you@company.com — leave blank to just check the connection',
      metaWa:
        'Paste these in Meta App Dashboard → WhatsApp → Configuration → Webhook and subscribe to the {field} field. One webhook is enough for every number.',
      metaSocial:
        'In your Meta app, subscribe {product} to the {field} field with this callback — the verify token is the same one as WhatsApp’s.',
      telegramIntro: 'Telegram sends its updates to {path}. Key in the public https address that reaches this API.',
      bspSecretMissing: 'No webhook signing secret saved yet — incoming messages will be refused until you add it in step 2.',
      twilioPublic: 'Twilio picks up the files you send from this API, so it needs a public https address (Settings → App & provider keys → public base URL).',
      wechatNote:
        'Use the same Token you keyed in here. Plaintext and safe mode both can — safe mode needs the EncodingAESKey above.',
      windowNote:
        'You can reply for {window} after the customer’s last message — after that, the composer, the AI and flows all kena refused until they write in again.',
      tokenPublic:
        'The token is public on purpose — it only lets a visitor start a conversation. If the snippet ends up somewhere it shouldn’t, just rotate it.',
      emailImap: 'Customer replies come in over IMAP, using the shared mail settings under Advanced on the Inboxes page.',
      agentStep2: 'One time only: create the outbound SIP trunk and copy the printed {id} into the same file:',
      agentThen: 'Then start the worker together with the API — it handles every AI call, SIP and WhatsApp:',
    },
    done: {
      allSet: 'All settled!',
    },
  },
  setup: {
    help: {
      waYcloud: {
        phoneNumber: 'The WhatsApp number you registered in YCloud, in international format (+60123456789).',
        appSecret: 'YCloud console → Developers → Webhooks → your endpoint → signing secret. Create the endpoint with the URL from the last step, then paste its secret here — until then, incoming messages will be refused.',
        wabaId: 'YCloud console → WhatsApp → Accounts. You need this to list, create and send templates.',
      },
      waTwilio: {
        accessToken: 'Twilio Console → Account info → Auth token. It also signs the webhooks Twilio sends over here.',
        messagingServiceSid: 'Or, instead of a sender: a Messaging Service that holds your WhatsApp sender.',
        apiKeySid: 'Optional — send with an API key instead of the Auth token (the Auth token still checks the webhooks).',
        apiKeySecret: 'The secret that shows only once, when the API key was created — hope you saved it.',
      },
      waCloud: {
        phoneNumberId:
          'Meta App Dashboard → WhatsApp → API Setup. The long ID under the phone number — not the number itself ah.',
        wabaId: 'Same API Setup page, just above the number. You need it to list and send Meta-approved templates.',
        accessToken:
          'Business Settings → Users → System users → Generate token. Tokens from the dashboard expire after 24 hours, so don’t use those.',
        appSecret: 'App Dashboard → App settings → Basic → App secret. Must set this before you go live.',
      },
      email: {
        fromEmail: 'The address your customers will see and reply to.',
        host: 'Your provider’s outgoing server — for Gmail smtp.gmail.com, for Outlook smtp.office365.com.',
        username: 'Usually the full email address — Gmail wants name@domain, not just the name.',
        password: 'Must be an app password, not your normal login — Google Account → Security → App passwords.',
        imapHost: 'Where customer replies are read from, using the same login. Leave blank to use the default under Advanced.',
      },
      telegram: {
        accessToken: 'In Telegram, message @BotFather → /newbot (or /token for a bot you already have) and paste whatever it sends back.',
      },
      meta: {
        appSecret:
          'Meta App Dashboard → App settings → Basic → App secret. Incoming messages are only accepted when Meta signs them with it, so make sure you set it before going live.',
      },
      instagram: {
        accessToken: 'Use the token of the Facebook page that the Instagram account is linked to.',
      },
      line: {
        channelSecret:
          'Same channel → Basic settings tab → Channel secret. Every webhook is checked against it, so nothing comes in until you set it.',
      },
      viber: {
        accessToken: 'Viber Partners → your bot → Info → Token. It also signs every webhook Viber sends over.',
        senderName: 'The name your messages show in Viber (max 28 characters). Leave blank and it uses the inbox name.',
      },
      sms: {
        accessToken: 'Same page → Auth token. It also checks every incoming-message webhook (X-Twilio-Signature).',
        fromNumber: 'A Twilio number that can do SMS — Phone numbers → Manage → Active numbers.',
        messagingServiceSid:
          'Or send through a Messaging Service (sender pools, A2P 10DLC). Set either this or a From number.',
      },
      wechat: {
        appSecret:
          'Same page → AppSecret (reset it to see it, only once). Add this server’s IP to the IP whitelist there also.',
        token:
          'Basic configuration → Server configuration → Token: just make one up and key in the same value in both places.',
        encodingAesKey:
          'Server configuration → EncodingAESKey. Needed for “Safe mode” (recommended) — messages get encrypted and signed.',
        externalId: 'Account info → Original ID. Optional — it gets picked up from the first message.',
      },
      tiktok: {
        externalId:
          'TikTok for Business → your business account. Only shows up after TikTok approves your app for Business Messaging.',
        accessToken: 'You get this when the business account authorises your app (TikTok API for Business → Authorization).',
        appSecret: 'Same app → Secret. Webhooks only get accepted when TikTok signs them with it.',
      },
      douyin: {
        refreshToken: 'Kept for renewing the access token later.',
      },
      zalo: {
        accessToken:
          'Your app → Official Account → OA access token (it only lasts about 25 hours — so add the refresh token below).',
        refreshToken: 'Comes together with the access token. With the app secret key, ChatBranch renews the token by itself — no need to do it by hand.',
        externalId: 'Optional — it fills itself in when you run Test.',
      },
      voice: {
        sipPassword: 'The password for that same credential list entry.',
      },
    },
    checks: {
      digits: '{field} is a long number — digits only, no letters.',
      email: 'That one doesn’t look like an email address leh.',
      e164: 'Must be an E.164 number with the country code, like +60123456789.',
      host: 'Only the host name, e.g. smtp.gmail.com — no https:// and no spaces.',
      port: 'Must be a port number, like {example}.',
      botToken: 'A bot token should look like 123456789:AAH… (digits, colon, key).',
      lineSecret: 'A LINE channel secret must be 32 hexadecimal characters.',
      viberName: 'Viber only allows 28 characters max.',
      accountSid: 'An Account SID should be AC followed by 32 hexadecimal characters.',
      messagingServiceSid: 'A Messaging Service SID should be MG followed by 32 hexadecimal characters.',
      wechatAppId: 'An AppID should start with wx.',
      wechatToken: 'The Token must be 3–32 letters and digits.',
      encodingAesKey: 'The EncodingAESKey must be exactly 43 letters and digits — not one more, not one less.',
      smsOneOf: 'Set either a From number or a Messaging Service SID lah.',
      apiKeySid: 'An API Key SID should be SK followed by 32 hexadecimal characters.',
      waTwilioOneOf: 'Set either the WhatsApp sender or a Messaging Service SID.',
    },
    guides: {
      waYcloud: {
        s3: 'Going to send templates? Note down the WABA ID under WhatsApp → Accounts.',
        s4: 'Once saved, create a webhook endpoint with the URL from the last step, then paste its signing secret back here.',
      },
      waTwilio: {
        s1: 'In the Twilio Console, open Messaging → Senders → WhatsApp senders (or try out the WhatsApp sandbox first).',
        s3: 'Key in the sender number — or a Messaging Service SID that holds it.',
        s4: 'Once saved, paste the webhook URL from the last step into the sender’s incoming-message setting. Templates come from the Content Template Builder.',
      },
      waCloud: {
        s4: 'Once setup is done, paste the callback URL and verify token from the last step into WhatsApp → Configuration.',
      },
      email: {
        s1: 'Use a mailbox your team already checks every day (support@, hello@…).',
        s2: 'Look for the SMTP server in your provider’s help pages (“SMTP settings”).',
        s3: 'If the account got two-step sign-in, create an app password.',
        s4: 'Incoming mail comes in over IMAP — this mailbox’s IMAP host, or the default under Advanced. Mail from people not in your contacts also shows up, under “Not in contacts”.',
      },
      telegram: {
        s2: 'Send /newbot, then pick a name and a username that ends with “bot”.',
        s3: 'Paste in the token BotFather sends back.',
        s4: 'Once setup is done, register the webhook from the last step.',
      },
      messenger: {
        s1: 'Your Facebook page must be connected to a Meta app that has the Messenger product.',
      },
      instagram: {
        s3: 'Use the linked page’s access token, then subscribe to messages (URL in the last step).',
      },
      line: {
        s3: 'Switch off “Auto-reply messages” in the LINE Official Account Manager, so replies only come from here.',
        s4: 'Once setup is done, register the webhook from the last step (or paste the URL and switch on “Use webhook”).',
      },
      viber: {
        s3: 'After setup, register the webhook from the last step — Viber checks it on the spot.',
      },
      sms: {
        s3: 'Once setup is done, paste the webhook URL into the number’s Messaging → “A message comes in” (HTTP POST).',
        s4: 'Twilio handles STOP / START by itself — the conversation’s opt-out follows along here.',
      },
      wechat: {
        s1: 'Must use a verified Service Account (服务号) — customer-service messages need it.',
      },
      tiktok: {
        s1: 'Business Messaging is a gated TikTok API — apply in TikTok API for Business, then wait for them to approve.',
        s4: 'You can reply within 48 hours of the customer’s last message.',
      },
      douyin: {
        s3: 'Paste the webhook URL from the last step in 开放平台 → 开发 → Webhooks (the challenge gets answered automatically).',
      },
      zalo: {
        s4: 'You can send consultation replies within 7 days of the user’s last interaction.',
      },
    },
    webhookHelp: {
      line: 'LINE Developers → the channel → Messaging API → Webhook URL, then switch on “Use webhook”. Or just register it from here.',
      viber: 'Viber needs the webhook set through its API — just register it from here (it checks the URL on the spot).',
      douyin: '抖音开放平台 → your app → Webhooks. The verify_webhook challenge gets answered automatically, no need to do anything.',
    },
  },
  unknown: {
    offHelp: 'A message from someone not in your contacts will wait for a person to link it.',
    askHelp:
      'When someone not in your contacts writes in for the first time, they get a short question asking their name and phone or email (in Malay if they write in Malay). Got a match? The chat gets linked and the AI carries on. No match? A note and a “Create contact” button are left on the chat. Max two asks, and people who opted out are never asked.',
    askCreateHelp:
      'When someone not in your contacts writes in for the first time, they get a short question asking their name and phone or email (in Malay if they write in Malay). Got a match? The chat gets linked and the AI carries on. No match? The contact gets created. Max two asks, and people who opted out are never asked.',
  },
  advanced: {
    metaIntro:
      'Set up once only, and every WhatsApp Business number shares it: paste these in Meta App Dashboard → WhatsApp → Configuration → Webhook and subscribe to {field}. Incoming messages go to the right inbox by the receiving number. For local development, you need a public https tunnel to the API.',
    intro: 'Used for sending when there’s no email inbox yet, and for pulling customer replies in over IMAP. First filled in from {env}.',
    introReadOnly:
      'Used for sending when there’s no email inbox yet, and for pulling customer replies in over IMAP. First filled in from {env} — only the super admin can change it.',
    unavailable: 'Cannot get the mail settings — {error}',
    saved: 'Default mail settings saved liao.',
    imapHint: 'Same login as SMTP. Only sending out? Leave it empty.',
  },
  whatsapp: {
    vcfTipTitle: 'Want to message new numbers?',
    vcfTip:
      '{b} WhatsApp limits first-contact messages from QR-paired numbers (error 463). Import your contacts into the paired phone — numbers in its address book don’t count for the limit.',
    vcfDownloaded: 'contacts.vcf downloaded — now import it on the phone.',
    gatewayError: 'Aiyo, the WhatsApp gateway reported an error.',
    qrExpired: 'The QR code expired before anyone scanned it. Press Try again for a new one, then scan it from WhatsApp → Linked devices.',
    pairNeedsGateway:
      'QR pairing runs in the WhatsApp gateway sidecar ({cmd}). Start it up and try again — or use WhatsApp Business (Cloud API), that one no need gateway.',
    connectedBody: 'This number can now send and receive through the inbox — steady!',
    scanHelp: 'On the phone: WhatsApp → Settings → Linked devices → Link a device, then scan. The code refreshes by itself, no need to reload.',
    loggedOut: 'Session got logged out — making a fresh QR…',
    contacting: 'Calling up the WhatsApp gateway, wait ah…',
    errors: {
      pacingWhole: 'Send pacing gaps must be whole seconds (0 or more), no decimals.',
      pacingOrder: 'The minimum send gap cannot be bigger than the maximum lah.',
      quietFormat: 'Quiet hours must be HH:MM (24h) — or leave blank to inherit.',
      dayFormat: '{day} quiet hours must be in HH:MM (24h) format.',
    },
    quietHint:
      'Sends from this number wait until this window is over (HH:MM, server time). If the end comes before the start, it runs past midnight. Leave blank to inherit.',
    quietHintNone:
      'Sends from this number wait until this window is over (HH:MM, server time). If the end comes before the start, it runs past midnight. Leave blank to inherit (none set).',
    perDayHint: 'Days not ticked never go quiet. Same start and end means the whole day is quiet.',
    pacingHint: 'A random-ish gap between queued sends — keeps personal numbers looking human.',
    voiceHint: 'The LLM and STT apply to ElevenLabs-voice calls made from this number, starting from the next call.',
  },
  widget: {
    titleHint: 'Shows at the top of the open chat panel. Leave blank and it uses the built-in title, in the visitor’s language.',
    greetingHint:
      'Shows before the visitor types anything — it is not sent as a message, so it won’t open a conversation by itself. Leave blank and it uses the built-in greeting, in the visitor’s language.',
    accentInvalid: 'Must be a hex colour, like #ff5a1f.',
    languageHint:
      'The language of the widget’s buttons, prompts and messages (English, Bahasa Melayu or 简体中文). Your own title, greeting and placeholder show exactly as you write them — no translating.',
  },
  integrations: {
    connectedNotice: '“{name}” connected liao.',
    notSetUp: '{name} not set up yet — register a Google OAuth client below, then connect with one click only.',
    saved: 'Integration saved, all good.',
    disconnectConfirm: 'Confirm disconnect “{name}”? Its stored tokens will be deleted, but the setup stays.',
    disconnected: '{name} disconnected.',
    deleteConfirm: 'Confirm delete “{name}”? Any flow steps using it will start skipping.',
    removed: '{name} removed — settled.',
    loading: 'Loading integrations, wait ah…',
    loadFailed: 'Cannot load the integrations leh — {error}',
    intro:
      'Hook up the outside systems a conversation needs: book a slot, file a document, log a row, email from a real mailbox, or let orders and payments flow in by themselves. Shopify and Stripe are {b} — their webhooks turn an order or a successful payment into a Record that journeys and the AI already understand. Everything here is optional: connect nothing and the app runs exactly the same, with integration steps just logging a clear skip.',
    connectHint: 'Shared connections: journeys, the AI’s booking skills and anyone without their own will use these. All the Google products share one sign-in.',
    managedByAdmin: 'An admin takes care of this one.',
    setUpHint: 'Register the Google OAuth client once only (below), then connect {name} with one click.',
    empty: 'No accounts connected yet leh.',
    newAccountHint: 'Paste in this provider’s token — the manual way for token-only integrations.',
    reach: {
      body: 'A personal connection ({link}) is only for its owner: what they run themselves, and bookings into “the assigned agent’s calendar”. Everything else — journeys, the AI, and anyone without their own — uses a shared connection from this page, checked in this order:',
      note: 'A branch can use head office’s connection, but head office never borrows a branch’s — and branches never share among themselves.',
    },
    connectFor: {
      subtitle: 'Choose who this shared connection is for. After this, you sign in with Google.',
      orgHint: 'Every entity that doesn’t have its own connection uses this one.',
      entityHint: 'This entity and its branches use this one, not the organization’s.',
      replaces: 'Already connected here — signing in again will replace the stored Google account.',
      personalHint: 'Just for yourself? Use {link} instead.',
    },
    blurbs: {
      google_calendar: 'Book appointments and find free slots on a shared calendar — no more back-and-forth.',
      google_drive: 'Keep the documents a conversation produced (quotes, photos, receipts) all in one place.',
      stripe: 'Payments confirm by themselves — a successful payment gets recorded against the matching Record.',
    },
  },
}
