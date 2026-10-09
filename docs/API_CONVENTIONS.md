# API conventions

Everything is served by the C# API (`backend-csharp/src/ChatBranch.Api/`,
ASP.NET Core minimal APIs, one `Endpoints/*Endpoints.cs` file per area, wired in
`Program.cs`). The dashboard and every external caller use the same routes.

| Prefix | What it is |
|---|---|
| `/api/v1/…` | the "versioned" surface kept from the Django DRF days: whoami, contacts, entities ("brands"), users, record sync. Errors use an envelope (below) |
| `/api/…` | everything else — the inbox, channels, flows, AI, analytics, settings, integrations, platform. Plain JSON in / JSON out |
| `/mcp` | ChatBranch as an MCP server (JSON-RPC 2.0 over HTTP POST) |
| `/widget.js` | the website widget script |

There is no OpenAPI document and no `/api/v1/docs` page; each endpoint file
opens with a doc comment describing its routes, and that is the reference.

---

## Authentication

```
Authorization: Bearer <token>
```

Two kinds of credential are accepted on every authenticated route:

- **Session tokens** — from `POST /api/auth/login` `{username, password}`. One
  `crm_authtoken` row per login (concurrent sessions are fine);
  `POST /api/auth/logout` revokes it. `GET /api/auth/whoami` /
  `GET /api/v1/whoami` return the user, role, tenant, accessible entity slugs
  and resolved **capabilities**.
- **API keys** — `cbk_<prefix>_<secret>`, created at **API keys & MCP**
  (`/developers`, `POST /api/developers/keys`, capability `api_keys.manage`) and
  shown once. Send as `Authorization: Bearer cbk_…` or `X-Api-Key: cbk_…`. A key
  acts as its owning user — same role, capabilities and entity scope — optionally
  narrowed to a list of capabilities; a scoped key holding no `.manage`/`.send`
  capability is read-only. Everything a key does is recorded in the activity log
  with source `api` (or `mcp`).

Authorization is two layers:

1. A coarse **policy** on every route: `authenticated`, `admin+` or
   `superadmin`. **There is no fallback policy** — a route without
   `RequireAuthorization(...)` would be public, so every route carries one or an
   explicit `AllowAnonymous()`.
2. A **capability** check inside the handler where the permission matrix
   applies (`AdminEndpoints.HasCapabilityAsync`,
   `SettingsEndpoints.RequireCapability`) — e.g. `tags.manage`, `flows.manage`,
   `ai.monitor`, `settings.custom_fields.manage`. Superadmin always passes.
   Catalog: [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md).

On top, **entity scope** bounds the ROWS: list endpoints accept `?entity=<slug>`
and narrow to that entity's subtree, but never beyond what the caller may see.
Per-conversation routes (`/api/whatsapp/threads/{key}/…`,
`/api/messaging/threads/{channel}/{key}/…`, the inbox's `{channel}/{key}`
routes, `/api/inbox/threads/{channel}/{key}/handover`) check it per request
(`.RequireThreadAccess(cap)`, `Auth/ThreadAccessFilter.cs`): a conversation
outside the caller's scope answers **404**, exactly like one that does not exist.

> `X-Role` is a bootstrap-only header: it is honoured **only while zero
> `crm_profile` rows exist**, so a database can be poked before the seed runs.

### Anonymous endpoints — the complete list

| Path | Why it is open | What protects it |
|---|---|---|
| `POST /api/auth/login` | obviously | credentials |
| `GET /api/whatsapp/status`, `GET /api/messaging/status` | pre-login sandbox/configured banner | exposes flags only |
| `GET /api/whatsapp/webhook`, `GET /api/messaging/webhook` | Meta's verification handshake | the verify token (Setting `whatsapp_verify_token`; the shipped default is never accepted) |
| `POST /api/whatsapp/webhook` | Meta Cloud inbound + delivery statuses | `X-Hub-Signature-256` HMAC, **required once any Cloud account is configured** (sandbox accepts unsigned) |
| `POST /api/messaging/webhook` | Messenger / Instagram inbound + receipts | `X-Hub-Signature-256` HMAC, **always required**; no Meta app secret configured → 401 |
| `POST /api/messaging/webhook/telegram/{token}` | Telegram inbound | the bot token in the path (constant-time match, unknown → 404) + `X-Telegram-Bot-Api-Secret-Token` once the webhook was registered from the dashboard (401 without) |
| `POST /api/messaging/webhook/{line,viber,sms,tiktok,douyin,zalo}/{account_id}`, `GET`/`POST /api/messaging/webhook/wechat/{account_id}` | inbound for the newer channels, one URL per account | that account's own signature (`X-Line-Signature`, `X-Viber-Content-Signature`, `X-Twilio-Signature`, WeChat's sha1 signature + 5-min freshness + single-use nonce (+ AES `msg_signature` in safe mode), `TikTok-Signature`, `X-Douyin-Signature`, `X-ZEvent-Signature`). Bad / missing signature → 401; **secret never configured → 503**; unknown account → 404 |
| `POST /api/whatsapp/baileys/{inbound,status-event,message-status,media,call-event}` | Baileys gateway pushes | `X-Baileys-Secret` shared secret (401 without) |
| `POST /api/integrations/webhooks/{shopify,stripe}` | commerce webhooks | HMAC (Shopify base64, Stripe `t=…,v1=…` + 300 s replay window). Bad signature → 401; **secret never configured → 503**, never "accept unsigned" |
| `GET /api/integrations/oauth/google/callback` | the browser returns from Google with no bearer token | the HMAC-signed, 600 s `state` (PKCE S256) |
| `GET /widget.js`, `GET /api/widget/{token}/config`, `POST /api/widget/{token}/session`, `GET/POST /api/widget/{token}/messages` | the website widget runs on other people's sites | permissive `widget` CORS policy; no credentials; each visitor sees only their own conversation, keyed by a server-minted 128-bit session id |
| `GET /api/help/articles`, `GET /api/help/articles/{slug}` | the public Help Center | only `Published` knowledge items |
| `GET /api/pronunciation`, `GET /api/voices`, `GET /api/voice/stt-settings`, `GET /api/voice/llm-settings` | read by the voice tooling | non-secret configuration |
| `POST /mcp` (and `GET`/`DELETE /mcp` → 405) | MCP clients authenticate inside the handler | **an API key is required** (Bearer `cbk_…` or `X-Api-Key`); anything else gets a JSON-RPC auth error |

Everything else — including every call-control route (`/api/calls/*`,
`/api/whatsapp/threads/{key}/call/*`) — needs a signed-in user or an API key.

---

## Responses

- **`/api/v1` errors** use an envelope:
  ```json
  {"error": {"code": "not_found", "message": "No such contact (or not visible to you)."}}
  ```
  `code` is a stable slug (`bad_request`, `forbidden`, `not_found`, `conflict`,
  …); `message` is operator-facing text the dashboard shows verbatim.
- **Everything else** returns bare objects and errors as `{"detail": "…"}` (some
  older routes `{"error": "…"}`) with an appropriate status. Do not assume one
  client parses both.
- **Lists** are bare arrays inside a named key (`{"contacts": [...]}`,
  `{"tags": [...]}`). Paged lists use a cursor or `before`/`limit`:
  `GET /api/inbox/threads` (`limit`, `cursor` → `next_cursor`, `count_only=1`),
  `GET /api/flows/runs` (`page`/`page_size`), `GET /api/audit`, message history
  (`?limit=&before=`).
- **Wire format is snake_case** on the newer surfaces and wherever the frontend
  consumed Django's exact keys; a few older DTOs (`Dtos.cs`) are camelCase.
  Follow the file you are in.
- **Timestamps** are ISO-8601 UTC with a `Z`.

## Conventions that hold everywhere

1. **Masked secrets round-trip.** A credential is returned as `••••1234`; a
   value containing `••` sent back means "unchanged" and the stored secret is
   kept. Applies to channel tokens, SMTP passwords, AI provider keys, Twilio
   voice settings, integration credentials, MCP server tokens.
2. **Mutations are audited.** Domain writes add a `crm_auditlog` row with
   provenance (source, entity, method, path, status); `RequestAuditMiddleware`
   adds one generic row for any mutating request that wrote none of its own.
   Never log bodies, query strings or secrets.
3. **Sandbox never fails loudly.** With no provider configured, sends store with
   `status="sandbox"`, integration actions return `{ok:false, skipped:true,
   detail}`, and every read endpoint answers 200.
4. **Webhooks never 500 a batch.** Garbage is acked; side effects never throw
   into the response.
5. **Scope is bounded server-side.** `?entity=` can narrow but never widen what
   the caller's role and assigned entities allow.

---

## The surface, by area

Not every route — the endpoint files are authoritative — but every area.

| Area | Routes | File |
|---|---|---|
| Auth | `/api/auth/login`, `logout`, `whoami`, `preferences` (per-user appearance + notification prefs) | `AuthEndpoints`, `SettingsEndpoints.Appearance` |
| RBAC | `/api/auth/roles[/{slug}]`, `/api/auth/permissions` (matrix; PUT needs `rbac.manage`) | `AdminEndpoints` |
| v1 | `/api/v1/whoami`, `/api/v1/contacts[/{id}][/entities]`, `/api/v1/entities[/{id}]`, `/api/v1/brands`, `/api/v1/users[/{id}]`, `POST /api/v1/sync/records` | `ApiV1Endpoints`, `AdminEndpoints` |
| Contacts | `/api/contacts[/{id}]` (POST/PATCH bind `company_name` as well as `companyName`), `/import` (multipart .csv / .xlsx / .vcf or JSON rows, `?preview=1`; admin+ and `contacts.manage`), `/import/google` (the caller's own Google Contacts, preview), `/{id}/channels` (channel hub) + `/{id}/channels/discover`, `/identity-checks/run`, `/{id}/invites`, `/identify/{channel}/{key}` + `/contact`, `/{id}/ai-mode`, `/{id}/calls`, `/{id}/tags`; `/api/inboxes/{id}/identify` (per-inbox unknown-customer mode) | `ContactsEndpoints`, `ContactIdentityEndpoints`, `AiControlsEndpoints`, `TagsEndpoints` |
| Inbox | `GET /api/inbox/threads` (server-filtered list; `handover=open\|any\|none`, `handover_kind`), `POST /api/inbox/threads/bulk` (`ai_on\|ai_off\|resolve\|reopen\|assign` over picked ids or a list filter, ≤1000, `chats.manage`), `POST /api/inbox/threads/{channel}/{key}/handover` (`take\|continue\|flag`, `chats.send`), `/api/views` (saved views), `GET /api/whatsapp/stream` (SSE, all channels), `POST /api/whatsapp/simulate`, `/api/whatsapp/threads/{key}/…` (send, send-template, send-media, ai, status, labels, account, call/*), `/api/messaging/threads/{channel}/{key}/…` (send, ai, status, assign, labels, note, link, summary), `/api/messaging/canned`, `/api/messaging/timeline`, `/api/messaging/engagement` | `InboxThreadsEndpoints`, `AiControlsEndpoints`, `SavedViewsEndpoints`, `InboxEndpoints`, `WhatsAppThreadsEndpoints`, `MessagingEndpoints` |
| Teams + automations | `/api/teams`, `/api/teams/assign`, `/api/automations[/{id}]`, `/api/automations/catalog`, `/{id}/test` | `TeamsEndpoints`, `AutomationsEndpoints` |
| Channels | `/api/whatsapp/accounts[/{id}]` (+ `/test`, `/baileys/start\|status\|logout`), `/api/messaging/accounts[/{id}]` (+ `/test`, `/set-webhook` for Telegram, LINE and Viber; the newer channels' fields in `MoreChannelAccounts`), `/api/widgets[/{id}]`, `/api/whatsapp/templates`, `/api/whatsapp/settings`, `/api/whatsapp/routing-rules`, `/api/whatsapp/voice-settings`, `/api/whatsapp/channels/health`, `/api/whatsapp/recovery`, `/api/whatsapp/assignments` + `/rebalance` | `WhatsAppSettingsEndpoints`, `VoiceMessagingEndpoints`, `WidgetAdminEndpoints`, `AssignmentsEndpoints` |
| Calls + voice | `/api/calls/dial`, `/api/calls/{room}[/live\|say\|hangup\|escalate]`, `/api/call-logs`, `/api/voices`, `/api/voice/*` (preview, clone, import, STT/LLM settings), `/api/pronunciation`, `/api/calls/script-templates` | `CallsVoiceEndpoints`, `VoiceMessagingEndpoints`, `PronunciationEndpoints`, `ContactsEndpoints` |
| Flows | `/api/flows/library`, `/api/flows/runs[/{id}]`, `/api/flows/{flowId}/stats`, `/api/flows/{flowId}/start`, `/api/flows/queue`, `/api/flows/enqueue`, `/api/flows/tick` | `FlowsEndpoints` |
| AI | `/api/aicore/persona`, `/api/aicore/knowledge[/{id}]` + `/preview` (`{query}` → the exact knowledge + learned blocks + every scored hit) + `/upload` (multipart `file` PDF/.txt/.md/.csv ≤10 MB, `kind`, `title`, `entity`), `/api/aicore/skills` + `/draft`, `/test` (`kind:"app"` = an availability dry run that books nothing), `/builtins`, `/apps` (the connected-app catalog), `/api/aicore/reply`, `/api/aicore/booking-hours` (legacy — stored, read by nothing) | `AiEndpoints`, `AiSkillsEndpoints` |
| AI monitor | `/api/ai/monitor` (list, `/{id}`, `/{id}/approve\|send\|reject`, `/bulk`, `/stats`, `/tick`; drafts carry `bookings[]`, `my_feedback`, `grounding`), `/api/ai/behaviour` (GET/PUT/DELETE an entity override) | `AiMonitorEndpoints`, `AiMonitorLearning` |
| AI controls + handover | `/api/ai/controls?entity=` (master switch, schedule, never-AI tags; GET `ai.monitor`/`ai.behaviour.manage`/`chats.view`, PUT `ai.behaviour.manage`), `/api/contacts/{id}/ai-mode` (PUT `contacts.manage`), `GET /api/ai/handovers/stats` (`ai.monitor` or `chats.view`) | `AiControlsEndpoints` |
| AI learning | `/api/ai/feedback` (POST a rating, `GET ?message_ids=`, `DELETE /{id}` your own), `/api/ai/lessons` (list, POST, PATCH `/{id}`, DELETE `/{id}` admin+, `POST /{id}/to-knowledge`), `/api/ai/learning/status`, `/reindex`, `/stats` — reads `ai.monitor`, writes `ai.behaviour.manage` | `LearningEndpoints` |
| Bookings | `GET /api/bookings` (`chats.view`, scoped like conversations; `contact_id`, `thread=<channel>:<key>`, `status`, `from`, `to`, `entity`, `upcoming`, `limit`; `start`/`end` carry the calendar's offset, e.g. `+08:00`), `POST /api/bookings/{id}/cancel` (`chats.send`) | `BookingsEndpoints` |
| Tags + bindings | `/api/tags[/{id}]`, `/{id}/targets`, `/api/tags/assignments`, `/api/entities/{slug}/tags`, `/api/bindings/catalog`, `/api/bindings/{kind}` | `TagsEndpoints`, `BindingsEndpoints` |
| Analytics | `/api/analytics/summary`, `/usage` (admin+), `/reports[.csv]`, `/flows`, `/api/live` | `AnalyticsEndpoints`, `ReportsEndpoints`, `FlowsEndpoints`, `LiveEndpoints` |
| Shell | `GET /api/search` (command palette; each result type gated + scoped like its page) | `SearchEndpoints` |
| Notifications | `/api/notifications` (inbox: list, read/unread, read-all, mark, delete, preferences), `/api/auth/notifications` (bell), `/api/auth/notification-prefs` (org mute switches) | `NotificationEndpoints*` |
| Settings | `/api/settings` (entity-scoped key/value, PUT superadmin), `/api/settings/appearance` (org default), `/api/settings/custom-fields`, `/api/settings/ai-providers` | `SettingsEndpoints*`, `CustomFieldsEndpoints`, `VoiceMessagingEndpoints` |
| Integrations | `/api/integrations`, `/providers`, `/mine` (your own Google connections — any signed-in user), `/accounts[/{id}]` (+ `/test`, `/disconnect` — your own row needs no capability), `/settings`, `/oauth/{provider}/start` (`?return=connections` lands the callback back on Settings → My connections), `/oauth/google/callback`, `/{provider}/connect`, `/action`, `/webhooks/{shopify,stripe}`, `/api/integrations/mcp[/{id}]` (external MCP servers, + `/test`) | `IntegrationsEndpoints`, `McpEndpoints` |
| Activity | `/api/audit`, `/api/audit.csv`, `/api/audit/facets` (`activity.view`); legacy `/api/auth/audit` | `AuditEndpoints`, `NotificationEndpoints` |
| Developers | `/api/developers/keys[/{id}]`, `/api/developers/mcp` (+ `/test`) | `DevelopersEndpoints` |
| Assistant | `POST /api/assistant/chat`, `/confirm`, `/cancel` | `AssistantEndpoints` |
| Public | `/api/help/articles[/{slug}]`, `/api/widget/{token}/*`, `/widget.js` | `HelpCenterEndpoints`, `WidgetEndpoints` |
| Webhooks | `/api/whatsapp/webhook`, `/api/messaging/webhook` (Messenger/Instagram), `/api/whatsapp/baileys/*` | `WebhooksEndpoints`, `MetaWebhookEndpoints` |
| Telegram inbound | `/api/messaging/webhook/telegram/{token}` | `TelegramWebhookEndpoints` |
| Newer channels inbound | `/api/messaging/webhook/{line\|viber\|sms\|wechat\|tiktok\|douyin\|zalo}/{account_id}` (WeChat also `GET` for URL verification) | `MoreChannelWebhookEndpoints` |
| Email + messaging attachments | `POST /api/messaging/email/sync` (IMAP pull), `POST /api/messaging/threads/{email\|telegram\|facebook\|instagram\|line\|viber\|sms\|wechat\|tiktok\|douyin\|zalo}/{key}/send-media` (multipart `file`; the channel's transport may refuse a type it can't carry) | `MessagingTransportEndpoints` |
| Media + recordings | `GET /api/whatsapp/media/{id}`, `GET /api/messaging/media/{id}` (the attachment of message `id`, every channel), `GET /api/whatsapp/threads/{key}/call/recordings`, `GET /api/whatsapp/calls/recording/{name}`, `GET /api/calls/recording/{call_id}` — signed-in + `chats.view` / `call_logs.view` + entity scope, Range supported | `WhatsAppMediaEndpoints`, `WhatsAppThreadsEndpoints` |

**Not present in the C# API** (despite older docs and some UI text):
`/api/auth/users` (use `/api/v1/users`), `/api/v1/ping`, OpenAPI.

---

## MCP server (`POST /mcp`)

ChatBranch speaks the Model Context Protocol over **Streamable HTTP**, answered
as plain JSON (no server push, stateless sessions — an `Mcp-Session-Id` is
issued on `initialize` and accepted, never required). Methods: `initialize`,
`notifications/initialized`, `ping`, `tools/list`, `tools/call`; batches are
accepted. Authenticate with an **API key**. The tools
(`Api/Tools/BuiltinTools.cs`) act as the key's user with the same capabilities
and entity scope: `search_contacts`, `get_contact`, `create_contact`,
`update_contact`, `list_conversations`, `get_conversation`, `send_message`,
`add_note`, `set_conversation_status`, `assign_conversation`, `list_templates`,
`search_knowledge`, `list_flows`, `get_analytics_summary`, `list_activity`,
`list_tags`. Write tools call the dashboard's own endpoints in-process, so every
guard (opt-out, 24h window, capability, scope) applies. `/developers` shows a
ready-made client config.

## The live channel that is not request/response

`GET /api/whatsapp/stream` is a **Server-Sent Events** stream covering *all*
chat channels (it lives under `whatsapp` for historical reasons). It sends
`event: change` whenever the change token moves (thread `updated_at` + message
count, polled every 2 s, keep-alive comments in between) and closes after ~10
quiet minutes; clients reconnect. The
dashboard holds ONE connection per tab (`frontend/src/lib/chatStream.ts`) and
re-fetches only on `change`. **Any write path that bypasses `ThreadStore` must
bump `Thread.UpdatedAt`** or the dashboard will not refresh.
