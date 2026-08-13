# C# / ASP.NET Core port — parity & cutover

Companion to `CSHARP_MIGRATION_PLAN.md`. Tracks what the C# backend
(`backend-csharp/`) covers versus the Django API, and how to verify + cut over.

Status as of this branch: **Phases 0–6 built and green** — 52 endpoints,
~4.5k lines of C#, **38 tests passing**. The port covers the core of every
subsystem; the long tail (per-provider live webhooks, media serving, voice-call
signaling, OAuth flows, the health checker) is deferred and listed below.

## Endpoint parity matrix

| Subsystem | Django routes | C# implemented | Notes |
|---|---:|---:|---|
| Auth (`/api/auth`) | ~11 | 3 | login/logout/whoami; user-management CRUD deferred |
| Settings | — | 2 | entity-scoped GET/PUT — the scoping rule is fully ported |
| Inbox (`/api/whatsapp`) | 41 | 10 | threads/messages/send/simulate/SSE/accounts/canned; **live Meta/Baileys webhooks, media, templates-from-Meta, voice-call signaling deferred** |
| Messaging (`/api/messaging`) | 31 | (shared) | canned + threads shared with inbox; email IMAP/SMTP + social webhooks deferred |
| aicore (`/api/aicore`) | 6 | 10 | persona/knowledge(+preview)/skills/reply — **full** for this surface |
| flows (`/api/flows`) | 5 | 6 | library/runs/queue/enqueue/tick + queue worker; **node interpreter (engine.py) deferred** |
| calls (`/api/calls`, contacts, analytics) | 25 | 15 | contacts/channels/analytics/script-templates + sidecar bridges; live dialling deferred |
| integrations (`/api/integrations`) | 13 | 5 | list/connect/action/webhooks(HMAC); **OAuth2/PKCE flow + real action execution deferred** |
| api v1 (`/api/v1`) + ingest | 14 | 5 | whoami/brands/contacts/sync-records; full user-tier management deferred |
| **Total** | **~159** | **52** | core of every subsystem; see "Deferred" |

## What's fully ported (behaviour parity, tested)

- **Entity scoping** (`ScopingService`) — the three verbs + the Setting
  global-row uniqueness rule, ported 1:1 from `crm/scoping.py`; parity tests
  mirror `test_entity_scoping.py`.
- **Auth** — Django PBKDF2 password interop, bearer `AuthToken` sessions, the
  `X-Role` pre-seed bootstrap, `superadmin`/`admin+` policies.
- **Inbox store** — get-or-create thread + entity sync, append with wamid
  dedup + `updated_at` bump, pagination, the SSE change token.
- **AI brain** — persona (HARD_RULES floor + override), knowledge retrieval
  (keyword/recency/priority, self-expiring window, 1800-char budget), skills
  sole-writer, channel-agnostic reply via a pluggable `ILlmClient`.
- **Outbound queue** — jittered pacing, daily cap, and the **atomic
  queued→sending claim** (tested with a real two-context race → exactly one
  send).
- **Webhook HMAC** — Shopify + Stripe verify, 503 with no secret.

## Deferred (the long tail — next passes)

These are known-not-yet-ported; each is isolated and additive:

- **Live channel webhooks**: Meta Cloud (signature, 24h-window 131047 handling),
  Baileys gateway intake, Telegram/Messenger/IG, email IMAP/SMTP.
- **Media**: upload/download + auth-gated serving (`/api/*/media/<pk>`).
- **Templates from Meta** (WABA listing), and the real send path (currently
  sandbox-stores; a real `IWhatsAppClient`/`IEmailClient` plugs in behind the
  store like `ILlmClient` does).
- **Voice**: WhatsApp call signaling (`wa_calls`), Baileys calls, SIP dial,
  recordings/transcripts — the `VoiceAgentBridge` file IPC contract is in place;
  the dial/signal endpoints are not.
- **Flow node interpreter** (`engine.py`): time-trigger matching, reply-parking,
  action-node execution. The queue + library are done; the interpreter is the
  biggest single remaining piece.
- **Channel health checker** + routing/failover, account roles, routing rules,
  business redirect, recovery.
- **Integrations OAuth2/PKCE** flow and real action execution (calendar, Drive,
  Sheets, Gmail, Stripe links).
- **RBAC admin**: full user/role/permission management, notifications, audit-log
  endpoints.
- **A real LLM client** (OpenRouter/OpenAI) behind `ILlmClient`.

## Verify against a real Django database

The context maps Django table + snake_case column names, so it can attach to an
existing `db.sqlite3`. Before enabling **writes** on real data:

1. **Read-only diff.** Point the connection string at a copy and run the read
   endpoints; compare payloads to Django's.
   ```bash
   ConnectionStrings__Default="Data Source=/path/to/copy-of-db.sqlite3" \
     dotnet run --project src/ChatBranch.Api --no-launch-profile
   ```
2. **Column check.** A few columns may differ from the snake_case guess (e.g.
   Django `USERNAME`, custom `db_column`). Compare `PRAGMA table_info(<table>)`
   between the Django DB and a C#-`EnsureCreated` DB; add explicit
   `.HasColumnName(...)` in `AppDbContext` for any mismatch.
3. **Constraints.** Confirm the partial unique indexes match (Django
   `sqlite_master` vs the C# ones).

## Migrations

Phase 0–6 use `EnsureCreated()` for a frictionless first run. For real
deployments (and to add columns to an existing DB without dropping it), switch
to EF Core migrations:

```bash
dotnet ef migrations add InitialCreate --project src/ChatBranch.Data --startup-project src/ChatBranch.Api
dotnet ef database update --project src/ChatBranch.Data --startup-project src/ChatBranch.Api
```

Then replace `Seed.Run`'s `EnsureCreated()` with `db.Database.Migrate()`.

## Sidecars (unchanged — still Python/Node)

| Sidecar | Contract in C# | Status |
|---|---|---|
| Voice agent (LiveKit, Python) | `VoiceAgentBridge` (file IPC) | contract done; dial/monitor endpoints deferred |
| Baileys gateway (Node) | `IBaileysGateway` (HTTP) + sandbox default | contract done; real client + call intake deferred |

The file paths + JSON shapes match `voice_agent/storage.py`, so the Python agent
needs no change when the C# backend drives it.

## Cutover (Phase 7)

1. Bring the deferred list to the coverage your deployment needs (live webhooks
   + media + a real send client are the minimum for production `/chats`).
2. Run C# and Django side-by-side on the same DB; diff responses per endpoint.
3. Flip the frontend's `NEXT_PUBLIC_API_URL` to the C# host. The Next.js app and
   both sidecars do not change.
