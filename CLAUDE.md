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
  voice agent and the Baileys gateway load `backend/.env` themselves; docker
  compose passes it to every service.
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
| WhatsApp Baileys | inbound text **+ media** (`/baileys/media` attaches the decrypted file to the `/inbound` placeholder), message-status ticks, lifecycle → health, **text + media send** (media: the gateway's `POST /accounts/{id}/send-media` — base64 in the body, ≤16 MB, never a path/URL, `backend/baileys_service/media.js`; `IBaileysGateway.SendMediaAsync`; the cold-text guard covers files too), presence, **voice calls** (call/start, call-event) + **call recordings** (paired transcript written on `ended`, listed per thread, WAV streamed) | templates go out as rendered text (WhatsApp Web has no template messages) |
| Telegram | inbound webhook `/api/messaging/webhook/telegram/{token}` (token compared in constant time, unknown → 404; `X-Telegram-Bot-Api-Secret-Token` required once registered from the dashboard; text + media via getFile, off the ack; inbound heals health); **send** text/photo/document (`Channels/TelegramClient.cs` `TelegramChannel`; the thread keeps its own bot, a bot-less thread is routed); **typing indicator** (`sendChatAction` during the AI's typing pause — `Ai/TypingIndicator.cs` `TelegramTypingIndicator`, routed by `ChannelTypingIndicator`); Register webhook (setWebhook + secret_token) and Test (getMe) | — |
| Email | account CRUD (masked SMTP + optional IMAP creds); **SMTP send** via MailKit (`Channels/EmailChannel.cs`) as **multipart/alternative** — the text as typed + an escaped HTML rendering (`EmailHtml`: paragraphs, `<br>`, http(s)/email auto-links, never raw HTML; multipart/mixed with attachments); the mailbox is chosen by `Routing.ForThread` like a WhatsApp number (thread's healthy mailbox → sticky assignment → least-loaded auto-assign → failover off disconnected/banned with note + audit + notification, the outlet's own first; all down ⇒ failed, none configured ⇒ default SMTP settings ⇒ sandbox); Message-ID / In-Reply-To / References + "Re: <subject>" from `crm_emailheader`; `Auto-Submitted` on AI mail; **IMAP pull** `POST /api/messaging/email/sync` (`Channels/ImapSync.cs`: 30 s throttle, known-sender filter, \Seen, Message-ID dedup, HTML-only mail flattened to readable text, attachments, normal inbound pipeline); Test = SMTP login (+ test mail) | no HTML compose (the HTML part is rendered from the plain text) |
| Messenger / Instagram | account CRUD (+ `app_secret`, also in the setup drawer); **webhook** `GET/POST /api/messaging/webhook` (WhatsApp's verify token; signature **required** against the known Meta app secrets, none ⇒ 401; routed by page / IG id; mid dedup; CDN attachments; delivery/read receipts; sender name); **Send API** text, media by public URL **and uploaded files** (multipart `filedata`, `IMetaMessagingClient.SendFileAsync`; `POST /api/messaging/threads/{facebook\|instagram}/{key}/send-media`; a caption follows as its own message); **messaging window** (`Channels/SocialWindow.cs`, live pages only: ≤24h anything as `RESPONSE`; 24h–7d a signed-in person only, `MESSAGE_TAG` + `HUMAN_AGENT`; AI / follow-ups / flows / queue / API keys / MCP refused — 409 on the inbox routes, failed job, blocked AI draft; >7d or never wrote → refused; `window_open` / `human_window_open` on the thread payload, the inbox `window` filter and the channel hub; a tagged reply refused for permissions never touches health); account Test = `GET /me` → health | no typing indicator (`sender_action`) |
| Website widget | full (internal transport) | — |
| SIP voice | dial / live / say / hangup / escalate through LiveKit | — |
| Media serving | `/api/whatsapp/media/{id}` + `/api/messaging/media/{id}` (`chats.view` + the conversation's entity scope, Range, non-renderable types as downloads; files under `data/{whatsapp,messaging}/media/`, `Channels/WhatsAppMediaStore.cs`), `/api/whatsapp/calls/recording/{name}` (gateway WAV or the agent's), `/api/calls/recording/{call_id}` (SIP) | — |
| Channel health | writer `AccountHealth.SetStatus` (one audit row + an admin `account_health` bell per problem/recovery transition); periodic prober `Workers/HealthProbeWorker` (`WHATSAPP_HEALTH_INTERVAL` s, default 300; `HEALTH_WORKER=0` off) via `Channels/HealthProber.cs`: Cloud Graph ping, Telegram getMe + getWebhookInfo, SMTP login, Messenger/IG `GET /me`, default SMTP → `email_health`; Meta send / status / test results classified by `Channels/MetaErrors.cs`; "Check now" runs the prober for the caller's scope; saving credentials resets to unknown + re-probes in the background | SIP voice stays config-presence only (by design) |
| Routing | `Channels/Routing.cs` at SEND time (queue drain, manual sends, email mailbox + Telegram bot choice, and the AI gate — instant / approved / auto-sent AI replies, follow-ups): thread's healthy number → sticky `AccountAssignment` → least-loaded auto-assign → failover off banned/disconnected (pin cleared, thread moved, note + audit + notification); roles/providers/templates are WhatsApp-only (`RouteNeed.For`; templates → Cloud, never Baileys; 409 when none); account-bound channels (Telegram, Messenger/IG, widget) keep their own account, email spreads even contactless threads; inbound re-attribution; `business_redirect` (`Channels/BusinessRedirect.cs`); recovery one-click + auto (`Channels/Recovery.cs`, hooked in `AccountHealth.SetStatus`) | — |
| Compliance | STOP/UNSUBSCRIBE/BERHENTI (+ `opt_out_keywords`) → `Thread.OptedOut` on every inbound channel, START/UNSTOP/MULA opts back in (`Channels/OptOut.cs`); queued jobs cancelled; manual sends need `force`; cold-text guard (manual + queued WhatsApp text), cold-call guard, do-not-call | — (the opt-out lives on the conversation by design: consent is per channel, as in the original spec) |

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
  overridden (persona, quiet hours, pacing, routing rules, AI review/behaviour…);
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
  `.manage`/`.send` capability is read-only). Roles: superadmin / admin /
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
  (pre-seed bootstrap). Frontend gating (`lib/useAccess.ts`, `nav.ts canSee`)
  is cosmetic.
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
  live call, linked, date ranges, sorts, cursor paging; `count_only=1` powers the
  filter pop-up's live "Show N chats"). The filter UI is `ChatFilterModal.tsx`;
  the filter tabs are per-user `SavedView` rows (`/api/views?scope=chats`,
  `lib/savedViews.ts`). Deep links: `/chats?thread=<channel>:<key>`,
  `/chats?q=<text>`. The page does NOT poll — `lib/chatStream.ts` holds ONE SSE
  connection per tab to `GET /api/whatsapp/stream` (all channels; the change
  token is `ThreadStore.ChangeToken()` = thread `UpdatedAt` + message count),
  ref-counted with a 2 s close grace so page navigation doesn't drop it, and
  exposes a connection status (connected / reconnecting / offline). Older
  per-channel endpoints (`/api/whatsapp/threads/{key}/…`,
  `/api/messaging/threads/{channel}/{key}/…`) still carry the thread actions
  (send, template, media, AI tick, status, assign, labels, note, link, calls).
  Bubbles are theme tokens in `src/app/chats/chats.css` (ours = primary ink,
  theirs = a hairline card) — no WhatsApp greens. `POST /api/whatsapp/simulate`
  fakes an inbound message through the same side effects as a real webhook
  (contact link, flows, team routing, automations, notifications; the AI step
  only with `?ai=1`).
- **Inbound path** = `Channels/InboundProcessor.ProcessInbound`, shared by every
  webhook: store (dedup) → link a contact (email exact, else last-8-digit phone,
  `Stores/PhoneMatch`) → move the thread onto the RECEIVING number → the inbox's
  AI default on a brand-new thread → STOP/START (`Channels/OptOut`) → resume flow
  runs parked on a reply → team auto-assign (`TeamsEndpoints.AutoAssignAsync` via
  the `InboundProcessor.TeamRouter` hook set in Program.cs) → automation rules →
  `NotificationService.NotifyInboundMessage` → expire stale AI drafts →
  **coverage gate** (linked contact AND the EFFECTIVE chatbot tick
  `Ai/AiEligibility` AND not opted out) → AI reply (debounced/queued or instant,
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
  `conversation` (Baileys personal number), defaulting by provider. **The
  per-chat Chatbot tick is per sending account** (`Thread.AiOverrides`
  `{account_pk: bool}`; ONE rule, `Ai/AiEligibility.Effective`: override →
  an account-less OFF stays off → the inbox's `ai_default` → role, with reminder
  numbers off unless the `reminder_ai_default` rule is on). Every payload
  (`ai_enabled`) AND every gate (inbound, queued ai_reply drain, AI monitor
  send-time guard, follow-ups) reads it — never `Thread.AiEnabled` directly;
  write a tick with `AiEligibility.Set` (per-account tick + legacy column).
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
  folds the existing account endpoints into one shape), "Add a channel" blocks,
  and a step-by-step setup drawer (`InboxSetupDrawer.tsx`: choose → connect →
  who answers it → test). "Who answers it" rides on every account write as
  top-level keys stored in `ChannelAccount.Config` (`Channels/InboxSettings.cs`):
  `team_id` (new conversations go to that team first), `ai_default` on|off|""
  (stamped on a brand-new thread only, so a later per-chat tick wins) and
  `ai_review_mode` (per-inbox AI monitor policy). Deleting a messaging account
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
  server-minted 128-bit session id. Admin CRUD at `/api/widgets`.
- **Teams + automations.** Assignment is TWO-LEVEL: a thread belongs to a `Team`
  and optionally one agent in it (`Thread.Assignee`). Round-robin uses a stored
  cursor (`Team.LastAssignee`) and runs on inbound AND `/simulate`.
  `AutomationRule` + `Flows/AutomationService.cs` are the CONVERSATION-centric,
  event-driven counterpart to the contact-centric flow builder: on
  conversation_created / message_created / status_changed / assigned, ALL
  matching rules run in Position order. An unknown operator matches NOTHING.
  `/api/automations/catalog` is the single vocabulary the editor reads.
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
  ChatBranch as an MCP server (Streamable HTTP, JSON-RPC, stateless) over
  `Api/Tools/ToolRegistry` (`BuiltinTools.cs`: search/get/create/update contact,
  list/get conversation, send message, add note, set status, assign, templates,
  knowledge, flows, analytics, activity, tags) — authenticated by an API key,
  acting as its owner. Tool writes call the dashboard's OWN endpoints
  in-process (`Tools/InternalApi.cs`) so every guard applies — a renamed route
  breaks the tool (tests catch it). External MCP servers the assistant may use:
  `/api/integrations/mcp` (Setting `mcp_servers`, real initialize + tools/list
  probe, masked auth). The page is `/developers`.
- **Ask ChatBranch** (operator assistant, `Api/Assistant/OperatorAssistant.cs`,
  `/api/assistant/chat|confirm|cancel`, `components/assistant/AssistantDock.tsx`
  docked on every signed-in page). Acts AS the signed-in user over the same tool
  registry + allowlisted external MCP tools: reads run straight away; writes
  become confirm-first `AssistantAction` rows (15 min, one user cannot confirm
  another's). With no LLM it falls back to a deterministic helper (find
  contacts, counts, "go to …", how-to answers from `AssistantGuide.cs` + the
  knowledge base).
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
  is no persona UI) + SKILLS block (prompt skills in scope) + `COMPANY
  KNOWLEDGE` (`KnowledgeService`: kind fact/policy/faq/event/notice,
  `StartsAt/EndsAt` self-expiry, tiers notice → event → fact then top-4
  keyword-matched faq/policy, **no embeddings on purpose**, 1800-char budget) +
  `CONTACT` (name, company, language, tag NAMES, profile summary — not custom
  fields, tag attributes or records) + `CONTACT MEMORY` (durable contact fields +
  the last 3 summaries, 1500 chars) + the current org-local date/time. It is a **PURE
  GENERATOR returning `AiReplyResult`** (text, draft, tools, checks, retries,
  tokens) — callers send. Every path is exception-safe. LLM:
  `OpenAiLlmClient` speaks OpenAI-compatible `/chat/completions` to openrouter,
  openai, **ilmu** (`ILMU_BASE_URL`, default `https://api.ilmu.ai/v1`,
  `ilmu-v3.1`), **google** (Gemini's `…/v1beta/openai`, `gemini-2.5-flash`) and
  local; keys from Setting rows (`openrouter_api_key`, `openai_api_key`,
  `ilmu_api_key`, `google_api_key`, `local_base_url`, `chat_model` — env
  `WHATSAPP_CHAT_MODEL`) with process-env fallback. A pin (`chat_provider_pref`,
  mirrored to `chat_provider`) is used ALONE (failure → sandbox, never another
  vendor); else `chat_provider_order` is tried in order, an error falling through
  to the next (`chat_model` applies to the first only). Edited at Settings → AI
  keys & models (`/api/settings/ai-providers`, `settings.ai_keys.manage`);
  nothing configured → `SandboxLlmClient`. Usage is
  metered into `LlmUsage` at every call site; costs are estimated at READ time
  from Setting `llm_prices` (`AnalyticsService`). Case summaries
  (`SummaryService`, `/api/messaging/threads/{channel}/{key}/summary`) are
  on-demand, persisted as `ConversationSummary`, heuristic without an LLM.
  Knowledge API: `/api/aicore/knowledge` (+ `/preview` = the exact block the AI
  receives), page `/knowledge`.
- **AI skills (`/skills`).** `AgentSkill.Kind` = **prompt** (`use_when` +
  `instructions` in the SKILLS block) | **tool** (JS `run(args, ctx)` the model
  CALLS) | **check** (JS `check(reply, ctx)` run on EVERY draft). JS runs in
  `Ai/Skills/SkillRuntime.cs` (Jint: fresh engine per run, no CLR interop, no
  eval, 250 ms / 8 MB / statement + recursion caps; faults come back as data).
  Built-in C# tools are always offered (`BuiltinSkills.cs`: current_time,
  current_week, date_add, days_between, convert_timezone; zone = Setting
  `org_timezone` → TZ → UTC). A failing check → rewrite with the reason (max 2),
  then `OnFail`: retry→handover, handover (immediate) or block; handover =
  private note + status pending + notify. **`GET/PUT /api/aicore/skills` is the
  ONLY writer of `AgentSkill`** (PUT admin+, prunes within its own scope, and
  refuses an ENABLED code skill whose code/tests changed unless its tests pass);
  the flow library PUT ignores the `agentSkills` it receives. Authoring:
  `POST /api/aicore/skills/draft` (describe → up to 4 clarifying questions →
  draft; sandbox = a deterministic template library), `/skills/test`,
  `GET /skills/builtins`; the wizard is `components/skills/SkillWizard.tsx`.
- **AI monitor + human-like behaviour** (`/ai-monitor`, `/api/ai/monitor*`,
  `/api/ai/behaviour`). EVERY AI-written outbound (instant reply, queued
  ai_reply, follow-up, flow job with payload `source:"ai"`) goes through
  `Ai/AiGate.Submit`, which records an `AiDraft` row EVEN when it sends
  (`auto_sent`) — the monitor is the full trail. Policy `ai_review_mode` = auto
  | review_flagged | review_all | shadow (+ autosend minutes, first-N, risky
  keywords, notify) resolved inbox `Config` → entity Setting → org Setting →
  default (`Ai/AiBehaviourSettings.cs`, whose `Fields` the settings UI renders
  verbatim). Approve/edit re-runs the send-time guards and routing (opt-out, coverage, 24h
  window; quiet hours wait). The chat shows a held reply in
  `PendingDraftBanner.tsx`. Generation sits behind ONE seam, `Ai/AiComposer` —
  adapt it there when `AssistantService`'s return type changes.
  `Ai/AiResponder` = ONE debounced ai_reply job per thread (wait
  `ai_wait_seconds`, capped by `ai_wait_max_seconds`) + acknowledgement skip;
  `AiGate.StartHumanSend` = 1–3 bubbles chained through the queue with typing
  pauses (`ITypingIndicator`; never sleeps on a request);
  `Ai/AiFollowUpService` = follow-ups when the customer goes quiet (only after
  an open question; stops on reply/opt-out/resolve/human takeover; closed 24h
  window → `ai_followup_template` or a skip note). `Workers/AiOpsWorker` (every
  30 s; `AI_FOLLOWUP_WORKER=0` disables) runs due auto-sends + follow-ups;
  `POST /api/ai/monitor/tick` is the manual seam. Caps `ai.monitor` (agents
  hold it) / `ai.behaviour.manage`. `AI_HUMANIZE=0` turns the human-like
  DEFAULTS off (explicit Setting rows still win) — the test host sets it.
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
- **Flows** — see **Flow builder** below for the editor. Execution:
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
  `POST` registers an identity by hand. Analytics (`/analytics`, the HOME page):
  `GET /api/analytics/summary?days=` (funnel, per-account volumes, call
  outcomes, health events, daily series, KPI deltas),
  `GET /api/analytics/usage?days=` (LLM metering/billing, admin+),
  `/api/analytics/reports`, `/api/analytics/flows`, `/api/live` (waiting
  conversations + active calls).
- **Integrations** (`/integrations`, `/api/integrations/*`,
  `ChatBranch.Data/Integrations/`). `Integration` is `ChannelAccount`'s twin:
  creds in a JSON column that **never leaves the server unmasked**, `Config` for
  per-provider settings. Providers: Google Calendar / Drive / Gmail / Sheets
  (`GoogleOAuth.cs`: one Google client, incremental per-product scopes, PKCE
  S256, HMAC-signed 600 s `state`, lazy refresh that marks a row disconnected
  and returns "" rather than throwing) and Shopify / Stripe (API keys).
  **Per user, per entity, or global:** `Integration.OwnerUserId` — a connect
  flow writes the CALLER's own row (`IntegrationResolver.OwnRowAsync`);
  resolution at call time is the acting user's grant → the entity's shared grant
  (then its subtree) → the organization-wide one (`IntegrationResolver.Active`).
  The org-wide keys live in ONE JSON Setting `integrations_settings`
  (`IntegrationSettings.cs` — a plain `SettingValue("google_client_id")` finds
  nothing). The callback `/api/integrations/oauth/google/callback` is
  deliberately ANONYMOUS (the signed state IS the CSRF control); set
  `GOOGLE_REDIRECT_URI` and `FRONTEND_URL` in production. Both commerce
  webhooks verify HMAC (`WebhookVerify.cs`: Shopify base64, Stripe `t=…,v1=…` +
  300 s replay window) and **no secret configured ⇒ 503, never "accept
  unsigned"**. Sandbox is mandatory: with zero credentials every endpoint
  answers and every action skips cleanly.
- **Docs:** `docs/vertical-guide.md` (configure the template for an industry:
  hard rules → persona → skills → knowledge → memory, plus tags, the AI monitor
  and follow-ups) · `docs/DATABASE_SCHEMA.md` (the real schema) ·
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
  (`NAV_GROUPS`: Analytics + Notifications, then Work · Automate · Library ·
  Channels · Admin · Review; Settings is the footer link) — **a new page = one
  entry there**. Groups are collapsible and the whole rail collapses
  (`nav-collapsed`). Items are gated by `cap` (a string, or an array = ANY of
  them) with a `roles` fallback for sessions without a capability list
  (`canSee`). **Analytics is home** (`HOME = '/analytics'`; `/`, post-login and
  `/dashboard` redirect there — there is no Dashboard page). Ctrl/⌘+K opens the
  command palette (`CommandPalette.tsx`): the visible nav pages plus
  `GET /api/search` (contacts, conversations, templates, knowledge, flows, users,
  entities — each type gated by the capability of the page it opens and scoped
  like that page's list endpoint). The top bar's **Live chip** (`LiveChip.tsx`)
  shows the SSE connection status dot + "N calls · N waiting" from `/api/live`
  (`liveStore.ts`: refreshed every 15 s AND on every stream `change`, keyed by
  entity; also feeds the sidebar's Chats counter and Analytics' waiting card).
  Below `lg` the rail becomes a drawer and a **bottom tab bar** (Home · Chats ·
  Contacts · More) takes over. `Shell.FULL_BLEED` (`/chats`, `/flow-builder`,
  `/settings`) render edge-to-edge and own their height; `PUBLIC_ROUTES`
  (`/help`, `/offline`) and `/login` render bare. **PWA:** `app/manifest.ts`,
  hand-written `public/sw.js` (never caches `/api/*` or the stream; offline
  navigations land on `/offline?from=`), icons in `public/icons/` rendered from
  `Logo.tsx`'s `brandMarkSvg`. **Ask ChatBranch** (`AssistantDock`) is docked
  bottom-right on every signed-in page; its conversation lives in
  `sessionStorage`.
- **`src/lib/AppContext.tsx`** holds shared state + the real-call seams. API
  base = `NEXT_PUBLIC_API_URL` (default `http://localhost:8090/api`; baked in at
  BUILD time). `src/lib/mocks.ts` is the type source (`Contact`, `RecordDto`,
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
  server-side): Personal — Appearance, My notifications (no cap); Organization —
  Notification defaults (`notifications.manage`), Custom fields
  (`settings.custom_fields.manage`), App & provider keys (`app_keys.manage`);
  AI — AI keys & models (`settings.ai_keys.manage`). New page = a
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
- **A modal's errors render INSIDE the modal.** `Modal` portals over a scrim,
  so a page-level `{msg && …}` under an open dialog is invisible. Pattern: a
  `role="alert"` div as the first child of the modal body, the page-level copy
  gated on the modal being closed, `setMsg(null)` when opening one. The same
  holds for drawers (`InboxSetupDrawer`).
- **Never call `getRole()` / `getAuth()` during render.** They read
  localStorage, which doesn't exist during SSR — the server paints the
  `'agent'` fallback, the client paints the real role, and React aborts
  hydration. Read the role/capabilities **after mount**: `useAccess()`
  (`lib/useAccess.ts`, `{ready, can}`), `useRole()` (`lib/useRole.ts`,
  `{role, ready}`), or `useState(false)` + `useEffect`. `lib/auth.ts` still
  migrates the legacy `chatwood-auth` / `ai-collector-auth` session keys and a
  cached `'collector'` role.
- **Contacts:** `/contacts` is a list (the kanban board is gone);
  `/contacts/[id]` shows the AI summary (`/api/messaging/timeline/summary`),
  details + custom fields, records, the channel hub, entities
  (`ContactEntitiesCard`), tags (`ContactTagsCard`) and the communications
  timeline.

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

## Flow builder

- Route `/flow-builder` (full-bleed), components `frontend/src/components/flow/`,
  data model `frontend/src/lib/flow/` (`@xyflow/react`). The library syncs to
  the DB via `GET/PUT /api/flows/library` (GET `flows.view`, PUT
  `flows.manage`); localStorage is only the offline fallback. ⚠ **The PUT
  PRUNES** server-side workflows (in its scope) missing from the browser's copy
  — after creating a Workflow row outside the UI, refresh `/flow-builder`
  before its next sync.
- **Two views, one graph.** **Steps** (default, `StepsView.tsx`) reads
  nodes/edges as a tree (`lib/flow/steps.ts buildStepTree`, mirroring the
  engine's edge picking) and edits it with insert / delete / move +
  `autoLayout`; the **Advanced canvas** (`CanvasView.tsx`) is React Flow over
  the same JSON. Plain-language summaries come from ONE place
  (`lib/flow/describe.ts`). New flows start from the template gallery
  (`lib/flow/templates.ts STARTER_TEMPLATES`: welcome series, appointment
  reminder, re-engage, lead qualification, feedback request — all paused).
- **The vocabulary is a contract** with `FlowEngine.cs`
  (`lib/flow/nodeConfig.ts` — change both sides together). Triggers: Contact
  created · Tag added · Message received (optional keyword/channel) · Date field
  (days before/on/after a contact DATE CUSTOM FIELD, optionally yearly — not the
  record's due date) · Scheduled (time + weekdays) · Manual / API
  (`POST /api/flows/{flowId}/start`); repeat Once / Every time / Once a day.
  Legacy record-due-date triggers still execute but are never offered.
  Step kinds: trigger · action (send message on WhatsApp/Email/Telegram/SMS(logged
  only)/In-app push, or a template · **AI message** = the `AI message` marker
  channel + a `skill` id, composed by `ComposeActionTextAsync` and sent through
  the AI gate · call = AI voice call or Script call, `callVia` `Phone (SIP)` |
  `WhatsApp (Baileys)` · app action) · wait (time, or until they reply) ·
  **followup** ("Follow up if no reply": yes = replied, no = no reply; up to 5
  nudges, stops on resolve) · decision ("Ask & branch": They replied / Reply
  contains / They said yes (EN + BM keyword lists — deterministic on purpose) /
  Has tag / Custom field equals) · handover (to a team) · update (add/remove
  tag, set a field) · end (outcome code); `stage` is a legacy pass-through. The
  step `skill` picker (`SkillPicker.tsx`) offers PROMPT skills only — tools and
  checks run on their own; skills are edited only on `/skills`.
- **Runs panel** (`RunsPanel.tsx`): totals, people entering per day, reach and
  drop-off per step, failure reasons, and each contact's path — from
  `/api/flows/{flowId}/stats` and paged `/api/flows/runs[/{id}]` (contact-scoped
  like every flows read).
- The variable catalog (`lib/flow/variables.ts`) must stay in step with
  `FlowEngine.VariablesFor` (contact, business, record, memory groups +
  `{{cf_*}}` + `{{tag_*}}`). Styles are token-based in
  `src/app/flow-builder/flow-builder.css`, scoped under `.flow-studio`.

## Verifying changes

- **Backend:** `cd backend-csharp && dotnet build src/ChatBranch.Api -c Release`
  then `dotnet test tests/ChatBranch.Tests` (or `npm run test:api` at the root) —
  **326 tests** covering auth/RBAC, scoping, the inbox + server-side filters +
  saved views, webhooks (wamid dedup, the queued ai_reply), the transports
  against fake providers (Meta Graph/Send API, SMTP/IMAP, the Bot API, media,
  recordings, the health prober), routing + failover + STOP/START, contacts, tags,
  templates + inbox settings, flows + step events + stats, the outbound queue +
  queued calls, the AI (tool loop, skills runtime/drafter/API, built-ins, AI
  monitor + human-like behaviour), integrations, notifications + settings,
  search/live, the platform (audit, API keys, MCP, assistant) and the LiveKit
  control plane. Keep them green. The test host (`ApiFactory` in
  `ApiSmokeTests.cs`) sets its own env: `QUEUE_WORKER=0`, `FLOWS_WORKER=0`,
  `AI_FOLLOWUP_WORKER=0`, `HEALTH_WORKER=0` (drains and probes are driven
  explicitly — no timing races),
  `AI_HUMANIZE=0` (sandbox answers inline), `SEED_DEMO=1` (the suite signs in as
  admin/agent), a throwaway `CHATBRANCH_DATA_DIR` and SQLite file, and a LiveKit
  client that is never configured (a developer's `backend/.env` can't place real
  calls). `HEALTH_WORKER=0` keeps the channel-health prober (and post-save
  re-checks) off; `TransportFactory` (`TransportFakes.cs`) swaps in fake
  SMTP/IMAP/Telegram/Graph clients for the transport tests.
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
  agent behind `voice`); `docker-compose.override.yml` + `deploy/nginx-
  chatbranch.conf` are one production single-origin setup. SQLite is a FILE —
  the API's `/data` volume is not optional, and the voice agent shares it.

## Notes

- Dev slowness is normal **Next.js on-demand route compilation** (each route
  compiles on first visit). Use `next dev --turbo` or a prod build for speed.
- CodeGraph MCP is available (`codegraph_*`) — prefer it for structural queries.
