# CLAUDE.md

Guidance for Claude Code working in this repo. Read before making changes.

## What this is

ChatBranch — an industry-agnostic multi-channel conversation platform
(template). Four cooperating processes:

1. **C# API** (`backend-csharp/`, port 8090) — ASP.NET Core 8 minimal APIs over
   SQLite or PostgreSQL (EF Core; `DATABASE_PROVIDER`, see `docs/postgres.md`). THE backend: contacts, the unified inbox, flows, the
   outbound queue, the AI, integrations, RBAC, the LiveKit control plane for
   calls, the MCP server. Root `/` 404s — it is API-only.
2. **Voice agent worker** (`backend/voice_agent/`) — a **separate** long-lived
   LiveKit Agents process (Node/TypeScript on `@livekit/agents`, no database).
   The API dispatches it into each call's room; it runs the conversation and
   writes results to the shared data dir. NOT started by the API.
3. **Next.js dashboard** (`frontend/`, port 3000) — operator UI.
4. **Baileys gateway** (`backend/baileys_service/`, port 3020) — Node sidecar
   for `provider="baileys"` WhatsApp accounts (unofficial WhatsApp Web:
   QR-paired personal numbers, text + voice calls). The C# API is its only
   client; its push-target env var is `API_URL` (`DJANGO_URL` still accepted).

> **There is no Python in the repo.** The Django backend was removed on
> 2026-08-16 and the voice agent was ported to Node/TypeScript in the
> 2026-10 overhaul. `backend/` holds only the two Node sidecars,
> `backend/.env(.example)` and the gitignored `backend/data/` — which holds the
> LIVE Baileys WhatsApp session (`data/whatsapp/baileys/<account>/creds.json`),
> media and the voice-agent IPC files. Never delete it. Comments and docs that
> name Django modules (`crm/scoping.py`, `whatsapp/store.py`, `flows/engine.py`,
> `aicore/assistant.py`…) describe the behaviour the C# port reproduces — read
> them as the SPEC and find the code under `backend-csharp/src/`. The C# port
> is NOT at full parity; see **Port status** below before promising a feature.

## Run

```bash
npm run dev:all  # repo root — all four processes via concurrently (api/agent/web/wa)
```

Or individually (what the root `package.json` scripts do):

```bash
npm run dev:api      # dotnet run --project backend-csharp/src/ChatBranch.Api --urls http://0.0.0.0:8090
npm run dev:agent    # voice agent worker (tsx)
npm run dev:web      # Next.js, :3000
npm run dev:baileys  # Baileys gateway, :3020
```

- First-time bootstrap: `npm install` then `npm run setup` (installs the
  frontend, the Baileys gateway and the voice agent). The API needs no
  bootstrap — SQLite is created and seeded on first boot (`Seed.Run`,
  additive `Seed.UpgradeSchema` for new columns/tables).
- **Seeding is clean by default**: only the Demo tenant, the `main` entity, the
  three built-in roles and ONE login — `superadmin` (password
  `SUPERADMIN_PASSWORD`, else the POC `super123`). **`SEED_DEMO=1`** adds the
  `admin/admin123` + `agent/agent123` logins and one sample contact. POC creds —
  rotate before deploy. The test host sets `SEED_DEMO=1` itself.
- **Nothing in `.env` is required to boot**; with nothing set the whole app runs
  in sandbox mode (the voice agent exits at once, naming the missing LIVEKIT_*
  keys). **Where config comes from:** the API reads Setting rows (dashboard) →
  its own process environment. It does NOT load `backend/.env`, except the
  LiveKit keys (`LIVEKIT_URL/API_KEY/API_SECRET`, `AGENT_NAME`,
  `SIP_OUTBOUND_TRUNK_ID`, read per call by `Sidecars/LiveKitClient.cs`). The
  voice agent and the Baileys gateway load `backend/.env` themselves; under
  docker compose only the voice agent gets it (`env_file`) — the API takes its
  `LIVEKIT_*` / `BAILEYS_*` from the shell or the repo-root `.env`.
- The API and the agent share ONE data dir: `CHATBRANCH_DATA_DIR`, default
  `backend/data` of the checkout (`VoiceAgentBridge.ResolveDataDir`; Docker
  `/data`).
- **`dotnet run` gotcha:** `Properties/launchSettings.json` (profile `http`,
  port 5223) OVERRIDES an `ASPNETCORE_URLS` env var. A `--urls` argument wins
  over the profile (that is what `dev:api` uses); if you configure the URL via
  env, run `dotnet run --no-launch-profile --urls …`.
- Under `dotnet run` the content root is the project dir, so the dev database is
  `backend-csharp/src/ChatBranch.Api/chatbranch.sqlite3` (gitignored;
  `ConnectionStrings__Default` overrides; Docker uses `/data/chatbranch.sqlite3`).
- **SQLite or PostgreSQL** (`Data/DatabaseSetup.cs`, the one place it is decided):
  `DATABASE_PROVIDER` = sqlite (default — dev, tests, a first boot need nothing) |
  postgres; Postgres reads `DATABASE_URL` (postgres://user:pass@host:5432/db, wins)
  else an Npgsql `ConnectionStrings__Default` — never the image's baked-in SQLite
  string. Compose: profile `postgres` (PostgreSQL 16 + a `postgres-backup` pg_dump
  loop into the data volume's `backups/postgres/`). `DB_IMPORT_SQLITE` copies an old
  SQLite file into an EMPTY Postgres database on boot (`Data/SqliteImport.cs`: backup-API
  snapshot, one transaction, per-table counts verified, sequences moved, Setting
  `db_imported_from_sqlite`). Switch-over runbook: `docs/postgres.md`.
- **Other companies' servers run prebuilt images, never the source:**
  `deploy/customer/` (image-based compose — Postgres + daily dumps, Caddy HTTPS
  for `DOMAIN`, `/api/*` → api, hardened containers, internal networks — with
  `.env.example` and a README) pulls what the **Release** workflow
  (`.github/workflows/release.yml`: tag `v*.*.*` or manual, after a green CI)
  pushes to GHCR as `chatbranch-{api,web,baileys,agent}:<version>` + `:latest`,
  with SBOM + provenance, cosign-signed. The web image is built with
  `NEXT_PUBLIC_API_URL=/api` (one image, any domain). The own servers' compose
  build + `deploy.yml` are unchanged.

## Port status — what is real, what is sandbox

Every transport is wired in C#; what is left is listed per row. Sandbox =
the message is stored with `status="sandbox"` and never leaves the machine.

| Area | Real | Still missing |
|---|---|---|
| WhatsApp Cloud | inbound webhook (verify token; `X-Hub-Signature-256` **required once any Cloud account is configured**; wamid dedup; Meta's own timestamps; text, media, button/interactive replies, location); **delivery statuses** (sent → delivered → read / failed + `Message.Error`, forward-only; 131047 closes the window; account-level codes → `AccountHealth.SetStatus`, per-recipient codes never — `Channels/MetaErrors.cs`); **text, real TEMPLATE messages** (header/body/URL-button parameters, rendered body stored) and **media** (upload → send by id) via Graph (`OutboundSender`, queue template jobs too); inbound media downloaded off the ack (`MediaFetcher`, the original file name / MIME / size kept on the row); live template registry when the account has a `waba_id` (paged list + create/edit/delete, `Channels/MetaTemplates.cs`); account Test = `GET /{phone_number_id}`; **AI voice notes** (upload Ogg/Opus → `type: audio`, `ai_voice_replies`); typing/read indicator; **quoted replies** (inbound `context.id` → `Message.ReplyToId` / `ReplyToExternalId`, outbound `context.message_id` from `…/send {reply_to: pk}`) + **emoji reactions** (inbound `type: reaction` → `crm_messagereaction` — never a bubble, the AI, flows/automations or a reopen; outbound `type: reaction` via `POST /api/messaging/threads/{channel}/{key}/messages/{pk}/react`, 24h window else 409 — `Channels/MessageReactions.cs`) | no Cloud voice calls (`call/start` on a Cloud account → 501) — Meta Business Calling is a gated beta and its WebRTC bridge (Python aiortc) went with Django; not planned until a calling-enabled number exists to build against |
| WhatsApp YCloud / Twilio ⚠ | official BSP numbers, **unverified against the live APIs** (`Channels/WhatsAppBsp/`: `WhatsAppProviders` = the one provider vocabulary, `WhatsAppClientRouter` routes every `IWhatsAppCloudClient` call by provider); one signed webhook per account `/api/messaging/webhook/whatsapp-{ycloud,twilio}/{account_id}` (`Endpoints/WhatsAppBspWebhookEndpoints.cs`: `YCloud-Signature` t=…,s=… HMAC-SHA256 of `t.body`, 5-min window / `X-Twilio-Signature` with the Auth Token; no secret ⇒ 503, bad ⇒ 401, unknown ⇒ 404) → `InboundProcessor` (text, media off the ack, location, buttons, quotes, reactions); delivery statuses forward-only (YCloud `whatsapp.message.updated`, Twilio `MessageStatus` + a stamped `StatusCallback`; Twilio codes mapped to Meta's); text, templates (YCloud = Meta templates + live registry with a WABA id; Twilio = `ContentSid` + Content API list, read-only — 409 on create/edit/delete) and media (YCloud upload → id, link fallback; Twilio by a signed 6 h public link `GET /api/media/public/{token}`, `MediaLinks`, needs a public https base URL); 24h window, reminder role and `RouteNeed.Template` (`WhatsAppProviders.AnyOfficial`) like Cloud; Test + prober (YCloud number profile / balance, Twilio `Accounts/{sid}`); YCloud typing indicator | no calls (501); Twilio: no reactions out (409), quotes go out unquoted, no typing indicator, templates authored in Twilio's console |
| WhatsApp Baileys | inbound text **+ media** (`/baileys/media` attaches the decrypted file to the `/inbound` placeholder — images, video + round video notes (`ptvMessage`), voice notes, documents (with their file name), stickers incl. lottie; wrappers opened by Baileys' `normalizeMessageContent` + `messages.js` `unwrap` (ephemeral / view-once / edited / album children / status mentions); every push checks the API's answer — `/media` retried 3× with backoff (it can race `/inbound`), a final failure logged as `mediaPushFailed` with its status in `events.log`; the API answers 400 / 413 / 500 when it stored nothing, never a silent 200), message-status ticks, lifecycle → health, **text + media send** (media: the gateway's `POST /accounts/{id}/send-media` — base64 in the body, ≤16 MB, never a path/URL, `backend/baileys_service/media.js`; `IBaileysGateway.SendMediaAsync`; the cold-text guard covers files too), presence, **voice calls** (call/start, call-event) + **call recordings** (paired transcript written on `ended`, listed per thread, WAV streamed); **number lookups** for the contact identity checker (`IBaileysGateway.CheckNumberAsync` → the gateway's `/accounts/{id}/check`; never sends a message); **quoted replies + reactions** both ways (the gateway pushes `reply_to` / `{reaction: {msg_id, emoji}}` on `/inbound`; `send` takes `quoted_id`, `POST /accounts/{id}/react`; `messages.js`, self-tested); **AI voice notes** (`send-media` with `ptt: true` → `audio/ogg; codecs=opus`); **keep phone notifications** (per number, `Config.keep_phone_notifications`, `BaileysSettings` — the drawer's toggle, on for a new QR number: the gateway stays offline between sends and comes online only while sending / typing, `backend/baileys_service/presence.js` + `BAILEYS_PRESENCE_IDLE_MS` (30 s), so WhatsApp doesn't silence the phone; handed over before pairing and pushed to a live session on change via `IBaileysGateway.SetPhoneNotificationsAsync` → `POST /accounts/{id}/settings`, kept in the session dir's `settings.json`; off = online all the time, as before); **messages sent from the phone** (or WhatsApp Web — another device of the number: a live `notify` from its own number the gateway didn't send itself (`ownSends`), 1:1, < 15 min old, not a reaction — `messages.js phoneSent`, self-tested) → `POST /api/whatsapp/baileys/from-phone` (a file on `/baileys/media` with `from_me=1`) → `Channels/PhoneSentMessages.cs`: the person's OUTBOUND reply (`external_client`, the phone's time, deduped by message id — ChatBranch's own sends never twice), so an open handover is answered (`agent:phone`), a queued AI reply finds nothing to answer and a held draft is superseded; no AI / flows / automations / notifications on it; a chat STARTED from the phone links the matching contact with the chatbot off; the bubble reads "Sent from the phone" (tests `PhoneSentTests`); **recalled messages** ("delete for everyone" — the customer's, or `from_me` one made on the number's own phone: a live REVOKE `protocolMessage`, 1:1, `messages.js recallOf`, self-tested) → `POST /api/whatsapp/baileys/recall` → `Channels/MessageRecalls.cs` (see Recalled messages) | templates go out as rendered text (WhatsApp Web has no template messages) |
| Telegram | inbound webhook `/api/messaging/webhook/telegram/{token}` (token compared in constant time, unknown → 404; `X-Telegram-Bot-Api-Secret-Token` required once registered from the dashboard; text + media via getFile, off the ack — video stickers (WebM) as video, animated `.tgs` stickers as their thumbnail; inbound heals health); **send** text/photo/document (`Channels/TelegramClient.cs` `TelegramChannel`; the thread keeps its own bot, a bot-less thread is routed); **typing indicator** (`sendChatAction` during the AI's typing pause — `Ai/TypingIndicator.cs` `TelegramTypingIndicator`, routed by `ChannelTypingIndicator`); **quoted replies** (`reply_to_message` in, `reply_parameters` out) + **reactions** (`message_reaction` updates in — setWebhook asks for them; `setMessageReaction` out, Telegram's own emoji set); **AI voice notes** (`sendVoice`); Register webhook (setWebhook + secret_token) and Test (getMe) | — |
| Email | account CRUD (masked SMTP + optional IMAP creds); **SMTP send** via MailKit (`Channels/EmailChannel.cs`) as **multipart/alternative** — the text as typed + an escaped HTML rendering (`EmailHtml`: paragraphs, `<br>`, http(s)/email auto-links, never raw HTML; multipart/mixed with attachments); the mailbox is chosen by `Routing.ForThread` like a WhatsApp number (thread's healthy mailbox → sticky assignment → least-loaded auto-assign → failover off disconnected/banned with note + audit + notification, the outlet's own first; all down ⇒ failed, none configured ⇒ default SMTP settings ⇒ sandbox); Message-ID / In-Reply-To / References + "Re: <subject>" from `crm_emailheader`; `Auto-Submitted` on AI mail; **two-way IMAP sync** `POST /api/messaging/email/sync` (`Channels/ImapSync.cs` + `ImapMailbox.cs`: 30 s throttle; folders opened READ-ONLY (EXAMINE) — `\Seen` is never read nor set, so mail already read in Outlook still arrives and nothing is marked read there; a UID cursor per mailbox × folder in `crm_mailboxcursor` (+ UIDVALIDITY: a first run or a rebuilt folder reads the 7-day window, seen or not, then `UID > last`, ≤10 per folder per pass oldest first, the cursor moving past every mail handled); INBOX: every sender kept (a non-contact opens an unlinked chat; only `Auto-Submitted` auto-replies and the mailbox's own From are skipped), normal inbound pipeline; **Sent folder** (SPECIAL-USE `\Sent`, else "Sent" / "Sent Items" / "Sent Messages" / "INBOX.Sent"; none ⇒ a note) imported as OUTBOUND `source = external_client` on each To address's thread (Cc/Bcc ignored, a new address = an unlinked chat, AI off) through `ThreadStore` only — no AI, flows, automations, notifications or STOP; ChatBranch's own sends skipped by Message-ID; Message-ID dedup, HTML-only mail flattened to readable text, attachments); **copy to Sent**: after a successful SMTP send through a mailbox with IMAP, `SentFolderCopier` APPENDs the same MIME (`\Seen`) off the request — skipped when the server already filed it, failures only logged, never a retry; Test = SMTP login (+ test mail) | no HTML compose (the HTML part is rendered from the plain text) |
| Messenger / Instagram | account CRUD (+ `app_secret`, also in the setup drawer); **webhook** `GET/POST /api/messaging/webhook` (WhatsApp's verify token; signature **required** against the known Meta app secrets, none ⇒ 401; routed by page / IG id; mid dedup; CDN attachments; delivery/read receipts; sender name); **Send API** text, media by public URL **and uploaded files** (multipart `filedata`, `IMetaMessagingClient.SendFileAsync`; `POST /api/messaging/threads/{facebook\|instagram}/{key}/send-media`; a caption follows as its own message); **messaging window** (`Channels/SocialWindow.cs`, live pages only: ≤24h anything as `RESPONSE`; 24h–7d a signed-in person only, `MESSAGE_TAG` + `HUMAN_AGENT`; AI / follow-ups / flows / queue / API keys / MCP refused — 409 on the inbox routes, failed job, blocked AI draft; >7d or never wrote → refused; `window_open` / `human_window_open` on the thread payload, the inbox `window` filter and the channel hub; a tagged reply refused for permissions never touches health); **typing indicator** (`mark_seen` + `typing_on` sender actions inside the 24h window, `Ai/TypingIndicator.cs` `MetaTypingIndicator`); **AI voice notes** (an MP3 audio file); **unsent messages** (`message.is_deleted` → `MessageRecalls`); account Test = `GET /me` → health | — |
| LINE | **webhook** (`X-Line-Signature`; text, image / video / audio / file downloaded off the ack, sticker / location placeholders; follow / unfollow → opt-in / opt-out; `unsend` → `MessageRecalls`; the bot profile names the thread); **send** text through the webhook's reply token while it is fresh (≤50 s, free) else push; Register webhook + Test (`/v2/bot/info`) | outbound media (LINE takes images by public URL only — refused honestly); typing indicator |
| Viber | **webhook** (`X-Viber-Content-Signature`; text + picture / video / file from Viber's CDN; subscribed / unsubscribed → opt-in / opt-out; `conversation_started` context rides on the first message as `[ref:…]`; delivered / seen / failed ticks); **send** text (`send_message`); Register webhook (`set_webhook`) + Test (`get_account_info`) | outbound media (public URL only) |
| SMS (Twilio) | **webhook** (`X-Twilio-Signature`; MMS media fetched with Basic auth off the ack; Twilio's own STOP/START `OptOutType` mirrored); **send** text ≤1600 chars from the inbox number or a Messaging Service (`TwilioSmsTransport`); phone-keyed — contacts link by last-8 digits like WhatsApp, numbers spread like mailboxes; error 21610 opts the conversation out; Test = the Twilio account (suspended / closed → health); flow SMS steps send through it | outbound MMS (needs a public media URL); delivery status callbacks |
| WeChat OA | **server URL** `GET` (echostr verification) + `POST` (sha1 signature, 5-min freshness, single-use nonces; plaintext or AES safe mode; text, image, voice, video, location, link; subscribe / unsubscribe → opt-in / opt-out; a parametric QR's scene rides as `[ref:…]`; media via `media/get`); **send** Customer Service text + images (temporary-media upload) inside a **48 h** window (errcode 45015 = the same refusal); access_token cached ~7000 s; Test = the token fetch | passive (in-response) replies |
| TikTok ⚠ | partner-gated; built to the documented shapes, **unverified against the live API**: webhook (`TikTok-Signature t=…,s=…`, 5-min replay window), send text inside a **48 h** window, Test = `business/get` | media (in and out) |
| Douyin ⚠ | approval-gated; **unverified against the live API**: webhook (`X-Douyin-Signature`, `verify_webhook` challenge, `im_receive_msg`), reply text quoting the customer's latest message inside a **48 h** window, Test = the client-credential token | media; access-token refresh |
| Zalo OA | **webhook** (`X-ZEvent-Signature` mac; text, image, file, sticker, link, location; follow / unfollow → opt-in / opt-out; OA echoes ignored); **send** consultation text + images (upload → media template) inside a **7-day** window (Zalo's -230 = the same refusal); an expired access token is refreshed once with the rotating refresh token; Test = `getoa` | — |
| Website widget | full (internal transport) | — |
| SIP voice | dial / live / say / hangup / escalate through LiveKit | — |
| Media serving | `/api/whatsapp/media/{id}` + `/api/messaging/media/{id}` (`chats.view` + the conversation's entity scope, Range, non-renderable types as downloads; files under `data/{whatsapp,messaging}/media/`, `Channels/WhatsAppMediaStore.cs`); the payload's `media` = `{type, url, mime, name, size}` (`crm_message.media_name` / `media_mime` / `media_size`, stamped by `MediaStore.Stamp` at every save; older rows derive them from the extension + the file) + for a signed-in SESSION `stream_url` / `download_url` / `alt_url` = **signed links** `GET /api/media/s/{token}` (`Endpoints/MediaStreamLinks.cs`: anonymous route, HMAC-SHA256 with a per-process key over message + profile + username + expiry, 60–90 min on a 30-min grid so a re-rendered thread keeps the same src; on open the person must still be active, hold `chats.view` and see the conversation — else 404, expired 410; Range; `?download=1` = attachment with the original name; `?format=m4a` = an AAC rendering of Ogg/Opus/AMR audio made by ffmpeg (`Channels/AudioTranscoder.cs`, env `MEDIA_FFMPEG` path / `off`, else PATH; cached as `{file}.m4a`; the API image installs it)) — the inbox plays them in the bubble (`components/chats/MediaPlayers.tsx`: images, an audio player with seek + speed that switches to the M4A when `canPlayType` says no, `<video playsInline>`, file cards with size + Download / Open; a placeholder `[image]` is never shown under its attachment and reads "Image — not available" when the file never came), `/api/whatsapp/calls/recording/{name}` (gateway WAV or the agent's), `/api/calls/recording/{call_id}` (SIP); AI-generated documents (PDF / QR, see Documents) live in the same media roots, `/api/documents/{id}/file` (`chats.view`, scoped) + the customer's anonymous `/api/documents/public/{token}` link, sent as a FILE on WhatsApp, Messenger/IG, Telegram, email, WeChat, Zalo and as that LINK on LINE, Viber, SMS, TikTok, Douyin and the website widget | — |
| Channel health | writer `AccountHealth.SetStatus` (one audit row + an admin `account_health` bell per problem/recovery transition); periodic prober `Workers/HealthProbeWorker` (`WHATSAPP_HEALTH_INTERVAL` s, default 300; `HEALTH_WORKER=0` off) via `Channels/HealthProber.cs`: Cloud Graph ping, Baileys = the gateway's own session state (`connected` → healthy; not paired / QR expired / logged out / gateway unreachable → disconnected; a banned number stays banned — `ChannelHealthProber.BaileysVerdict`), Telegram getMe + getWebhookInfo, SMTP login, Messenger/IG `GET /me`, the newer channels' own probes (`IChannelTransport.ProbeAsync`), default SMTP → `email_health`; Meta send / status / test results classified by `Channels/MetaErrors.cs`; "Check now" runs the prober for the caller's scope; saving credentials resets to unknown + re-probes in the background | SIP voice stays config-presence only (by design) |
| Routing | `Channels/Routing.cs` at SEND time (queue drain, manual sends, email mailbox + Telegram bot choice, and the AI gate — instant / approved / auto-sent AI replies, follow-ups): thread's healthy number → sticky `AccountAssignment` → least-loaded auto-assign → failover off banned/disconnected (pin cleared, thread moved, note + audit + notification); roles/providers/templates are WhatsApp-only (`RouteNeed.For`; templates → Cloud, never Baileys; 409 when none); account-bound channels (Telegram, Messenger/IG, widget, LINE, Viber, WeChat, TikTok, Douyin, Zalo) keep their own account, email and SMS spread even contactless threads; inbound re-attribution (between numbers of the SAME company only — never across companies); `business_redirect` (`Channels/BusinessRedirect.cs`); recovery one-click + auto (`Channels/Recovery.cs`, hooked in `AccountHealth.SetStatus`) | — |
| Compliance | STOP/UNSUBSCRIBE/BERHENTI (+ `opt_out_keywords`) → `Thread.OptedOut` on every inbound channel, START/UNSTOP/MULA opts back in (`Channels/OptOut.cs`); platform signals set it too (Viber unsubscribed, LINE / WeChat / Zalo unfollow, Twilio `OptOutType` and error 21610 — `ChannelOptOut` in `Channels/More/MoreChannels.cs`); queued jobs cancelled; manual sends need `force`; cold-text guard (manual + queued WhatsApp text), cold-call guard, do-not-call | — (the opt-out lives on the conversation by design: consent is per channel, as in the original spec) |

The seven newer channels (LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo —
`Channels/More/`) share one shape: one signed webhook per ACCOUNT,
`/api/messaging/webhook/{channel}/{account_id}` (`MoreChannelWebhookEndpoints.cs`;
no secret configured ⇒ 503, bad signature ⇒ 401, unknown account ⇒ 404; provider
message-id dedup; inbound heals health); sends through `MoreChannelSender`
(called by `OutboundSender`; no configured account ⇒ sandbox); reply windows in
`ChannelWindow` (WeChat / TikTok / Douyin 48 h, Zalo 7 days; LINE, Viber and SMS
none), enforced like Messenger's (`OutboundSender.SocialWindowBlock`: 409 on the
inbox routes, failed job, blocked AI draft) and shown as `window_open` on the
thread payload, the inbox `window` filter and the channel hub; account fields +
masked round-trips in `MoreChannelAccounts.cs`.

When you fix one of these, update this table, `README.md` and
`docs/channel-adapter.md` in the same change.

## Architecture that isn't obvious from the tree

- **The database is one EF Core context** (`ChatBranch.Data/AppDbContext.cs`)
  over SQLite or PostgreSQL with **Django's table names** (`crm_*`, `ingest_*`, `auth_user`,
  `integrations_integration`) and snake_case columns. There are no EF
  migrations: `EnsureCreated()` builds a fresh DB and `Seed.UpgradeSchema`
  (append-only, idempotent `ADD COLUMN` / `CREATE TABLE IF NOT EXISTS`) upgrades
  an old SQLite one — a new column or table MUST be added there too. On Postgres
  that SQLite DDL is skipped: `PostgresSchema.Sync` adds missing tables / columns /
  indexes from the EF model itself (a new object's value as the default for
  existing rows), so the model is all Postgres needs. Postgres keeps SQLite's
  semantics: DateTime columns are `timestamp without time zone` read back
  unspecified (`UnspecifiedDateTime`), and `PostgresTranslators.cs` turns
  `DateTime.UtcNow` in a query into `timezone('UTC', now())` and makes
  `EF.Functions.Like` case-insensitive. Index filters use `"col"` quoting
  (portable), never `[col]`; a raw SQL statement that may fail must not run inside
  a Postgres transaction (a failed statement aborts it). Entities live in
  `ChatBranch.Domain/`. Field reference: `docs/DATABASE_SCHEMA.md`.
  **Write messages through `Stores/ThreadStore`**: `GetOrCreateThread` finds
  or creates the conversation of the RECEIVING account's company — one per
  customer PER COMPANY, unique `(channel, key, company_entity_id)` (the
  company = the top-level entity, set at creation and never moved; null =
  unattributed, one per `(channel, key)`; `Stores/ThreadCompanies.cs`) — adopts
  the account and a contact that company may see (`ContactCompanies.MayLink`)
  and re-syncs the thread's entity inside its company;
  `AppendMessage` dedupes on `(thread, external_id)`, stamps
  `Message.AccountId`, reopens a resolved/snoozed thread on inbound and bumps
  `Thread.UpdatedAt` — the SSE change token. A bulk `ExecuteUpdate` on
  messages/threads must bump `UpdatedAt` itself or the dashboard won't refresh.
- **Entities are a TREE (`ingest_entity.parent_id`).** A top-level entity
  (parent null) with branches/outlets under it; a child ALWAYS inherits its
  parent's tenant, so the dashboard asks for a **parent**, never a tenant slug.
  `POST/PATCH /api/v1/entities` take `parent_id` (number or numeric string —
  hence `ParentPk()`) or `parent` (slug); 0/""/null = top level. Guards: no
  self-parent, no cycles (`IsDescendantAsync`), same tenant, and DELETE refuses
  while children exist. Pickers render the tree with
  `components/ui/EntityPicker.tsx` (`entityTree`, `EntityPicker`,
  `EntityMultiPicker`). Tenant still exists underneath — it is simply never
  typed in by hand. ("Brands" in `/api/v1/brands` are entities.)
- **Entity scope flows UP the tree.** `ScopeOwned` narrows to an entity's
  SUBTREE (`ScopingService.EntitySubtreeIds`): head office reads a branch's
  contacts/threads, a branch never reads head office's. Agents/custom roles are
  additionally bounded by their ASSIGNED entities
  (`ContactsEndpoints.AssignedEntityIds`; conversations via
  `Endpoints/ShellScope.cs`) — an out-of-scope `?entity=` narrows to their own
  scope instead of widening; an agent with NO assignment keeps the tenant-wide
  view (not a lockout).
- **Entity scoping = global default → entity override, resolved DOWN the tree.**
  `ChatBranch.Data/ScopingService.cs` is the ONE place the rule lives: *the
  nearest level that sets something wins; the global one is the fallback*.
  Three verbs, and picking the wrong one is the bug the class exists to prevent:
  `SettingValue(key, entityId)` (config, ONE value: the entity's own row → its
  parent's → … → the top-level entity's → global row → ""; `SettingSource`
  also says which level answered, `SettingScope` = entity | parent | global;
  `EntityChain` = the entity + its ancestors, nearest first),
  `ScopeInherited(q, entityId)` (libraries an outlet INHERITS — knowledge,
  canned/script templates, skills, workflows, tags, teams, channel accounts,
  lessons: its own PLUS every ancestor's PLUS the global ones; never a
  sibling's or a child's) and `ScopeOwned(q, entityId)`
  (records that belong to exactly ONE outlet — contacts, threads, AI drafts,
  analytics). `entityId == null` means "All entities" for the Scope* helpers and
  "the global value" for the setting helpers.
  **Multi-entity libraries ("Available to").** Quick replies, call scripts,
  knowledge, AI skills, automation rules and background AI tasks (`IMultiEntityItem`) may be ticked
  for SEVERAL entities: `EntityId` stays the PRIMARY (first tick), the others
  are `crm_libraryentity` rows (`kind`, `item_id`, `entity_id`); none = global.
  Visibility is ONE rule, `ScopingService.LibraryVisible` (global, or the
  primary, or a tick in the reader's set), which `ScopeInherited` and
  `ShellScope.ScopeLibrary` both call — so a row ticked for A and B reaches A,
  B and their branches, never a sibling; a legacy single-`EntityId` row works
  unchanged. Writes: `entities: [slug|id…]` (or the older `entity`) through
  `Endpoints/LibraryScope.cs` (parse + permission: every added/removed entity
  must pass `AiControlsEndpoints.MayEdit`; going global — or narrowing a global
  row — needs `MayGoGlobal`: an UNBOUND caller — superadmin, an org-wide admin or any
  capability holder with NO assigned entities), stored by `Data/LibraryEntities.Set` (also `Load` for payloads
  `entities: [{id, slug, name}]` + `global`, `Clear` on delete,
  `OnEntityDeleted`). The skills PUT keeps the scope it is saved in as the
  primary. UI: `components/ui/AvailabilityPicker.tsx` (◉ Everyone / ◯ Selected
  entities + `EntityMultiPicker`, up front in every editor; `AvailabilityChips`
  in the lists; `useDefaultAvailability` = Everyone for admins, else the
  caller's entity). Tests: `MultiEntityLibraryTests`.
  **`Setting` is not key-unique**: a nullable `EntityId` with two partial unique
  indexes (one global row per key; one override per `(key, entity)`), so a raw
  read MUST say `EntityId == null` or another outlet's override leaks into the
  org-wide value. ADDITIVE keys (house rules `ai_house_rules`) read every level
  with `SettingValuesDownTree` (organization first). Only keys in
  `ScopingService.EntityScopedSettings` may be
  overridden (persona, quiet hours, pacing, routing rules, AI review/behaviour,
  the AI switch/schedule/never-tags, the learning mode…);
  credentials and webhook secrets are organization-wide BY DESIGN.
  **`ChannelAccount.EntityId` is the linchpin**: the number that receives a
  message decides which outlet the conversation belongs to.
  `ScopingService.ThreadEntity` = receiving account's entity → linked contact's
  entity → null ("unassigned", shown in every view rather than guessed at),
  denormalized onto `Thread.EntityId` and re-synced by `SyncThreadEntity` at
  EVERY seam that moves a thread (creation, account switch, contact link/unlink,
  account re-binding); a new write path that sets `thread.AccountId` or
  `thread.ContactId` must call it too. Endpoints take `?entity=<slug>`
  (`InboxEndpoints.EntityId` resolves it; scoped lists then bound it by RBAC).
  Frontend: `useEntityQuery()` (`lib/EntityContext`) is the only way pages build
  the parameter, and it belongs in the fetching effect's dependency array —
  omitting it is what leaves a page showing the previous outlet's data.
  **The switcher is multi-select** (`EntitySwitcher.tsx`): a name shows only that
  entity, its checkbox adds it to the view; each ticked entity brings its
  branches; the pick is remembered per browser (localStorage
  `chatbranch-entities`) and a first visit starts on the first company, never
  "All"; it re-loads on every sign-in / sign-out (`auth-change`, fired by
  `login()` / `clearAuth()`) and on a `storage` event from another tab. Its
  list is `AdminEndpoints.SwitcherEntitiesAsync` (whoami + `GET /api/v1/entities`):
  admins = `AccessibleEntitiesAsync`; an ASSIGNED agent = their entities PLUS
  every branch under them (never wider than `ScopeOwned` already reads — it only
  lets them narrow to a branch); an unassigned agent = the tenant's entities.
  `useEntityQuery()` sends `entity=a,b`; on a GET the API's
  `UseEntitySelection` middleware publishes the ticked ids — cut to the caller's
  `SwitcherEntitiesAsync` — as `EntitySelection` (Data, async-flow like the key
  scopes); `InboxEndpoints.EntityId` returns the first pick and `ScopeOwned` /
  `ScopeInherited` / `ScopingService.PickSubtree` widen it to every pick (use
  `PickSubtree` for a picked filter, never for an assignment). Pages that read
  or EDIT one level (persona, AI behaviour + learning mode) use
  `useEntityLevelQuery()` (the single ticked entity, else the organization) +
  `<MultiEntityNotice />`; `activeEntity` is null unless exactly one is ticked.
  Tests: `EntitySelectionTests`.
  Tests: `ScopingServiceTests.cs`.
- **Auth + RBAC.** `POST /api/auth/login` issues a bearer token stored as one
  `AuthToken` row per session (concurrent sessions allowed; `Profile.Token` is a
  legacy fallback) — `Auth/TokenAuthenticationHandler.cs`. **API keys**
  (`cbk_<prefix>_<secret>`, Bearer or `X-Api-Key`, `Auth/ApiKeyAuth.cs`) act as
  their owning user, optionally narrowed by scopes (a scoped key with no
  `.manage`/`.send` capability is read-only). A key's scopes bind EVERY
  capability check — the route filters and the in-handler
  `AdminEndpoints.HasCapabilityAsync` calls alike, superadmin's keys included —
  because `ApiKeyAuth.UseKeyScopes` (right after `UseAuthentication`) makes them
  ambient for the request (`ApiKeyAuth.CurrentScopes`). Roles: superadmin / admin /
  agent / custom (`Role` + `RolePermission`). Capabilities are data-driven:
  the catalog is `AdminEndpoints.Catalog` (41 entries — keep it in step with
  `CAPABILITY_CATALOG` in `frontend/src/lib/auth.ts`; `PermissionCatalogTests`
  fails when the two differ or when code gates on an id the Permissions page
  doesn't list); **superadmin is always
  allowed in code**, a missing matrix row falls back to the code default, admin
  gets everything except `rbac.manage` / `app_keys.manage` /
  `settings.ai_keys.manage` / `billing.manage` / `settings.ai_hard_rules.manage` /
  `settings.raw.manage` / `testlab.run`, agent gets `contacts.view chats.view chats.send
  flows.view analytics.view call_logs.view activity.view tags.view ai.monitor`.
  **Every operator action follows the Permissions page:** a route's permission
  gate is a CAPABILITY, never a role — `.RequireCapability` /
  `.RequireAnyCapability` (`Auth/CapabilityFilter.cs`) on the `authenticated`
  policy, or `AdminEndpoints.HasCapabilityAsync` /
  `SettingsEndpoints.RequireCapability` in the handler; the `admin+` /
  `superadmin` policies are unused (`PermissionMatrixTests` fails on a route that
  carries one, and checks every converted route: the capability alone opens it,
  an admin denied it is refused, the built-in defaults hold). A new action = a
  capability that names it (add a catalog entry — both catalogs, en/ms/zh labels
  under `admin.caps` — only when none fits). MCP tools follow their endpoints'
  gates (`ToolRegistry.EndpointGates` reads the route table). Anti-escalation
  stays role-keyed: below superadmin the matrix editor (`rbac.manage`) only
  switches capabilities its holder holds and never the superadmin row; a user
  manager never hands out a role holding a capability they lack, never edits
  their own login or entity access, never acts on a user above their tier.
  **Company admins:** an admin
  WITH assigned entities is a company admin — admin capabilities bounded by
  those entities' subtrees exactly like a bound agent (contacts, inbox, drafts,
  analytics, channels, libraries, bookings, activity, the AI bill/quotas, users,
  the entity switcher); an admin with none stays organization-wide. ONE rule:
  `ShellScope.ScopeRoots(db, me)` (empty = unbound: superadmin, org-wide admin,
  unassigned agent) with `IsBound` / `IsCompanyAdmin` / `IsOrgWideAdmin` /
  `MayActAt` / `CompanyLevelError` / `OrgWideOnly` / `OrgWideAction` /
  `ObjectScopeError`; every scope helper builds on it — never test
  `role == Roles.Admin` for a scope shortcut. `Auth/CompanyAdminScope.cs` drops
  the org-level capabilities (`rbac.manage`, `app_keys.manage`,
  `settings.ai_keys.manage`, `billing.manage`, `notifications.manage`,
  `settings.appearance.org`, `settings.custom_fields.manage`,
  `pronunciation.manage`, `settings.ai_hard_rules.manage`, `settings.raw.manage`,
  `testlab.run`) for them — and for EVERY bound person; global library items,
  top-level entities, org-wide settings stay with unbound holders. A bound agent
  or custom role granted an admin capability acts exactly like a company admin
  with it (`CompanyLevelError` / `MayActAt` / `OrgWideOnly` apply to every bound
  caller; no-entity actions — the voice library, MCP servers, raw settings read,
  journey enqueue / tick, record sync — carry `OrgWideAction`: unbound holders and
  company admins only). Only a superadmin makes an
  org-wide admin; an org-wide admin may make company admins (`entity_ids` on an
  admin). `whoami.company_admin`; details in `docs/RBAC_AND_BRANDS.md`
  ("Company admins"), tests `CompanyAdminTests`. **Every BOUND person**
  (company admins, agents and custom roles with entities — `whoami.bound`) acts
  only inside their subtrees whatever their capabilities: org-level AI writes
  (behaviour, controls, persona, safety rules, re-index, the monitor tick, an
  org lesson's delete) are 403, a sibling's AI levels 404, and their feedback /
  monitor decisions never create, merge or demote an org-wide lesson
  (`LearningActor.Bound` / `AutoApproves`); the UI never puts them on "All
  entities" or "Organization" (`EntityContext` `bound` / `levelEntity`) —
  "Bound users never act at the organization level" in the same doc, tests
  `EntityBoundAiScopeTests`. **There is no fallback policy:
  every route must carry `RequireAuthorization(...)` or an explicit
  `AllowAnonymous()`** — the anonymous set is listed in
  `docs/API_CONVENTIONS.md`. User management (`/api/v1/users`, `users.manage`) is tiered:
  superadmin manages everyone; an org-wide admin lists their own tenant and
  manages agents (and company admins) there; a company admin only the
  agent-level users bound inside their companies; a custom role granted
  `users.manage` like an admin of its scope, agent-level users only. A login may be bound to a Contact (`Profile.ContactId`,
  "customer login"). **Invites:** `POST /api/v1/users` takes `email` + `send_invite`
  (default on when sign-in emails can go out) — a welcome email with the username,
  the login address and a ONE-TIME set-password link (72 h, only its SHA-256 in
  `crm_passwordtoken`, never a password in an email; the login holds an unusable
  `!…` password until then); `POST /api/v1/users/{id}/invite {reset?}` resends /
  sends a reset link under the same tiers (≤ 5 / person / hour); the public half is
  `GET/POST /api/auth/set-password` + the bare `/set-password` page; mail goes out
  through Settings → System email, else the default mail settings
  (`Api/Security/PasswordLinks.cs`, `SystemEmail.cs`, `WelcomeEmail.cs`; no
  "Forgot password?" by design; `WelcomeEmailTests`). The `X-Role` header only works while NO Profile rows exist
  (pre-seed bootstrap). **Per-conversation routes**
  (`/api/whatsapp/threads/{key}/…`, `/api/messaging/threads/{channel}/{key}/…`,
  the inbox's `{channel}/{key}` routes, `/api/inbox/threads/{channel}/{key}/handover`)
  carry `.RequireThreadAccess("chats.view"|"chats.send")`
  (`Auth/ThreadAccessFilter.cs`): the capability AND the inbox's entity scope
  (`ShellScope.ThreadEntityIds`), out of scope = 404 (the multipart
  `…/{channel}/{key}/send-media` in `MessagingTransportEndpoints` included).
  Routes that name the conversation in the BODY apply the same rule with
  `ThreadAccessFilter.InScope` (`POST /api/aicore/reply`, and starting a chat —
  `POST /api/messaging/threads` / `POST /api/whatsapp/threads`, `chats.send`:
  an existing out-of-scope conversation is a 404, a new one never links a
  contact or a number from outside the caller's scope, and moving an existing
  conversation to another number stays the per-chat switch, `account_assignment.manage`). Linking needs a contact in
  the caller's scope, the contact lookups / timeline are scoped, both thread
  lists need `chats.view` (details in `docs/RBAC_AND_BRANDS.md`). Frontend gating
  (`lib/useAccess.ts`, `nav.ts canSee`) is not the control — the server is — but
  `components/shell/RouteGuard.tsx` renders a "no access" panel instead of any
  page whose sidebar entry (`nav.ts routeDef`, detail routes + `alsoActive`
  included) the caller can't see, so the page never mounts or fetches; nothing
  renders until the session is read, and someone without the Dashboard lands on
  `firstVisibleHref`. **Libraries** (skills, knowledge, quick replies, call scripts,
  integrations) are read through `ShellScope.ScopeLibrary` — the org-wide rows
  plus the caller's assigned outlets for agents/custom roles, never another
  outlet's. Integrations honour `integrations.view` / `integrations.manage`;
  a personal connection (`Integration.OwnerUserId`) is always its owner's to
  see and disconnect, and running an action needs `integrations.manage` or
  `flows.manage`. **Read gates.** Every signed-in `/api` GET carries the
  capability of the page(s) that read it: `.RequireCapability("x")` /
  `.RequireAnyCapability("x","y")` (`Auth/CapabilityFilter.cs` — 403 before the
  handler; stamps `RequiredCapabilities` metadata), `.RequireThreadAccess(...)`
  on per-conversation routes (never a role policy). A lookup shared by several pages
  opens for ANY of their capabilities and may return a narrower shape to the
  weaker one (`/api/whatsapp/accounts` → picker shape for the inbox;
  `/api/bindings/catalog` without user/team/account pools for the Tags page).
  The only ungated signed-in reads are the shell and the caller's own things
  (whoami, preferences, notifications, saved views, appearance, entity switcher,
  Live chip, search, stream, own API keys / connections / assistant history) —
  each listed with its reason in `PermissionGatesTests.AnySignedInUser`, whose
  route-table walk fails on any new GET that is neither gated nor listed. A new
  GET = its gate + a row in `PermissionGatesTests.Gates` + a row in the Read
  gates table of `docs/RBAC_AND_BRANDS.md`, which holds the full gate table.
- **Security** (`Api/Security/`, guide `docs/security.md` — every knob, the
  incident checklist). All knobs in ONE singleton, `SecurityOptions` (from config /
  env; tests swap it as a service — `SecurityFactory` / `RealInstallFactory` /
  `RateLimitedFactory` in `tests/SecurityFactory.cs`, which also give each request a
  client IP via `X-Test-Peer`). Pipeline order in `Program.cs`: `UseTrustedProxies`
  (FIRST — `X-Forwarded-For/-Proto` believed only from `TRUSTED_PROXIES`, default
  loopback + private ranges; read the client IP with `ClientIp.Of(ctx)`, never the
  header) → `UseSecurityHeaders` (nosniff / no-referrer / noindex everywhere, a
  `default-src 'none'` CSP + DENY on data, a sandbox CSP on HTML/SVG, HSTS on https,
  no `Server`) → CORS → `UseApiKeyGate` → auth → `UseRateLimiter` (`RateLimiting.Classify`:
  login / widget / public-link / media / oauth per IP, signed-in per session, webhooks
  exempt; 429 `{detail, code: "rate_limited"}`; `RATE_LIMIT=0` off) →
  `UseSessionGate`. **`LoginThrottle`** (singleton, in memory) is the ONE place
  failed passwords / codes / API keys are counted — per username AND per IP, a lock
  answers 429 `too_many_attempts` before the password is checked (an IP lock spares
  people with a `login` from that address in the last 30 days — `LockedForAsync`),
  `Report` audits `login.locked` + one collapsing admin alert; a new password or code
  check must `LockedForAsync` → `Fail` / `Succeed` through it. **Two-step sign-in** = `TwoFactorService`
  (`crm_usertwofactor`; TOTP secrets under ASP.NET Data Protection, keys in
  `<data dir>/keys`; hashed recovery codes; a 5-minute signed login challenge →
  `POST /api/auth/login/2fa`, anonymous) + `Endpoints/TwoFactorEndpoints.cs`
  (`/api/auth/me/2fa*` session-only, `/api/settings/security` = the `require_2fa`
  policy, capability `security.manage`, superadmin-only, org-level) + the admin reset
  `POST /api/v1/users/{id}/2fa/reset`. **Restricted sessions**: `AuthToken.Restriction`
  `change_password` (a seeded password on a real install — `DefaultCredentials`) or
  the derived `enroll_2fa` → `SessionGate` answers 403 `account_setup_required`
  except its allowlist (whoami, logout, own account / password / 2FA setup, the
  shell's appearance reads); whoami + the login answer carry `restriction`; the
  dashboard's `/account-setup` (rendered bare by the Shell) handles both. New
  passwords go through `PasswordPolicy.Check` (≥ 8 — `PASSWORD_MIN_LENGTH` — on a real install, not the
  username, not common; 8 on a demo). AI: `PromptGuard.Rules` ends every
  customer-facing prompt (not editable), customer text in a system prompt goes
  through `PromptGuard.Inline`, side-effect tools are capped per conversation by
  `AiActionCaps` (`AI_CAP_*`), Sheets cells through `IntegrationActions.SheetCell`.
  Dashboard headers + CSP live in `next.config.js headers()` (verify a change in a
  real browser: zero CSP violations), `app/robots.ts`. CI: the separate
  **Security** workflow (`.github/workflows/security.yml` — NuGet + npm audit via
  `scripts/npm-audit-gate.mjs` and `.github/security/npm-audit-allowlist.json`,
  gitleaks with `.gitleaks.toml` / `.gitleaksignore`). Tests: `SignInSecurityTests`,
  `TwoFactorTests`, `DefaultCredentialsTests`, `RateLimitTests`, `AiAbuseTests`.
- **Voice: the API is the control plane, the agent is a file-IPC sidecar.**
  `POST /api/calls/dial` and WhatsApp `call/start` build the dispatch metadata
  in `Sidecars/VoiceDispatch.cs` (contact, primary record, memory, knowledge,
  skills, voice/brain/STT, script — the contract is documented key by key in
  `backend/voice_agent/src/metadata.ts`, keep both in step) and dispatch the
  agent by `AGENT_NAME` through `Sidecars/LiveKitClient.cs` (HS256 tokens +
  Twirp JSON; SIP participant via `SIP_OUTBOUND_TRUNK_ID`); `/hangup` deletes
  the room. No LIVEKIT_* / trunk → a **503 naming what to set**, never a fake
  call. **Every call endpoint requires a signed-in user** (`/api/calls/*`,
  `/api/whatsapp/threads/{key}/call/*`). The agent never touches the database:
  `voice_agent/src/storage.ts` ↔ `Sidecars/VoiceAgentBridge.cs` share, atomically,
  `live/{room}.json` (transcript, `done`, `ended_by`, `llm_usage`),
  `inbox/{room}.json` (operator `{mode: guide|takeover, text, ts}` list,
  consume-once), `calls.json` (finished records — folded into `CallRecord` +
  `LlmUsage` by `Sidecars/VoiceCallSync.cs`, idempotent by call id),
  `memory/{contact_id}.md` and `pronunciation.json`.
- **The unified inbox.** Every channel is a `Thread` (unique `(channel, key,
  company)` — one conversation per customer per company; a route that names
  `{channel}/{key}` resolves the caller's ONE through `Auth/ThreadResolver.cs`:
  a bound person only their company's, anyone who sees several passes
  `thread_id` / `company`, else 409 with the candidates — never a silent pick)
  + `Message` rows. `/chats` is a three-pane inbox → chat → info panel; its list
  is SERVER-filtered by `GET /api/inbox/threads`
  (`Endpoints/InboxThreadsEndpoints.cs` — status, channel, account, assignee,
  team, entity, labels, contact tags, AI on/off, 24h window, opt-out, waiting,
  live call, linked, handover (`open`/`any`/`none` + `handover_kind`), date
  ranges, sorts, cursor paging; `count_only=1` powers the filter pop-up's live
  "Show N chats"). `sort` = newest | customer_newest | oldest_waiting | unread;
  customer_newest = `LastInTs` descending (the customer's last inbound message),
  never-wrote chats last, ties by activity then id, paged by the offset `cursor`
  (`before` is for newest only); the merged view orders a contact's entry by its
  newest inbound across channels. The list header carries All / Contacts / Not in
  contacts (= `linked`) and Newest message / Newest from customer
  (`components/chats/Segmented.tsx`); the order is per person (`Profile.Preferences`
  `chats.sort` via `/api/auth/preferences`, `components/chats/chatSortPref.ts`). A conversation with NO messages (a chat opened from a contact
  and left) is not listed — only `thread=<channel>:<key>` still finds it, so the
  open chat's scope check keeps a freshly started chat open. The filter UI is `ChatFilterModal.tsx`; the filter tabs are
  per-user `SavedView` rows (`/api/views?scope=chats`, `lib/savedViews.ts`) next
  to a built-in "Needs human" tab. **Select mode** (`BulkBar.tsx`) drives `POST
  /api/inbox/threads/bulk` (`chats.manage`; picked conversations or everything a
  list filter matches, scoped like the list, ≤1000, one audit row; `ai_on` |
  `ai_off` | `resolve` | `reopen` | `assign`). Messaging payloads carry `pk` (the
  message id) so AI bubbles get 👍/👎 (`components/ai-learning/BubbleFeedback.tsx`,
  see AI learning). Deep links: `/chats?thread=<channel>:<key>`,
  `/chats?q=<text>`; the open chat IS the URL (opening one replaces `?thread=`,
  pushes on a phone so Back returns to the list; an unknown or out-of-scope
  `?thread=` shows "Conversation not found"). The composer's `/shortcut` picker:
  ↑/↓ move, Enter/Tab insert, Esc closes (Enter never sends the shortcut); quick
  replies are fetched for the conversation's own entity, and a reply still
  holding `{{…}}` placeholders gets an inline warning. The page does NOT poll — `lib/chatStream.ts` holds ONE SSE
  connection per tab to `GET /api/whatsapp/stream` (all channels; the change
  token is `ThreadStore.ChangeToken()` = thread `UpdatedAt` + message count),
  ref-counted with a 2 s close grace so page navigation doesn't drop it, and
  exposes a connection status (connected / reconnecting / offline). Older
  per-channel endpoints (`/api/whatsapp/threads/{key}/…`,
  `/api/messaging/threads/{channel}/{key}/…`) still carry the thread actions
  (send, template, media, AI tick, status, assign, labels, note, link, calls);
  `POST /api/inbox/threads/{channel}/{key}/handover` takes / hands back / flags
  (see Human handover).
  Bubbles are theme tokens in `src/app/chats/chats.css` (ours = primary ink,
  theirs = a hairline card) — no WhatsApp greens. **Bubble extras** live in their
  own components (`components/chats/MessageActions.tsx` — hover / long-press bar:
  quick reactions + more, Reply, Copy text; `ReactionBar.tsx` chips;
  `QuotedBlock.tsx` quote + "jump to original" (`#msg-<pk>`) + the composer's
  "Replying to"; `MessageContent.tsx` location / contact-card / sticker
  rendering; `EmojiPicker.tsx` — a built-in ~300-emoji set with en/ms/zh search
  words in `lib/emoji/data.ts`, recents in localStorage `emoji-recent`; strings
  in the `bubble` i18n namespace). Payloads carry `reply_to` + `reactions`
  (`Endpoints/MessageExtras.cs`); quotes and reactions are WhatsApp + Telegram
  only (`REACTION_CHANNELS` / `MessageReactions.Supports`). `POST /api/whatsapp/simulate`
  fakes an inbound message through the same side effects as a real webhook
  (contact link, flows, team routing, automations, notifications; the invite
  binder, the unknown-customer step and the AI step only with `?ai=1`).
- **Inbound path** = `Channels/InboundProcessor.ProcessInbound`, shared by every
  webhook: get-or-create the thread → the **invite binder**
  (`Contacts/ContactInvites.cs`: `[ref:CODE]`, `(ref:CODE)`, `(ref CODE)`, a
  loose `ref CODE`, Telegram `/start CODE` link the invited contact BEFORE
  matching; the marker is
  stripped, the original kept in the note, audit `contact.invite_used`; Messenger
  `referral.ref` is bound in its webhook) → link a contact (email exact, else
  last-8-digit phone on WhatsApp and SMS, `Stores/PhoneMatch`) → move the thread
  onto the RECEIVING number (`Reattribute`: only between numbers of the SAME
  company — another company's number has its own conversation) → the inbox's AI default on a brand-new thread → store
  (dedup) → STOP/START (`Channels/OptOut`) → resume flow runs parked on a reply →
  team auto-assign (`TeamsEndpoints.AutoAssignAsync` via the
  `InboundProcessor.TeamRouter` hook set in Program.cs) → automation rules →
  `NotificationService.NotifyInboundMessage` → **unknown customers**
  (`Contacts/IdentityRequests.cs`: per-inbox `identify_unknown` off | ask |
  ask_and_create, or the `ask_identity` automation; a deterministic EN/BM
  exchange — one match → linked, none → created under `ask_and_create`; several
  matches, no contact under `ask`, or giving up → an `unknown_customer` handover;
  at most 2 asks, a 24 h sweep closes unanswered ones, opted-out chats are never
  asked; the asks are SYSTEM messages, queued on live accounts) → expire stale AI
  drafts →
  **coverage gate** (linked contact AND the EFFECTIVE verdict
  `Ai/AiEligibility.Decide` AND not opted out) → AI reply (debounced/queued or instant,
  see AI monitor); else on a reminder number the business redirect. Nothing in
  it may throw into a webhook. **Nothing on the inbound path drops a message
  because the sender is not a contact:** every channel opens or continues an
  UNLINKED conversation (`linked=0`, "Not in contacts"), and only the coverage gate
  keeps them from the AI (the website widget excepted). Email skips only
  `Auto-Submitted` mail, its own From address and Message-ID duplicates
  (`UnknownSenderAsserts` + one test per channel).
- **WhatsApp specifics.** Accounts are `ChannelAccount` rows (provider `cloud`,
  `ycloud`, `twilio` — the three OFFICIAL ones, `WhatsAppProviders.IsOfficial` —
  or `baileys`; ask `WhatsAppProviders`, never compare to `"cloud"`); `WHATSAPP_*` env keys surface as a read-only `env-default`
  account. The Cloud webhook routes by `metadata.phone_number_id`; the verify
  token lives in Setting `whatsapp_verify_token` (legacy `verify_token` is kept
  in sync). **24h-window truth:** inbound rows store Meta's own timestamp,
  because webhooks can arrive hours late; free text into a closed window → 409
  (use a template). **Per-account delivery + AI settings**
  (`Channels/AccountDelivery.cs`): quiet hours (`quiet_start`/`quiet_end` or a
  per-weekday `quiet_schedule`), send pacing (`queue_min_gap_s`/`queue_max_gap_s`)
  and the voice-call `llm_provider`/`stt_provider` live in
  `ChannelAccount.Config`, resolved account → entity/global Setting → env →
  default in ONE place, applied by the queue drain at SEND time. **Account
  roles:** `Config["role"]` = `reminder` (Cloud business number) or
  `conversation` (Baileys personal number), defaulting by provider. **Is the AI
  on for a conversation = ONE function, `Ai/AiEligibility.Decide`**: opted out →
  the **AI usage quota** (the conversation's entity or ANY ancestor at its monthly
  cap, `Ai/AiQuota.cs` — rule `quota`, forced, reason "Off: AI quota reached for
  <entity> (RM x of RM y this month)") → the **master switch / schedule** (Setting `ai_enabled` on | off | schedule;
  `ai_schedule` per-weekday windows + `ai_schedule_mode` inside | outside, org
  timezone; resolved UP the entity tree, nearest level wins, `off` wins) → the
  contact's `ai_mode` `never` or a **never-AI tag** (`ai_never_tags`, which add up
  across levels) → the **per-chat tick** (per sending account on WhatsApp,
  `Thread.AiOverrides {account_pk: bool}`; account-less threads mark an
  operator's decision as `AiOverrides["thread"]`; an account-less OFF stays off)
  → the contact's `always` → the inbox's `ai_default` → the role (reminder numbers
  off unless the `reminder_ai_default` rule is on) (`Ai/AiControls.cs` resolves
  the settings in bulk). The verdict carries `ai_rule` + `ai_reason`, and
  `ai_forced` when a rule above the tick decided (the chat header's switch is then
  disabled with the reason as its tooltip). Every payload (`ai_enabled`) AND
  every gate (inbound, queued ai_reply drain, AI monitor send-time guard,
  follow-ups, flow AI steps — skipped with a note) reads it — never
  `Thread.AiEnabled` directly; a draft a person approves still sends. Write a
  tick with `AiEligibility.Set` (per-account tick + legacy column). API: `GET/PUT
  /api/ai/controls?entity=` (GET `ai.monitor` / `ai.behaviour.manage` /
  `chats.view`; PUT `ai.behaviour.manage`, the organization level only for an unbound
  caller (`ShellScope.MayActAt`), an outlet must be in the caller's subtree), `GET/PUT /api/contacts/{id}/ai-mode` (PUT
  `contacts.manage`); UI: the master-switch card on `/ai-monitor`
  (`components/ai-controls/AiMasterSwitch.tsx` + `AiScheduleEditor.tsx`), the Live
  chip's quick toggle (`AiQuickToggle.tsx`) and "AI for this contact"
  (`ContactAiModeCard.tsx`).
  Routing-rule knobs live behind `GET/PUT /api/whatsapp/routing-rules` (PUT
  `account_assignment.manage`; `ai_reply_mode` queued|instant, `cold_text_guard` block|warn|allow,
  `cold_call_guard`, `reminder_ai_default`, `business_redirect*`,
  `recovery_mode`/`recovery_template`, org-wide `opt_out_keywords` /
  `opt_in_keywords` that ADD to STOP/UNSUBSCRIBE/BERHENTI · START/UNSTOP/MULA).
  **Business redirect** (`Channels/BusinessRedirect.cs`): inbound on a
  reminder number with the chatbot off → ONE queued answer per 24h (throttle =
  OutboundJob rows with `payload.redirect`), pinned to that number, target
  `conversation` (wa.me link to the contact's conversation number — account
  `Config.phone`/`number`, else a numeric name), `support` or `template`.
  **Recovery** (`Channels/Recovery.cs`): a conversation number going INTO
  banned/disconnected notifies "Account X down — N contacts affected" and, with
  `recovery_mode=auto`, queues `recovery_template` per assigned contact (24h
  dedup; also `POST /api/whatsapp/recovery/{id}`). `Message.AccountId` records which
  number carried each message; the UI shows "via <account>" and a divider at
  sender switches. Baileys: `IBaileysGateway` (`Sidecars/IBaileysGateway.cs`,
  Setting `baileys_url`/`baileys_secret` → env → default) drives the gateway;
  gateway pushes come in on `/api/whatsapp/baileys/*` authenticated by
  `X-Baileys-Secret`. ⚠ Baileys is a ToS-violating transport — dev/demo only;
  read `backend/baileys_service/README.md` + `RESEARCH.md`.
- **Inboxes (`/channels`)** — Chatwoot-style: every number, mailbox, bot, page,
  widget and the SIP trunk as a connected card (`components/channels/inboxes.ts`
  folds the existing account endpoints into one shape), "Add a channel" blocks
  grouped Messaging apps · Social · SMS, email & voice · China
  (`inboxes.ts KIND_GROUPS`; TikTok and Douyin carry a "Requires approval"
  badge), and a step-by-step setup drawer (`InboxSetupDrawer.tsx`: choose →
  connect → who answers it → test; the newer channels' test step shows the
  account's signed webhook URL). "Who answers it" rides on every account write as
  top-level keys stored in `ChannelAccount.Config` (`Channels/InboxSettings.cs`):
  `team_id` (new conversations go to that team first), `ai_default` on|off|""
  (stamped on a brand-new thread only, so a later per-chat tick wins) and
  `ai_review_mode` (per-inbox AI monitor policy); **unknown customers**
  (`identify_unknown` off | ask | ask_and_create + optional `identify_message`)
  go through `GET/PUT /api/inboxes/{id}/identify` (`channels.view` /
  `channels.manage`), saved by the drawer after the account
  (`UnknownCustomersField.tsx`); an inbox without the key inherits the Setting
  `identify_unknown`, else off. Deleting a messaging account
  or a widget KEEPS its conversations (`InboxSettings.DetachAccountAsync`).
  `ChannelsAdvanced.tsx` shows the Meta webhook URL + verify token (edited at
  Settings → App & provider keys) and the default mail settings; the
  routing-rules card sits on `/assignments`.
- **Account assignment (`/assignments`, a Work module)** — sticky
  `AccountAssignment` rows (contact × channel → account, `Pinned`), edited via
  `GET/PUT /api/whatsapp/assignments?channel=…`; `POST
  /api/whatsapp/assignments/rebalance` (`AssignmentsEndpoints.cs`) spreads
  unpinned rows evenly over eligible accounts. Gate:
  `account_assignment.manage`. Per-chat switch: `POST
  /api/whatsapp/threads/{key}/account` (`account_assignment.manage` + the thread's
  `chats.send`; the target number must be one the caller's entities may use) pins the assignment and appends
  a system note. **Send-time routing** is `Channels/Routing.ForThread` (queue
  drain, manual sends, the email mailbox / Telegram bot choice and the AI
  gate's instant, approved and auto-sent replies; payload `account_id` pins a
  job): the thread's healthy
  number → sticky row → least-loaded auto-assign (an unpinned row) → failover
  off a banned/disconnected number (row moves, pin cleared, thread moves, system
  note, `account.failover` audit, admin notification). Conversational sends only
  fail over to conversation numbers; templates go to a Cloud number (reminder
  first) per send without moving the sticky row, never to Baileys — none
  eligible ⇒ the job fails / the endpoint 409s, thread untouched. Entity rule:
  an outlet's own numbers (nearest level of the tree), else the global ones; an
  unattributed conversation (no entity) only ever takes an ORG-WIDE number
  (`Routing.Pool`), never an outlet's.
- **Channel health.** `Channels/AccountHealth.SetStatus` is the ONLY writer of
  `ChannelAccount.HealthStatus` (unknown/healthy/degraded/disconnected/banned):
  one `account.health` AuditLog row per real transition (detail
  `"prev → next — reason"`, which the Dashboard's health card parses). Writers
  today: Baileys lifecycle pushes (`/api/whatsapp/baileys/status-event`),
  inbound traffic (heals), email/Telegram send outcomes and the periodic
  prober (`Channels/HealthProber.cs`, `HealthRecorder` decides the verdict —
  per-recipient failures never touch an account). Saving new credentials
  resets an account to `unknown` silently and re-probes it (`HealthRecheck`).
  `GET /api/whatsapp/channels/health` feeds the Dashboard's channel-health card;
  SIP voice health is config-presence only (`/api/whatsapp/voice-settings`, PUT
  superadmin, masked round-trip, Setting `voice_twilio_settings` with
  `TWILIO_*` env fallback).
- **Website widget** (`/api/widget.js` — also `/widget.js` for tags already
  pasted; the embed hands out the `/api/` one because a single-origin proxy
  forwards only `/api/` — + `/api/widget/{token}/*`) — the one channel
  a customer can start with no phone number. A widget IS a `ChannelAccount` with
  channel `website` (its ExternalId is the PUBLIC token in the script tag), so
  the inbox, teams, automations, AI replies and entity scoping work with no
  special-casing. The script renders into a SHADOW ROOT. Public endpoints are
  anonymous with their own permissive CORS policy (`widget`) — safe because they
  carry no credentials and expose one visitor's own conversation, keyed by a
  server-minted 128-bit session id. Admin CRUD at `/api/widgets`. A widget's
  `language` (auto | en | ms | zh, validated, handed to the public `/config`)
  picks the words `/widget.js` uses — the script carries its own en / ms / zh
  dictionary; auto follows the visitor's browser (`WidgetLanguageTests`).
- **Teams + automations.** Assignment is TWO-LEVEL: a thread belongs to a `Team`
  and optionally one agent in it (`Thread.Assignee`). Round-robin uses a stored
  cursor (`Team.LastAssignee`) and runs on inbound AND `/simulate`.
  `AutomationRule` + `Flows/AutomationService.cs` are the CONVERSATION-centric,
  event-driven counterpart to the contact-centric flow builder: on
  conversation_created / message_created / status_changed / assigned, ALL
  matching rules run in Position order. An unknown operator matches NOTHING.
  Order matters: rules run by `Position` (a new rule goes last;
  `POST /api/automations/reorder {ids}` reorders within the places those rules
  hold — the page's drag handle / arrows), and the page's Test button is
  `POST /api/automations/{id}/test` (a dry run with a per-condition
  `conditions[]` explanation, `AutomationService.Explain`).
  `/api/automations/catalog` is the single vocabulary the editor reads
  (conditions include `ai_on`, `handed_over`, `contact_linked` yes/no; actions
  include `hand_over` (a tracked handover, kind `rule`) and `ask_identity` ask |
  ask_and_create).
- **Notifications.** `NotificationService` (`ChatBranch.Data/Notifications/`,
  root namespace) is the ONLY notification writer: org mute
  `notif_<category>` (categories `messages`/`account_health`/`flows`/`admin`/
  `system`) → the recipient's own switch (`Profile.Preferences` JSON,
  `notifications{}`) → "only conversations assigned to me / my teams" for
  messages → `RefKey` collapse (one unread row per conversation per user).
  Fail-open, never throws. Inbound messages notify assignee → team members →
  the tenant's admins. `/notifications` is everyone's inbox
  (`/api/notifications*`, per-user prefs at `/api/notifications/preferences`);
  the bell keeps `/api/auth/notifications`; org defaults are
  `GET/PUT /api/auth/notification-prefs` (`notifications.manage`).
- **Activity log.** `AuditLog` carries provenance: `Source`
  (user|api|mcp|assistant|automation|flow|webhook|system), `EntityId`,
  method/path/status/user-agent/request-id. Rows are stamped from the ambient
  `AuditContext` by an EF interceptor (`Data/Audit/AuditStampInterceptor.cs`),
  so the many per-file audit helpers need no edits; new code uses
  `AuditTrail.Add/Write` and passes `source` only when it is not the request's
  actor. `Api/Audit/RequestAuditMiddleware.cs` writes ONE generic row per
  mutating `/api` (or `/mcp`) request that wrote no domain row of its own (opt a
  read-POST out with `[SkipRequestAudit]`); it never logs bodies, query strings
  or secret path segments. Read API: `/api/audit`, `/api/audit.csv`,
  `/api/audit/facets` (`activity.view`) → the `/activity` page.
- **Developer platform.** API keys at `/api/developers/keys`
  (`api_keys.manage`; token shown once, SHA-256 stored). **`POST /api/mcp`**
  (also `/mcp` — but a single-origin deployment proxies only `/api/`, so
  `/api/mcp` is what the dashboard and docs hand out) = ChatBranch as an MCP server (Streamable HTTP, JSON-RPC, stateless;
  `tools/list` cursor paging, one page by default, `MCP_TOOLS_PAGE_SIZE`) over
  `Api/Tools/ToolRegistry`: the original 16 (`BuiltinTools.cs`) plus ~230
  declarative tools (`ToolCatalog.*.cs`, `EndpointTool`) covering EVERY
  operator feature, named `domain_verb_object`, authenticated by an API key and
  acting as its owner. A tool IS a dashboard endpoint run in-process
  (`Tools/InternalApi.cs`, JSON or multipart), so every guard applies. The
  registry adds: any-of capability, the endpoint's role policy, read-only keys
  refused for writes, the entity check on `entity` args, the conversation scope
  check on thread tools, and `"confirm": true` for destructive tools over MCP
  (tool annotations carry read-only / destructive / idempotent hints). Results
  are compacted, deep-linked and redacted (`ToolResults`). What is NOT a tool
  is in `Tools/McpExclusions.cs` with its reason (credentials, RBAC, key
  minting, binary streams, legacy twins, `/simulate`, the Test lab);
  **`McpCoverageTests` fails on any signed-in `/api` route that is neither**
  and keeps `docs/mcp.md`'s tables generated (`UPDATE_MCP_DOCS=1`). A new
  endpoint = one `T(...)` line or an exclusion; a renamed route breaks its tool
  (the tests catch it). External MCP servers the assistant may use:
  `/api/integrations/mcp` (Setting `mcp_servers`, real initialize + tools/list
  probe, masked auth). The page is `/developers`; the guide is `docs/mcp.md`.
  The repo's `.mcp.json` registers `chatbranch` for Claude Code from env vars
  (`CHATBRANCH_API_KEY`, optional `CHATBRANCH_MCP_URL`, default production) —
  never commit a key there.
- **Ask ChatBranch** (operator assistant, `Api/Assistant/OperatorAssistant.cs`,
  `/api/assistant/chat|confirm|cancel`, `components/assistant/AssistantDock.tsx`,
  a side panel on every signed-in page). It uses the SAME model settings as the
  AI brain (`ILlmClient` → Settings → AI keys & models, else the API process's
  env keys); with none — or when the provider fails — it answers with the
  deterministic helper and the panel says so (`mode: "sandbox"`, a note linking
  to `/settings/ai-providers` for `settings.ai_keys.manage` holders). Acts AS the signed-in user over the same tool
  registry + allowlisted external MCP tools: reads run straight away; writes
  become confirm-first `AssistantAction` rows (15 min, one user cannot confirm
  another's). **History is per user and server-side**
  (`Api/Assistant/AssistantHistory.cs`; `crm_assistantconversation` +
  `crm_assistantmessage`; `GET/DELETE /api/assistant/conversations`,
  `GET/PATCH/DELETE /api/assistant/conversations/{id}` — the caller's own rows
  only, another login's id is a 404, superadmin included; 100 per user, least
  recently used pruned). The chat POST takes `{message, conversation_id?}` and
  the model's context is rebuilt from the conversation's last 12 STORED turns
  (with each confirm card's outcome), never from client-sent history;
  confirm/cancel/expiry update the stored card, and deleting a chat cancels
  writes still waiting in it. Each model call gets a bounded tool list
  (`Api/Assistant/ToolRouter.cs`: a core set + deterministic keyword/domain
  picks + a `find_tools` meta-tool that loads more mid-loop); the confirm button
  stands in for MCP's `confirm` argument. With no LLM it falls back to a
  deterministic helper (find contacts, counts, "go to …", how-to answers from
  `AssistantGuide.cs` + the knowledge base).
- **Help Center** (`/help`, `/api/help/articles`) — published `KnowledgeItem`s,
  readable by anyone. `Published` (public readability) is NOT `Active` (whether
  the AI may use it). `Shell.PUBLIC_ROUTES` (`/help`, `/offline`) render outside
  the operator shell with no auth guard.
- **Reports** (`/api/analytics/reports[.csv]`) — by agent / team / label /
  channel, counting conversations TOUCHED in the window, with "(unassigned)"
  reported rather than dropped; CSV fields are quoted.
- **The AI brain** (`ChatBranch.Data/Ai/`). `AssistantService` is the
  channel-agnostic generator: system prompt = the safety rules
  (`PersonaService.HardRulesText()`: org-wide Setting `ai_hard_rules`, else the
  built-in `HardRules`; edited only with `settings.ai_hard_rules.manage` —
  superadmin by default, never a company admin — via `PUT /api/aicore/persona
  {hard_rules}` ("" = the default), the "Safety rules" card; never overridable
  by a persona or house rule) + HOUSE RULES (Setting `ai_house_rules`, one rule
  per line, ADDED UP from the organization down the entity tree —
  `PersonaService.HouseRules` / `HouseRulesBlock`; an outlet can add, never
  drop) + persona (Setting `aicore_persona`, the nearest level of the tree wins,
  else `DefaultPersona`; `PersonaService.Preamble` builds the three) —
  `GET/PUT /api/aicore/persona` (`settings.ai.manage`; PUT `{persona?,
  house_rules?, entity?}`, the organization level only for an unbound caller, an
  outlet in the caller's scope, an
  outlet's empty persona drops its override; GET says where the persona comes
  from, what it would inherit, inherited + own house rules and the composed
  `preamble`). UI: `/ai-persona` "Persona & rules" (Library,
  `components/persona/PersonaRules.tsx`) and the playground's "Save as persona"
  + SKILLS block (prompt skills in scope, plus how to use each
  connected-app skill's tools) + `COMPANY KNOWLEDGE` (`KnowledgeService.Compose`:
  kind fact/policy/faq/event/notice, `StartsAt/EndsAt` self-expiry; STANDING
  knowledge first — live notices, events and short typed facts ≤800 chars — then
  faq/policy items and chunks of long items / uploaded documents RETRIEVED for
  the customer's latest messages by `Ai/Learning/RetrievalService` (BM25 with EN +
  BM stopwords and light stemming, Chinese/Japanese as overlapping character
  bigrams — `TextAnalysis.Words`; plus embeddings when a provider is configured —
  0.7·cosine + 0.3·BM25, a provider failure falls back to keywords; keyword-only
  is the zero-config path); zero hits add nothing; 1800-char budget; with no
  query (voice dispatch) the old top-4 faq/policy by priority) + `LEARNED FROM
  PAST CONVERSATIONS` (active lessons — rules first, at most 5, then the top 3
  relevant examples/corrections, 1200 chars, the conversation's own entity +
  its ancestors + org-wide only; chat replies only, see AI learning) + `CONTACT` (name, company,
  language, tag NAMES, profile summary — not custom fields, tag attributes or
  records) + `CONTACT MEMORY` (durable contact fields + the last 3 summaries,
  1500 chars) + the current org-local date/time. It is a **PURE GENERATOR
  returning `AiReplyResult`** (text, draft, tools, checks, retries, tokens,
  `Grounding` = what the reply was based on, `PendingActions` = calendar
  reservations) — callers send. Every path is exception-safe. LLM:
  `OpenAiLlmClient` speaks OpenAI-compatible `/chat/completions` to every
  provider in ONE list, `Ai/LlmProviderCatalog.cs` (id, label, group global |
  asia | self, key setting + env, base URL + optional override setting / region
  `Endpoints`, own default-model setting `<id>_chat_model`, suggested models,
  vision, reasoning-low, max_tokens, where to get a key, OpenRouter price
  vendor): openrouter, openai, **anthropic** (`api.anthropic.com/v1`, Claude,
  `max_tokens` always sent), **ilmu** (`ILMU_BASE_URL`, `ilmu-v3.1`), **google**
  (Gemini's `…/v1beta/openai`), mistral, xai, groq, deepseek, **qwen**
  (DashScope, international or China `qwen_base_url`), moonshot (Kimi), zhipu
  (GLM) and local (any OpenAI-compatible URL, key optional) — a new vendor is a
  row there; the client, the settings page, the playground / reviewer pickers,
  `AiAvailable` and the vision list all read it. Keys from Setting rows
  (`<id>_api_key`, `chat_model` — env `WHATSAPP_CHAT_MODEL`) with process-env
  fallback. A pin (`chat_provider_pref`, mirrored to `chat_provider`; nicknames
  like claude / kimi / gemini normalized) is used ALONE (failure → sandbox,
  never another vendor); else `chat_provider_order` is tried in order, an error
  falling through to the next (`chat_model` applies to the first only). Edited
  at Settings → AI keys & models (`/api/settings/ai-providers`,
  `settings.ai_keys.manage` — the payload's `providers` list feeds a tile per
  provider (search, Connected filter, Global · Asia · Self-hosted) whose panel
  saves that provider alone and `POST /api/settings/ai-providers/test
  {provider, api_key?, base_url?, model?}` tries it, a typed key never echoed;
  "Who answers" = the pin / order / model override; Advanced = the reviewer
  model, ElevenLabs, `embedding_provider` / `embedding_model`
  (`EmbeddingsSection.tsx`) and the tunables). A vendor-direct model id is
  priced from OpenRouter's list by its name after the vendor prefix
  (`LlmPricing.TryPrice`: `claude-haiku-5-5` → `anthropic/claude-haiku-5.5`,
  ambiguous = unknown). **A refused parameter never breaks a reply:** Anthropic's entry sends no `temperature` (`SendsTemperature = false` — current Claude models answer 400 "`temperature` is deprecated"), and for ANY provider a 400 / 422 that names an optional knob (`temperature`, `top_p`, `reasoning_effort`, `tool_choice`, `usage`, `max_tokens` → retried as `max_completion_tokens`) is retried without it, the refusal remembered per provider + model (`OpenAiLlmClient.RefusedParam`). **A failing provider is never silent:** when every provider of a call fails the reply still degrades to the sandbox answer, but `Ai/LlmHealth` records it (Setting `llm_last_error` {at, error, count}, throttled) → a red banner on Settings → AI keys & models (`last_error` on the payload) + ONE collapsing admin notification (RefKey `llm_failure`); the next success clears it. Tests: `LlmProviderCatalogTests`;
  nothing configured → `SandboxLlmClient`. Usage is
  metered into `LlmUsage` at every call site; costs are estimated at READ time
  from Setting `llm_prices` (`AnalyticsService`). Case summaries
  (`SummaryService`, `/api/messaging/threads/{channel}/{key}/summary`) are
  on-demand, persisted as `ConversationSummary` (a conversation with no contact
  keeps its brief in-process, keyed by thread — the table needs a contact; private
  notes are never counted or summarised; the chat panel auto-generates once per
  message count), heuristic without an LLM.
  Knowledge API: `/api/aicore/knowledge` (+ `/preview` `{query}` = the exact
  knowledge + learned blocks the AI receives plus every scored hit — the
  "Test retrieval" view, `RetrievalTester.tsx`; `/upload` = a PDF / .txt / .md /
  .csv ≤10 MB → one item with `source_file`, text layer via PdfPig, image-only
  PDFs refused, `KnowledgeUpload.tsx`), page `/knowledge`.
- **AI skills (`/skills`).** `AgentSkill.Kind` = **prompt** (`use_when` +
  `instructions` in the SKILLS block) | **tool** (JS `run(args, ctx)` the model
  CALLS) | **check** (JS `check(reply, ctx)` run on EVERY draft) | **app** (a
  "Connected app" — a READY-MADE capability switched on without code:
  `AgentSkill.Config`, snake_case JSON, `app` = `calendar` (see Calendar
  booking; form `components/skills/AppSkillForm.tsx`) | `documents` | `qr` |
  `payments` | `sheets` (see Documents; form `components/skills/ToolAppForm.tsx`);
  ONE dispatcher `Ai/Skills/AppSkills.cs` validates / normalizes every app's
  config, a config without `app` is a calendar). JS runs in
  `Ai/Skills/SkillRuntime.cs` (Jint: fresh engine per run, no CLR interop, no
  eval, 250 ms / 8 MB / statement + recursion caps; faults come back as data) —
  it can NEVER make a file or call the network, so a run that reaches for
  `fetch` / `require` / a PDF library gets "X is not defined — Code skills run
  in a sandbox with no internet, no files…" pointing at the ready-made apps
  (`SkillRuntime.Explain`), and the wizard's `/skills/draft` answers status
  `use_app` + `suggest_app` (documents | qr | payments | sheets) instead of
  drafting a JS tool for "create a PDF / invoice / QR / payment link / sheet
  row" (`SkillDrafter.AppIntent`; a prompt or check hint is never steered).
  The Skills page's **Ready-made skills** gallery
  (`components/skills/ReadySkillsGallery.tsx`, words in the `readySkills`
  namespace) adds those apps and 4 instruction templates (quote → invoice,
  collect order details, polite follow-up, payment reminder) by opening the
  wizard prefilled (`WizardInit.app` / `.template`).
  Built-in C# tools are always offered (`BuiltinSkills.cs`: current_time,
  current_week, date_add, days_between, convert_timezone, **calculate**
  (`Ai/Skills/Calculator.cs`: exact `decimal` arithmetic, a recursive-descent
  parser — never eval — `+ - * / ^`, parentheses, `%` with the calculator
  convention "200 + 10%" = 220, round/floor/ceil/abs/min/max, "RM 1,250.50 x 3"
  accepted, half-away-from-zero rounding to `decimals`); zone = Setting
  `org_timezone` (Settings → Time zone) → TZ → UTC — plus `request_handover(reason)`: the model's
  structured hand-over signal, `AiReplyResult.HandoverAsked` → meta
  `handover_asked` → the gate marks an `asked_for_human` handover while the
  reply still sends; the wording match `Humanizer.HandsOver` (EN/BM/ZH,
  commitments only — an offer sentence or a question doesn't count) stays as
  the fallback). A failing check → rewrite with the reason (max 2),
  then `OnFail`: retry→handover, handover (immediate) or block; handover = a
  tracked `check_failed` handover (see Human handover). **`GET/PUT
  /api/aicore/skills` is the ONLY writer of `AgentSkill`** (PUT
  `settings.ai.manage`, prunes within its own scope, refuses an ENABLED code
  skill whose code/tests changed unless its tests pass, and refuses an invalid
  app config — `AppSkills.Normalize` → `Bookings/CalendarAppConfig.cs`,
  `Documents/DocumentAppConfig.cs`, `QrAppConfig` / `PaymentAppConfig` /
  `SheetsAppConfig`; an app row sent without `config` keeps the stored one);
  the flow library PUT ignores the `agentSkills` it
  receives. Authoring: `POST /api/aicore/skills/draft` (describe → up to 4
  clarifying questions → draft; sandbox = a deterministic template library),
  `/skills/test` (`{kind:"app", config, app_action:"availability"}` is a DRY RUN
  that books nothing; a documents config renders a SAMPLE PDF into a
  throw-away preview — no number used —, qr a sample code, payments / sheets
  report whether their connection is there), `GET /skills/builtins`,
  `GET /skills/apps` (the connected-app catalog — calendar first, then
  documents (+ `cjk_font`), qr, payments, sheets with `connected` — sources
  with the Google Calendar connections the CALLER may see, actions, defaults),
  `POST /skills/documents/logo` + `GET /skills/documents/logo/{name}` (the
  letterhead logo, PNG/JPEG ≤ 1 MB by magic bytes, `settings.ai.manage`);
  the wizard is `components/skills/SkillWizard.tsx`.
- **Calendar booking** (app skills, `ChatBranch.Data/Bookings/`). An enabled
  calendar skill gives the model `calendar_availability` / `_book` / `_cancel` /
  `_my_bookings` (enabled actions only; a second skill becomes `calendar_2_…`),
  run by `BookingService` through `SkillService.ExecuteToolAsync` with the
  conversation in the `SkillContext`. Source `google` (calendar `assignee` = the
  assigned agent's own grant — connected at Settings → My connections — → the
  entity's → the org's; `shared` = entity → org; or one integration id) or
  `chatbranch` (zero credentials); a google skill with no connection says the
  calendar is unavailable — it never falls back. Availability
  (`BookingAvailability`) = weekday windows (org timezone unless the skill sets
  one) − Google free/busy − ChatBranch's own pending/booked/cancelling rows on
  the same `calendar_key`, with buffer, minimum notice, max days ahead and a
  step grid. `calendar_availability` over several days answers a SPREAD of ≤8
  slots (`complete: false` when that is a sample); a single day (from = to) lists
  EVERY free time (≤48, slots shortened rather than cut to fit the 4000-char result);
  `time` (HH:mm + one `from_date`) checks one exact start — `requested.available`, else
  a reason (outside_hours | too_soon | busy | off_grid | beyond_last_day, the pure
  `BookingAvailability.CheckTime`) + the 3 nearest free times (that day, else the next
  7 — the same `AlternativesAsync` the book tool's refusal uses); its notes tell the
  model a time missing from a sample may still be free and that earlier results were
  real. **Booking length:** `duration_minutes` is the DEFAULT length; with
  `duration_unit_minutes` (15 | 30 | 60, default = the length) and `max_units`
  (1–16, default 1) a customer books N × unit as ONE appointment
  (`CalendarAppConfig.Lengths`; the default must be a whole number of units ≤ the
  longest; `max_units` 1 = one fixed length, a stale unit ignored — an older config
  behaves exactly as before). `_availability` / `_book` then take an optional
  `duration_minutes` (any other length → `{ok:false, error, advice,
  allowed_lengths}`), `FreeSlots` / `CheckTime` measure the requested length (free only
  when the WHOLE span is, buffer included), the tools and the SKILLS how-to tell the
  model to ask how long and restate a time RANGE ("Mon 12 Oct, 2:00–3:00 PM"), and the
  booking is one `crm_booking` row + one Google event start → start + length — make,
  cancel and `_my_bookings` (`end`, `duration_minutes`, `range`) read the stored
  span; `max_active_per_contact` counts bookings, not units. Form: "Length unit" +
  "Longest booking" under Advanced (`AppSkillForm.tsx` `LengthFields`). A slot is RESERVED while
  the reply is written (`crm_booking` pending / cancelling) and MADE at delivery:
  `AiReplyResult.PendingActions` → the draft's `pending_actions`; `AiGate`
  delivery runs `BookingService.ConfirmAsync` BEFORE the text goes out (books
  then cancels, re-checks the slot; Google through
  `Integrations/GoogleCalendarClient`, attendee + `sendUpdates=all` only with a
  contact email); a failure BLOCKS the reply (draft failed, `booking_failed`
  handover, note, notify). Reject / expiry / supersede / shadow / a failed send
  release; `AiOpsWorker` releases orphans after 30 min; an operator preview, an
  empty reply and a dropped flow message release at once. Limits: one book + one
  cancel per reply, `max_active_per_contact`, the conversation's own contact only.
  **Rescheduling = cancel + book in ONE reply, all-or-nothing.** The customer's own
  booking never blocks its own reschedule: the book tool ignores the booking the
  reply is cancelling (`FreeSlotsAsync(ignoreBookingId)` drops its hold AND its own
  Google event — the free/busy span with exactly its start and end; a span merged
  with a neighbour stays busy, so that refuses rather than double-books), the race
  guard and the delivery re-check (`MakeAsync` `replacing`) do the same, and the
  per-customer limit doesn't count it; `_availability` takes `ignore_booking_id`
  (one of THIS customer's upcoming bookings, else ok:false; the reply's pending
  cancel is ignored by default). A book that reserves nothing gives the reply's
  cancel back (cancelling → booked, `old_booking_kept`, "The old booking (X) was
  kept because the new time could not be booked."); a cancel after a failed book is
  refused; a cancel after a "cancel first" refusal (the limit, or their own booking
  in the way) says `requires_new_booking`, and `SettleRescheduleAsync` (after each
  tool round in `AssistantService`) gives it back when the reply never booked — the
  booking-claim check then catches "has been cancelled". At delivery `ConfirmAsync`
  makes books first; a failed book releases the cancel. Tests: the reschedule block
  of `BookingTests`.
  **Meeting links** (`CalendarAppConfig.Meeting` = "" | `zoom` | `google_meet` |
  `link` + `meeting_link` (https, the same link every booking) + optional
  `meeting_message` ({{link}} {{time}} {{name}}); `MeetingKinds`). The meeting is
  MADE with the booking at delivery (`MakeAsync` → `MakeMeetingAsync`): Zoom = ONE
  meeting per booking through `Integrations/ZoomClient.cs` (a Server-to-Server OAuth
  app — integration provider `zoom`, `account_id` + `client_id` config,
  `client_secret` credential, token cached in-process; resolved like Stripe: the
  booking's entity → parents → org; created BEFORE the Google event so the invite's
  `location` carries it, deleted again if the event fails); Google Meet =
  `conferenceData.createRequest` + `conferenceDataVersion=1` on the event
  (`EventResult.MeetLink`, Google source only); `link` = the skill's own. Stored on
  `crm_booking.meeting_provider|meeting_url|meeting_id|meeting_integration_id|meeting_sent_at`.
  A meeting that can't be made never stops the booking (the 📅 note says "No meeting
  link: … Send the customer a link yourself."). The LINK is sent by ChatBranch,
  never the model: `AiGate.DeliverExtras` sends `BookingService.MeetingLinksToSend`
  in its own AI message right after the reply's text (`MeetingMessage`: the skill's
  template, else EN / BM / ZH by the contact's language), stamps `meeting_sent_at`
  (once; a failed send is noted); the book tool's result and the SKILLS block tell
  the model the link follows and never to write one, and `_my_bookings` (offered
  whenever a meeting is set, even without `cancel`) returns `meeting_link` for a
  customer who asks again. Cancelling deletes the Zoom meeting (best effort, noted
  on failure). Flow variable `{{booking_meeting_link}}`; the Bookings page / card /
  contact card show "Join Zoom" (`components/bookings/MeetingLink.tsx`); the skill
  form's "Video meeting" section (`AppSkillForm.tsx MeetingSection`, `/skills/apps`
  calendar `meetings[]` with Zoom's `connected`); the Integrations page's Zoom card
  (setup help, a real Test = token + `GET /users/me`). Tests: `MeetingLinkTests`
  (fake Zoom + fake Google), `ZoomIntegrationApiTests`.
  Every outcome writes audit `booking.created|cancelled|failed` (+
  `booking.unconfirmed` when the confirmation text is refused after booking), a
  "📅" private note and a notification (assignee, else admins; RefKey
  `booking:<id>`). The SKILLS block tells the model to offer 2–3 times, wait,
  book, then restate weekday/date/time/timezone; the voice SKILLS block omits app
  skills. Pages: `/bookings` (Work; `GET /api/bookings`, `chats.view`, scoped
  like conversations; `POST /api/bookings/{id}/cancel`, `chats.send`), the
  `BookingsCard` on the contact and the chat info panel. **The Bookings page opens on
  its Calendar** (`components/bookings/BookingsCalendar.tsx`; `?view=month|week|day`,
  remembered in localStorage `bookings-view`; `?tab=upcoming|past` = the lists, and a
  `?contact=` link opens on Upcoming): ChatBranch's own bookings on a Month / Week /
  Day grid in the zone times are shown in (Settings → Time zone) from `GET
  /api/bookings?from=&to=&limit=500` (a day either side, placed by `useFormat`'s zone) —
  booked solid, reserved / cancelling dashed, cancelled behind "Show cancelled"; a click
  opens the booking's card (Open the conversation, Cancel booking → the page's
  `CancelBookingModal`); week / day = a time grid (8 AM–8 PM stretched to what is
  booked, overlaps side by side, a now line); phones get dots + the tapped day's list.
  **Google layer**: pick a connected Google Calendar and its OTHER events are laid over
  in grey, read-only (all-day ones in their own row; the events ChatBranch made are
  skipped — they are bookings already) — every event on it (made in Google or by
  ChatBranch), all-day and "free" ones marked, through `GET /api/bookings/calendars` +
  `GET /api/bookings/calendar-events?connection=&calendar=&from=&to=` (`chats.view`,
  ≤62 days; `GoogleCalendarClient.ListEventsAsync` = `events.list`, recurring events
  expanded, only id / title / times / location / link / transparency — never a
  description or attendees — and `ListCalendarsAsync` = the account's readable
  calendars for the picker; a calendar id outside that list is a 404). Which
  calendars (`BookingsEndpoints.VisibleCalendarsAsync`): the caller's OWN personal
  grant + — with `integrations.view` / `integrations.manage` — the shared ones the
  Integrations page shows them; another person's personal calendar never, admins
  included. A ChatBranch booking among the events is linked only when in the caller's
  conversation scope (else just "booked in ChatBranch"); Google failures answer
  `{ok:false, error: not_connected | google}`, no calendar `no_calendar`. MCP
  `bookings_list_calendars` / `bookings_list_calendar_events`. Tests:
  `BookingCalendarViewTests`. The older "Booking
  hours" panel (`BookingHoursSettings.tsx` on the Google Calendar card at
  `/integrations`, `/api/aicore/booking-hours`, entity-scoped Setting
  `booking_hours`) is stored and edited but READ BY NOTHING in the C# port —
  booking skills carry their own hours and the flow "Find free slot" action
  searches free/busy without working hours. Tests: `BookingTests.cs`.
- **Documents, QR codes, payment links, Sheets** (app skills `documents` / `qr` /
  `payments` / `sheets`; `ChatBranch.Data/Documents/`,
  `Ai/Skills/ConnectedAppTools.cs`). The model of a calendar booking — MADE while
  the reply is written, DELIVERED with it. `documents` gives the model
  `document_create_invoice|quotation|receipt|letter` (enabled actions only; a
  second skill → `document_2_…`) and `qr` `qr_create(text, caption?)`, run by
  `DocumentService` through `SkillService.ExecuteToolAsync` with the conversation
  in the `SkillContext` (`ctx.Docs`). **The server does the maths**
  (`DocumentMath`: items `{description, qty, unit_price, discount: 5 | "10%"}`,
  each line rounded half away from zero, tax once on the subtotal) and hands out
  the running number (series `<skill entity | org>:<skill id>:<type>`, prefix +
  zero-padded, `start_numbers` the floor, a partial unique index on
  `(series, seq)`); the tool answers with the number and totals at once so the
  reply quotes them. The PDF (A4 or Letter, MigraDoc/PDFsharp 6 — MIT — with
  Liberation Sans + Serif embedded from `Documents/Fonts/`, SIL OFL; letterhead,
  logo, accent = a tag palette key, EN or BM labels by the contact's language,
  laid out by the skill's TEMPLATE — see below) is stored as
  conversation media (`MediaStore`) and recorded in `crm_document` as `pending`;
  its id rides on the reply (`AiReplyResult.Attachments` → the draft's
  `attachments: [{type:"document", document_id}]`). **Delivery**
  (`AiGate.DeliverExtras`): right after the reply's text — the single inline
  bubble, or the LAST queued bubble (`QueueService`) — through the channel's real
  media send (WhatsApp Cloud/Baileys, Messenger/Instagram, WeChat, Zalo
  `SendMedia`; email / Telegram as an attachment); LINE, Viber, SMS, TikTok,
  Douyin and the website widget (no outbound files) get a link to the anonymous
  `GET /api/documents/public/{token}` (128-bit token, sent/issued only, 90 days;
  base = Setting `public_base_url` → `PUBLIC_BASE_URL` → `FRONTEND_URL`), which is
  also the fallback when a file send is refused — never silent. Each sent
  document writes audit `document.sent|failed` and a private note "📄 Invoice
  INV-0012 sent (MYR 117.72)". A held / shadow / rejected / expired / blocked /
  failed reply VOIDS its documents (`ReleaseAll`; the number stays used — no
  silent gaps); a re-engagement template voids them too. The playground
  (`ctx.Docs.Preview`) renders the real PDF into `data/documents/preview/`
  (`GET /api/documents/preview/{id}`, a day) — no row, no number, no media.
  Chinese/Japanese/Korean: only with a TrueType CJK font (env
  `DOCUMENT_CJK_FONT`, else a few system paths); without one the tool refuses
  CJK text with "write it in English or Malay" (`DocumentFonts.CjkAvailable`).
  `payments` → `payment_create_link(amount, currency?, description)` runs the
  flow action "Create Stripe payment link" at once (shared connection, entity →
  parents → org; `max_amount` guard; no connection = `{ok:false, skipped}`; the
  playground never creates one). `sheets` → `sheets_append_row({values})` is
  DEFERRED: recorded as the draft's `deferred_actions` and appended ("Append row
  to Sheet", `rowJson` = [time, name, phone, email, …columns]) only when the reply
  is delivered (`deferred_done`, a 📊 note). API: `GET /api/documents`
  (`chats.view`, scoped like conversations), `GET /api/documents/{id}/file`,
  `POST /api/documents` (`chats.send` + the conversation in scope: an operator
  makes one with the conversation's Documents skill, `send: true` sends it now);
  MCP `documents_list` / `documents_create`. UI: drafts carry `documents[]`
  (`components/ai-monitor/DraftDocuments.tsx` — review queue, activity, the
  chat's pending-draft banner, "Open" fetches the auth-gated file); the
  playground inspector lists `documents_previewed`. Tests: `DocumentTests`.
  **The template designer.** `config.template` (`Documents/DocumentTemplate.cs`,
  snake_case, `version` 1) = preset classic | modern | minimal | bold (fills
  every key not given) + page (A4 | Letter, margins mm), fonts (sans | serif,
  size), hex colours (`accent` "" = the config's palette accent), header
  (layout split | stacked | centered, plain | band, logo position / size, which
  business lines, `extra_lines`, tagline, rule), title, info arrangement
  (meta_in_header | side_by_side | stacked), table (columns ⊇ description +
  amount, labels, header style / colour, zebra, border), totals (position,
  style), notes (`terms`, `bank_details`), footer, `sections` (the body order),
  `letterhead {file, pages: all|first}` and `overrides {invoice|quotation|receipt|letter:
  partial}` (fonts … sections only). ONE strict normalizer, `DocumentTemplate.From`
  (unknown keys refused, the first problem returned — the skills PUT 400s on it);
  `ToDictionary` is canonical. **A config WITHOUT a template renders exactly as
  before** (= the classic preset; `DocumentTemplateTests` compares every word's
  position with golden fixtures captured from the old renderer). The renderer
  (`DocumentRenderer`) reads `cfg.TemplateFor(type)`; an uploaded letterhead
  (`data/documents/letterhead/{32hex}.pdf`, the FIRST page of the upload,
  `DocumentService.SaveLetterhead`) is drawn UNDER the content with `XPdfForm`
  (`XGraphicsPdfPageOptions.Prepend`). API (all `settings.ai.manage`; MCP
  exclusions except assist): `POST/GET /api/aicore/skills/documents/letterhead[/{name}]`;
  `POST …/documents/template/analyze` (multipart: a PDF ≤ 10 MB or PNG / JPEG ≤
  5 MB by magic bytes, optional `template` / `config` JSON, `keep_letterhead`) →
  `DocumentTemplateAi.AnalyzeAsync`: the chat model with VISION (OpenAI
  `image_url` data-URL parts — `LlmMessage.Images`; a PDF's first page via
  `pdftoppm` when the server has it, env `DOCUMENT_PDF_RASTERIZER` = a path or
  `off`, else its embedded images) + the PdfPig text layer (lines with band /
  side / size / colour), strict JSON through the same normalizer, one retry with
  the refusal; no model / a model that fails (non-vision) / never valid → a
  deterministic heuristic over the text layer (`mode: heuristic`); returns
  `{mode, template, business, summary, note, letterhead, preview{url}}`;
  `POST …/template/assist {template, instruction}` (MCP
  `skills_assist_document_template`) → the edited template + a one-line summary,
  sandbox = a small rule set (presets, colour words / hex, logo / title sides,
  zebra, borders, fonts, page size, page numbers, "bank details in the footer").
  Both quota-guarded (`QuotaGuard`, 429) and metered as `LlmUsage` feature
  `document_template`; nothing is saved until the skill is. `/skills/apps`'
  documents entry carries `template_presets`. Previews of an edited template go
  through `/skills/test`'s documents dry run. UI: "Look → Design template" on the
  Documents form opens `components/skills/DocumentDesigner.tsx` (full screen,
  panels left — simple first, the rest under Advanced, section order up/down;
  "Editing" all documents or one type = an override — live preview right, an
  iframe of the auth-fetched blob + "Open preview" for iOS; top: preset
  thumbnails, "Upload my design", "Ask AI", Undo); client model
  `components/skills/documentTemplate.ts`, words `readySkills.designer`. Tests:
  `DocumentTemplateTests`, `DocumentTemplateApiTests`.
  **Custom layout (HTML templates)** — for a document that must match an existing
  design exactly. `config.layout` = `designer` (default; renders exactly as
  before) | `html` + `config.html` (`Documents/DocumentHtmlConfig.cs`, one strict
  normalizer: `template` ≤200 KB, per-type `templates`, ≤20 `fields` {key, label,
  type text|multiline|date|number, required, default — never a built-in name},
  `page {size, margin_mm}`, `letterhead`, `terms`, `bank_details`; saving
  sanitizes + test-compiles every type). Handlebars.Net
  (`DocumentHtmlTemplating.cs`): every value HTML-escaped, `{{{ }}}` / `{{&` /
  partials / decorators refused (the logo data-URL is the only raw value), helpers
  money · number · date · words (EN/BM amount in words, with sen) · upper · lower
  · inc · eq/ne/gt · or · nl2br · first_line/rest_lines · label · default, errors
  carry a line number; the data model is ONE place, `DocumentHtmlModel.Build`
  (business, document, customer, items, totals + words, fields, terms, page).
  `HtmlSanitizer` runs on save AND before every render (no script / iframe /
  object, no `on*`, no non-data URLs, no `@import` / `url()`). Printing = headless
  Chromium (`HtmlPdfConverter.cs`, `IHtmlPdfConverter`; `DOCUMENT_HTML_CHROMIUM`
  path | `off`, else the PATH — Google Chrome before a generic `chromium`: GitHub's
  runner image ships an unofficial Chromium snapshot that never finishes a headless
  run; ≤2 at once, 30 s then the process tree is killed, temp
  profile removed): JavaScript is off by an injected CSP (`script-src 'none'` —
  `--blink-settings=scriptEnabled=false` makes `--print-to-pdf` print nothing) and
  the network by `--host-resolver-rules=MAP * ~NOTFOUND` + `--no-proxy-server`,
  both proven in `DocumentHtmlApiTests`; a letterhead is drawn under the Chromium
  PDF with the same `XPdfForm` underlay. No renderer ⇒ html documents are REFUSED
  ("custom layout is unavailable on this server", 503 on `POST /api/documents`)
  and `/skills/apps` says `html_renderer: false` — never a silent designer
  fallback. Starters (`DocumentHtmlStarters.cs` + `HtmlStarters/*.html`):
  Malaysian classic (calibrated against a real CL-series invoice: Ref No / Date,
  Attn + Tel / Fax, PRO / REF, grid table, "1)" numbering, amount in words, NOTE
  list, signature block), Clean modern, Letter. API (all `settings.ai.manage`):
  `POST /api/aicore/skills/documents/html/preview` (instant HTML, `{ok:false,
  error, line}` on a bad template; MCP `skills_preview_document_layout`),
  `…/html/draft` (multipart upload → `DocumentHtmlAi.DraftAsync`: vision + PdfPig
  text, validated, one retry; no model / never valid → the closest starter filled
  from the PDF, `mode:"starter"`; MCP exclusion), `…/html/assist` (MCP
  `skills_assist_document_layout`; sandbox leaves it unchanged with a note);
  quota-guarded, metered `document_template`. `/skills/apps` adds
  `html_renderer`, `html_starters`, `html_reference`. Custom field values ride in
  `POST /api/documents` `fields.fields` (and the AI tools' `fields`; the SKILLS
  block lists a layout's fields); `GET /api/documents/form?channel=&key=`
  (`chats.send` + thread scope; MCP `documents_get_form`) feeds the chat's "New
  document" form (`components/chats/DocumentsCard.tsx` in the info panel). UI:
  the designer's Designer | Custom layout (HTML) switch →
  `components/skills/HtmlLayoutEditor.tsx` (+ `htmlLayout.ts`, `CodeEditor
  highlight="html"`; Start from · Upload my design · Ask AI · Undo; tabs Fields ·
  Words & page · Reference with insert-at-cursor; instant sandboxed `srcdoc`
  preview + the real PDF preview), words `docLayout`. The API image installs
  chromium, fonts-liberation, fonts-noto-cjk and poppler-utils (so the AI also
  SEES an uploaded design — `pdftoppm`). Tests: `DocumentHtmlTests`,
  `DocumentHtmlApiTests` (real Chromium when present — CI's runners ship Chrome).
- **AI monitor + human-like behaviour** (`/ai-monitor`, `/api/ai/monitor*`,
  `/api/ai/behaviour`). EVERY AI-written outbound (instant reply, queued
  ai_reply, follow-up, flow job with payload `source:"ai"`) goes through
  `Ai/AiGate.Submit`, which records an `AiDraft` row EVEN when it sends
  (`auto_sent`) — the monitor is the full trail. Policy `ai_review_mode` = auto
  | review_flagged | review_all | shadow (+ autosend minutes, first-N, risky
  keywords, notify) resolved inbox `Config` → entity Setting → org Setting →
  default (`Ai/AiBehaviourSettings.cs`, whose `Fields` the settings UI renders
  verbatim; its `learning` group holds `ai_learning_mode`). Approve/edit re-runs
  the send-time guards and routing (opt-out, coverage, 24h window; quiet hours
  wait) and records implicit feedback (AI learning). Drafts carry `bookings[]`
  (`{type, booking_id, label, status, calendar_label, timezone, start}` — what
  the reply's calendar tools reserved; delivering MAKES them first and is
  blocked if that fails; reject / expiry / supersede / shadow release them, so a
  held reply never books — `DraftBookings` in `components/ai-monitor/DraftBits.tsx`),
  `my_feedback` (the caller's own 👍/👎) and `grounding` (what the reply was
  based on — `DraftFeedback` / `GroundingLine` in
  `components/ai-learning/DraftLearning.tsx`). Tabs (`?tab=`): Waiting for review
  · AI activity · Learning (`LearningPanel.tsx`) · Stats (+ `LearningStats.tsx`,
  `HandoverStats.tsx`) · Settings, under the master-switch card. The chat shows a held reply in
  `PendingDraftBanner.tsx`. Generation sits behind ONE seam, `Ai/AiComposer` —
  adapt it there when `AssistantService`'s return type changes.
  `Ai/AiResponder` = ONE debounced ai_reply job per thread (wait
  `ai_wait_seconds`, capped by `ai_wait_max_seconds`) + acknowledgement skip;
  `AiGate.StartHumanSend` = 1–3 bubbles chained through the queue with typing
  pauses (`ITypingIndicator`; never sleeps on a request);
  `Ai/AiFollowUpService` = follow-ups when the customer goes quiet (only after
  an open question; stops on reply/opt-out/resolve/human takeover; closed 24h
  window → `ai_followup_template` or a skip note). `Workers/AiOpsWorker` (every
  30 s; `AI_FOLLOWUP_WORKER=0` disables) runs due auto-sends + follow-ups and the
  booking housekeeping (`BookingService.Housekeep`); `POST /api/ai/monitor/tick`
  is the manual seam (its answer includes `bookings_released`). Caps
  `ai.monitor` (agents hold it) / `ai.behaviour.manage`. `AI_HUMANIZE=0` turns
  the human-like DEFAULTS off (explicit Setting rows still win) — the test host
  sets it.
  **Human-like touches** (`Ai/HumanTouch.cs`, settings group `human`, resolved
  inbox → entity → org → default like the rest): `ai_emoji_style` off | light
  (default) | expressive = a `STYLE — emoji` line composed BELOW the hard rules,
  house rules and persona ("if the persona says otherwise, follow the persona";
  email always gets "don't use emoji"; follow-ups and flow messages get it too,
  voice nothing). `ai_reactions` (default on) offers chat replies
  `react_to_message(message_id, emoji)` where `HumanTouch.ReactionsPossible`
  (WhatsApp Cloud/YCloud/Baileys, Telegram — not Twilio): ONE reaction on a
  customer message of THIS turn (since our last outbound), the emoji validated by
  `MessageReactions.ForAi` (one emoji; Telegram's own set, spelled without
  U+FE0F; a few never-for-the-AI ones). `ai_quote_replies` auto (default) | off
  offers `quote_message(message_id)` — the reply's FIRST bubble quotes it
  (`reply_to` on the draft → `OutboundSender.Send(replyTo:)` direct or through the
  first queued bubble's payload; ignored where quotes aren't carried). **A reply
  in parts:** when the customer's messages since the last reply are about different
  things, the model starts each part on a new line with the `[msg <id>]` it answers
  (prompt "ANSWERING IN PARTS", offered only with quotes on and ≥ 2 waiting
  messages); `HumanTurn.SplitParts` cuts there (an id it was never shown quotes
  nothing, repeats join, ≤ 4 parts, one marked part = a plain quote) →
  `AiReplyResult.Parts` → the draft's `parts: [{text, reply_to}]`, its content the
  parts joined by a blank line; `AiGate.StartHumanSend` sends ONE bubble per part,
  each quoting its message (the first inline or queued, the rest through the queue —
  `reply_to` + `rest_quotes` on the job); a held reply keeps its parts, a person's
  edit (content no longer the parts — `AiReplyPart.From`) sends one message; a
  voice reply stays one note. The monitor lists each part's quote (`parts` on the
  draft payload), the playground answers `bubble_quotes`. Tests:
  `ReplyPartsTests`. No heuristic quoting: only when the model asks. The model sees ids as `[msg <id>] `
  in front of customer messages (only when a tool can use them; stripped from any
  reply). Results ride on `AiReplyResult.Reactions` / `ReplyToId` → the draft's
  `reactions: [{message_id, emoji}]` / `reply_to`; `AiGate.Deliver` reacts FIRST
  through `OutboundSender.React` + `MessageReactions.Set` (actor `ai`, "ours" like
  a person's — `MessageReactions.IsOurs`; audit `message.react`), then the text.
  A reaction can stand alone (`AiReplyResult.ReactionOnly`, an empty-content
  draft); the acknowledgement skip reacts instead of staying silent when
  reactions are on and `ai_ack_text` is blank (`Humanizer.AckReaction`: 🙏 thanks
  EN/BM/ZH, ❤️ emoji-only, 👍 otherwise). Held / shadow / rejected / expired
  drafts never react; approving reacts then. A refused touch never flags a reply.
  **Voice replies** (`Ai/Voice/`, same `human` group): `ai_voice_replies` off
  (default) | mirror (the customer's latest message was audio — `MediaType` audio or
  its "[audio]" placeholder) | always, + `ai_voice_id` (empty = Setting
  `default_voice` when `default_voice_provider` is elevenlabs, else ElevenLabs'
  stock voice). Decided at DELIVERY by `VoiceReplyService.Wants` (mode + channel
  `Supports`: WhatsApp Baileys / Cloud / YCloud / sandbox, Telegram, Messenger,
  Instagram + `Speakable` — markdown/emoji/ids stripped; a link, nothing speakable or
  over 1500 chars = text). `AiGate.Deliver` → `StartVoiceSend` (ONE message, never
  split; a typing pause / quiet hours queue ONE text job `voice: true`) →
  `SendVoiceOrText`: AI quota (`AiQuota.EntityBlock`) → ElevenLabs TTS
  (`SpeechClient`: Setting `elevenlabs_api_key` → env; `opus_48000_64` Ogg-checked for
  WhatsApp / Telegram, `mp3_44100_128` for Messenger / IG) → `MediaStore` →
  `OutboundSender.SendVoiceNote` (Baileys gateway `send-media` `ptt: true`, Cloud
  upload → `type: audio`, Telegram `sendVoice`, Meta audio file) — the row keeps the
  WORDS + `MediaType` audio; any failure sends the text instead (no failed row),
  never silence. Draft context `voice` = planned (set at submit — held/shadow drafts
  never synthesize) | sent | text (+ `voice_error`) → the monitor's "🎤 voice note"
  chip (`DraftTouches`); audit `ai.voice_reply`; `LlmUsage` `voice_reply`
  (characters as prompt tokens). The playground answers `voice_note`. **Customer
  voice notes → words:** `AiComposer` first runs `VoiceTranscriber.TranscribePendingAsync`
  (latest downloaded audio without a transcript, 20 s budget; `SpeechClient.SttProvider`:
  Setting `transcription_provider` (env `STT_PROVIDER`) "" automatic = OpenAI
  `gpt-4o-mini-transcribe` with a key, else ElevenLabs `scribe_v1`, else none | openai |
  elevenlabs | **local** = a self-hosted OpenAI-compatible Whisper at
  `transcription_base_url` (env `STT_BASE_URL`, default `http://whisper:8000/v1` = the compose
  `whisper` service, profile `stt`, internal network only, no published port), model
  `transcription_model` (env `STT_MODEL` / `OPENAI_STT_MODEL`, default
  `Systran/faster-whisper-small`), optional `transcription_api_key` — needs NO OpenAI key, so it
  never adds OpenAI to the chat providers | off; NOT the voice-call agent's `stt_provider`; on the
  AI keys page under Advanced) → `Message.Transcript`
  (`crm_message.transcript`), read as "[voice note] …" by `AssistantService.History`
  and `SummaryService`, `transcript` on message payloads, `LlmUsage` `transcription`.
  Tests: `VoiceReplyTests`.
  **Customer photos → the model:** a chat reply hands the customer's images of THIS turn
  (since our last outbound, newest 3, JPEG / PNG / WebP / GIF ≤ 4 MB, read from the media
  folder) to the model as `LlmMessage.Images` on their latest message when
  `ILlmClient.SupportsVision` (the catalog's `Vision` flag), plus a system line saying
  they are attached ("never say you cannot open images"); without vision, or a file
  missing / too large, the line says the model cannot see it and should ask the customer
  to describe it (`AssistantService.AttachCustomerImages`). Tests: `ChatImageTests`.
  The monitor shows `reactions` + `quote` on the draft (`DraftTouches` in
  `DraftBits.tsx`); the playground previews both (`reaction` / `quote` by
  transcript index, `options.emoji_style|reactions|quote_replies` overrides need
  `ai.behaviour.manage`). Tests: `HumanTouchTests`.
  **Channel formatting + honest booking claims** (in the reply loop of
  `AssistantService.GenerateAsync`, AI text only — a person's message is never
  rewritten). `Ai/ChannelFormatter.ForChannel` turns the model's Markdown into
  what the channel shows: WhatsApp gets its own markup (`**x**`/`__x__` → `*x*`,
  `~~x~~` → `~x~`, `# Heading` → `*Heading*`); SMS, Messenger, Instagram, LINE,
  Viber, WeChat, Zalo, TikTok, Douyin and Telegram (sent without `parse_mode`) get
  plain text; `[text](url)` → `text: url` on both; email and the widget are left
  as written. Only matched pairs around text change — never `2*3` or a masked IC
  `******-**-1234` — and it is idempotent; it runs BEFORE the checks. The built-in
  check `builtin:no_false_booking_claim` (`Ai/BookingClaims.cs`, only when a
  calendar app skill is in scope) fails a draft that says an appointment HAS
  BEEN cancelled / booked / confirmed when nothing backs it. A "made" claim is
  backed by this reply's `*_book` (`reserved`) OR by restating one of the
  conversation's ACTIVE bookings (`BookingService.KnownForClaims`: the contact's
  upcoming booked rows + those cancelled in 30 days, same company, this
  conversation's first — loaded before the loop): the claim's sentence (else the
  whole draft) names that booking's start time or date ("3:15" / "3.15" / "15:15",
  "12 Oct" / "Monday" / "today") and no other time or date — its end only beside
  its start; a price ("RM 12.50") or a dotted date is never a time. A "cancelled"
  claim is backed by this reply's `*_cancel` (`cancel_pending`) or when the most
  recent booking for the time it names is already cancelled. Pure
  (`BookingClaims.UnbackedClaims` → the failing sentences); the fix line lists the
  active bookings ("Existing booking: Mon 12 Oct, 3:15–3:45 PM. You may say it is
  booked for that time; don't say a NEW booking was made unless you call the book
  tool."). Retry, then — this check PINS its sentences (`CheckRun.Sentences` /
  `Topic`) — a **partial handover** (`Ai/PartialReply.cs`): when every failing
  check is pinned, those sentences are dropped and the rest is sent with "I'll get
  a teammate to check the appointment part for you." (EN/BM/ZH by the reply's
  language), `AiReplyResult.Partial` → the draft's `partial_handover` → a
  `check_failed` handover naming the topic and quoting what was left out, the AI
  staying ON; too little left (< 3 words) = the whole draft is held with
  `ai_keeps_answering` (still on). Only a check about the whole reply (unpinned,
  or OnFail handover) stands the AI down, as before. Chat replies (and the
  playground) carry `AssistantService.EveryTopicRule` — answer every topic raised
  since the last reply, in order, say so for an item it can't do and still answer
  the rest, the latest message wins on a conflict — and read every waiting
  message (`ReplyWindow`: 20 messages, up to 60 when more are waiting). Tests:
  `ChannelFormatterTests`, `AiSilenceTests`.
- **AI reviewer — "AI to monitor AI"** (`Ai/Review/AiReviewService.cs`,
  `Endpoints/AiReviewEndpoints.cs`, the AI monitor's "AI review" tab
  `?tab=ai-review`, `components/ai-monitor/AiReviewPanel.tsx`). ON DEMAND ONLY —
  nothing schedules it. `POST /api/ai/review {from?, to?, days?, entity?, limit?}`
  (`ai.behaviour.manage`; default last 7 days, 30 replies, max 100; one run at a
  time → 409) samples decided `AiDraft`s (auto_sent / sent / approved / edited /
  rejected — never pending/shadow), newest first and round-robin across
  conversations, scoped like the monitor's drafts (comma `entity` = union of
  subtrees, bounded by an agent's assignment). It runs in a background DI scope
  and the POST waits ≤20 s, so a long run answers `status: running` — poll
  `GET /api/ai/review/runs/{id}`. Model = Setting `ai_reviewer_model` (env
  `AI_REVIEWER_MODEL`; default `anthropic/claude-sonnet-5.5` on OpenRouter;
  `provider:model` for another vendor) via `OpenAiLlmClient.Pinned`; no key for
  that provider → the normal chat provider order with its model AND a note
  saying so. **Per-run reviewer:** the POST's optional `model` (same format)
  must name a CONFIGURED provider (else 400, `AiReviewService.CheckModel`) and
  is used for that run only — stored canonical as `crm_aireviewrun.requested_model`
  ("" = the default; `provider`/`model` stay what actually reviewed) and shown on
  the run. `GET /api/ai/review/options` (`ai.behaviour.manage`) feeds the panel's
  "Reviewer model" `Select`: providers (never keys; the list is
  `Endpoints/AiProviderChoices.cs`, shared with the playground's options) with
  `AiReviewService.SuggestedModels` as `provider:model` values, unconfigured
  ones disabled, "Custom…" free text, the default preselected + its `source`
  (setting | env | builtin), `can_set_default` = `settings.ai_keys.manage`,
  whose holders get "Make this the default" (`PUT /api/settings/ai-providers`
  `{ai_reviewer_model}`); others see the default as text. No model at all (or
  every batch failing) → deterministic checks
  (language mismatch EN/BM/ZH, numbers with no grounding on a factual question,
  unanswered questions, length, missed hand-over, edited/rejected drafts),
  labelled `mode: "heuristic"` per run and `source` per finding. 5 replies per
  call, temperature 0.1, strict JSON, customer text fenced, the entity's persona +
  house rules + review mode + knowledge TITLES as the setup to judge against;
  a summary call adds top problems + 3–5 setup suggestions. Every call metered
  as `LlmUsage` feature `ai_review` (entity = the run's). Tables
  `crm_aireviewrun` (last 50 kept) + `crm_aireviewfinding`. Reads
  `GET /api/ai/review/runs[/{id}]` (`ai.monitor` | `ai.behaviour.manage`): a run
  is visible only when every entity it read is in the caller's scope; findings
  filtered again by the conversation's entity. `POST
  /api/ai/review/findings/{id}/apply {as: lesson|knowledge|house_rule, text?}`
  → a rule lesson (ALWAYS suggested — a person approves it in the Learning tab),
  an INACTIVE knowledge draft, or a house-rule line at the CONVERSATION's entity
  level (`settings.ai.manage` for the last two); an edited `text` replaces the
  suggestion; the apply is an atomic claim (`… WHERE applied = 0`, parallel
  applies succeed once). One run at a time is a process-wide lock around the
  check + insert; a run heartbeats `updated_at` per batch and `ExpireStale` only
  fails a run whose heartbeat went quiet. Heuristic fix suggestions never quote
  the customer. An optional `level` (entity slug, "" = organization) overrides
  where it lands — the panel's confirm dialog always sends it (editable text,
  defaulting to the conversation's entity). Tests: `AiReviewTests`.
- **Background AI tasks — silent AI workers** (`/ai-tasks`, Automate;
  `Data/Ai/Tasks/AiTaskService.cs` + `AiTaskTools.cs`, `Endpoints/AiTaskEndpoints.cs`,
  `Workers/AiTaskWorker.cs`, `components/ai-tasks/AiTasks.tsx`, words in the
  `aiTasks` namespace). An AI that keeps READING customers' inbound messages and
  acts through tools but NEVER writes to the customer — there is no send tool by
  construction. `AiTask` (`crm_aitask`) is a multi-entity library row (kind
  `ai_task`, "Available to" like skills): channels (empty = all), trigger
  `inbound_message` + conditions (attachment "" | any | pdf | image, ANY keyword,
  linked contact yes/no), free-text `instructions`, an explicit tool ALLOWLIST
  (`AiTaskTools`: `add_private_note`, `add_conversation_label`, `set_contact_field`
  (defined custom fields only), `hand_over_to_person` (a tracked handover, kind
  `rule`, by `ai_task:<id>`, `standDown: false`), `sheets_append_row` (time + name +
  phone in front), `drive_upload_attachment` (the message's file —
  `IntegrationActions.UploadDriveBytesAsync`, never reachable from a flow step),
  `calendar_create_event` (no attendee), `webhook_post` (https, a public host, a
  ≥16-char secret or refused; `X-ChatBranch-Signature: t=…,v1=<HMAC-SHA256 of
  "t.body">`), `mcp:<server>__<tool>` = an external MCP server's tool exactly as
  the operator assistant offers it (`ExternalMcpTools`)) and per-tool `config`
  (the webhook secret leaves masked; a masked value sent back keeps it). The
  inbound path (`InboundProcessor` after the message is stored, and `/simulate`)
  only QUEUES a pending `AiTaskRun` (`crm_aitaskrun`, unique per task × message —
  idempotent) for enabled in-scope tasks on the channel matching the keywords,
  `AI_TASK_DELAY_SECONDS` (default 10) ahead; `AiTaskService.Enqueue` never
  throws. `RunDueAsync` (the worker, every 10 s, `AI_TASK_WORKER=0` off;
  `POST /api/ai/tasks/tick` is the manual seam) waits ≤90 s for a needed
  attachment (fetched off the ack), drops a run whose attachment / linked
  condition fails (no row), claims pending→running atomically, then SKIPS (said
  in `error`) when the task was switched off or left the scope, the AI master
  switch is `off` (schedule mode doesn't stop it), the entity's AI quota is
  reached, or no model is configured — it runs whether the chatbot is on or off
  for the chat, and on opted-out chats (it messages no one). Input: the message
  text fenced as data + the attachment (PDF / .txt / .csv text via PdfPig
  `DocumentExtractor`, ≤20 000 chars; an image ≤4 MB as an OpenAI-compatible
  `image_url` data URL when `ILlmClient.SupportsVision` — OpenRouter / OpenAI /
  Gemini — else noted "not readable without a vision model"; `LlmMessage.Images`),
  the `ToolLoop` with ≤6 tool calls at temperature 0.1, and a closing JSON
  `{relevant, extracted, summary}` (`ParseFinal`, lenient). Status ok | failed (a
  tool failed — its error kept) | skipped (not relevant and no calls: no note);
  each run stores the extracted JSON, every tool call + result (redacted —
  `AiTaskTools.Redact` — and capped), tokens, provider/model; a run that did
  something writes a "🤖 <task>: <summary>" private note and audit `ai_task.run`
  (source automation); usage metered as `LlmUsage` feature `ai_task` (entity =
  the conversation's; `user_name` only on a dry run). API: `GET /api/ai/tasks`
  (+ the tool catalog with `connected` per integration and the ready MCP
  servers' tools; `ai.monitor` | `settings.ai.manage`), `POST/PUT/DELETE`
  (`settings.ai.manage`), `GET …/{id}/runs` (scoped like conversations),
  `POST …/{id}/test {text, attachment?: {name, mime, base64}, task?}` = a DRY RUN
  (unsaved edits ride as `task`; tools that change something answer "preview",
  a read-only MCP tool runs; nothing stored but the usage row; the caller's user
  quota → 429). MCP `ai_tasks_*`. UI: list (trigger summary, Available to, tools,
  last runs, on/off) → editor (name, `AvailabilityPicker`, attachment,
  instructions; channels / keywords / sender under `Advanced`; the allowlist
  grouped In ChatBranch · Connected apps · Your own system · External MCP with
  "Not connected" shown; per-tool settings; the Test box) → Runs modal; templates
  "Save invoices" and "Log receipts / payment slips". Tests: `AiTaskTests`,
  `AiTaskApiTests`.
- **Human handover** (`Ai/Handover.cs`, the ONLY writer of `crm_thread.handover_*`).
  `Handover.Mark(db, thread, kind, reason, by)`: kinds `check_failed` ·
  `asked_for_human` · `send_blocked` · `booking_failed` · `low_confidence` ·
  `rule` · `manual` · `unknown_customer` · `quota` (the AI usage quota, `standDown: false`); `by` = `ai` · `rule:<id>` · `flow` ·
  `agent:<username>` (· `identity`). It counts once while open, sets the chat
  pending (a RESOLVED chat reopens as pending too), adds the private note, notifies the owners, audits `ai.handover` and
  (default `standDown`) turns the chatbot off — the identity exchange's
  `unknown_customer` handover leaves the chatbot as it was, and a person linking
  or creating the contact through the identity banner (`POST
  /api/contacts/identify/{channel}/{key}/contact` →
  `IdentityRequestService.ResolveByHand`) clears it — the plain `…/link` route
  does not. Sites: the AI gate (refused drafts, "a
  person will help you" replies, send-time blocks, booking failures), **an AI
  reply that came back EMPTY or threw** (`AiResponder.RespondAsync` → kind
  `low_confidence`, `standDown: false` — never silence; the chat reply's tool loop
  has 6 rounds and its tool-less last round carries `AssistantService.LastRoundNote`
  "Tools are no longer available. Reply … honestly", and an empty outcome's usage
  `reason` keeps the raw text + the last tool; tests in `AiSilenceTests`), the
  assistant's handover outcome, the flow Hand-over step, the automation
  `hand_over` action, the identity exchange and `POST
  /api/inbox/threads/{channel}/{key}/handover` `{take|continue|flag}`
  (`chats.send`). **continue** (and the bulk `ai_on`) also answers the message the
  customer is waiting on (`Ai/AiResume.QueueWaitingReply`): when the last
  customer-visible message is theirs, under 24 h old (`WaitingWindow`), the AI
  eligible and no AI draft held for review, ONE ai_reply job is queued (payload
  `resume_for` = that message; a job already queued / for that message = none),
  audit `ai.resume.reply`; the answer carries `ai_reply_queued` (bulk:
  `replies_queued`) and the banner says "The AI will answer the last message."
  Tests: `AiSilenceTests`, `AiResumeApiTests`. A person's reply (the human send endpoints — not API keys or
  MCP) or resolving sets `handover_resolved_at`. UI: the inbox `handover` filter
  + "Needs human" tab, an amber chip, `HandoverBanner.tsx` (Take it / Let AI
  continue); `/api/live` adds `needs_human_count`; `GET /api/ai/handovers/stats`
  (`ai.monitor` or `chats.view`) feeds the AI monitor's Stats tab.
- **AI learning** (`ChatBranch.Data/Ai/Learning/`, `Endpoints/LearningEndpoints.cs`).
  **Feedback** (`crm_aifeedback`): explicit 👍/👎 on an AI message or draft
  (`/api/ai/feedback`, `ai.monitor`; one per user per reply, re-rating replaces
  it; reasons `wrong_info|didnt_answer|tone|too_long|should_handover|other`,
  note, correction) and IMPLICIT from the AI monitor
  (`LearningService.OnDraftDecided`: approved +1 weight 0.5, edited −1 weight 0.5
  with the sent text as the correction, rejected −1 with the reason); every row
  snapshots the question and the answer. **Lessons** (`crm_ailesson`,
  `/api/ai/lessons`, writes and delete `ai.behaviour.manage`): `example` |
  `correction` | `rule`; entity-scoped Setting `ai_learning_mode` = review
  (default — every lesson waits for a person) | auto (an admin's feedback goes
  live at once) | off (counted, nothing learned); a near-identical suggestion
  MERGES (normalized text at once, cosine ≥0.95 in the indexer; the latest
  correction wins and sends a live lesson back to review unless an admin gave it
  in auto mode); `/{id}/to-knowledge` turns one into a FAQ (a rule → a policy).
  Lessons are DATA: `HardRules` win and they never cross to a sibling, a parent
  or an unassigned conversation (a branch DOES use its ancestors' lessons).
  **Retrieval** (`RetrievalService`):
  `embedding_provider` openai | google | openrouter | local, "" = the first configured
  (in that order; defaults text-embedding-3-small / gemini-embedding-001 /
  openai/text-embedding-3-small — billed as OpenRouter credits / nomic-embed-text), `none`
  = keywords only; `embedding_model` overrides the model (env `EMBEDDING_PROVIDER`
  / `EMBEDDING_MODEL`); vectors live in `crm_embedding`, written ONLY by
  `Workers/EmbeddingWorker` (`EmbeddingIndexer`: every 30 s or on a kick; a
  changed hash or model re-embeds; `EMBEDDING_WORKER=0` disables; last failure in
  Setting `embedding_last_error`), metered as `LlmUsage` feature `embedding`.
  `AiReplyResult.Grounding` → the draft's `grounding` → the monitor.
  `/api/ai/learning/status|reindex|stats` (the 👍 rate, 👎 reasons, knowledge
  gaps). UI: thumbs on AI bubbles in `/chats` (`BubbleFeedback.tsx`), the AI
  monitor's Learning tab, `/knowledge` upload + Test retrieval, Settings → AI keys
  & models → embeddings. Tests: `RetrievalTests`, `LearningTests`,
  `LearningApiTests` (`LearningFakes.FakeEmbeddingClient`).
- **AI playground** (`/ai-playground`, Automate after AI monitor;
  `Endpoints/AiPlaygroundEndpoints.cs`, `Data/Ai/AssistantService.Playground.cs`,
  `Data/Ai/AiPlayground.cs`). An operator chats with the AI as a customer would —
  a made-up profile (name, language, tag names, custom-field values: a transient
  `Contact`, never attached), a real contact (read only, `ScopeContacts`) or a
  copy of a real conversation (`POST /api/ai/playground/import {channel, key,
  take?}`, `chats.view` + the inbox scope). `POST /api/ai/playground/reply` is
  STATELESS (the browser sends the transcript every time; ≤60 turns, ≤4000
  chars, 30/min per user → 429) and runs the real prompt assembly and generate →
  check → retry engine through ONE seam, `AssistantService.GeneratePlaygroundAsync`:
  per-call toggles (knowledge, lessons, skills = every tool incl. built-ins +
  checks, contact memory), an unsaved persona that replaces the stored one
  (`HardRules` always lead), a provider pin (`OpenAiLlmClient.Pinned(provider,
  model)`, configured providers only, else 400). NO side effects: no thread /
  message / draft / note / notification / handover / status / audit row,
  calendar tools PREVIEW (`BookingService.Session.Preview`: checked for real,
  nothing written), lessons not counted as used, usage metered as `LlmUsage`
  feature `playground`. "What would happen in production" = `AiPlayground.Decide`
  over the gate's own pure rules (`AiGate.Review` + `AiGate.FlagReasons`,
  `Handover.KindForAiDraft`, `AiEligibility`): send | hold (shadow) | handover |
  blocked | ai_off. `GET /api/ai/playground/options?entity=` lists providers
  (never keys), the stored persona (`ai.behaviour.manage` only), channels, the
  policy, the learning mode and custom fields. Gates: `ai.monitor` or
  `ai.behaviour.manage`; overrides (provider/model, persona, parts off,
  show_prompt) `ai.behaviour.manage` → 403; the entity in the caller's assigned
  subtree (org-wide: admins or unassigned users). The page
  (`components/ai-playground/`, `lib/aiPlayground.ts`): Setup · Chat · Inspector
  (tabs below lg), compare two set-ups, teach a correction / example through
  `/api/ai/lessons` (status follows the learning mode), export JSON / Markdown;
  the transcript is cached in localStorage `playground:<username>`, cleared by
  `clearAuth()` and every login (`lib/playgroundCache.ts`). Tests:
  `AiPlaygroundTests` (HTTP-level `FakeChatApi`); Test lab scenario
  `ai-playground-no-side-effects`.
- **Tags replace Service Providers.** `Resource`/`ResourceLink`,
  `ProviderScope`, the `resource_id` claim, the provider role and
  `{{provider_<key>}}` are GONE (the old `crm_profile.resource_id` column is
  only kept by `UpgradeSchema`). `Tag` (palette-key colour
  tangerine/ink/teal/plum/ochre/blue/green/rose, `AppliesTo`
  contact|entity|conversation, an exclusive `GroupName`, attribute field defs,
  entity scope = ScopeInherited) + `TagAssignment` (attribute values; unique per
  tag × target). ALL rules live in `Data/Tags/TagService.cs`: group
  exclusivity, attribute validation, and `Contact.Tags` as a write-through
  CACHE of tag names (both ways — a raw `tags` write via PATCH/import is
  reconciled into the registry, creating org-wide tags for unknown names).
  Conversation tags stay on `Thread.Labels` as slugs; the registry is their
  vocabulary (`lib/tags.ts useTags`). Attributes render as
  `{{tag_<slug_with_underscores>_<key>}}`. Caps `tags.view` / `tags.manage`;
  page `/tags` (Library). Tests: `TagsTests.cs`.
- **Bindings (`/bindings`).** One page for every many-to-many relationship,
  built on `components/ui/BindingGrid.tsx` (level 1: a card per group with a
  None / k of N / All N pill and a tri-state checkbox; level 2: Manage modal
  with per-member switches; nothing persists until the `SaveBar`). Server side
  `Endpoints/BindingsEndpoints.cs`: `GET /api/bindings/catalog`,
  `GET/POST /api/bindings/{kind}` (pick a target, get candidates with a
  `member` flag, POST a diff). Kinds: user-entities, team-users,
  contact-entities, tag-contacts, tag-entities, entity-accounts (+ reversed
  entity-contacts, user-teams, entity-users for API callers), each gated by its
  own capability.
- **Custom fields reach templates as variables.** Settings → Custom fields
  (`custom_fields` Setting, `settings.custom_fields.manage`) merge into
  `FlowEngine.VariablesFor` as `{{cf_<key>}}` (off `Contact.Attributes`), next
  to the tag attributes. The prefixes are deliberate — a field labelled
  "Company" would otherwise shadow a built-in. Values format like the dashboard
  (boolean → Yes/No, multiselect → comma list); a missing value renders empty.
  Both template editors share `components/settings/TemplateVariablePicker.tsx`.
- **Templates (`/templates`)** — ONE module (`components/templates/
  TemplatesModule.tsx`) with tabs in the URL (`?tab=messages|scripts|whatsapp`):
  Messages = `CannedResponse` quick replies (`/api/messaging/canned`,
  `templates.manage`, optional `Channels` list — empty = every channel; the
  composer's `/short_code` picker passes `?channel=`), Call scripts =
  `ScriptTemplate` (`/api/calls/script-templates`, `script_templates.manage`),
  WhatsApp = per-account templates (`/api/whatsapp/templates`). Old
  `/script-templates` and `/settings/templates` redirect here.
- **Flows** (the product calls them **Journeys**) — see **Journeys** below for
  the editor and the decision step (Check conditions / Let AI decide). Execution:
  `Flows/FlowEngine.cs` is the ONLY interpreter of the builder's React Flow
  JSON (`Workflow.Nodes/Edges`, stored verbatim). Time axis
  `FlowEngine.Tick` (`Workers/FlowEngineWorker`, every 10 s,
  `FLOWS_WORKER=0` disables; `POST /api/flows/tick` is the manual seam);
  event axis `OnInboundMessage` (resume runs parked on a reply, start
  "Message received" flows). Per-contact state is `FlowRun`; every transition
  appends a `FlowStepEvent` (org-local `Day`). "Contact created" and
  "Scheduled" triggers only act on what happens after `Workflow.LiveSince`, so
  arming a flow never back-fires on the whole contact book. Sends are
  **QUEUED** onto `OutboundJob`, never inline: `Flows/QueueService.cs` drains
  (`Workers/OutboundQueueWorker`, every 5 s, `QUEUE_WORKER=0` disables) with a
  jittered per-account gap, daily cap, quiet hours and an **atomic
  queued→sending claim** (two tickers can never double-send). Job kinds: text |
  template | ai_reply (reply generated at SEND time by `AiResponder`) | call.
  Payload `source:"ai"` jobs go through the AI gate; `humanize:true` jobs are
  split and typed. **Calls are queued too**
  (`Flows/QueueCallPlacer.cs`): `transport:"sip"` → the same LiveKit path as
  `/api/calls/dial` (paced by `queue_call_gap_s` + one dial per pass);
  `transport:"baileys"` → dispatch the agent, then the gateway's `/call`. A
  WhatsApp number carries ONE call — busy is checked at the GATEWAY, and busy is
  a WAIT (job stays queued), never a failure; only compliance refusals
  (do-not-call, opt-out, cold-call guard) or an unconfigured transport fail a
  call for good. `sent` means DIALLED. Integration action steps
  (`Integrations/IntegrationActions.cs`) execute for real against the resolved
  connection or SKIP with `{ok:false, skipped:true}` so the run continues — the
  seven action labels are a contract with `frontend/src/lib/flow/nodeConfig.ts
  INTEGRATION_ACTIONS`. Inspect: `/api/flows/runs[/{id}]`,
  `/api/flows/{flowId}/stats`, `/api/flows/queue`, `/api/analytics/flows`.
- **Communication hub + analytics.** `GET /api/contacts/{id}/channels` = the
  per-contact channel profile (identities across channels with account +
  health, in/out counts, last active, 24h window + opt-out) rendered by
  `components/ChannelHub.tsx` in the chat info panel and `/contacts/[id]`;
  `POST` registers an identity by hand. Besides conversations it lists
  REGISTERED identities (`crm_contactidentity`, `Contacts/ContactIdentities.cs`:
  WhatsApp / voice / SMS / email derived from the contact's phone and email on
  create, edit, import or by hand — never an empty thread) with a WhatsApp
  verdict `verified` | `not_on_whatsapp` | `unknown` from a lookup through a
  connected QR number (`Workers/IdentityCheckWorker`: ≤1 lookup / 2 s per
  gateway, 30-day cache, `IDENTITY_CHECK_WORKER=0` disables; Cloud has no
  lookup). "Find channels" = `POST /api/contacts/{id}/channels/discover`;
  "Invite to another app" = `POST /api/contacts/{id}/invites` (8-character codes,
  30 days; wa.me / `t.me?start=` / `m.me?ref=` / ig.me / mailto links + a QR SVG
  via QRCoder). Routes in `Endpoints/ContactIdentityEndpoints.cs`.
  The Dashboard (`/dashboard`, the HOME page; the word "Analytics" is never
  shown to users — `/analytics` redirects, the `analytics.*` capability ids and
  `/api/analytics/*` routes keep their names):
  `GET /api/analytics/summary?days=` (funnel, per-account volumes, call
  outcomes, health events, daily series, KPI deltas),
  `GET /api/analytics/usage?days=&entity=` (LLM metering at COST, `billing.manage` —
  companies never see it; follows the entity switcher: rows stamped in the picked
  subtrees + unstamped rows whose contact belongs there, entity-less usage only
  under All), **the AI bill** (`/billing`, Review;
  `Endpoints/AiBillingEndpoints.cs`): per calendar month, per COMPANY = a
  top-level entity (branches roll up). A usage row's company = its
  `crm_llmusage.entity_id` (stamped by the AI brain from the reply's entity)
  → its contact's entity when every link shares one company → unattributed
  (skill drafting, embeddings, the operator assistant — shown to managers,
  billed to nobody). Bill = cost × (1 + markup %) × the exchange rate + a
  monthly fee — never for a month that ended before the company's
  `created_at` — (amounts kept to 4 decimals, shown so below 1 — a light month is
  fractions of a cent); cost = the provider's own price per call where it reports one
  (`crm_llmusage.cost_usd` — OpenRouter's `usage.cost`, requested with
  `usage.include`), else tokens × `llm_prices` over OpenRouter's public price
  list (`ModelPriceCatalog`, Setting `llm_prices_catalog`, refreshed ≤ daily
  when a report is read, `LLM_PRICE_SYNC=0` off) over the shipped defaults (ONE
  pricing rule, `Ai/LlmPricing.cs`, shared by the usage report, the bill and the
  quotas); terms in ONE org-wide Setting `ai_billing`
  (`GET/PUT /api/billing/ai/settings`, `billing.manage`, Settings → AI
  billing `/settings/billing`; model + rules in `Ai/AiBillingTerms.cs`). **Terms
  are locked per month** (UTC months, like the bill): the Setting holds the
  current terms + `effective_from` (yyyy-MM) + `history[]`; a PUT replaces this
  month's terms, or moves older ones into the history and starts the new ones
  this month (unchanged terms change nothing); the bill for month M uses the
  latest terms with `effective_from` ≤ M (a legacy row without one = every
  earlier month; a month older than every set uses the oldest). The settings GET
  returns `history` (newest first, with `until`) for the "Terms by month" list;
  consecutive identical periods are ONE period (`AiBilling.Collapse`, applied on
  save and when listing). The bill's `pricing_note` stays English for API
  callers; the dashboard translates `pricing_note_code`.
  **AI usage quotas** (`Ai/AiQuota.cs`, Setting `ai_quotas` {entities:{id:amount},
  users:{username:amount}}, billing currency, 0/absent = no cap): spend = the
  month's BILLED usage (the month's terms; the fee never counts) — a company cap
  counts everything its bill counts (contact fallback included), a branch cap the
  rows whose `entity_id` is in its subtree, a user cap the rows stamped with
  `crm_llmusage.user_name` (the operator assistant, the playground, skill
  drafting, thread + timeline summaries — `AiQuota.CurrentUser()` from the audit
  context). Cached ≤60 s per database in-process (`AiQuota.Invalidate()` on every
  quota / terms save; tests call it after writing usage rows); no caps = no
  query; a read failure fails OPEN. At 100 % the AI is off for those chats (see
  `AiEligibility` above, so every gate + payload follows) and an inbound the AI
  would otherwise have answered gets ONE tracked handover kind `quota`
  (`InboundProcessor.QuotaHandover`, `standDown: false` — the tick stays and the
  AI resumes by itself); a user at cap gets 429 `{detail, quota: true}` from the
  playground, skill drafting and summaries (`Endpoints/QuotaGuard.cs`) and the
  assistant answers the refusal (`mode: "quota"`). `AiQuota.Sweep` warns the
  tenant's admins (+ the person for a user cap) at 80 % and 100 %, once per scope
  per month (Setting `ai_quota_warned`), from the AI-ops worker, the quotas GET
  and a quota handover. `GET/PUT /api/billing/ai/quotas` (`billing.view` or
  `billing.manage`; PUT merges, 0/null clears): billing managers set anything,
  admins (WRITE cap `billing.quotas.manage`, theirs by default — `billing.view`
  alone, or a key scoped to it, only reads) set branch caps + same-tenant users
  inside their scope (never a company cap, never a superadmin, never their OWN
  cap — only a superadmin sets their own — and never a cap a billing manager set:
  `ai_quotas.set_by_manager`); a cap is ≥ 0.01 (0 = none); audited `billing.quotas`.
  Quotas also gate the operator's suggest-reply (`POST /api/aicore/reply`, charged
  to them as `LlmUsage.UserName`), the AI reviewer, AI voice calls (dial, WhatsApp
  call/start, queued calls) and flow AI decisions (the "when unsure" path with a
  note). UI: the Quotas card on
  `/billing` (`components/billing/AiQuotas.tsx`). Tests: `AiQuotaTests`,
  `AiBillingTermsTests`. `GET /api/billing/ai?month=&company=`
  (`billing.view`): billed amounts for the caller's companies (assigned
  entities' companies; unassigned = all); cost, markup, margin and the
  unattributed bucket only with `billing.manage`. **Download PDF** =
  `GET /api/billing/ai/pdf?month=&company=&lang=en|ms|zh` (same gate, scope and
  visibility as the JSON — both built by `AiBillingEndpoints.BuildBillAsync`;
  A4 MigraDoc with the Documents fonts, `Endpoints/AiBillPdf.cs`; one company =
  summary + lines by feature + days with usage, "All companies" = every visible
  company + a total row; `zh` falls back to English without a CJK font; an MCP
  exclusion, binary) — the dashboard fetches it as a Blob and saves it. On an
  iPhone / iPad (`pwa.ts isIos`), where `window.print()` does nothing inside the
  installed app, Print is hidden and Download PDF opens the PDF in a new tab
  through a **signed link**: `POST /api/billing/ai/pdf/link` (`billing.view`,
  sessions only — an API key gets 403) mints `GET /api/billing/ai/pdf/s/{token}`
  (ANONYMOUS; `Endpoints/BillLink.cs`: HMAC-SHA256 with a per-process key, binds
  profile + username + month + company + language, 5 minutes; on open the person
  must still be active with `billing.view` and the bill is rebuilt AS them).
  Print (desktop) hides the shell (`.print-hide` + `@media print` in
  `globals.css`: html/body/the shell column/scroll panes un-clipped, floating
  pieces and the Quotas card hidden, ink on white whatever the theme, rows never
  split). Tests: `AiBillingTests`, `AiBillPdfTests`.
  `/api/analytics/reports`, `/api/analytics/flows`, `/api/live` (waiting
  conversations + active calls + `needs_human_count`).
- **Integrations** (`/integrations`, `/api/integrations/*`,
  `ChatBranch.Data/Integrations/`). `Integration` is `ChannelAccount`'s twin:
  creds in a JSON column that **never leaves the server unmasked**, `Config` for
  per-provider settings. Providers: Google Calendar / Drive / Gmail / Sheets /
  Contacts (read-only `contacts.readonly` — the caller's own address book for the
  contact import) (`GoogleOAuth.cs`: one Google client, incremental per-product
  scopes, PKCE S256, HMAC-signed 600 s `state`, lazy refresh that marks a row
  disconnected and returns "" rather than throwing), Shopify / Stripe (API
  keys) and Zoom (a Server-to-Server OAuth app — meeting links for bookings, see
  Calendar booking; the add-account form stores each provider's own secret fields). Google Calendar HTTP (free/busy, insert / delete an event, list events /
  calendars) is ONE client, `Integrations/GoogleCalendarClient.cs`, shared by the flow
  actions, the AI's booking tools and the Bookings page's Calendar tab. **Personal, entity or organization — every payload
  says which** (`scope` personal | entity | organization + `entity_name` /
  `entity_slug`; the page's "Used by" column): `Integration.OwnerUserId` set =
  personal (its owner only); ownerless with an `EntityId` = that entity AND its
  branches; ownerless without one = the whole organization. Resolution at call
  time (`IntegrationResolver.Active`) = the acting user's own grant → the
  entity's shared grant → its PARENT entities' (`AncestorChain`, walking UP) →
  the organization's: a branch inherits head office's connection, head office
  never borrows a branch's, siblings never share. Automated work (journeys,
  the AI with a shared calendar) has no acting user, so it only ever sees
  shared rows. **The Integrations page is for SHARED connections**: "Connect
  for…" (`EntityPicker`, the organization-wide choice first) →
  `GET /api/integrations/oauth/{provider}/start?for=shared[&entity=slug]`
  (`integrations.manage` + the entity in the caller's scope; `IntegrationResolver.SharedRowAsync`,
  reconnecting the same scope reuses its row; an unknown entity = 404); API-key
  accounts take `entity` the same way. Without `for=shared` the start writes
  the CALLER's own row (`OwnRowAsync`). Google Contacts is personal only.
  **Settings → My connections** (`/settings/connections`, `MyConnectionsSettings.tsx`) is where
  anyone connects their own: `GET /api/integrations/mine` (any signed-in user, no
  capability) lists the Google products a person connects for themselves + their
  own grant of each; connect = `GET
  /api/integrations/oauth/{provider}/start?return=connections` (the signed state
  carries an allowlisted return key, so the callback — success or refusal —
  lands back on that page); disconnecting one's OWN row
  (`POST /api/integrations/accounts/{id}/disconnect`) needs no capability. A
  personal Google Calendar is what a booking skill set to "the assigned agent's
  calendar" books into.
  The org-wide keys live in ONE JSON Setting `integrations_settings`
  (`IntegrationSettings.cs` — a plain `SettingValue("google_client_id")` finds
  nothing). The callback `/api/integrations/oauth/google/callback` is
  deliberately ANONYMOUS (the signed state IS the CSRF control); it is safe to load twice (browsers reload / prefetch it: a repeat that finds the row connected within 2 minutes — verifier gone, or Google refusing the reused code — lands on `?connected=` too, `GoogleOAuth.JustConnected`); set
  `GOOGLE_REDIRECT_URI` and `FRONTEND_URL` in production. Both commerce
  webhooks verify HMAC (`WebhookVerify.cs`: Shopify base64, Stripe `t=…,v1=…` +
  300 s replay window) and **no secret configured ⇒ 503, never "accept
  unsigned"**. Sandbox is mandatory: with zero credentials every endpoint
  answers and every action skips cleanly.
- **Test lab** (`/test-lab`, Review; `/api/testlab/*`, superadmin;
  `Api/TestLab/`) — ~40 scripted use cases (AI, review, handover, identity,
  compliance, routing, access, automation, contacts, booking, platform) run
  INSIDE the API through the real pipelines (`InboundProcessor`, the AI gate,
  the queue, routing, flows, automations, the dashboard's endpoints via
  `InternalApi` as a given user), each with named checks and links to what it
  made; runs are stored (`crm_testlabrun`, last 50). Everything it creates is
  FENCED under one top-level entity `test-lab` (sandbox inboxes with no
  credentials, `@testlab.invalid` contacts, `testlab_*` users, its own entity
  Settings) and `POST /api/testlab/reset` deletes exactly the fence. "Load demo
  data" fills the fence with a clickable dataset. With no AI key a deterministic
  test brain writes the AI's words. CLI: `npm run test:scenarios`
  (`scripts/test-lab.mjs`, exit 1 on a failure); `TestLabTests` runs it in the
  suite. Lab runs drive the shared queue / flow / monitor ticks, so set
  `TEST_LAB=0` (routes not mapped) on a production server. Guide:
  `docs/testing.md`.
- **Docs:** `docs/vertical-guide.md` (configure the template for an industry:
  hard rules → persona → skills → knowledge → lessons → memory, plus tags, the
  AI monitor, follow-ups and teaching the AI) · `docs/DATABASE_SCHEMA.md` (the real schema) ·
  `docs/DOMAIN_MODEL.md` (Entity · Customer/Contact · Tag · Admin/Profile) ·
  `docs/API_CONVENTIONS.md` (the REST surface + which endpoints are anonymous) ·
  `docs/RBAC_AND_BRANDS.md` (roles, capabilities, entity scope) ·
  `docs/postgres.md` (SQLite ↔ PostgreSQL, the one-time copy, the switch-over runbook) ·
  `docs/record-sync.md` (`/api/v1/sync/records`) · `docs/channel-adapter.md`
  (per-channel capabilities, port gaps, how to add one) ·
  `backend/voice_agent/README.md` · `backend/baileys_service/README.md` +
  `RESEARCH.md` (Baileys ban evidence, the pacing numbers the queue defaults
  come from). Files marked "historical" at the top (`CSHARP_*`, `UPGRADE-SPEC*`,
  `*-PLAN.md`, `reschedule-flow.md`) describe how the code got here, not how it
  is.

## Frontend conventions

- **Shell (`components/shell/`).** The sidebar IA lives in `nav.ts`
  (`NAV_GROUPS`: Dashboard + Notifications, then Work (Chats · Contacts ·
  Bookings · Account assignment) · Automate · Library · Channels · Admin ·
  Review; Settings is the footer link) — **a new page = one entry there**, which
  also gates it: `RouteGuard.tsx` shows a "no access" panel instead of a page
  whose entry (`routeDef`) the caller can't see. Groups are collapsible and the
  whole rail collapses (`nav-collapsed`). Items are gated by `cap` (a string, or
  an array = ANY of them) with a `roles` fallback for sessions without a
  capability list (`canSee`). **The Dashboard is home** (`HOME = '/dashboard'`, the page
  is `app/dashboard/page.tsx`; `/` and the old `/analytics` redirect there —
  next.config.js on the server, `LegacyRedirect` as the client fallback — and
  post-login lands there; someone without `analytics.view` is sent to `firstVisibleHref`). Ctrl/⌘+K opens the
  command palette (`CommandPalette.tsx`): the visible nav pages plus
  `GET /api/search` (contacts, conversations, templates, knowledge, flows, users,
  entities — each type gated by the capability of the page it opens and scoped
  like that page's list endpoint). The top bar's **Live chip** (`LiveChip.tsx`)
  shows the SSE connection status dot + "N calls · N waiting" from `/api/live`
  (`liveStore.ts`: refreshed every 15 s AND on every stream `change`, keyed by
  entity; also feeds the sidebar's Chats counter and the Dashboard's waiting card);
  its popover carries the AI quick toggle (`components/ai-controls/AiQuickToggle.tsx`).
  Below `lg` the rail becomes a drawer and a **bottom tab bar** (Home · Chats ·
  Contacts · More) takes over. `Shell.FULL_BLEED` (`/chats`, `/journeys`,
  `/settings`) render edge-to-edge and own their height; `PUBLIC_ROUTES`
  (`/help`, `/offline`) and `/login` render bare. **PWA:** `app/manifest.ts`,
  hand-written `public/sw.js` (never caches `/api/*` or the stream; offline
  navigations land on `/offline?from=`), icons in `public/icons/` rendered from
  `Logo.tsx`'s `brandMarkSvg`. **Install:** iPhone/iPad browsers never fire
  `beforeinstallprompt`, so `InstallHint.tsx` (in the Shell, signed-in only,
  never in the installed app) shows the Share → "Add to Home Screen" steps there
  and an Install button where the browser can prompt (`pwa.ts` `isIos`,
  `useInstallPrompt`); "Not now" snoozes 30 days, the sidebar's "Install app"
  reopens it (`pwa:show-install`). It never stacks with a page guide (waits
  while `useTour().busy`, and does not open by itself on the first visit to a
  page whose guide auto-starts), and only its buttons take touches. The body never scrolls (the shell's panes
  do), so bare pages (`/login`, `PUBLIC_ROUTES`) scroll the DOCUMENT instead
  (`PageScroll` in `Shell.tsx` adds `html.page-scroll`, `globals.css`; it also
  jumps to a `#anchor` once drawn) — an inner scroll box did not scroll on iOS. **Ask ChatBranch** (`AssistantDock`) opens from
  the top bar's sparkle button, Ctrl/⌘+J, or — on desktop, while closed — a
  slim tab on the right edge (6 px peek at 42% height, slides out on hover);
  the shell owns its `open` state. Desktop can switch it between docked (the
  right-hand panel) and a FLOATING window dragged by its header
  (`assistantLayout.ts`, localStorage `assistant-layout`, clamped on screen).
  Never a bottom-right pill: that covered composers and save bars. Its chats live on the server (History
  view, per user); the browser keeps only `assistant:<username>:current` (open
  chat id + draft, `lib/assistantCache.ts`), and `clearAuth()` (sign-out, 401
  expiry) and every login remove every `assistant:*` key.
- **Page guides + the manual.** `lib/tours.ts` is ONE list of tours (page,
  steps → `data-tour="<target>"` elements; words in the `guide` namespace by
  convention `guide.tours.<id>.steps.s<n>.title|body`, en/ms/zh) feeding BOTH
  the in-app guide and the public manual. `components/tour/TourProvider.tsx`
  (in the Shell) spotlights one target at a time (missing targets are skipped,
  no target = a centred card), starts by itself on a person's FIRST visit to a
  page (localStorage `tour-seen:<user>:<id>`, off via the Guide menu's
  `tour-auto:<user>` — both mirrored to the ACCOUNT as `Profile.Preferences`
  `guides {auto, seen[]}` through `PUT /api/auth/preferences {guides: {auto?,
  seen?, reset?}}` (seen ids add up) and copied back into the browser once per
  sign-in by `syncGuidesFromServer`, so a closed guide stays closed on every
  device and after a re-login; an iPhone keeps Safari's and the installed app's
  storage apart), and on `?tour=<id>`; the top bar's ? button
  (`GuideMenu.tsx`) replays the page's guide, the app tour (`shell`) or opens
  the manual. `/help/manual` (public, like `/help`) prints every tour as a
  chapter with "Show me in the app" (`<href>?tour=<id>`), Download PDF (print),
  Download HTML (one self-contained file in the reader's language) and an
  iframe embed snippet (`?embed=1` drops the chrome). A new page = a `TOURS`
  entry + its `guide.tours.<id>` words in 3 locales + `data-tour` targets
  (`PageHeader` carries `page-header`; `Card`, `DataTable` and `Advanced`
  forward `data-tour`). Every sidebar page has a tour. **Step-by-step guides**
  (the manual's "baby guide" half) are data in `lib/guides/<group>.ts` (start ·
  channels · work · ai · automate · admin; `GuideDef` = href, tour, step / tip
  counts, before / trouble, who) with words in the `manual` namespace
  (`i18n/messages/<locale>/manual/<group>.ts`, keys in `lib/guides/types.ts`);
  troubleshooting + glossary ids in `lib/guides/help.ts`; `QUICK_START` is the
  "Your first day" checklist. `/help/manual` renders them all with a search box,
  and the Guide menu lists the guides done on the current page (`guidesFor`). A
  UI change that renames a button or moves a field = update the guide that
  quotes it.
- **Simple first, Advanced on click.** A form, drawer or settings card shows
  only what a first-time user needs; everything else sits in
  `components/ui/Advanced.tsx` ("Advanced settings", closed by default, `count`
  of customised values, `forceOpen` when a value inside is non-default or has an
  error — never hide a problem). A moved field must have a working default; a
  field that is optional to save but breaks things when empty (Meta's app
  secret) stays up front (`FieldSpec.recommended`).
- **`src/lib/AppContext.tsx`** holds shared state + the real-call seams. API
  base = `NEXT_PUBLIC_API_URL` (default `http://localhost:8090/api` in
  `next.config.js` and `frontend/.env.example` — no longer the Django-era 8000;
  baked in at BUILD time). `src/lib/mocks.ts` is the type source (`Contact`, `RecordDto`,
  `TranscriptLine`, …) — its data arrays are EMPTY since the production-clean
  pass; only the voice presets remain. It was once lost to a `.gitignore`
  `lib/` rule — `.gitignore` explicitly un-ignores `frontend/src/lib/`.
- **The API base comes from ONE helper, `src/lib/apiBase.ts`** — import `API`
  from it, never read `process.env.NEXT_PUBLIC_API_URL`. The base may be RELATIVE
  (`/api`, what release images are built with): `fetch` / `EventSource` / `src`
  take `API` or `apiResource(path)` as they are, but anything SHOWN or handed to
  another site (the MCP URL, the widget snippet, webhook URLs, a tab to open)
  goes through `absoluteApiUrl()` / `absoluteApiResource()`, which resolve
  against `window.location.origin` (call them after mount). Nothing in Next
  calls the API server-side; a future server-side caller needs its own runtime
  variable (e.g. `API_INTERNAL_URL`).
- **Calls are multi-concurrent.** State is a `calls: Record<room, LiveCall>` map
  plus a waiting `queue`. Seams: `dialContact(id)`, `dialManual(phone,…)`,
  `launchBatch(ids)`; every per-call action is room-scoped (`sendSay(room,…)`,
  `hangupCall(room)`, `escalateCall(room)`, `setHumanControl(room,v)`,
  `resetCall(room?)`). Cap = `NEXT_PUBLIC_MAX_CONCURRENT` (default 3 — your SIP
  trunk's channel limit); `startDial` inserts a `pending-N` placeholder
  synchronously so a batch can't overshoot it. The call UI lives in `/chats`
  (`components/chats/`: `CallLauncher`, `VoiceCallButton`, `ScriptCallButton`,
  `ActiveCallsStrip`, `VoiceCallPanel`) plus `CampaignModal` for batch dials;
  there is no `/live-calls` page. `/call-logs` lists finished calls (its
  detail page's "Sentiment" is a static mapping from call status, not an AI
  judgement). `/persona` ("Voice & calls") is the voice library, opening line
  and pronunciation — not the chat persona. Clicking a voice there saves the
  ORGANIZATION's call voice (`PUT /api/voice/default-voice`, `settings.ai.manage`,
  unbound callers only, Settings
  `default_voice` + `default_voice_provider`; `GET /api/voices` returns
  `default_voice`); `VoiceDispatch` uses it for any call that names no voice
  and no provider.
- **Settings is system configuration ONLY**, organised as SECTIONS of flat
  `/settings/<id>` pages, each with its own capability
  (`components/settings/SettingsNav.tsx` `SETTINGS_SECTIONS`, checked again
  server-side): Personal — My account (no cap; `/settings/account`, your own
  name / email / password / devices via `/api/auth/me/*` —
  `Endpoints/MyAccountEndpoints.cs`, writes session-only, wrong passwords
  throttled; also opened from the sidebar's name chip), Appearance (incl. Language), Time zone (no cap; the business
  zone editable with `settings.appearance.org` — see Time zones), My notifications, My connections (no
  cap; `/settings/connections` connects your own Google products, see
  Integrations); Organization — Notification defaults (`notifications.manage`),
  Custom fields (`settings.custom_fields.manage`), App & provider keys
  (`app_keys.manage`), System email (`app_keys.manage`; `/settings/system-email`, the
  SMTP mailbox welcome emails and password links go out through —
  `Endpoints/SystemEmailEndpoints.cs`), AI billing (`billing.manage`); AI — AI keys & models (`settings.ai_keys.manage`; also
  the embeddings provider, `components/ai-learning/EmbeddingsSection.tsx`). New page = a
  `SETTINGS_SECTIONS` entry + `app/settings/<id>/page.tsx` + the capability in
  `AdminEndpoints.cs` and `lib/auth.ts` + the server-side check. Everything
  else moved to the sidebar and its old route redirects (`movedAreas.ts`,
  `LegacyRedirect`): Channels, Integrations, Users, Entities, Templates, Script
  templates, Knowledge, Skills, Account assignment (`/assignments`), MCP
  (`/developers`), Service providers (`/tags`), Pronunciation
  (`/persona#pronunciation`); the AI Configuration (`/settings/ai` →
  `/channels`), Chat Persona and Escalation Rules tabs are gone — don't re-add
  them. Pages that stage edits use `SettingsDirtyContext` (Discard / Save in the
  masthead).
- **Crashes never blank the app.** `app/error.tsx` (route segments) and
  `app/global-error.tsx` (the root layout, inline styles only) render
  `components/ui/CrashPanel.tsx`; `components/ui/ErrorBoundary.tsx` isolates the
  shell's floating pieces (command palette, campaign modal, Ask ChatBranch) so
  one of them failing leaves the page usable. `lib/clientErrors.ts` reports
  each crash to `POST /api/client-errors` (signed-in, logged as a warning under
  `ChatBranch.ClientErrors`, no audit row) and turns a stale-deploy
  `ChunkLoadError` into one reload onto the new build.
- **A modal's errors render INSIDE the modal.** `Modal` portals over a scrim,
  so a page-level `{msg && …}` under an open dialog is invisible. Pattern: a
  `role="alert"` div as the first child of the modal body, the page-level copy
  gated on the modal being closed, `setMsg(null)` when opening one. The same
  holds for drawers (`InboxSetupDrawer`).
- **Localisation (`src/i18n/`).** English, Bahasa Melayu, 简体中文, Manglish (`locales.ts`;
  keep in step with `SettingsEndpoints.SupportedLocales`). Strings live in
  `messages/<locale>/<namespace>.ts` — one file per area (`common`, `shell`,
  `ui`, `chats`, `contacts`, `flows`, `ai`, `admin`, `settings`, `channels`…);
  English is the SOURCE and `Dict<typeof source>` makes every other locale carry
  exactly its keys, so a missing translation is a type error. **Manglish**
  (code `manglish`, the dashboard's friendly Malaysian-English voice) is an
  OVERLAY instead (`messages/manglish/`, `Overlay<T>`): it rewrites only the
  sentences (help, notices, empty states, confirmations, errors, the page
  guides), `translate()` falls back to English per key (the exact plural form
  first), labels people click / the manual quotes and anything customer- or
  AI-facing stay English; rules + voice in `src/i18n/GLOSSARY.md`, checked by
  `node scripts/check-manglish.mjs` (keys, `{placeholders}`, plural pairs). It is
  never a customer's language: `CONTENT_LANGUAGES` (the widget, a contact's
  language) and the API's `SettingsEndpoints.CustomerLocales` keep en/ms/zh, and
  emails / the bill PDF in Manglish go out in English. **No user-visible
  English literal in a component**: `const t = useT()` → `t('chats.filter.title')`,
  `t('common.counts.items', { count })` (plural pairs `x_one`/`x_other`, picked by
  `Intl.PluralRules`), `{name}` placeholders, `<Trans k=… values={{ link: <Link/> }} />`
  for sentences with elements; shared words come from `common`. Dates, times,
  relative times, numbers and currency go through `useFormat()` (never
  `toLocaleString()` or hand-built strings). Module-level constants hold KEYS,
  translated at render (`nav.ts` `lbl` is a `MessageKey`). The language: the
  user's own (`Profile.Preferences.locale`) → the org default (Setting
  `default_locale`, both via the appearance endpoints) → the browser; Settings →
  Appearance → Language, a switcher on `/login`, synced once per sign-in
  (`syncLocaleFromServer`). Server render and first paint are English (no
  hydration mismatch). API error `detail`s, stored notes and audit text stay
  English.
- **Never call `getRole()` / `getAuth()` during render.** They read
  localStorage, which doesn't exist during SSR — the server paints the
  `'agent'` fallback, the client paints the real role, and React aborts
  hydration. Read the role/capabilities **after mount**: `useAccess()`
  (`lib/useAccess.ts`, `{ready, can}`), `useRole()` (`lib/useRole.ts`,
  `{role, ready}`), or `useState(false)` + `useEffect`. `lib/auth.ts` still
  migrates the legacy `chatwood-auth` / `ai-collector-auth` session keys and a
  cached `'collector'` role.
- **Contact identity is per COMPANY** (`Data/Contacts/ContactCompanies.cs`): a
  contact needs a name AND a phone or an email; the same phone (last-8 rule) or
  email may exist once per company (top-level entity + branches) — create,
  edit and import check only the companies the contact belongs to (no entity =
  the org-wide contacts), and a 409 names the existing contact (+
  `existing_id`) only when the caller can see it (the Add form then offers "add
  this entity to that contact instead"). When the caller CAN'T see it, a new
  contact's 409 names no one and carries an opaque `request_token`
  (`Contacts/ContactAccessToken.cs`: AES-GCM, the caller + the target entity,
  30 min) → **Request access** (`Endpoints/ContactAccessEndpoints.cs`,
  `crm_contactaccessrequest`, `/api/contacts/access-requests`, ask with
  `contacts.manage` or `chats.send`): the admins who can see the contact AND
  manage the target branch get a notification + audit `contact.access_requested`;
  approve links the contact to that entity (its others stay), deny notifies the
  requester with the reason; nothing about the contact reaches the requester
  before approval. UI: `components/contacts/AccessRequestsPanel.tsx` on
  `/contacts` (`?access=<id>` opens it) + the Add form's offer. Tests:
  `ContactAccessTests`. Inbound matching
  (`InboundProcessor.MatchContact`, the SMS start, the identity exchange) picks
  the receiving entity's contact, else its company's, else an org-wide one —
  never another company's. Chat-made contacts are linked to their
  conversation's entity; `Seed.BackfillContactEntities` linked the old ones once
  (Setting `contacts_entity_backfill_v1`). The contacts list under an entity
  also shows contacts with a conversation there; `?entity=__none__` = no entity.
  A contact's views (merged conversation, calls, bookings, timeline + its AI
  summary, channel hub, documents, search hits) list only the caller's own
  entities' conversations / calls / bookings / documents — "has a conversation
  there" makes a contact visible, never another company's data. The contact
  page asks for its log BY CONTACT (`GET /api/messaging/timeline?contact_id=`,
  the summary POST `{contact_id}` — `MessagingEndpoints.CollectForContact`): the
  conversations and calls linked to THAT contact plus unlinked ones on its phone /
  email inside its own companies, never one linked to another contact — the same
  phone saved as a contact in two companies keeps two separate logs and
  summaries (`CallsForAsync` likewise). The phone / email form (MCP, API callers)
  lists every chat on the number and names a contact only when exactly one
  visible contact has it. Tests: `ContactTimelineTests`.
- **Contacts:** `/contacts` is a list (the kanban board is gone) with a 4-step
  **import wizard** (`components/contacts/ContactImportModal.tsx`: a .csv / .xlsx
  / .vcf file or your own Google Contacts → map columns → duplicates skip / fill
  empty / overwrite + entity, tags, find channels → import, skipped rows as a
  CSV; 20 000 rows / 10 MB; phones normalized to E.164 with Setting
  `default_country_code`, default 60; `POST /api/contacts/import`,
  `contacts.manage`); `/contacts/[id]` shows the AI summary
  (`/api/messaging/timeline/summary`), details + custom fields, records, the
  channel hub, entities (`ContactEntitiesCard`), tags (`ContactTagsCard`),
  bookings (`BookingsCard`), "AI for this contact" (`ContactAiModeCard`) and the
  communications timeline. In `/chats`, an unlinked conversation shows
  `IdentityBanner.tsx` (the identity exchange's state) with a "Create contact"
  quick action (`POST /api/contacts/identify/{channel}/{key}/contact`).

### Design system — Paper & Ink (light/dark)

Design canvas: https://claude.ai/artifact/7dcX3rLGAH3zXZeKu61Pdb

- **Ten switchable themes × light/dark**, all CSS variables on `<html>`
  defined in `src/app/globals.css`: `theme-paper` = **Paper & Ink** (default:
  warm paper, ink type, Fraunces serif headings, ONE tangerine accent),
  `theme-graphite` = Graphite Pro (neutral, violet accent, compact),
  `theme-soft` = Soft Blocks (pastel, round), `theme-ocean` (sea blue, Plus
  Jakarta Sans), `theme-forest` (sage + deep green, Lora headings),
  `theme-midnight` (deep navy/indigo, best dark), `theme-rose` (blush +
  raspberry, Fraunces), `theme-sand` (desert linen + terracotta, Source Serif
  headings), `theme-nord` (frosted grey-blue, IBM Plex Sans), `theme-contrast`
  (High contrast — black/white, 7:1 text). Every theme defines the SAME token
  set in both modes (a dark block must redefine every colour its light block
  sets, or the light value leaks through — `:root.theme-x` and `:root.dark`
  tie on specificity). `npm run check:contrast`
  (`frontend/scripts/check-theme-contrast.mjs`) resolves the cascade per theme ×
  mode and fails on any text ink under 4.5:1 on app/panel/surface/card/well, or
  on-primary / on-accent / nav-active pairs — run it after touching a palette.
  The picker's preview swatches mirror the tokens in `lib/theme.ts THEMES`
  (keep them in step). Mode = `.light` / `.dark` class
  (Tailwind `darkMode: 'class'`). Every semantic Tailwind colour resolves to a
  var — surfaces `app / surface / panel / card / well`, text `text-primary /
  text-heading / text-high / text-mid / text-muted / text-faint`, `line /
  line-strong`, `fill / fill-hover / fill-subtle`, `primary / on-primary`,
  `accent` — so no component carries per-theme overrides.
- **Accent scales are bare RGB-triplet vars** (`--c-brand`, `--c-green`,
  `--c-amber`, `--c-red`, `--c-blue`, `--c-purple`, `--c-cyan` + shades) so
  opacity modifiers keep working (`bg-brand-400/10`). Shade convention: DEFAULT
  = fills/dots/borders, 400 = stronger fill/border, 300/200 = readable TEXT ink.
  For inline text colour use the `--brand-ink / --green-ink / --amber-ink /
  --red-ink / --blue-ink / --purple-ink / --cyan-ink` vars. `brand` is
  TANGERINE on Paper & Ink (each theme brings its own brand hue). On Paper &
  Ink, Midnight and High contrast the primary action is INK (`THEMES[].inkPrimary`);
  on the others it is the accent.
- **Appearance prefs** (`lib/theme.ts` `Appearance`, every key also validated
  server-side in `SettingsEndpoints.Appearance.cs` — keep the vocabularies in
  step; old saved looks load, missing keys take `DEFAULT_APPEARANCE`): theme,
  mode (light/dark/system), accent override (with **contrast correction** —
  `accentVars` walks lightness until text inks reach 4.5:1 and fills 3:1
  against the theme's ground), text size (S–XL presets or a free `fontScale`
  85–130 % → `--ui-zoom`: the shell applies `zoom` and divides its height by
  it), corner radius (theme/square/sharp/round → `--r-md…--r-3xl`), body font
  (`--font-sans`) and heading font (`--font-display`; legacy `serif`/`sans`
  still accepted) from 14 self-hosted faces (`FONTS`; @fontsource-variable
  imported in `app/layout.tsx` — @font-face only downloads what renders; Noto
  Sans SC's ~100 KB stylesheet is imported on demand by `applyAppearance`),
  line height (`--lh` on body), **density** (compact/comfortable/spacious or a
  free `spacing` 70–140 % → `--space`; Tailwind's `padding` / `margin` / `gap`
  / `space` scales are `calc(<rem> * var(--sp))` in `tailwind.config.js`, so
  every page follows without edits — widths/heights don't scale), card padding
  (`--card-pad`, applied inside `.ui-card` = `Card` and `DataTable`), page width
  + margins (`--content-max` / `--gutter`, read by `.shell-content`, the
  shell's page column), and layout/motion/surfaces as **data attributes** on
  `<html>`: `data-nav="top"` (desktop top navigation, `components/shell/
  TopNav.tsx`, built from `nav.ts`), `data-nav-side="right"`, `--nav-w`
  (sidebar width), `navStart` (rail starts collapsed unless the browser has
  its own toggle), `data-motion` (reduce/full; default follows
  `prefers-reduced-motion`), `data-surface` (flat/bordered/shadow cards). All
  layout switches are CSS (globals.css, end of file) so they apply before
  hydration; phones (below lg) always keep the drawer + bottom tab bar.
  `applyAppearance` sets classes + inline vars + attributes and caches them
  in localStorage (`ui-appearance`: prefs, vars, attrs, cls); the pre-paint
  script (`lib/themeScript.ts PREPAINT_SCRIPT`, inlined by `app/layout.tsx`,
  whose `<html className="theme-paper light" suppressHydrationWarning>` swap is
  an intentional pre-hydration mutation) re-applies all of it before first
  paint. Persistence: the user's own choice at `GET/PUT /api/auth/preferences`
  (`Profile.Preferences`), the organization default at
  `GET/PUT /api/settings/appearance` (`settings.appearance.org`); the shell
  syncs once per sign-in. UI: Settings → Appearance — language, theme, mode
  and text size up front; accent, typography, spacing, layout and details
  under `Advanced`; "Reset to organization default"; admins publish the
  current look as the org default.
- **Recalled messages** (`Channels/MessageRecalls.cs`, the ONE writer of
  `crm_message.recalled_at` / `recalled_by`). A message taken back on the channel —
  WhatsApp QR "delete for everyone" (`/api/whatsapp/baileys/recall`, `from_me` =
  recalled from our own phone → `business`), LINE `unsend`, Instagram / Messenger
  `is_deleted` (`MessageRecalls.Supports`; Cloud / YCloud / Twilio, Telegram and the
  rest never report one). Per inbox, `Config.keep_recalled` (`InboxSettings.KeepRecalledKey`,
  on every account write, the drawer's "Keep messages the customer deletes", shown
  only where `recalls_supported`): off (default) = erased like on the customer's
  phone — text, transcript, the file on disk (+ its `.m4a`), reactions, the
  notification preview and the AI drafts' `answering` snapshot; on = kept, marked.
  Either way the AI never answers or reads it (`RecalledAt == null` on every AI read:
  `AiResponder`'s unanswered, history, `AiResume`, follow-ups, summaries, voice,
  reactions / quotes, learning, the reviewer; an AI task run on it is skipped) and a
  held AI reply answering only recalled messages expires (`AiGate.ExpireForRecall`).
  Payload `recalled: {at, by, kept}` (+ `reply_to.recalled`), list preview `[deleted]`;
  the bubble reads "This message was deleted" or carries "Deleted by the customer".
  Audit `message.recalled`. Tests: `RecallTests`.
- **Time zones.** Every `DateTime` the API writes is UTC with its "Z"
  (`UtcDateTimeConverter`, `ConfigureHttpJsonOptions` in Program.cs — the
  database hands timestamps back unspecified, and without it a browser read
  them as local time: a notification showed UTC). A time meant as a wall clock
  (a booking's local start, an org-local day) goes out as a STRING without an
  offset, and `useFormat()` shows such a string exactly as written in any zone.
  Settings → Time zone (`/settings/time-zone`, everyone): **Show times in** —
  the person's choice (`Profile.Preferences` `time_zone`: none = this device,
  `org` = the business zone, or an IANA id; `PUT /api/auth/preferences
  {time_zone}`), held by `lib/timeZone.ts` (synced once per sign-in, cached in
  localStorage `ui-timezone`) and applied by `useFormat()` to every time and by
  `dayStartIso` / `f.daysAgo` to date filters and day groups; **Business time
  zone** — Setting `org_timezone` (`OrgClock.Describe` / `Normalize`: IANA ids
  only, a Windows id converted), read by everyone, set with
  `settings.appearance.org` through `PUT /api/settings/appearance {time_zone}`
  (`time_zone_source` setting | env | server; unset = the server's clock, said
  in amber with a one-click "Use <this device's zone>"). Tests: `TimeZoneTests`.
- **Typography helpers:** `.display` (the theme's display family/weight/
  tracking — page titles) and `.eyebrow` (the small uppercase label above a
  title). `tint(color, alpha)` (`lib/color.ts`) makes a translucent version of
  ANY colour, including a `var()` token, via `color-mix` — use it instead of the
  old `hex + '1F'` idiom.
- **Shared components (`components/ui/`) — use them, don't re-invent:**
  `Select` / `MultiSelect` (search, keyboard, groups, tree indent, portalled
  list), `EntityPicker` / `EntityMultiPicker` + `entityTree` /
  `entityOptions`, `BindingGrid` + `SaveBar`, `ChannelLogo` (+ `channelLabel`,
  `channelColor` — real brand marks for channels and integrations; with
  `AiProviderLogo` (the AI providers' marks, LobeHub Icons, MIT) the only
  places brand hexes like WhatsApp green are allowed), `PageHeader`, `Toggle`,
  `Modal`, `DataTable` (keeps wide tables from scrolling the page sideways on a
  phone), `Button` (variants `primary | secondary | accent | danger | success |
  ghost`, sizes `sm | md`), `Card`/`SectionLabel`, `Badges` (status, health,
  account-role chips), `PhoneInput`, `TagListInput`.
- **Rules:** never a native `<select>` (use `Select`); never hardcode palette
  colours (raw hex, `white/α`, Tailwind's fixed palettes like `slate-*` or
  `emerald-*`) — use the tokens, or `tint()` on a token; **never put `var(--…)`
  in a lucide `color=` prop** or any SVG presentation attribute (it doesn't
  resolve there and the icon vanishes) — use a Tailwind text class or a static
  hex; tag colours are palette KEYS mapped to tokens, never hex.

## Voice agent gotchas

- **Node/TypeScript worker** (`backend/voice_agent/src/`): `agent.ts` (entry,
  call lifecycle, watchdogs), `session.ts` (provider models), `prompts.ts`,
  `storage.ts` (file IPC), `metadata.ts` (the dispatch-metadata contract — keep
  it in step with the API's `VoiceDispatch.cs`), `config.ts` (env from
  `backend/.env` + validation), `audioGate.ts`, `recorder.ts`, `summary.ts`,
  `transcript.ts`, `dial.ts`, `setupTrunk.ts`, `selftest.ts`. Run it with
  `npm run dev` / `npm start` in that folder (or `npm run dev:agent` at the root).
- **Pin LiveKit packages to matching versions.** Every `@livekit/agents*`
  package is pinned to the same exact version (`1.9.1`) — skew between them
  breaks imports. Bump them together, then `npm run selftest`.
- `REALTIME_PROVIDER=openai`, model `gpt-realtime` (Realtime API — NOT available
  via OpenRouter). Gemini Live (`@livekit/agents-plugin-google`) exists but its
  free tier is flaky (429 / `1011 Deadline`). `voice_provider` `elevenlabs` /
  `ilmu` in the metadata switches to an STT→LLM→TTS cascade.
- VAD: `VAD_SILENCE_MS` ≈ 900 — lower values cut off speakers who pause.
  Interruptions are VAD-based on purpose (the SDK's adaptive detector needs
  LiveKit Cloud inference).
- SIP requires E.164 phone numbers (`+60…`). One-time trunk setup:
  `npm run setup-trunk` (in `backend/voice_agent`); `npm run dial -- +60…` for
  a manual test call. Config + validation in `voice_agent/src/config.ts`.
- The agent polls the inbox each second: `guide` → `generateReply` (steer),
  `takeover` → `session.say` (verbatim), plus a max-duration failsafe
  (`MAX_CALL_SECONDS`) and auto-hangup after the goodbye.
- **Greeting is gated on the callee answering.** `awaitAnswer()` waits for
  `sip.callStatus == "active"` (SIP) or `wa.callStatus == "active"` (the
  Baileys bridge) before the agent speaks; the gateway joins EARLY (attribute
  "ringing") and gates audio on `this.active` so the agent never hears
  ringback. After the flip, `ANSWER_SETTLE_MS` (default 1500) adds a human beat.
- **Call endings have three layers:** the `end_call` tool; a marker failsafe
  (realtime models sometimes SAY "{end_call}" — agent.ts detects it, scrubs it
  from the transcript AND from what the cascade TTS speaks, hangs up); and a
  contact-silence watchdog (`SILENCE_NUDGE_SECONDS`=12 → re-ask,
  `SILENCE_HANGUP_SECONDS`=120 → forced hangup; only USER speech resets it).
  A `ParticipantDisconnected` handler ends the call when the callee hangs up.
- **Call provenance:** every hangup site records who ended the call
  (`END_CAUSE`, first cause wins → live store `ended_by`: agent / contact /
  operator / system:silence / system:max_duration / system:no_answer /
  system:script_complete / system:tts_unavailable), written with the final live
  write after the LLM summary. An operator's End (`/api/calls/{room}/hangup`
  and WhatsApp `/api/whatsapp/threads/{key}/call/end`) first drops a
  `live/{room}.operator-end` marker (`VoiceAgentBridge.MarkOperatorEnd`) and
  then ends the call, so the disconnect that follows is recorded as
  `operator`, not `contact`.
- **Script calls** (flow "Script call" step) read the `script` metadata key
  verbatim with audio input disabled, then hang up — they need a literal TTS
  voice (a cascade voice), because native realtime models have been observed to
  emit no audible speech when asked to read verbatim.
- **Prompts inject the real date** via `prompts.today()`. The **call language
  comes from the contact** — `languageDirective` opens and conducts the call in
  `contact.language` (`en`/`ms`/`zh`), switching only if the callee clearly
  uses another.
- **Smoke-testing without a phone line:** a local `livekit-server --dev` plus an
  OpenAI-compatible mock as `ILMU_BASE_URL` (voice_provider `ilmu`) exercises a
  full cascade call — dispatch, answer gating, inbox, endings, calls.json.

## Journeys (the flow builder)

- Product name **Journeys** (en) / **Perjalanan** (ms) / **客户旅程** (zh);
  route `/journeys` (full-bleed); `/flow-builder` redirects, query and hash
  kept. Internals keep the flow names (`FlowEngine`, `Workflow`,
  `/api/flows/*`, `flows.view|manage`, `crm_workflow`, the nav key
  `shell.nav.flowBuilder`) — no data migration. Components
  `frontend/src/components/flow/`, data model `frontend/src/lib/flow/`
  (`@xyflow/react`). The library syncs to the DB via `GET/PUT
  /api/flows/library` (GET `flows.view`, PUT `flows.manage`); localStorage is
  only the offline fallback. Both sides use the library rule
  (`ShellScope.ScopeLibrary`: an entity-bound agent reads their subtrees +
  ancestors + global). **"Runs for"** = one or SEVERAL entities, each with its
  branches, or the whole organization: `Workflow` is a multi-entity library kind
  (`LibraryKinds.Workflow` — `EntityId` = the primary + `crm_libraryentity`
  ticks), read by the engine through `FlowEngine.JourneyEntities` /
  `JourneyCompanies` (the audience `EntityContacts`, the booking trigger's scope,
  `Serves`, `RunThread` / `RunCompany`) and `ThreadStore.JobCompany` (a journey
  of several companies sends into the run's conversation's company, else the
  contact's own one among them). Each GET row carries `entities` [{id, slug,
  name}], `global`, `editable` (`flows.manage` AND every entity
  `ShellScope.MayActAt`) and the primary as `entity` / `entity_name`; each PUT row
  may carry `entities` ([] = organization; or the single `entity`, "" =
  organization; absent = unchanged; a new row without one = the save's level,
  else organization for an unbound caller / their first entity for a bound one).
  A bound person never creates or moves a journey to the organization or outside
  their subtrees (403, nothing saved) and never writes or prunes one that reaches
  beyond them (an unchanged copy is skipped, a changed one listed in
  `read_only`); changing a live journey's entities restarts `LiveSince`. The
  editor's header chips + "Change" (`AvailabilityPicker` in a dialog) and the
  list's "Runs for" column; a read-only journey opens view-only (Duplicate makes
  your own). The PUT matches an existing flow by `flowId` across every scope the
  caller reads and updates it IN PLACE. ⚠ **The PUT PRUNES** the rows the same
  GET would have loaded, the caller may write and the payload lacks — a body
  `entity` ("" or a slug, what the MCP flow tools send) saves ONE level and
  prunes only that level's own journeys; after creating a Workflow row outside
  the UI, refresh `/journeys` before its next sync. Tests: `JourneyEntityTests`. `flows.view` without
  `flows.manage` opens `/journeys` READ-ONLY (no New / Import / Go live / rename /
  step edits, and nothing is ever PUT).
- **List first.** `/journeys` opens on the list of every journey
  (`JourneyList.tsx`: trigger summary from `describe.ts`, live / paused, steps,
  people entered in the period + in it now from `/api/analytics/flows`, Open);
  `/journeys?flow=<flowId>` opens the editor (the URL is the selection — refresh
  and browser Back work; an unknown id falls back to the list), and the
  editor's "All journeys" button returns to the list.
- **Two views, one graph.** **Steps** (default, `StepsView.tsx`) reads
  nodes/edges as a tree (`lib/flow/steps.ts buildStepTree`, mirroring the
  engine's edge picking) and edits it with insert / delete / move +
  `autoLayout`; the **Advanced canvas** (`CanvasView.tsx`) is React Flow over
  the same JSON. Plain-language summaries come from ONE place
  (`lib/flow/describe.ts`). New flows start from the template gallery
  (`lib/flow/templates.ts STARTER_TEMPLATES`: welcome series, appointment
  reminder, re-engage, lead qualification, feedback request, lead
  qualification with AI — all paused).
- **The vocabulary is a contract** with `FlowEngine.cs`
  (`lib/flow/nodeConfig.ts` — change both sides together). Triggers: Contact
  created · Tag added · Message received (optional keyword/channel) · Date field
  (days before/on/after a contact DATE CUSTOM FIELD, optionally yearly — not the
  record's due date) · Before a booking (N minutes / hours / days before a
  ChatBranch booking, per booking, follows reschedules, stops on cancel —
  `FlowEngine.Bookings.cs`: `amount` + `unit` + optional `calendar` skill id, its
  own pass in `EvaluateTriggers`, the run's sends go to the booking's chat via
  `trigger_thread_id`; `{{booking_time}}` / `{{booking_date}}` /
  `{{booking_start_time}}` / `{{booking_end_time}}` / `{{booking_length}}` /
  `{{booking_calendar}}` / `{{booking_link}}` / `{{booking_meeting_link}}` in the booking's timezone, empty
  outside such a run) · Scheduled (time + weekdays) · Manual / API
  (`POST /api/flows/{flowId}/start`); repeat Once / Every time / Once a day.
  Legacy record-due-date triggers still execute but are never offered.
  Step kinds: trigger · action (send message on WhatsApp/Email/Telegram/SMS
  (Twilio; sandbox until an SMS inbox exists)/In-app push, or a template · **AI
  message** = the `AI message` marker channel + a `skill` id, composed by
  `ComposeActionTextAsync` and sent through the AI gate (skipped with a note while
  the AI controls say no) · call = AI voice call or Script call, `callVia` `Phone (SIP)` |
  `WhatsApp (Baileys)` · app action) · wait (time, or until they reply — a
  reply that arrives while the run's own message step is still queued is NOT
  an answer and wakes nothing, `FlowEngine.MessageNotSentYet`) ·
  **followup** ("Follow up if no reply": yes = replied, no = no reply; up to 5
  nudges, stops on resolve) · decision (see Decisions below) · handover (to a team; a tracked handover,
  kind `rule`; `aiKeepsAnswering` = "AI keeps answering while the team is
  notified" — on for a new step and the appointment-reminder starter, it leaves the
  chatbot on (`standDown: false`); a step saved without the key stands the AI down
  as before) · update (add/remove tag, set a field, **Set chatbot on/off** =
  op `chatbot`, value on|off, scope all|channels — written through
  `AiEligibility.Set`) · end (outcome code); `stage` is a legacy pass-through. The
  step `skill` picker (`SkillPicker.tsx`) offers PROMPT skills only — tools,
  checks and connected apps run on their own; skills are edited only on `/skills`.
- **Decisions** (`basedOn`). **`Conditions`** — `match` all|any +
  `conditions` [{subject, op, field?, value?, values?, amount?}] over custom
  fields (typed by the field's definition), tags, the contact (language, phone,
  email, entity subtree, opted out), the conversation (status, assigned, team,
  open handover, AI on via `AiEligibility.Decide`, channel, last customer
  message within N h, ≥ N customer messages), the reply (replied, contains
  any/all, said yes/no — EN + BM lists, deterministic on purpose) and bookings;
  `FlowEngine.Conditions.cs`, never parks, an unknown subject/operator matches
  nothing, an empty group is NO. **`AI yes/no`** / **`AI picks a path`** (2–6
  `options` [{id: opt-N, label, description}] + `none`) —
  `Flows/FlowAiDecision.cs` + `FlowEngine.AiDecisions.cs`: history = the
  contact + the last `historyMessages` (20, 5–100) of all channels / the run's
  channel / the triggering conversation (`historyScope`), optionally
  `sinceEntered`, private notes never, + the latest reply; the question and
  options sit in the system prompt with hard rules (classify only, never
  message the customer), customer text is fenced as data; strict JSON,
  temperature 0.1; below `threshold` (0.6) = unsure → `unsure` yes | no | none
  | wait (park on the next reply, `maxAsks` 2, `waitHours` 24, then NO/none) |
  end; the AI master switch off, model errors and unreadable answers take the
  unsure path with a note; no model = a deterministic "sandbox guess"; metered
  as `LlmUsage` `flow_decision`; the verdict ("AI decided YES (0.82): …") is the
  step's completed event, translated in the Runs panel by `lib/flow/runText.ts`.
  The pre-Journeys single checks (They replied, Reply contains, They said yes,
  Has tag, Custom field equals) run unchanged — the server never rewrites them;
  the editor opens them as the equivalent condition and saves the new shape on
  edit ("Contact's answer" / "Record status" stay legacy). Vocabulary:
  `lib/flow/conditions.ts` ↔ `FlowEngine.Conditions.cs`; editor
  `DecisionEditor.tsx`; outcomes from `nodeConfig.branchOutcomes` (Steps view,
  canvas handles, `autoLayout`, `reconcileBranchEdges` when a step's kind or
  options change). Tests: `FlowDecisionTests.cs`.
- **Runs panel** (`RunsPanel.tsx`): totals, people entering per day, reach and
  drop-off per step, failure reasons, and each contact's path — from
  `/api/flows/{flowId}/stats` and paged `/api/flows/runs[/{id}]` (contact-scoped
  like every flows read).
- The variable catalog (`lib/flow/variables.ts`) must stay in step with
  `FlowEngine.VariablesFor` (contact, business, record, memory groups +
  `{{cf_*}}` + `{{tag_*}}`). Styles are token-based in
  `src/app/journeys/journeys.css`, scoped under `.flow-studio`.

## Verifying changes

- **The fast loop (do this, not the whole suite, before a push).** Push → CI →
  deploy is the gate: the deploy waits for a green CI, which runs EVERY API test
  (3 shards × SQLite + PostgreSQL), the dashboard checks + build and the sidecar
  self-tests — so a red push never reaches production. Locally run only what the
  change touches: `npm run test:api:changed` (`scripts/test-changed.mjs`: changed
  test files, test files naming a type declared in a changed API file,
  PermissionCatalogTests for dashboard changes, McpCoverageTests for
  `docs/mcp.md`; the shared wiring — Program.cs, AppDbContext, Seed,
  ScopingService, DatabaseSetup, a .csproj, the test hosts — or most of the
  suite runs it all; `-- --list` shows the pick, `-- --base <ref>` compares with
  another commit) plus `tsc --noEmit` / `eslint` for dashboard changes. Batch
  related fixes into one push, and keep working while CI runs instead of
  waiting on it. The whole suite locally is ~3 min on 4 cores (~5 min on 2).
- **Backend:** `cd backend-csharp && dotnet build src/ChatBranch.Api -c Release`
  then `dotnet test tests/ChatBranch.Tests` (or `npm run test:api` at the root) —
  **1181 tests** covering auth/RBAC (+ API-key scopes inside handlers),
  scoping, the inbox + server-side filters + saved views, the per-conversation scope guard, webhooks (wamid dedup, the
  queued ai_reply), the transports against fake providers (Meta Graph/Send API,
  SMTP/IMAP, the Bot API, LINE / Viber / Twilio / WeChat / TikTok / Douyin /
  Zalo, WhatsApp through YCloud / Twilio — `WhatsAppBspTests` —, media,
  recordings, the health prober, the media payload + signed stream links + M4A rendering —
  `MediaPlaybackTests`), routing + failover + STOP/START,
  contacts (identities, WhatsApp lookups, invites, unknown customers, import),
  tags, templates + inbox settings, flows + step events + stats + decision
  conditions + AI decisions (`FlowDecisionTests`), the outbound
  queue + queued calls, the AI (tool loop, skills runtime/drafter/API, built-ins,
  AI monitor + human-like behaviour, AI controls + handover, calendar booking —
  availability, reserve → make-at-delivery, a fake Google Calendar — retrieval and
  learning), contact access requests (`ContactAccessTests`), integrations (+ My connections, shared/personal scope and
  up-the-tree resolution — `IntegrationScopeTests`), notifications + settings,
  search/live, the platform (audit, API keys, MCP — every route a tool or a
  documented exclusion, `McpCoverageTests` — assistant), the AI playground
  (`AiPlaygroundTests`), background AI tasks (`AiTaskTests`), the Documents / QR / payments / Sheets apps + the calculator
  (`DocumentTests`), the document template designer — normalizer, presets, the
  pre-template golden layout, letterhead, upload-your-design + Ask AI against a fake
  vision model (`DocumentTemplateTests`, `DocumentTemplateApiTests`) —, custom
  document layouts (HTML templates, the sanitizer, the Chromium printer's no-JS /
  no-network guarantees — `DocumentHtmlTests`, `DocumentHtmlApiTests`), AI voice replies + voice-note
  transcription against fake ElevenLabs / OpenAI speech (`VoiceReplyTests`), the Test lab and the LiveKit
  control plane. Keep them green. The test host (`ApiFactory` in
  `ApiSmokeTests.cs`) sets its own env: `QUEUE_WORKER=0`, `FLOWS_WORKER=0`,
  `AI_FOLLOWUP_WORKER=0`, `HEALTH_WORKER=0`, `EMBEDDING_WORKER=0`,
  `IDENTITY_CHECK_WORKER=0`, `AI_TASK_WORKER=0` (drains, probes, indexing, lookups and AI task runs are driven
  explicitly — no timing races),
  `AI_HUMANIZE=0` (sandbox answers inline), `LLM_PRICE_SYNC=0`, `RATE_LIMIT=0` (no request rate
  limits — `RateLimitTests` turns them on for its own host), `PASSWORD_HASH_ITERATIONS=1000` (fast
  hashes for the seeded logins — 600 000 rounds per sign-in saturated a 2-vCPU
  runner; production keeps Django's default), `SEED_DEMO=1` (the suite signs in as
  admin/agent), a throwaway `CHATBRANCH_DATA_DIR` and SQLite file (removed with its `-wal`/`-shm`
  on dispose — leaving those filled the disk), and a LiveKit
  client that is never configured (a developer's `backend/.env` can't place real
  calls). `HEALTH_WORKER=0` keeps the channel-health prober (and post-save
  re-checks) off; `TransportFactory` (`TransportFakes.cs`) swaps in fake
  SMTP/IMAP/Telegram/Graph clients for the transport tests (`MoreChannelsFakes.cs`
  for the newer channels, `LearningFakes.cs` for embeddings).
- **Schema changes** go in `Seed.UpgradeSchema` as well as the entity +
  `AppDbContext` mapping (append-only, idempotent) because `EnsureCreated()`
  never alters a live database (Postgres picks the change up from the model —
  `PostgresSchema.Sync`).
- The suite runs under `BoundedParallelFramework` (tests project): xUnit's parallel
  runner WITHOUT its SynchronizationContext, at most max(4, 2 × cores) test classes
  in flight. xUnit's context owns only `cores` threads, and a test reaching a
  blocking send seam (`.GetAwaiter().GetResult()`) deadlocked a 2-core runner once
  the database did real async I/O (Postgres). Production has no context.
- Many test hosts at once can exhaust the machine's inotify instances
  ("inotify limit (128)"); run with `DOTNET_USE_POLLING_FILE_WATCHER=1` then.
- **Postgres:** `TEST_DATABASE=postgres` + `TEST_POSTGRES="Host=…;Username=…;Password=…"`
  runs every API test host on its own throwaway database (`ApiFactory`, dropped on
  dispose); CI's `C# API on PostgreSQL` job does it on PostgreSQL 16. `PostgresTests`
  (settings, older-database upgrade, date round trip, translators, the import) only
  touch a server in that mode.
- **Frontend:** `cd frontend && npx tsc --noEmit` (clean) and `npx eslint src`;
  `npx next build` for a production check. There is no `frontend/__tests__/`
  (jest is configured, no test files).
- **Voice agent:** `cd backend/voice_agent && npx tsc --noEmit && npm run selftest`
  (offline — metadata, prompts, call-ending cues, file IPC, WAV writer, VAD,
  provider wiring; no LiveKit server or keys). Root: `npm run test:agent`.
- **Baileys gateway:** `node backend/baileys_service/calls.js --selftest`.
- CI (`.github/workflows/ci.yml`) runs on push to master / PRs, built for
  wall-clock time (the deploy waits for it): a **What changed** job diffs against
  the previous commit and the rest runs only where something changed — API
  (`backend-csharp/`, `docs/mcp.md`) = build + tests in **3 shards on SQLite and
  3 on PostgreSQL 16**, six jobs side by side (`TEST_SHARD=i/n` →
  `BoundedParallelFramework.Shard`: the test classes sorted by name and dealt
  out in turn; unset = everything); a dashboard-only change runs just
  PermissionCatalogTests on the API side; dashboard (`frontend/`) = "Dashboard
  checks" (tsc + eslint) beside "Dashboard build" (`NEXT_SKIP_CHECKS=1 next
  build` — `next.config.js` then skips the type-check + lint it would repeat,
  about half the build); the gateway and voice agent self-tests on their own
  folders; a manual run, a new branch, an undiffable push or a change to
  ci.yml itself = everything. NuGet packages are cached. The production compose
  (`docker-compose.override.yml`) builds the web image with
  `NEXT_SKIP_CHECKS=1` too (frontend/Dockerfile `ARG`), since the deploy waits
  for CI; the base compose and release images keep the full checks. Containers: `docker compose up` builds the API
  and the dashboard (the gateway sits behind the `whatsapp` profile, the voice
  agent behind `voice`); the api service gets `BAILEYS_URL=${BAILEYS_URL:-http://baileys:3020}`
  + `BAILEYS_SECRET` (a URL saved in the dashboard still wins), and the baileys
  service (listening on `BAILEYS_HOST=0.0.0.0`) mounts the shared
  `chatbranch-data` volume at `/srv/data` — so its call recordings land where the
  API streams them — with `baileys-auth` mounted over `/srv/data/whatsapp/baileys`
  for the paired sessions; `docker-compose.override.yml` + `deploy/nginx-
  chatbranch.conf` are one production single-origin setup. SQLite is a FILE —
  the API's `/data` volume is not optional, and the voice agent shares it (it also
  holds media, documents and recordings under Postgres).
  Images: the dashboard's is multi-stage — `output: 'standalone'`, the final
  stage carries `.next/standalone` + `.next/static` + `public` and runs
  `node server.js` as `node` (no `src/`, no dev dependencies); the API publishes
  with no `.pdb` (`-p:DebugType=None`); the baileys and agent images run as uid
  1654 (= the API's `app`, one owner for the shared volume) — their
  `docker-entrypoint.sh` starts as root only to hand root-owned paths left by
  older images to that user, then drops to it (`user: "1654:1654"` skips that,
  as `deploy/customer/` does). No obfuscation (reflection / EF / JSON-name heavy).

## Notes

- Dev slowness is normal **Next.js on-demand route compilation** (each route
  compiles on first visit). Use `next dev --turbo` or a prod build for speed.
- **Thread-pool floor.** The send seams (`OutboundSender`, the queue drain, the
  flow engine, the AI gate, the flow AI decision) still block on async provider
  calls (`.GetAwaiter().GetResult()`), so `Program.cs` raises the pool minimum to
  `THREADPOOL_MIN_THREADS` (default 64) at boot — without it a 2-vCPU host (CI,
  a small server) starved and requests stalled past 100 s. A new blocking seam
  makes this worse; prefer async end to end.
- CodeGraph MCP is available (`codegraph_*`) — prefer it for structural queries.
