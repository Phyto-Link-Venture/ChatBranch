/** How-to guides — channels group. Keys: see lib/guides/types.ts. */
export const channels = {
  connectFirstChannel: {
    title: 'Connect your first channel',
    summary:
      'Link the place your customers already message you — WhatsApp, your website, email or another app — so their messages land in ChatBranch and your team can answer them in one inbox.',
    before:
      'You need an admin login with the “Manage channel accounts” permission. For most channels you also need an account on that platform (for example a Meta business account or a Telegram bot). Until a channel is connected, everything runs in sandbox: messages are stored but never sent.',
    steps: {
      s1: {
        title: 'Open the Inboxes page',
        body: 'In the sidebar, open Channels → “Channels”. The page is called “Inboxes” and lists every number, mailbox and widget your team answers from.',
      },
      s2: {
        title: 'Pick where to start',
        body: 'Start with the channel most of your customers already use. “Website chat” is the quickest (no outside account needed); “WhatsApp Business” is best for a shop that sells on WhatsApp; “Email” suits a support mailbox.',
      },
      s3: {
        title: 'Click a channel block',
        body: 'Scroll down to “Add a channel” and click a block, for example “WhatsApp Business”. A setup panel slides in from the right with four steps at the top: “Choose channel”, “Connect”, “Who answers it” and “Test and finish”.',
      },
      s4: {
        title: 'Fill in the connection details',
        body: 'On the “Connect” step, copy each value from the platform into its field. The grey line under each field says where to find it, and “Where do I find these?” opens a short walk-through. Click “Continue”.',
      },
      s5: {
        title: 'Say who answers it',
        body: 'Type an “Inbox name” your team will recognise (for example “Main shop WhatsApp”), pick a team if you have one, and leave the rest as it is for now. Click “Create inbox”.',
      },
      s6: {
        title: 'Run the test',
        body: 'On “Test and finish”, click “Test connection”. A green line means ChatBranch can reach the account; a red line says what is wrong. Some channels also show a webhook address here to copy into the platform — the channel’s own guide explains it.',
      },
      s7: {
        title: 'Finish',
        body: 'Click “Finish”. You see “Inbox ready” with the inbox, channel, entity and team. Click “Close”, or “Add another” to connect the next channel.',
      },
      s8: {
        title: 'Check the new card',
        body: 'Your inbox now appears under “Connected” as a card with a health label (for example “Healthy”) and “Settings” and “Test” buttons.',
      },
      s9: {
        title: 'Send yourself a message',
        body: 'From your own phone or email, send a message to the channel you just connected. In the sidebar, open Work → “Chats”: the message appears at the top of the list within a few seconds.',
      },
    },
    done: 'The inbox card shows under “Connected” and your test message appears in Chats.',
    tips: {
      t1: 'Click “Settings” on a card at any time to change who answers it or to replace an expired token — leaving a secret field blank keeps the current value.',
      t2: 'Use the channel chips above the cards to show only one kind of inbox when the list grows.',
      t3: 'Next, read “Who answers it” to decide whether the AI replies on this inbox and which team gets new chats.',
    },
    trouble:
      'The blocks are greyed out: your role lacks “Manage channel accounts” — ask an admin. “Continue” shows red text: a required field is empty or in the wrong format; the red line says which. The test passes but no message arrives: the platform has not been pointed at ChatBranch yet (paste the webhook address from the test step), or ChatBranch is not reachable at a public https address.',
  },
  whatsappCloud: {
    title: 'Connect a WhatsApp Business number (Cloud API)',
    summary:
      'Connect your official WhatsApp Business number through Meta’s Cloud API. It is the safe, approved way to message customers on WhatsApp and the only one that can send Meta-approved templates.',
    before:
      'You need: a Meta (Facebook) business account; an app at developers.facebook.com with the WhatsApp product added; a phone number added and verified in that app; and ChatBranch reachable at a public https address. Ask your developer to help with the Meta side if it is new to you. Admins only.',
    steps: {
      s1: {
        title: 'Copy the two IDs from Meta',
        body: 'Open developers.facebook.com → My Apps → your app → WhatsApp → API Setup. Copy the “Phone number ID” (the long number under your phone number — not the phone number itself) and the “WhatsApp Business Account ID”.',
      },
      s2: {
        title: 'Create a permanent access token',
        body: 'In Meta Business Settings → Users → System users, choose (or add) a system user and click “Generate token” with the whatsapp_business_messaging and whatsapp_business_management permissions. Copy it — the temporary token on the API Setup page stops working after 24 hours.',
      },
      s3: {
        title: 'Copy the app secret',
        body: 'In your app, open App settings → Basic and click “Show” next to “App secret”. ChatBranch uses it to check that incoming messages really come from Meta.',
      },
      s4: {
        title: 'Start the setup in ChatBranch',
        body: 'In the sidebar, open Channels → “Channels”. Under “Add a channel” → “Messaging apps”, click “WhatsApp Business”. The panel opens on “Connect the number”.',
      },
      s5: {
        title: 'Paste the details',
        body: 'Paste the “Phone number ID”, the “WhatsApp Business Account ID”, the “Access token” and the “App secret”. The account ID is marked optional, but without it you cannot list or send templates. Click “Continue”.',
      },
      s6: {
        title: 'Name it and choose who answers',
        body: 'Type an “Inbox name”, for example “Shop WhatsApp”, and pick a team. “Account role” starts on “Reminder (business)”, where the AI is off by default — set “AI replies” to “On” if you want the AI to chat on this number. Click “Create inbox”.',
      },
      s7: {
        title: 'Test the connection',
        body: 'On “Test and finish”, click “Test connection”. A green line such as “Credentials present for Your Shop · +60 12-345 6789.” means Meta accepted the token.',
      },
      s8: {
        title: 'Copy the webhook details',
        body: 'Under “Point Meta at ChatBranch” you see a “Callback URL” and a “Verify token”, each with a copy button. Copy both.',
      },
      s9: {
        title: 'Paste them into Meta',
        body: 'In your Meta app open WhatsApp → Configuration → Webhook → Edit. Paste the Callback URL and the Verify token, click “Verify and save”, then click “Manage” and subscribe to the “messages” field. You only do this once — one webhook serves every WhatsApp Business number.',
      },
      s10: {
        title: 'Finish and try it',
        body: 'Back in ChatBranch click “Finish”. Send a WhatsApp message from your own phone to the business number and open Work → “Chats” — it appears within seconds.',
      },
    },
    done: 'The card shows a green “Healthy” label and your test message arrives in Chats.',
    tips: {
      t1: 'WhatsApp only lets you send free text within 24 hours of the customer’s last message. After that, send an approved template from the chat.',
      t2: 'The verify token can be changed under Settings → “App & provider keys”; if you change it, paste the new one into Meta too.',
      t3: 'Add more numbers the same way — the Meta webhook does not need to be set again.',
    },
    trouble:
      'Meta says the callback could not be verified: ChatBranch must be reachable at a public https address, and the verify token must match exactly. The test says the account state is wrong: the token expired or lacks permissions — generate a system-user token again and paste it in “Settings”. Messages arrive in Meta but not in ChatBranch: check the app secret is filled in and that you subscribed to the “messages” field.',
  },
  whatsappQr: {
    title: 'Pair a personal WhatsApp number by QR code',
    summary:
      'Link an ordinary WhatsApp number to ChatBranch by scanning a QR code, the same way you open WhatsApp Web. It needs no Meta approval — but it is unofficial, so use it only for demos and testing.',
    before:
      'Warning: this uses the unofficial WhatsApp Web connection. It breaks WhatsApp’s terms and the number can be banned — always use a spare number, never your main business line. You need the phone with that number in your hand, and your technical team must have the WhatsApp gateway running next to ChatBranch. Admins only.',
    steps: {
      s1: {
        title: 'Open the WhatsApp by QR block',
        body: 'In the sidebar, open Channels → “Channels”. Under “Add a channel” → “Messaging apps”, click “WhatsApp by QR” (it carries a “Demo” badge and an orange warning).',
      },
      s2: {
        title: 'Read the warning and tick the box',
        body: 'The panel opens on “Before you pair” and explains the risk. Tick “I understand the risk and will pair a spare number.” and click “Continue”.',
      },
      s3: {
        title: 'Name the inbox',
        body: 'Type an “Inbox name”, for example “Demo WhatsApp”. “Account role” is already “Conversation (personal)”, which keeps sending slow and human-like to protect the number. Pick a team if you like.',
      },
      s4: {
        title: 'Create the inbox',
        body: 'Click “Create inbox”. The panel moves to “Test and finish” and shows “Pair the phone” with a QR code. The code refreshes by itself every few seconds.',
      },
      s5: {
        title: 'Open Linked devices on the phone',
        body: 'On the phone, open WhatsApp → Settings → “Linked devices” → “Link a device”. Unlock the phone if it asks.',
      },
      s6: {
        title: 'Scan the code',
        body: 'Point the phone’s camera at the QR code on your screen. After a moment the panel says “Connected as” followed by the number.',
      },
      s7: {
        title: 'Test it',
        body: 'Click “Test connection”. You should see “Paired and connected” with the number.',
      },
      s8: {
        title: 'Finish',
        body: 'Click “Finish”, then “Close”. The card appears under “Connected” with a “Healthy” label.',
      },
      s9: {
        title: 'Keep the phone alive',
        body: 'Leave the phone charged, connected to the internet and logged in to WhatsApp. If the phone stays offline for a long time, WhatsApp unlinks ChatBranch and you must scan again (click “Pair” on the card).',
      },
    },
    done: 'The card shows “Healthy” and a message sent to that number appears in Chats.',
    tips: {
      t1: 'WhatsApp limits first messages from paired numbers to people who are not in the phone’s address book. Click “Export contacts (.vcf)” and import the file on the phone to avoid this.',
      t2: 'Never send bulk or cold messages from a paired number — it is the fastest way to get it banned.',
      t3: 'For real customers, connect a WhatsApp Business (Cloud API) number instead; it can also send templates.',
    },
    trouble:
      'No QR code appears, or a message mentions the gateway: the WhatsApp gateway is not running — ask your technical team to start it, then reopen the card and click “Pair”. The card says “Disconnected”: the phone was offline or logged out — click “Pair” and scan again. The card says “Banned”: WhatsApp blocked the number; stop using it and pair a different spare number.',
  },
  websiteWidget: {
    title: 'Add the chat widget to your website',
    summary:
      'Put a chat bubble on your website so visitors can message you without a phone number. Their chats land in the same inbox as WhatsApp and email, with the same AI and teams.',
    before:
      'You need an admin login and a way to edit your website’s HTML (or a “custom code” / “footer code” box in your website builder). No outside account is needed.',
    steps: {
      s1: {
        title: 'Open the Website chat block',
        body: 'In the sidebar, open Channels → “Channels”. Under “Add a channel” → “Messaging apps”, click “Website chat”. The panel opens on “Design the widget”.',
      },
      s2: {
        title: 'Design how it looks',
        body: 'Optionally fill in “Header title” (for example “Chat with Ana’s Bakery”), a “Greeting” shown before the visitor types, the “Accent colour” of the bubble and the “Input placeholder”. Blank fields use friendly built-in words.',
      },
      s3: {
        title: 'Choose the language',
        body: 'Under “Widget language”, keep “Automatic — the visitor’s browser language”, or pick English, Bahasa Melayu or 简体中文 to always use one. Click “Continue”.',
      },
      s4: {
        title: 'Name it and create it',
        body: 'Type an “Internal name” such as “Website chat — main site”, pick a team if you have one, and click “Create inbox”.',
      },
      s5: {
        title: 'Copy the code',
        body: 'On “Test and finish”, find “Put it on your site”. Click “Copy” next to the code — it is a single script tag.',
      },
      s6: {
        title: 'Paste it into your website',
        body: 'Open your website’s HTML and paste the code just before the closing </body> tag, on every page where the bubble should appear. In a website builder, paste it into the site-wide footer or custom-code box. Publish the site.',
      },
      s7: {
        title: 'Check that it answers',
        body: 'Back in ChatBranch, click “Check the widget answers”. A green line like “Widget answers — it opens as “Chat with us”.” means the widget is live.',
      },
      s8: {
        title: 'Finish',
        body: 'Click “Finish”, then “Close”. The widget appears under “Connected” with the number of conversations it has.',
      },
      s9: {
        title: 'Try it as a visitor',
        body: 'Open your website (a private browser window works best), click the chat bubble in the corner and send a message. Open Work → “Chats” — it appears there.',
      },
    },
    done: 'The chat bubble shows on your website and your test message appears in Chats.',
    tips: {
      t1: 'The code is safe to be public — it only lets visitors start a chat. If it ends up somewhere it should not, click “Rotate token” and paste the new code.',
      t2: 'Make one widget per website or brand, each with its own name and team.',
      t3: 'Change the look later from the card’s “Settings” — the code on your site stays the same.',
    },
    trouble:
      'No bubble on the site: make sure the whole code was pasted before </body>, the page was published, and clear the browser cache. The check says the widget did not answer: ChatBranch is not reachable from the internet at its public address — ask your technical team. After “Rotate token” the bubble disappeared: paste the new code; the old one stops working on purpose.',
  },
  emailMailbox: {
    title: 'Connect a support mailbox',
    summary:
      'Send and receive email in ChatBranch from your own mailbox (for example support@yourshop.com). Outgoing mail uses SMTP; customer replies are read back in over IMAP.',
    before:
      'You need the mailbox address and its login. For Gmail or Outlook with two-step sign-in you need an app password (a separate 16-character password made just for ChatBranch) — not your normal password. Admins only.',
    steps: {
      s1: {
        title: 'Create an app password',
        body: 'Gmail: Google Account → Security → turn on 2-Step Verification → “App passwords”, create one named “ChatBranch” and copy the 16 letters. Outlook / Microsoft 365: Microsoft account → Security → “App passwords” (work accounts may need your IT admin to allow SMTP sign-in).',
      },
      s2: {
        title: 'Open the Email block',
        body: 'In the sidebar, open Channels → “Channels”. Under “Add a channel” → “SMS, email & voice”, click “Email”. The panel opens on “Connect the mailbox”.',
      },
      s3: {
        title: 'Fill in the sending details',
        body: 'Type the “From address” (support@yourshop.com), the “SMTP host” (Gmail smtp.gmail.com, Outlook smtp.office365.com), the “Username” (usually the full email address) and paste the app password in “Password”. “Port” can stay empty (587 is used).',
      },
      s4: {
        title: 'Add the incoming details',
        body: 'To receive replies, fill in “IMAP host (replies)” — Gmail imap.gmail.com, Outlook outlook.office365.com. “IMAP port” can stay empty (993). Leave “Use TLS” switched on. Click “Continue”.',
      },
      s5: {
        title: 'Name it and create it',
        body: 'The “Inbox name” is filled with the address; change it if you like, pick a team, and click “Create inbox”.',
      },
      s6: {
        title: 'Send a test email',
        body: 'On “Test and finish”, type your own address in “Send a test email to” and click “Test connection”. You see “Test email sent to …” — check your inbox.',
      },
      s7: {
        title: 'Finish',
        body: 'Click “Finish”, then “Close”. The mailbox appears under “Connected”.',
      },
      s8: {
        title: 'Make sure the sender is a contact',
        body: 'ChatBranch only pulls in mail from people who are already contacts, so spam stays out. Add yourself as a contact in Work → “Contacts” with the email you will test from.',
      },
      s9: {
        title: 'Reply to the test',
        body: 'Reply to the test email from that address. After a minute, open Work → “Chats” — the reply appears as an email conversation you can answer.',
      },
    },
    done: 'The test email reaches your inbox and your reply shows up in Chats.',
    tips: {
      t1: 'Use a shared team address (support@, hello@) rather than someone’s personal mailbox.',
      t2: 'Replies go out as normal email threads with “Re:” subjects, so customers can keep replying from their own mail app.',
      t3: 'You can connect several mailboxes; each conversation keeps using the mailbox it started on.',
    },
    trouble:
      'Sign-in failed: use an app password, not your normal password, and the full email address as username. Gmail says “less secure app”: turn on 2-Step Verification first, then create the app password. Replies never arrive: check the IMAP host is filled in and that the sender is saved as a contact.',
  },
  telegramBot: {
    title: 'Connect a Telegram bot',
    summary:
      'Create a Telegram bot for your business and connect it, so customers who message the bot reach your team in ChatBranch.',
    before:
      'You need the Telegram app on your phone or computer, an admin login, and ChatBranch reachable at a public https address (your technical team knows it, for example https://chat.yourshop.com).',
    steps: {
      s1: {
        title: 'Create the bot with BotFather',
        body: 'In Telegram, search for @BotFather (it has a blue tick) and open the chat. Send /newbot, then a display name (for example “Ana’s Bakery”) and a username that ends in “bot” (for example anasbakery_bot).',
      },
      s2: {
        title: 'Copy the token',
        body: 'BotFather replies with a token that looks like 123456789:AAH… — copy all of it. Keep it private: anyone with it controls the bot.',
      },
      s3: {
        title: 'Open the Telegram block',
        body: 'In ChatBranch, open Channels → “Channels”. Under “Add a channel” → “Messaging apps”, click “Telegram”. Paste the token in “Bot token” and click “Continue”.',
      },
      s4: {
        title: 'Name it and create it',
        body: 'Type an “Inbox name”, pick a team if you have one, and click “Create inbox”.',
      },
      s5: {
        title: 'Test the connection',
        body: 'On “Test and finish”, click “Test connection”. You should see “Connected as” followed by your bot’s name.',
      },
      s6: {
        title: 'Register the webhook',
        body: 'Under “Register the webhook”, type ChatBranch’s public https address in “Public base URL” (for example https://chat.yourshop.com) and click “Register”. You see “Webhook registered.” — Telegram now delivers messages to ChatBranch.',
      },
      s7: {
        title: 'Finish',
        body: 'Click “Finish”, then “Close”. The bot appears under “Connected”.',
      },
      s8: {
        title: 'Message the bot',
        body: 'In Telegram, open your bot (t.me/your_bot_username), tap “Start” and send a message. Open Work → “Chats” — it appears there.',
      },
    },
    done: 'The card shows “Healthy” and your message to the bot appears in Chats.',
    tips: {
      t1: 'Use BotFather’s /setuserpic and /setdescription to give the bot your logo and a short description.',
      t2: 'Share the bot as a link (t.me/your_bot_username) on your website, receipts or social profiles.',
      t3: 'Lost the token? Send /token to BotFather, pick the bot, then paste the new one in the card’s “Settings”.',
    },
    trouble:
      '“Register” shows an error about https: type the public https address, not localhost. The test fails: the token was copied incompletely or was replaced in BotFather — paste it again. Messages do not arrive: click “Register” again after any address change.',
  },
  messengerInstagram: {
    title: 'Connect a Facebook page or Instagram account',
    summary:
      'Bring Facebook Messenger and Instagram direct messages into ChatBranch, so your team answers them next to WhatsApp and email.',
    before:
      'You need: a Facebook page you manage; a Meta app at developers.facebook.com with the Messenger product (and the Instagram product for Instagram); for Instagram, a professional account linked to that page; and ChatBranch reachable at a public https address. Admins only.',
    steps: {
      s1: {
        title: 'Find the page or account ID',
        body: 'Facebook page ID: your page → About → Page transparency, or Meta Business Suite → Settings → Page info. Instagram account ID: Meta App Dashboard → Instagram → API setup.',
      },
      s2: {
        title: 'Generate a page access token',
        body: 'In the Meta App Dashboard open Messenger → Settings → Access tokens and click “Generate” next to your page. Copy the token. Instagram uses the token of the page it is linked to.',
      },
      s3: {
        title: 'Copy the app secret',
        body: 'In your app open App settings → Basic and copy the “App secret”. Fill it in — ChatBranch refuses incoming messages that Meta has not signed with it.',
      },
      s4: {
        title: 'Open the block',
        body: 'In ChatBranch, open Channels → “Channels”. Under “Add a channel” → “Social”, click “Messenger” or “Instagram”.',
      },
      s5: {
        title: 'Paste the details',
        body: 'Paste the “Facebook Page ID” (or “Instagram account ID”), the “Page access token” and the “App secret”. Click “Continue”.',
      },
      s6: {
        title: 'Name it and create it',
        body: 'Type an “Inbox name” such as “Facebook page”, pick a team, and click “Create inbox”. Then click “Test connection” — a green line means the token works.',
      },
      s7: {
        title: 'Copy the webhook details',
        body: 'Under “Point Meta at ChatBranch”, copy the “Callback URL” and the “Verify token”.',
      },
      s8: {
        title: 'Paste them into Meta',
        body: 'In your Meta app, open the Messenger (or Instagram) webhook settings, paste the Callback URL and Verify token, verify, and subscribe your page to the “messages” field. Then click “Finish” in ChatBranch.',
      },
      s9: {
        title: 'Know the reply window',
        body: 'Within 24 hours of the customer’s last message anyone can reply — the AI, flows or an agent. From 24 hours to 7 days only a person typing in Chats can reply. After 7 days nobody can message them until they write again.',
      },
    },
    done: 'A message sent to your page or Instagram account appears in Chats and you can reply to it.',
    tips: {
      t1: 'Answer fast: after 24 hours the AI and automatic messages are blocked on these channels.',
      t2: 'Use the Chats filter for the reply window to find conversations that are about to close.',
      t3: 'Messenger and Instagram use the same callback URL and verify token as WhatsApp Business.',
    },
    trouble:
      'Messages do not arrive: the app secret is missing or wrong, or the page is not subscribed to “messages”. The test fails: the page token expired — generate a new one and paste it under “Settings”. A reply is refused: the customer wrote more than 24 hours ago (the AI is blocked) or more than 7 days ago (everyone is blocked).',
  },
  otherChannels: {
    title: 'Connect LINE, Viber, SMS, WeChat, Zalo, TikTok or Douyin',
    summary:
      'All these channels connect the same way: copy a few credentials from the platform into ChatBranch, then paste the webhook address ChatBranch shows you back into the platform.',
    before:
      'You need a business account on the platform (a LINE Official Account, a Viber bot, a Twilio account with an SMS number, a WeChat Service Account, a Zalo Official Account…) and ChatBranch reachable at a public https address. TikTok and Douyin only open their messaging to approved apps — apply first. Admins only.',
    steps: {
      s1: {
        title: 'Find the channel block',
        body: 'In the sidebar, open Channels → “Channels” and scroll to “Add a channel”. LINE, Viber and Zalo are under “Messaging apps”, TikTok under “Social”, SMS under “SMS, email & voice”, WeChat and Douyin under “China”. Blocks marked “Requires approval” need the platform’s approval first.',
      },
      s2: {
        title: 'Open the walk-through',
        body: 'Click the block. On the “Connect” step, open “Where do I find these?” for the platform’s own steps, and read the grey help line under each field.',
      },
      s3: {
        title: 'Copy the credentials',
        body: 'Copy each value from the platform. For example: LINE — “Channel access token” and “Channel secret”; Viber — “Authentication token”; SMS — “Account SID”, “Auth token” and a “From number” like +60123456789; WeChat — “AppID”, “AppSecret” and a “Server token” you make up; Zalo — “App ID”, “OA secret key” and the tokens. Click “Continue”.',
      },
      s4: {
        title: 'Name it and create it',
        body: 'Type an “Inbox name”, pick a team, and click “Create inbox”.',
      },
      s5: {
        title: 'Copy the webhook URL',
        body: 'On “Test and finish”, find “Point … at ChatBranch” and click “Copy” next to “Webhook URL”. Each inbox has its own address.',
      },
      s6: {
        title: 'Paste it into the platform',
        body: 'Follow the line above the URL. For example, Twilio: your number → Messaging configuration → “A message comes in”, Webhook, HTTP POST. WeChat: Server configuration → URL, the same Token, then Enable. For LINE and Viber you can instead type the “Public base URL” and click “Register”.',
      },
      s7: {
        title: 'Test the connection',
        body: 'Click “Test connection”. A green line (often “Connected as …”) means the credentials work; an orange line is a warning to read, such as the webhook not being active yet.',
      },
      s8: {
        title: 'Finish and send a test',
        body: 'Click “Finish”. Send a message to your account from the app on your phone, then open Work → “Chats” to see it.',
      },
    },
    done: 'The card shows “Healthy” and messages from the app appear in Chats.',
    tips: {
      t1: 'Some channels only let you reply for a while after the customer’s last message: WeChat, TikTok and Douyin 48 hours, Zalo 7 days. The note under the webhook URL says which applies.',
      t2: 'LINE, Viber and SMS cannot send files from ChatBranch yet — send text, or a link.',
      t3: 'When a customer unfollows, unsubscribes or texts STOP, the conversation is marked opted out and nothing more is sent.',
      t4: 'TikTok and Douyin are marked “Beta”: they follow the platforms’ published rules but have not yet been tried against the live service.',
    },
    trouble:
      'The platform says the webhook failed: ChatBranch must be at a public https address, and the secret (channel secret, auth token, OA secret key…) must be filled in — without it incoming messages are refused. WeChat test fails: add ChatBranch’s server IP to the IP whitelist in the WeChat platform. TikTok or Douyin show no credentials: the platform has not approved your app yet; the inbox stays in sandbox until then.',
  },
  whoAnswers: {
    title: 'Choose who answers an inbox',
    summary:
      'Decide, for each inbox, which team gets new chats, whether the AI replies, whether the AI’s replies are checked first, and what happens when a stranger writes in.',
    before:
      'The inbox must already be connected. You need an admin login. To route chats to a team, create the team first under Admin → “Teams”.',
    steps: {
      s1: {
        title: 'Open the inbox settings',
        body: 'In the sidebar, open Channels → “Channels” and click “Settings” on the inbox card. The panel opens on “Who answers it”.',
      },
      s2: {
        title: 'Check the name',
        body: '“Inbox name” is what your team sees in Chats and in reports. Make it clear, for example “KL branch WhatsApp”.',
      },
      s3: {
        title: 'Pick the entity',
        body: 'Choose the branch or company that owns this inbox. Every conversation arriving here is filed under it. Leave it organization-wide if all branches share the inbox.',
      },
      s4: {
        title: 'Pick a team',
        body: 'Under “Team”, choose who gets new conversations. They are shared out in turn among the team’s members. “No team” uses the entity’s auto-assign team.',
      },
      s5: {
        title: 'Set the account role (WhatsApp only)',
        body: '“Reminder (business)” is for an official number that sends templates; its AI is off by default. “Conversation (personal)” holds AI chats and sends at a slow, human pace.',
      },
      s6: {
        title: 'Choose if the AI replies',
        body: 'Under “AI replies”, choose “On” or “Off” for new conversations. “Default” (or “By role” on WhatsApp) follows the normal rule. An agent can still switch the AI per chat, and their choice wins.',
      },
      s7: {
        title: 'Choose if the AI is checked first',
        body: 'Under “Check the AI before it sends”, pick “Send automatically”, “Hold risky replies for review”, “Hold every AI reply for review” or “Shadow — draft only, never send”. “Use the AI monitor setting” follows the organization’s choice.',
      },
      s8: {
        title: 'Decide what happens with unknown customers',
        body: 'Under “Unknown customers”, pick “Do nothing”, “Ask who they are” or “Ask and create the contact”. Asking sends a short question for the person’s name and phone or email; you can write your own words in “Your own wording (optional)”.',
      },
      s9: {
        title: 'Save',
        body: 'Click “Save & continue”, then “Finish”. The card now shows the team and “AI on” or “AI off”.',
      },
    },
    done: 'The inbox card shows the team you picked and the AI on/off label, and new chats go to that team.',
    tips: {
      t1: 'New to the AI? Start with “Hold every AI reply for review” and approve replies in Automate → “AI monitor” until you trust it.',
      t2: 'These are defaults for NEW conversations only — changing them does not switch the AI on or off in chats that already exist.',
      t3: '“Ask and create the contact” keeps your contact list full without typing, but check the new contacts now and then.',
    },
    trouble:
      'The team list says there are no teams: create one under Admin → “Teams”, then come back. The AI still does not reply: the organization-wide AI switch on the AI monitor may be off, the contact may be set to never use AI, or the agent switched it off in that chat. The AI replies but nothing is sent: the review mode holds replies — approve them in the AI monitor.',
  },
  channelHealth: {
    title: 'Check that your channels are working',
    summary:
      'Read the health labels on your inboxes, check them on demand, and know what to do when a number is disconnected or banned.',
    steps: {
      s1: {
        title: 'Look at the health labels',
        body: 'In the sidebar, open Channels → “Channels”. Every card has a coloured dot and a label: “Healthy”, “Degraded”, “Unknown”, “Disconnected” or “Banned”. Hover the label to read the reason.',
      },
      s2: {
        title: 'Understand the labels',
        body: '“Healthy” works. “Degraded” works with a problem (for example the webhook is not active). “Unknown” has not been checked yet. “Disconnected” cannot send — usually an expired token or an unpaired phone. “Banned” means the platform blocked the account.',
      },
      s3: {
        title: 'Test one inbox now',
        body: 'Click “Test” on a card. A short result appears on the card: green when all is well, red with the reason when not.',
      },
      s4: {
        title: 'Check all of them at once',
        body: 'Open “Dashboard” (the home page). On the “Channel health” card, click “Check now” to check every account; “Manage” takes you back to the Inboxes page.',
      },
      s5: {
        title: 'Fix a disconnected inbox',
        body: 'Click “Settings” on the card, go to the “Connect” step and paste a new token or password (blank fields keep the old one), then save. For a QR-paired WhatsApp number, click “Pair” and scan the code again.',
      },
      s6: {
        title: 'Handle a banned number',
        body: 'Stop using it — sending more will not help. Connect a replacement number, then delete the banned inbox or leave it for its history; its conversations are kept.',
      },
      s7: {
        title: 'Know what failover does',
        body: 'When a number goes “Disconnected” or “Banned”, ChatBranch moves its conversations to another healthy number of the same kind, writes a note in each chat and alerts the admins in the bell. If there is no other number, sends fail and say why.',
      },
      s8: {
        title: 'Set up ban recovery (optional)',
        body: 'Open Work → “Account assignment” → “Routing rules”. Under “Ban recovery”, choose “One-click from notification” or “Automatic”, and pick a “Recovery template” that tells customers your new number.',
      },
    },
    done: 'Every card shows “Healthy”, and you know where to look when one does not.',
    tips: {
      t1: 'Health is checked automatically every few minutes, and an admin gets a bell notification whenever an account goes down or recovers.',
      t2: 'Keep a spare conversation number connected, so failover has somewhere to move chats.',
      t3: 'Saving new credentials resets the label to “Unknown” and checks again in the background — wait a moment or click “Test”.',
    },
    trouble:
      'A card says “Not monitored”: that inbox has no live health check (for example the voice trunk) — use “Test” instead. A label stays “Unknown”: click “Check now” on the Dashboard. “Degraded” on Telegram, LINE or Viber: the webhook is not registered — open “Settings”, go to “Test and finish” and click “Register” again.',
  },
}
