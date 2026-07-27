# Database schema

The **actual** shipped schema, verified against `backend/crm/models.py`,
`backend/ingest/models.py` and `backend/integrations/models.py`.

Engine: **SQLite by default** (zero-config), via the Django ORM. Setting
`DATABASE_URL` to a `postgres://` URL switches the ORM to Postgres — nothing
beyond the ORM has been tuned for it, and there is no row-level security
(see [Isolation](#isolation-and-what-it-does-not-do)).

Three apps own tables:

| App | Owns |
|---|---|
| `crm` | everything operational — contacts, records, conversations, accounts, AI config, queue, audit |
| `ingest` | multi-tenancy roots + the external-sync landing zone |
| `integrations` | third-party provider connections |

Migrations were squashed to a clean initial for the template release:
`crm/0001_initial` + `crm/0002_knowledgeitem`, `ingest/0001_initial`,
`integrations/0001_initial`. `makemigrations --check --dry-run` reports no
changes.

---

## The shape in one picture

```
Tenant ──┬── Brand ──── DataSource ──── SyncBatch ──── RecordSyncStaging
         │     │                                              │ normalizer
         │     └──────────── ContactBrand ─┐                  ▼
         ├── Profile (RBAC, M2M → Brand)   │        Contact ──── Record ──── RecordEvent
         ├── KnowledgeItem ────────────────┘           │  │
         └── Integration                               │  ├── Thread ──── Message
                                                       │  │      ▲            │
                                                       │  │      └ ChannelAccount
                                                       │  ├── AccountAssignment
                                                       │  ├── CallRecord
                                                       │  ├── ConversationSummary
                                                       │  ├── PendingToolAction
                                                       │  ├── FlowRun ──── OutboundJob
                                                       │  └── (Workflow, AgentSkill: global)
                                                       └── Setting, CannedResponse,
                                                           ScriptTemplate, AuditLog,
                                                           Notification  (global)
```

Two naming notes that will otherwise confuse you:

- **`Message.source` still has the value `"customer"`** (choices:
  `customer | agent | ai | system`). That is *who wrote the message* — a speaker
  role, not a pointer at a model. Everything that points **at** a `Contact` is
  called `contact` / `contact_id`, including every JSON wire key.
- Several tables are deliberately **not** tenant-scoped: `Thread`,
  `ChannelAccount`, `Message`, `CallRecord`, `Workflow`, `AgentSkill`,
  `Setting`. They are global to the installation. Tenanting them is an open
  piece of work, not a shipped feature.

---

## 1. Tenancy, identity, RBAC

### `ingest.Tenant`
The organization using the platform. Root of isolation.

| Field | Type | Notes |
|---|---|---|
| `name` | char(160) | |
| `slug` | slug(80), **unique** | the id used in API payloads |
| `status` | char | `active` \| `suspended` — a suspended tenant's sync is rejected |
| `created_at` / `updated_at` | datetime | |

### `ingest.Brand`
A client brand a tenant operates. Access control is brand-level.

`tenant` FK · `name` · `slug` · `is_active` · timestamps.
**unique together** `(tenant, slug)`.

### `crm.Profile`
Attaches a role + API token to a Django `auth.User` (one-to-one).

| Field | Type | Notes |
|---|---|---|
| `user` | O2O → auth.User | |
| `role` | char(12) | `superadmin` \| `admin` \| `agent` |
| `token` | char(64), indexed | bearer token, **rotated on every login** |
| `tenant` | FK → Tenant, null | the user's org |
| `brands` | M2M → Brand | agents only; superadmin/admin access is derived |

### `crm.ContactBrand`
Which of a tenant's brands a contact is active on. The contact profile is shared
tenant-wide; this records the per-brand association.

`contact` FK · `brand` FK · `external_ref` · `created_at`.
**unique together** `(contact, brand)`.

### `crm.AuditLog`
`user` FK(null) · `actor` (username \| `system` \| `webhook`) · `action`
(e.g. `login`, `settings.update`, `account.health`, `integration.status`) ·
`target` · `detail` · `ip` · `created_at`. Ordered newest first.

### `crm.Notification`
`user` FK · `kind` (`message` \| `call` \| `system`) · `title` · `body` ·
`link` · `read` · `created_at`. Fanned out by role; muting is per category via
`Setting` rows `notif_<category>`.

---

## 2. The two core business tables

### `crm.Contact` — the person
The identity side. Anything vertical-specific about *why* you are talking hangs
off `Record`; anything vertical-specific about the *person* goes in
`attributes` / `tags`.

| Field | Type | Notes |
|---|---|---|
| `name` | char(120) | required |
| `phone` | char(32) | E.164. **The identity key everywhere.** Unique per tenant, not globally |
| `email` | email | |
| `language` | char(8) | `auto` \| `en` \| `ms` \| `zh` — drives the voice agent's opening language |
| `status` | char(16) | `active` \| `pending` \| `message-sent` \| `resolved` \| `failed` |
| `method` | char(12) | `ai-call` \| `bot-call` \| `message` — preferred outreach |
| `company_name` | char(120) | |
| `do_not_call` | bool | hard compliance gate on dialling |
| `notes` | text | renders as the `{{situation}}` flow variable |
| **`external_id`** | char(64), indexed | id in your system of record |
| **`tags`** | JSON list | `["vip","wholesale"]` — branchable in the flow builder |
| **`attributes`** | JSON dict | vertical custom fields; **visible to the AI** |
| `preferred_contact_time` | char(64) | written by the AI summarizer |
| `communication_style` | char(120) | written by the AI summarizer |
| `sentiment_trend` | char(16) | `positive` \| `neutral` \| `negative` |
| `ai_profile_summary` | text | written by the AI summarizer |
| `tenant` | FK, null | |
| `last_synced_at` | datetime | |
| `created_at` / `updated_at` | | ordered by `-updated_at` |

Constraints:
- `uniq_contact_tenant_external_id` — unique `(tenant, external_id)`, partial
  (only where `external_id != ""`).
- `uniq_contact_tenant_phone` — unique `(tenant, phone)`, partial (only where
  `tenant` is set).

### `crm.Record` — the thing the conversation is about
An order, a shipment, an invoice, a booking, a ticket. **This is the model a
vertical customises.**

| Field | Type | Notes |
|---|---|---|
| `contact` | FK → Contact | `related_name="records"` |
| `tenant` | FK, null | |
| `type` | char(16) | `order` \| `shipment` \| `invoice` \| `booking` \| `ticket` \| `custom` — **fixed choices** |
| `external_id` | char(64), indexed | order #, tracking #, invoice # |
| `title` | char(190) | |
| `status` | char(32), default `open` | **free text, your vocabulary** |
| `amount` | decimal(12,2), null | |
| `currency` | char(8), default `MYR` | |
| `due_date` | date, null | delivery ETA / due date / appointment. **Every time trigger is an offset from this** |
| `data` | JSON dict | line items, addresses, anything. `data["tracking_url"]` → `{{tracking_url}}`; `data["link"]` or `data["url"]` → `{{link}}` |
| `created_at` / `updated_at` | | ordered by `-updated_at` |

Constraint `uniq_record_tenant_type_external_id` — unique
`(tenant, type, external_id)`, partial.

`flows.engine.primary_record(contact)` picks the record a flow run and a
conversation are about: the first record **not** in
`CLOSED_RECORD_STATUSES = ("closed","done","resolved","cancelled","completed")`,
else the most recent of any.

### `crm.RecordEvent` — the record's timeline
`record` FK · `kind` (`status` \| `payment` \| `note` \| `scan`) · `at` ·
`amount` · `data` · `created_at`. Ordered `-at`.

Written by the sync normalizer on a status/amount change, and by the Shopify /
Stripe webhook mappers. `integrations.mapping.book_event(dedupe_key=…)` makes
webhook redelivery idempotent.

---

## 3. Conversations

### `crm.ChannelAccount` — a provider account
One row per WhatsApp number, Facebook page, Instagram account, Telegram bot or
SMTP mailbox.

| Field | Notes |
|---|---|
| `channel` | `whatsapp` \| `facebook` \| `instagram` \| `telegram` \| `email` |
| `provider` | `cloud` \| `baileys` (only meaningful for WhatsApp) |
| `name`, `external_id` | `external_id` = phone_number_id / page id / bot id / from-address |
| `waba_id` | WhatsApp Business Account id — needed to *list* templates |
| `access_token`, `app_secret`, `api_version` | Telegram puts the BotFather token in `access_token` |
| `config` | JSON. Email SMTP lives here; `config["role"]` = `reminder` \| `conversation` |
| `health_status` | `unknown` \| `healthy` \| `degraded` \| `disconnected` \| `banned` |
| `health_detail`, `last_ok_at`, `last_inbound_at`, `fail_streak`, `health_changed_at` | |

> **`whatsapp/health.py` is the only writer of the `health_*` fields.** Every
> send path, webhook, gateway push and the periodic checker funnels through
> `set_status`, which fans out one Notification + one AuditLog per real
> transition. Do not write these fields anywhere else.

`healthy` / `degraded` accounts are routable; `disconnected` / `banned` are
skipped and trigger failover of their assigned contacts.

### `crm.Thread` — one conversation per (channel, counterparty)

| Field | Notes |
|---|---|
| `channel` + `key` | **unique together.** `key` = wa_id digits / email / PSID / IGSID / TG chat id |
| `address`, `name` | |
| `status` | `open` \| `pending` \| `snoozed` \| `resolved` |
| `assignee`, `labels` | |
| `contact` | FK, null — the CRM join. **Auto-replies only fire on linked threads** |
| `account` | FK → ChannelAccount, null |
| `ai_enabled` | the account-less/sandbox fallback |
| `ai_overrides` | JSON `{account_pk: bool}` — **the real per-chat Chatbot tick, per sending account** |
| `opted_out` | STOP/UNSUBSCRIBE/BERHENTI seen; blocks AI + templates |
| `call_permission`, `call_id`, `call_status`, `call_room` | WhatsApp voice-call state |

The `/chats` SSE change token watches `Thread.updated_at` + message count — any
write path using a queryset `.update()` must also bump `updated_at` or the
dashboard will not refresh.

### `crm.Message`

| Field | Notes |
|---|---|
| `thread` | FK |
| `account` | FK, null — **which number carried this message**, denormalized at write time so attribution survives later account switches |
| `external_id` | wamid / mid / SMTP Message-ID / TG id |
| `direction` | `in` \| `out` |
| `source` | `customer` \| `agent` \| `ai` \| `system` (see the naming note above) |
| `private` | agent-only note, never sent |
| `text`, `status` | status: `sent`/`delivered`/`read`/`received`/`sandbox`/`failed` |
| `media_type`, `media_path` | served auth-gated via `/api/whatsapp/media/<pk>` or `/api/messaging/media/<pk>` |
| `created_at` | ordered ascending |

Constraint `uniq_message_thread_external_id` — unique `(thread, external_id)`,
partial (`external_id != ""`). **This is the webhook-redelivery idempotency
guarantee.**

### `crm.AccountAssignment` — sticky contact→account routing
`contact` FK · `channel` · `account` FK · `pinned` · timestamps.
**unique together** `(contact, channel)`.

Auto-created by `whatsapp/routing.py` (least-loaded healthy account) on first
outreach. `pinned` marks an operator's explicit choice: rebalancing skips pinned
rows, but **failover overrides even a pin** — a dead account must never block
follow-up.

### `crm.CannedResponse` / `crm.ScriptTemplate`
Both are named, reusable text with `{{variable}}` placeholders rendered from the
CRM at send time. `CannedResponse.short_code` (unique) is what the composer's
`/short_code` picker inserts on every channel. `ScriptTemplate` is for one-way
"dial, read verbatim, hang up" calls.

---

## 4. AI configuration

### `crm.KnowledgeItem`
Operator-authored knowledge, retrieved by `aicore/knowledge.py` into a bounded
`COMPANY KNOWLEDGE` prompt block.

| Field | Notes |
|---|---|
| `tenant`, `brand` | `brand=null` = every brand. Brand scoping stops one brand's private notice leaking into another's conversation |
| `kind` | `fact` \| `policy` \| `faq` \| `event` \| `notice` — decides the retrieval tier |
| `title`, `question`, `content` | `question` is the FAQ's *asked* form |
| `tags` | JSON list — keyword matching hints |
| `channels` | JSON list, `[]` = every channel |
| `priority` | int, ties broken high-first |
| `active` | |
| `starts_at` / `ends_at` | null = always live. **This is what makes a "sudden notice" self-expire** |
| `created_by`, timestamps | |

Ordered `-priority, -updated_at`; index on `(kind, active)`.

Retrieval is **keyword + recency + priority — no embeddings**, the same
deliberate choice as `aicore/memory.py`. Tiers: all live `notice` → all live
`event` → all live `fact` → top-4 keyword-matched `faq`/`policy`. Hard budget
1800 chars, per-item truncation labelled. Zero keyword hits contributes nothing
rather than padding.

### `crm.AgentSkill`
`skill_id` (unique) · `name` · `use_when` · `instructions` · `position`.

> ⚠ **`GET/PUT /api/aicore/skills` is the sole writer.** `PUT
> /api/flows/library` used to upsert *and prune* these rows, so a stale
> flow-builder tab could silently delete a skill an operator had just typed. The
> library PUT now ignores the `agentSkills` it still receives, and the builder's
> skill sidebar is read-only. Do not add a second writer.

### `crm.ConversationSummary` — AI memory
`contact` FK · `source` (`chat` \| `call`) · `channel` · `ref` (thread key /
call id) · `summary` · `facts` (JSON list) · `sentiment` · `created_at`.

Generated after calls and after idle chats. Durable facts are written back onto
the `Contact` (`preferred_contact_time`, `communication_style`,
`sentiment_trend`, `ai_profile_summary`).

### `crm.PendingToolAction` — AI tool double-confirmation
`contact` FK · `channel` · `tool` · `fingerprint` (canonical JSON of the args,
minus the `confirmed` flag) · `session` (the reply-generation round that asked)
· `asked_at`. Unique on (`contact`, `channel`, `tool`).

The enforcement state behind `aicore/tools.py`'s double-confirm gate for
state-changing tools (`_MUTATING`, e.g. `book_appointment`): the model's first
call records a row and is refused with "ask the customer"; only a confirmed
re-call from a LATER reply round with the same fingerprint executes, and the
execution CONSUMES the row (atomic delete — a replayed confirm re-asks instead
of double-firing). Rows expire after 30 minutes (`_CONFIRM_TTL_S`), pruned
opportunistically on each gate pass. Written only by `aicore/tools.py`.

### `crm.Setting` — runtime config
`key` (unique) · `value` (text) · `updated_at`.

A row overrides the matching `backend/.env` default; blanking it falls back to
env. Notable keys: `aicore_persona`, `voice_twilio_settings`,
`integrations_settings`, the business variables (`business_name`, `agent_name`,
`support_number`, `support_email`, `website`), the queue knobs
(`queue_min_gap_s`, `queue_max_gap_s`, `queue_daily_cap`, `queue_active_cap`,
`queue_active_window_h`, `queue_call_gap_s`, `queue_call_daily_cap`), the
routing rules (`ai_reply_mode`, `business_redirect`, `recovery_mode`,
`cold_text_guard`), and `notif_<category>`.

Read these through their accessor functions (`wa_config.*`, `catalog.settings()`,
`health.voice_settings()`, `persona.persona_text()`) — never re-add module-level
constants, or dashboard edits will need a restart.

---

## 5. Automation

### `crm.Workflow`
`flow_id` (unique, builder id `wf-…`) · `name` · `hint` · `nodes` (JSON) ·
`edges` (JSON) · `active` · `position` · `updated_by` · timestamps.

The React Flow JSON is stored **verbatim**. The builder is the only writer;
`flows/engine.py` is the only interpreter. Only `active` workflows execute.

> ⚠ `PUT /api/flows/library` **prunes** server-side workflows missing from the
> browser's copy. After creating a Workflow row outside the UI, refresh
> `/flow-builder` before its next sync.

### `crm.FlowRun`
`workflow` FK · `contact` FK(null) · `trigger_key` · `status`
(`running` \| `waiting` \| `done` \| `failed` \| `cancelled`) · `current_node` ·
`wait_until` · `wait_for` (`''` \| `reply` \| `payment`) · `context` (JSON) ·
`log` (JSON) · timestamps.

`context["deliveries"]` records what was sent; `context["integrations"]` keeps
the last 20 integration-action results.

### `crm.OutboundJob` — the pacing queue
`channel` · `kind` (`text` \| `template` \| `ai_reply` \| `call`) · `contact` FK ·
`to_address` · `payload` (JSON) · `account` FK(null) · `flow_run` FK(null) ·
`flow_node` · `status` (`queued` \| `sent` \| `failed` \| `cancelled`) ·
`attempts` · `not_before` · `last_error` · `sent_message` FK(null) ·
`created_at` / `updated_at` / `sent_at`.

Drained by `flows/queue.py` with human-like jitter, per-account gaps, daily caps
and an active-conversation cap. **The account is resolved at send time** via
routing + health failover, not at enqueue time. A job may pin its account with
`payload["account_id"]`. For `kind="call"`, `sent` means **dialled**, not
completed; a busy number is a *wait*, never a failure.

### `crm.CallRecord`
`call_id` (unique) · `contact` FK(null) · `phone` · `name` · `started_at` /
`ended_at` (strings) · `transcript` · `summary` · `outcome` · `followup_date` ·
`needs_human`.

The voice agent process runs **without Django** and writes
`backend/data/calls.json`; `seed_demo` / the timeline sync mirror those records
into this table for querying and admin.

---

## 6. External sync (`ingest`)

### `DataSource`
`tenant` FK · `brand` FK(null) · `name` · `api_key_hash` (indexed) ·
`is_active` · timestamps. **unique together** `(tenant, name)`.

Only a **hash** of the `X-API-Key` is stored; inbound keys are hashed and
compared in constant time. Create one with `manage.py create_data_source`.

### `SyncBatch`
One row per push. `tenant` · `data_source` · `source_timestamp` ·
`active_record_count` · `status` (`received` \| `processing` \| `completed` \|
`partial` \| `failed`) · `rows_received` / `rows_upserted` / `rows_failed` ·
`error_detail` · `started_at` / `finished_at` / `created_at`.

Constraint `uniq_syncbatch_source_ts` — unique `(data_source, source_timestamp)`,
partial. **Replaying the same payload is a no-op.**

### `RecordSyncStaging`
Immutable raw landing zone, one row per record in the payload. `tenant` ·
`sync_batch` · `record_external_id` (indexed) · `raw_payload` (JSON) ·
`processed` · `process_error` · `processed_at` · `attempts` · `dead_lettered` ·
`created_at`.

Indexes: `(tenant, record_external_id)`, plus the partial
`idx_staging_unprocessed` on `processed = False` for worker pickup.

Each row normalizes in its own savepoint, so one bad row never poisons a batch.
Three failed attempts dead-letter the row until `manage.py replay_batch` resets
it. Old staging rows are dropped by `manage.py purge_staging`.

Payload shape and match order are documented in
`backend/ingest/normalizer.py`'s module docstring — that is the contract, and it
is worth reading before you write an exporter.

---

## 7. Integrations

### `integrations.Integration`
`ChannelAccount`'s twin for third parties.

`tenant` FK(null) · `provider` (`google_calendar` \| `google_drive` \| `gmail` \|
`google_sheets` \| `shopify` \| `stripe`) · `name` · `external_id` ·
`credentials` (JSON) · `scopes` (JSON) · `config` (JSON) · `status`
(`unknown` \| `healthy` \| `degraded` \| `disconnected` \| `revoked`) ·
`status_detail` · `last_ok_at` · `status_changed_at` · `created_by` ·
timestamps. `.connected` = has an access token or an API key.

> **`credentials` never leaves the server unmasked.** The API emits only
> `api_key_masked` / `webhook_secret_masked` (`••••1234`) and
> `has_refresh_token`; access and refresh tokens have no API representation at
> all. A value containing `••` on the way in is treated as an untouched
> round-trip. `integrations/health.py set_status` is the only writer of
> `status`. Nothing sensitive is logged — not tokens, not provider bodies.

---

## Isolation, and what it does not do

- **Tenant isolation is application-layer only.** Every scoped queryset filters
  by tenant; there is no database-level enforcement. A forgotten filter is a
  cross-tenant data leak. If you move to Postgres, adding RLS policies on
  `tenant_id` is the obvious defence in depth — nobody has done it.
- **Several operational tables are not tenanted at all** (see the note at the
  top). A single-tenant deployment is the well-tested path.
- **There is no PII vault, no column encryption and no PII access log.**
  Contact names, phones, emails, message bodies, call transcripts and call
  recordings are stored in plain text in the database and under `backend/data/`.
  `backend/data/` is gitignored for that reason. Whatever regulation applies to
  you (PDPA, GDPR, …) — consent capture, retention windows, right-to-erasure and
  recording consent are **yours to add**.
- **Retention is mostly manual.** WhatsApp call logs self-prune after 14 days
  and `purge_staging` handles sync staging; transcripts, recordings, messages,
  audit rows and notifications grow without bound.
