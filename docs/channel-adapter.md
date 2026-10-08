# Channels: how they work, and how to add one

A reference for the channels ChatBranch ships — what each one can actually do
**in the C# backend today**, where its code lives, and what it takes to add
another.

> ## ⚠ Read this first: several transports are not ported yet
>
> The Django backend had live clients for every channel; the C# port does not
> (yet). Today **WhatsApp (Cloud + Baileys), the website widget, Telegram,
> email and SIP calls are live**; **Messenger/Instagram are sandbox-only** —
> and there is **no Messenger/Instagram webhook route at all**:
> `/api/messaging/webhook`, which the Channels setup drawer still shows as the
> callback URL, returns 404. The old "Messenger/IG webhook has no signature
> check" gap is therefore moot until the route is rebuilt — and when it is, it
> must verify `X-Hub-Signature-256` like the WhatsApp webhook does
> (`Integrations/WebhookVerify.Meta`).

---

## 1. What each channel can do

| | WhatsApp Cloud | WhatsApp Baileys | Website widget | Telegram | Messenger / IG | Email |
|---|---|---|---|---|---|---|
| Receive text | ✅ webhook | ✅ gateway push | ✅ widget API | ✅ webhook | ❌ no route | ✅ IMAP pull (`/email/sync`) |
| Send text | ✅ Graph API | ✅ gateway | ✅ (stored; the widget polls them) | ✅ Bot API | ❌ sandbox | ✅ SMTP (threaded replies) |
| Templates | ⚠ sent as rendered text; no Meta listing | rendered as free text | — | — | — | — |
| Send media | ❌ stored locally as sandbox | ❌ | — | ✅ sendPhoto / sendDocument | — | ✅ attachment |
| Receive media | ❌ text only | ❌ `/baileys/media` acked and dropped | — | ✅ getFile, off the ack | — | ✅ attachments (≤5, 10 MB) |
| Delivery receipts | ❌ `statuses` ignored | ✅ `/baileys/message-status` | — | — | — | — |
| Typing / read | ✅ Graph `typing_indicator` | ✅ gateway presence | — | — | — | — |
| 24h window | ✅ enforced (409 → use a template) | none | none | none | not modelled | — |
| Opt-out | honoured; **STOP detection not ported** | same | same | same | same | same |
| Health | ✅ Graph token ping (prober) | ✅ lifecycle pushes + inbound heal | — | ✅ getMe + getWebhookInfo, inbound heal | ✅ `GET /me` (prober) | ✅ SMTP login (prober), send outcomes |
| Voice calls | ❌ (Cloud bridge removed; `call/start` → 501) | ✅ (ToS-violating) | — | — | — | — |
| Auth on inbound | `X-Hub-Signature-256` when the account has an `app_secret` | `X-Baileys-Secret` | anonymous by design (session id) | bot token in path (constant time) + `X-Telegram-Bot-Api-Secret-Token` once registered from the dashboard | — | n/a (we pull over IMAP) |

Where the code is (`backend-csharp/src/`):

| Concern | File |
|---|---|
| Every inbound | `ChatBranch.Data/Channels/InboundProcessor.cs` |
| Every outbound (live vs sandbox) | `ChatBranch.Data/Channels/OutboundSender.cs` |
| Thread/message store | `ChatBranch.Data/Stores/ThreadStore.cs` |
| WhatsApp Cloud client | `ChatBranch.Data/Channels/WhatsAppCloudClient.cs` |
| Baileys gateway client | `ChatBranch.Data/Sidecars/IBaileysGateway.cs` (+ the Node gateway `backend/baileys_service/`) |
| Webhooks (Meta, Baileys) | `ChatBranch.Api/Endpoints/WebhooksEndpoints.cs` |
| Telegram (webhook, Bot API client, send) | `TelegramWebhookEndpoints.cs`, `ChatBranch.Data/Channels/TelegramClient.cs` |
| Email (SMTP send, IMAP pull, settings) | `ChatBranch.Data/Channels/EmailChannel.cs`, `EmailTransport.cs`, `ImapSync.cs`, `EmailSettings.cs`; endpoints `MessagingTransportEndpoints.cs` |
| Non-WhatsApp media files | `ChatBranch.Data/Channels/MessagingMedia.cs` (served at `/api/messaging/media/{id}`) |
| Widget (public + admin) | `WidgetEndpoints.cs`, `WidgetAdminEndpoints.cs` |
| Accounts (WhatsApp / messaging) | `WhatsAppSettingsEndpoints.cs`, `VoiceMessagingEndpoints.cs` |
| Inbox settings (team, AI default, review mode), channel vocabulary for templates | `ChatBranch.Data/Channels/InboxSettings.cs` |
| Per-account delivery (quiet hours, pacing, voice brain/STT) | `ChatBranch.Data/Channels/AccountDelivery.cs` |
| Health writer | `ChatBranch.Data/Channels/AccountHealth.cs` |
| Health prober (periodic + "Check now") | `ChatBranch.Data/Channels/HealthProber.cs`, `GraphHealthProbe.cs`, `ChatBranch.Api/Workers/HealthProbeWorker.cs` |
| Typing indicator | `ChatBranch.Data/Ai/TypingIndicator.cs` |

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
- **Signature:** the webhook verifies `X-Hub-Signature-256` against the account
  matched by `metadata.phone_number_id` **when that account has an
  `app_secret`**. A live account without an app secret accepts unsigned posts —
  always set it.
- **Verify token:** Setting `whatsapp_verify_token` (legacy `verify_token` kept
  in sync), env `WHATSAPP_VERIFY_TOKEN` fallback; edited at Settings → App &
  provider keys and shown with the callback URL under Channels → Advanced.
- **Templates** are the 3 demo templates plus operator-defined ones (Setting
  `whatsapp_custom_templates`); a live send renders the body and sends it as
  TEXT, which Meta refuses outside the 24h window. Template routing prefers a
  `reminder`-role Cloud account with a WABA id.
- `WHATSAPP_*` env keys surface as a read-only `env-default` account.

### WhatsApp over Baileys
- Unofficial WhatsApp Web protocol. **Violates WhatsApp's ToS**; dev/demo only.
  See `backend/baileys_service/README.md` and `RESEARCH.md`.
- No templates (rendered as text), no 24h window. The cold-text guard
  (`cold_text_guard`, default `block`) refuses manual free text from a
  conversation-role number to a contact who never wrote to it; the queue's
  cold-call guard does the same for calls.
- Gateway pushes: `/api/whatsapp/baileys/inbound` (text), `/message-status`
  (ticks), `/status-event` (lifecycle → health), `/call-event` (ringing →
  connected → ended), `/media` (acked, not stored). All require
  `X-Baileys-Secret` (Setting `baileys_secret` → env `BAILEYS_SECRET` →
  `baileys-local-dev` — change it).
- One call at a time per number; outbound only.

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
- No phone number, so CRM linking is manual (the `/chats` contact picker or
  `POST /api/messaging/threads/telegram/{key}/link`). Auto-replies only fire on
  linked threads.

### Messenger / Instagram
- Accounts can be created and stored (page token masked), but there is no
  inbound webhook route and sends are sandbox. "Test" answers 501.

### Email
- Accounts: per-mailbox SMTP creds in `Config` (`host port username password
  use_tls from_email`) plus optional IMAP (`imap_host imap_port imap_username
  imap_password`; blank = the default IMAP host and the mailbox's own SMTP
  login). Passwords never leave the API unmasked. The default mailbox is the
  Setting rows `smtp_*` / `imap_*` / `email_subject` (env `SMTP_*`, `IMAP_*`).
- Out (MailKit): mailbox = thread's account → sticky `AccountAssignment` →
  first routable mailbox (the outlet's own, then shared) → default SMTP →
  sandbox. Port 465 = implicit TLS, otherwise STARTTLS when `use_tls`. Replies
  carry Message-ID / In-Reply-To / References and "Re: <last subject>" (from
  `crm_emailheader`); a flow step's `subject` wins. Refused login →
  disconnected; unreachable server → degraded after 3 in a row; refused
  recipient → health untouched.
- In: `POST /api/messaging/email/sync` (the /chats nudge; real IMAP at most
  every 30 s, `{force:true}` skips it) pulls UNSEEN mail from the last 7 days
  (10 per mailbox per pass), marks it \Seen, keeps only KNOWN senders (a CRM
  contact's email, or an address we already wrote to / a contact-linked
  thread), dedups by Message-ID, strips quoted history conservatively, and runs
  the normal inbound pipeline. A new email thread starts with the chatbot off.
- "Test" = SMTP connect + login (+ a test mail when `to` is given).

---

## 3. The cross-channel machinery

These are channel-blind; a new channel gets them for free if it goes through
`InboundProcessor` and `ThreadStore`.

**Inbound** — `InboundProcessor.ProcessInbound(channel, key, text, name,
accountId, externalId, providerTs)`: get-or-create the thread (entity
attribution), link a contact (email exact, else last-8-digit phone), apply the
inbox's AI default to a brand-new thread, store the message (dedup on external
id), resume flow runs parked on a reply, route to a team, run automations,
notify (assignee → team → admins), expire stale AI drafts, then — behind the
**coverage gate** (linked contact, AI on, not opted out) — the AI reply,
debounced through the queue or instant (`ai_reply_mode` + the AI behaviour
settings). It never throws into the webhook.

**Outbound** — `OutboundSender.Send(thread, text, source, accountId)` is the one
live-or-sandbox seam: a configured Cloud or Baileys account sends for real
(recording a provider failure honestly as a `failed` system message); anything
else stores `status="sandbox"`. Flows and AI never send inline — they enqueue
`OutboundJob`s that `QueueService` drains with per-account pacing, daily caps,
quiet hours and an atomic claim.

**Account health** — `AccountHealth.SetStatus` is the **only** writer of
`ChannelAccount.HealthStatus`, recording one `account.health` audit row (and an
admin `account_health` bell for a problem or a recovery) per real transition.
`healthy`/`degraded` accounts are usable; `disconnected`/`banned` show red on
the Analytics channel-health card. Signals: the Baileys gateway's lifecycle
pushes, inbound traffic, email/Telegram send outcomes, and the periodic prober
(`HealthProbeWorker`, every `WHATSAPP_HEALTH_INTERVAL` s, default 300;
`HEALTH_WORKER=0` disables) which "Check now" also runs. Saving new credentials
resets an account to `unknown` and re-probes it. Per-recipient errors (a closed
window, a bad number, a blocked bot) must never damage account health.

**Assignment** — sticky `AccountAssignment` rows (contact × channel → account,
optionally pinned), edited on `/assignments`, spread evenly by
`POST /api/whatsapp/assignments/rebalance`; the per-chat switch pins one. The
queue uses `job.AccountId ?? thread.AccountId` — automatic least-loaded
assignment and failover at send time are not ported.

**The inbox stream** — `GET /api/whatsapp/stream` (SSE) covers all channels;
its change token is thread `updated_at` + message count, so **any write that
bypasses `ThreadStore` must bump `Thread.UpdatedAt`**.

**The AI** — `AssistantService` is channel-agnostic and every AI-written message
passes `AiGate` (the AI monitor). Skills and knowledge items can be limited to
channels; quick replies (`CannedResponse.Channels`) too.

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

## 5. Deliberately out of scope

- **Voice calls stay transport-specific.** SIP (LiveKit SIP participant) and
  Baileys (`backend/baileys_service/calls.js`) each terminate a different media
  stack into a LiveKit room; the API's `VoiceDispatch` + `LiveKitClient` and the
  voice agent are shared. There is no WhatsApp Cloud call bridge any more.
- **SMS has no provider.** The flow engine's SMS channel logs and skips.
