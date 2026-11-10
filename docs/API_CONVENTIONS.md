# API conventions

Everything is served by the C# API (`backend-csharp/src/ChatBranch.Api/`,
ASP.NET Core minimal APIs, one `Endpoints/*Endpoints.cs` file per area, wired in
`Program.cs`). The dashboard and every external caller use the same routes.

| Prefix | What it is |
|---|---|
| `/api/v1/…` | the "versioned" surface kept from the Django DRF days: whoami, contacts, entities ("brands"), users, record sync. Errors use an envelope (below) |
| `/api/…` | everything else — the inbox, channels, flows, AI, analytics, settings, integrations, platform. Plain JSON in / JSON out |
| `/api/mcp` (also `/mcp`) | ChatBranch as an MCP server (JSON-RPC 2.0 over HTTP POST) |
| `/api/widget.js` (also `/widget.js`) | the website widget script |

`/api/mcp` and `/api/widget.js` are the addresses the dashboard hands out: a
single-origin deployment usually proxies only `/api/` to the API
(`deploy/nginx-chatbranch.conf`), where a bare `/mcp` or `/widget.js` would
reach the dashboard and 404. The root paths stay for API-origin deployments
and for widget tags already pasted into sites.

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
  and resolved **capabilities** (whoami also `restriction` and `two_factor`).
  Failed sign-ins are throttled per username AND per client IP (5 / 30 in 15 min
  → 429 `{detail, code: "too_many_attempts", retry_after}` + `Retry-After`); an
  unknown username answers exactly like a wrong password (401
  `{code: "invalid_credentials"}`). **Two-step sign-in:** when the person has it
  on, the login answers `{two_factor_required: true, challenge, expires_in,
  issuer}` instead of a token, and `POST /api/auth/login/2fa {challenge, code}`
  (a code from the authenticator app or a recovery code; the challenge is signed,
  lives 5 minutes and signs in once) answers the token. A login answer may carry
  `restriction` (`change_password` — a seeded password on a real install — or
  `enroll_2fa` — the organization requires two-step sign-in): such a session gets
  403 `{code: "account_setup_required", restriction}` from everything except
  whoami, logout, the own-account / two-step setup routes and the shell's
  appearance reads. Details: [security.md](security.md).
- **Your own account** (Settings → My account, any signed-in user, always the
  CALLER — no id in the route): `GET /api/auth/me/account` (username, first /
  last / display name, email, role, entities, last sign-in, active sessions),
  `PATCH /api/auth/me/account {first_name?, last_name?, email?}` (email
  validated and unique across users → 409; the username never changes),
  `POST /api/auth/me/password {current_password, new_password}` (the password
  policy — ≥ 8 characters on a real install, not the username, not a common
  password; ≥ 8 on a demo; ≠ the current one; a refusal carries `code`; signs out
  every OTHER session and answers `{signed_out}`; wrong current passwords count
  toward the sign-in lock → 429; lifts a `change_password` restriction; audit
  `user.password_changed`, no secret in it),
  `POST /api/auth/me/sessions/revoke-others`, and two-step sign-in:
  `GET /api/auth/me/2fa` (status, never a secret), `POST /api/auth/me/2fa/setup`
  (`{password}` only when it is already on — a new phone) → `{manual_key,
  otpauth_uri, qr_svg, expires_at}`, `POST …/2fa/confirm {code}` →
  `{recovery_codes}` (10, shown once), `POST …/2fa/recovery-codes {password, code}`
  and `POST …/2fa/disable {password, code}` (409 while the organization requires
  it). The writes are **session-only** — an API key gets 403 — and none of them is
  an MCP tool.
- **API keys** — `cbk_<prefix>_<secret>`, created at **API keys & MCP**
  (`/developers`, `POST /api/developers/keys`, capability `api_keys.manage`) and
  shown once. Send as `Authorization: Bearer cbk_…` or `X-Api-Key: cbk_…`. A key
  acts as its owning user — same role, capabilities and entity scope — optionally
  narrowed to a list of capabilities; a scoped key holding no `.manage`/`.send`
  capability is read-only. Everything a key does is recorded in the activity log
  with source `api` (or `mcp`).

Authorization is two layers:

1. The **`authenticated` policy** on every signed-in route. **There is no
   fallback policy** — a route without `RequireAuthorization(...)` would be
   public, so every route carries one or an explicit `AllowAnonymous()`. No
   route is gated by a ROLE (`admin+` / `superadmin` policies are gone from the
   route table — `PermissionMatrixTests` fails on one).
2. A **capability** from the permission matrix — the Permissions page governs
   every action: the endpoint filter `.RequireCapability("x")` /
   `.RequireAnyCapability("x", "y")` (`Auth/CapabilityFilter.cs`), or a check
   inside the handler where the write reads its body first
   (`AdminEndpoints.HasCapabilityAsync`, `SettingsEndpoints.RequireCapability`)
   — e.g. `tags.manage`, `flows.manage`, `channels.manage`, `ai.monitor`,
   `settings.custom_fields.manage`. Superadmin always passes. Catalog:
   [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md).

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
| `POST /api/auth/login` | obviously | credentials; failures throttled per username + per IP (429), per-IP request limit |
| `POST /api/auth/login/2fa` | the second step of a two-step sign-in | the challenge from a correct password (Data Protection-encrypted + signed, 5 minutes, single use) + a TOTP or recovery code; throttled like passwords |
| `GET /api/auth/set-password/{token}`, `POST /api/auth/set-password` | the welcome email's (or an admin's reset email's) link to choose a password — the person has no password yet | a 256-bit one-time token (only its SHA-256 stored, compared in constant time), 72 h, single use, revoked by a newer link / deactivation / an email change; ONE generic 404 for unknown, used, revoked or expired; the password policy; per-IP limit like sign-in |
| `GET /api/whatsapp/status`, `GET /api/messaging/status` | pre-login sandbox/configured banner | exposes flags only |
| `GET /api/whatsapp/webhook`, `GET /api/messaging/webhook` | Meta's verification handshake | the verify token (Setting `whatsapp_verify_token`; the shipped default is never accepted) |
| `POST /api/whatsapp/webhook` | Meta Cloud inbound + delivery statuses | `X-Hub-Signature-256` HMAC, **required once any Cloud account is configured** (sandbox accepts unsigned) |
| `POST /api/messaging/webhook` | Messenger / Instagram inbound + receipts | `X-Hub-Signature-256` HMAC, **always required**; no Meta app secret configured → 401 |
| `POST /api/messaging/webhook/telegram/{token}` | Telegram inbound | the bot token in the path (constant-time match, unknown → 404) + `X-Telegram-Bot-Api-Secret-Token` once the webhook was registered from the dashboard (401 without) |
| `POST /api/messaging/webhook/{line,viber,sms,tiktok,douyin,zalo}/{account_id}`, `GET`/`POST /api/messaging/webhook/wechat/{account_id}` | inbound for the newer channels, one URL per account | that account's own signature (`X-Line-Signature`, `X-Viber-Content-Signature`, `X-Twilio-Signature`, WeChat's sha1 signature + 5-min freshness + single-use nonce (+ AES `msg_signature` in safe mode), `TikTok-Signature`, `X-Douyin-Signature`, `X-ZEvent-Signature`). Bad / missing signature → 401; **secret never configured → 503**; unknown account → 404 |
| `POST /api/messaging/webhook/whatsapp-ycloud/{account_id}`, `POST /api/messaging/webhook/whatsapp-twilio/{account_id}` | WhatsApp through a BSP (YCloud / Twilio): inbound + delivery statuses, one URL per account | that account's own signature — `YCloud-Signature: t=…,s=…` (hex HMAC-SHA256 of `t.body` with the endpoint's signing secret, 5-min window) / `X-Twilio-Signature` (the Auth Token). Bad / missing signature → 401; **secret never configured → 503**; unknown account → 404 |
| `GET /api/media/public/{token}` | a provider fetching an outbound attachment it can't take as an upload (Twilio's `MediaUrl`, YCloud's fallback `link`) | an HMAC-signed token (key in Setting `media_link_secret`) naming ONE stored file, valid 6 hours; resolved under the media root only; `nosniff`, non-renderable types as downloads |
| `GET /.well-known/oauth-protected-resource[/…]`, `GET /.well-known/oauth-authorization-server[/…]` | How an AI app discovers that this ChatBranch can be signed into (RFC 9728 / RFC 8414) | public by necessity — a client reads them BEFORE it has any credential; they carry no data, only URLs |
| `POST /api/oauth/register` | An AI app registers ITSELF (RFC 7591) | open by design: a client id alone can do nothing — it can only ask a PERSON to approve it |
| `GET /api/oauth/authorize` | The start of a sign-in: stores the request and redirects to the dashboard's Allow page | the browser arrives before anything; the request id it mints is meaningless alone, and the person's own session is what approves it |
| `POST /api/oauth/token`, `POST /api/oauth/revoke` | Code → tokens, refresh, and handing a token back | a public client holds no secret; PKCE S256 + an exact `redirect_uri` match + a single-use 5-minute code are what stand in for one |
| `POST /api/whatsapp/baileys/{inbound,from-phone,history,recall,edited,status-event,message-status,media,call-event}` | Baileys gateway pushes (`history` = the number's existing chats, replayed when it links) | `X-Baileys-Secret` shared secret (401 without) |
| `POST /api/commerce/inbound` | the marketplace seller service pushing a buyer message (Shopee, Lazada, TikTok Shop) | `X-Commerce-Secret` shared secret — **503 when none is configured**, 401 when it is wrong |
| `POST /api/zernio/webhook` | Zernio events: comments, Google reviews, DMs, account connect / disconnect, post lifecycle | `X-Zernio-Signature` = lowercase hex HMAC-SHA256 of the raw body with Setting `zernio_webhook_secret` (env `ZERNIO_WEBHOOK_SECRET`, generated by `POST /api/zernio/webhook/register`) — **503 when none is configured**, 401 when missing / wrong; deduped on `X-Zernio-Event-Id` (`crm_zernioevent`) |
| `POST /api/integrations/webhooks/{shopify,stripe}` | commerce webhooks | HMAC (Shopify base64, Stripe `t=…,v1=…` + 300 s replay window). Bad signature → 401; **secret never configured → 503**, never "accept unsigned" |
| `GET /api/integrations/oauth/google/callback` | the browser returns from Google with no bearer token | the HMAC-signed, 600 s `state` (PKCE S256) |
| `GET /api/widget.js` (and `GET /widget.js`), `GET /api/widget/{token}/config`, `POST /api/widget/{token}/session`, `GET/POST /api/widget/{token}/messages` | the website widget runs on other people's sites | permissive `widget` CORS policy; no credentials; each visitor sees only their own conversation, keyed by a server-minted 128-bit session id |
| `GET /api/help/articles`, `GET /api/help/articles/{slug}` | the public Help Center | only `Published` knowledge items |
| `GET /api/documents/public/{token}` | the customer's download link for a document (PDF / QR code) on a channel that can't carry files (LINE, Viber, SMS, TikTok, Douyin, the website widget) or when a file send was refused | a server-minted 128-bit random token per document; only SENT / issued documents (never pending or void), and only for 90 days; a server-rendered PDF / PNG, `nosniff` |
| `GET /api/billing/ai/pdf/s/{token}` | the AI bill PDF opened by the installed iOS app (a navigation cannot carry the bearer token) — minted by the signed-in `POST /api/billing/ai/pdf/link` (`billing.view`, sessions only) | an HMAC-SHA256-signed token (per-process key) binding the person, month, company and language, valid 5 minutes; on open the person must still be active and hold `billing.view`, and the bill is built AS them with the same scope rules (out of scope = 404); `inline`, `no-store`, `nosniff`, `no-referrer` |
| `GET /api/media/s/{token}` | a conversation's attachment played / downloaded by the inbox (`<audio>` / `<video>` / a download link cannot carry the bearer token, and blob-fetching a 20 MB video first is slow) — the `stream_url` / `download_url` / `alt_url` of a message payload, minted only for a signed-in session | an HMAC-SHA256-signed token (per-process key) binding the message, the profile and its username, valid 60–90 minutes (expiry on a 30-minute grid); on open the person must still be active, hold `chats.view` and see the conversation (out of scope / tampered = 404, expired = 410); Range, `nosniff`, sandbox CSP, `no-referrer`; `?download=1` = attachment with the original name, `?format=m4a` = AAC rendering of Ogg/Opus/AMR audio (ffmpeg) |
| `GET /api/pronunciation`, `GET /api/voices`, `GET /api/voice/stt-settings`, `GET /api/voice/llm-settings` | read by the voice tooling | non-secret configuration |
| `POST /api/mcp` and `POST /mcp` (and `GET`/`DELETE` on either → 405) | MCP clients authenticate inside the handler | **an API key is required** (Bearer `cbk_…` or `X-Api-Key`); anything else gets a JSON-RPC auth error |

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
- **429 Too Many Requests** carries `Retry-After` (seconds) and
  `{detail, code, retry_after}` — `code` is `rate_limited` (the request rate
  limits: per IP for anonymous callers, per session / API key when signed in,
  stricter for sign-in, the website widget and public links; provider webhooks
  exempt) or `too_many_attempts` (failed passwords / codes / API keys). Back off
  for `Retry-After` and try again. Limits and knobs: [security.md](security.md).
- **Security headers**: every answer carries `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer` and `X-Robots-Tag: noindex, nofollow`; JSON adds
  `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` +
  `X-Frame-Options: DENY`; HSTS over https; no `Server` header.

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
| Contacts | `/api/contacts[/{id}]` (POST/PATCH bind `company_name` as well as `companyName`), `/import` (multipart .csv / .xlsx / .vcf or JSON rows, `?preview=1`; `contacts.manage`), `/import/google` (the caller's own Google Contacts, preview), `/{id}/channels` (channel hub) + `/{id}/channels/discover`, `/identity-checks/run`, `/{id}/invites`, `/identify/{channel}/{key}` + `/contact`, `/{id}/ai-mode`, `/{id}/calls`, `/{id}/tags`; `/api/inboxes/{id}/identify` (per-inbox unknown-customer mode) | `ContactsEndpoints`, `ContactIdentityEndpoints`, `AiControlsEndpoints`, `TagsEndpoints` |
| Inbox | `GET /api/inbox/threads` (server-filtered list; `handover=open\|any\|none`, `handover_kind`; `linked=1\|0` = from a contact / not in contacts; `sort=newest\|customer_newest\|oldest_waiting\|unread` — `customer_newest` = the customer's last inbound message, never-wrote chats last, paged by the offset `cursor`), `POST /api/inbox/threads/bulk` (`ai_on\|ai_off\|resolve\|reopen\|assign` over picked ids or a list filter, ≤1000, `chats.manage`), `POST /api/inbox/threads/{channel}/{key}/handover` (`take\|continue\|flag`, `chats.send`), `/api/views` (saved views), `GET /api/whatsapp/stream` (SSE, all channels), `POST /api/whatsapp/simulate`, `/api/whatsapp/threads/{key}/…` (send, send-template, send-media, send-voice — an operator's recorded voice note, re-containered to Ogg/Opus, ai, status, labels, account, call/*), `/api/messaging/threads/{channel}/{key}/…` (send, ai, status, assign, labels, note, link, summary, `messages/{pk}/react` — an emoji reaction, `""` removes it, WhatsApp + Telegram, `chats.send`; `messages/{pk}/recall` — WhatsApp's "delete for everyone" on a message WE sent, and `messages/{pk}/edit {text}` — its ~15-minute edit, both `chats.send` and both QR-paired numbers only (Meta's API has neither, so an official number answers 409 naming the reason); `messages/{pk}/forward {to_channel, to_key, to_thread_id?}` — a true WhatsApp forward into another conversation on the SAME channel, the target scoped like any conversation named in a body, 409 once the gateway no longer holds the original; `send-card {kind: location|contact|poll, …}` — a location, a contact card or a poll, QR-paired numbers (an official number answers 409 saying what it can and cannot carry); `…/send` takes `reply_to` = the quoted message pk on every send route), `/api/messaging/canned`, `/api/messaging/timeline`, `/api/messaging/engagement` | `InboxThreadsEndpoints`, `AiControlsEndpoints`, `SavedViewsEndpoints`, `InboxEndpoints`, `WhatsAppThreadsEndpoints`, `MessagingEndpoints`, `MessageReactionEndpoints`, `MessageActionEndpoints` |
| Teams + automations | `/api/teams`, `/api/teams/assign`, `/api/automations[/{id}]`, `/api/automations/catalog`, `/{id}/test` | `TeamsEndpoints`, `AutomationsEndpoints` |
| Marketplace shops | `/api/commerce/status`, `/api/commerce/shops/sync`, `/api/commerce/poll` — the bridge to the seller service that holds the Shopee / Lazada / TikTok Shop / Shopify grants | `CommerceEndpoints` |
| Social (Zernio) | `/api/zernio/status`, `/api/zernio/test`, `/api/zernio/webhook/register`, `POST /api/zernio/webhook`; `/api/social/accounts` (`{accounts:[…]}`), `POST /api/social/connect {platform, entity?}` → `{url}`, `DELETE /api/social/accounts/{id}`, `POST /api/social/threads/{key}/hide {hidden}`, `…/private-reply {text}`, `/api/social/posts[/{id}]` (GET / POST / PATCH / DELETE, `{posts:[…]}`), `/api/social/posts/{id}/retry`, `POST /api/social/media` (multipart `file` → `{url, type}`), `POST /api/social/posts/draft {brief, platforms[], tone?}` → `{content}`. Zernio not configured → 409 `{code: "zernio_not_configured"}` | `SocialEndpoints` |
| Channels | `/api/whatsapp/accounts[/{id}]` (+ `/test`, `/baileys/start\|status\|logout`), `/api/messaging/accounts[/{id}]` (+ `/test`, `/set-webhook` for Telegram, LINE and Viber; the newer channels' fields in `MoreChannelAccounts`), `/api/widgets[/{id}]`, `/api/whatsapp/templates`, `/api/whatsapp/settings`, `/api/whatsapp/routing-rules`, `/api/whatsapp/voice-settings`, `/api/whatsapp/channels/health`, `/api/whatsapp/recovery`, `/api/whatsapp/assignments` + `/rebalance` | `WhatsAppSettingsEndpoints`, `VoiceMessagingEndpoints`, `WidgetAdminEndpoints`, `AssignmentsEndpoints` |
| Calls + voice | `/api/calls/dial`, `/api/calls/{room}[/live\|say\|hangup\|escalate]`, `/api/call-logs`, `/api/voices`, `/api/voice/*` (preview, clone, import, STT/LLM settings), `/api/pronunciation`, `/api/calls/script-templates` | `CallsVoiceEndpoints`, `VoiceMessagingEndpoints`, `PronunciationEndpoints`, `ContactsEndpoints` |
| Flows | `/api/flows/library`, `/api/flows/runs[/{id}]`, `/api/flows/{flowId}/stats`, `/api/flows/{flowId}/start`, `/api/flows/queue`, `/api/flows/enqueue`, `/api/flows/tick` | `FlowsEndpoints` |
| AI | `/api/aicore/persona`, `/api/aicore/knowledge[/{id}]` + `/preview` (`{query}` → the exact knowledge + learned blocks + every scored hit) + `/upload` (multipart `file` PDF/.txt/.md/.csv ≤10 MB, `kind`, `title`, `entity`), `/api/aicore/skills` + `/draft`, `/test` (`kind:"app"` = an availability dry run that books nothing), `/builtins`, `/apps` (the connected-app catalog), `/api/aicore/reply`, `/api/aicore/booking-hours` (legacy — stored, read by nothing); the Documents app's designer (`/skills/documents/logo`, `/letterhead`, `/template/analyze`, `/template/assist`) and custom layout (`/skills/documents/html/preview` `{html, fields, type, language, config?}` → `{ok, html}` sanitized for a sandboxed iframe or `{ok:false, error, line}`; `/html/draft` multipart design → a whole layout; `/html/assist` `{html, fields, instruction}`) | `AiEndpoints`, `AiSkillsEndpoints`, `DocumentTemplateEndpoints`, `DocumentHtmlEndpoints` |
| AI monitor | `/api/ai/monitor` (list, `/{id}`, `/{id}/approve\|send\|reject`, `/bulk`, `/stats`, `/tick`; drafts carry `bookings[]`, `my_feedback`, `grounding`), `/api/ai/behaviour` (GET/PUT/DELETE an entity override) | `AiMonitorEndpoints`, `AiMonitorLearning` |
| AI controls + handover | `/api/ai/controls?entity=` (master switch, schedule, never-AI tags; GET `ai.monitor`/`ai.behaviour.manage`/`chats.view`, PUT `ai.behaviour.manage`), `/api/contacts/{id}/ai-mode` (PUT `contacts.manage`), `GET /api/ai/handovers/stats` (`ai.monitor` or `chats.view`) | `AiControlsEndpoints` |
| AI learning | `/api/ai/feedback` (POST a rating, `GET ?message_ids=`, `DELETE /{id}` your own), `/api/ai/lessons` (list, POST, PATCH `/{id}`, DELETE `/{id}`, `POST /{id}/to-knowledge`), `/api/ai/learning/status`, `/reindex`, `/stats` — reads `ai.monitor`, writes `ai.behaviour.manage` | `LearningEndpoints` |
| AI playground | `GET /api/ai/playground/options?entity=` (providers + default models, never keys; the stored persona for `ai.behaviour.manage`; channels, the production policy, learning mode, custom fields), `POST /api/ai/playground/reply` (`{entity, channel, contact_id \| profile{name, language, tags, fields}, account_id?, thread?, messages: [{role: customer\|ai, text}], options{provider, model, persona, knowledge, lessons, skills, memory, show_prompt}}` → reply, bubbles, production verdict, grounding + hits, tools, previewed bookings, checks, retries, provider/model, tokens, cost estimate, latency, prompt?; ≤60 turns, ≤4000 chars, 30/min per user → 429), `POST /api/ai/playground/import` (`{channel, key, take?}` → a transcript, read only). Stateless, no side effects, no request-audit row; `ai.monitor` or `ai.behaviour.manage`, overrides `ai.behaviour.manage` | `AiPlaygroundEndpoints` |
| Bookings | `GET /api/bookings` (`chats.view`, scoped like conversations; `contact_id`, `thread=<channel>:<key>`, `status`, `from`, `to`, `entity`, `upcoming`, `limit`; `start`/`end` carry the calendar's offset, e.g. `+08:00`), `POST /api/bookings/{id}/cancel` (`chats.send`), `GET /api/bookings/calendars` + `GET /api/bookings/calendar-events?connection=&calendar=&from=&to=` (`chats.view`; the connected Google Calendar's own events, read-only, ≤62 days, `{ok, connection, calendar_id, calendars, timezone, from, to, truncated, events: [{id, title, all_day, start, end, day, location, link, free, chatbranch, booking}]}` — `ok:false` + `error` not_connected / google / no_calendar when it can't be read) | `BookingsEndpoints` |
| Tags + bindings | `/api/tags[/{id}]`, `/{id}/targets`, `/api/tags/assignments`, `/api/entities/{slug}/tags`, `/api/bindings/catalog`, `/api/bindings/{kind}` | `TagsEndpoints`, `BindingsEndpoints` |
| Analytics | `/api/analytics/summary`, `/usage` (`billing.manage`), `/api/billing/ai` + `/pdf` + `/settings` + `/quotas` (the AI bill, its PDF, its month-locked terms and the AI usage quotas, `AiBillingEndpoints`), `/reports[.csv]`, `/flows`, `/api/live` | `AnalyticsEndpoints`, `ReportsEndpoints`, `FlowsEndpoints`, `LiveEndpoints` |
| Shell | `GET /api/search` (command palette; each result type gated + scoped like its page) | `SearchEndpoints` |
| Notifications | `/api/notifications` (inbox: list, read/unread, read-all, mark, delete, preferences), `/api/auth/notifications` (bell), `/api/auth/notification-prefs` (org mute switches) | `NotificationEndpoints*` |
| Settings | `/api/settings` (entity-scoped key/value: GET `settings.raw.view`, PUT `settings.raw.manage` — superadmin by default), `/api/settings/appearance` (org default), `/api/settings/custom-fields`, `/api/settings/ai-providers` | `SettingsEndpoints*`, `CustomFieldsEndpoints`, `VoiceMessagingEndpoints` |
| Integrations | `/api/integrations`, `/providers`, `/mine` (your own Google connections — any signed-in user), `/accounts[/{id}]` (+ `/test` — a real probe for Zoom, `/disconnect` — your own row needs no capability), `/settings`, `/oauth/{provider}/start` (`?return=connections` lands the callback back on Settings → My connections), `/oauth/google/callback`, `/{provider}/connect`, `/action`, `/webhooks/{shopify,stripe}`, `/api/integrations/mcp[/{id}]` (external MCP servers, + `/test`) | `IntegrationsEndpoints`, `McpEndpoints` |
| Activity | `/api/audit`, `/api/audit.csv`, `/api/audit/facets` (`activity.view`); legacy `/api/auth/audit` | `AuditEndpoints`, `NotificationEndpoints` |
| Developers | `/api/developers/keys[/{id}]`, `/api/developers/mcp` (+ `/test`) | `DevelopersEndpoints` |
| Assistant | `POST /api/assistant/chat`, `/confirm`, `/cancel`; per-user history `GET/DELETE /api/assistant/conversations`, `GET/PATCH/DELETE /api/assistant/conversations/{id}` | `AssistantEndpoints` |
| Public | `/api/help/articles[/{slug}]`, `/api/widget/{token}/*`, `/api/widget.js` (+ `/widget.js`) | `HelpCenterEndpoints`, `WidgetEndpoints` |
| Webhooks | `/api/whatsapp/webhook`, `/api/messaging/webhook` (Messenger/Instagram), `/api/whatsapp/baileys/*`, `/api/commerce/inbound` (the marketplace seller service) | `WebhooksEndpoints`, `MetaWebhookEndpoints`, `CommerceEndpoints` |
| Push notifications | `/api/push/config`, `/api/push/devices[/{id}]`, `/api/push/test` (the caller's own devices), `/api/push/tick` (`notifications.manage`) | `PushEndpoints`, `PushWorker` |
| Media library | `POST /api/media/library` (multipart upload, `chats.send`), `GET /api/media/library` (recent + `?q=` + `?entity=`, `chats.view`), `GET …/{file_id}/file`, `DELETE …/{file_id}`, `POST …/upload-url` (a 15-minute presigned link) and `PUT /api/media/library/u/{token}` (anonymous — the signed token IS the credential, single-use) | `MediaLibraryEndpoints` |
| Send a file | `POST /api/messaging/send-file` (`chats.send`) — one file to one or up to 20 conversations by a SHORT reference: `media_url` (an https link, on EVERY channel — the server fetches it, Google Drive share links included), `file_id` (the media library), `source_message_id` (a file ChatBranch already stores, resent across channels) or `file_base64`. The file is fetched/read ONCE and each target gets its own result and its own guards; 200 whenever the file was readable | `MessagingSendFileEndpoints`, `RemoteMedia` |
| Send a sticker | `POST /api/messaging/send-sticker` (`chats.send`) — a picture, GIF or short clip shaped into a 512×512 WebP sticker and sent as one (WhatsApp, QR-paired numbers; an official number is refused with the reason). Takes the same four file references as `send-file`; the conversation is named in the body, so scope is `ThreadAccessFilter.InScope` | `MessagingStickerEndpoints`, `StickerImage` |
| Transactional send | `POST /api/messaging/notify` (`chats.send`) — ONE call for a message an app originated (a login code, an order update): finds or opens the conversation, routes, falls back to a template on a shut WhatsApp window, idempotent on the caller's `idempotency_key`. Opt-out always refuses (no `force`); `purpose: "otp"` skips the cold-text guard. It NEVER answers 404 — a client reads that as "no such route" — so an unknown entity is 422 and an out-of-scope one 403 | `MessagingNotifyEndpoints`, `ThreadOpen` |
| Telegram inbound | `/api/messaging/webhook/telegram/{token}` | `TelegramWebhookEndpoints` |
| Newer channels inbound | `/api/messaging/webhook/{line\|viber\|sms\|wechat\|tiktok\|douyin\|zalo}/{account_id}` (WeChat also `GET` for URL verification) | `MoreChannelWebhookEndpoints` |
| Email + messaging attachments | `POST /api/messaging/email/sync` (two-way IMAP sync: Inbox + Sent, read-only), `POST /api/messaging/threads/{email\|telegram\|facebook\|instagram\|line\|viber\|sms\|wechat\|tiktok\|douyin\|zalo}/{key}/send-media` (multipart `file`; the channel's transport may refuse a type it can't carry) | `MessagingTransportEndpoints` |
| Media + recordings | `GET /api/whatsapp/media/{id}`, `GET /api/messaging/media/{id}` (the attachment of message `id`, every channel), `GET /api/whatsapp/threads/{key}/call/recordings`, `GET /api/whatsapp/calls/recording/{name}`, `GET /api/calls/recording/{call_id}` — signed-in + `chats.view` / `call_logs.view` + entity scope, Range supported | `WhatsAppMediaEndpoints`, `WhatsAppThreadsEndpoints` |

**Not present in the C# API** (despite older docs and some UI text):
`/api/auth/users` (use `/api/v1/users`), `/api/v1/ping`, OpenAPI.

---

## MCP server (`POST /api/mcp`, also `/mcp`)

ChatBranch speaks the Model Context Protocol over **Streamable HTTP**, answered
as plain JSON (no server push, stateless sessions — an `Mcp-Session-Id` is
issued on `initialize` and accepted, never required). Methods: `initialize`,
`notifications/initialized`, `ping`, `tools/list` (cursor paging),
`tools/call`; batches are accepted. Authenticate with an **API key**. The
tools cover every operator feature of the dashboard (~250 — the registry is
`Api/Tools/ToolRegistry` over `BuiltinTools.cs` + the declarative
`ToolCatalog.*.cs`), act as the key's user with the same capabilities and
entity scope, and call the dashboard's own endpoints in-process, so every guard
(opt-out, 24h / social windows, capability, scope) applies. Destructive tools
need `"confirm": true`; a read-only key runs no write tool. What is NOT a tool
(credentials, RBAC edits, key minting, binary streams…) is listed with reasons
in `Api/Tools/McpExclusions.cs`. Full guide, client configs and the generated
catalogue: **[docs/mcp.md](mcp.md)**. `/developers` shows a ready-made client config.

## Operator assistant (`/api/assistant/*`)

"Ask ChatBranch" acts AS the signed-in user (or the API key's owner); every
route needs a signed-in caller and works on the **caller's own** conversations
only — another login's id is a `404`, superadmin included.

| Route | Body / query | Answer |
|---|---|---|
| `POST /api/assistant/chat` | `{message, conversation_id?, context:{path, entity}}` | `{reply, steps, pending_actions, navigation, mode, conversation_id, title, message_id}` |
| `POST /api/assistant/confirm` | `{id}` (an `act_…` id from `pending_actions`) | `{ok, id, status, message, result, navigation}` — `404` not yours, `409` already decided / `410` expired (both carry `status`) |
| `POST /api/assistant/cancel` | `{id}` | `{ok, id, status}` |
| `GET /api/assistant/conversations` | `?limit=` (1–100, default 30) `&before=` (the previous page's `next_before`) | `{conversations:[{id, title, entity_id, created_at, updated_at, message_count, snippet}], total, has_more, next_before}`, most recently used first |
| `GET /api/assistant/conversations/{id}` | — | `{id, title, entity_id, created_at, updated_at, messages:[{id, role, text, payload, created_at}]}` |
| `PATCH /api/assistant/conversations/{id}` | `{title}` (1–120 chars) | the conversation |
| `DELETE /api/assistant/conversations/{id}` | — | `{ok, deleted}` |
| `DELETE /api/assistant/conversations` | — | `{ok, deleted}` — clears all of the caller's chats |

- **The model's context is the stored conversation** (its last 12 turns, with
  the outcome of each confirm card), never history a client sends. A chat
  without `conversation_id` — or naming one that is not the caller's — starts a
  new conversation titled after the question. Legacy clients may still send
  `messages:[{role, content}]` instead of `message`: the last user entry is the
  question, and the rest is honoured as context only when no
  `conversation_id` is given.
- An assistant turn's `payload` is `{steps, pending_actions:[{id, tool, title,
  args, summary, expires_at, state, message?}], navigation, mode}`; confirm /
  cancel / expiry update the card's `state` on the stored turn, and a read
  reports a still-pending card that has since expired as `expired`.
- At most 100 conversations per user; starting one more prunes the least
  recently used. Deleting a chat cancels any write still waiting for
  confirmation in it. Deletes are recorded as `assistant.conversation.delete` /
  `assistant.conversation.clear` in the activity log; chat POSTs are not (the
  tools they run write their own rows).

## The live channel that is not request/response

`GET /api/whatsapp/stream` is a **Server-Sent Events** stream covering *all*
chat channels (it lives under `whatsapp` for historical reasons). It sends
`event: change` whenever the change token moves (thread `updated_at` + message
count, polled every 2 s, keep-alive comments in between) and closes after ~10
quiet minutes; clients reconnect. The
dashboard holds ONE connection per tab (`frontend/src/lib/chatStream.ts`) and
re-fetches only on `change`. **Any write path that bypasses `ThreadStore` must
bump `Thread.UpdatedAt`** or the dashboard will not refresh.
