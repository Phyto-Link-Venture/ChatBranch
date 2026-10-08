# Channels: how they work, and how to add one

A reference for the channels ChatBranch ships — what each one can actually do
**in the C# backend today**, where its code lives, and what it takes to add
another.

> ## ⚠ Read this first: several transports are not ported yet
>
> The Django backend had live clients for every channel; the C# port does not
> (yet). Today **WhatsApp (Cloud + Baileys), Messenger/Instagram, the website
> widget and SIP calls are live**; Telegram is inbound-only; **email is
> sandbox-only**. The Messenger/Instagram webhook (`/api/messaging/webhook`)
> **requires** `X-Hub-Signature-256` — the old "no signature check" gap is
> closed.

---

## 1. What each channel can do

| | WhatsApp Cloud | WhatsApp Baileys | Website widget | Telegram | Messenger / IG | Email |
|---|---|---|---|---|---|---|
| Receive text | ✅ webhook | ✅ gateway push | ✅ widget API | ✅ webhook | ✅ webhook | ❌ IMAP sync is a stub |
| Send text | ✅ Graph API | ✅ gateway | ✅ (stored; the widget polls them) | ❌ sandbox | ✅ Send API (page token) | ❌ sandbox |
| Templates | ✅ real template messages; live registry (list/create/edit/delete) with a `waba_id` | rendered as free text | — | — | — | — |
| Send media | ✅ upload → send by id | ❌ stored locally (no gateway route) | — | — | ✅ by public URL | — |
| Receive media | ✅ downloaded off the ack | ✅ `/baileys/media` (two-phase) | — | ❌ | ✅ CDN url, downloaded off the ack | — |
| Delivery receipts | ✅ `statuses` (forward-only; failed + error code) | ✅ `/baileys/message-status` | — | — | ✅ delivery / read | — |
| Typing / read | ✅ Graph `typing_indicator` | ✅ gateway presence | — | — | — | — |
| 24h window | ✅ enforced (409 → use a template); 131047 closes it | none | none | none | not modelled (refusal = failed send) | — |
| Opt-out | honoured; **STOP detection not ported** | same | same | same | same | same |
| Health | send / status / test results (`MetaErrors`), inbound heal | ✅ lifecycle pushes + inbound heal | — | — | send / test (`GET /me`), inbound heal | — |
| Voice calls | ❌ (Cloud bridge removed; `call/start` → 501) | ✅ (ToS-violating) + recordings | — | — | — | — |
| Auth on inbound | `X-Hub-Signature-256`, **required once any Cloud account is configured** | `X-Baileys-Secret` | anonymous by design (session id) | bot token in path | `X-Hub-Signature-256`, **always required** | — |

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
| Baileys gateway client | `ChatBranch.Data/Sidecars/IBaileysGateway.cs` (+ the Node gateway `backend/baileys_service/`) |
| Webhooks (Meta Cloud, Telegram, Baileys, email-sync stub) | `ChatBranch.Api/Endpoints/WebhooksEndpoints.cs` |
| Messenger / Instagram webhook + the shared Meta verify/signature rules | `ChatBranch.Api/Endpoints/MetaWebhookEndpoints.cs` |
| Widget (public + admin) | `WidgetEndpoints.cs`, `WidgetAdminEndpoints.cs` |
| Accounts (WhatsApp / messaging) | `WhatsAppSettingsEndpoints.cs`, `VoiceMessagingEndpoints.cs` |
| Inbox settings (team, AI default, review mode), channel vocabulary for templates | `ChatBranch.Data/Channels/InboxSettings.cs` |
| Per-account delivery (quiet hours, pacing, voice brain/STT) | `ChatBranch.Data/Channels/AccountDelivery.cs` |
| Health writer | `ChatBranch.Data/Channels/AccountHealth.cs` |
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

### WhatsApp over Baileys
- Unofficial WhatsApp Web protocol. **Violates WhatsApp's ToS**; dev/demo only.
  See `backend/baileys_service/README.md` and `RESEARCH.md`.
- No templates (rendered as text), no 24h window. The cold-text guard
  (`cold_text_guard`, default `block`) refuses manual free text from a
  conversation-role number to a contact who never wrote to it; the queue's
  cold-call guard does the same for calls.
- Gateway pushes: `/api/whatsapp/baileys/inbound` (text / media placeholder),
  `/message-status` (ticks), `/status-event` (lifecycle → health), `/call-event`
  (ringing → connected → ended; `ended` writes the recording's paired transcript
  `{wa_id}-{ms}.json`), `/media` (multipart `wa_id`, `msg_id`, `media_type`,
  `file` — attached to the placeholder message, 20 MB cap). All require
  `X-Baileys-Secret` (Setting `baileys_secret` → env `BAILEYS_SECRET` →
  `baileys-local-dev` — change it).
- Call recordings: `GET /api/whatsapp/threads/{key}/call/recordings` lists the
  gateway's `data/whatsapp/calls/recordings/{wa_id}-{ms}.wav` (else the voice
  agent's `data/recordings/{room}.wav`) with the transcript and who started /
  ended the call; `GET /api/whatsapp/calls/recording/{name}` streams the WAV
  (Range supported, name-validated).
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
  is the authentication. Register it with Telegram yourself (the dashboard's
  "Set webhook" answers 501).
- Replies are stored as sandbox — there is no Bot API client in C# yet.
- No phone number, so CRM linking is manual (the `/chats` contact picker or
  `POST /api/messaging/threads/telegram/{key}/link`). Auto-replies only fire on
  linked threads.

### Messenger / Instagram
- Webhook `GET/POST /api/messaging/webhook` (objects `page` / `instagram`),
  same verify token as WhatsApp. The POST **always** needs a valid
  `X-Hub-Signature-256` — the page account's `app_secret` (or any known Meta app
  secret); none configured ⇒ 401. Events route by the entry id to the account
  whose `external_id` is that page / Instagram account id; the thread key is the
  PSID / IGSID (name fetched once). Redelivered mids are skipped; attachments
  download from the CDN off the ack (one media per message row); delivery / read
  receipts tick our outbound messages; echoes are ignored.
- Sends go through the Send API (`POST /me/messages`, page token, `RESPONSE`),
  text or an attachment by public URL (`media_url` on the messaging send route).
  "Test" calls `GET /me` and moves health (ok → healthy, refused → disconnected).
- The dashboard's Messenger/Instagram setup has no app-secret field yet: set it
  with `PATCH /api/messaging/accounts/{id}` (`app_secret`), Setting
  `meta_app_secret`, or reuse the WhatsApp account's app secret (one Meta app).

### Email
- Accounts (per-mailbox SMTP/IMAP creds in `Config`, password never returned
  unmasked) can be created, but SMTP send is sandbox and
  `POST /api/messaging/email/sync` returns `fetched: 0`. "Test" answers 501.

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
live-or-sandbox seam (with `SendTemplate` / `SendMedia` beside it): a configured
Cloud, Baileys, Messenger or Instagram account sends for real
(recording a provider failure honestly as a `failed` system message); anything
else stores `status="sandbox"`. Flows and AI never send inline — they enqueue
`OutboundJob`s that `QueueService` drains with per-account pacing, daily caps,
quiet hours and an atomic claim.

**Account health** — `AccountHealth.SetStatus` is the **only** writer of
`ChannelAccount.HealthStatus`, recording one `account.health` audit row per real
transition. `healthy`/`degraded` accounts are usable; `disconnected`/`banned`
show red on the Analytics channel-health card. There is no periodic prober in
C#; the Baileys gateway's lifecycle pushes, Meta send / status / test results
(classified by `Channels/MetaErrors.cs`) and inbound traffic drive it.
Per-recipient errors (a closed window, a bad number) must never damage account
health.

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
