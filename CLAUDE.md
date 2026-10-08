# CLAUDE.md

Guidance for Claude Code working in this repo. Read before making changes.

## What this is

ChatBranch — an industry-agnostic multi-channel conversation platform
(template). Four cooperating processes:

1. **C# API** (`backend-csharp/`, port 8090) — ASP.NET Core minimal APIs over
   SQLite (EF Core). THE backend: contacts, the unified inbox, flows, the
   outbound queue, AI, integrations, RBAC. Root `/` 404s — it is API-only.
2. **Voice agent worker** (`backend/voice_agent/`) — a **separate** long-lived
   LiveKit Agents process (Node/TypeScript on `@livekit/agents`, no database).
   The API dispatches it into each call's room; it runs the conversation and
   writes results to the shared data dir (`backend/data/`). NOT started by the
   API.
3. **Next.js dashboard** (`frontend/`, port 3000) — operator UI.
4. **Baileys gateway** (`backend/baileys_service/`, port 3020) — Node sidecar
   for `provider="baileys"` WhatsApp accounts (unofficial WhatsApp Web:
   QR-paired personal numbers, text + voice calls). The C# API is its only
   client; its env var is `API_URL` (`DJANGO_URL` still accepted).

> **The Django backend was removed on 2026-08-16.** Everything it did lives in
> `backend-csharp/` now, including OAuth and the integration actions that were
> the last stubs. `backend/` retains only the two Django-free sidecars above
> plus `backend/data/` — which holds the LIVE Baileys WhatsApp session
> (`data/whatsapp/baileys/<account>/creds.json`) and inbound media. Never
> delete it. Historical references below to `crm/*.py`, `whatsapp/store.py`,
> `flows/engine.py` etc. describe the behaviour the C# port reproduces; read
> them as the SPEC, and find the code under `backend-csharp/src/`.

## Run

```bash
npm run dev:all  # repo root — all four processes via concurrently (api/agent/web/wa)
```

Or individually (what the root `package.json` scripts do):

```bash
npm run dev:api      # C# API, :8090
npm run dev:agent    # voice agent worker, tsx (separate terminal)
npm run dev:web      # Next.js, :3000 (separate terminal)
npm run dev:baileys  # Baileys gateway, :3020 (separate terminal)
```

First-time bootstrap: `npm install` then `npm run setup` (installs the frontend,
the Baileys gateway and the voice agent). The API needs no bootstrap — SQLite
is created and seeded on first boot (`Seed.Run`, additive `UpgradeSchema` for
new columns). **Nothing in `.env` is required to boot**; with it empty the whole
app runs in sandbox mode (the voice agent exits at once, naming the missing
LIVEKIT_* keys). The API and the agent share ONE data dir: `CHATBRANCH_DATA_DIR`,
default `backend/data` of the checkout (`VoiceAgentBridge.ResolveDataDir`).

## Architecture that isn't obvious from the tree

- **The database is `crm` (SQLite via Django ORM)** — Contact, Record,
  RecordEvent, ContactBrand, ChannelAccount, AccountAssignment, Thread,
  Message, CannedResponse, ScriptTemplate, KnowledgeItem, AgentSkill,
  Workflow, FlowRun, OutboundJob, ConversationSummary, CallRecord, Setting,
  Profile, AuditLog, Notification. Full field-level reference:
  `docs/DATABASE_SCHEMA.md`. The whatsapp/messaging
  `store.py` and `*_config.py` modules are ORM-backed but kept their old
  function signatures + dict shapes — views/AI/bridge code never touch models
  directly, so **change the store layer, not the callers**. Bootstrap:
  the C# API seeds itself on first boot (`Seed.Run`: demo users, the Main
  entity, roles/capabilities). Demo creds superadmin/super123, admin/admin123,
  agent/agent123 — POC only, rotate before deploy.
- **Entities are a TREE (`ingest_entity.parent_id`, added 2026-08-15).** A
  top-level entity (parent null) with branches/outlets under it; a child ALWAYS
  inherits its parent's tenant, so the dashboard's Add-entity form asks for a
  **parent**, never a tenant slug (the Tenant field is gone; `/entities` is
  titled just "Entities"). `POST/PATCH /api/v1/entities` take `parent_id` (pk,
  number or numeric string — `Str()` returns null for JSON numbers, hence
  `ParentPk()`) or `parent` (slug); 0/""/null = top level. Guards: no
  self-parent, no cycles (`IsDescendantAsync`), parent and child share a
  tenant, and DELETE refuses while children exist. The GET payloads carry
  `parent_id`/`parent`/`parent_name`, and the page renders the tree
  depth-first with indentation (`orderTree`). Tenant still exists underneath —
  RBAC/scoping are unchanged — it is simply never typed in by hand.
- **Entity scope flows UP the tree (2026-08-16).** `ScopeOwned` narrows to an
  entity's SUBTREE (`ScopingService.EntitySubtreeIds`, breadth-first, depth
  capped): head office reads a branch's contacts/threads, a branch never reads
  head office's. `/api/contacts?entity=<slug>` filters by that subtree too.
  Agents/custom roles are additionally bounded by their ASSIGNED entities
  (`ContactsEndpoints.AssignedEntityIds`) — an out-of-scope `?entity=` narrows
  to their own scope instead of widening; an agent with NO assignment keeps the
  tenant-wide view (unchanged behaviour, not a lockout).
- **Entity scoping = global default -> entity override.** `crm/scoping.py` is
  the ONE place the rule lives: *an entity-specific binding wins; the global
  one is the fallback*. Three verbs, and picking the wrong one is the bug the
  module exists to prevent:
  `setting_value(key, entity)` (config: entity row -> global row -> ""),
  `scope_inherited(qs, entity)` (libraries an outlet INHERITS — knowledge,
  canned/script templates, skills, workflows, channel accounts, integrations:
  its own PLUS the global ones) and `scope_owned(qs, entity)` (records that
  belong to exactly ONE outlet — contacts, threads, call records, analytics).
  `entity=None` means "All entities" for the scope_* helpers and "the global
  value" for the setting helpers.
  **`crm.Setting` is no longer key-unique**: it carries a nullable `entity`
  FK with a partial unique constraint per scope, so every global read MUST say
  `filter(entity__isnull=True, …)` or another outlet's override leaks into the
  org-wide value — that sweep is already applied across wa_config / msg_config
  / health / queue / engine / catalog / persona / app_settings. Only the keys
  in `scoping.ENTITY_SCOPED_SETTINGS` may be overridden (hours, persona, quiet
  hours, pacing, routing rules, tool use…); credentials and webhook secrets are
  organization-wide BY DESIGN and `set_setting` raises if you try.
  **`ChannelAccount.entity` is the linchpin**: the number that receives a
  message decides which outlet the conversation belongs to, so attribution
  needs no manual tagging. `scoping.thread_entity` is that chain — receiving
  account's entity -> linked contact's entity -> None ("unassigned", shown in
  every view rather than guessed at). It is denormalized onto `Thread.entity`
  and re-synced by `sync_thread_entity` at EVERY seam that moves a thread
  (creation, account switch, contact link/unlink, account re-binding); a new
  write path that sets `thread.account` or `thread.contact` must call it too.
  `accounts_for_entity` / `integration_for_entity` are a PREFERENCE, not a
  union: an outlet with its own WhatsApp number or Google Calendar never falls
  back to the shared one. Endpoints take `?entity=<slug>` via
  `scoping.entity_from_request`, which BOUNDS the slug by RBAC — and falls
  through to the caller's own scope rather than returning None for a rejected
  slug, because None means "all entities" and would widen the response.
  Frontend: `useEntityQuery()` (lib/EntityContext) is the only way pages build
  the parameter, and it belongs in the fetching effect's dependency array —
  omitting it is what leaves a page showing the previous outlet's data.
  Tests: `tests/test_entity_scoping.py`.
- **Cross-process IPC with the voice agent stays file-based ON PURPOSE** —
  the agent process never touches the database. `voice_agent/src/storage.ts`
  writes/reads (atomically): live transcripts (`data/live/{room}.json`), the
  operator instruction inbox (`data/inbox/{room}.json`, a list of
  `{mode, text, ts}`, consume-once), final call records (`data/calls.json`,
  folded into `CallRecord` + the `LlmUsage` ledger by `VoiceCallSync`),
  per-contact `memory/{id}.md` and `pronunciation.json`. The C# side is
  `ChatBranch.Data/Sidecars/VoiceAgentBridge.cs`. The WhatsApp call-signal
  files (`data/whatsapp/calls/`) are the same pattern.
- **`calls/lk.py` bridges Django → voice-agent code.** It adds `backend/voice_agent`
  to `sys.path` and imports `caller`/`storage` directly (the voice_agent dir is a
  plain script dir, not a package). `_AGENT_DIR` in `lk.py` is the join point — if
  you rename the folder, update it there.
- **`lk` is imported LAZILY inside the views that dial/poll** (`calls/views.py`),
  never at module top. Importing it pulls the whole LiveKit SDK (seconds). Keep
  `/contacts` and other cheap endpoints free of that import. Don't "tidy" the
  lazy imports up to the top.
- **`calls/contacts.py` is DATABASE-FIRST** (`crm.Contact`), falling back to a
  legacy `backend/data/contacts.json` file only when the table is empty (fresh
  clone before the API seeds itself). It maps snake_case contact dicts →
  the frontend's camelCase `Contact` (`to_contact`) and builds the voice-agent
  dispatch metadata (`dial_meta`, everything vertical-specific riding on the
  contact's primary `Record`). `backend/data/` is gitignored — it holds PII.
- **`backend/whatsapp/` = WhatsApp Cloud API app** (endpoints under
  `/api/whatsapp/`; dashboard page `/chats`). API surface ported from the
  Whatomate repo (Go, AGPL — protocol shapes reimplemented, not copied).
  Threads/messages are `crm.Thread` / `crm.Message` rows (the old
  `data/whatsapp/{wa_id}.json` files were migrated into the DB; `store.py`
  kept its dict shapes).
  **Sandbox mode**: without a linked account everything stores locally and
  `/simulate` fakes inbound messages; with accounts the same code hits
  graph.facebook.com. **Multi-account** (Whatomate's model): accounts are
  `crm.ChannelAccount` rows (managed from Settings → Channels → WhatsApp;
  `WHATSAPP_*` env keys surface as a read-only `env-default` account), every
  `client.py` function takes the account dict, threads carry `account_id`,
  and the webhook routes by `metadata.phone_number_id`. Global app settings
  (AI keys, verify token) live in `crm.Setting` rows, editable from
  Settings → App Keys. **Quiet hours, send pacing (queue min/max gap) and
  the voice-call LLM brain / STT engine are PER CHANNEL ACCOUNT** since
  2026-08-15 (the AI Configuration tab was retired; `/settings/ai` redirects
  to `/channels`): they live in `ChannelAccount.config` next to `role`, edited
  from Channels → edit account → "Delivery & AI", accepted as top-level keys
  on `POST /api/whatsapp/accounts/<id>` (`quiet_start`, `quiet_end`,
  `queue_min_gap_s`, `queue_max_gap_s`, `llm_provider`, `stt_provider`; blank
  = inherit) and returned as `delivery{}` in the account payload. Resolution
  is account → Setting row → env → default, in ONE place (C#
  `ChatBranch.Data/Channels/AccountDelivery.cs`); the queue drain applies the
  pacing window AND quiet hours per job account at send time, and the
  template-send gate checks the sending account. The old global
  `POST /api/whatsapp/settings` quiet keys and `PUT /api/whatsapp/routing-rules`
  gaps still exist as the inherited defaults (no UI edits them now).
  **RBAC is real**: `/api/auth/login` issues bearer
  tokens (rotated per login, stored on `crm.Profile`); roles = superadmin /
  admin / **agent** (`lib/auth.ts getAuth()` normalizes a cached `'collector'`
  session from the pre-template naming, and still reads the legacy
  `ai-collector-auth` localStorage key). Server-side gates: global settings
  = superadmin; account management = admin+; audit log = admin+ (superadmin
  sees all actors). **Brands/tenant model**: `ingest.Tenant`
  owns `ingest.Brand`; admins see their whole tenant, agents only
  `Profile.brands`. `/api/v1` (DRF, same bearer token): whoami / brands
  (GET/POST) / users (GET/POST, creation hierarchy) / contacts
  (GET/POST, brand-scoped) + contacts/<id> (GET/PATCH). UI: Settings →
  **Brands & Tenants** (`BrandsSettings.tsx`,
  admin+ create) and Settings → Users & Roles (visible to admin+, with a
  role-capability matrix; agent rows carry tenant/brand chips + a Brands
  editor). Both `/api/v1/users` and the legacy `/api/auth/users` call
  `crm/user_service.py`, which holds the TIERED rules in one place:
  superadmin manages everyone; admin lists only their own
  tenant and creates/manages ONLY agents there (no role changes, no
  touching admins/superadmins — 403s); they accept `brand_ids` (agents
  only, one tenant, validated before create; role change away from agent
  clears brand access; an agent created without brands inherits the
  creating admin's tenant).
  Demo users from `seed_demo`: superadmin/super123, admin/admin123,
  agent/agent123 — POC creds, change before deploy. Frontend auth in
  `lib/auth.ts` (login page `/login`, guard in `Shell.tsx`, `lib/role.ts` is
  a back-compat shim). Notifications (`crm.Notification`) fan out by role on
  inbound messages + settings changes → bell in the header; Settings →
  Notifications is REAL (not a mock): global mute toggles per category
  (`messages` / `account_health` / `flows` / `admin`), Setting rows
  `notif_<category>`, `GET/PUT /api/auth/notification-prefs` (PUT admin+),
  enforced fail-open inside `rbac.notify` via its `category=` param
  (unset → derived from kind: message→messages, else admin). Audit trail
  (`crm.AuditLog`) records logins, sends, calls, account/settings changes →
  Activity page + admin. The `X-Role` header only works while NO Profile
  rows exist (pre-seed bootstrap). Read config via `wa_config` accessor
  functions — never re-add module-level constants.
  **Hardened (production pass):** every `/api/whatsapp/` endpoint except the
  webhook and `status` returns 401 without a bearer role (messaging endpoints
  use the same `_auth_required` pattern); the webhook signature is REQUIRED
  once any account is configured (live accounts must set `app_secret`); the
  verify token auto-generates once into a Setting row — the shipped default is
  never accepted, copy the real value from Settings; inbound wamids are deduped
  (partial unique constraint, crm migration 0003) and AI replies run on a
  daemon thread so the webhook ack never blocks on OpenAI. **24h-window
  truth:** inbound rows store Meta's own `messages[].timestamp`
  (`append_message provider_ts`) because webhooks can arrive HOURS late (Meta
  retry backlog) — window math off arrival time offers sends Meta then
  rejects with 131047; a 131047 status auto-runs `store.close_window` (caps
  too-new inbound created_at; `except_account_id` keeps the new number's own
  replies on account switches) so the composer flips to template mode.
  **Text features:** template messages (account `waba_id` enables live listing
  from Meta; sandbox serves 3 demo templates), media send/receive (files under
  `data/whatsapp/media/`, served auth-gated at `/api/whatsapp/media/<pk>`;
  **inbound media is ALL-channel**: the Baileys gateway decrypts attachments
  itself — only it holds the Signal keys — and pushes them multipart to
  `POST /api/whatsapp/baileys/media` (X-Baileys-Secret, two-phase like the
  Cloud webhook: text placeholder first, media attached after); telegram
  (getFile) / messenger+IG (CDN url) download async off the webhook ack,
  email attachments come straight from the IMAP part — those non-whatsapp
  files live under `data/messaging/media/` served at
  `/api/messaging/media/<pk>`, one media per crm.Message so extra
  attachments become extra rows; 20MB cap, animated .tgs/.webm stickers keep
  their text placeholder, webp stickers render as images),
  24h-window flags on threads (`window_open`; closed window → 409, use a
  template), STOP/UNSUBSCRIBE/BERHENTI opt-out (blocks AI + templates; manual
  send needs `{"force": true}`), quiet hours (`quiet_start`/`quiet_end` global
  settings, server-local time), message pagination (`?limit=&before=`).
  The `/chats` page does NOT poll the threads endpoints — it subscribes to
  `GET /api/whatsapp/stream` (SSE; lives in the whatsapp app because that
  channel's plumbing predates the unified inbox, but covers ALL chat channels via
  `crm.Thread`) and re-fetches only on `change` events
  (`frontend/src/lib/chatStream.ts`). The stream's change token watches
  `Thread.updated_at` + message count, so any store write path that uses a
  queryset `.update()` must also bump the thread's `updated_at` (see
  `update_message_status`) or the dashboard won't refresh.
  Linking walkthrough: `backend/whatsapp/SETUP.md`. AI auto-reply (`whatsapp/ai.py`) uses
  OpenRouter if `OPENROUTER_API_KEY` is set, else `OPENAI_API_KEY` — lazy
  `openai`/`httpx` imports, same rule as `lk`: don't hoist them to module top.
  **Coverage gate:** auto-answers (AI replies on ALL channels + the
  business-number redirect/template) fire only for threads linked to a
  `crm.Contact` (`thread.contact_id`, set by phone match or manual link) —
  unknown contacts get nothing; the queued ai_reply drain re-checks it
  (cancel "no linked contact").
  WhatsApp **voice calls**: fully implemented — `wa_calls.py` (signaling +
  process control) spawns one `whatsapp/bridge.py` process per call (aiortc
  terminates Meta's WebRTC leg, joins a LiveKit room as `whatsapp_borrower`,
  and the normal voice agent is dispatched there). Webhook↔bridge IPC =
  `data/whatsapp/calls/{call_id}.json` signal files (SDP answer, hangup).
  (The Cloud-API WebRTC bridge was Python and went with the Django removal —
  WhatsApp calling now runs over the Baileys transport only.)
  Calls are PID-tracked (`data/whatsapp/calls/pending-{wa_id}.json`, pid
  merged into the call signal): stale call state self-recovers when the
  bridge pid is dead, each call writes its own log under
  `data/whatsapp/calls/logs/` (14-day retention), and TURN/STUN comes from
  `WHATSAPP_ICE_SERVERS` in .env (JSON list; malformed → aiortc defaults).
  Untested against a real Meta calling-enabled number (Business Calling is
  gated beta) — expect field debugging on first live call.
  **Baileys transport** (`provider="baileys"` accounts): the Node gateway
  `backend/baileys_service/` pairs a personal WhatsApp number by QR
  (Settings → WhatsApp Accounts) and does text send/receive AND **voice
  calls** without Meta's beta. Calls (`calls.js` + Django
  `whatsapp/baileys_calls.py`) compose baileys-caller's internals
  (SignalingBridge/WasmEngine — NOT its VoipClient, which would open a
  conflicting second socket) around the gateway's existing account socket and
  bridge audio in-process to LiveKit (16 kHz, no ffmpeg, no per-call
  process). Django dispatches the agent + mints the room token; the gateway
  joins as `whatsapp_borrower` and pushes ringing/connected/ended to
  `/api/whatsapp/baileys/call-event` (X-Baileys-Secret, like `/inbound`).
  No permission flow (auto-granted), one call at a time per gateway,
  outbound-only. Agent voice is selectable per call: `POST call/start` takes
  `{voice_provider, voice_id}` → `baileys_calls._resolve_voice` resolves an
  ElevenLabs dashboard voice to its real voice_id and passes `voice_provider`
  /`voice` in the dispatch metadata (the agent then runs the ElevenLabs
  STT→LLM→TTS pipeline instead of the native realtime voice); empty = native.
  The `/chats` call panel has an "Agent voice" picker (ElevenLabs clones from
  `GET /api/voices`, remembered in `localStorage['wa-call-voice']`). Only the
  Baileys transport honours it — the Cloud bridge builds its own metadata.
  Offline check: `node calls.js --selftest`. ⚠ ToS-violating
  transport — dev/demo only, not for production; use a spare number. Docs: `backend/baileys_service/README.md`.
  **Live call monitor** (both call transports): a WhatsApp call runs the same
  voice agent as a SIP call, so the agent writes its transcript to the
  voice-agent file store (`data/live/{room}.json`) and reads operator
  instructions from `data/inbox/{room}.json` regardless of channel — the room
  is stored on the thread as `call.room`. `/api/whatsapp/threads/{wa_id}/call/
  {live,say,escalate}` (auth-gated) reuse that store via `wa_calls.live_transcript`
  / `push_say` (which import voice_agent `storage` directly — NOT `calls.lk`,
  to skip the LiveKit SDK on the frequent poll). The `/chats` info panel polls
  `call/live` and shows the transcript + Guide/Takeover/Escalate/End. **There is
  no separate `/live-calls` page** — the whole call UI lives in `/chats`
  (`components/chats/`: `CallLauncher`, `VoiceCallButton`, `ScriptCallButton`,
  `ActiveCallsStrip`, `VoiceCallPanel`) plus `CampaignModal` for batch dials.
  **Recording + transcript (one entry):** the Baileys gateway records each call
  to a local stereo WAV (`data/whatsapp/calls/recordings/{wa_id}-{ms}.wav`,
  L=contact R=agent), captured off the pacer with no I/O in the hot path (env
  `BAILEYS_RECORD_CALLS=0` to disable). On the gateway's `ended` push (which
  carries the recording filename), `wa_calls.save_call_transcript` writes the
  paired `{wa_id}-{ms}.json` transcript next to it. `GET call/recordings` lists
  each call with its transcript inline; the WAV streams auth-gated via
  `GET /api/whatsapp/calls/recording/<name>`. The `/chats` info panel shows a
  "Calls" section — one entry per call = audio player + click-to-expand
  transcript (transcript is NOT dumped into the message stream). Cloud-bridge
  calls are recorded too since 2026-07-17 (`bridge.py _CallRecorder`, same
  `{wa_id}-{ms}.wav` naming, `wa_calls.save_call_transcript` on cleanup, env
  `WHATSAPP_RECORD_CALLS=0` to disable); inbound calls are unsupported
  (baileys-caller is outbound-only). **Greeting waits for answer:** the gateway defers its LiveKit
  join until `CallState.Active`, so the agent (which waits on the participant)
  doesn't greet into a ringing line. **Contact identity:** `_dispatch_agent`
  looks the CRM `Contact` up by phone (`store._match_contact`) and passes the
  real name/amount/overdue/language into the dispatch metadata — NOT the
  WhatsApp push-name.
  The `/chats` UI is a three-pane inbox → chat → info panel in the Paper &
  Ink design: bubbles are theme tokens in `src/app/chats/chats.css`
  (ours = primary ink, theirs = a hairline card); the old WhatsApp-green
  palette is retired — don't reintroduce raw hex. The list is SERVER-filtered
  via `GET /api/inbox/threads` (every channel, entity-scoped like the old
  lists; `count_only=1` powers the filter pop-up's live "Show N chats") and
  the filter tabs are per-user `SavedView` rows (`/api/views?scope=chats`).
  Deep links: `/chats?thread=<channel>:<key>`, `/chats?q=<text>`.
- **Channel health + contact→account routing.** `whatsapp/health.py` is the
  ONLY writer of `ChannelAccount.health_status` (unknown/healthy/degraded/
  disconnected/banned): send paths, the Cloud webhook, Baileys gateway
  lifecycle pushes (`POST /api/whatsapp/baileys/status-event`,
  X-Baileys-Secret) and a periodic checker thread (whatsapp/apps.py,
  `WHATSAPP_HEALTH_INTERVAL` default 300s) all funnel through `set_status`,
  which fans out a Notification + AuditLog (`account.health`) once per
  transition. Checker coverage: Graph token ping (Cloud), Telegram
  `getMe` + `getWebhookInfo` (revoked token → disconnected; missing/failing
  webhook or stuck backlog → degraded), SMTP probe (email), SIP voice
  (`voice_health()` — **config-presence only, `live_check` is always false**:
  no REST endpoint proves a SIP credential short of placing a real call.
  Configured = `SIP_OUTBOUND_TRUNK_ID` in .env **plus** a termination URI, and
  health degrades to a hint when the SIP username/password are missing. Creds
  are dashboard-managed: Settings →
  Channels → Voice ↔ `GET/PUT /api/whatsapp/voice-settings` (PUT superadmin,
  masked-token round-trip) + `POST …/voice-settings/test`; stored in Setting
  `voice_twilio_settings` via `health.voice_settings()`, per-field fallback
  to `TWILIO_SIP_USERNAME`/`TWILIO_SIP_PASSWORD`/`TWILIO_TERMINATION_URI` in
  .env. `SIP_OUTBOUND_TRUNK_ID` stays env-only — the agent worker reads it at
  boot); Baileys is event-driven. The Meta error-code
  classification lives in the health.py docstring — per-recipient codes
  (131047 window closed etc.) never damage account health. The env-default
  pseudo-account appears in health only when .env actually configures it.
  **Routing is ALL-channel**: `routing.account_for_contact(contact,
  channel)` = sticky `crm.AccountAssignment` → least-loaded eligible
  auto-assign (even distribution) → failover when the assigned account is
  banned/disconnected (clears the pin, moves `thread.account` at send time,
  notifies). Non-whatsapp channels count as configured when `access_token`
  is set. Endpoints: `GET /api/whatsapp/channels/health`, `GET/PUT
  /api/whatsapp/assignments?channel=…`, `POST …/assignments/rebalance`
  (admin+, body `{"channel"}`). Per-chat manual switch: `POST /api/whatsapp/
  threads/<wa_id>/account` (admin+) — provider-aware (Baileys → ban-risk
  guidance, no templates; Cloud → per-account window truth via
  `Message.account` history), pins the assignment, appends a system note.
  UI: dashboard "Channel health" card (60s poll, red on any down account,
  incl. email + voice chips), Settings → Account Assignment (channel
  selector; pins survive rebalance, not failover), chat info panel "Sending
  account" row. **Reconnect reset:** saving new credentials on an account
  calls `health.reset_on_reconfigure` (silent → unknown + instant re-check
  on a daemon thread) — without it a re-tokened account stays
  "disconnected" and failover keeps steering sends away. **Capability
  routing:** template sends resolve via `_template_send_account` /
  `routing.account_for_contact(provider="cloud")` — templates NEVER fail
  over to Baileys; a healthy-but-wrong-provider assignment is overridden
  per-send without moving the sticky assignment, and zero eligible Cloud
  accounts → 409 with a clear message, thread untouched. **Account roles
  (client operating model):** `ChannelAccount.config["role"]` = "reminder"
  (Cloud business number = compliant template cannon) or "conversation"
  (Baileys personal numbers = the AI negotiation layer, inbound-first);
  unset defaults by provider (cloud → reminder, baileys → conversation).
  **The per-chat Chatbot tick is PER SENDING ACCOUNT**
  (`Thread.ai_overrides` {account_pk: bool}, crm migration 0017,
  `store._effective_ai`): no override → the account's role decides
  (reminder off, conversation on), so a WhatsApp thread moving between
  business/personal numbers (inbound re-attribution, per-send template
  restamp, manual switch) automatically uses the CURRENT number's own
  tick — switches never mutate AI state, and operator ticks stick per
  (chat, account). `Thread.ai_enabled` survives only as the
  account-less/sandbox fallback (and for non-whatsapp channels); a role
  change → reminder stamps explicit OFF ticks on the account's threads so
  flipping the role back never re-enables anything. Accessor
  `wa_config.account_role(row_or_dict)`; the accounts API surfaces/accepts
  `role` (no health reset on change); `routing.eligible_accounts` /
  `account_for_contact` take a `role=` filter — conversational sends
  (queue text jobs, AI replies) use role="conversation" so conversation
  failover never lands on the business number, and template routing PREFERS
  role="reminder" clouds (falls back to any eligible Cloud). Operating
  knobs = `GET/PUT /api/whatsapp/routing-rules` (Setting rows, PUT admin+):
  `ai_reply_mode` queued(default)|instant — queued turns AI replies on
  conversation accounts into kind="ai_reply" OutboundJobs (dedup one per
  thread; reply GENERATED AT SEND TIME so several contact messages get one
  coherent answer; quiet hours park the job via not_before, sandbox stays
  instant); `business_redirect` (default true) — inbound on a reminder
  number with AI off gets ONE queued auto-answer per 24h
  (`views._maybe_business_redirect`, job pinned via payload account_id to
  the receiving account; throttle reads OutboundJob rows with
  payload.redirect, not message text): `business_redirect_target`
  conversation(default) = wa.me nudge to the contact's conversation number |
  support = human-support pointer | template = the operator's own approved
  template (`business_redirect_template` name; unset falls back to the
  wa.me nudge); `recovery_mode`
  one_click(default)|auto + `recovery_template` — on a conversation
  account's transition INTO banned/disconnected, health.py
  `_on_conversation_account_down` notifies "Account X down — N contacts
  affected" and (auto) queues the recovery template per affected contact;
  `GET /api/whatsapp/recovery` lists down conversation accounts with
  assignments, `POST /api/whatsapp/recovery/<id>` (admin+) is the manual
  trigger (409 without template/Cloud account); `cold_text_guard`
  block(default)|warn|allow — free-text into a ZERO-inbound thread on a
  conversation-role number 409s (admin `{"force": true}` overrides) or gets
  an amber system note; queue text jobs fail the same way; templates +
  reminder numbers unaffected; `reminder_ai_default` off(default)|on — the
  DEFAULT Chatbot tick for reminder-role (business) accounts: "on" lets the
  business number hold AI conversations without per-chat ticks (replies stay
  on the INSTANT path — queued pacing is conversation-role-only — and the
  business redirect stands down, since it only answers AI-off inbound);
  explicit per-chat overrides always win, incl. the OFF ticks a
  conversation→reminder role change stamps
  (`wa_config.reminder_ai_default_on`, `store._effective_ai`, toggle on the
  Routing Rules card). `crm.Message.account` records which number carried every
  message — both stores stamp it; keep that when adding write paths. The
  chat UI shows "via <account>" on bubbles when >1 account exists and an
  amber divider at sender-account switches AND (in the merged contact view)
  at contact-identity switches.
- **Website widget** (`/widget.js` + `/api/widget/<token>/*`, 2026-08-16) — the
  one channel a customer can start with no phone number. A widget IS a
  `ChannelAccount` with channel="website" (its ExternalId is the PUBLIC token
  that ships in the script tag), so the inbox, teams, automations, AI replies
  and entity scoping work on widget conversations with no special-casing. The
  script renders into a SHADOW ROOT (host CSS can't leak in or out). Public
  endpoints are anonymous by necessity and carry their own permissive CORS
  policy ("widget") — a widget runs on other people's domains, so an origin
  allow-list is impossible; safe because they carry no credentials and expose
  one visitor's own conversation, keyed by a server-minted 128-bit session id.
  Admin CRUD at `/api/widgets` (paste-ready snippet, token rotation; deleting a
  widget KEEPS its conversations).
- **Teams + automations** (2026-08-16). Assignment is TWO-LEVEL: a thread
  belongs to a `Team` (who owns it) and optionally one agent inside it
  (`Thread.Assignee`), so an unclaimed conversation still has an owner.
  Round-robin uses a stored cursor (`Team.LastAssignee`) and runs on inbound
  (`TeamsEndpoints.AutoAssignAsync`, hooked into `InboundProcessor` AND
  `/simulate` — a sandbox that behaves differently hides bugs).
  `AutomationRule` + `AutomationService` are the CONVERSATION-centric,
  event-driven counterpart to the contact-centric flow builder: on
  conversation_created / message_created / status_changed / assigned, ALL
  matching rules run in Position order (not first-match). The engine never
  throws into a webhook, and an unknown operator matches NOTHING rather than
  everything. `/api/automations/catalog` is the single vocabulary source the
  editor reads.
- **Notifications + Settings sections** (2026-10-08). `NotificationService`
  (ChatBranch.Data, root namespace) is the ONLY notification writer: org mute
  `notif_<category>` → the recipient's own switch (`Profile.Preferences` JSON,
  `notifications{}`) → "only assigned to me / my teams" for messages →
  `RefKey` collapse (one unread row per conversation per user). Fail-open,
  never throws. Inbound customer messages notify assignee → team members →
  the tenant's admins (InboundProcessor, /simulate, widget). `/notifications`
  is everyone's inbox (`/api/notifications*`); the bell keeps
  `/api/auth/notifications`. Settings = sections of flat `/settings/<id>`
  pages, each with its own capability (`SETTINGS_SECTIONS`), checked
  server-side via `SettingsEndpoints.RequireCapability`. Appearance:
  `/api/auth/preferences` + `/api/settings/appearance` (org default).
  Account assignment is a Work module at `/assignments` (+ `POST
  /api/whatsapp/assignments/rebalance`).
- **Help Center** (`/help`, `/api/help/articles`, 2026-08-16) — published
  `KnowledgeItem`s, readable by anyone. `Published` is PUBLIC readability and is
  NOT `Active` (whether the AI may use it): an internal note is active and
  unpublished, a public FAQ is both. Slugs derive from the title when unset.
  `Shell.PUBLIC_ROUTES` renders `/help` outside the operator shell with no auth
  guard — don't let a customer reading the refund policy hit a login screen.
- **Reports** (`/api/analytics/reports[.csv]`) — by agent / team / label /
  channel, counting conversations TOUCHED in the window (not created), with
  "(unassigned)" reported rather than dropped. CSV fields are quoted so a label
  containing a comma cannot corrupt the export.
- **`backend/messaging/` = email + social channels** in the same unified
  `/chats` inbox (endpoints under `/api/messaging/`) — the PERMANENT in-house
  home for these channels (Chatwoot was fully removed 2026-07-17 on the
  client's decision; do not reintroduce it). Email = outbound SMTP + inbound
  IMAP (`imap_client.py`, pulled by `POST /api/messaging/email/sync` — /chats
  nudges it every 60s, server throttles real IMAP hits to one per 30s; the
  fetch marks mail `\Seen` so each message is considered exactly once).
  **Inbound mail is scope-filtered** (`views._known_sender`): only senders
  matching a CRM contact email, or an email thread we've corresponded on
  (contact-linked or has an outbound message), are ingested — newsletters /
  platform notifications are skipped for good.
  **Multi-account**: `ChannelAccount(channel="email")` rows hold per-mailbox
  SMTP creds in the `config` JSONField (crm migration 0014; external_id =
  from-address, password never leaves the API unmasked), managed via
  `/api/messaging/accounts`, sent per-account (thread account → sticky
  routing → global `data/messaging/config.json`/env fallback), probed by the
  health checker; Messenger + Instagram =
  Meta Send API (`/me/messages`) with webhook intake at
  `/api/messaging/webhook` (objects `page` / `instagram`, same verify token
  as WhatsApp); Telegram = Bot API (token accounts, Chatwoot-style webhook
  path `/api/messaging/webhook/telegram/<token>`, inbound heals health).
  AI auto-replies reuse `whatsapp/ai.py` so every text channel behaves
  identically. Same sandbox rule: unconfigured channels store locally.
  Endpoints are bearer-auth gated like whatsapp (webhook + status stay open).
  Cross-channel shared templates = `crm.CannedResponse` with full CRUD at
  `GET/POST /api/messaging/canned` + `PATCH/DELETE …/canned/<id>` (mutations
  admin+, 409 on duplicate short_code) — managed in Settings → Message
  Templates, inserted via the composer's `/short_code` picker on ALL channels.
- **`backend/aicore/` = the AI brain.** `aicore/assistant.py` is the
  channel-agnostic AI: persona hard rules + CRM enrichment (Contact lookup by
  phone; status, tags, attributes and open `Record`s) + `detect_handoff()`;
  `whatsapp/ai.py` is a thin delegate to it, so every channel's bot behaves
  identically.
  **AI memory + persona** (`aicore/memory.py`, `aicore/persona.py`):
  `crm.ConversationSummary` rows are generated after calls
  (`wa_calls.save_call_transcript` daemon-thread hook) and idle chats (aicore
  apps.py ticker, `AICORE_SUMMARY_INTERVAL`=600s / `AICORE_SUMMARY_IDLE_MIN`
  =120; one summary per burst, cheap already-summarized check); durable
  facts write back onto Contact (preferred_contact_time,
  communication_style, sentiment_trend, ai_profile_summary).
  `memory.build_context` (~1500 chars, structured + recency, no embeddings
  on purpose) feeds a "CONTACT MEMORY" block into BOTH assistant.py and the
  voice dispatch metadata (`memory` key → `voice_agent/src/prompts.ts`) — chat
  and voice share one memory. (The C# dispatch builder, `VoiceDispatch`, sends
  memory, knowledge and skills on SIP and WhatsApp calls alike.) Persona =
  `HARD_RULES` (safety floor, never overridable) +
  `DEFAULT_PERSONA` (a neutral business assistant), overridable via Setting
  `aicore_persona` — `GET/PUT /api/aicore/persona` (PUT superadmin),
  Settings → AI Persona. All memory entry points are
  exception-safe: they must never break a send/webhook path.
  **Knowledge base + skills** (`aicore/knowledge.py`, Phase 8):
  `crm.KnowledgeItem` rows (kind = fact/policy/faq/event/notice, with a
  `starts_at`/`ends_at` window that makes a "sudden notice" SELF-EXPIRE) are
  assembled by `build_knowledge_context()` into a bounded `COMPANY KNOWLEDGE`
  block and injected at the SAME seams as memory — `assistant.py`'s system
  prompt, `whatsapp.store.call_metadata()` and `calls.contacts.dial_meta()`
  (`knowledge` key → `prompts._metadata_knowledge_block`) — so it reaches all
  six channels AND both call transports with no per-channel code. Retrieval
  is keyword + recency + priority, **no embeddings, same deliberate choice as
  memory.py**: tiers notice → event → fact (all live rows), then the top-4
  keyword-matched faq/policy; zero hits contributes NOTHING rather than
  padding. `BUDGET`=1800 chars hard cap, per-item truncation is labelled, and
  every entry point is exception-safe (a failure degrades to "answer without
  knowledge"). Filters: active, date window, `channels` (empty = all) and
  brand scope (item brand null, or one of the contact's `ContactBrand`
  links — an anonymous build sees only brand-agnostic rows). API:
  `GET/POST /api/aicore/knowledge`, `PATCH/DELETE …/knowledge/<id>`,
  `POST …/knowledge/preview` (the exact block the AI receives — turns "why did
  the bot say that?" into a button), Settings → Knowledge Base.
  ⚠ **`crm.AgentSkill` is owned by `GET/PUT /api/aicore/skills` ALONE**
  (Settings → AI Skills, PUT admin+). `PUT /api/flows/library` used to upsert
  AND PRUNE those rows, so a stale flow-builder tab silently deleted skills an
  operator had just typed; it now ignores the `agentSkills` it still receives
  and the builder's skill sidebar/editor are READ-ONLY. Skills render as a
  `SKILLS` prompt section (`use_when` selects, `instructions` is the body) on
  every channel and every call. Don't re-add a second writer.
- **Code skills (2026-10).** `AgentSkill.Kind` = prompt (the SKILLS block) |
  tool (JS `run(args, ctx)` the model CALLS) | check (JS `check(reply, ctx)`
  run on EVERY draft). JS runs in `Ai/Skills/SkillRuntime` (Jint: no CLR, no
  eval, 250 ms / 8 MB / statement + recursion caps; faults come back as data).
  Built-in C# tools (current_time, current_week, date_add, days_between,
  convert_timezone; zone = Setting `org_timezone` → TZ → UTC) are always
  offered. `AssistantService` is a PURE GENERATOR returning `AiReplyResult`
  (text, draft, tools, checks, retries, tokens) — callers send. A failing
  check → rewrite with the reason (max 2), then OnFail: retry→handover,
  handover (immediate), block; handover = private note + status pending +
  notify. Flow Action `skill` (SkillId, or legacy name) / "AI message" →
  `ComposeActionTextAsync`; jobs carry `source:"ai"` + `ai_meta`. PUT refuses
  an ENABLED code skill whose code/tests changed unless its tests pass.
  Authoring: `POST /api/aicore/skills/draft` (describe → questions | draft;
  sandbox = template library), `/skills/test`, `GET /skills/builtins`.
- **`backend/flows/` = the flow-builder execution engine.** The `/flow-builder`
  page syncs its whole library to the DB (`crm.Workflow` / `crm.AgentSkill`,
  React Flow JSON stored verbatim) via `GET/PUT /api/flows/library` (PUT is
  admin+; localStorage is only the offline fallback). Workflows toggled **Live**
  (`active`) are executed automatically by `flows/engine.py` — the ONLY
  interpreter of that JSON: time triggers match `Contact.overdue_days` each
  tick (in-process ticker thread started by `flows/apps.py` under runserver;
  `POST /api/flows/tick` is the manual seam), and
  inbound customer messages resume runs parked on a reply (hooked into the
  whatsapp + messaging webhooks via `flows_engine.on_inbound_async`, off the
  ack path). Per-contact execution state lives in `crm.FlowRun`
  (`GET /api/flows/runs` to inspect). Action channels: WhatsApp/Email/Telegram
  send for real or sandbox with `{{variable}}` rendering from the CRM
  (Telegram texts every linked telegram thread — no phone addressing), AI
  voice call via lazy `calls.lk` OR — node config `callVia: "WhatsApp
  (Baileys)"` — an automatic WhatsApp voice call through `baileys_calls
  .start_call` on the contact's sticky/least-loaded Baileys account (agent
  voice from node config `voice` = an ElevenLabs voice id → Setting
  `flow_call_voice` global default → native realtime; `engine._flow_call_voice`,
  builder field type `voiceselect` fetching `GET /api/voices`, same voice
  source as the /chats manual call picker), **Script call** = the one-way
  "dial, read a `crm.ScriptTemplate` verbatim, hang up" bot call (no LLM
  conversation, no listening — `engine._dial_script`, node config
  `scriptTemplateId`, library at Settings → Script Templates /
  `GET/POST /api/calls/script-templates`); it honours the SAME `callVia` as
  the AI voice call, so a script call runs over SIP **or** a linked WhatsApp
  number — the script rides the agent's `script` dispatch metadata key on
  both transports, so neither the Baileys gateway nor agent.ts needs a
  per-transport branch, and `baileys_calls.start_call`'s compliance gates
  (opt-out absolute, cold-call guard) apply unchanged. Script calls force a
  literal-TTS voice (`engine._script_call_voice` auto-picks a ready ElevenLabs
  clone when nothing else chose one) because native realtime models have no
  standalone TTS and have been observed to emit NO audible speech when asked
  to read verbatim. In-app
  push → Notification; SMS stays log-only (no provider). Trigger `when`
  options include "Days overdue (exactly)" (od == days — pinpoint targeting,
  unlike the ≥ default). `drain()` holds a non-blocking in-process lock AND
  claims each job atomically (queued→"sending" UPDATE, stale-claim reaper
  after 10min) — two tickers (manual tick vs ticker, or a mid-reload zombie
  runserver, both observed live) used to double-send the same job.
  `_flow_owns_reply` (whatsapp + messaging) blocks the AI when a run is
  parked on a reply OR has CONSUMED the thread's latest inbound — without
  the latter the AI text-answered the same message the flow already handled
  (landed while the flow's auto call was ringing). AI replies
  (aicore/assistant.py) render leaked
  `{{placeholders}}` through the same `variables_for` catalog before
  returning. ⚠ The /flow-builder page's library PUT PRUNES server-side
  workflows missing from the browser's copy — after creating a Workflow row
  outside the UI, the user must refresh /flow-builder before its next sync.
  Decision "Contact's answer" classifies replies with keyword lists
  (EN + BM) — deterministic on purpose, keep it that way for tests.
  **WhatsApp action sends are QUEUED, not inline:** `flows/queue.py` drains
  `crm.OutboundJob` on the same ticker with human-like pacing — Setting keys
  (env fallback; the gap pair is dashboard-editable from Settings → AI
  Configuration → Send Pacing) `queue_min_gap_s`=10 / `queue_max_gap_s`=30 (jittered
  per-account gap, derived from `OutboundJob.sent_at` so restarts keep the
  clock), `queue_daily_cap`=200, `queue_active_cap`=8 +
  `queue_active_window_h`=24 (a thread awaiting reply holds a conversation
  slot; an inbound frees it — that's the pause/resume behaviour). Account
  resolved AT SEND TIME via `whatsapp/routing` (+ health failover),
  role-aware: text jobs route role="conversation", template jobs
  provider="cloud" preferring role="reminder"; a job may pin its account via
  payload `account_id` (`_pinned_account`, used by the business redirect),
  and message attribution honours payload `source` ("system" for redirects,
  default "ai"). Job kinds: text | template | **ai_reply** (queued AI
  replies — guards re-checked at drain, reply generated at send time via
  `ai.generate_reply(thread)`, sent through the thread's own account;
  guard-declined jobs go `cancelled`, not failed) | **call**. **Calls are
  queued too** (crm migration 0019): a WhatsApp number carries exactly ONE
  call and the gateway 502s a second dial, so the engine's call nodes
  enqueue (`engine._enqueue_call`, payload `{transport, script,
  voice_provider, voice, extra}`) instead of ringing inline — two runs
  reaching their call step in the same tick used to collide and the loser
  failed permanently. `queue._place_call` serialises on
  `baileys_calls.account_call_busy` (**gateway-authoritative** — thread rows
  aren't reliably attributed to the calling account, so a DB-first check
  reports "free" mid-call; stale busy rows are reconciled there), and a busy
  number is a WAIT (job stays queued, no attempt burned), never a failure.
  Only compliance refusals (opt-out, cold-call guard, do-not-call —
  re-checked at dial time) fail a call for good. `sent` means DIALLED, not
  completed. Settings `queue_call_gap_s` (60, post-call breather) /
  `queue_call_daily_cap` (50). `transport:"sip"` jobs have no liveness
  endpoint so they pace by gap + one dial per pass. When a call can't be
  placed the queue also **un-parks its flow run** (`_unpark_run`) — the run
  waits on a reply only the call could prompt, and a parked run blocks
  `_already_ran` from ever re-enrolling that contact. Quiet
  hours push `not_before`; opt-out fails the job; the cold-text guard fails
  text jobs into zero-inbound conversation-number threads (Setting
  `cold_text_guard`); text jobs fail on a closed
  24h window while **template action nodes pass it** — builder saves
  `waMode:"template"` + `templateName`/`templateLanguage`/`templateVariables`
  on the node, engine renders values via `variables_for` ({{name}},
  {{amount_due}}, {{days_overdue}}, {{due_date}}, {{payment_link}},
  {{company}}) and enqueues; Baileys templates send as rendered free text.
  Node config `allLinked` (builder's "Send to" switch on Action steps, shown
  when WhatsApp or Email is selected; legacy key `waAllNumbers` still
  honoured) fans the send out to EVERY linked identity on the step's
  channels — one queue job per WhatsApp number (deduped on normalized
  digits, per-thread compliance gates apply per number) and one email per
  linked address (deduped case-insensitively vs the CRM email). AI voice
  calls always dial the primary number — no multi-dial.
  Deliveries land in `FlowRun.context["deliveries"]`; inspect jobs via
  `GET /api/flows/queue`. Default library workflow `wf-repayment-reminder`
  ships inactive in `lib/flow/defaultWorkflows.ts`.
- **Custom fields reach templates as variables** (2026-08-15). The operator's
  Custom Fields (Settings → Custom Fields, `custom_fields` Setting row) are
  merged into `FlowEngine.VariablesFor` as `{{cf_<key>}}` (customer fields, off
  `Contact.Attributes`) and `{{provider_<key>}}` (the service provider bound via
  an active `ResourceLink`, off `Resource.Attributes`). The prefixes are
  deliberate — a field labelled "Company"/"Email" would otherwise shadow a
  built-in. Values format like the dashboard does (boolean → Yes/No,
  multiselect → comma list); a missing value renders empty, never the literal
  placeholder. Both template editors share
  `components/settings/TemplateVariablePicker.tsx` (built-in chips + a live
  custom-fields group) instead of their old hardcoded arrays. Also filled the
  gaps the pickers were already advertising: `{{link}}`/`{{tracking_url}}` (off
  `Record.Data`), `{{account_number}}`, `{{best_contact_time}}`,
  `{{preferred_channel}}`, `{{last_contact_date}}`.
- **Communication hub + analytics** (`calls/views.py`):
  `GET /api/contacts/<id>/channels` = the per-contact unified channel profile
  (identities across whatsapp/email/social + voice, carrying account + its
  health, in/out counts, last active, 24h-window + opt-out state; reuses
  whatsapp.store's own window helpers so numbers match the inbox) → rendered
  by `components/ChannelHub.tsx` in the `/chats` info panel and
  `/contacts/[id]`. `POST` on the same URL ({channel, address}) registers an
  identity by hand — creates/claims the thread via the channel's own store
  and links it (409 if linked to another contact); the contact page's hub shows
  the add form (`ChannelHub allowAdd`), the /chats "Contact" picker stays the
  link-an-existing-chat seam. `GET /api/analytics/summary?days=` powers `/analytics`
  (message funnel with documented replied-approximation, repayment
  conversion, per-account volumes incl. the null-account bucket, call
  outcomes, `account.health` AuditLog trail, zero-filled daily series).
  `GET /api/analytics/usage?days=` = LLM metering/billing (crm.LlmUsage rows
  written by `aicore/usage.py record()` at EVERY LLM call site — chat replies
  incl. tool rounds, memory/timeline summaries, playground, voice calls via
  agent metrics → live store llm_usage → `wa_calls.save_call_transcript`;
  costs estimated at READ time from Setting `llm_prices` (USD per 1M tokens)
  over `usage.PRICES_DEFAULT`, per-contact breakdown for customer billing;
  "AI usage" card on /analytics renders it).
- **`backend/integrations/` = third-party integrations** (`/api/integrations/`;
  Settings → Integrations). `Integration` is `ChannelAccount`'s twin: creds in
  a JSONField that **never leave the server unmasked**, `config` for
  per-provider settings, and `status` written ONLY through
  `integrations/health.py set_status` (one AuditLog + one admin+ Notification
  per real transition; never-connected rows transition silently). Providers:
  Google Calendar / Drive / Gmail / Sheets (**OAuth2, one Google client,
  incremental per-product scopes, PKCE S256 + a signed 600s `state`** —
  `integrations/oauth.py`; the callback is the only open non-webhook route
  because a browser back from Google carries no bearer token, so the signed
  state IS the CSRF control), plus Shopify + Stripe as **API-key** providers
  (Shopify's own single-store path is a custom-app Admin token; Stripe OAuth
  is Connect, a marketplace feature — neither fits a template). Token refresh
  is LAZY at call time and a failed refresh transitions status to
  `disconnected` and returns `""` — it never throws into a send path. Never
  log tokens/secrets/provider bodies. Global keys (Google client, webhook
  signing secrets) live in Setting `integrations_settings` with .env
  fallback, masked round-trip, PUT superadmin — same pattern as
  `voice_twilio_settings`. **Both webhooks verify HMAC**
  (`integrations/webhooks.py`: Shopify base64 `X-Shopify-Hmac-Sha256`, Stripe
  `t=…,v1=…` + a 300s replay window, both `compare_digest`) and **no secret
  configured ⇒ 503, never "accept unsigned"** — the explicit non-repeat of the
  Messenger/IG gap. `integrations/mapping.py` turns `orders/*` and
  `payment_intent.succeeded` into `crm.Record` + `crm.RecordEvent` (phone-first
  then email contact matching, creating a Contact when the payload carries an
  identity; `book_event(dedupe_key=…)` makes redelivery idempotent; Stripe
  `metadata.record_external_id` attaches a payment to the EXISTING order).
  Flow-builder action nodes (`integrations/actions.py`, dispatched by
  `flows/engine.py _do_integration` off the Action node's `channels` list):
  Create calendar event · Find free slot · Upload file to Drive · Append row
  to Sheet · Send via Gmail · Update order status · Create Stripe payment
  link. **Sandbox is mandatory**: with zero credentials every endpoint answers,
  `oauth/start` 400s with an operator-facing reason instead of opening a
  socket, and every action returns `{ok: false, skipped: true, detail: …}` so
  the step logs a clear skip and the run continues. The seven action labels are
  a contract shared with `frontend/src/lib/flow/nodeConfig.ts
  INTEGRATION_ACTIONS` — change both sides together.
- **Integrations are per USER, per entity, or global (2026-08-16).**
  `Integration.OwnerUserId` (nullable) is what makes "different people,
  different Google Calendars" possible: a connect flow writes to the CALLER's
  own row (`IntegrationResolver.OwnRowAsync`), never an ownerless one, so a
  second user can no longer overwrite the first's tokens. Resolution at call
  time is **the acting user's grant → the entity's shared grant (then its
  subtree) → the organization-wide one** (`IntegrationResolver.Active`); only
  rows that actually carry credentials count.
  `ChatBranch.Data/Integrations/`: `GoogleOAuth.cs` (PKCE S256, HMAC-signed
  600s state, lazy refresh that marks a row disconnected and returns "" rather
  than throwing), `IntegrationActions.cs` (the seven flow actions, executed for
  real; a missing connection SKIPS so the run continues), `IntegrationSettings.cs`
  (the org-wide keys live in ONE JSON Setting row `integrations_settings` — a
  plain `SettingValue("google_client_id")` finds nothing, which is exactly how
  the OAuth service first shipped believing no client was registered; both the
  API and the OAuth service now read this helper). The callback
  `/api/integrations/oauth/google/callback` is deliberately ANONYMOUS — the
  browser arrives from accounts.google.com with no bearer token, so the signed
  state IS the CSRF control. Set `GOOGLE_REDIRECT_URI` (must match the Google
  console exactly) and `FRONTEND_URL` (where the callback bounces back to) in
  production; both fall back to localhost.
- **Docs:** `docs/vertical-guide.md` = how to configure the template for an
  industry (the five layers: hard rules → persona → skills → knowledge →
  memory). `docs/DATABASE_SCHEMA.md` = the real schema.
  `docs/DOMAIN_MODEL.md` = the four core concepts (Entity/Tenant+Brand ·
  Resource/provider · Customer/Contact · Admin/Profile) + how they bind.
  `docs/API_CONVENTIONS.md` = both REST surfaces + which endpoints are open.
  `docs/RBAC_AND_BRANDS.md` = roles/tenants/brands + dynamic capabilities.
  `docs/record-sync.md` = the bulk `/api/v1/sync/records` pipeline.
  `docs/channel-adapter.md` = per-channel capabilities + how to add one
  (**Messenger/IG webhook has NO signature check — real, open gap**).
  `backend/baileys_service/
  RESEARCH.md` = Baileys production research: DisconnectReason table, ban
  evidence (<2% inbound-first vs 15–30% cold outbound), pacing numbers the
  queue defaults come from, and unused native pre-ban telemetry
  (`reachoutTimeLock`, `message-capping.update`) worth wiring next.

## Frontend conventions

- **`src/lib/AppContext.tsx`** holds all shared state + the real-call seams.
  Pages consume `useApp()`. API base = `NEXT_PUBLIC_API_URL` (default
  `http://localhost:8000/api`); falls back to `src/lib/mocks.ts` data if the API
  is down.
- **Calls are multi-concurrent.** State is a `calls: Record<room, LiveCall>` map
  plus a waiting `queue`, not a single call. Dial seams: `dialContact(id)`,
  `dialManual(phone,name,language)`, `launchBatch(ids)` (enqueue many). A pump
  effect starts queued dials as slots free up. **All per-call actions are
  room-scoped:** `sendSay(room,…)`, `hangupCall(room)`, `escalateCall(room)`,
  `setHumanControl(room,v)`, `injectResponse(text,room?)`, `resetCall(room?)`.
  `selectedRoom`/`selectCall` track the focused call.
- **Concurrency cap = `MAX_CONCURRENT`** (`NEXT_PUBLIC_MAX_CONCURRENT`, default
  **3**) — set it to your SIP trunk's channel limit. `startDial` inserts a
  placeholder synchronously (status `dialing`, temp key `pending-N`) so it counts
  toward the cap *before* the network round-trip, then re-keys to the real room
  id; this prevents a batch from overshooting the cap.
- **The call UI lives in `/chats`, not a dedicated page.** `CallLauncher`
  (dial + agent-voice picker), `VoiceCallButton` / `ScriptCallButton`,
  `ActiveCallsStrip` (every live call at a glance) and `VoiceCallPanel`
  (transcript + Guide/Takeover/Escalate/End) are all in
  `components/chats/`; `CampaignModal` enqueues a batch. There is **no**
  `/live-calls` route and no `CallPanel` component — older notes referring to
  a monitor grid describe a page that no longer exists. AI insight
  sentiment/highlights are **static mock**; KB refs are real from `turn.kb`.
- **Settings IA:** every tab is its OWN ROUTE (`/settings/<id>` — refresh
  keeps your place): tab registry + chrome in
  `components/settings/SettingsNav.tsx` (`SETTINGS_TABS` + `SettingsShell`,
  rendered from `app/settings/layout.tsx`), one thin
  `app/settings/<id>/page.tsx` per tab. `/settings` itself only REDIRECTS —
  legacy `?tab=x` links (backend notification links use them, incl. the
  pre-consolidation `whatsapp` id) map through `LEGACY_TABS`. New tab = a
  `SETTINGS_TABS` entry + a route page. ONE consolidated **Channels** tab
  (`components/settings/ChannelsSettings.tsx`) is where all provider accounts
  are added — pick WhatsApp / Telegram / Messenger / Instagram / Email, add N
  accounts per channel. The Meta template manager (`TemplatesSettings`) lives
  INSIDE the WhatsApp panel; the global "Message Templates" tab is the
  cross-channel CannedResponse manager (`SharedTemplatesSettings`). Don't
  re-add per-channel top-level tabs. Three tabs were added in the
  integrations/knowledge pass: **Integrations** (`IntegrationsSettings.tsx`,
  provider card grid), **Knowledge Base** (`KnowledgeSettings.tsx`) and
  **AI Skills** (`SkillsSettings.tsx`, the sole writer of `crm.AgentSkill`).
  The Escalation Rules tab was removed
  2026-07-22 (it was mock-only; escalation is modelled in the flow builder
  instead — don't re-add it). The **AI Configuration** tab was removed
  2026-08-15: its real rows (quiet hours, send pacing, voice-call LLM/STT)
  became PER-ACCOUNT settings in Channels → edit account → "Delivery & AI",
  and its remaining `MockRows` demo rows went with it —
  `AiChatSettings.tsx` / `AiConfigDemoRows.tsx` / `LlmProviderSelect.tsx` /
  `SttProviderSelect.tsx` are deleted, `/settings/ai` redirects to
  `/channels`. Don't re-add the tab. Settings → Notifications is real
  (server-backed).
- **Settings is system configuration ONLY.** Content the operator authors lives
  in the sidebar: Knowledge Base (`/knowledge`) and AI Skills (`/skills`) moved
  out of Settings 2026-08-16 (old routes + `?tab=` redirect), joining Message
  and Script Templates under Library. The **Chat Persona** tab was removed
  entirely — the assistant ships `DEFAULT_PERSONA` and is shaped through Skills
  and Knowledge; `/settings/persona` redirects. Analytics / Call Logs /
  Activity are three sidebar destinations, NOT a tabbed section — the shared
  `ReviewTabs` strip is deleted, don't re-add it.
- **A modal's errors render INSIDE the modal.** `Modal` portals over a
  `backdrop-blur-sm` scrim, so a page-level `{msg && …}` under an open dialog is
  invisible — the "password too short" report. The pattern (WhatsApp account
  editor, Users, Entities, both template editors): a `role="alert"` div as the
  first child of the modal body for `!msg.ok`, the page-level copy gated on the
  modal being closed, and `setMsg(null)` when opening one.
- **Shell (2026-10-08):** the sidebar IA lives in `components/shell/nav.ts`
  (`NAV_GROUPS`, `canSee`) — a new page = one entry there; the command palette
  searches the same list plus `GET /api/search` (each type capability-gated
  and scoped like its list endpoint). `/api/live` + `lib/chatStream` (ONE SSE
  connection per tab, now with a connection status) feed the Live chip, the
  sidebar's Chats counter and Analytics' waiting card via `liveStore`.
  Analytics is home (`/`, post-login, `/dashboard` all go there). PWA:
  `app/manifest.ts`, hand-written `public/sw.js` (never caches `/api/*` or
  the stream; offline navigations redirect to `/offline?from=`), icons in
  `public/icons/` rendered from `Logo.tsx`'s `brandMarkSvg`. Channel health
  transitions write `account.health` audit rows via `AccountHealth.SetStatus`.
- **Never call `getRole()` / `getAuth()` during render.** They read
  localStorage, which doesn't exist during SSR — the server paints the
  `'agent'` fallback, the client paints the real role, and React aborts
  hydration ("Expected server HTML to contain a matching `<svg>` in
  `<button>`") and re-renders the whole root. Read the role **after mount**:
  either `useRole()` from `src/lib/useRole.ts` (returns
  `{ role, ready }` — `'agent'`/`false` until the mount effect runs; gate
  admin UI on `role`, use `ready` to render nothing for a frame instead of
  flashing a "not allowed" notice) or the older
  `useState(false)` + `useEffect(() => setCanManage(getRole() !== 'agent'), [])`
  pattern still used across `components/settings/`. Both are hydration-safe;
  the direct-call form is not.
- **`src/lib/mocks.ts`** is the type + mock-data source of truth (`Contact`,
  `RecordDto`, `TranscriptLine {who,t,kb}`, `ScheduledCall`, etc.) AND the
  offline fallback the pages render when the API is unreachable. It was once
  lost to a `.gitignore` `lib/` rule — `.gitignore` now explicitly un-ignores
  `frontend/src/lib/`. Don't re-break that.

### Design & theming (light/dark)

- **The design language follows Whatomate** (the open-source WhatsApp platform
  this app's WhatsApp module was ported from): near-black neutral surfaces
  (`#0a0a0b`/`#0f0f10`), zinc text scale, and **emerald as the single brand
  accent** — the Tailwind `brand` scale maps to emerald, NOT blue. Shell =
  left **sidebar** (`components/shell/SideNav.tsx`, grouped Overview → Work →
  Configure → Review → Settings) + a `TopBar`. `Shell.tsx` `FULL_PAGE` lists
  the routes that break out of the shell entirely (`/flow-builder`,
  `/settings`) and own their own back/save chrome. A new page needs a
  `NAV_GROUPS` entry in `SideNav.tsx`.
- Colors are **CSS variables** in `src/app/globals.css`: `:root` = dark (default),
  `:root.light` = light — the **class strategy** (`html.dark` / `html.light`,
  Tailwind `darkMode: 'class'`), NOT a data-theme attribute. Tailwind semantic
  tokens resolve to them: `app / surface / card / text-* / line / line-strong /
  fill / fill-hover / fill-subtle`. **Use these tokens, not raw `white/α` or
  hardcoded slate hex** — raw white-alpha doesn't flip and vanishes on light
  backgrounds.
- Theme is toggled by `ThemeToggle` (sidebar footer) → swaps the class on
  `<html>` + `localStorage`. A pre-paint script in `layout.tsx` applies it
  before render (html ships with `className="light"` +
  `suppressHydrationWarning` — the script's class swap on a stored 'dark'
  choice is an intentional pre-hydration mutation).
- Full-height pages (`/flow-builder`, `/chats`) size themselves with
  `h-[calc(100dvh-98px)]` — 98px = 58px header + 40px main padding; update
  both if the shell chrome changes.
- **Never put `var(--…)` in a lucide `color=` prop** (or any SVG presentation
  attribute) — `var()` doesn't resolve there and the icon renders invisible. Use
  a static hex for icons, or a Tailwind text class. For inline **text** accent
  colors that must flip, use the `--brand-ink / --amber-ink / --green-ink /
  --red-ink` vars. Pale accent text classes (`text-brand-200`, `text-*-300`) are
  darkened in light mode via scoped overrides at the top of `globals.css`.

## Voice agent gotchas

- **Node/TypeScript worker** (`backend/voice_agent/src/`): `agent.ts` (entry,
  call lifecycle, watchdogs), `session.ts` (provider models), `prompts.ts`,
  `storage.ts` (file IPC), `metadata.ts` (the dispatch-metadata contract — keep
  it in step with the API's `VoiceDispatch.cs`), `config.ts` (env from
  `backend/.env` + validation), `audioGate.ts`, `recorder.ts`. Run it with
  `npm run dev` / `npm start` in that folder (or `npm run dev:agent` at the root).
- **Pin LiveKit packages to matching versions.** Every `@livekit/agents*`
  package is pinned to the same exact version (`1.9.1`) — skew between them
  breaks imports. Bump them together, then `npm run selftest`.
- **The API is the control plane.** `/api/calls/dial` and WhatsApp call/start
  dispatch the agent (by `AGENT_NAME`) through `Sidecars/LiveKitClient.cs`
  (HS256 tokens + Twirp JSON); `/hangup` deletes the room. No LIVEKIT_* or
  `SIP_OUTBOUND_TRUNK_ID` → a 503 that names what to set, never a fake call.
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
  `takeover` → `session.say` (verbatim), plus a max-duration failsafe and
  auto-hangup after the goodbye.
- **Greeting is gated on the callee answering.** `awaitAnswer()` waits for
  `sip.callStatus == "active"` (SIP) or `wa.callStatus == "active"` (the
  Baileys bridge) before the agent speaks. The WhatsApp bridge joins the
  room EARLY (during ringing, attribute "ringing") so pickup→greeting is
  ~1-2s instead of ~8s; the Baileys gateway gates audio on `this.active` so
  the agent never hears ringback. After the flip, `ANSWER_SETTLE_MS`
  (default 1500) adds a human beat before the opening.
- **Call endings have three layers:** the `end_call` tool; a marker failsafe
  (realtime models sometimes SAY "{end_call}" instead of invoking it —
  agent.ts detects the text, scrubs it from the transcript AND from what the
  cascade TTS speaks, hangs up); and a contact-silence watchdog
  (`SILENCE_NUDGE_SECONDS`=12 → re-ask, `SILENCE_HANGUP_SECONDS`=120 → forced
  hangup; only USER speech resets it).
- **Caller-hangup detection:** a `ParticipantDisconnected` room handler ends the
  call when the callee hangs up first (one-shot guard, then hang up).
- **Call provenance:** every hangup site records who ended the call
  (`END_CAUSE` → live store `ended_by`: agent / contact / system:silence /
  system:max_duration / system:no_answer / system:script_complete /
  system:tts_unavailable), written with the final live write after the LLM
  summary. An operator hang-up through the API deletes the room, which the
  agent records as `contact`.
- **Prompts inject the real date** via `prompts.today()` (the model otherwise
  assumes its training cutoff and misreads "next Friday"/overdue math). The
  **call language comes from the contact** — `languageDirective` opens and
  conducts the whole call in `contact.language` (`en`/`ms`/`zh`), switching only
  if the callee clearly uses another.
- **Smoke-testing without a phone line:** a local `livekit-server --dev` plus an
  OpenAI-compatible mock as `ILMU_BASE_URL` (voice_provider `ilmu`) exercises a
  full cascade call — dispatch, answer gating, inbox, endings, calls.json.

## Flow builder

- The **visual flow builder** lives in the dashboard: route `/flow-builder`,
  components `frontend/src/components/flow/`, data model
  `frontend/src/lib/flow/` (uses `@xyflow/react`; the library syncs to the DB
  via `/api/flows/library` and `backend/flows/` executes Live flows —
  localStorage is only the offline fallback). **Agent skills are READ-ONLY on
  this page** — Settings → AI Skills owns them (see the aicore bullet);
  `lib/flow/defaultAgentSkills.ts` is now only the offline fallback for the
  Action node's picker and the "Load starter skills" button. Its variable catalog
  (`lib/flow/variables.ts`) must stay in step with
  `flows/engine.py variables_for()`. Styles are token-based in
  `src/app/flow-builder/flow-builder.css`, scoped under `.flow-studio` — same
  theming rules as the rest of the frontend.

> There is no `handbook/` directory. The domain SOP that used to live there was
> removed in the template conversion; `docs/vertical-guide.md` is what replaces
> it — how an operator configures a vertical without forking.

## Verifying changes

- Frontend types: `cd frontend && npx tsc --noEmit` — clean, no known
  exceptions. Lint with `npx eslint src`. (There is **no**
  `frontend/__tests__/`; jest is configured but has no test files.)
- CI runs all of this on push/PR (`.github/workflows/ci.yml`): API build+tests,
  dashboard types/lint/build, the gateway's offline call-engine self-test, and
  the voice agent's types + offline self-test.
  Containers: `docker compose up` builds `backend-csharp/Dockerfile` +
  `frontend/Dockerfile` (the gateway sits behind the `whatsapp` profile, the
  voice agent behind `voice`). SQLite is a FILE — the API's /data volume is not
  optional, and the voice agent shares it.
- Backend: `cd backend-csharp && dotnet build src/ChatBranch.Api -c Release`
  then `QUEUE_WORKER=0 dotnet test tests/ChatBranch.Tests` — 56 tests covering
  auth/RBAC, the inbox, webhooks (incl. wamid dedup and the QUEUED ai_reply
  that Send Pacing acts on), contacts, flows + the outbound queue, analytics,
  settings, custom fields and the LiveKit control plane. `QUEUE_WORKER=0`
  stops the drain thread racing the assertions. Keep them green.
- The API rebuilds and reseeds against `src/ChatBranch.Api/chatbranch.sqlite3`;
  new columns go in `Seed.UpgradeSchema` (append-only, idempotent
  `ALTER TABLE ADD COLUMN`) because `EnsureCreated()` never alters a live
  schema.
- Voice agent: `cd backend/voice_agent && npx tsc --noEmit && npm run selftest`
  (offline — no LiveKit server or keys).

## Notes

- Dev slowness is normal **Next.js on-demand route compilation** (each route
  compiles on first visit). `Documents\` here is local, NOT OneDrive-synced, so
  that's not the cause. Use `next dev --turbo` or a prod build for speed.
- CodeGraph MCP is available (`codegraph_*`) — prefer it for structural queries.
