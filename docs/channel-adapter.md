# Channels: how they work, and how to add one

A reference for the channels ChatBranch ships — what each one can actually do
**in the C# backend today**, where its code lives, and what it takes to add
another.

> ## ⚠ Read this first: live, but not field-tested
>
> **Every channel has a live transport in the C# API** — WhatsApp (Cloud,
> YCloud, Twilio + Baileys), Messenger/Instagram, Telegram, email, the website widget, SIP
> calls, and the newer LINE, Viber, SMS (Twilio), WeChat Official Account,
> TikTok, Douyin and Zalo OA — and each falls back to sandbox (stored locally,
> `status="sandbox"`) when it isn't configured. The provider clients are tested
> against fakes built from the providers' documented payloads, not against the
> live services, so expect field debugging on a first real account. **TikTok
> and Douyin are partner / approval-gated**: built to the documented shapes, no
> one has verified them live. The Messenger/Instagram webhook
> (`/api/messaging/webhook`) **requires** `X-Hub-Signature-256` — the old "no
> signature check" gap is closed — and every newer channel's webhook refuses
> unsigned traffic.

---

## 1. What each channel can do

| | WhatsApp Cloud | WhatsApp Baileys | Website widget | Telegram | Messenger / IG | Email |
|---|---|---|---|---|---|---|
| Receive text | ✅ webhook | ✅ gateway push | ✅ widget API | ✅ webhook | ✅ webhook | ✅ IMAP pull (`/email/sync`) |
| Send text | ✅ Graph API | ✅ gateway | ✅ (stored; the widget polls them) | ✅ Bot API | ✅ Send API (page token) | ✅ SMTP (threaded replies; text + escaped HTML alternative) |
| Templates | ✅ real template messages; live registry (list/create/edit/delete) with a `waba_id` | rendered as free text | — | — | — | — |
| Send media | ✅ upload → send by id | ✅ gateway `send-media` (base64, ≤16 MB) | — | ✅ sendPhoto / sendDocument | ✅ uploaded file (multipart) or public URL | ✅ attachment |
| Receive media | ✅ downloaded off the ack | ✅ `/baileys/media` (two-phase) | — | ✅ getFile, off the ack | ✅ CDN url, downloaded off the ack | ✅ attachments (≤5, 10 MB) |
| AI documents (PDF / QR, after the reply text) | ✅ document / image | ✅ document / image | 🔗 download link (`/api/documents/public/{token}`) | ✅ sendDocument / sendPhoto | ✅ uploaded file | ✅ attachment (a second mail) |
| Delivery receipts | ✅ `statuses` (forward-only; failed + error code) | ✅ `/baileys/message-status` | — | — | ✅ delivery / read | — |
| Typing / read | ✅ Graph `typing_indicator` | ✅ gateway presence | — | ✅ `sendChatAction` (typing) | ✅ `mark_seen` + `typing_on` (24h window only) | — |
| Quoted replies | ✅ in `context.id` · out `context.message_id` | ✅ in `contextInfo.stanzaId` · out gateway `quoted_id` | — | ✅ in `reply_to_message` · out `reply_parameters` | in only (`reply_to.mid`); our replies go unquoted | — |
| Reactions | ✅ in `type: reaction` · out `type: reaction` (24h window, else 409) | ✅ in `reactionMessage` · out gateway `react` | — | ✅ in `message_reaction` · out `setMessageReaction` (Telegram's own emoji set) | — (409) | — (409) |
| 24h window | ✅ enforced (409 → use a template); 131047 closes it | none | none | none | ✅ ≤24h anything; 24h–7d a person only (`HUMAN_AGENT` tag), AI/automation refused; >7d refused | — |
| Opt-out | ✅ STOP/UNSUBSCRIBE/BERHENTI → opted out, START/UNSTOP/MULA → back in | same | same | same | same | same |
| Health | ✅ Graph token ping (prober) + send / status / test results (`MetaErrors`), inbound heal | ✅ lifecycle pushes + inbound heal | — | ✅ getMe + getWebhookInfo, inbound heal | ✅ `GET /me` (prober, test), send results, inbound heal | ✅ SMTP login (prober), send outcomes |
| Voice calls | ❌ (Cloud bridge removed; `call/start` → 501) | ✅ (ToS-violating) + recordings | — | — | — | — |
| Auth on inbound | `X-Hub-Signature-256`, **required once any Cloud account is configured** | `X-Baileys-Secret` | anonymous by design (session id) | bot token in path (constant time) + `X-Telegram-Bot-Api-Secret-Token` once registered from the dashboard | `X-Hub-Signature-256`, **always required** | n/a (we pull over IMAP) |

**WhatsApp through a Business Solution Provider** — YCloud and Twilio are
OFFICIAL business numbers like Cloud (templates, the 24h window, the `reminder`
role by default; `Channels/WhatsAppBsp/WhatsAppProviders.cs` is the one place
that knows what each provider means, `WhatsAppClientRouter` sends every
`IWhatsAppCloudClient` call to the account's provider). One signed webhook per
ACCOUNT, `/api/messaging/webhook/whatsapp-{ycloud,twilio}/{account_id}`
(`Endpoints/WhatsAppBspWebhookEndpoints.cs`; no secret ⇒ 503, bad signature ⇒
401, unknown account ⇒ 404). **Both are built to the providers' public API
docs and tested against fakes only — unverified against the live APIs.**

| | WhatsApp (YCloud) | WhatsApp (Twilio) |
|---|---|---|
| Credentials | API key (`X-API-Key`), the business number (E.164), optional WABA id, the webhook endpoint's signing secret | Account SID + Auth Token (or an API Key SID + secret for REST), the `whatsapp:+…` sender or a Messaging Service SID |
| Receive | `whatsapp.inbound_message.received` — Meta's message shape: text, media (downloaded off the ack from YCloud's `link`, the API key only ever sent to `*.ycloud.com`), location, buttons / interactive, contacts; YCloud's `sendTime` keeps the 24h window honest | the incoming-message form: `Body`, `MediaUrl{n}` (fetched with Basic auth off the ack), location, `ButtonText`; no provider timestamp (receipt time) |
| Send text | `POST /v2/whatsapp/messages/sendDirectly` | `POST /2010-04-01/Accounts/{sid}/Messages.json` |
| Templates | real template messages; live registry (list / create / edit / delete) with a WABA id | Content templates by `ContentSid` + `ContentVariables`; the Content API list is READ-ONLY here (create them in Twilio's Content Template Builder → 409 from our editor) |
| Send media | upload → send by id; a refused upload falls back to a signed public link | Twilio fetches the file: a signed, 6-hour public link `GET /api/media/public/{token}` (`MediaLinks`) — needs `public_base_url` to be a public https address, else an honest failure |
| Delivery receipts | `whatsapp.message.updated` (forward-only; Meta's error code → `MetaErrors`) | `MessageStatus` on the same URL (a per-message `StatusCallback` is stamped when the API knows its public address); Twilio error codes mapped to Meta's (63016 = window closed, 20003 = auth) |
| Quoted replies | ✅ both ways (`context`) | in (`OriginalRepliedMessageSid`); our replies go unquoted |
| Reactions | ✅ both ways | in (never a bubble); out refused (409) — Twilio's API has none |
| Typing / read | ✅ `inboundMessages/{wamid}/typingIndicator` | — (none in Twilio's API) |
| 24h window | ✅ enforced like Cloud (409 → use a template) | ✅ enforced like Cloud |
| Health (Test + prober) | the number's profile (`phoneNumbers/{waba}/{phone}`) else `GET /balance` | `GET /Accounts/{sid}.json` (suspended / closed → health) |
| Auth on inbound | `YCloud-Signature: t=…,s=…` (hex HMAC-SHA256 of `t.body`, 5-min window) | `X-Twilio-Signature` (the Auth Token — an API-key-only account cannot receive) |
| Voice calls | ❌ (`call/start` → 501) | ❌ (`call/start` → 501) |

**Newer channels** — one webhook per ACCOUNT,
`/api/messaging/webhook/{channel}/{account_id}`; no secret configured ⇒ 503,
bad signature ⇒ 401, unknown account ⇒ 404:

| | LINE | Viber | SMS (Twilio) | WeChat OA | TikTok ⚠ | Douyin ⚠ | Zalo OA |
|---|---|---|---|---|---|---|---|
| Receive text | ✅ | ✅ | ✅ | ✅ (plaintext or AES safe mode) | ✅ (unverified) | ✅ (unverified) | ✅ |
| Send text | ✅ reply token (≤50 s) else push | ✅ `send_message` | ✅ ≤1600 chars | ✅ Customer Service message | ✅ (unverified) | ✅ reply to the latest message (unverified) | ✅ consultation message |
| Send media | ❌ public URL only — refused | ❌ public URL only — refused | ❌ MMS needs a public URL — refused | ✅ images | ❌ | ❌ | ✅ images |
| Receive media | ✅ off the ack | ✅ Viber CDN | ✅ MMS (Basic auth) | ✅ `media/get` | ❌ | ❌ | ✅ https URL |
| AI documents (PDF / QR) | 🔗 download link | 🔗 download link | 🔗 download link | ✅ image file (QR); PDF via the transport's media send | 🔗 download link | 🔗 download link | ✅ image file (QR); PDF via the transport's media send |
| Delivery receipts | — | ✅ delivered / seen / failed | — (no status callbacks yet) | — | — | — | — |
| Reply window | none | none | none | 48 h (45015) | 48 h | 48 h | 7 days (-230) |
| Opt-out | STOP words + unfollow | STOP words + unsubscribed | STOP words + Twilio `OptOutType` / 21610 | STOP words + unsubscribe | STOP words | STOP words | STOP words + unfollow |
| Health (Test + prober) | `/v2/bot/info` | `get_account_info` | the Twilio account | the token fetch | `business/get` | client-credential token | `getoa` |
| "Register webhook" | ✅ one click | ✅ one click | paste in Twilio | paste in the WeChat console | paste | paste | paste |
| Auth on inbound | `X-Line-Signature` | `X-Viber-Content-Signature` | `X-Twilio-Signature` | sha1 signature + 5-min freshness + single-use nonce (+ AES `msg_signature`) | `TikTok-Signature` (5-min window) | `X-Douyin-Signature` | `X-ZEvent-Signature` |

Where the code is (`backend-csharp/src/`):

| Concern | File |
|---|---|
| Every inbound | `ChatBranch.Data/Channels/InboundProcessor.cs` |
| Every outbound (live vs sandbox) | `ChatBranch.Data/Channels/OutboundSender.cs` |
| Thread/message store | `ChatBranch.Data/Stores/ThreadStore.cs` |
| WhatsApp Cloud client (text, templates, media, template registry) | `ChatBranch.Data/Channels/WhatsAppCloudClient.cs` |
| Messenger / Instagram Send API client | `ChatBranch.Data/Channels/MetaMessagingClient.cs` |
| Meta webhook parsing + delivery statuses, error classification, templates | `Channels/MetaWebhook.cs`, `Channels/MetaErrors.cs`, `Channels/MetaTemplates.cs` |
| Media + call recordings on disk, inbound downloads | `Channels/WhatsAppMediaStore.cs`, `Channels/WhatsAppMediaRecordings.cs`, `Channels/WhatsAppMediaFetcher.cs` |
| Media / recording routes | `ChatBranch.Api/Endpoints/WhatsAppMediaEndpoints.cs` |
| Baileys gateway client | `ChatBranch.Data/Sidecars/IBaileysGateway.cs` (+ the Node gateway `backend/baileys_service/`; outbound-media checks in `media.js`) |
| Messenger / Instagram messaging window | `ChatBranch.Data/Channels/SocialWindow.cs` (enforced in `OutboundSender`, `AiGate.SendTimeBlock`, `QueueService`, the messaging send routes) |
| Webhooks (Meta Cloud, Baileys) | `ChatBranch.Api/Endpoints/WebhooksEndpoints.cs` |
| Messenger / Instagram webhook + the shared Meta verify/signature rules | `ChatBranch.Api/Endpoints/MetaWebhookEndpoints.cs` |
| Telegram (webhook, Bot API client, send) | `TelegramWebhookEndpoints.cs`, `ChatBranch.Data/Channels/TelegramClient.cs` |
| Email (SMTP send, IMAP pull, settings) | `ChatBranch.Data/Channels/EmailChannel.cs`, `EmailTransport.cs`, `ImapSync.cs`, `EmailSettings.cs`; endpoints `MessagingTransportEndpoints.cs` |
| Media files | `ChatBranch.Data/Channels/WhatsAppMediaStore.cs` (`MediaStore`), `MessagingMedia.cs`; served by `WhatsAppMediaEndpoints.cs` |
| Widget (public + admin) | `WidgetEndpoints.cs`, `WidgetAdminEndpoints.cs` |
| Accounts (WhatsApp / messaging) | `WhatsAppSettingsEndpoints.cs`, `VoiceMessagingEndpoints.cs` |
| Inbox settings (team, AI default, review mode), channel vocabulary for templates | `ChatBranch.Data/Channels/InboxSettings.cs` |
| Per-account delivery (quiet hours, pacing, voice brain/STT) | `ChatBranch.Data/Channels/AccountDelivery.cs` |
| Health writer | `ChatBranch.Data/Channels/AccountHealth.cs` |
| Health prober (periodic + "Check now") | `ChatBranch.Data/Channels/HealthProber.cs`, `GraphHealthProbe.cs`, `ChatBranch.Api/Workers/HealthProbeWorker.cs` |
| Typing indicator | `ChatBranch.Data/Ai/TypingIndicator.cs` (`ChannelTypingIndicator` → `LiveTypingIndicator` / `TelegramTypingIndicator` / `MetaTypingIndicator`) |
| Quoted replies + reactions | `ChatBranch.Data/Channels/MessageReactions.cs` (store + `MessageCards`), `ThreadStore.AppendMessage(replyToExternalId)`, `OutboundSender.Send(replyTo)` / `.React`, `ChatBranch.Api/Endpoints/MessageReactionEndpoints.cs`, `MessageExtras.cs` (payload `reply_to` / `reactions`), `backend/baileys_service/messages.js` |
| Newer channels: registry, transports, send seam, reply windows | `ChatBranch.Data/Channels/More/` — `MoreChannels.cs` (which channels, configured?, account-bound?, phone-keyed?, `IChannelTransport`, `ChannelOptOut`), `LineTransport.cs`, `ViberTransport.cs`, `TwilioSmsTransport.cs`, `WeChatTransport.cs`, `TikTokTransport.cs`, `DouyinTransport.cs`, `ZaloTransport.cs`, `MoreChannelSender.cs`, `ChannelWindow.cs` |
| Newer channels: webhooks + DI, account fields | `ChatBranch.Api/Endpoints/MoreChannelWebhookEndpoints.cs`, `MoreChannelAccounts.cs` |
| Invite links, unknown customers, identities | `ChatBranch.Data/Contacts/ContactInvites.cs`, `InviteReferrals.cs`, `IdentityRequests.cs`, `ContactIdentities.cs`; endpoints `ContactIdentityEndpoints.cs` |

All channels write to the **same** `crm_thread` / `crm_message` tables, so the
inbox, the SSE stream, the AI, teams, automations, the flow engine and the
outbound queue are already channel-blind.

---

## 2. Per-channel notes that will bite you

### WhatsApp Cloud API
- **The 24-hour window is derived at read time** from the newest inbound
  message; free text and media into a closed window return **409 — use a
  template**. Inbound rows store **Meta's own timestamp**, not arrival time —
  Meta's retry backlog can deliver a webhook hours late.
- **Signature:** `X-Hub-Signature-256` is **required once any Cloud account is
  configured** (a row with token + phone_number_id, or the env-default
  `WHATSAPP_*` pair) and is verified against the known Meta app secrets (the
  accounts' `app_secret`, Setting `meta_app_secret`, env `META_APP_SECRET` /
  `WHATSAPP_APP_SECRET`). A live setup with no app secret answers 401. Only a
  fully unconfigured (sandbox) install accepts unsigned posts.
- **Delivery statuses** (`statuses[]`) update the outbound message by wamid —
  forward-only (sent → delivered → read), `failed` stores Meta's code in
  `Message.Error` (the bubble's `error`). **131047** closes the window (inbound
  timestamps capped; a thread that has since moved to another number keeps that
  number's replies). Account-level codes (190, 131031, 368, 133010, rate
  limits…) move health through `AccountHealth.SetStatus`; per-recipient codes
  (131047, 131026, 131050, …) never do — `Channels/MetaErrors.cs` is the table.
- **Media:** inbound media is stored with a text placeholder and downloaded off
  the ack (`GET /{media-id}` → the lookaside URL, both with the bearer token,
  20 MB cap) into `data/whatsapp/media/{wa_id}/`; outbound media uploads to
  `/{phone_number_id}/media` and sends by id. Served by `/api/whatsapp/media/{id}`.
- **Verify token:** Setting `whatsapp_verify_token` (legacy `verify_token` kept
  in sync), env `WHATSAPP_VERIFY_TOKEN` fallback; edited at Settings → App &
  provider keys and shown with the callback URL under Channels → Advanced.
- **Templates:** a configured Cloud number sends a real `type: "template"`
  message (name, language, header/body parameters from the variables, dynamic
  URL-button parameters) and the inbox stores the rendered body. With a
  `waba_id` the registry is Meta's (`GET /{waba_id}/message_templates`, paged,
  cached 5 min; create/edit/delete go to Graph) merged with operator custom ones
  (Setting `whatsapp_custom_templates`); sandbox is the 3 demo templates + custom.
  The composer, flows (`parameters`), business redirect and recovery all go
  through `OutboundSender.SendTemplate`. Template routing prefers a
  `reminder`-role Cloud account with a WABA id.
- `WHATSAPP_*` env keys surface as a read-only `env-default` account.
- **Quoted replies + reactions:** an inbound `context.id` is stored as
  `Message.ReplyToExternalId` and resolved to `Message.ReplyToId` when we hold
  that wamid; a reply from the inbox (`reply_to` = the quoted pk on `…/send`)
  goes out with `context.message_id`. An inbound `type: reaction` lands in
  `crm_messagereaction` on the message it targets (`MetaWebhook.CloudReactions`
  → `MessageReactions.ApplyInbound`; an empty emoji removes it) — never a
  bubble, never the AI / flows / automations, never a reopen; it only bumps
  `Thread.UpdatedAt`. Our reaction (`POST /api/messaging/threads/whatsapp/{key}/messages/{pk}/react`)
  is a `type: reaction` message — Meta refuses one outside the 24h window, so the
  route answers 409 first.

### WhatsApp through YCloud / Twilio
- Add them from Channels → "WhatsApp via YCloud" / "WhatsApp via Twilio"; the
  test step shows the account's own webhook URL to paste into the provider.
  YCloud's signing secret only exists once you create the endpoint with that
  URL, so the order is: save → paste the URL in YCloud → copy its secret back
  into step 2. Until then inbound is refused (503), never accepted unsigned.
- They never touch Meta's `/api/whatsapp/webhook`: that route and the Meta
  app-secret list only look at `provider = cloud` rows (a YCloud row's
  `app_secret` is its webhook secret, not a Meta credential).
- Routing treats every OFFICIAL number alike: `RouteNeed.Template` asks for
  `WhatsAppProviders.AnyOfficial` (cloud / ycloud / twilio), never Baileys.
- Twilio takes media by URL only. The signed link names one stored file for 6
  hours (key in Setting `media_link_secret`) and resolves under the media root.

### WhatsApp over Baileys
- Unofficial WhatsApp Web protocol. **Violates WhatsApp's ToS**; dev/demo only.
  See `backend/baileys_service/README.md` and `RESEARCH.md`.
- No templates (rendered as text), no 24h window. The cold-text guard
  (`cold_text_guard`, default `block`) refuses free text — manual sends and
  queued text jobs alike — from a live conversation-role number to a contact who
  never wrote to it; the queue's cold-call guard does the same for calls.
- Gateway pushes: `/api/whatsapp/baileys/inbound` (text / media placeholder),
  `/message-status` (ticks), `/status-event` (lifecycle → health), `/call-event`
  (ringing → connected → ended; `ended` writes the recording's paired transcript
  `{wa_id}-{ms}.json`), `/media` (multipart `wa_id`, `msg_id`, `media_type`,
  `file` — attached to the placeholder message, 20 MB cap). All require
  `X-Baileys-Secret` (Setting `baileys_secret` → env `BAILEYS_SECRET` →
  `baileys-local-dev` — change it).
- Outbound media: the gateway's `POST /accounts/{id}/send-media` (`{to,
  media_type, file_b64, mime, filename, caption}`, ≤16 MB, base64 in the body —
  the gateway never reads a path or fetches a URL; `media.js` checks the payload
  and is self-tested). A refusal is a failed row with the gateway's reason. The
  cold-text guard covers files as well as text.
- Call recordings: `GET /api/whatsapp/threads/{key}/call/recordings` lists the
  gateway's `data/whatsapp/calls/recordings/{wa_id}-{ms}.wav` (else the voice
  agent's `data/recordings/{room}.wav`) with the transcript and who started /
  ended the call; `GET /api/whatsapp/calls/recording/{name}` streams the WAV
  (Range supported, name-validated).
- One call at a time per number; outbound only.
- **Quoted replies + reactions** (`backend/baileys_service/messages.js`, self-tested):
  the gateway pushes `reply_to` (the quoted `contextInfo.stanzaId`) with an
  inbound, and a `reactionMessage` as `{reaction: {msg_id, emoji}}` on
  `/inbound` (stored as a reaction, never a bubble). Outbound: `POST
  /accounts/{id}/send` takes `quoted_id` / `quoted_from_me` / `quoted_text` (the
  gateway quotes the real message from a 2 000-entry key map, else rebuilds a
  minimal one from that text), `POST /accounts/{id}/react` `{to, msg_id,
  from_me, emoji}` sends a reaction (`""` removes ours). Locations and contact
  cards arrive as `[location] lat,lng label` / `[contact] Name · +phone`.
- **Number lookups:** the contact identity checker asks a connected QR number
  whether a contact's phone is on WhatsApp (`IBaileysGateway.CheckNumberAsync` →
  the gateway's `POST /accounts/{id}/check` → `{exists, jid}`; nothing is ever
  sent). `Workers/IdentityCheckWorker` paces it at ≤1 lookup per 2 s per gateway
  and caches the verdict 30 days; Cloud numbers have no lookup.

### Website widget
- A widget IS a `ChannelAccount` (channel `website`) whose `external_id` is the
  public token in the script tag. Public endpoints are anonymous with a
  permissive CORS policy; a visitor's conversation is keyed by a server-minted
  128-bit session id. The script renders into a shadow root.
- The AI coverage gate is relaxed for the widget: a visitor needs no linked
  contact, but AI must be on.

### Telegram
- Inbound: `/api/messaging/webhook/telegram/{bot-token}` — the token in the path
  routes to the bot (compared in constant time; unknown → 404). "Register
  webhook" (`POST /api/messaging/accounts/{id}/set-webhook`) calls setWebhook
  with a fresh `secret_token`, stored as `config.webhook_secret`; from then on
  `X-Telegram-Bot-Api-Secret-Token` must match (401 otherwise). The public base
  is the body's `base_url` → Setting `public_base_url` → env `PUBLIC_BASE_URL`
  → the request's forwarded origin, and must be https. The answer redacts the
  token. Media (photo, document, voice, video, static sticker) is fetched with
  getFile off the ack and attached when it lands (20 MB cap).
- Outbound: `TelegramChannel` — sendMessage (split at 4096 chars), sendPhoto /
  sendDocument / sendAudio / sendVideo for attachments. A 401/404 from the Bot
  API marks the bot disconnected; 400/403 (chat not found, bot blocked) is the
  recipient's problem and leaves health alone. "Test" = getMe (+ webhook state).
  The bot is the thread's own — a Telegram chat only works with the bot the
  customer started, so there is no failover; a bot-less thread takes the
  contact's sticky / least-loaded / sole bot (`Routing.ForThread`). The AI's
  typing pause shows "typing…" (`sendChatAction`, fire-and-forget,
  `TelegramTypingIndicator`).
- Quoted replies: an inbound `reply_to_message.message_id` resolves
  `Message.ReplyToId`; a reply from the inbox sends `reply_parameters`
  (`allow_sending_without_reply`, first chunk only). Reactions: a
  `message_reaction` update (setWebhook now asks for `message_reaction` too — press
  "Register webhook" again on an older bot) lands on the message, never a bubble;
  ours is `setMessageReaction` — Telegram only accepts its own emoji set and a
  refused one comes back as its reason (502).
- No phone number, so a Telegram chat links to a contact by hand (the `/chats`
  contact picker or `POST /api/messaging/threads/telegram/{key}/link`, a contact
  in the caller's scope), through an **invite link** (`t.me/<bot>?start=CODE`
  from the contact's "Invite to another app" — the `/start CODE` binds it), or
  through the **unknown-customer exchange** when the inbox asks who they are.
  Auto-replies only fire on linked threads.

### Messenger / Instagram
- Webhook `GET/POST /api/messaging/webhook` (objects `page` / `instagram`),
  same verify token as WhatsApp. The POST **always** needs a valid
  `X-Hub-Signature-256` — the page account's `app_secret` (or any known Meta app
  secret); none configured ⇒ 401. Events route by the entry id to the account
  whose `external_id` is that page / Instagram account id; the thread key is the
  PSID / IGSID (name fetched once). Redelivered mids are skipped; attachments
  download from the CDN off the ack (one media per message row); delivery / read
  receipts tick our outbound messages; echoes are ignored. An invite link
  `m.me/<page>?ref=CODE` arrives as a referral (`messaging_referrals`,
  `postback.referral` or `message.referral`) and binds the conversation to the
  invited contact (`Contacts/InviteReferrals.cs`); Instagram has no referral, so
  its invite asks the customer to type `ref CODE`.
- Sends go through the Send API (`POST /me/messages`, page token): text, an
  attachment by public URL (`media_url` on the messaging send route) or an
  uploaded file (`POST /api/messaging/threads/{facebook|instagram}/{key}/send-media`
  → multipart `recipient` / `messaging_type` / `message` / `filedata`; a caption
  goes out as the next text message). "Test" calls `GET /me` and moves health
  (ok → healthy, refused → disconnected).
- **Messaging window** (`Channels/SocialWindow.cs`, from the newest inbound row,
  live pages only): within 24h anything (`RESPONSE`); 24h–7 days only a person —
  the inbox routes send `MESSAGE_TAG` + `HUMAN_AGENT` for a signed-in user, while
  AI replies, follow-ups, flow/automation jobs, API keys and MCP clients are
  refused (409 / failed job / blocked AI draft); beyond 7 days, or before the
  customer ever wrote, nothing goes out. The thread payload carries
  `window_open`, `window_expires_at`, `human_window_open`,
  `human_window_expires_at`; the inbox `window=open|closed` filter and the
  channel hub use the same rule. A HUMAN_AGENT send refused for permissions (the
  app lacks Meta's Human Agent feature) fails without touching health.
- The app secret is set in the setup drawer (or `PATCH
  /api/messaging/accounts/{id}` `app_secret`, Setting `meta_app_secret`, or the
  WhatsApp account's app secret when it is the same Meta app).

### Email
- Accounts: per-mailbox SMTP creds in `Config` (`host port username password
  use_tls from_email`) plus optional IMAP (`imap_host imap_port imap_username
  imap_password`; blank = the default IMAP host and the mailbox's own SMTP
  login). Passwords never leave the API unmasked. The default mailbox is the
  Setting rows `smtp_*` / `imap_*` / `email_subject` (env `SMTP_*`, `IMAP_*`).
- Out (MailKit): mailbox = the shared send-time routing (`Routing.ForThread`,
  channel `email`): the thread's healthy mailbox → the contact's sticky
  `AccountAssignment` → least-loaded auto-assign (an unpinned row; a
  contactless conversation also takes the least-loaded mailbox, without a row)
  → failover off a disconnected/banned mailbox (row moves, pin cleared, thread
  moves, system note + `account.failover` audit + admin notification), the
  outlet's own mailboxes first; every eligible mailbox down ⇒ a failed message
  naming it (nothing sent); no configured mailbox ⇒ default SMTP ⇒ sandbox.
  Every mail is multipart/alternative — the text as typed + an HTML rendering
  of it (`EmailHtml`: everything escaped, paragraphs / line breaks, http(s) and
  email auto-links, never raw HTML) — wrapped in multipart/mixed with
  attachments; AI mail carries `Auto-Submitted: auto-replied`. Port 465 = implicit TLS, otherwise STARTTLS when `use_tls`. Replies
  carry Message-ID / In-Reply-To / References and "Re: <last subject>" (from
  `crm_emailheader`); a flow step's `subject` wins. Refused login →
  disconnected; unreachable server → degraded after 3 in a row; refused
  recipient → health untouched.
- In: `POST /api/messaging/email/sync` (the /chats nudge; real IMAP at most
  every 30 s, `{force:true}` skips it) pulls UNSEEN mail from the last 7 days
  (10 per mailbox per pass), marks it \Seen, keeps only KNOWN senders (a CRM
  contact's email, or an address we already wrote to / a contact-linked
  thread), dedups by Message-ID, strips quoted history conservatively, flattens HTML-only mail to readable
  text (scripts/styles/comments dropped, line breaks + paragraphs kept, links
  keep their URL, entities decoded), and runs
  the normal inbound pipeline. A new email thread starts with the chatbot off.
- "Test" = SMTP connect + login (+ a test mail when `to` is given).

### Newer channels (LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo)
- **One shape.** Each is an `IChannelTransport` (`Channels/More/`: send text,
  send media, probe, download) registered in
  `MoreChannelWebhookEndpoints.AddMoreChannels`; `MoreChannelSender` (called by
  `OutboundSender`) picks the account with the shared routing, checks the reply
  window (`ChannelWindow`), calls the provider and records the outcome honestly
  — `sent` + the provider id, or a failed system row; health moves only for
  account-level failures (a refused token, a suspension, repeated outages),
  never a recipient's. No configured account ⇒ `status="sandbox"`.
- **Webhooks are per account** (`/api/messaging/webhook/{channel}/{account_id}`)
  so each request is verified with exactly that account's secret; garbage and
  events the inbox doesn't use are acked 200. Verified traffic runs the one
  inbound path, dedups on the provider's message id, heals health and downloads
  media off the ack. The setup drawer's test step shows the URL; LINE and Viber
  can register it in one click (`POST /api/messaging/accounts/{id}/set-webhook`).
- **Who the conversation belongs to.** LINE, Viber, WeChat, TikTok, Douyin and
  Zalo identities are minted by the receiving account, so those conversations
  are account-bound (no failover, no spreading). SMS is phone-keyed: the thread
  key is the E.164 digits, contacts link by last-8 digits like WhatsApp, and any
  of our numbers can carry it (spread like email mailboxes).
- **Opt-out** follows the platform too (`ChannelOptOut`): Viber `unsubscribed`,
  LINE / WeChat / Zalo unfollow (a re-follow, re-subscribe or Viber `subscribed`
  opts back in), Twilio's `OptOutType` STOP/START, and a Twilio 21610 on send.
- **Referral payloads** that platforms pass on a first contact (a Viber
  `conversation_started` context, a WeChat parametric-QR scene) ride on the
  first message as a `[ref:CODE] ` prefix, which the invite binder reads.
- **Per-channel edges.** LINE replies with the webhook's reply token while it is
  fresh (≤50 s, free) and falls back to push (may cost quota). Viber ids are
  base64, so the thread key is their URL-safe form. Twilio texts are capped at
  1600 chars per message. WeChat caches its access_token ~7000 s and refetches
  once on expiry; a wrong AppSecret or a server IP missing from the whitelist
  disconnects the account. Zalo refreshes an expired access token ONCE with the
  refresh token and stores the rotated pair. TikTok and Douyin are text-only and
  unverified; Douyin replies quote the customer's latest message id and has no
  token refresh yet.

---

## 3. The cross-channel machinery

These are channel-blind; a new channel gets them for free if it goes through
`InboundProcessor` and `ThreadStore`.

**Inbound** — `InboundProcessor.ProcessInbound(channel, key, text, name,
accountId, externalId, providerTs)`: get-or-create the thread (entity
attribution), run the **invite binder** (`Contacts/ContactInvites.cs`: a valid,
unexpired code in `[ref:CODE]`, `(ref:CODE)`, `(ref CODE)`, a loose `ref CODE`
or Telegram's `/start CODE` links the invited contact BEFORE any matching; the
marker is
stripped from the stored text, the original kept in the note, the use audited as
`contact.invite_used`; unknown / expired / spent codes are ignored silently —
**a channel whose platform passes a referral payload should prefix the first
message with `[ref:<payload>] `**, as Viber and WeChat do, and Messenger binds
`referral.ref` in its webhook), link a contact (email exact, else last-8-digit
phone on the phone-keyed channels, WhatsApp and SMS), move the conversation
onto the account that received it, apply the inbox's AI default to a brand-new
thread, store the message (dedup on external id), apply STOP/START
(`Channels/OptOut.cs`: the whole message is the keyword; org Settings
`opt_out_keywords` / `opt_in_keywords` add words; opting out cancels the
conversation's queued jobs, writes a private note and an audit row, and sends
nothing back; the newer channels' platform signals do the same through
`ChannelOptOut`), resume flow runs parked on a reply, route to a team, run
automations, notify (assignee → team → admins), the **unknown-customer step**
(`Contacts/IdentityRequests.cs`, when the inbox's `identify_unknown` or an
`ask_identity` automation asks: a deterministic EN/BM "who are you?" exchange
that links, creates or hands over), expire stale AI drafts, then —
behind the **coverage gate** (linked contact, the effective chatbot verdict from
`Ai/AiEligibility.Decide` — the same rule every `ai_enabled` payload shows — and
not opted out) — the AI reply, debounced through the queue or instant
(`ai_reply_mode` + the AI behaviour settings); with the chatbot off on a
WhatsApp reminder (business) number, the business redirect instead. It never
throws into the webhook. Manual sends to an opted-out conversation need
`{"force": true}` on every send endpoint; templates are refused outright.

**Outbound** — `OutboundSender.Send(thread, text, source, accountId)` is the one
live-or-sandbox seam (with `SendTemplate` / `SendMedia` beside it): a configured
Cloud, Baileys, Messenger, Instagram, Telegram, email or newer-channel account
(through `MoreChannelSender`) sends for real
(recording a provider failure honestly as a `failed` system message); anything
else stores `status="sandbox"`. Flows and AI never send inline — they enqueue
`OutboundJob`s that `QueueService` drains with per-account pacing, daily caps,
quiet hours and an atomic claim.

**Account health** — `AccountHealth.SetStatus` is the **only** writer of
`ChannelAccount.HealthStatus`, recording one `account.health` audit row (and an
admin `account_health` bell for a problem or a recovery) per real transition.
`healthy`/`degraded` accounts are usable; `disconnected`/`banned` show red on
the Dashboard channel-health card. Signals: the Baileys gateway's lifecycle
pushes, inbound traffic, send outcomes (Meta results classified by
`Channels/MetaErrors.cs`; email/Telegram by their transports), and the periodic
prober (`HealthProbeWorker`, every `WHATSAPP_HEALTH_INTERVAL` s, default 300;
`HEALTH_WORKER=0` disables) which "Check now" also runs. Saving new credentials
resets an account to `unknown` and re-probes it. Per-recipient errors (a closed
window, a bad number, a blocked bot) must never damage account health.

**Assignment** — sticky `AccountAssignment` rows (contact × channel → account,
optionally pinned), edited on `/assignments`, spread evenly by
`POST /api/whatsapp/assignments/rebalance`; the per-chat switch pins one.
Routing happens AT SEND TIME (`Channels/Routing.cs`, used by the queue drain,
the manual send endpoints, the email/Telegram account choice and the AI gate —
instant, approved and auto-sent AI replies, follow-ups; a job's payload
`account_id` pins it): the thread's
healthy number → the sticky row → least-loaded auto-assign (writes an unpinned
row) → failover when the number is banned/disconnected (row moves and loses its
pin, the thread moves, a system note + `account.failover` audit + admin
notification). Eligible = configured and not down, among the conversation's
entity's own numbers (else the organization-wide ones); WhatsApp text/AI never
fails over onto a reminder number when conversation numbers exist; templates
need a Cloud number (reminder first), are carried per send without moving the
sticky row and never fall back to Baileys — nothing eligible fails the job / 409s
the endpoint. With no configured account nothing is routed (sandbox). Roles,
providers and templates are WhatsApp-only (`RouteNeed.For`); Telegram,
Messenger/Instagram, widget, LINE, Viber, WeChat, TikTok, Douyin and Zalo
conversations stay on the account that received them (account-bound — no
failover or spreading); email and SMS spread even a contactless conversation to
the least-loaded mailbox / number. A
conversation number going INTO banned/disconnected notifies admins and, with
`recovery_mode=auto`, queues `recovery_template` for its contacts
(`Channels/Recovery.cs`, hooked in `AccountHealth.SetStatus`).

**The inbox stream** — `GET /api/whatsapp/stream` (SSE) covers all channels;
its change token is thread `updated_at` + message count, so **any write that
bypasses `ThreadStore` must bump `Thread.UpdatedAt`**.

**The AI** — `AssistantService` is channel-agnostic and every AI-written message
passes `AiGate` (the AI monitor). Skills, knowledge items and lessons can be
limited to channels; quick replies (`CannedResponse.Channels`) too.

---

## 4. Adding a channel

There is no plugin system or adapter registry — a channel is a code review. The
work, in order:

1. **Name it.** Add the id to `ChatBranch.Domain/Enums.cs` `Channels` and to
   `InboxSettings.TemplateChannels.All` (so templates and skills can target it).
2. **Inbound.** Add a route in `WebhooksEndpoints.cs` with REAL authentication
   (a signature via `WebhookVerify`, a shared secret, or a token in the path),
   explicitly `AllowAnonymous()`, that parses the payload and calls
   `InboundProcessor.ProcessInbound(...)` with the provider's message id as the
   external id (that is the redelivery guarantee). Ack garbage with 200; never
   500 a batch.
3. **Outbound.** Add a client (an interface in `ChatBranch.Data/Channels/`
   registered in `Program.cs`, sandbox when unconfigured) and a branch in
   `OutboundSender.Send` / `WouldSendLive`.
4. **Accounts.** Reuse `ChannelAccount` (`external_id`, `access_token`,
   `config`); mask secrets on the way out and treat `••` as "unchanged" on the
   way in.
5. **Health.** Call `AccountHealth.SetStatus` from the transport's own signals.
6. **Dashboard.** Add an `InboxKind` + setup fields
   (`components/channels/inboxes.ts`, `setupSpec.ts`, `InboxSetupDrawer.tsx`),
   a mark in `components/ui/ChannelLogo.tsx`, the channel in
   `components/chats/inboxTypes.ts` `CHANNELS`/`CHANNEL_META`, and — if flows
   should trigger on it — `MESSAGE_CHANNELS` in `lib/flow/nodeConfig.ts`.
7. **Sandbox.** Unconfigured must store locally with `status="sandbox"` and never
   raise. This is a hard rule of the template.
8. **Docs.** Update the Port status table in `CLAUDE.md`, this file and the
   README's channel table.

**The shortcut for a token-and-webhook messaging app** is the shape the seven
newer channels share (`Channels/More/`): implement `IChannelTransport` (send
text, send media — or refuse it honestly —, probe, download), register it in
`MoreChannelWebhookEndpoints.AddMoreChannels`, add the id to `MoreChannels.All`
(and to `Configured` / `AccountBound` / `PhoneKeyed`), its field table to
`MoreChannelAccounts.Fields`, a per-account webhook handler in
`MoreChannelWebhookEndpoints` (503 without a secret, 401 on a bad signature,
404 for an unknown account) and, if the platform has a reply window, a
`ChannelWindow` rule. Routing, the health prober, the accounts API, the send
seam (`MoreChannelSender`) and the window checks then pick it up without
enumerating channel ids. Steps 1, 6, 7 and 8 above still apply.

## 5. Deliberately out of scope

- **Voice calls stay transport-specific.** SIP (LiveKit SIP participant) and
  Baileys (`backend/baileys_service/calls.js`) each terminate a different media
  stack into a LiveKit room; the API's `VoiceDispatch` + `LiveKitClient` and the
  voice agent are shared. There is no WhatsApp Cloud call bridge any more.
- **TikTok and Douyin are partner / approval-gated.** Their APIs only work once
  the platform approves the app, so they were built to the documented shapes
  (every endpoint path in one class each) and have never run against the live
  services; the inbox marks them "Requires approval". Treat a first account as
  the integration test.
