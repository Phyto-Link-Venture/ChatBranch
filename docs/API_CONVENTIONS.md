# API conventions

ChatWood has **two REST surfaces**, and that is worth knowing up front:

| Surface | Style | Where |
|---|---|---|
| `/api/v1/…` | DRF class-based views, paginated, typed error envelope, OpenAPI schema | `backend/api/`, `backend/ingest/` |
| `/api/…` | plain Django function views, JSON in / JSON out | `backend/calls/`, `whatsapp/`, `messaging/`, `flows/`, `aicore/`, `integrations/`, `crm/` |

Both accept the **same bearer token**. The function views carry the bulk of the
product (the inbox, channels, flows, AI config, integrations) and are what the
dashboard talks to; `/api/v1` is the versioned, self-describing surface for
external callers. If you are adding an endpoint a third party will call, add it
under `/api/v1`.

---

## Authentication

```
Authorization: Bearer <token>
```

The token comes from `POST /api/auth/login` `{username, password}` and is stored
on `crm.Profile.token`. **It is rotated on every login** — logging in
invalidates the previous token. Session auth also works for the DRF browsable
API and Django admin.

Roles are `superadmin` / `admin` / `agent`, read from `crm.Profile.role`.
`/api/v1` enforces them with DRF permission classes (`api/permissions.py`); the
function views use a shared `_auth_required` + role-gate pattern.

**Open endpoints** (no bearer token), and only these:

| Path | Why |
|---|---|
| `GET /api/v1/ping` | liveness |
| `GET /api/v1/schema`, `GET /api/v1/docs` | discovery |
| `POST /api/auth/login` | obviously |
| `GET/POST /api/whatsapp/webhook` | Meta signs it (`X-Hub-Signature-256`, required once any account is configured) |
| `GET/POST /api/messaging/webhook` | Meta Messenger/IG — ⚠ **not signature-verified**, see [channel-adapter.md](channel-adapter.md) |
| `POST /api/messaging/webhook/telegram/<token>` | the bot token in the path is the auth |
| `POST /api/whatsapp/baileys/*` | `X-Baileys-Secret` shared header |
| `POST /api/integrations/webhook/{shopify,stripe}` | HMAC-verified. Missing/bad signature → 401; secret never configured → **503**. It never falls back to accepting unsigned traffic |
| `GET /api/integrations/oauth/<provider>/callback` | a browser arriving from Google carries no token — the signed, 600s-expiring `state` is the CSRF control |
| `GET /api/whatsapp/status`, `GET /api/messaging/status` | sandbox/configured flags for the pre-login banner |
| `POST /api/v1/sync/records` | `X-API-Key` (hashed, constant-time compared) |

> `X-Role` is a bootstrap-only header: it is honoured **only while zero
> `crm.Profile` rows exist**, so a fresh clone can be poked before `seed_demo`
> runs. Once any profile exists it is ignored.

---

## `/api/v1` responses

**Lists** are paginated (`api/pagination.py`, `PAGE_SIZE = 50`):

```json
{"count": 120, "page": 1, "num_pages": 3, "page_size": 50,
 "next": "…", "previous": null, "results": [ … ]}
```

`?page=` + `?page_size=` (max 200). A cursor paginator exists in the same module
for append-only lists.

**Errors** always use the envelope (`api/exceptions.py`):

```json
{"error": {"code": "not_found", "message": "…", "detail": {}}}
```

`code` is a stable slug: `unauthorized`, `forbidden`, `not_found`,
`method_not_allowed`, `rate_limited`, `bad_request`, `conflict`, …

**The function-view surface does not use either convention.** It returns bare
objects and `{"error": "…"}` or `{"detail": "…"}` with an appropriate status.
Do not assume one client can parse both.

## Discoverability

- OpenAPI 3 schema: `GET /api/v1/schema`
- Swagger UI: `GET /api/v1/docs`

Only `/api/v1` appears in the schema. The function views are documented by their
module docstrings — `backend/whatsapp/views.py` and `backend/messaging/views.py`
each open with a complete endpoint list.

---

## `/api/v1` endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/ping` | none | liveness |
| GET | `/api/v1/whoami` | bearer | current user + role + tenant + accessible brand slugs |
| GET | `/api/v1/contacts` | bearer | contacts, brand/tenant-scoped by role. `?search=` (name/phone/email/external_id), `?ordering=` (updated_at/name/status) |
| POST | `/api/v1/contacts` | admin+ | create a contact. 409 if the phone already exists in-tenant |
| GET | `/api/v1/contacts/<id>` | bearer | one contact |
| PATCH | `/api/v1/contacts/<id>` | admin+ | update |
| GET | `/api/v1/brands` | bearer | brands the caller can access |
| POST | `/api/v1/brands` | admin+ | create a brand |
| GET | `/api/v1/users` | admin+ | users (admin sees only their own tenant) |
| POST | `/api/v1/users` | admin+ | create a user (creation hierarchy — see [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md)) |
| POST | `/api/v1/users/<id>` | admin+ | update a user |
| DELETE | `/api/v1/users/<id>` | admin+ | deactivate a user |
| GET | `/api/v1/schema` · `/api/v1/docs` | none | OpenAPI schema · Swagger UI |
| POST | `/api/v1/sync/records` | `X-API-Key` | external-system sync intake → 202 + batch id |
| GET | `/api/v1/sync/health` | bearer | sync pipeline health |
| GET | `/api/v1/sync/batches/<id>` | bearer | one batch's status + row errors |

### User management is consolidated

`/api/v1/users` (+ `/<id>`) is the **canonical** endpoint. The legacy
`/api/auth/users` (+ `/<id>`) still works as a backward-compatible alias — both
call the single source of truth `crm/user_service.py`, so the RBAC/brand rules
live in one place. They differ only in error shape: v1 uses
`{error:{code,message}}`; the alias keeps the legacy `{detail}`.

---

## The function-view surface, by app

Not exhaustive — the authoritative list is each app's `urls.py`. This is the map.

| Prefix | App | What lives there |
|---|---|---|
| `/api/contacts…` | `calls` | contact list, per-contact channel hub, per-contact calls |
| `/api/calls/…` | `calls` | dial, live transcript, say, hangup, escalate, summary, recordings, script templates |
| `/api/voices`, `/api/voice/…` | `calls` | voice library, preview, clone, playground, STT/LLM settings |
| `/api/analytics/summary` | `calls` | the `/analytics` page payload |
| `/api/whatsapp/…` | `whatsapp` | webhook, accounts, templates, threads, send, media, SSE stream, channel health, assignments, routing rules, recovery, voice settings, WhatsApp calls, Baileys gateway callbacks |
| `/api/messaging/…` | `messaging` | Meta + Telegram webhooks, email sync, accounts, threads, canned responses, timeline, engagement |
| `/api/flows/…` | `flows` | library (GET/PUT), runs, queue, tick, status |
| `/api/aicore/…` | `aicore` | persona, knowledge (+ preview), skills |
| `/api/integrations/…` | `integrations` | providers, settings, accounts, test, disconnect, OAuth start/callback, Shopify + Stripe webhooks |
| `/api/auth/…` | `crm` | login, logout, me, notifications, notification-prefs, audit, users |

Three conventions hold across all of them:

1. **Masked secrets round-trip.** Any endpoint returning a credential emits a
   masked form (`••••1234`). A value containing `••` sent back is treated as
   "unchanged" and the stored secret is kept. This applies to WhatsApp account
   tokens, email SMTP passwords, Twilio voice settings and integration
   credentials alike.
2. **Mutations are audited.** They write a `crm.AuditLog` row and, where
   relevant, fan out a `crm.Notification` by role.
3. **Sandbox never fails loudly.** With no provider configured, sends store
   locally with `status="sandbox"` and integration actions return
   `{ok: false, skipped: true, detail: …}`. An unconfigured install answers
   every read endpoint with a 200.

## The one live channel that is not HTTP request/response

`GET /api/whatsapp/stream` is a **Server-Sent Events** stream covering *all*
chat channels (it lives in the whatsapp app for historical reasons). The
dashboard's `/chats` page subscribes to it and re-fetches only on a `change`
event; it does not poll the thread endpoints. The change token is derived from
`Thread.updated_at` + message count, so **any store write path using a queryset
`.update()` must also bump the thread's `updated_at`** or the dashboard will not
refresh.
