# Database schema

The **actual** schema, verified against `backend-csharp/src/ChatBranch.Domain/`
(the entities) and `backend-csharp/src/ChatBranch.Data/AppDbContext.cs` (table
names, JSON columns, indexes).

- **Engine:** SQLite through EF Core, one context (`AppDbContext`). The file is
  `chatbranch.sqlite3` in the API's content root in development
  (`backend-csharp/src/ChatBranch.Api/`) and `/data/chatbranch.sqlite3` in
  Docker; `ConnectionStrings__Default` overrides it.
- **Names:** tables keep the names the Django backend used (`<app>_<model>`:
  `crm_*`, `ingest_*`, `auth_user`, `integrations_integration`) and every column
  is snake_case (`EntityId` → `entity_id`), so the C# API could attach to the
  old Django database.
- **No migrations.** `EnsureCreated()` builds a fresh database;
  `Seed.UpgradeSchema` (append-only, idempotent `ALTER TABLE … ADD COLUMN` /
  `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`) brings an older
  file up to date on every boot. **A new column or table must be added there as
  well as on the entity**, or existing installs break on the first query.
- **JSON columns** are TEXT holding JSON, converted by `JsonColumn` (listed per
  table below).
- **Timestamps** are stored as TEXT and are UTC.

---

## The shape in one picture

```
Tenant ──── Entity (tree: parent_id) ──┬── ChannelAccount ── Thread ── Message
  │            │                       │        │              │  ▲
  │            │                       │        │              │  └ AiDraft, AiConversationState,
  │            │                       │        │              │    AiFeedback, IdentityRequest
  │            │                       │        └ AccountAssignment (contact × channel)
  │            │                       ├── Setting (entity override) · Team ── TeamMember
  │            │                       ├── libraries: CannedResponse, ScriptTemplate,
  │            │                       │   KnowledgeItem, AgentSkill, Workflow, Tag,
  │            │                       │   AutomationRule, Integration, AiLesson
  │            │                       │   (null = org-wide) ── Embedding (vectors)
  │            └── Profile (role, M2M → Entity) ── AuthToken, ApiKey, SavedView,
  │                                                  AssistantConversation ── AssistantMessage
  │
  └── Contact ──┬── ContactEntity ──▶ Entity
                ├── ContactIdentity, ContactInvite
                ├── TagAssignment ──▶ Tag   (entity targets too)
                ├── Record ── RecordEvent
                ├── Booking (calendar app skills)
                ├── FlowRun ── OutboundJob, FlowStepEvent
                ├── CallRecord, ConversationSummary, LlmUsage
                └── Thread (contact_id)
```

Two naming notes:

- **`Message.source` has the value `"customer"`** (`customer | agent | ai |
  system`) — who wrote the message, not a pointer at a model. Everything that
  points at a `Contact` is `contact_id`.
- **Entities scope almost everything**, but by two different rules (see
  `ScopingService`): *libraries* with a nullable `entity_id` are inherited (an
  outlet sees its own rows plus the org-wide `null` ones); *records* (threads,
  contacts via `crm_contactentity`, AI drafts, bookings, AI feedback) belong to
  exactly one subtree.

---

## 1. Tenancy, identity, access

### `ingest_tenant`
`name` · `slug` · `status` (`active` …) · `created_at` / `updated_at`. The
organization; root of isolation.

### `ingest_entity`
`tenant_id` · **`parent_id`** (self-FK, `Restrict` on delete — the API refuses
to delete an entity with children) · `name` · `slug` · timestamps. A tree:
`parent_id` null = a top-level ("main") entity; a child always shares its
parent's tenant. The `/api/v1/brands` endpoints read and write these rows.

### `auth_user`
`username` · `password` (Django-format `pbkdf2_sha256$…`, verified by
`DjangoPasswordHasher`) · `is_active` · `is_superuser` · `last_login`.

### `crm_profile` (+ `crm_profile_entities`)
| Column | Notes |
|---|---|
| `user_id` | one-to-one → `auth_user` |
| `role` | a `crm_role.slug` (`superadmin`, `admin`, `agent` or a custom role) |
| `token` | legacy single token (sessions live in `crm_authtoken`) |
| `tenant_id` | the user's organization |
| `contact_id` | nullable → `crm_contact` (`SetNull`): a **customer login** — this user IS that contact |
| `preferences` | TEXT JSON `{appearance, notifications}`; null = organization defaults |
| *(legacy)* `resource_id` | still added by `UpgradeSchema`; unused since Service Providers were removed |

`crm_profile_entities` is the M2M of entities an agent/custom role is assigned
to (empty for admin/superadmin, whose access is derived).

### `crm_authtoken`
`profile_id` · `token` (**unique**) · `created_at`. One row per login session;
concurrent sessions are allowed.

### `crm_role` · `crm_rolepermission`
`crm_role`: `slug` · `label` · `is_system` · `created_at`.
`crm_rolepermission`: `role` (slug) · `capability` · `allowed` · `updated_at` —
the capability matrix. A missing row falls back to the code default; superadmin
is always allowed in code. Catalog: `docs/RBAC_AND_BRANDS.md`.

### `crm_apikey`
`user_id` · `name` · `prefix` (**unique**, `uniq_apikey_prefix`) · `hash`
(lowercase hex SHA-256 of the secret) · `scopes` (JSON list of capabilities;
empty = everything the owner holds) · `last_used_at` (refreshed at most once a
minute) · `expires_at` · `revoked_at` · `created_at`. The token
`cbk_<prefix>_<secret>` is shown once and never stored.

### `crm_team` · `crm_teammember`
`crm_team`: `entity_id` (null = org-wide) · `name` · `description` ·
`auto_assign` · `last_assignee` (round-robin cursor) · timestamps.
`crm_teammember`: `team_id` · `user_id` · `created_at`.

---

## 2. Contacts and records

### `crm_contact`
| Column | Notes |
|---|---|
| `name`, `phone` (E.164), `email` | phone is the identity key; the API refuses a second contact whose phone shares the last 8 digits (409) — there is no DB constraint |
| `language` | `auto` \| `en` \| `ms` \| `zh` — the voice agent opens calls in it |
| `status`, `method`, `company_name`, `notes` | `notes` renders as `{{situation}}` |
| `do_not_call` | hard gate on dialling |
| `ai_mode` | `""` (follow the rules) \| `never` \| `always` — "AI for this contact" (`AiEligibility.Decide`) |
| `external_id` | id in your system of record |
| `tags` | JSON list — **a write-through cache of the contact's registry tag NAMES** (`TagService`); writing it directly is reconciled into `crm_tagassignment` |
| `attributes` | JSON dict — custom-field values (`{{cf_<key>}}`); not part of the AI prompt |
| `preferred_contact_time`, `communication_style`, `sentiment_trend`, `ai_profile_summary` | AI memory (summaries write `ai_profile_summary`) |
| `tenant_id`, `last_synced_at`, timestamps | |

### `crm_contactentity`
`contact_id` · `entity_id` · `external_ref` · `created_at`. Which entities a
contact belongs to (a contact on a sub-entity is visible to its parent).

### `crm_contactidentity` — where a contact can be reached
`contact_id` (FK, cascade) · `channel` (`whatsapp` \| `voice` \| `sms` \|
`email`, any thread channel id) · `address` (digits for whatsapp/sms, E.164 for
voice, lower-cased email) · `source` (`auto` \| `import` \| `manual` \| `invite`
\| `self`) · `verification` (WhatsApp only: `pending` \| `verified` \|
`not_on_whatsapp` \| `unknown`) · `verification_detail` · `checked_at` (the
lookup verdict is cached 30 days) · `checked_account_id` · timestamps. **Unique**
`(contact_id, channel, address)` (`uniq_contactidentity`); index
`(channel, address)`. Registered from the contact's own phone/email
(`Contacts/ContactIdentities.cs`); a row never creates a conversation.

### `crm_contactinvite` — invite links
`code` (**unique**, 8 characters) · `contact_id` (FK, cascade) · `entity_id` ·
`channel` (`""` = any channel) · `account_id` · `created_by` · `created_at` ·
`expires_at` (30 days) · `used_at` · `used_thread_id` · `used_channels` (comma
list — an any-channel code binds once per channel). Bound by the inbound path
before contact matching (`Contacts/ContactInvites.cs`).

### `crm_identityrequest` — "who are you?" with an unknown customer
`thread_id` (FK, cascade; **unique** — one per conversation) · `state` (`armed`
\| `asking` \| `linked` \| `created` \| `needs_contact` \| `ambiguous` \|
`gave_up` \| `cancelled`) · `mode` (`ask` \| `ask_and_create`) · `attempts`
(max 2 asks) · `language` (`en` \| `ms`) · `collected` (JSON `{name, phone,
email}`) · `original_message_id` (the customer's real question) · `contact_id` ·
`pending_job_id` (the queued ask) · `note` · `started_at` · `last_asked_at` ·
`resolved_at` · `updated_at`. Driven by `Contacts/IdentityRequests.cs`.

### `crm_record` — what a conversation is about
`contact_id` · `tenant_id` · `type` (`order` \| `shipment` \| `invoice` \|
`booking` \| `ticket` \| `custom`) · `external_id` · `title` · `status` (free
text; `closed/done/resolved/cancelled/completed` count as closed) · `amount`
(decimal) · `currency` (default `MYR`) · `due_date` (date) · `data` (JSON —
`tracking_url` → `{{tracking_url}}`, `link`/`url` → `{{link}}`) · timestamps.
The "primary record" of a contact is the first open one, else the most recent.

### `crm_recordevent`
`record_id` · `kind` (`status` \| `payment` \| `note` \| `scan`) · `at` ·
`amount` · `data` (JSON) · `created_at`. Written by the Shopify/Stripe mappers.

---

## 3. Tags

### `crm_tag`
| Column | Notes |
|---|---|
| `entity_id` | null = organization-wide (inherited by every outlet) |
| `name`, `slug` | slug is globally unique and immutable (conversation labels store it) |
| `color` | a palette KEY — `tangerine`/`ink`/`teal`/`plum`/`ochre`/`blue`/`green`/`rose` — never a hex |
| `description` | |
| `applies_to` | `contact` \| `entity` \| `conversation` |
| `group_name` | optional exclusivity group ("" = none): one tag per group per target |
| `attributes` | JSON list of field defs `{key, label, type (text\|number\|date\|select\|boolean\|phone\|email), options, required}` |
| `position`, timestamps | |

### `crm_tagassignment`
`tag_id` · `target_type` (`contact` \| `entity`) · `target_id` · `values`
(JSON attribute values) · `created_by` · `created_at`. **Unique**
`(tag_id, target_type, target_id)` (`uniq_tag_assignment`). Conversation tags
are NOT assignments — they stay on `crm_thread.labels` as slugs. Attribute
values render as `{{tag_<slug_with_underscores>_<key>}}`.

All rules (group exclusivity, attribute validation, the `crm_contact.tags`
cache) live in `ChatBranch.Data/Tags/TagService.cs`.

---

## 4. Conversations

### `crm_channelaccount` — an inbox
| Column | Notes |
|---|---|
| `entity_id` | null = org-wide. **Decides which outlet a conversation belongs to** |
| `channel` | `whatsapp` \| `facebook` \| `instagram` \| `telegram` \| `email` \| `website` \| `line` \| `viber` \| `sms` \| `wechat` \| `tiktok` \| `douyin` \| `zalo` |
| `provider` | `cloud` \| `baileys` (WhatsApp) |
| `name`, `external_id` | external_id = phone_number_id / page id / bot id / from-address / the widget's PUBLIC token / the Baileys gateway account key / the LINE or Viber bot id, SMS from number, WeChat original id, TikTok business id, Douyin open_id, Zalo OA id |
| `waba_id`, `access_token`, `app_secret`, `api_version`, `owner` | secrets never leave the API unmasked (the newer channels' mapping is in `Channels/More/MoreChannels.cs`) |
| `config` | JSON: `role` (`reminder`\|`conversation`), delivery (`quiet_start`, `quiet_end`, `quiet_schedule`, `queue_min_gap_s`, `queue_max_gap_s`, `llm_provider`, `stt_provider`), inbox ("who answers it": `team_id`, `ai_default`, `ai_review_mode`, any AI-behaviour override; unknown customers: `identify_unknown` off\|ask\|ask_and_create, `identify_message`), email SMTP creds, widget look, the newer channels' non-secret ids and secrets (`account_sid`, `from_number`, `messaging_service_sid`, WeChat `app_id` / `token` / `encoding_aes_key`, Zalo `app_id` / `refresh_token` / `oa_secret_key`, Douyin `client_key` / `refresh_token`…) |
| `health_status` | `unknown` \| `healthy` \| `degraded` \| `disconnected` \| `banned` — written only by `AccountHealth.SetStatus` |
| `health_detail`, `last_ok_at`, `last_inbound_at`, `fail_streak`, `health_changed_at`, `created_at` | |

### `crm_thread` — one conversation per `(channel, key)`
| Column | Notes |
|---|---|
| `channel` + `key` | **unique** (`uniq_thread_channel_key`); key = wa_id digits / email / PSID / IGSID / TG chat id / widget session |
| `address`, `name` | |
| `status` | `open` \| `pending` \| `snoozed` \| `resolved` (an inbound message reopens resolved/snoozed) |
| `assignee`, `team_id` | two-level assignment; `team_id` → `crm_team` (`SetNull` on delete) |
| `labels` | JSON list of conversation-tag slugs |
| `contact_id` | the CRM link. **AI auto-replies only fire on linked threads** |
| `account_id` | current sending account |
| `entity_id` | denormalized attribution, re-synced by `ScopingService.SyncThreadEntity` |
| `ai_enabled` | the account-less fallback tick (legacy column, kept in step by `AiEligibility.Set`) — every reader goes through `AiEligibility.Decide`, never this column |
| `ai_overrides` | JSON `{account_pk: bool}` — the per-sending-account Chatbot tick; key `"thread"` marks an operator's decision on an account-less thread |
| `opted_out` | blocks AI, templates and calls |
| `handover_at`, `handover_reason`, `handover_by` (`ai` \| `rule:<id>` \| `flow` \| `agent:<username>` \| `identity`), `handover_kind` (`check_failed` \| `asked_for_human` \| `send_blocked` \| `booking_failed` \| `low_confidence` \| `rule` \| `manual` \| `unknown_customer`), `handover_resolved_at`, `handover_count` | human handover, written only by `Ai/Handover.cs`; open = `handover_at` set and `handover_resolved_at` null. Index `ix_thread_handover (handover_at, handover_resolved_at)` |
| `call_permission`, `call_id`, `call_status`, `call_room` | WhatsApp voice-call state |
| `created_at`, `updated_at` | **`updated_at` + message count is the SSE change token** — bump it on every write |

### `crm_message`
`thread_id` · `account_id` (which number carried it) · `external_id` (wamid /
mid / tg id) · `direction` (`in`\|`out`) · `source` (`customer`\|`agent`\|`ai`\|
`system`) · `private` (internal note) · `text` · `status`
(`sent`/`delivered`/`read`/`received`/`sandbox`/`failed`) · `media_type` ·
`media_path` (relative to `data/whatsapp/media/` or `data/messaging/media/`) ·
`error` (Meta's code + reason for a failed delivery, e.g. `131047: …`) ·
`created_at` (provider timestamp for inbound). **Unique**
`(thread_id, external_id)` where `external_id <> ''`
(`uniq_message_thread_external_id`) — the webhook-redelivery guarantee.

### `crm_accountassignment`
`contact_id` · `channel` · `account_id` · `pinned` · timestamps. **Unique**
`(contact_id, channel)`. Sticky contact → account routing; rebalance moves
unpinned rows.

### `crm_savedview`
`user_id` · `scope` (`chats` today) · `name` · `filters` (the posted filter
object, JSON text) · `position` · `created_at`. Strictly per user — the inbox's
saved filter tabs.

---

## 5. Libraries (entity-scoped, `entity_id` null = org-wide)

Each has a pair of partial unique indexes: unique per `(key, entity_id)` where
`entity_id IS NOT NULL`, and unique per key where `entity_id IS NULL`.

### `crm_cannedresponse` — quick replies
`entity_id` · `short_code` (the unique key) · `content` · **`channels`** (JSON
list of thread channel ids; empty = every channel) · `created_by` ·
`created_at` · **`updated_at`** (null on old rows).

### `crm_scripttemplate`
`entity_id` · `name` (the unique key) · `content` · `created_by` · timestamps.
The text a one-way "Script call" reads verbatim.

### `crm_knowledgeitem`
`tenant_id` · `entity_id` · `kind` (`fact`\|`policy`\|`faq`\|`event`\|`notice`) ·
`title` · `question` · `content` · `tags` (JSON) · `channels` (JSON; empty =
all) · `priority` · **`published`** (public on `/help`) · **`slug`** ·
**`category`** · `active` (the AI may use it) · `starts_at` / `ends_at`
(self-expiry) · **`source_file`** (the uploaded document's name — `""` for a
typed item; an uploaded or long fact is chunked and retrieved rather than always
included) ·
`created_by` · timestamps.

### `crm_agentskill`
| Column | Notes |
|---|---|
| `entity_id`, `skill_id` (the unique key), `name`, `use_when`, `instructions`, `position` | the original prompt-skill fields |
| **`kind`** | `prompt` (default) \| `tool` (JS `run(args, ctx)`) \| `check` (JS `check(reply, ctx)`) \| `app` (a connected app — `config`) |
| **`config`** | JSON (default `{}`), app skills only: `{app: "calendar", source google\|chatbranch, calendar assignee\|shared\|<integration id>, calendar_id, actions [availability, book, cancel], timezone, hours {mon: [["09:00","17:00"]], …}, duration_minutes, slot_step_minutes, buffer_minutes, min_notice_minutes, max_days_ahead, max_active_per_contact, event_title, event_description}` — validated by `Bookings/CalendarAppConfig.cs` |
| **`description`** | what the operator described, in their words |
| **`code`** | JavaScript for tool/check skills (Jint sandbox) |
| **`parameters`** | JSON-schema text of a tool's arguments |
| **`tests`** | JSON-array text of test cases |
| **`enabled`** | default 1 |
| **`channels`** | JSON list; empty = every channel |
| **`on_fail`** | check skills: `retry` (default) \| `handover` \| `block` |
| **`updated_at`** | |

`GET/PUT /api/aicore/skills` is the only writer.

### `crm_workflow`
`entity_id` · `flow_id` (the unique key, `wf-…`) · `name` · `hint` · `nodes` /
`edges` (JSON — React Flow, stored verbatim) · `active` · **`live_since`** (when
it last went live; "Contact created"/"Scheduled" triggers only act after it) ·
`position` · `updated_by` · timestamps.

### `crm_automationrule`
`entity_id` · `name` · `description` · `event` (`conversation_created` \|
`message_created` \| `status_changed` \| `assigned`) · `active` · `position` ·
`conditions` / `actions` (JSON) · `times_fired` · `last_fired_at` · timestamps.

---

## 6. Flows and the outbound queue

### `crm_flowrun`
`workflow_id` · `contact_id` · `trigger_key` · `status` (`running` \|
`waiting` \| `done` \| `failed` \| `cancelled`) · `current_node` · `wait_until`
· `wait_for` (`''` \| `reply` \| `record`) · `context` (JSON; deliveries,
integration results) · `log` (JSON) · `started_at` · `updated_at` ·
`finished_at` · **`detail`** (why it ended: the End step's outcome, "end of
path", or the failure reason).

### `crm_flowstepevent`
`workflow_id` (the builder's `flow_id` string) · `run_id` · `contact_id` ·
`node_id` · `kind` (`entered` \| `completed` \| `waiting` \| `failed` \|
`skipped` \| `followup`) · `detail` · `at` · `day` (yyyy-MM-dd in the org's
timezone). Indexes `(workflow_id, day)` and `(run_id)`. Append-only; read by
`/api/flows/{flowId}/stats`, `/api/flows/runs` and `/api/analytics/flows`.

### `crm_outboundjob`
`channel` · `kind` (`text` \| `template` \| `ai_reply` \| `call`) · `contact_id`
· `to_address` · `payload` (JSON: `source` ai\|flow\|system, `humanize`,
`draft_id`, `account_id` pin, call `{transport, script, voice_provider, voice,
extra}` …) · `account_id` · `flow_run_id` · `flow_node` · `status` (`queued` \|
`sending` (claimed) \| `sent` \| `failed` \| `cancelled` \| `held` (AI message
waiting in the monitor)) · `attempts` · `not_before` · `last_error` ·
`sent_message_id` · timestamps · `sent_at`. For calls, `sent` means DIALLED.

---

## 7. AI

### `crm_aidraft` — the AI monitor
`thread_id` · `thread_key` (`<channel>:<key>`) · `channel` · `contact_id` ·
`entity_id` · `account_id` · `kind` (`reply` \| `follow_up` \| `flow_message`) ·
`content` · `original_content` (the AI's text when a person edited it) ·
`context` (JSON: the messages answered, tools, checks, model, tokens, why held,
`pending_actions` — the calendar reservations delivery makes —, `grounding`)
· `status` (`pending` \| `approved` \| `edited` \| `rejected` \| `sent` \|
`auto_sent` \| `expired` \| `failed` \| `shadow`) · `hold_reason` ·
`decided_by` / `decided_at` · `reject_reason` · `send_after` (auto-send
deadline) · `created_at` · `sent_at` · `sent_message_id` · `error` ·
`updated_at`. Indexes `(status, created_at)`, `(thread_id)`. One row for EVERY
AI-written outbound, sent or held.

### `crm_aiconversationstate` — follow-up bookkeeping
`thread_id` (**unique**) · `follow_ups_sent` · `last_follow_up_at` ·
`next_follow_up_at` · `anchor_message_id` (the inbound the count belongs to) ·
`note` (why follow-ups stopped/postponed) · `updated_at`.

### `crm_booking` — calendar bookings (connected-app skills)
| Column | Notes |
|---|---|
| `entity_id`, `contact_id`, `thread_id` | the conversation's outlet (owned, scoped like threads), the contact it is for, the conversation it was made in |
| `skill_id` | the app skill (`crm_agentskill.skill_id`) that made it; `""` for none |
| `source`, `integration_id` | `google` (through that `integrations_integration` row) \| `chatbranch` |
| `calendar_key` | the calendar a slot belongs to, for overlap checks: `google:<integration id>:<calendar id>` \| `chatbranch:<skill entity id \| global>` |
| `calendar_label`, `external_event_id`, `event_link`, `title`, `note` | `note` = what the customer asked to note (the book tool's argument) |
| `start_utc`, `end_utc`, `timezone` | UTC instants + the IANA zone the times were offered in |
| `status` | `pending` (reserved by an undelivered reply) \| `booked` \| `cancelling` (an undelivered reply cancels it) \| `cancelled` \| `released` (the reply never went out) \| `failed`; pending / booked / cancelling hold the slot |
| `created_by` | `ai` \| `agent:<username>` |
| `draft_id`, `cancel_draft_id` | the AI drafts whose delivery makes / cancels it |
| `detail`, `created_at`, `updated_at` | why it failed / was released / who cancelled it |

Indexes `(contact_id, status)`, `(calendar_key, start_utc)`; no foreign keys.
Every rule lives in `Bookings/BookingService.cs`.

### `crm_aifeedback` — judgements of AI replies
`entity_id` · `thread_id` · `message_id` · `draft_id` · `contact_id` · `channel`
· `rating` (+1 \| −1) · `weight` (explicit 1.0, approved / edited 0.5) · `kind`
(`explicit` \| `approved` \| `edited` \| `rejected`) · `reasons` (JSON list:
`wrong_info` \| `didnt_answer` \| `tone` \| `too_long` \| `should_handover` \|
`other`) · `note` · `question` / `answer` (snapshots, so a lesson survives
message edits) · `correction` · `user_id` (null = the system) · `lesson_id` ·
timestamps. Partial **unique** indexes: one explicit rating per user per message
(`uniq_aifeedback_user_message`) and per draft (`uniq_aifeedback_user_draft`).

### `crm_ailesson` — what the AI learned (a library: `entity_id` null = org-wide)
`entity_id` · `kind` (`example` \| `correction` \| `rule`) · `status`
(`suggested` \| `active` \| `archived` \| `dismissed`; only active lessons reach
the prompt) · `question` · `answer` (the good answer, or the rule text) ·
`avoid` (corrections) · `note` · `channels` (JSON; empty = every channel) ·
`source_count` (feedback merged into it) · `uses` · `last_used_at` ·
`created_by` · `approved_by` · timestamps. Index `(status, entity_id)`.

### `crm_embedding` — retrieval vectors
`owner_type` (`knowledge` \| `lesson`) · `owner_id` · `chunk_index` · `text` ·
`content_hash` · `model` · `dims` · `vector` (BLOB: float32 little-endian,
L2-normalised) · `updated_at`. Index `(owner_type, owner_id, chunk_index)`.
Written ONLY by the embedding worker (`EmbeddingIndexer`); a row whose hash or
model no longer matches is re-embedded.

### `crm_conversationsummary`
`contact_id` · `source` (`chat`\|`call`) · `channel` · `ref` (thread key / call
id) · `summary` · `facts` (JSON) · `sentiment` · `created_at`. On-demand case
summaries.

### `crm_llmusage` — metering ledger
`feature` (`chat_reply` \| `summary` \| `timeline` \| `playground` \|
`voice_call` \| `embedding` …) · `provider` · `model` · `channel` · `contact_id` · `ref` ·
`prompt_tokens` · `completion_tokens` · `audio_prompt_tokens` ·
`audio_completion_tokens` · `detail` (JSON) · `created_at`. Costs are NOT
stored — estimated at read time from Setting `llm_prices`.

### `crm_callrecord`
`call_id` (**unique**) · `contact_id` · `phone` · `name` · `started_at` /
`ended_at` (strings from the agent) · `transcript` · `summary` · `outcome` ·
`followup_date` · `needs_human`. Folded in from the voice agent's `calls.json`
by `VoiceCallSync`.

### `crm_pendingtoolaction` *(legacy)*
`contact_id` · `channel` · `tool` · `fingerprint` · `session` · `asked_at`;
unique `(contact_id, channel, tool)`. The Django tool double-confirm state —
mapped but unused by the C# AI (only cleaned up on contact delete).

---

## 8. Platform

### `crm_auditlog` — the activity log
| Column | Notes |
|---|---|
| `user_id`, `actor` | username \| `system` \| `webhook` … |
| `action`, `target`, `detail`, `ip`, `created_at` | e.g. `login`, `settings.update`, `account.health` (`"prev → next — reason"`), `ai.draft.approve`, `api_key.create` |
| **`source`** | `user` \| `api` \| `mcp` \| `assistant` \| `automation` \| `flow` \| `webhook` \| `system` |
| **`entity_id`** | the outlet, no FK on purpose (the trail outlives a deleted entity) |
| **`method`**, **`path`**, **`status`**, **`user_agent`**, **`request_id`** | request provenance (never the query string) |

Stamped automatically from the ambient `AuditContext` by an EF interceptor.

### `crm_notification`
`user_id` · `kind` (`message` \| `call` \| `system`) · `title` · `body` ·
`link` · `read` · `created_at` · **`category`** (`messages` \| `account_health`
\| `flows` \| `admin` \| `system`; "" on old rows = derived from kind) ·
**`ref_key`** (collapse key, e.g. `thread:<id>` — at most one UNREAD row per key
per user). Written only by `NotificationService`.

### `crm_assistantaction`
`id` (TEXT, opaque `act_…`) · `user_id` · `tool` · `args` (JSON text, exactly
as it will run) · `summary` · `status` (`pending` \| `done` \| `failed` \|
`cancelled` \| `expired`) · `result` · `created_at` · `expires_at` (15 min) ·
`decided_at`. The operator assistant's confirm-before-write queue.

### `crm_assistantconversation` — "Ask ChatBranch" history
`id` · `user_id` (the owner — every read is filtered by it; no FK, like
`crm_savedview`) · `title` (the first question, shortened; renamable) ·
`entity_id` (the outlet in view when the chat started, null = all) ·
`created_at` · `updated_at` (bumped per turn and per confirm/cancel — the
history list's order). Index `ix_assistantconversation_user_updated
(user_id, updated_at)`. At most 100 per user (the least recently used are
pruned when a new one starts).

### `crm_assistantmessage`
`id` · `conversation_id` (FK → `crm_assistantconversation`, ON DELETE CASCADE;
index `ix_assistantmessage_conversation`) · `role` (`user` \| `assistant`) ·
`text` · `payload` (JSON text: an assistant turn's `{steps, pending_actions:[…,
state, message], navigation, mode}`, a user turn's `{context:{path, entity}}`)
· `created_at`. The model's context is rebuilt from these rows (last 12).

### `crm_setting` — runtime configuration
`entity_id` (null = organization-wide) · `key` · `value` (text) · `updated_at`.
Two partial unique indexes: one global row per key (`uniq_setting_key_global`)
and one override per `(key, entity_id)` (`uniq_setting_key_entity`). Only keys in
`ScopingService.EntityScopedSettings` may carry an entity override. Read through
`ScopingService.SettingValue` — a raw read must say `entity_id IS NULL`.

Notable keys:

| Group | Keys |
|---|---|
| AI providers | `openrouter_api_key`, `openai_api_key`, `google_api_key`, `ilmu_api_key` (+ optional `ilmu_base_url`, `ilmu_chat_model`), `elevenlabs_api_key`, `local_base_url`, `local_api_key`, `chat_model`, `chat_provider_pref` (mirrored to `chat_provider`), `chat_provider_order`, `llm_prices` |
| Compliance | `opt_out_keywords`, `opt_in_keywords` (org-wide JSON lists that ADD to STOP/UNSUBSCRIBE/BERHENTI and START/UNSTOP/MULA) |
| AI content | `aicore_persona`, `org_timezone` (built-in date tools and booking skills), `booking_hours` (legacy: edited at `/api/aicore/booking-hours`, read by nothing — booking skills carry their own hours) |
| AI controls (entity-scoped, resolved up the tree) | `ai_enabled` (`on` \| `off` \| `schedule`), `ai_schedule` (JSON per-weekday windows), `ai_schedule_mode` (`inside` \| `outside`), `ai_never_tags` (comma list; levels add up) |
| AI monitor + behaviour | `ai_review_mode`, `ai_review_autosend_minutes`, `ai_review_first_n`, `ai_review_keywords`, `ai_review_notify`, `ai_wait_seconds`, `ai_wait_max_seconds`, `ai_typing*`, `ai_split*`, `ai_ack_skip`, `ai_ack_text`, `ai_followup*` |
| AI learning + retrieval | `ai_learning_mode` (`review` \| `auto` \| `off`, entity-scoped), `embedding_provider` (`""` automatic \| `openai` \| `google` \| `local` \| `none`; env `EMBEDDING_PROVIDER`), `embedding_model` (env `EMBEDDING_MODEL`), `embedding_last_error` (written by the indexer) |
| Contacts | `identify_unknown` (the default for inboxes without their own `identify_unknown`), `default_country_code` (E.164 normalisation on import, default `60`) |
| Queue + routing | `quiet_start`, `quiet_end`, `queue_min_gap_s`, `queue_max_gap_s`, `queue_daily_cap`, `queue_active_cap`, `queue_active_window_h`, `queue_call_gap_s`, `queue_call_daily_cap`, `ai_reply_mode`, `reminder_ai_default`, `business_redirect*`, `recovery_mode`, `recovery_template`, `cold_text_guard`, `cold_call_guard`, `flow_call_voice` |
| Channels | `whatsapp_verify_token` (+ legacy `verify_token`), `whatsapp_custom_templates`, `whatsapp_template_bindings`, `voice_twilio_settings`, `baileys_url`, `baileys_secret`, SMTP defaults |
| Organization | `custom_fields` (seeded `{"customer":[]}`), `appearance_default`, `notif_<category>`, `timezone` (flow statistics' day boundary), business variables (`business_name`, `agent_name`, `support_number`, `support_email`, `website`) |
| Integrations + MCP | `integrations_settings` (one JSON row: Google client, webhook secrets), `oauth_state_secret`, `mcp_servers` |
| Voice | `pronunciation_terms` |
| Markers | `tags_registry_v1` (one-time tag-registry bootstrap) |

### `integrations_integration`
`entity_id` · **`owner_user_id`** (null = a shared connection; `SetNull` when
the login is deleted) · `provider` (`google_calendar` \| `google_drive` \|
`gmail` \| `google_sheets` \| `google_contacts` \| `shopify` \| `stripe`) · `status` (`unknown` \|
`connected` \| `disconnected` \| `revoked`) · `status_detail` · `credentials`
(JSON, **never leaves the server unmasked**) · `config` (JSON) · **`scopes`**
(JSON, what Google actually granted) · timestamps.

---

## Legacy tables

A database first created by the Django backend can still contain tables the C#
context does not map — the Service Provider tables (`Resource` /
`ResourceLink`), brand/`ContactBrand` tables and the old `ingest` sync staging
tables (data sources, batches, staging rows). The C# API never reads or writes
them; they are safe to leave in place.

---

## Isolation, and what it does not do

- **Tenant and entity isolation is application-layer only.** Queries filter by
  tenant and by entity subtree (`ScopingService`); SQLite has no row-level
  security. A forgotten filter is a data leak.
- **There is no PII vault, no column encryption and no PII access log.**
  Contact details, message bodies, call transcripts and recordings are stored in
  plain text in the database and under the data dir (`backend/data/` is
  gitignored for that reason). Consent capture, retention windows,
  right-to-erasure and recording consent are **yours to add**.
- **Retention is manual.** Messages, AI drafts, AI feedback, bookings, flow step
  events, audit rows, notifications and LLM usage grow without bound.
