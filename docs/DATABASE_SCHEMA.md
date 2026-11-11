# Database schema

The **actual** schema, verified against `backend-csharp/src/ChatBranch.Domain/`
(the entities) and `backend-csharp/src/ChatBranch.Data/AppDbContext.cs` (table
names, JSON columns, indexes).

- **Engine:** SQLite (default) or PostgreSQL through EF Core, one context
  (`AppDbContext`), the same model on both (`DATABASE_PROVIDER`, see
  `docs/postgres.md`). The SQLite file is `chatbranch.sqlite3` in the API's
  content root in development (`backend-csharp/src/ChatBranch.Api/`) and
  `/data/chatbranch.sqlite3` in Docker; `ConnectionStrings__Default` overrides it.
  On PostgreSQL the types are the natural ones — `text`, `integer`, `boolean`,
  `numeric`, `date`, `bytea` — and every DateTime column is `timestamp without time
  zone` (UTC values, read back as written, like SQLite's text dates).
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
`DjangoPasswordHasher`) · `is_active` · `is_superuser` · `last_login` ·
`first_name` / `last_name` / `email` (Django's columns, added by
`UpgradeSchema`; edited by the person themselves at Settings → My account —
display name = first + last, else the username; email unique across users when set).

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

### `crm_passwordtoken`
`user_id` · `token_hash` (**unique**, `uniq_passwordtoken_hash` — lowercase hex
SHA-256 of the link's token; the token itself is never stored) · `purpose`
(`invite` = the welcome email, `reset` = an admin's password reset link) ·
`created_by` (username) · `created_at` · `expires_at` (72 h) · `used_at` ·
`revoked_at` (a newer link, a deactivation, an email or password change). A
one-time set-password link — `docs/security.md` "The welcome email".

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
| `attributes` | JSON dict — custom-field values (`{{cf_<key>}}`). A `date` field holds the canonical `YYYY-MM-DD`, a `json` field a JSON object (≤ 8000 chars) an outside system syncs in; both are validated on write (`ContactCustomFields`). Only the fields whose definition sets `show_to_ai` reach the AI's prompts (the CONTACT block and a journey's "Let AI decide"), and `entity_id` scopes a field to one company on every AI-facing path including a code skill's `ctx.contact` |
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

### `crm_contactaccessrequest` — "Request access" to another branch's contact
`contact_id` (FK, cascade) · `entity_id` (the branch to add the contact to) ·
`requested_by` (username) · `note` · `status` (`pending` \| `approved` \|
`denied` \| `cancelled`) · `decided_by` · `reason` (a denial's reason, shown to
the requester) · `decided_at` · `created_at`. **Partial unique** index
`(contact_id, entity_id) WHERE status = 'pending'` — at most one pending request
per contact × branch (a repeat returns it). Approving adds a
`crm_contactentity` link (the contact's other entities stay). Written only by
`Endpoints/ContactAccessEndpoints.cs`; the 409's `request_token` is
`Contacts/ContactAccessToken.cs` (AES-GCM, 30 min, Setting
`contact_access_secret`).

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
| `name`, `external_id` | external_id = phone_number_id / page id / bot id / from-address / the widget's PUBLIC token / the Baileys gateway account key / the LINE or Viber bot id, SMS from number, WeChat original id, TikTok business id, Douyin open_id, Zalo OA id. **WhatsApp Cloud: unique** — `uniq_channelaccount_wa_pnid` `(external_id) WHERE channel = 'whatsapp' AND provider IN ('cloud', '') AND external_id <> ''`, because the Cloud webhook matches inbound traffic by it and two rows holding the same number made routing a coin flip between their companies (`Endpoints/WebhooksEndpoints`, `Channels/CloudInboxRouting`). The filter is that webhook's own match: a BSP or QR row is addressed by its own webhook URL / gateway key, never by a shared number lookup, so those providers are left alone (the same business number may sit on two of them while one is migrated). Added on boot by `Seed.UpgradeSchema` / `PostgresSchema.Sync` — defensively: a database that already holds a duplicate still boots, logs the clash and simply has no index |
| `waba_id`, `access_token`, `app_secret`, `api_version`, `owner` | secrets never leave the API unmasked (the newer channels' mapping is in `Channels/More/MoreChannels.cs`). `waba_id` is also the Cloud webhook's cross-check: a payload whose `entry[].id` contradicts it is refused, a blank one on either side never is |
| `config` | JSON: `role` (`reminder`\|`conversation`), delivery (`quiet_start`, `quiet_end`, `quiet_schedule`, `queue_min_gap_s`, `queue_max_gap_s`, `llm_provider`, `stt_provider`), inbox ("who answers it": `team_id`, `ai_default`, `ai_review_mode`, any AI-behaviour override; unknown customers: `identify_unknown` off\|ask\|ask_and_create, `identify_message`), email SMTP creds, widget look, the newer channels' non-secret ids and secrets (`account_sid`, `from_number`, `messaging_service_sid`, WeChat `app_id` / `token` / `encoding_aes_key`, Zalo `app_id` / `refresh_token` / `oa_secret_key`, Douyin `client_key` / `refresh_token`…) |
| `health_status` | `unknown` \| `healthy` \| `degraded` \| `disconnected` \| `banned` — written only by `AccountHealth.SetStatus` |
| `health_detail`, `last_ok_at`, `last_inbound_at`, `fail_streak`, `health_changed_at`, `created_at` | |

### `crm_thread` — one conversation per customer PER COMPANY: `(channel, key, company)`
| Column | Notes |
|---|---|
| `channel` + `key` + `company_entity_id` | **unique** — two partial unique indexes (NULLs are distinct in SQL): `uniq_thread_channel_key_company` `(channel, key, company_entity_id) WHERE company_entity_id IS NOT NULL` and `uniq_thread_channel_key_unattributed` `(channel, key) WHERE company_entity_id IS NULL`. key = wa_id digits / email / PSID / IGSID / TG chat id / widget session — or a GROUP's own id: WhatsApp's whole `…@g.us` jid, a negative Telegram chat id, a LINE groupId `C…` / roomId `R…` (group-ness is derived from channel + key, `Channels/GroupChats.IsGroup`; a group thread never has a `contact_id`). The old `uniq_thread_channel_key` (`(channel, key)` for the whole tenant — incident 2026-10-11) is dropped on boot |
| `company_entity_id` | the COMPANY (top-level entity) the conversation belongs to — part of its identity. Set at creation from the receiving account's entity (else the thread's entity); null = unattributed (no account, no entity — the organization-wide bucket **every company can see**, which the next company to touch the conversation adopts, so nothing that could belong to a real company may be filed here: an inbound WhatsApp Cloud payload whose `phone_number_id` matches no account is recorded and dropped instead, once the install has any Cloud number — `Channels/CloudInboxRouting`). Never changed afterwards: a contact link, a same-company number taking over or an account re-binding never moves a thread into another company (an unattributed thread adopts a company once, when it first gets one and the slot is free). Backfilled on boot by `Stores/ThreadCompanies.Upgrade` (SQLite `Seed.UpgradeSchema`; PostgreSQL adds the column + indexes from the model in `PostgresSchema.Sync`, the same backfill then runs): the account's company, else the entity's; the new indexes are created BEFORE the old one is dropped |
| `address`, `name` | |
| `status` | `open` \| `pending` \| `snoozed` \| `resolved` (an inbound message reopens resolved/snoozed) |
| `assignee`, `team_id` | two-level assignment; `team_id` → `crm_team` (`SetNull` on delete) |
| `labels` | JSON list of conversation-tag slugs |
| `contact_id` | the CRM link. **AI auto-replies only fire on linked threads**. Written when a message arrives (`Channels/InboundProcessor.LinkContact` → `ContactCompanies.MatchPhone`/`MatchEmail` against the RECEIVING entity, then `MayLink`), by hand from the chat, or — for conversations that already existed when their contact was created (a contact import never touches this table) — by the on-demand backfill `Contacts/ThreadContactBackfill` behind `POST /api/contacts/link-conversations`, which reuses that very resolution, fills only NULLs, skips groups / broadcasts and bumps `updated_at` |
| `account_id` | current sending account — always a number of the thread's company (or an organization-wide one) |
| `entity_id` | denormalized attribution, re-synced by `ScopingService.SyncThreadEntity` — always inside `company_entity_id` |
| `ai_enabled` | the account-less fallback tick (legacy column, kept in step by `AiEligibility.Set`) — every reader goes through `AiEligibility.Decide`, never this column |
| `ai_overrides` | JSON `{account_pk: bool}` — the per-sending-account Chatbot tick; key `"thread"` marks an operator's decision on an account-less thread |
| `opted_out` | blocks AI, templates and calls |
| `handover_at`, `handover_reason`, `handover_by` (`ai` \| `rule:<id>` \| `flow` \| `agent:<username>` \| `identity`), `handover_kind` (`check_failed` \| `asked_for_human` \| `send_blocked` \| `booking_failed` \| `low_confidence` \| `rule` \| `manual` \| `unknown_customer`), `handover_resolved_at`, `handover_count` | human handover, written only by `Ai/Handover.cs`; open = `handover_at` set and `handover_resolved_at` null. Index `ix_thread_handover (handover_at, handover_resolved_at)` |
| `call_permission`, `call_id`, `call_status`, `call_room` | WhatsApp voice-call state |
| `created_at`, `updated_at` | **`updated_at` + message count is the SSE change token** — bump it on every write |

### `crm_message`
`thread_id` · `account_id` (which number carried it) · `external_id` (wamid /
mid / tg id) · `direction` (`in`\|`out`) · `source` (`customer`\|`agent`\|`ai`\|
`system`\|`external_client` — a mail someone sent from their own mail app, read
back from the mailbox's Sent folder) · `private` (internal note) · `text` · `status`
(`sent`/`delivered`/`read`/`received`/`sandbox`/`failed`) · `media_type` ·
`media_path` (relative to `data/whatsapp/media/` or `data/messaging/media/`) ·
`media_name` (the attachment's original file name, `''` = unknown) ·
`media_mime` (the provider's MIME type, `''` = derive from the extension) ·
`media_size` (bytes, `0` = unknown — older rows; the payload reads the file) ·
`error` (Meta's code + reason for a failed delivery, e.g. `131047: …`) ·
`reply_to_id` (nullable — the message of the SAME thread this one quotes,
resolved from `reply_to_external_id` when we hold it) · `reply_to_external_id`
(the provider id of the quoted message: Cloud `context.id`, Baileys
`contextInfo.stanzaId`, Telegram `reply_to_message.message_id`, Messenger
`reply_to.mid`; `''` = not a reply) · `transcript` (what a voice note says) ·
`recalled_at` (nullable — taken back on the channel, "delete for everyone" / unsend;
`Channels/MessageRecalls.cs`) · `recalled_by` (`customer`\|`business`, `''` = never;
the inbox's `keep_recalled` decides whether `text` / the file were kept or erased) ·
`idempotency_key` (the caller's own key for a transactional send,
`POST /api/messaging/notify`; `''` = not one, which is nearly every row) ·
`sender_key` / `sender_name` / `sender_phone` (who wrote an inbound message of a
GROUP conversation — WhatsApp's full participant jid `…@s.whatsapp.net` / `…@lid`,
a Telegram user id, a LINE userId; the name the channel showed; the phone digits
when known. `''` on every 1:1 message — a 1:1 thread's author is the thread itself.
`Channels/GroupChats.cs`; payload `sender: {id, name, phone|null}`) ·
`created_at` (provider timestamp for inbound). **Unique**
`(thread_id, external_id)` where `external_id <> ''`
(`uniq_message_thread_external_id`) — the webhook-redelivery guarantee — and
`idempotency_key` where `idempotency_key <> ''` (`uniq_message_idempotency`),
which is what makes a retried transactional send read its own message back
instead of sending a second one.

### `crm_messagereaction`
`message_id` · `thread_id` · `actor` (`contact` \| `user:<username>`) · `emoji` ·
`created_at`. **Unique** `(message_id, actor)` (`uniq_messagereaction_actor`) —
reacting again replaces, an empty emoji deletes the row; one of OUR reactions
replaces any colleague's on the same message (the business is one sender to
WhatsApp / Telegram). Index `thread_id`. Written only by
`Channels/MessageReactions.cs`; a reaction is never a `crm_message` row and
only bumps the thread's `updated_at`.

### `crm_socialthread` — what a social conversation is about (Zernio)
One row per `crm_thread` of channel `social` (**unique** `thread_id`):
`kind` (`comment` \| `review` \| `dm`) · `platform` (Zernio's id: instagram,
facebook, googlebusiness…) · `zernio_account_id` · `account_username` ·
`post_id` (the platform's post id — what a comment reply / hide / private reply
addresses) · `zernio_post_id` · `post_text` · `post_permalink` · `post_media_url`
· `root_comment_id` (the top-level comment the thread is about) · `review_id`
(the FULL review name, e.g. `accounts/…/locations/…/reviews/…`; the thread key
carries a URL-safe short form) · `conversation_id` (Zernio's DM conversation) ·
`rating` (1–5, reviews) · `hidden` · `private_replied` · `author_id` /
`author_name` / `author_handle` / `author_avatar` · timestamps. Index
`(zernio_account_id, root_comment_id)`. Written by `Social/ZernioWebhook.cs`;
read by `Social/SocialTransport.cs` (replies) and the inbox payloads' `social`.

### `crm_socialprofile` — entity → Zernio profile
`entity_id` (null = the organization: the key's default profile) · `profile_id`
(**unique**) · `external_id` (`cb-entity-<id>`) · `name` · `created_at`.

### `crm_socialpost` — posts composed in ChatBranch
`entity_id` · `zernio_post_id` ("" = a local draft) · `content` · `media`
(JSON `[{url, type}]`) · `account_ids` / `platforms` (JSON) · `scheduled_at`
(UTC, null = now) · `timezone` · `status` (`draft` \| `scheduled` \|
`publishing` \| `published` \| `partial` \| `failed` \| `deleted`) · `error` ·
`results` (JSON `[{account_id, platform, status, url, error}]`) · `created_by` ·
timestamps. Kept in step by the `post.*` webhooks and a reconcile on read.

### `crm_zernioevent` — webhook dedupe
`event_id` (**unique**, Zernio's `X-Zernio-Event-Id`) · `event` · `received_at`.
Claimed before an event is processed; released if processing fails so the retry runs.

### `crm_accountassignment`
`contact_id` · `channel` · `account_id` · `pinned` · timestamps. **Unique**
`(contact_id, channel)`. Sticky contact → account routing; rebalance moves
unpinned rows.

### `crm_mailboxcursor` — how far the IMAP sync has read
`mailbox` (`imap_host|imap_username`, lower-cased — account mailboxes and the
default mailbox alike) · `folder` (the IMAP full name: `INBOX`, `Sent Items`…) ·
`uid_validity` · `last_uid` (every UID up to it has been considered) ·
`updated_at`. **Unique** `(mailbox, folder)` (`uniq_mailboxcursor_folder`).
Written only by `Channels/ImapSync.cs` (`EmailSyncService`); another
`uid_validity` from the server means the folder was rebuilt and the sync reads
its 7-day window again (Message-IDs keep that from importing twice).

### `crm_savedview`
`user_id` · `scope` (`chats` today) · `name` · `filters` (the posted filter
object, JSON text) · `position` · `created_at`. Strictly per user — the inbox's
saved filter tabs.

---

## 5. Libraries (entity-scoped, `entity_id` null = org-wide)

Each has a pair of partial unique indexes: unique per `(key, entity_id)` where
`entity_id IS NOT NULL`, and unique per key where `entity_id IS NULL`.

**Multi-entity availability ("Available to").** Quick replies, call scripts,
knowledge items, AI skills and automation rules can be available to SEVERAL
entities. `entity_id` stays the **primary** entity (the first ticked one; what
every older reader and the unique indexes see); every further entity is a row in
`crm_libraryentity`. No entity at all (`entity_id` null, no tick rows) = global
(everyone). A row ticked for A and B is visible to A, B and their branches, never
to a sibling outside the ticks — ONE rule, `ScopingService.LibraryVisible`, used
by `ScopeInherited` and `ShellScope.ScopeLibrary`. A legacy row with only
`entity_id` behaves exactly as before (no migration).

### `crm_libraryentity` — extra "available to" entities
`kind` (`canned` \| `script` \| `knowledge` \| `skill` \| `automation` \| `ai_task` —
`Domain/LibraryEntity.cs LibraryKinds`) · `item_id` (the library row's id) ·
`entity_id` · `created_at`. **Unique** `(kind, item_id, entity_id)`
(`uniq_libraryentity`), index `(kind, entity_id)`. One generic table (like
`crm_tagassignment`'s `target_type`) rather than one per library, so the
visibility rule is one query for every kind. Never holds the primary entity, and
a global row has none (`Data/LibraryEntities.cs` keeps that invariant; deleting
an entity drops its ticks and promotes another tick to primary on rows it was
primary of; deleting an item drops its ticks).

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
| **`config`** | JSON (default `{}`), app skills only: `{app: "calendar", source google\|chatbranch, calendar assignee\|shared\|<integration id>, calendar_id, actions [availability, book, cancel], timezone, hours {mon: [["09:00","17:00"]], …}, duration_minutes (the default length), duration_unit_minutes (15\|30\|60), max_units (1–16: a booking may be N × unit long), slot_step_minutes, buffer_minutes, min_notice_minutes, max_days_ahead, max_active_per_contact, event_title, event_description}` — validated by `Bookings/CalendarAppConfig.cs` |
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
| `meeting_provider`, `meeting_url` | the video meeting made with it: `zoom` \| `google_meet` \| `link` (the calendar skill's own link) and the link the customer joins with; `""` = none |
| `meeting_id`, `meeting_integration_id` | the Zoom meeting's id and the Zoom connection that made it (what cancelling deletes) |
| `meeting_sent_at` | when the link was sent to the customer (after the reply that booked); null = not yet |

Indexes `(contact_id, status)`, `(calendar_key, start_utc)`; no foreign keys.
Every rule lives in `Bookings/BookingService.cs`.

### `crm_document` — generated documents (Documents / QR app skills, operators)
| Column | Notes |
|---|---|
| `entity_id`, `contact_id`, `thread_id` | the conversation's outlet (owned, scoped like threads), the contact, the conversation |
| `skill_id` | the app skill (`crm_agentskill.skill_id`) whose letterhead / numbering it used |
| `type` | `invoice` \| `quotation` \| `receipt` \| `letter` \| `qr` |
| `series`, `seq`, `number` | the running-number series `<skill entity id \| org>:<skill id>:<type>`, the number in it (0 for a QR code), and the printed number (`INV-0012` = prefix + zero-padded seq) |
| `title`, `currency` | a letter's subject / a quotation's heading; ISO currency |
| `subtotal`, `discount`, `tax`, `total` | decimals (stored as TEXT), computed by the SERVER (`Documents/DocumentMath.cs`) — never the model |
| `data` | JSON: what was printed (issue / due / valid-until dates, customer, lines, tax label + rate, notes, reference, payment method, letter paragraphs, language) |
| `file_path`, `file_channel`, `file_name`, `mime`, `size_bytes` | the stored PDF / PNG under the channel's media root (`MediaStore`) |
| `status` | `pending` (made by an undelivered reply) \| `sent` \| `void` (its reply never went out — the number stays used) \| `failed` \| `issued` (made by hand, not sent) |
| `created_by` | `ai` \| `agent:<username>` |
| `draft_id`, `message_id` | the AI draft whose delivery sends it; the message that carried the file (or the link) |
| `public_token` | 128-bit random hex — the customer's download link `/api/documents/public/{token}` (sent / issued only, 90 days) |
| `detail`, `created_at`, `updated_at`, `sent_at` | how it went out (`file` \| `link`) or why it was voided / failed |

Partial **unique** index `uniq_document_series_seq` on `(series, seq) WHERE seq > 0`
(two replies racing for a number: one takes the next); indexes on `thread_id` and
`public_token`. Every rule lives in `Documents/DocumentService.cs`.

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

### `crm_aireviewrun` — one on-demand AI review ("Review now" on the AI monitor)
`created_by` · `entity_id` (the picked entity — where "Add house rule" writes; null =
organization-wide) · `entity_ids` (JSON: every entity the run could read; empty =
unrestricted — a caller sees the run only when all of them sit in their scope) ·
`period_from` / `period_to` · `limit` · `mode` (`model` \| `heuristic`) · `provider` ·
`model` · `status` (`running` \| `done` \| `failed`) · `note` (fallbacks taken) ·
`error` · `summary` (JSON: `summary`, `top_problems[]`, `suggestions[]`, `source`) ·
`reviewed` · `issues` · `high_severity` · `prompt_tokens` · `completion_tokens` ·
`cost_usd` · `requested_model` (the reviewer picked for this run, `provider:model`;
"" = the default `ai_reviewer_model` — `provider` / `model` say what actually
reviewed) · `created_at` · `finished_at` · `updated_at`. Index `created_at`; only
the last 50 runs are kept (their findings go with them).

### `crm_aireviewfinding` — the reviewer's verdict on one AI reply
`run_id` · `draft_id` · `thread_id` · `channel` · `thread_key` · `entity_id` (the
conversation's — scopes the finding) · `contact_id` · `draft_status` · `verdict`
(`good` \| `issue`) · `severity` (`low` \| `medium` \| `high`, "" when good) ·
`categories` (JSON list) · `explanation` · `evidence` (the knowledge / rule cited) ·
`fix_type` (`knowledge` \| `house_rule` \| `lesson` \| `persona` \| `review_setting`
\| `none`) · `fix_text` · `customer_excerpt` · `reply_excerpt` · `source` (`model` \|
`heuristic`) · `applied` · `applied_as` · `applied_ref` (`lesson:12`,
`knowledge:4`, `house_rule`) · `applied_by` · `applied_at` · `created_at`. Index `run_id`.

### `crm_aitask` — a background AI task (a silent AI worker; a library: `entity_id` null = org-wide)
`entity_id` (primary "Available to"; further ticks are `crm_libraryentity` rows of
kind `ai_task`) · `name` · `enabled` · `channels` (JSON; empty = every channel) ·
`trigger` (`inbound_message`) · `attachment` ("" \| `any` \| `pdf` \| `image`) ·
`keywords` (JSON; ANY must appear, empty = no condition) · `linked` ("" \| `yes`
\| `no`) · `instructions` · `tools` (JSON allowlist: `add_private_note`,
`add_conversation_label`, `set_contact_field`, `hand_over_to_person`,
`sheets_append_row`, `drive_upload_attachment`, `calendar_create_event`,
`webhook_post`, `mcp:<server>__<tool>`) · `config` (JSON: `spreadsheet_id`,
`sheet_range`, `sheet_columns`, `drive_folder_id`, `calendar_id`, `webhook_url`,
`webhook_secret` — the secret only leaves masked) · `run_count` · `last_run_at` ·
`created_by` · `created_at` · `updated_at`.

### `crm_aitaskrun` — one run of a task on one inbound message
`task_id` · `thread_id` · `message_id` · `entity_id` (the conversation's when
queued — scopes who may read it) · `channel` · `thread_key` · `status` (`pending`
\| `running` \| `ok` \| `failed` \| `skipped`) · `due_at` (the inbound path
queues it a few seconds ahead — attachments are fetched off the ack) ·
`extracted` (JSON object) · `steps` (JSON: `[{tool, args, result, ok, preview}]`,
redacted and capped) · `summary` (also the "🤖" private note) · `error` ·
`provider` · `model` · `prompt_tokens` · `completion_tokens` · `cost_usd` ·
`created_at` · `started_at` · `finished_at`. **Unique** `(task_id, message_id)`
(`uniq_aitaskrun_task_message` — a redelivered message never runs a task twice);
indexes `(status, due_at)`, `thread_id`. A message that fails the attachment /
linked condition at run time leaves no row.

### `crm_embedding` — retrieval vectors
`owner_type` (`knowledge` \| `lesson`) · `owner_id` · `chunk_index` · `text` ·
`content_hash` · `model` · `dims` · `vector` (BLOB: float32 little-endian,
L2-normalised) · `updated_at`. Index `(owner_type, owner_id, chunk_index)`.
Written ONLY by the embedding worker (`EmbeddingIndexer`); a row whose hash or
model no longer matches is re-embedded.

### `crm_conversationsummary`
`contact_id` · `source` (`chat`\|`call`\|`timeline`…) · `channel` · `ref`
(`thread:{id}` for a conversation's case summary — a customer has one
conversation per company, so the older `channel:key` refs were moved to their
one thread on boot by `ThreadCompanies.AttributeSummaries`; a call id; the
timeline's phone / email) · `summary` · `facts` (JSON; a timeline summary
records the `thread_ids` / `call_ids` it was written from and is served only to
a caller who sees all of them) · `sentiment` · `created_at`. On-demand case
summaries. The AI's CONTACT MEMORY reads a conversation's summary only for that
conversation's company, and contact-level rows only when every company the
contact's data came from is the answering one.

### `crm_llmusage` — metering ledger
`feature` (`chat_reply` \| `summary` \| `timeline` \| `playground` \|
`voice_call` \| `embedding` \| `ai_task` …) · `provider` · `model` · `channel` · `contact_id` ·
`entity_id` (the outlet the call was for — the AI bill's company) · `ref` ·
`user_name` (the signed-in person who ran it themselves — the operator assistant,
the AI playground, skill drafting, on-demand / timeline summaries; "" = automated
work; what a user's AI quota counts) ·
`prompt_tokens` · `completion_tokens` · `audio_prompt_tokens` ·
`audio_completion_tokens` · `cost_usd` (the provider's own price for the call when
it reports one — OpenRouter; null = estimate at read time from Setting
`llm_prices` over OpenRouter's public list `llm_prices_catalog`) · `detail` (JSON) ·
`created_at`.

### `crm_aicreditaccount` — prepaid AI credit balances
`scope` (`entity` \| `user`) · `entity_id` (the COMPANY — a top-level entity;
**unique** where non-null, `uniq_aicreditaccount_entity`) · `user_name` (the
login of a per-person balance; **unique** where non-blank,
`uniq_aicreditaccount_user`; "" on a company account) · `balance` · `currency` ·
`enabled` · `overdraft_limit` · `low_balance_threshold` · `created_at` ·
`updated_at`.

One prepaid balance per client company. A branch has **no** balance of its own:
it draws on its top-level parent's, the same attribution the AI bill and the AI
quotas use. `balance` is a **cache** of `sum(crm_aicreditentry.amount)` for the
same scope, moved by one atomic `UPDATE … SET balance = balance + x` inside the
ledger line's own transaction (`Data/Ai/AiCredit.cs`) — never read-modify-write.
It is **signed and never clamped**: consumption is only knowable after a model
call, so the last reply before exhaustion overshoots, and clamping to zero would
forgive money and stop the ledger reconciling. A company with no row is simply
not on credit. The currency is copied from the billing terms when the account is
opened and a later currency change does **not** convert it — a grant in another
currency is refused rather than silently mixed.

### `crm_aicreditentry` — the credit ledger (append-only)
`scope` (`entity` \| `user`) · `entity_id` · `user_name` · `kind` (`grant` \|
`topup` \| `promo_bonus` \| `consumption` \| `adjustment` \| `refund` \|
`chargeback` \| `expiry` \| `conversion`) · `amount` (**signed**: credits
positive, usage and refunds negative) · `currency` · `balance_after` · `ref`
(`llmusage:12..84`, `stripe:evt_…`, `payment:12`) · `idempotency_key`
(**unique where non-blank**, `uniq_aicreditentry_idem` — the
`crm_message.idempotency_key` pattern: a replayed grant or webhook delivery
credits exactly once) · `note` · `actor` · `source` (`AuditSources`) ·
`offer_id` / `payment_id` (reserved for the promotion and Stripe phases; no FK —
the ledger outlives both) · `created_at`. Index
`ix_aicreditentry_entity_created` on `(entity_id, created_at)`.

The **source of truth** for credit: never updated, never deleted — a correction
is a new signed line. `sum(amount)` per scope must equal that account's
`balance` to within 1e-6 (`AiCredit.ReconcileAsync`, asserted by
`AiCreditTests`). Nothing writes `consumption` lines yet: AI usage is still
billed by the monthly AI bill and a credit balance cannot stop an AI reply.

### `crm_aipricebook` — AI price books (named pricing plans)
`slug` (**unique** where non-blank, `uniq_aipricebook_slug` — what a month's
terms snapshot refers to) · `label` · `mode` (`markup` \| `flat` \| `unit`) ·
`markup_pct` (percent over the provider's cost) · `flat_amount` (per company per
month, usage or no usage) · `unit_per1k_tokens` / `unit_per_reply` (a price per
unit, quoted in the billing currency) · `monthly_fee` · `user_allowance` (the
monthly AI allowance the plan grants EACH login it covers) · `is_active` ·
`is_default` (the organization default — at most one) · `note` · `created_by` ·
`created_at` · `updated_at`.

A plan is what a client is sold, so a hundred companies can be priced by
assigning a plan instead of by typing a markup into a hundred boxes
(`Data/Ai/AiPriceBooks.cs`). `mode` carries exactly ONE price mode; a `unit`
plan needs at least one of the two unit prices, a `flat` plan a flat amount.
Only `billing.manage` writes these rows, and every write leaves an
`AuditTrail` row (`billing.pricebook`).

### `crm_aipricebookassignment` — which entity a plan prices
`price_book_id` · `entity_id` (**unique** where > 0,
`uniq_aipricebookassignment_entity` — one plan per entity) · `actor` ·
`created_at` · `updated_at`. Index `ix_aipricebookassignment_book` on
`price_book_id`.

ANY level of the tree may carry an assignment: an entity with none inherits its
nearest assigned ancestor's plan, through the same nearest-level-wins walk as
every other setting (`ScopingService.EntityChain` / `ChainMap`). Assignments are
made in bulk through the bindings pattern (`POST
/api/bindings/pricebook-entities`, target = the plan, add/remove = entity ids),
which audits as `binding.update` plus `billing.pricebook.assign`.

**These two tables are the editing surface, not what bills a month.** Every plan
or assignment change re-snapshots them into the month-versioned `ai_billing`
Setting (`BillingTerms.plans` / `entity_plans` / `default_plan`, written by
`AiPriceBooks.SyncTerms` through `AiBilling.Apply`): the change prices the
CURRENT month and later, and a month already billed keeps the snapshot that
billed it. Billing and the AI quotas read only that snapshot, through the one
resolver `AiPriceBooks.Resolve`, whose order is: the per-company explicit
override in `ai_billing.companies` (field by field) → the plan assigned nearest
up the tree → the organization default plan → the terms' own
`default_markup_pct` / `default_monthly_fee` (which is exactly what billed
before price books existed, so a database with no plans bills identically).

### `crm_callrecord`
`call_id` (**unique**) · `contact_id` · `entity_id` (nullable — the entity the
call was placed for, from the dispatch metadata's `entity_id`; contact views
list only the caller's entities' calls, `Endpoints/CallScope.cs`; null = an
older call, shown only to unbound viewers) · `phone` · `name` · `started_at` /
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

### `crm_medialibraryitem`
`file_id` (**unique**, the short public `f_…` a send refers to) · `name` · `mime` ·
`kind` (image \| video \| audio \| document) · `size` · `path` (relative to
`MediaStore.LibraryRoot()` = `data/library/<yyyy-MM>/`) · `user_id` (who uploaded
it) · `entity_id` (the outlet it belongs to; null = organization-wide) ·
`created_at` · `expires_at` (`MediaLibraryItem.RetentionDays` = 30 days; the
sweep on upload and on listing deletes the bytes and the row) · `last_sent_at`.

### `crm_pushdevice`
`user_id` · `platform` (`web` \| `android` \| `ios`) · `endpoint` (Web Push: the
push service's URL; the app: its Firebase token — **unique** where non-empty,
`uniq_pushdevice_endpoint`, so re-registering a device refreshes its row) ·
`p256dh` / `auth` (a browser subscription's own keys; "" for the app) · `label` ·
`created_at` · `last_push_at` · `last_error` · `failures` (consecutive; at
`PushDevice.MaxFailures` the device is parked until it registers again). A 404 or
410 from the push service DELETES the row — that is the protocol's own expiry.

### `crm_oauthclient`
An AI app that registered itself (RFC 7591): `client_id` (**unique**) · `name` ·
`redirect_uris` (JSON list — an exact match is required at authorize time) ·
`uri` · `created_at` · `last_used_at`. A client id on its own can do nothing; it
can only ask a person to approve it.

### `crm_oauthauthorization`
One sign-in in progress, and then the code it becomes: `request_id` (**unique** —
what the Allow page's URL carries) · `client_id` · `redirect_uri` · `scope` ·
`state` · `code_challenge` / `code_challenge_method` (PKCE, S256 only) ·
`resource` · `user_id` (null until someone approves) · `scopes` (what they
agreed to; empty = everything they can do) · `code_hash` · `used_at`
(single-use) · `expires_at` (15 min for the page, 5 for the code) · `created_at`.

### `crm_oauthtoken`
A connected app's token pair, hashes only: `client_id` · `user_id` · `scopes`
(empty = everything its owner can do) · `requested` · `access_prefix` /
`access_hash` · `refresh_prefix` / `refresh_hash` · `expires_at` (8 h) ·
`refresh_expires_at` (60 days) · `revoked_at` · `created_at` · `last_used_at`.
Refreshing ROTATES both halves, so a replayed refresh token buys nothing.

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
| AI content | `aicore_persona`, `org_timezone` (the business time zone — built-in date tools, booking skills, journeys, quiet hours, report days; set at Settings → Time zone via `PUT /api/settings/appearance {time_zone}`), `booking_hours` (legacy: edited at `/api/aicore/booking-hours`, read by nothing — booking skills carry their own hours) |
| AI controls (entity-scoped, resolved up the tree) | `ai_enabled` (`on` \| `off` \| `schedule`), `ai_schedule` (JSON per-weekday windows), `ai_schedule_mode` (`inside` \| `outside`), `ai_never_tags` (comma list; levels add up). The same four keys may also sit in one inbox's `ChannelAccount.Config` (`InboxSettings.SetMaster`) as the level BELOW the entity: there they only ever SUBTRACT — `off` silences that inbox, a schedule narrows its hours, its tags add to the inherited ones, and `on` gates nothing, because the rule is "off anywhere up the tree wins" (`Ai/AiInboxMaster`). Not `ai_default`, which is stamped on a new conversation and loses to the per-chat tick |
| AI monitor + behaviour | `ai_review_mode`, `ai_review_autosend_minutes`, `ai_review_first_n`, `ai_review_keywords`, `ai_review_notify`, `ai_wait_seconds`, `ai_wait_max_seconds`, `ai_typing*`, `ai_split*`, `ai_ack_skip`, `ai_ack_text`, `ai_followup*` |
| AI learning + retrieval | `ai_learning_mode` (`review` \| `auto` \| `off`, entity-scoped), `embedding_provider` (`""` automatic \| `openai` \| `google` \| `local` \| `none`; env `EMBEDDING_PROVIDER`), `embedding_model` (env `EMBEDDING_MODEL`), `embedding_last_error` (written by the indexer) |
| Contacts | `identify_unknown` (the default for inboxes without their own `identify_unknown`), `default_country_code` (E.164 normalisation on import, default `60`) |
| Queue + routing | `quiet_start`, `quiet_end`, `queue_min_gap_s`, `queue_max_gap_s`, `queue_daily_cap`, `queue_active_cap`, `queue_active_window_h`, `queue_call_gap_s`, `queue_call_daily_cap`, `ai_reply_mode`, `reminder_ai_default`, `business_redirect*`, `recovery_mode`, `recovery_template`, `cold_text_guard`, `cold_call_guard`, `flow_call_voice` |
| Channels | `whatsapp_verify_token` (+ legacy `verify_token`), `whatsapp_custom_templates`, `whatsapp_template_bindings`, `whatsapp_accept_unknown_numbers` (`on` puts the OLD behaviour back: a Cloud payload whose `phone_number_id` matches no account files into the unattributed bucket instead of being recorded and dropped — organization-wide, off by default, `Channels/CloudInboxRouting`), `voice_twilio_settings`, `baileys_url`, `baileys_secret`, SMTP defaults |
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
