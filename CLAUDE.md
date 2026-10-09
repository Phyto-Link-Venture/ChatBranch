# CLAUDE.md

Guidance for Claude Code working in this repo. Read before making changes.

## What this is

ChatBranch — an industry-agnostic multi-channel conversation platform
(template). Four cooperating processes:

1. **C# API** (`backend-csharp/`, port 8090) — ASP.NET Core 8 minimal APIs over
   SQLite (EF Core). THE backend: contacts, the unified inbox, flows, the
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

## Port status — what is real, what is sandbox

Every transport is wired in C#; what is left is listed per row. Sandbox =
the message is stored with `status="sandbox"` and never leaves the machine.

| Area | Real | Still missing |
|---|---|---|
| WhatsApp Cloud | inbound webhook (verify token; `X-Hub-Signature-256` **required once any Cloud account is configured**; wamid dedup; Meta's own timestamps; text, media, button/interactive replies, location); **delivery statuses** (sent → delivered → read / failed + `Message.Error`, forward-only; 131047 closes the window; account-level codes → `AccountHealth.SetStatus`, per-recipient codes never — `Channels/MetaErrors.cs`); **text, real TEMPLATE messages** (header/body/URL-button parameters, rendered body stored) and **media** (upload → send by id) via Graph (`OutboundSender`, queue template jobs too); inbound media downloaded off the ack (`MediaFetcher`); live template registry when the account has a `waba_id` (paged list + create/edit/delete, `Channels/MetaTemplates.cs`); account Test = `GET /{phone_number_id}`; typing/read indicator | no Cloud voice calls (`call/start` on a Cloud account → 501) — Meta Business Calling is a gated beta and its WebRTC bridge (Python aiortc) went with Django; not planned until a calling-enabled number exists to build against |
| WhatsApp Baileys | inbound text **+ media** (`/baileys/media` attaches the decrypted file to the `/inbound` placeholder), message-status ticks, lifecycle → health, **text + media send** (media: the gateway's `POST /accounts/{id}/send-media` — base64 in the body, ≤16 MB, never a path/URL, `backend/baileys_service/media.js`; `IBaileysGateway.SendMediaAsync`; the cold-text guard covers files too), presence, **voice calls** (call/start, call-event) + **call recordings** (paired transcript written on `ended`, listed per thread, WAV streamed); **number lookups** for the contact identity checker (`IBaileysGateway.CheckNumberAsync` → the gateway's `/accounts/{id}/check`; never sends a message) | templates go out as rendered text (WhatsApp Web has no template messages) |
| Telegram | inbound webhook `/api/messaging/webhook/telegram/{token}` (token compared in constant time, unknown → 404; `X-Telegram-Bot-Api-Secret-Token` required once registered from the dashboard; text + media via getFile, off the ack; inbound heals health); **send** text/photo/document (`Channels/TelegramClient.cs` `TelegramChannel`; the thread keeps its own bot, a bot-less thread is routed); **typing indicator** (`sendChatAction` during the AI's typing pause — `Ai/TypingIndicator.cs` `TelegramTypingIndicator`, routed by `ChannelTypingIndicator`); Register webhook (setWebhook + secret_token) and Test (getMe) | — |
| Email | account CRUD (masked SMTP + optional IMAP creds); **SMTP send** via MailKit (`Channels/EmailChannel.cs`) as **multipart/alternative** — the text as typed + an escaped HTML rendering (`EmailHtml`: paragraphs, `<br>`, http(s)/email auto-links, never raw HTML; multipart/mixed with attachments); the mailbox is chosen by `Routing.ForThread` like a WhatsApp number (thread's healthy mailbox → sticky assignment → least-loaded auto-assign → failover off disconnected/banned with note + audit + notification, the outlet's own first; all down ⇒ failed, none configured ⇒ default SMTP settings ⇒ sandbox); Message-ID / In-Reply-To / References + "Re: <subject>" from `crm_emailheader`; `Auto-Submitted` on AI mail; **IMAP pull** `POST /api/messaging/email/sync` (`Channels/ImapSync.cs`: 30 s throttle, known-sender filter, \Seen, Message-ID dedup, HTML-only mail flattened to readable text, attachments, normal inbound pipeline); Test = SMTP login (+ test mail) | no HTML compose (the HTML part is rendered from the plain text) |
| Messenger / Instagram | account CRUD (+ `app_secret`, also in the setup drawer); **webhook** `GET/POST /api/messaging/webhook` (WhatsApp's verify token; signature **required** against the known Meta app secrets, none ⇒ 401; routed by page / IG id; mid dedup; CDN attachments; delivery/read receipts; sender name); **Send API** text, media by public URL **and uploaded files** (multipart `filedata`, `IMetaMessagingClient.SendFileAsync`; `POST /api/messaging/threads/{facebook\|instagram}/{key}/send-media`; a caption follows as its own message); **messaging window** (`Channels/SocialWindow.cs`, live pages only: ≤24h anything as `RESPONSE`; 24h–7d a signed-in person only, `MESSAGE_TAG` + `HUMAN_AGENT`; AI / follow-ups / flows / queue / API keys / MCP refused — 409 on the inbox routes, failed job, blocked AI draft; >7d or never wrote → refused; `window_open` / `human_window_open` on the thread payload, the inbox `window` filter and the channel hub; a tagged reply refused for permissions never touches health); **typing indicator** (`mark_seen` + `typing_on` sender actions inside the 24h window, `Ai/TypingIndicator.cs` `MetaTypingIndicator`); account Test = `GET /me` → health | — |
| LINE | **webhook** (`X-Line-Signature`; text, image / video / audio / file downloaded off the ack, sticker / location placeholders; follow / unfollow → opt-in / opt-out; the bot profile names the thread); **send** text through the webhook's reply token while it is fresh (≤50 s, free) else push; Register webhook + Test (`/v2/bot/info`) | outbound media (LINE takes images by public URL only — refused honestly); typing indicator |
| Viber | **webhook** (`X-Viber-Content-Signature`; text + picture / video / file from Viber's CDN; subscribed / unsubscribed → opt-in / opt-out; `conversation_started` context rides on the first message as `[ref:…]`; delivered / seen / failed ticks); **send** text (`send_message`); Register webhook (`set_webhook`) + Test (`get_account_info`) | outbound media (public URL only) |
| SMS (Twilio) | **webhook** (`X-Twilio-Signature`; MMS media fetched with Basic auth off the ack; Twilio's own STOP/START `OptOutType` mirrored); **send** text ≤1600 chars from the inbox number or a Messaging Service (`TwilioSmsTransport`); phone-keyed — contacts link by last-8 digits like WhatsApp, numbers spread like mailboxes; error 21610 opts the conversation out; Test = the Twilio account (suspended / closed → health); flow SMS steps send through it | outbound MMS (needs a public media URL); delivery status callbacks |
| WeChat OA | **server URL** `GET` (echostr verification) + `POST` (sha1 signature, 5-min freshness, single-use nonces; plaintext or AES safe mode; text, image, voice, video, location, link; subscribe / unsubscribe → opt-in / opt-out; a parametric QR's scene rides as `[ref:…]`; media via `media/get`); **send** Customer Service text + images (temporary-media upload) inside a **48 h** window (errcode 45015 = the same refusal); access_token cached ~7000 s; Test = the token fetch | passive (in-response) replies |
| TikTok ⚠ | partner-gated; built to the documented shapes, **unverified against the live API**: webhook (`TikTok-Signature t=…,s=…`, 5-min replay window), send text inside a **48 h** window, Test = `business/get` | media (in and out) |
| Douyin ⚠ | approval-gated; **unverified against the live API**: webhook (`X-Douyin-Signature`, `verify_webhook` challenge, `im_receive_msg`), reply text quoting the customer's latest message inside a **48 h** window, Test = the client-credential token | media; access-token refresh |
| Zalo OA | **webhook** (`X-ZEvent-Signature` mac; text, image, file, sticker, link, location; follow / unfollow → opt-in / opt-out; OA echoes ignored); **send** consultation text + images (upload → media template) inside a **7-day** window (Zalo's -230 = the same refusal); an expired access token is refreshed once with the rotating refresh token; Test = `getoa` | — |
| Website widget | full (internal transport) | — |
| SIP voice | dial / live / say / hangup / escalate through LiveKit | — |
| Media serving | `/api/whatsapp/media/{id}` + `/api/messaging/media/{id}` (`chats.view` + the conversation's entity scope, Range, non-renderable types as downloads; files under `data/{whatsapp,messaging}/media/`, `Channels/WhatsAppMediaStore.cs`), `/api/whatsapp/calls/recording/{name}` (gateway WAV or the agent's), `/api/calls/recording/{call_id}` (SIP) | — |
| Channel health | writer `AccountHealth.SetStatus` (one audit row + an admin `account_health` bell per problem/recovery transition); periodic prober `Workers/HealthProbeWorker` (`WHATSAPP_HEALTH_INTERVAL` s, default 300; `HEALTH_WORKER=0` off) via `Channels/HealthProber.cs`: Cloud Graph ping, Telegram getMe + getWebhookInfo, SMTP login, Messenger/IG `GET /me`, the newer channels' own probes (`IChannelTransport.ProbeAsync`), default SMTP → `email_health`; Meta send / status / test results classified by `Channels/MetaErrors.cs`; "Check now" runs the prober for the caller's scope; saving credentials resets to unknown + re-probes in the background | SIP voice stays config-presence only (by design) |
| Routing | `Channels/Routing.cs` at SEND time (queue drain, manual sends, email mailbox + Telegram bot choice, and the AI gate — instant / approved / auto-sent AI replies, follow-ups): thread's healthy number → sticky `AccountAssignment` → least-loaded auto-assign → failover off banned/disconnected (pin cleared, thread moved, note + audit + notification); roles/providers/templates are WhatsApp-only (`RouteNeed.For`; templates → Cloud, never Baileys; 409 when none); account-bound channels (Telegram, Messenger/IG, widget, LINE, Viber, WeChat, TikTok, Douyin, Zalo) keep their own account, email and SMS spread even contactless threads; inbound re-attribution; `business_redirect` (`Channels/BusinessRedirect.cs`); recovery one-click + auto (`Channels/Recovery.cs`, hooked in `AccountHealth.SetStatus`) | — |
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
  over SQLite with **Django's table names** (`crm_*`, `ingest_*`, `auth_user`,
  `integrations_integration`) and snake_case columns. There are no EF
  migrations: `EnsureCreated()` builds a fresh DB and `Seed.UpgradeSchema`
  (append-only, idempotent `ADD COLUMN` / `CREATE TABLE IF NOT EXISTS`) upgrades
  an old one — a new column or table MUST be added there too. Entities live in
  `ChatBranch.Domain/`. Field reference: `docs/DATABASE_SCHEMA.md`.
  **Write messages through `Stores/ThreadStore`**: `GetOrCreateThread` adopts
  the receiving account / matched contact and re-syncs the thread's entity;
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
- **Entity scoping = global default → entity override.**
  `ChatBranch.Data/ScopingService.cs` is the ONE place the rule lives: *an
  entity-specific binding wins; the global one is the fallback*. Three verbs,
  and picking the wrong one is the bug the class exists to prevent:
  `SettingValue(key, entityId)` (config: entity row → global row → ""),
  `ScopeInherited(q, entityId)` (libraries an outlet INHERITS — knowledge,
  canned/script templates, skills, workflows, tags, teams, channel accounts,
  integrations: its own PLUS the global ones) and `ScopeOwned(q, entityId)`
  (records that belong to exactly ONE outlet — contacts, threads, AI drafts,
  analytics). `entityId == null` means "All entities" for the Scope* helpers and
  "the global value" for the setting helpers.
  **`Setting` is not key-unique**: a nullable `EntityId` with two partial unique
  indexes (one global row per key; one override per `(key, entity)`), so a raw
  read MUST say `EntityId == null` or another outlet's override leaks into the
  org-wide value. Only keys in `ScopingService.EntityScopedSettings` may be
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
  the catalog is `AdminEndpoints.Catalog` (34 entries — keep it in step with
  `CAPABILITY_CATALOG` in `frontend/src/lib/auth.ts`); **superadmin is always
  allowed in code**, a missing matrix row falls back to the code default, admin
  gets everything except `rbac.manage` / `app_keys.manage` /
  `settings.ai_keys.manage`, agent gets `contacts.view chats.view chats.send
  flows.view analytics.view call_logs.view activity.view tags.view ai.monitor`.
  Server checks use `AdminEndpoints.HasCapabilityAsync` /
  `SettingsEndpoints.RequireCapability` on top of the coarse policies
  (`authenticated` / `admin+` / `superadmin`). **There is no fallback policy:
  every route must carry `RequireAuthorization(...)` or an explicit
  `AllowAnonymous()`** — the anonymous set is listed in
  `docs/API_CONVENTIONS.md`. User management (`/api/v1/users`) is tiered:
  superadmin manages everyone; admin lists their own tenant and manages only
  agents there. A login may be bound to a Contact (`Profile.ContactId`,
  "customer login"). The `X-Role` header only works while NO Profile rows exist
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
  conversation to another number stays an admin's per-chat switch). Linking needs a contact in
  the caller's scope, the contact lookups / timeline are scoped, both thread
  lists need `chats.view` (details in `docs/RBAC_AND_BRANDS.md`). Frontend gating
  (`lib/useAccess.ts`, `nav.ts canSee`) is not the control — the server is — but
  `components/shell/RouteGuard.tsx` renders a "no access" panel instead of any
  page whose sidebar entry (`nav.ts routeDef`, detail routes + `alsoActive`
  included) the caller can't see, so the page never mounts or fetches; nothing
  renders until the session is read, and someone without Analytics lands on
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
  on per-conversation routes, or a role policy. A lookup shared by several pages
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
- **The unified inbox.** Every channel is a `Thread` (unique `(channel, key)`)
  + `Message` rows. `/chats` is a three-pane inbox → chat → info panel; its list
  is SERVER-filtered by `GET /api/inbox/threads`
  (`Endpoints/InboxThreadsEndpoints.cs` — status, channel, account, assignee,
  team, entity, labels, contact tags, AI on/off, 24h window, opt-out, waiting,
  live call, linked, handover (`open`/`any`/`none` + `handover_kind`), date
  ranges, sorts, cursor paging; `count_only=1` powers the filter pop-up's live
  "Show N chats"). The filter UI is `ChatFilterModal.tsx`; the filter tabs are
  per-user `SavedView` rows (`/api/views?scope=chats`, `lib/savedViews.ts`) next
  to a built-in "Needs human" tab. **Select mode** (`BulkBar.tsx`) drives `POST
  /api/inbox/threads/bulk` (`chats.manage`; picked conversations or everything a
  list filter matches, scoped like the list, ≤1000, one audit row; `ai_on` |
  `ai_off` | `resolve` | `reopen` | `assign`). Messaging payloads carry `pk` (the
  message id) so AI bubbles get 👍/👎 (`components/ai-learning/BubbleFeedback.tsx`,
  see AI learning). Deep links: `/chats?thread=<channel>:<key>`,
  `/chats?q=<text>`. The page does NOT poll — `lib/chatStream.ts` holds ONE SSE
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
  theirs = a hairline card) — no WhatsApp greens. `POST /api/whatsapp/simulate`
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
  onto the RECEIVING number → the inbox's AI default on a brand-new thread → store
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
  it may throw into a webhook.
- **WhatsApp specifics.** Accounts are `ChannelAccount` rows (provider `cloud`
  or `baileys`); `WHATSAPP_*` env keys surface as a read-only `env-default`
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
  the **master switch / schedule** (Setting `ai_enabled` on | off | schedule;
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
  `chats.view`; PUT `ai.behaviour.manage`, org-wide admin+, an outlet must be in
  the caller's subtree), `GET/PUT /api/contacts/{id}/ai-mode` (PUT
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
  /api/whatsapp/threads/{key}/account` (admin+) pins the assignment and appends
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
  an outlet's own numbers (nearest level of the tree), else the global ones.
- **Channel health.** `Channels/AccountHealth.SetStatus` is the ONLY writer of
  `ChannelAccount.HealthStatus` (unknown/healthy/degraded/disconnected/banned):
  one `account.health` AuditLog row per real transition (detail
  `"prev → next — reason"`, which the Analytics health card parses). Writers
  today: Baileys lifecycle pushes (`/api/whatsapp/baileys/status-event`),
  inbound traffic (heals), email/Telegram send outcomes and the periodic
  prober (`Channels/HealthProber.cs`, `HealthRecorder` decides the verdict —
  per-recipient failures never touch an account). Saving new credentials
  resets an account to `unknown` silently and re-probes it (`HealthRecheck`).
  `GET /api/whatsapp/channels/health` feeds the Analytics channel-health card;
  SIP voice health is config-presence only (`/api/whatsapp/voice-settings`, PUT
  superadmin, masked round-trip, Setting `voice_twilio_settings` with
  `TWILIO_*` env fallback).
- **Website widget** (`/widget.js` + `/api/widget/{token}/*`) — the one channel
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
  (`api_keys.manage`; token shown once, SHA-256 stored). **`POST /mcp`** =
  ChatBranch as an MCP server (Streamable HTTP, JSON-RPC, stateless;
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
  channel-agnostic generator: system prompt = `PersonaService.HardRules` (safety
  floor, never overridable) + persona (Setting `aicore_persona`, entity-scoped,
  else `DefaultPersona`; `GET/PUT /api/aicore/persona`, PUT superadmin — there
  is no persona UI) + SKILLS block (prompt skills in scope, plus how to use each
  connected-app skill's tools) + `COMPANY KNOWLEDGE` (`KnowledgeService.Compose`:
  kind fact/policy/faq/event/notice, `StartsAt/EndsAt` self-expiry; STANDING
  knowledge first — live notices, events and short typed facts ≤800 chars — then
  faq/policy items and chunks of long items / uploaded documents RETRIEVED for
  the customer's latest messages by `Ai/Learning/RetrievalService` (BM25 with EN +
  BM stopwords and light stemming; plus embeddings when a provider is configured —
  0.7·cosine + 0.3·BM25, a provider failure falls back to keywords; keyword-only
  is the zero-config path); zero hits add nothing; 1800-char budget; with no
  query (voice dispatch) the old top-4 faq/policy by priority) + `LEARNED FROM
  PAST CONVERSATIONS` (active lessons — rules first, at most 5, then the top 3
  relevant examples/corrections, 1200 chars, the conversation's own entity +
  org-wide only; chat replies only, see AI learning) + `CONTACT` (name, company,
  language, tag NAMES, profile summary — not custom fields, tag attributes or
  records) + `CONTACT MEMORY` (durable contact fields + the last 3 summaries,
  1500 chars) + the current org-local date/time. It is a **PURE GENERATOR
  returning `AiReplyResult`** (text, draft, tools, checks, retries, tokens,
  `Grounding` = what the reply was based on, `PendingActions` = calendar
  reservations) — callers send. Every path is exception-safe. LLM:
  `OpenAiLlmClient` speaks OpenAI-compatible `/chat/completions` to openrouter,
  openai, **ilmu** (`ILMU_BASE_URL`, default `https://api.ilmu.ai/v1`,
  `ilmu-v3.1`), **google** (Gemini's `…/v1beta/openai`, `gemini-2.5-flash`) and
  local; keys from Setting rows (`openrouter_api_key`, `openai_api_key`,
  `ilmu_api_key`, `google_api_key`, `local_base_url`, `chat_model` — env
  `WHATSAPP_CHAT_MODEL`) with process-env fallback. A pin (`chat_provider_pref`,
  mirrored to `chat_provider`) is used ALONE (failure → sandbox, never another
  vendor); else `chat_provider_order` is tried in order, an error falling through
  to the next (`chat_model` applies to the first only). Edited at Settings → AI
  keys & models (`/api/settings/ai-providers`, `settings.ai_keys.manage` — it
  also holds `embedding_provider` / `embedding_model`, `EmbeddingsSection.tsx`);
  nothing configured → `SandboxLlmClient`. Usage is
  metered into `LlmUsage` at every call site; costs are estimated at READ time
  from Setting `llm_prices` (`AnalyticsService`). Case summaries
  (`SummaryService`, `/api/messaging/threads/{channel}/{key}/summary`) are
  on-demand, persisted as `ConversationSummary`, heuristic without an LLM.
  Knowledge API: `/api/aicore/knowledge` (+ `/preview` `{query}` = the exact
  knowledge + learned blocks the AI receives plus every scored hit — the
  "Test retrieval" view, `RetrievalTester.tsx`; `/upload` = a PDF / .txt / .md /
  .csv ≤10 MB → one item with `source_file`, text layer via PdfPig, image-only
  PDFs refused, `KnowledgeUpload.tsx`), page `/knowledge`.
- **AI skills (`/skills`).** `AgentSkill.Kind` = **prompt** (`use_when` +
  `instructions` in the SKILLS block) | **tool** (JS `run(args, ctx)` the model
  CALLS) | **check** (JS `check(reply, ctx)` run on EVERY draft) | **app** (a
  "Connected app": `AgentSkill.Config`, snake_case JSON, today only `app:
  "calendar"` — see Calendar booking; the form is
  `components/skills/AppSkillForm.tsx`). JS runs in
  `Ai/Skills/SkillRuntime.cs` (Jint: fresh engine per run, no CLR interop, no
  eval, 250 ms / 8 MB / statement + recursion caps; faults come back as data).
  Built-in C# tools are always offered (`BuiltinSkills.cs`: current_time,
  current_week, date_add, days_between, convert_timezone; zone = Setting
  `org_timezone` → TZ → UTC). A failing check → rewrite with the reason (max 2),
  then `OnFail`: retry→handover, handover (immediate) or block; handover = a
  tracked `check_failed` handover (see Human handover). **`GET/PUT
  /api/aicore/skills` is the ONLY writer of `AgentSkill`** (PUT
  `settings.ai.manage`, prunes within its own scope, refuses an ENABLED code
  skill whose code/tests changed unless its tests pass, and refuses an invalid
  app config — `Bookings/CalendarAppConfig.cs`; an app row sent without `config`
  keeps the stored one); the flow library PUT ignores the `agentSkills` it
  receives. Authoring: `POST /api/aicore/skills/draft` (describe → up to 4
  clarifying questions → draft; sandbox = a deterministic template library),
  `/skills/test` (`{kind:"app", config, app_action:"availability"}` is a DRY RUN
  that books nothing), `GET /skills/builtins`, `GET /skills/apps` (the
  connected-app catalog: sources with the Google Calendar connections the CALLER
  may see, actions, defaults); the wizard is `components/skills/SkillWizard.tsx`.
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
  step grid; at most 8 slots spread across the days. A slot is RESERVED while
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
  Every outcome writes audit `booking.created|cancelled|failed` (+
  `booking.unconfirmed` when the confirmation text is refused after booking), a
  "📅" private note and a notification (assignee, else admins; RefKey
  `booking:<id>`). The SKILLS block tells the model to offer 2–3 times, wait,
  book, then restate weekday/date/time/timezone; the voice SKILLS block omits app
  skills. Pages: `/bookings` (Work; `GET /api/bookings`, `chats.view`, scoped
  like conversations; `POST /api/bookings/{id}/cancel`, `chats.send`), the
  `BookingsCard` on the contact and the chat info panel. The older "Booking
  hours" panel (`BookingHoursSettings.tsx` on the Google Calendar card at
  `/integrations`, `/api/aicore/booking-hours`, entity-scoped Setting
  `booking_hours`) is stored and edited but READ BY NOTHING in the C# port —
  booking skills carry their own hours and the flow "Find free slot" action
  searches free/busy without working hours. Tests: `BookingTests.cs`.
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
- **Human handover** (`Ai/Handover.cs`, the ONLY writer of `crm_thread.handover_*`).
  `Handover.Mark(db, thread, kind, reason, by)`: kinds `check_failed` ·
  `asked_for_human` · `send_blocked` · `booking_failed` · `low_confidence` ·
  `rule` · `manual` · `unknown_customer`; `by` = `ai` · `rule:<id>` · `flow` ·
  `agent:<username>` (· `identity`). It counts once while open, sets the chat
  pending, adds the private note, notifies the owners, audits `ai.handover` and
  (default `standDown`) turns the chatbot off — the identity exchange's
  `unknown_customer` handover leaves the chatbot as it was, and a person linking
  or creating the contact through the identity banner (`POST
  /api/contacts/identify/{channel}/{key}/contact` →
  `IdentityRequestService.ResolveByHand`) clears it — the plain `…/link` route
  does not. Sites: the AI gate (refused drafts, "a
  person will help you" replies, send-time blocks, booking failures), the
  assistant's handover outcome, the flow Hand-over step, the automation
  `hand_over` action, the identity exchange and `POST
  /api/inbox/threads/{channel}/{key}/handover` `{take|continue|flag}`
  (`chats.send`). A person's reply (the human send endpoints — not API keys or
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
  `/api/ai/lessons`, writes `ai.behaviour.manage`, delete admin+): `example` |
  `correction` | `rule`; entity-scoped Setting `ai_learning_mode` = review
  (default — every lesson waits for a person) | auto (an admin's feedback goes
  live at once) | off (counted, nothing learned); a near-identical suggestion
  MERGES (normalized text at once, cosine ≥0.95 in the indexer; the latest
  correction wins and sends a live lesson back to review unless an admin gave it
  in auto mode); `/{id}/to-knowledge` turns one into a FAQ (a rule → a policy).
  Lessons are DATA: `HardRules` win and they never cross entities.
  **Retrieval** (`RetrievalService`):
  `embedding_provider` openai | google | local, "" = the first configured, `none`
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
  Analytics (`/analytics`, the HOME page):
  `GET /api/analytics/summary?days=` (funnel, per-account volumes, call
  outcomes, health events, daily series, KPI deltas),
  `GET /api/analytics/usage?days=` (LLM metering/billing, admin+),
  `/api/analytics/reports`, `/api/analytics/flows`, `/api/live` (waiting
  conversations + active calls + `needs_human_count`).
- **Integrations** (`/integrations`, `/api/integrations/*`,
  `ChatBranch.Data/Integrations/`). `Integration` is `ChannelAccount`'s twin:
  creds in a JSON column that **never leaves the server unmasked**, `Config` for
  per-provider settings. Providers: Google Calendar / Drive / Gmail / Sheets /
  Contacts (read-only `contacts.readonly` — the caller's own address book for the
  contact import) (`GoogleOAuth.cs`: one Google client, incremental per-product
  scopes, PKCE S256, HMAC-signed 600 s `state`, lazy refresh that marks a row
  disconnected and returns "" rather than throwing) and Shopify / Stripe (API
  keys). Google Calendar HTTP (free/busy, insert / delete an event) is ONE
  client, `Integrations/GoogleCalendarClient.cs`, shared by the flow actions and
  the AI's booking tools. **Personal, entity or organization — every payload
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
  (admin+ with `integrations.manage`; `IntegrationResolver.SharedRowAsync`,
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
  deliberately ANONYMOUS (the signed state IS the CSRF control); set
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
  `docs/record-sync.md` (`/api/v1/sync/records`) · `docs/channel-adapter.md`
  (per-channel capabilities, port gaps, how to add one) ·
  `backend/voice_agent/README.md` · `backend/baileys_service/README.md` +
  `RESEARCH.md` (Baileys ban evidence, the pacing numbers the queue defaults
  come from). Files marked "historical" at the top (`CSHARP_*`, `UPGRADE-SPEC*`,
  `*-PLAN.md`, `reschedule-flow.md`) describe how the code got here, not how it
  is.

## Frontend conventions

- **Shell (`components/shell/`).** The sidebar IA lives in `nav.ts`
  (`NAV_GROUPS`: Analytics + Notifications, then Work (Chats · Contacts ·
  Bookings · Account assignment) · Automate · Library · Channels · Admin ·
  Review; Settings is the footer link) — **a new page = one entry there**, which
  also gates it: `RouteGuard.tsx` shows a "no access" panel instead of a page
  whose entry (`routeDef`) the caller can't see. Groups are collapsible and the
  whole rail collapses (`nav-collapsed`). Items are gated by `cap` (a string, or
  an array = ANY of them) with a `roles` fallback for sessions without a
  capability list (`canSee`). **Analytics is home** (`HOME = '/analytics'`; `/`,
  post-login and `/dashboard` redirect there — there is no Dashboard page;
  someone without `analytics.view` is sent to `firstVisibleHref`). Ctrl/⌘+K opens the
  command palette (`CommandPalette.tsx`): the visible nav pages plus
  `GET /api/search` (contacts, conversations, templates, knowledge, flows, users,
  entities — each type gated by the capability of the page it opens and scoped
  like that page's list endpoint). The top bar's **Live chip** (`LiveChip.tsx`)
  shows the SSE connection status dot + "N calls · N waiting" from `/api/live`
  (`liveStore.ts`: refreshed every 15 s AND on every stream `change`, keyed by
  entity; also feeds the sidebar's Chats counter and Analytics' waiting card);
  its popover carries the AI quick toggle (`components/ai-controls/AiQuickToggle.tsx`).
  Below `lg` the rail becomes a drawer and a **bottom tab bar** (Home · Chats ·
  Contacts · More) takes over. `Shell.FULL_BLEED` (`/chats`, `/journeys`,
  `/settings`) render edge-to-edge and own their height; `PUBLIC_ROUTES`
  (`/help`, `/offline`) and `/login` render bare. **PWA:** `app/manifest.ts`,
  hand-written `public/sw.js` (never caches `/api/*` or the stream; offline
  navigations land on `/offline?from=`), icons in `public/icons/` rendered from
  `Logo.tsx`'s `brandMarkSvg`. **Ask ChatBranch** (`AssistantDock`) opens from
  the top bar's sparkle button or Ctrl/⌘+J — the shell owns its `open` state;
  nothing floats over the page (the old bottom-right pill covered composers and
  save bars, don't bring it back); its chats live on the server (History
  view, per user); the browser keeps only `assistant:<username>:current` (open
  chat id + draft, `lib/assistantCache.ts`), and `clearAuth()` (sign-out, 401
  expiry) and every login remove every `assistant:*` key.
- **`src/lib/AppContext.tsx`** holds shared state + the real-call seams. API
  base = `NEXT_PUBLIC_API_URL` (default `http://localhost:8090/api` in
  `next.config.js` and `frontend/.env.example` — no longer the Django-era 8000;
  baked in at BUILD time). `src/lib/mocks.ts` is the type source (`Contact`, `RecordDto`,
  `TranscriptLine`, …) — its data arrays are EMPTY since the production-clean
  pass; only the voice presets remain. It was once lost to a `.gitignore`
  `lib/` rule — `.gitignore` explicitly un-ignores `frontend/src/lib/`.
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
  and pronunciation — not the chat persona.
- **Settings is system configuration ONLY**, organised as SECTIONS of flat
  `/settings/<id>` pages, each with its own capability
  (`components/settings/SettingsNav.tsx` `SETTINGS_SECTIONS`, checked again
  server-side): Personal — Appearance (incl. Language), My notifications, My connections (no
  cap; `/settings/connections` connects your own Google products, see
  Integrations); Organization — Notification defaults (`notifications.manage`),
  Custom fields (`settings.custom_fields.manage`), App & provider keys
  (`app_keys.manage`); AI — AI keys & models (`settings.ai_keys.manage`; also
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
- **Localisation (`src/i18n/`).** English, Bahasa Melayu, 简体中文 (`locales.ts`;
  keep in step with `SettingsEndpoints.SupportedLocales`). Strings live in
  `messages/<locale>/<namespace>.ts` — one file per area (`common`, `shell`,
  `ui`, `chats`, `contacts`, `flows`, `ai`, `admin`, `settings`, `channels`…);
  English is the SOURCE and `Dict<typeof source>` makes every other locale carry
  exactly its keys, so a missing translation is a type error. **No user-visible
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
- **Contacts:** `/contacts` is a list (the kanban board is gone) with a 4-step
  **import wizard** (`components/contacts/ContactImportModal.tsx`: a .csv / .xlsx
  / .vcf file or your own Google Contacts → map columns → duplicates skip / fill
  empty / overwrite + entity, tags, find channels → import, skipped rows as a
  CSV; 20 000 rows / 10 MB; phones normalized to E.164 with Setting
  `default_country_code`, default 60; `POST /api/contacts/import`, admin+ and
  `contacts.manage`); `/contacts/[id]` shows the AI summary
  (`/api/messaging/timeline/summary`), details + custom fields, records, the
  channel hub, entities (`ContactEntitiesCard`), tags (`ContactTagsCard`),
  bookings (`BookingsCard`), "AI for this contact" (`ContactAiModeCard`) and the
  communications timeline. In `/chats`, an unlinked conversation shows
  `IdentityBanner.tsx` (the identity exchange's state) with a "Create contact"
  quick action (`POST /api/contacts/identify/{channel}/{key}/contact`).

### Design system — Paper & Ink (light/dark)

Design canvas: https://claude.ai/artifact/7dcX3rLGAH3zXZeKu61Pdb

- **Three switchable themes × light/dark**, all CSS variables on `<html>`
  defined in `src/app/globals.css`: `theme-paper` = **Paper & Ink** (default:
  warm paper, ink type, Fraunces serif headings, ONE tangerine accent),
  `theme-graphite` = Graphite Pro (neutral, violet accent, compact),
  `theme-soft` = Soft Blocks (pastel, round). Mode = `.light` / `.dark` class
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
  TANGERINE (not emerald, not blue). On Paper & Ink the primary action is INK;
  on the other two it is the accent.
- **Appearance prefs** (`lib/theme.ts`): theme, mode (light/dark/system),
  accent override (with **contrast correction** — `accentVars` walks lightness
  until text inks reach 4.5:1 and fills 3:1 against the theme's ground), text
  size (`--ui-zoom`: the shell applies `zoom` and divides its height by it),
  corner radius (`--r-md…--r-3xl`), heading font (`--font-display`).
  `applyAppearance` sets the classes + inline vars and caches the resolved set
  in localStorage (`ui-appearance`); the pre-paint script
  (`lib/themeScript.ts PREPAINT_SCRIPT`, inlined by `app/layout.tsx`, whose
  `<html className="theme-paper light" suppressHydrationWarning>` swap is an
  intentional pre-hydration mutation) re-applies it before first paint.
  Persistence: the user's own choice at `GET/PUT /api/auth/preferences`
  (`Profile.Preferences`), the organization default at
  `GET/PUT /api/settings/appearance` (`settings.appearance.org`); the shell
  syncs once per sign-in. UI: Settings → Appearance.
- **Typography helpers:** `.display` (the theme's display family/weight/
  tracking — page titles) and `.eyebrow` (the small uppercase label above a
  title). `tint(color, alpha)` (`lib/color.ts`) makes a translucent version of
  ANY colour, including a `var()` token, via `color-mix` — use it instead of the
  old `hex + '1F'` idiom.
- **Shared components (`components/ui/`) — use them, don't re-invent:**
  `Select` / `MultiSelect` (search, keyboard, groups, tree indent, portalled
  list), `EntityPicker` / `EntityMultiPicker` + `entityTree` /
  `entityOptions`, `BindingGrid` + `SaveBar`, `ChannelLogo` (+ `channelLabel`,
  `channelColor` — real brand marks for channels and integrations; the only
  place brand hexes like WhatsApp green are allowed), `PageHeader`, `Toggle`,
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
  only the offline fallback. ⚠ **The PUT PRUNES** server-side workflows (in its
  scope) missing from the browser's copy — after creating a Workflow row
  outside the UI, refresh `/journeys` before its next sync.
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
  record's due date) · Scheduled (time + weekdays) · Manual / API
  (`POST /api/flows/{flowId}/start`); repeat Once / Every time / Once a day.
  Legacy record-due-date triggers still execute but are never offered.
  Step kinds: trigger · action (send message on WhatsApp/Email/Telegram/SMS
  (Twilio; sandbox until an SMS inbox exists)/In-app push, or a template · **AI
  message** = the `AI message` marker channel + a `skill` id, composed by
  `ComposeActionTextAsync` and sent through the AI gate (skipped with a note while
  the AI controls say no) · call = AI voice call or Script call, `callVia` `Phone (SIP)` |
  `WhatsApp (Baileys)` · app action) · wait (time, or until they reply) ·
  **followup** ("Follow up if no reply": yes = replied, no = no reply; up to 5
  nudges, stops on resolve) · decision (see Decisions below) · handover (to a team; a tracked handover,
  kind `rule`) · update (add/remove tag, set a field, **Set chatbot on/off** =
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

- **Backend:** `cd backend-csharp && dotnet build src/ChatBranch.Api -c Release`
  then `dotnet test tests/ChatBranch.Tests` (or `npm run test:api` at the root) —
  **654 tests** covering auth/RBAC (+ API-key scopes inside handlers),
  scoping, the inbox + server-side filters + saved views, the per-conversation scope guard, webhooks (wamid dedup, the
  queued ai_reply), the transports against fake providers (Meta Graph/Send API,
  SMTP/IMAP, the Bot API, LINE / Viber / Twilio / WeChat / TikTok / Douyin /
  Zalo, media, recordings, the health prober), routing + failover + STOP/START,
  contacts (identities, WhatsApp lookups, invites, unknown customers, import),
  tags, templates + inbox settings, flows + step events + stats + decision
  conditions + AI decisions (`FlowDecisionTests`), the outbound
  queue + queued calls, the AI (tool loop, skills runtime/drafter/API, built-ins,
  AI monitor + human-like behaviour, AI controls + handover, calendar booking —
  availability, reserve → make-at-delivery, a fake Google Calendar — retrieval and
  learning), integrations (+ My connections, shared/personal scope and
  up-the-tree resolution — `IntegrationScopeTests`), notifications + settings,
  search/live, the platform (audit, API keys, MCP — every route a tool or a
  documented exclusion, `McpCoverageTests` — assistant), the AI playground
  (`AiPlaygroundTests`), the Test lab and the LiveKit
  control plane. Keep them green. The test host (`ApiFactory` in
  `ApiSmokeTests.cs`) sets its own env: `QUEUE_WORKER=0`, `FLOWS_WORKER=0`,
  `AI_FOLLOWUP_WORKER=0`, `HEALTH_WORKER=0`, `EMBEDDING_WORKER=0`,
  `IDENTITY_CHECK_WORKER=0` (drains, probes, indexing and lookups are driven
  explicitly — no timing races),
  `AI_HUMANIZE=0` (sandbox answers inline), `SEED_DEMO=1` (the suite signs in as
  admin/agent), a throwaway `CHATBRANCH_DATA_DIR` and SQLite file, and a LiveKit
  client that is never configured (a developer's `backend/.env` can't place real
  calls). `HEALTH_WORKER=0` keeps the channel-health prober (and post-save
  re-checks) off; `TransportFactory` (`TransportFakes.cs`) swaps in fake
  SMTP/IMAP/Telegram/Graph clients for the transport tests (`MoreChannelsFakes.cs`
  for the newer channels, `LearningFakes.cs` for embeddings).
- **Schema changes** go in `Seed.UpgradeSchema` as well as the entity +
  `AppDbContext` mapping (append-only, idempotent) because `EnsureCreated()`
  never alters a live database.
- **Frontend:** `cd frontend && npx tsc --noEmit` (clean) and `npx eslint src`;
  `npx next build` for a production check. There is no `frontend/__tests__/`
  (jest is configured, no test files).
- **Voice agent:** `cd backend/voice_agent && npx tsc --noEmit && npm run selftest`
  (offline — metadata, prompts, call-ending cues, file IPC, WAV writer, VAD,
  provider wiring; no LiveKit server or keys). Root: `npm run test:agent`.
- **Baileys gateway:** `node backend/baileys_service/calls.js --selftest`.
- CI (`.github/workflows/ci.yml`) runs all four on push to master / PRs: API
  build + tests, dashboard types + lint + build, the gateway self-test, the
  voice agent's types + self-test. Containers: `docker compose up` builds the API
  and the dashboard (the gateway sits behind the `whatsapp` profile, the voice
  agent behind `voice`); the api service gets `BAILEYS_URL=${BAILEYS_URL:-http://baileys:3020}`
  + `BAILEYS_SECRET` (a URL saved in the dashboard still wins), and the baileys
  service (listening on `BAILEYS_HOST=0.0.0.0`) mounts the shared
  `chatbranch-data` volume at `/srv/data` — so its call recordings land where the
  API streams them — with `baileys-auth` mounted over `/srv/data/whatsapp/baileys`
  for the paired sessions; `docker-compose.override.yml` + `deploy/nginx-
  chatbranch.conf` are one production single-origin setup. SQLite is a FILE —
  the API's `/data` volume is not optional, and the voice agent shares it.

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
