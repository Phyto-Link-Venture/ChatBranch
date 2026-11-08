// Manglish overlay for the `settings` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const settings: Overlay<Messages['settings']> = {
  shell: {
    subtitle: 'Your own preferences, plus how the whole organization is set up — all in one place.',
    lockNote: 'Pages with a lock need a permission — you only see the ones your role got.',
    noAccessTitle: 'Sorry ah, no access to {page}',
    noAccessBody: 'Your role doesn’t have the {cap} permission. Ask an admin to tick it for you on the Permissions page.',
  },
  nav: {
    pages: {
      account: {
        blurb: 'Your name, email and password, plus every device you’re signed in on.',
      },
      appearance: {
        blurb: 'Theme, colour mode, text size, fonts, spacing and layout. Saved to your profile, so wherever you sign in also same look.',
      },
      myNotifications: {
        blurb: 'Pick which alerts can reach your bell and your notification inbox.',
      },
      connections: {
        blurb: 'Connect your own Google Calendar, Contacts and more — the AI will book into your calendar for chats assigned to you.',
      },
      notifications: {
        blurb: 'Switches for the whole organization. Once a category is muted, nobody gets it — no matter what they chose.',
      },
      customFields: {
        blurb: 'Extra fields on customers — you can use them everywhere as template variables.',
      },
      billing: {
        blurb: 'Set the markup, monthly fee and currency for each company’s AI bill here.',
      },
      security: {
        blurb: 'Two-step sign-in for your organization, plus the protections that are always switched on.',
      },
      systemEmail: {
        blurb: 'The mailbox that sends out welcome emails and password links.',
      },
    },
  },
  scope: {
    ownTitle: 'This one belongs to {entity} only',
    ownTitleThis: 'This one belongs to this entity only',
    inheritedTitle: 'Set for the whole organization — every entity follows this one',
  },
  appKeys: {
    superOnly: 'App keys only the super admin can manage.',
    unavailable: 'Cannot load the app settings — is the backend API running?',
    saved: 'Saved liao. Live straight away, no need to restart.',
    movedNote:
      'AI provider keys (OpenAI, Anthropic, Gemini, Qwen, DeepSeek, OpenRouter and more, plus ElevenLabs) now stay in {page}, next to the provider picker and model. This page only keeps the WhatsApp webhook verify token.',
    verifyHint: 'All WhatsApp accounts share this one — must match what you put in the Meta webhook form.',
    fcmHint:
      'Paste the service-account JSON from your Firebase console (Project settings → Service accounts → Generate new private key). Only the phone app needs this one — browser notifications can work without any account.',
    fcmStored: 'Got one stored already — paste a new one if you want to replace it.',
    fcmConnected: 'Connected to the Firebase project {project}. Paste a new JSON to replace it.',
    voiceNote:
      'Take note: the {agent} worker reads its keys (LiveKit, realtime voice) from {env} when it starts — for those you still need to edit the env and restart the agent. Keys here apply to WhatsApp text auto-replies straight away.',
  },
  providerKeys: {
    nothingChanged: 'Nothing changed leh.',
    saved: 'Provider keys saved — connections use them straight away.',
    superOnly: 'Provider keys only the super admin can manage.',
    unavailable: 'Cannot load the provider keys — is the backend API running?',
    unavailableError: 'Cannot load the provider keys — {error}',
    intro:
      'One-time system setup only. One Google OAuth client covers Calendar, Drive, Gmail & Sheets for everybody — set it once, then anyone can connect their own account in one click from {integrations}. The Shopify/Stripe secrets are for checking their inbound webhooks. Values already stored show masked — just leave them alone if you want to keep them.',
    redirectHint: 'Go to Google Cloud console → Credentials → your OAuth client → Authorized redirect URIs. Paste this exact value, don’t change anything.',
  },
  customFields: {
    customer: {
      saved: 'Customer fields saved liao.',
      preview: 'Preview — this is how it looks on the customer form',
    },
    loading: 'Loading custom fields, wait ah…',
    intro:
      'Set the labels that show on the Customer form — so the app fits your business, not the other way round. The AI can see the values, so a customer’s {grade} helps shape its replies. Details only some contacts have (a driver’s licence number, an outside company’s contract end) better put on a {tag} with attributes instead.',
    loadError: 'Couldn’t load the saved fields ({error}) — showing an empty schema for now.',
    empty: 'Got no custom fields yet.',
    emptyEdit: 'No custom fields yet — add one above lah.',
  },
  notifCategories: {
    messages: { description: 'A customer messaged in, on any chat channel' },
    accountHealth: {
      description: 'An account went down or got degraded, plus routing failover and recovery prompts',
    },
    system: { description: 'Everything else the platform wants to tell you' },
  },
  notifications: {
    unavailable: 'Cannot load the notification defaults — is the API running?',
    mutedCount_one: 'Got {count} category muted for the whole organization.',
    mutedCount_other: 'Got {count} categories muted for the whole organization.',
    allOn: 'All categories on — nothing muted.',
    footer:
      'Each person can still switch categories off for themselves under {link}; a muted category stays quiet for everybody. Changes apply to new alerts straight away — alerts already delivered stay in each inbox.',
  },
  myNotifications: {
    unavailable: 'Cannot load your notification preferences — is the API running?',
    hint: 'Switch off what you never need to see. Don’t worry, nothing here affects anybody else.',
    orgMuted: 'An admin muted this for the whole organization.',
    deviceHint:
      'The bell and inbox only reach you while ChatBranch is open. Switch this on and this device will buzz even after you close it — every device needs to be switched on once, here.',
    deviceUnsupported: 'This browser cannot show notifications when ChatBranch is closed, sorry ah.',
    deviceDenied: 'Notifications are blocked for this site. Allow them in your browser or phone settings first, then try again.',
    deviceNotConfigured: 'This server cannot send notifications to a device yet.',
    deviceAppNotConfigured: 'The app needs Firebase credentials first — your admin adds them under Settings → App & provider keys.',
    deviceTestNone: 'Nothing went out. {reason}',
    onlyAssignedHint:
      'New-message alerts go to the conversation’s assignee, else its team, else the admins. Switch this on if you don’t want unassigned conversations landing on you just because you’re an admin.',
  },
  appearance: {
    themes: {
      midnight: { blurb: 'Deep navy and indigo — looks best in dark mode.' },
      nord: { blurb: 'Cool frosted grey-blue, quiet and very easy on the eyes.' },
      contrast: { blurb: 'Black on white (or white on black), bold lines — the easiest one to read.' },
    },
    notSavedToProfile: 'Applied on this device, but cannot save it to your profile leh.',
    backToOrg: 'Okay, back to the organization default.',
    backToDefault: 'Back to the default look already.',
    resetNotSaved: 'Reset here already, but the profile didn’t update (HTTP {status}).',
    resetOffline: 'Reset here already, but cannot reach the server.',
    orgCleared: 'Organization default cleared — new users will start on Paper & Ink, light.',
    orgSaved: 'Saved as the organization default for everyone who hasn’t picked their own look yet.',
    themeHint: 'Ten looks, each one got light and dark',
    modeHint: 'System just follows your device',
    devicePrefersDark: 'Your device prefers dark right now.',
    devicePrefersLight: 'Your device prefers light right now.',
    accentHint: 'We adjust the inks so text is still easy to read',
    adjustedOwnLight: 'Your colour, tweaked darker or lighter for light mode so text still keeps 4.5:1 contrast.',
    adjustedOwnDark: 'Your colour, tweaked darker or lighter for dark mode so text still keeps 4.5:1 contrast.',
    adjustedThemeLight: 'The theme’s own accent, tweaked darker or lighter for light mode so text still keeps 4.5:1 contrast.',
    adjustedThemeDark: 'The theme’s own accent, tweaked darker or lighter for dark mode so text still keeps 4.5:1 contrast.',
    noOrgDefault: 'No organization default yet — so it’s Paper & Ink, light.',
    textSizeHint: 'Pick a preset, or drag for any size you like',
    spacingHint: 'How much room around everything',
    layoutHint: 'Computers only — on phones the menu and bottom tabs stay the same',
  },
  connections: {
    unavailable: 'Cannot load your connections — is the API running?',
    connected: 'Done — your {product} is connected.',
    cancelled: 'Google sign-in was cancelled — nothing got connected.',
    notAvailable: '{product} not available yet — ask an admin to register the Google client first.',
    disconnected: 'Your {product} is disconnected.',
    intro:
      'Connect your own Google products. Each grant is yours only — other people still use their own or the shared connection, and you can disconnect anytime.',
    notConfigured:
      'Google sign-in not set up for this workspace yet. A super admin registers the Google client once (Settings → App & provider keys); after that everybody can connect their own account in one click.',
    reconnectHint: 'Sign in again (like if you want to switch Google account)',
    shared: 'Shared connections for the whole organization or an outlet are handled by admins on {link}.',
    order:
      'When something needs Google, ChatBranch tries your own connection first, then a shared one: the conversation’s entity, its parent entities, then the whole organization.',
    usedFor: {
      google_calendar:
        'Booking skills set to “the conversation’s assigned agent” will check your free time and book into this calendar for conversations assigned to you.',
      google_contacts: 'Contacts → Import → Google Contacts reads your address book (read-only, won’t change anything).',
      gmail: 'Actions you run yourself go out from your own Gmail; automated journeys use the shared connection.',
      google_drive: 'Actions you run yourself save into your own Drive; automated journeys use the shared connection.',
      google_sheets: 'Actions you run yourself go into your own spreadsheets; automated journeys use the shared connection.',
    },
  },
  aiProviders: {
    groups: {
      providers: {
        blurb:
          'Paste one key and the AI is on. Leave the provider on Automatic and the model empty — the defaults are good enough.',
      },
      tunables: {
        blurb:
          'Timings for the flow engine, channel-health checker and the idle-chat summariser. Some are only read when a thread starts, so a change only kicks in after the next restart.',
      },
    },
    fields: {
      ilmuKeyHelp: 'Used for AI chat/summaries. Live voice calls take the key from the agent’s .env instead.',
      elevenlabsKeyHelp: 'For voice listing / cloning / preview / TTS. Live voice calls take the key from the agent’s .env instead.',
      orderHelp: 'Only used in Automatic mode — the order the chat brain tries providers in, picking the first one with a key set.',
      chatModelHelp: 'Applies to whichever provider wins. Empty = that provider’s default model; pick Custom… if you want to type any model id.',
      localUrlHelp:
        'For the Local (Ollama / LM Studio) provider — e.g. http://localhost:11434/v1 (Ollama) or http://localhost:1234/v1 (LM Studio). Then pin “local” above and set the chat model to the model name (e.g. llama3.1). Make sure the server can reach this URL.',
      localKeyHelp: 'Optional — Ollama doesn’t care about it; some gateways need one.',
      reviewerModelHelp:
        'The stronger model the AI monitor’s “AI review” uses to check past replies — only runs when someone clicks Review now. Empty = anthropic/claude-sonnet-5.5 on OpenRouter; write provider:model (for example openai:gpt-4.1) for another provider. No key for that provider? Then the normal chat model does the review.',
      summaryIntervalHelp: 'Only read when the summariser thread starts — a change kicks in after the next restart.',
      sttProviderHelp:
        'How customers’ voice notes turn into text the AI can read. Automatic uses OpenAI when its key is set, else ElevenLabs. Local Whisper uses your own Whisper server: no need any OpenAI key, and the audio never leaves your network.',
      sttKeyHelp: 'Only needed if your Whisper server asks for one.',
    },
    loading: 'Loading AI & provider settings, wait ah…',
    loadError: 'Aiyo, cannot load AI & provider settings — {error}',
    readOnly: 'read-only — you need the AI keys permission',
    envNote:
      'Live voice calls read ILMU_API_KEY / ELEVENLABS_API_KEY / PIPELINE_LLM_PROVIDER from the voice agent’s .env at call time — the agent process cannot see these settings. Keys here power AI chat, summaries and the server-side voice tools.',
    chooseAutomatic: 'Choose “Automatic (priority order)” above first, then only you can edit this.',
    failure: {
      title: 'Alamak, AI replies are failing.',
      body_one:
        'Last failure {when}: the AI provider rejected the request, so the customer got the built-in fallback reply, not a real answer.',
      body_other:
        'Last failure {when} ({count} so far): the AI provider rejected the requests, so customers got the built-in fallback reply, not a real answer.',
      hint: 'Open the provider below and press Test connection to see why. This message will go away by itself after the next reply that works.',
    },
    providersBlurb:
      'Connect one or more. Click a provider to paste its key, choose its model and test it. One key is enough to switch the AI on.',
    noMatch: 'No provider matches your search leh.',
    answersBlurb:
      'Automatic tries your connected providers in the order below — if one fails, it moves on to the next. Pick one provider if you want to use only that one.',
    autoHelp: 'If the first provider fails, the next connected one will answer.',
    pinHelp: 'Only this provider answers. If it fails, the AI just keeps quiet — it won’t jump to another vendor.',
    orderEmpty: 'Connect a provider above first lah.',
    tile: {
      visionHint: 'Can read photos and PDFs turned into images — invoice photos, uploaded designs, all can.',
    },
    catalog: {
      ilmu: 'YTL’s own Malaysian model — your data stays in Malaysia.',
      local: 'Ollama, LM Studio or any OpenAI-compatible server you run yourself.',
    },
    panel: {
      fromEnv: 'This key comes from the server’s .env file. Save one here and it will replace that one.',
      localHelp:
        'Any OpenAI-compatible server also can — Ollama, LM Studio, vLLM, LiteLLM or a company gateway. Just make sure the ChatBranch server can reach it.',
      modelHelp: 'Used every time this provider answers. Pick Custom… to type any model id.',
      testOk: 'Can liao — {model} answered in {ms} ms.',
      testFail: 'Aiyo, didn’t work: {error}',
    },
  },
  systemEmail: {
    sourceSystem: 'Welcome emails and password links go out from this mailbox ({from}).',
    sourceDefault: 'Not set up here yet — for now they borrow the default mail settings under Channels ({from}).',
    sourceNone: 'Not set up yet — new users cannot get a welcome email until you fill in the mailbox below.',
    loginOk: 'The links inside will open {url}.',
    loginMissing:
      'The login address is not configured yet: whoever runs the server must set FRONTEND_URL (or PUBLIC_BASE_URL) to the address people open ChatBranch at. Until then, no welcome email can go out.',
    loginLocal:
      'FRONTEND_URL is pointing to this machine (localhost), so a link in an email won’t work for anybody else. Ask whoever runs the server to set it to the dashboard’s public address.',
    linkHours: 'Each set-password link can be used once only, for {hours} hours. No email will ever have a password inside.',
    formIntro:
      'The SMTP login of the mailbox these emails come from — a no-reply address at your company is good. The password stays on the server and won’t be shown again.',
    fromNameHint: 'Shown as the sender. Leave it empty = ChatBranch.',
    passwordKept: 'Saved already — leave it to keep it, or type a new one.',
    advancedHint: 'Port and connection security — the defaults are fine for most providers.',
    saved: 'Saved liao.',
    removeConfirm:
      'Remove the System email ah? Sign-in emails will then use the default mail settings, or stop until it’s set up again.',
    removed: 'Removed already.',
    testTo: 'The test will go to your own address, {email}.',
    testNoEmail: 'Add your own email address in {link} first, then you can send yourself a test.',
    testSaveFirst: 'Save your changes first — the test uses what’s saved.',
    testSent: 'Test email sent to {email}. Go check the inbox (and the spam folder also).',
    testFailed: 'Alamak, the test email didn’t go out: {error}',
    notConfigured: 'Nothing to send with yet — fill in the mailbox and save first lah.',
    invalid: {
      host: 'That one doesn’t look like a server name (for example smtp.example.com).',
      from_email: 'Put in a proper sender address lah.',
      port: 'Port must be a number between 1 and 65535.',
      security: 'Choose a connection security first.',
      username: 'The login is too long leh.',
    },
    loadFailed: 'Cannot load the System email settings.',
  },
  security: {
    policyIntro:
      'Ask for a code from an authenticator app every time someone signs in. People who must use it but haven’t set it up yet will be brought to the setup screen first, before anything else.',
    saved: 'Saved — all set.',
    selfNote: 'If you haven’t set it up yourself yet, you’ll be asked to do it right after saving.',
    lockout:
      'A username gets locked for {minutes} min after {count} wrong passwords or codes; a network address after {ip} (except for people who signed in from there before).',
    rateLimitOn: 'Request rate limits are on — extra strict for sign-in, the website widget and public links.',
    passwordRule: 'New passwords need at least {min} characters, cannot be the username and cannot be a well-known password.',
    loadFailed: 'Cannot load the security settings.',
  },
}
