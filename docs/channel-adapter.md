# Channels: how they work, and how to add one

A reference for the channels ChatBranch ships — what each one can actually do
**in the C# backend today**, where its code lives, and what it takes to add
another.

> ## ⚠ Read this first: live, but not field-tested
>
> **Every channel has a live transport in the C# API** — WhatsApp (Cloud,
> YCloud, Twilio + Baileys and WAHA), Messenger/Instagram, Telegram, email, the website widget, SIP
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

## 0. Marketplace buyer chat (Shopee, Lazada, TikTok Shop, Shopify)

These four are channels like any other in the inbox, with one structural
difference worth understanding before you touch them: **the credentials are not
here.** A marketplace grant lives in the `e-comerce-seller-mcp` sidecar, which
owns the OAuth flow, the token refresh and the provider APIs — the same division
of labour as `backend/baileys_service` for a QR-paired WhatsApp number.

| | Shopee | Lazada | TikTok Shop | Shopify |
|---|---|---|---|---|
| The account | one `ChannelAccount` per SHOP; `ExternalId` is the shop handle (`shopee:123456`, `shopify:my-store.myshopify.com`) | same | same | same |
| The conversation | `Thread.Key` = the marketplace's CONVERSATION id — that is what a reply needs | same | same | n/a |
| Receive | the sidecar's webhook → `POST /api/commerce/inbound` | the sidecar POLLS (Lazada's IM API has no seller webhook) → the same push | the sidecar's webhook → the same push | — |
| Send text | ✅ through the sidecar (`POST /api/bridge/send`) | ✅ | ✅ | ❌ Shopify has no public buyer-chat API — refused honestly as `Unsupported` |
| Send media | ❌ (each marketplace wants its own upload step); inbound media is LINKED, not downloaded | ❌ | ❌ | — |
| Reply window | none published | none published | none published | — |
| Health | the sidecar's own verdict (`CommerceTransport.ProbeAsync` → its `/api/bridge/accounts`): `connected` → healthy, `revoked` → disconnected. An unreachable sidecar is TRANSIENT — the shop is fine | same | same | same |
| Auth on inbound | `X-Commerce-Secret` (Setting `commerce_secret` → env `COMMERCE_SECRET`) | same | same | same |

**Where the code is.** `Channels/Commerce/CommerceChannels.cs` is the
vocabulary (ids, labels, the `marketplace:shop_id` handle, what carries chat);
`Channels/Commerce/CommerceTransport.cs` is one `IChannelTransport` per channel
over `Sidecars/ICommerceGateway.cs`; `Endpoints/CommerceEndpoints.cs` is the
bridge. They are listed in `MoreChannels.All`, so routing, `MoreChannelSender`,
the health prober and the channel labels carry them unchanged —
`MoreChannels.IsCommerce` is what marks them as credential-free, which is why
`MoreChannelAccounts.Handles` is false for them and the accounts API refuses to
create one.

**The push answers honestly.** The sidecar checks the answer and retries, so
`/api/commerce/inbound` returns 200 only when the message was stored, 400 when
the push can never work (unknown channel, no conversation key, malformed body)
and 500 when a retry might. A silent 200 over a dropped buyer message is the one
thing it must not do.

**A shop appears by itself.** Its grant cannot be typed into ChatBranch, so the
first buyer message creates the inbox (audited `account.create`), and
`POST /api/commerce/shops/sync` pulls in the shops that have not chatted yet.
Everything else about the inbox — the team, the AI default, kept recalls, its
entity — is edited exactly like any other channel's.

**What is NOT here.** Products, stock, orders and sales are the sidecar's own
REST API and MCP server; ChatBranch's side is the conversation. See
`docs/mcp.md` there, and `docs/chatbranch-bridge.md` for the contract.

Tests: `CommerceBridgeTests` (a fake seller service — the secret closed / wrong /
right, the push's honest answer, dedup, a shop becoming an inbox, our own sends
not coming back as buyer messages, Shopify's refusal, health verdicts).

---

## 0b. Managed connections through Zernio (WhatsApp, Messenger, Instagram DM, Telegram, SMS)

Zernio is a CONNECTION METHOD on ChatBranch's native messaging channels
(`Data/Zernio/`, `ZernioNative`): one Zernio account = ONE `ChannelAccount` on its
native channel with `Provider = "zernio"` (ExternalId = Zernio's account id,
`Config.platform`), so routing, windows, templates, flows, the AI, automations,
inbox settings, health and scoping treat it like any other connection of that
channel. Threads keep the native key (WhatsApp / SMS digits, PSID / IGSID, Telegram
chat id) and a reply addresses Zernio's conversation id (`crm_zernioconversation`).
ONE Zernio API key serves the deployment (Setting `zernio_api_key` → env
`ZERNIO_API_KEY`, saved with the other app keys; base `zernio_base_url` →
`ZERNIO_BASE_URL`, default `https://zernio.com/api/v1`). Each entity gets its own
Zernio PROFILE (`externalId = cb-entity-<id>`, created on first connect); the
organization level uses the key's default profile. A Messenger account's Facebook
Pages are mirrored in `crm_socialpage`; the selected Page's conversations reach the
inbox (`POST /api/social/accounts/{id}/pages/{pageId}/select`).

**Scope:** ChatBranch is a connection hub for person-to-person messaging and calls.
Zernio's social-media features — posts, public comments, reviews, other networks —
are deliberately NOT used (removed Nov 2026): accounts of non-messaging platforms
are never mirrored, and `comment.*` / `review.*` / `post.*` webhook events are
answered 200 and ignored. The webhook (`POST /api/zernio/webhook`) verifies
`X-Zernio-Signature` (no secret ⇒ 503, bad ⇒ 401) and dedupes on the event id;
`POST /api/zernio/webhook/register` creates it with a generated secret, subscribed
to `account.*` + `message.*` only. Not verified against a live Zernio account:
tested with `FakeZernioClient` (`ZernioTests`, `ZernioNativeTests`).

### Self-serve WhatsApp onboarding (the client connects their own number)

`POST /api/social/connect {platform:"whatsapp", onboarding, signup}`
runs **Meta's Embedded Signup** through Zernio
(`GET /v1/connect/whatsapp?profileId=&redirect_url=&onboarding=&signup=`), so a client
signs in to Meta in their own browser and picks the number — no System User token is
ever typed into ChatBranch. The headless
`POST /api/social/connect/whatsapp` (the business's own `accessToken` + `wabaId` +
`phoneNumberId`) stays as the advanced fallback.

- **`onboarding` is required** and has no default here, because Zernio's own default
  (the parameter omitted) is **coexistence**:
  - `api` — Cloud API only. The number **stops working in the WhatsApp Business app**,
    the phone's history does not come across, and undoing it means deregistering the
    number. A number on the *consumer* WhatsApp app does not qualify at all.
  - `business_app` — coexistence. The number keeps working in the app, messages are
    mirrored both ways (up to ~6 months of history), **someone scans a QR code during
    setup**, and the API side is capped: 20 msg/s, no groups made in the app, no
    calls, no disappearing or view-once messages.
  Either way the number must first be **disconnected from its previous provider** —
  one provider at a time (WhatsApp Business app → Settings → Account → Business
  Platform → Disconnect). The warnings are returned per mode (`warnings[].code`) and
  the dashboard makes them an acknowledgement before the browser leaves.
- **`signup=hosted`** (the default in `api` mode) makes `authUrl` a page on zernio.com
  that opens Meta's popup itself and connects exactly the number the user picked — no
  Facebook SDK, no domain to register, and no second number picker for a login that
  manages several WABAs. Coexistence runs in Meta's own window (its QR step), so the
  hosted picker is not sent with `business_app`; the answer says which was used.
- **The round trip** is bound to a single-use nonce (`cb`) stored as a Setting
  (`zernio_wa_signup:<nonce>`, 2 hours, pruned on the next start) carrying the entity,
  the profile and Zernio's own `state`.
  `GET /api/social/connect/whatsapp/callback` is **anonymous** (the browser arrives
  from zernio.com / facebook.com): that nonce IS the CSRF control, a `state` sent back
  must match, and the nonce accepts ONE account id. It mirrors the number with
  `MirrorConnectedAsync` — keyed on the Zernio account id, and only for an account
  `GET /v1/accounts` really lists in that profile — so a reloaded callback never makes
  a second inbox, and an inbound message right after the signup already has an inbox.
  Then it redirects to `/channels?zernio=connected|error`.
- **Inbound needs nothing new**: `ZernioWebhook` already handles WhatsApp through
  Zernio (Meta error codes, the 24h window) and `zernio` is already in
  `WhatsAppProviders.Official`, so templates and the window apply.

### Phone numbers (buy / release)

`GET /api/social/phone-numbers` lists the numbers the outlet's Zernio profile owns
(`monthly_cents` each) **plus the live per-country price**
(`GET /v1/phone-numbers/countries`), so the recurring cost is on screen before anyone
buys. No profile for that level = an empty list (a bare `GET /v1/phone-numbers` would
return every company's numbers on the key). `POST …/purchase` needs
`confirm: true` **and** a caller-minted `purchase_intent_id` (a retry must not buy a
second number) and always sends `connectWhatsapp` explicitly, since Zernio defaults it
to true. `DELETE …/{id}` needs `confirm=true` **and** the number typed back, and is
refused for a number in another entity's profile. Both are audited
(`zernio.phone_number.purchase` / `.release`) and both are deliberately **not** MCP
tools (`McpExclusions`): they spend the owner's money and a release cannot be undone.
A number bought through Zernio is pre-verified for WhatsApp and carries calls and SMS
on the same number; a customer's own carrier number does not.

---

## 1. What each channel can do

| | WhatsApp Cloud | WhatsApp Baileys | Website widget | Telegram | Messenger / IG | Email |
|---|---|---|---|---|---|---|
| Receive text | ✅ webhook | ✅ gateway push | ✅ widget API | ✅ webhook | ✅ webhook | ✅ IMAP sync (`/email/sync`): Inbox in, Sent folder as outbound, read-only (never marks mail read); shown as a mail card — the mail's cleaned HTML, From / To / Cc and its attachments (`GET /api/messaging/messages/{id}/email`) |
| Send text | ✅ Graph API | ✅ gateway | ✅ (stored; the widget polls them) | ✅ Bot API | ✅ Send API (page token) | ✅ SMTP (threaded replies; text + escaped HTML alternative; a copy filed in the mailbox's Sent folder over IMAP) |
| Templates | ✅ real template messages; live registry (list/create/edit/delete) with a `waba_id` | rendered as free text | — | — | — | — |
| Send media | ✅ upload → send by id | ✅ gateway `send-media` (base64, ≤16 MB) | — | ✅ sendPhoto / sendDocument | ✅ uploaded file (multipart) or public URL | ✅ attachment |
| Receive media | ✅ downloaded off the ack | ✅ `/baileys/media` (two-phase) | — | ✅ getFile, off the ack | ✅ CDN url, downloaded off the ack | ✅ attachments (≤5, 10 MB) |
| AI documents (PDF / QR, after the reply text) | ✅ document / image | ✅ document / image | 🔗 download link (`/api/documents/public/{token}`) | ✅ sendDocument / sendPhoto | ✅ uploaded file | ✅ attachment (a second mail) |
| Delivery receipts | ✅ `statuses` (forward-only; failed + error code) | ✅ `/baileys/message-status` | — | — | ✅ delivery / read | — |
| Typing / read | ✅ Graph `typing_indicator` | ✅ gateway presence | — | ✅ `sendChatAction` (typing) | ✅ `mark_seen` + `typing_on` (24h window only) | — |
| Quoted replies | ✅ in `context.id` · out `context.message_id` | ✅ in `contextInfo.stanzaId` · out gateway `quoted_id` | — | ✅ in `reply_to_message` · out `reply_parameters` | in only (`reply_to.mid`); our replies go unquoted | — |
| Reactions | ✅ in `type: reaction` · out `type: reaction` (24h window, else 409) | ✅ in `reactionMessage` · out gateway `react` | — | ✅ in `message_reaction` · out `setMessageReaction` (Telegram's own emoji set) | — (409) | — (409) |
| Delete for everyone (ours) | ❌ Meta publishes no delete | ✅ gateway `revoke` → `MessageRecalls.RecordOurs` (~2-day window) | — | ❌ | ❌ | — |
| Edit a sent message | ❌ Meta publishes no edit | ✅ gateway `edit` → `MessageEdits` (~15-minute window) | — | ❌ | ❌ | — |
| Edit made ON the channel (their phone or ours) | ❌ | ✅ `MESSAGE_EDIT` → `/baileys/edited` → `MessageEdits.RecordExternal` — the same row re-worded, the old wording in the audit log, and a held AI reply that answered only it expires | — | ❌ | ❌ | — |
| Location / contact card (ours) | ⚠ Meta's API can, ChatBranch doesn't wire it yet (409 saying so) | ✅ gateway `send-location` / `send-contact` (a vCard built from the name + phones) | — | ❌ | ❌ | — |
| Poll | ❌ the official API has no poll message | ✅ out: gateway `send-poll` (2–12 options, `selectable_count`) · in: read as `[poll] question — a / b`; **votes do not come back** either way — they arrive encrypted against the poll's creation message, which the gateway loses on restart | — | ❌ | ❌ | — |
| Forward a message | ❌ | ✅ gateway `forward` — a true WhatsApp forward of the original, same channel only, and 409 once the gateway no longer holds it | — | ❌ | ❌ | — |
| Operator's own voice note | ✅ upload → `type: audio` | ✅ gateway `send-media` `ptt: true` (the browser's recording re-containered to Ogg/Opus by ffmpeg; no ffmpeg ⇒ 503) | — | ✅ `sendVoice` | ❌ | ❌ |
| AI voice replies (`ai_voice_replies`) | ✅ upload Ogg/Opus → `type: audio` (a voice note) | ✅ gateway `send-media` with `ptt: true` (`audio/ogg; codecs=opus`) | text | ✅ `sendVoice` (Ogg/Opus, no caption) | ✅ MP3 audio attachment (inside the window) | text |
| Inbound voice notes → words | ✅ transcribed (`crm_message.transcript`) | ✅ | — | ✅ | ✅ (audio attachments) | — |
| 24h window | ✅ enforced (409 → use a template); 131047 closes it | none | none | none | ✅ ≤24h anything; 24h–7d a person only (`HUMAN_AGENT` tag), AI/automation refused; >7d refused | — |
| Opt-out | ✅ STOP/UNSUBSCRIBE/BERHENTI → opted out, START/UNSTOP/MULA → back in | same | same | same | same | same |
| Health | ✅ Graph token ping (prober) + send / status / test results (`MetaErrors`), inbound heal | ✅ lifecycle pushes + inbound heal | — | ✅ getMe + getWebhookInfo, inbound heal | ✅ `GET /me` (prober, test), send results, inbound heal | ✅ SMTP login (prober), send outcomes |
| Voice calls | ❌ (Cloud bridge removed; `call/start` → 501) | ✅ (ToS-violating) + recordings | — | — | — | — |
| Auth on inbound | `X-Hub-Signature-256`, **required once any Cloud account is configured** | `X-Baileys-Secret` | anonymous by design (session id) | bot token in path (constant time) + `X-Telegram-Bot-Api-Secret-Token` once registered from the dashboard | `X-Hub-Signature-256`, **always required** | n/a (we read over IMAP) |

**WhatsApp through WAHA** — the operator's OWN self-hosted WhatsApp HTTP API
(`github.com/devlikeapro/waha`, docs `waha.devlike.pro`), provider `waha`. It is
**UNOFFICIAL**: the same category as Baileys, never a Business Solution Provider —
no templates, no 24h window, the `conversation` role by default, and using it may
breach WhatsApp's Terms of Service with a real risk of the number being banned.
Unlike Baileys it is **not a sidecar of ours**: no container in this repo's
compose, no `npm run dev:*`. The operator runs WAHA and types three things into
the inbox — base URL, session name, API key.

| | WhatsApp (WAHA) |
|---|---|
| Credentials | `Config.base_url` (where WAHA listens, http or https), `Config.session` (WAHA's session name, default `default`), `Config.engine` (WEBJS \| WPP \| NOWEB \| GOWS — **declared by the operator for display only**: WAHA picks the engine with its own `WHATSAPP_DEFAULT_ENGINE`, not per session, and ChatBranch calls only endpoints every engine supports), AccessToken = WAHA's API key (`X-Api-Key`; optional — a WAHA started without `WAHA_API_KEY` needs none), AppSecret = the webhook HMAC key (minted on pairing when blank). ExternalId = the paired number's digits, LEARNED when the session reaches WORKING |
| Pair | `POST /api/whatsapp/accounts/{id}/waha/start` — `GET /api/sessions/{s}`, then `PUT /api/sessions/{s}` (existing) or `POST /api/sessions` `{name, start, config}` (new) with OUR webhook + `hmac.key`, then `POST …/start`. `{}` → the QR (`GET /api/{s}/auth/qr`, `Accept: application/json` → base64 PNG; WAHA's codes expire — 60 s for the first, 20 s after, 6 in all — so the panel re-reads it every 3 s); `{"phone": "+60…"}` → `POST /api/{s}/auth/request-code` `{phoneNumber}` → `{code}`, with the QR kept as the fallback when WhatsApp refuses |
| Session statuses | `STOPPED` · `STARTING` · `SCAN_QR_CODE` · `PASSKEY_REQUIRED` · `PASSKEY_CONFIRMATION_REQUIRED` · `WORKING` · `FAILED`. `PASSKEY_CONFIRMATION_REQUIRED` carries `data.code` — a number to CONFIRM on the phone, surfaced as `confirm_code` and never as a pairing code |
| Never restart a live number | WAHA's `PUT /api/sessions/{s}` is destructive — `SessionService.updateSession` stops the session, upserts the config and starts it again. So a WORKING session whose webhook is already ours (or whose config WAHA did not return) is left untouched, and the pairing panel reads the status BEFORE it posts anything. Opening the setup drawer must never drop a paired WhatsApp connection |
| Inbound media's address | a WAHA started without `WAHA_BASE_URL` advertises its files as `http://localhost:3000/api/files/…` — its own address, not one we can reach. Any `/api/…` link whose authority is not the configured `base_url` is rebased onto it (`WahaWhatsAppClient.Rebase`), which also keeps the API key scoped to the one host the operator named |
| Receive | the `message` event on `/api/messaging/webhook/whatsapp-waha/{account_id}`: WAHA's `WAHAWebhook<WAMessage>` envelope (`{id, timestamp, event, session, me, engine, environment, payload}`) — text, media by `payload.media.url`, location, vCards, `replyTo`. 1:1 only: a group (`@g.us`), a channel (`@newsletter`), `status@broadcast` and our own echoes are acked and dropped |
| Send text | `POST /api/sendText` `{session, chatId, text, reply_to?}`; `chatId` = `<digits>@c.us` |
| Send media | `POST /api/sendImage` \| `sendVideo` \| `sendVoice` \| `sendFile` \| `sendSticker` with `file: {mimetype, data}` — **inline base64, because WAHA has no upload step** (≤16 MB). `UploadMediaAsync` therefore only encodes: it hands `SendMediaAsync` a `data:` URI through the same two-call seam every other provider uses. A public `file.url` is accepted too |
| Reactions / quotes | ✅ both ways (`PUT /api/reaction`, `reply_to`) |
| Delivery receipts | ❌ not subscribed (`message.ack` exists in WAHA; the inbox does not read it yet) |
| Typing / read | ❌ WAHA has `POST /api/startTyping`, not wired — and deliberately short-circuited so a WAHA account can never fall through to Meta's Graph |
| Templates | ❌ unofficial: `HasTemplateRegistry` is false and `RouteNeed.Template` asks for `AnyOfficial`, so a template never routes here |
| 24h window | none (`WhatsAppProviders.WindowApplies` is false) |
| Health (prober + session push) | `GET /api/sessions/{s}` → `WahaWhatsAppClient.Verdict`: WORKING → healthy; **STARTING / PASSKEY_REQUIRED / PASSKEY_CONFIRMATION_REQUIRED → TRANSIENT, health untouched** (they are the pairing flow working, including the restart right after pairing); SCAN_QR_CODE / STOPPED / FAILED → disconnected; 401/403 → disconnected ("WAHA refused the API key"); WAHA unreachable → transient (the number is fine). The `session.status` push applies the same rule through `AccountHealth.SetStatus` |
| Auth on inbound | `X-Webhook-Hmac` — **lowercase hex HMAC-SHA512 of the raw body** (`X-Webhook-Hmac-Algorithm: sha512`), keyed by the session's `hmac.key`. No key ⇒ 503, bad ⇒ 401, unknown account ⇒ 404. There is no signed timestamp header, so the replay window reads `timestamp` out of the SIGNED body (10 min — WAHA retries a webhook up to 15 times with its own backoff); the dedup on WhatsApp's message id is the other half |
| Voice calls | ❌ (`call/start` → 501) |
| AI voice replies | text (WAHA's `sendVoice` is not wired to the voice-reply path) |
| Tier | **WAHA Plus was folded into WAHA Core in 2026.6.1** ("Plus is now Core": one `devlikeapro/waha` image, "unlimited sessions, multimedia messages, all storages, built-in security"), so nothing ChatBranch depends on is paid. On a pre-2026.6 image, media messages and more than one session are Plus features — the setup drawer says so rather than letting a media send fail mysteriously |

Where the code is: `Channels/WhatsAppBsp/WahaWhatsAppClient.cs` (the client, the
session lifecycle, the health verdict, the HMAC), `WahaWebhook.cs` (the envelope),
`ChatBranch.Api/Endpoints/WahaEndpoints.cs` (create / settings / pair / status /
logout / the signed webhook), `components/channels/WahaPairPanel.tsx` (the pairing
UX). Tests: `WahaTests` — against a fake of WAHA's documented API, never a live
WAHA, so treat a first real server as the integration test.

**WhatsApp through a Business Solution Provider** — YCloud and Twilio are
OFFICIAL business numbers like Cloud (templates, the 24h window, the `reminder`
role by default; `Channels/WhatsAppBsp/WhatsAppProviders.cs` is the one place
(WAHA rides the same router while staying unofficial)
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
| AI voice replies | ✅ like Cloud (upload → `type: audio`) | text (no voice notes) |

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
| Email (SMTP send, two-way IMAP sync, copy to Sent, settings) | `ChatBranch.Data/Channels/EmailChannel.cs`, `EmailTransport.cs`, `ImapSync.cs` (parser + sync), `ImapMailbox.cs` (IMAP client, UID cursor rule, Sent folder, `SentFolderCopier`), `EmailSettings.cs`; endpoints `MessagingTransportEndpoints.cs` |
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
- **Routing: a payload is matched to ONE registered number.** The webhook finds
  the `ChannelAccount` whose `external_id` is the payload's `phone_number_id`
  (`uniq_channelaccount_wa_pnid` makes that at most one row — two accounts on the
  same number used to make it a coin flip between their companies). The entry id
  (`entry[].id`, the WABA) is a cross-check: a payload that CONTRADICTS the
  number's stored `waba_id` is refused, while a missing or blank entry id — Meta
  specifies it only for `account_update` / `account_review_update` — changes
  nothing, so it is defence in depth and never the only guard. **Once the install
  has any Cloud number, a payload matching no number is recorded and dropped**:
  no thread, no message, no reaction, no delivery receipt, an audit row
  `whatsapp.webhook.unknown_number` (the number, the WABA, counts and the first
  wamid — never the message text or the sender, because that row is
  organization-wide) and still **200**, so Meta stops retrying. It used to be
  processed with no account, which filed the conversation in the unattributed
  bucket every company can see and let the next company to touch it adopt the
  chat. An install with **no** Cloud number at all (sandbox, demo, Baileys-only)
  is unchanged, and the organization Setting
  `whatsapp_accept_unknown_numbers=on` restores the old permissive bucketing.
  `ChatBranch.Data/Channels/CloudInboxRouting.cs` holds the rule.
- **Senders with no phone number (WhatsApp usernames / BSUID).** Every message
  webhook now carries the sender's business-scoped user id (`contacts[].user_id`,
  `messages[].from_user_id`); for a user who adopted a username and whose phone
  number is unavailable Meta **omits** `contacts[].wa_id` and `messages[].from`
  (`contacts[].profile.username` is then present). ChatBranch keys a WhatsApp
  conversation on the phone number, so such a message used to vanish without a
  trace. It is now **recorded**, never filed: an audit row
  `whatsapp.webhook.no_phone_number` scoped to the receiving number's entity, with
  the message id, type, username and BSUID (never the words); a number with no
  company gets an organization-level row without the username / BSUID. It is NOT
  keyed on the BSUID: `PhoneMatch.Digits` runs on WhatsApp thread keys (contact
  matching, `ThreadStore.JobKey`, the canonical "60…" phone rule) and would match a
  random contact by the BSUID's digits, outbound only knows `to` (a BSUID needs
  `recipient`), and nothing links a BSUID to the wa_id the same person may arrive
  with later — `Channels/WhatsAppUsernameSenders.cs` says what a BSUID key would
  need. No path creates a conversation with an empty key any more
  (`ThreadStore` refuses one). YCloud / Twilio inbounds without a sender number
  are still dropped by their parsers.
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
  **131031** means a policy lock (Banned) OR "data in the request could not be
  verified, e.g. an incorrect two-step PIN"; `error_subcode` is deprecated (not
  returned on v16.0+), so the error's message / `error_data.details` decides: PIN /
  verification wording with no policy wording is a recoverable **Degraded** (like
  133005); anything else, Meta's own two-meaning sentence included, stays Banned.
  **133015** (deletion not complete, retry in 5 min) is transient; **133016**
  ((de)registration attempts over 10 per 72 h, blocked for 72 h) is a rate limit
  (Degraded until a success), never a ban.
- **Media:** inbound media is stored with a text placeholder and downloaded off
  the ack (`GET /{media-id}` → the lookaside URL, both with the bearer token,
  20 MB cap) into `data/whatsapp/media/{wa_id}/`; outbound media uploads to
  `/{phone_number_id}/media` and sends by id. Served by `/api/whatsapp/media/{id}`.
  Every media save (every channel) goes through `MediaStore.Stamp`, which keeps
  the original file name, the provider's MIME type and the size on the row
  (`crm_message.media_name` / `media_mime` / `media_size`); the payload's `media`
  is `{type, url, mime, name, size}` plus, for a signed-in session, short-lived
  signed links (`stream_url`, `download_url`, `alt_url` — `GET /api/media/s/{token}`,
  `Endpoints/MediaStreamLinks.cs`) that the inbox's players use as a plain
  `src` with Range. `alt_url` (`?format=m4a`) is offered for Ogg/Opus/AMR audio
  when the server has ffmpeg (`Channels/AudioTranscoder.cs`; `MEDIA_FFMPEG`),
  for Safari / iOS, which can't play Ogg voice notes.
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
  `WhatsAppProviders.AnyOfficial` (cloud / ycloud / twilio / a managed
  connection), never Baileys and never WAHA. `IsOfficial` is a WHITELIST of
  `WhatsAppProviders.Official`, so a new unofficial transport cannot acquire
  templates, the window or the reminder role by accident.
- Twilio takes media by URL only. The signed link names one stored file for 6
  hours (key in Setting `media_link_secret`) and resolves under the media root.

### WhatsApp over WAHA
- The operator's own server, so the failure modes are theirs: an unreachable
  WAHA is TRANSIENT (health untouched) and the pairing panel says "WAHA is your
  own server, not part of ChatBranch — check that it is running and that the base
  URL and API key are right", never "start a gateway". The remedy has to match
  the cause: ChatBranch has no WAHA to start.
- **Treat the pairing lifecycle as progress.** `STARTING` and the two `PASSKEY`
  statuses are the flow working (a session restarts once right after pairing),
  so they are reported `transient: true`, never recorded as a health failure and
  never shown as a hard error. Only `FAILED`, a refused API key or an unreachable
  server are errors.
- The webhook key is MINTED on the first Pair and written into the session's
  `config.webhooks[].hmac.key`, so inbound is never accepted unsigned (503 until
  it exists). Re-pairing is idempotent; the key is kept.
- WAHA also takes a GLOBAL `WHATSAPP_HOOK_URL`. ChatBranch configures the webhook
  PER SESSION instead, because that is what lets each account verify with its own
  secret — and because a global hook would carry other sessions' traffic here.
- The engine (`WEBJS` / `WPP` / `NOWEB` / `GOWS`) is chosen on the WAHA side with
  `WHATSAPP_DEFAULT_ENGINE`, not per session and not by us. It is stored only so
  the inbox can show it; **no call here is engine-specific**, which is why the
  field is optional and purely informational.
- Gaps, honestly: no delivery ticks (`message.ack` is not subscribed), no typing
  indicator, no delete / edit / poll / forward, no history import, no number
  lookup, no calls, no group conversations, no AI voice notes. All of those exist
  on the Baileys gateway and several have WAHA endpoints — they are simply not
  wired.

### WhatsApp over Baileys
- Unofficial WhatsApp Web protocol. **Violates WhatsApp's ToS**; dev/demo only.
  See `backend/baileys_service/README.md` and `RESEARCH.md`.
- **Every push names its number, and an unknown one is dropped.** The gateway's
  `account_id` is the paired number's key (`ChannelAccount.external_id`, written by
  `EnsureBaileysKey` before the gateway is ever told to start or pair a session — so
  the first message after pairing or a reconnect always names an existing row). A
  `/baileys/inbound` (message or reaction) or `/baileys/media` push whose key matches
  no QR number used to be filed with no account — the organization-wide unattributed
  bucket, adoptable by the next company to touch the customer, the same shape as the
  Cloud leak. It is now a recorded drop: no thread, message, reaction or file; an
  audit row `whatsapp.baileys.unknown_account` carrying only the (capped) key — no
  text, sender number, name or message id; and **200**, so the gateway does not
  retry it. Typical source: a number deleted here whose session the gateway still
  restores from its auth directory. A media push also attaches only to a message in
  THIS number's company's conversation. `/from-phone`, `/history`, `/recall`,
  `/edited` and `/status-event` already ignored unknown keys.
  `ChatBranch.Data/Channels/BaileysInboxRouting.cs`.
- **Two ways to link a number.** `POST /api/whatsapp/accounts/{id}/baileys/start`
  with `{}` emits a QR to scan (as before); with `{"phone": "+60123456789"}` it asks
  WhatsApp for an 8-character **pairing code** the person types under WhatsApp →
  Linked devices → Link a device → "Link with phone number" — the only way in when
  the phone cannot point its camera at the dashboard. The gateway requests the code
  on the first handshake (the QR event proves it is up — `requestPairingCode`), keeps
  the QR as the fallback if WhatsApp refuses the number, and reports `state`
  `pairing_code` with `pairing_code` / `pairing_phone` on `/status`. The number is
  cleaned the same way on both sides (`BaileysPairing.Phone` ↔ `pairing.js
  pairingPhone`) so an impossible one is refused with a 400 before a pairing attempt
  is spent — WhatsApp rate-limits them per number. The code is a credential for the
  length of the attempt: never audited (the audit row says only "by phone number"),
  never logged, redacted by `Tools/ToolResults`. A session that is already registered
  reconnects and is never asked for a code. Each start supersedes the previous socket
  (`a.gen` in `index.js`), so switching between the two — or pressing Pair again —
  cannot leave an older socket's reconnect timer fighting the new attempt.
  Tests: `BaileysPairingTests`, `pairing.js --selftest`.
- **Keep phone notifications** (per number, on for a new one): WhatsApp silences the
  phone's notifications while a linked device shows as online, so the gateway stays
  offline between sends and comes online only while sending / typing
  (`presence.js`, `BAILEYS_PRESENCE_IDLE_MS`, default 30 s). The API stores it in
  `ChannelAccount.Config.keep_phone_notifications` and hands it to the gateway
  (`POST /accounts/:id/settings`) before pairing and whenever it changes; the gateway
  keeps a copy in the session dir (`settings.json`). Off = online all the time.
- **The chats the number already had** are imported when it links. WhatsApp replays a
  new companion's recent history (`messaging-history.set`); the gateway filters it to
  1:1 chats, text and the placeholders `parseInbound` already produces, inside the
  window the inbox asked for (`history.js`, self-tested), and pushes it in batches of
  ≤500 to `POST /api/whatsapp/baileys/history`.
  `Channels/WhatsAppHistoryImport.cs` files it as a **backfill**, the same shape as the
  mailbox Sent-folder import: straight through `ThreadStore` and nothing else — no AI
  reply, no flows, no automation rules, no notifications, no STOP handling, no handover
  being answered, no resolved conversation reopening (the status is restored and
  `UpdatedAt` is set to the newest message the conversation holds, so a dormant chat
  does not jump to the top of the inbox). A message from the number itself is stored
  OUTBOUND `external_client`, the customer's as a normal inbound; dedup is on
  WhatsApp's own id, so a replay costs nothing. Historical MEDIA is NOT downloaded —
  the files are usually gone from WhatsApp's servers and the inbox already renders a
  placeholder whose file never came. How far back is per inbox
  (`ChannelAccount.Config.import_history_days`: 0 off, 30 — the default — , 90, -1
  everything WhatsApp sends; only -1 asks for a full sync, since that can be tens of
  thousands of messages). It is handed to the gateway with the other per-number
  settings BEFORE each pairing attempt, because `syncFullHistory` is a socket option.
  Tests: `WhatsAppHistoryTests`, `history.js --selftest`.
- **Fetching OLDER messages on demand.** That replay is shallow — WhatsApp sends the
  chat LIST and the most recent messages, then stops (one measured number: 287
  conversations, 386 messages), and `messaging-history.set` does not fire again. An
  operator can ask for more per chat:
  `POST /api/whatsapp/accounts/{id}/baileys/history-fetch` `{depth?, wa_id?}` starts
  ONE run, `GET` the same route reports its progress, `POST .../stop` ends it
  (capability `channels.manage`; the GET is `channels.view`; MCP tools
  `channels_fetch_older_history` / `channels_get_history_fetch` /
  `channels_stop_history_fetch`). The API contributes only the two things the
  database knows (`Channels/WhatsAppHistoryFetch.cs`): the bounds on `depth`
  (1–300 messages per chat, 200 by default) and the **seed anchors** — the oldest
  message it already holds per 1:1 chat *that this number carried* (a conversation
  can hold messages sent through another account, whose ids the paired phone never
  had), which is what makes a number paired before this feature existed deepenable
  at all. It also refuses a second concurrent run
  itself (it reads the gateway's progress first), an inbox whose
  `import_history_days` is 0, and a number with nothing imported to anchor on.
- The run itself lives in the gateway, because that is where the pacing and the caps
  belong (`baileys_service/history.js`, every decision self-tested):
  `sock.fetchMessageHistory(count, oldestMsgKey, oldestMsgTimestamp)` — the installed
  Baileys' own README passes `msg.messageTimestamp`, i.e. SECONDS — one request in
  flight, a jittered **20–45 s** between requests (slower than the send queue's own
  10–30 s), **50** messages a request, **6** per chat, **20** per run, **60** per
  number per rolling 24 h, a **15-minute** cooldown between runs, and a stop the
  moment WhatsApp stops serving. Answers arrive as another `messaging-history.set`
  with `syncType` `ON_DEMAND` (6) and are pushed through the SAME `pushHistory` path,
  so the filtering, batching and dedupe stay in one place — and historical media is
  still never downloaded. A per-chat cursor in
  `data/whatsapp/baileys/<id>/history-cursors.json` (next to the session, under
  `CHATBRANCH_DATA_DIR` — never in the API's database) makes a run resumable and
  idempotent: it only ever walks backwards, so a second run continues rather than
  re-asking, and a chat already back past the inbox's own window is skipped instead
  of asked about. Chats are keyed by the customer's NUMBER, not the jid: WhatsApp
  hosts some conversations on a `<lid>@lid` identity, so the cursor carries the
  identity its anchor arrived under (that is what the request names) and the answer
  is matched by number — a request made on the phone jid and answered under the lid
  still counts as the real answer it is. **The ceiling is WhatsApp's**: it serves what the paired phone
  still holds, and that phone must be ONLINE. The progress payload says what was
  asked, what was served, what the inbox actually took and why it stopped — it is
  never a full archive, and the dashboard copy does not claim one.
  Tests: `BaileysHistoryFetchTests`, `history.js --selftest`.
- **Messages sent from the phone** (or WhatsApp Web — another device of the number)
  reach ChatBranch as the person's reply: the gateway pushes a live (`notify`)
  message from its own number that it did not send itself, to a 1:1 chat, under 15
  minutes old and not a reaction (`messages.js phoneSent`, self-tested) to
  `POST /api/whatsapp/baileys/from-phone` (a file follows on `/baileys/media` with
  `from_me=1`); `Channels/PhoneSentMessages.cs` stores it OUTBOUND, source
  `external_client`, at the phone's own time, deduped by message id (ChatBranch's
  own sends never twice). Like a person's reply in the dashboard it answers an open
  handover (`agent:phone`), leaves a queued AI reply nothing to answer and supersedes
  a held AI draft — no AI, flows, automations or notifications run on it. A chat the
  person STARTED from the phone links the matching contact with the chatbot off. The
  chat labels these bubbles "Sent from the phone".
- **"Delete for everyone"** reaches ChatBranch too (QR numbers here, plus LINE
  `unsend` and Instagram / Messenger `is_deleted`; Cloud, YCloud, Twilio, Telegram
  and the rest don't report it): the gateway pushes a live REVOKE in a 1:1 chat
  (`messages.js recallOf`) to `POST /api/whatsapp/baileys/recall` (`from_me` = recalled
  on the number's own phone). `Channels/MessageRecalls.cs` erases the message (words,
  file, reactions, the notification preview) — or, with the inbox's "Keep messages the
  customer deletes" (`keep_recalled`), keeps it marked "Deleted by the customer". The
  AI never answers or reads a recalled message; a held reply to it expires.
- No templates (rendered as text), no 24h window. The cold-text guard
  (`cold_text_guard`, default `block`) refuses free text — manual sends and
  queued text jobs alike — from a live conversation-role number to a contact who
  never wrote to it; the queue's cold-call guard does the same for calls.
- Gateway pushes: `/api/whatsapp/baileys/inbound` (text / media placeholder),
  `/message-status` (ticks), `/status-event` (lifecycle → health), `/call-event`
  (ringing → connected → ended; `ended` writes the recording's paired transcript
  `{wa_id}-{ms}.json`), `/media` (multipart `wa_id`, `msg_id`, `media_type`,
  `file_name`, `file` — attached to the placeholder message, 20 MB cap; a file
  that wasn't stored answers 400 / 413 / 500, never 200). The gateway checks
  every push's answer: `/inbound` and `/media` are retried up to 3 times with
  backoff (the media push can race the text push; 4xx other than 404/408/409/429
  is final), and a final failure lands in `events.log` as `inboundPushFailed` /
  `mediaPushFailed` with its status. Downloadable kinds: image, video, round
  video note (`ptvMessage`), audio, document, sticker (incl. lottie); wrappers
  are opened by Baileys' `normalizeMessageContent` and `messages.js` `unwrap`
  (ephemeral, view-once, edited, album children, status mentions) — self-tested. All require
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
  token. Media (photo, document, voice, video, video note, sticker) is fetched with
  getFile off the ack and attached when it lands (20 MB cap); a video sticker
  (WebM) is a video, an animated `.tgs` sticker (Lottie — no browser renders it)
  is fetched as its thumbnail image.
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
  recipient → health untouched. **Copy to Sent:** after a successful send
  through a mailbox with IMAP, `SentFolderCopier` (`ImapMailbox.cs`) APPENDs
  the very same MIME message (same Message-ID) to the mailbox's Sent folder
  flagged `\Seen`, so Outlook shows it too — off the request on the thread
  pool with its own 90 s timeout; skipped when the folder already holds that
  Message-ID (Gmail and many Exchange setups file SMTP mail themselves) or
  there is no Sent folder; a failure is logged (no secrets) and never fails,
  delays or repeats the send.
- In — **two-way sync**: `POST /api/messaging/email/sync` (the /chats nudge)
  and `Workers/EmailSyncWorker` on the server (every `EMAIL_SYNC_INTERVAL` s,
  default 60; `EMAIL_SYNC_WORKER=0` off), so mail arrives with no dashboard
  open; real IMAP at most every 30 s, `{force:true}` skips it). A mailbox whose
  IMAP sign-in is REFUSED is then left alone for 5 min, doubling per refusal to
  30 (`EmailSyncService.AuthBackoffFirst` / `AuthBackoffMax`), because the worker
  would otherwise retry a wrong password ~1 440 times a day and Microsoft 365 and
  Gmail lock an account for that; the wait is reported as a NOTE (nothing failed
  this pass), a mailbox that signs in clears it, and `{force:true}` ignores it so
  a person who fixed the password can retry at once. `\Seen` plays no
  part: folders are opened READ-ONLY (IMAP EXAMINE, bodies fetched with
  BODY.PEEK) so nothing is ever marked read, and mail someone already read in
  Outlook still arrives. Each folder has a UID cursor (`crm_mailboxcursor`:
  mailbox key `imap_host|imap_username`, folder full name, UIDVALIDITY, last
  UID): no cursor or another UIDVALIDITY ⇒ `SENTSINCE` the last 7 days (seen
  or not), else `UID last+1:*` (IMAP's answer of the highest UID even when ≤
  last is dropped client-side); oldest first, 25 per folder per pass; the
  cursor moves to the last UID handled (kept, skipped, duplicate or
  undownloadable), or — when everything that qualified was read — to
  UIDNEXT−1, so a backlog drains over passes and nothing stalls. The rule is
  `MailboxCursorPlan`, shared by the real reader and the tests' fake.
  **Inbox**: keeps EVERY sender — mail from an address that is no contact
  opens an unlinked conversation ("Not in contacts", never answered by the
  AI) like every other channel; only `Auto-Submitted` auto-replies and mail
  from the mailbox's own From address are skipped — and runs the normal
  inbound pipeline. A new email thread starts with the chatbot off.
  **Sent folder** (`SentFolders`: the SPECIAL-USE / XLIST `\Sent` folder,
  else a folder flagged `\Sent`, else the first of "Sent", "Sent Items", "Sent
  Messages", "INBOX.Sent" — case-insensitive, '.' or '/', with or without the
  personal namespace prefix; none ⇒ a `notes` line, not an error): each mail
  becomes an OUTBOUND message (`source = external_client`, status sent, the
  mail's Date) on the thread of every distinct To address that is not the
  mailbox's own (Cc/Bcc ignored; a new address opens an unlinked thread with
  the chatbot off, a known contact's address is linked like inbound mail)
  through `ThreadStore` only — no AI, flows, automations, notifications, STOP
  handling or handover. A Message-ID ChatBranch already holds is skipped:
  that is how its own sends (Message-ID stored as the message's external id)
  never appear twice. The AI treats such a message like a teammate's reply
  (follow-ups stop, a pending draft is superseded, a queued AI reply finds the
  customer answered). Both folders: Message-ID dedup, quoted history stripped
  conservatively, HTML-only mail flattened to readable text
  (scripts/styles/comments dropped, line breaks + paragraphs kept, links keep
  their URL, entities decoded), attachments, headers recorded in
  `crm_emailheader`. The report adds `sent_imported`, `sent_duplicates`,
  `sent_skipped` and `notes`.
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

### Sending a file by reference (`POST /api/messaging/send-file`)

`media_url` is accepted on EVERY channel, not only the three whose provider APIs
take a URL themselves: the server downloads the file and then hands it to the
same send path a multipart upload takes, so a tool call stays one line whatever
the file weighs. `Channels/RemoteMedia.cs` owns the fetch — https only, at most
3 redirects with **every hop re-checked**, a 30 second budget, the channel's own
size cap read from `Content-Length` and enforced again while reading, the type
from `Content-Type` then the extension, the name from the request then
`Content-Disposition` then the URL.

The address rules (`RemoteMediaRules.Refuse`, unit-tested) are what keep this
from being a server-side request forgery: loopback, link-local (including
`169.254.169.254`), private and carrier-NAT ranges, unique-local IPv6,
multicast, IPv4-in-IPv6, and any host with no dot or ending `.local` /
`.internal` / `.localhost` — checked against the host's RESOLVED addresses, not
just a literal IP. A name that resolves inside is refused. DNS could still
change its answer between the check and the socket; the mitigation is that a
fetched file only ever lands in the conversation it was asked for.

Google Drive share links are understood (`/file/d/<id>/`, `?id=`): with a Drive
connection for the conversation's outlet the file is downloaded through the API,
so a PRIVATE file needs nothing made public, and without one the share link is
rewritten to a direct download. An HTML answer is reported as "no access",
because that is what Drive returns for a file that is not shared.

`file_id` covers the third case, and the one that was impossible: a file that
exists only on somebody's phone. They drop it into the **media library**
(`MediaLibraryEndpoints`, `crm_medialibraryitem`, kept 30 days) from the
dashboard or their phone, and sending it afterwards costs the id. An upload
belongs to an outlet (the uploader's, when they are bound to one), so a branch's
files are not another branch's to send; a caller that can reach the server can
also ask for a 15-minute presigned upload link rather than sending multipart.

Two other references avoid the file entirely: `source_message_id` resends
something ChatBranch already stores, ACROSS channels (an email attachment into a
WhatsApp chat — `conversations_forward_message` cannot do this, since a WhatsApp
forward is same-channel by nature), and `targets` (≤20) delivers one fetch to
several conversations, each with its own guards and its own result.

### Sending a sticker (`POST /api/messaging/send-sticker`)

Receiving a sticker has always worked on Baileys (animated Lottie wrappers
included), WhatsApp Cloud and Telegram, and the inbox renders one small with no
bubble behind it. Sending one is a different job, because a sticker is not a
picture with a different label: WhatsApp shows one only for a **512×512 WebP**
under its own ceiling (100 KB still, 500 KB animated) carrying an **EXIF pack
block**. Send an arbitrary image as a `stickerMessage` and it renders wrong or is
dropped — so the shaping happens in `Channels/StickerImage.cs` before the gateway
is called:

* scale inside the square and pad the rest TRANSPARENT (never squash the aspect
  ratio, never letterbox bars);
* walk the WebP quality down until the file fits, halving an animated sticker's
  frame rate before giving up — a slower sticker beats no sticker;
* write the EXIF chunk (little-endian TIFF, one undefined-type entry holding the
  pack JSON) and set the VP8X EXIF flag. Without it the sticker renders but has
  no attribution and cannot be added to the viewer's tray.

**Three input paths, because ffmpeg cannot decode an animated WebP.** An animated
WebP is therefore passed through untouched and only stamped — and refused honestly
when it is not already 512², since it cannot be resized here. An animated GIF or a
short clip is converted to an animated sticker (≤6 s at 12 fps). Everything else
becomes one still. ffmpeg is the same seam the audio renderings use
(`AudioTranscoder.FfmpegPath`, env `MEDIA_FFMPEG`); a server without it still
sends an already-correct WebP and refuses anything else **naming ffmpeg**, rather
than sending something WhatsApp will drop.

The picture comes from the same four references `send-file` takes — `media_url`,
`source_message_id`, `file_id`, `file_base64` — read by that route's own loader,
so the two cannot drift. Every guard a manual send applies still applies (scope,
opt-out with `force`, the cold-outbound guard, the 24-hour window), and routing is
not bypassed: a conversation pinned to a number that cannot carry a sticker fails
over exactly as a text would.

**QR-paired numbers only, and the refusal says which kind of no it is.** Meta's
Cloud API *can* carry a sticker (by the media id of an uploaded WebP) and is told
it is simply not wired here; the BSPs publish no sticker send at all and are told
that. Another channel is refused by name. Telegram's `sendSticker` takes the same
WebP and is the obvious next one — it is a gap in Telegram's adapter, not in
WhatsApp's.

The message row is stored as `[sticker]` with the file attached as an **image**,
which is exactly how an INBOUND sticker is recorded, so the inbox needs no second
rendering path for ours.

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
`Ai/AiEligibility.Decide` — the same rule every `ai_enabled` payload shows: opted
out → quota → the master switch / schedule of the entity tree AND of the
conversation's own inbox (which can only switch itself off) → contact never /
never-AI tag → the per-chat tick → contact always → the inbox default → the
account role — and not opted out) — the AI reply, debounced through the queue or instant
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

## AI voice replies and voice notes the AI can read

`Ai/Voice/` (`SpeechClient`, `VoiceReplyService`, `VoiceTranscriber`). Setting
`ai_voice_replies` off (default) | mirror | always + `ai_voice_id`, resolved inbox →
entity → organization like every human-like touch. At DELIVERY (`AiGate.Deliver` /
the queue's `voice: true` job → `AiGate.SendVoiceOrText`) the reply's words are cleaned
(markdown, emoji, message ids; a link or > 1500 characters keeps it TEXT), spoken by
ElevenLabs (`POST /v1/text-to-speech/{voice_id}?output_format=opus_48000_64` for
WhatsApp / Telegram — checked to be Ogg — `mp3_44100_128` for Messenger / Instagram,
model `eleven_multilingual_v2`, env `ELEVENLABS_TTS_MODEL`), stored as conversation
media and sent ONCE through `OutboundSender.SendVoiceNote`. The message row keeps the
words with the audio attached. Text instead — never silence — on every other channel
(email, SMS, LINE, Viber, WeChat, Zalo, TikTok, Douyin, widget, Twilio WhatsApp), with
no ElevenLabs key, at the entity's AI quota, or when TTS / the media send fails (no
failed row is left). Metered as `LlmUsage` `voice_reply` (characters as prompt tokens).
⚠ Built to ElevenLabs' documented request shapes; the tests run against fakes.

Inbound audio is transcribed lazily right before the AI writes (`AiComposer` →
`VoiceTranscriber.TranscribePendingAsync`, 20 s budget): OpenAI
`/v1/audio/transcriptions` (`gpt-4o-mini-transcribe`, env `OPENAI_STT_MODEL`) else
ElevenLabs `/v1/speech-to-text` (`scribe_v1`) else nothing ("[audio]" stays). Stored on
`Message.Transcript`, read as "[voice note] …" by the AI and the case summary, exposed
as `transcript` on message payloads, metered as `LlmUsage` `transcription`.
