# ChatBranch → C# / ASP.NET Core Migration Plan

> **Historical.** The plan for porting the Django API to C# (August 2026). The port shipped and Django was removed; for the current state and the remaining gaps see the Port status table in [`CLAUDE.md`](../CLAUDE.md).

Status: **planning** ·
Author: Claude Code · Date: 2026-08-13

## Decisions locked in

| Decision | Choice |
|---|---|
| Scope | **Backend only** — rewrite the Django API in ASP.NET Core; keep the Next.js dashboard unchanged |
| Voice agent + Baileys | **Keep as sidecar services** — the C# backend orchestrates them over HTTP/files, exactly as Django does today |
| Database | **SQLite via EF Core** — preserves the "run one binary + a file" local-run story |
| Source hiding | **Nice to have** — ship normal compiled DLLs; no Native-AOT constraints on library/MVC choices |

> Reality check on "hide my code": plain compiled .NET IL decompiles trivially
> (ILSpy). Since hiding is only *nice to have*, we do NOT constrain the design
> for Native AOT. If that ever becomes critical, revisit — AOT rules out some
> reflection-heavy libraries and full MVC.

## Why this is feasible

The app is already **four cooperating processes**, and the two that can't be
rewritten in C# (voice agent, Baileys) are *already* isolated behind
file/HTTP boundaries. We only replace **one** process — the Django API — and
leave the frontend and both sidecars in place.

```
                 BEFORE                                   AFTER
  ┌──────────────┐   ┌──────────────┐        ┌──────────────┐   ┌──────────────┐
  │ Next.js :3000│──▶│ Django  :8000│        │ Next.js :3000│──▶│ ASP.NET :8000│   (unchanged frontend)
  └──────────────┘   └──────┬───────┘        └──────────────┘   └──────┬───────┘
                            │                                          │  same JSON contracts
             file IPC  ┌────┴────┐  HTTP                  file IPC ┌────┴────┐ HTTP
                       ▼         ▼                                 ▼         ▼
              voice_agent   baileys :3020            voice_agent (py)   baileys :3020 (node)
              (python/LK)   (node)                   — UNCHANGED —      — UNCHANGED —
```

The contract the frontend depends on is the `/api/**` JSON surface (~159
routes). Hold that contract byte-for-byte and the Next.js app never knows the
backend changed.

## Scope of work (grounded in the real tree)

| Django app | Routes | Python LOC | Port difficulty | Notes |
|---|---:|---:|---|---|
| `whatsapp` | 41 | 7,061 | High | Cloud API client, webhook, stores, wa_calls (calls delegate to sidecars) |
| `messaging` | 31 | 2,814 | Medium | Email IMAP/SMTP, Telegram/Messenger/IG send + webhook |
| `calls` | 25 | 3,415 | Medium | Contacts, analytics, channel hub, script templates; `lk.py` → sidecar bridge |
| `integrations` | 13 | 3,293 | High | Google OAuth/PKCE, Shopify/Stripe HMAC webhooks, flow actions |
| `crm` | 11 | 5,465 | Medium | 28 models, auth, RBAC, scoping — the **foundation**, do first |
| `core` | 13 | 891 | Low | settings, root routing, settings_api |
| `api` (v1 DRF) | 11 | 863 | Low | whoami/brands/users/contacts + record sync |
| `aicore` | 6 | 3,020 | Medium | assistant, memory, persona, knowledge, usage metering |
| `flows` | 5 | 2,383 | High | Visual-flow execution engine + outbound queue |
| `ingest` | 3 | 1,048 | Low | Tenant/Brand + bulk record sync |
| **Sidecars** (keep) | — | 2,709 py + 1,549 js | **none** | voice_agent, baileys_service unchanged |

Total to rewrite: **~30k lines of non-test Python** across ~159 endpoints and
**28 data models**. Test suite (~15k lines, 863 tests) becomes the acceptance
spec — port the assertions to xUnit as you go.

## Target solution structure

A single `.sln`, layered so the port maps app-by-app:

```
ChatBranch.sln
├─ src/
│  ├─ ChatBranch.Api            # ASP.NET Core host: minimal APIs / controllers, DI, middleware
│  ├─ ChatBranch.Domain         # entities (28), enums, domain services (scoping, routing, health)
│  ├─ ChatBranch.Data           # EF Core DbContext, migrations, SQLite provider, "store" repos
│  ├─ ChatBranch.Channels       # whatsapp/messaging clients + webhook handlers per channel
│  ├─ ChatBranch.Ai             # aicore: assistant, memory, persona, knowledge, usage, LLM clients
│  ├─ ChatBranch.Flows          # flow engine + outbound queue
│  ├─ ChatBranch.Integrations   # Google/Shopify/Stripe + OAuth
│  └─ ChatBranch.Workers        # BackgroundService hosts (tickers, queue drain, health checker)
└─ tests/
   └─ ChatBranch.Tests          # xUnit — port from backend/tests + calls/tests
```

## Technology mapping

| Django / Python | ASP.NET Core / C# |
|---|---|
| Django ORM models | **EF Core** entities + `DbContext` |
| `manage.py migrate` | `dotnet ef migrations` / `dotnet ef database update` |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` |
| DRF views / `@api_view` | **Minimal APIs** (or controllers) returning `Results.Json` |
| DRF serializers | DTOs + `System.Text.Json` (keep snake_case ↔ camelCase mapping the frontend expects) |
| Bearer `AuthToken` table | Custom `AuthenticationHandler<>` reading the same token table |
| RBAC role checks | Policy-based authorization (`[Authorize(Policy="admin+")]`) |
| `apps.py` `ready()` tickers | `BackgroundService` / `IHostedService` |
| `threading.Thread(daemon=True)` for AI replies | `Task.Run` / `Channel<T>` background queue, or Hangfire if you want durability |
| `requests` / `httpx` | `HttpClient` via `IHttpClientFactory` (typed clients per channel) |
| `openai` SDK | official `OpenAI` .NET SDK (or raw `HttpClient` to OpenRouter — you already support both) |
| `.env` + `python-dotenv` | `appsettings.json` + env + user-secrets; keep `.env` compatibility shim if desired |
| Django admin | Skip, or a small admin controller set (low priority) |
| file IPC (`data/live`, `data/inbox`, `data/whatsapp/calls`) | **identical file paths + JSON shapes** — this is the sidecar contract, do not change it |

## Data layer — 28 entities

The full model set (from `crm/models.py`):

`Profile, AuthToken, Role, RolePermission, AuditLog, Notification, Contact,
ContactEntity, Resource, ResourceLink, Record, RecordEvent, ChannelAccount,
Setting, Thread, Message, CannedResponse, ScriptTemplate, Workflow,
AgentSkill, KnowledgeItem, FlowRun, CallRecord, AccountAssignment,
OutboundJob, LlmUsage, PendingToolAction, ConversationSummary`

Porting rules that MUST survive:
- **`Setting` is not key-unique** — it carries a nullable `entity` FK with a
  partial unique constraint per scope. In EF Core: composite index
  `(key, entity)` unique, and every global read filters `entity == null`.
  (Django enforces this with a partial unique constraint; SQLite supports
  filtered indexes, so replicate it.)
- **Entity scoping** (`crm/scoping.py`) is the one place the "entity override
  wins, global is fallback" rule lives → port verbatim as
  `Domain/Scoping.cs` with the same three verbs (`SettingValue`,
  `ScopeInherited`, `ScopeOwned`). Getting this wrong leaks one outlet's data
  into another.
- **`Thread.entity` denormalization** (`sync_thread_entity`) must be called at
  every seam that moves a thread (create, account switch, contact link/unlink,
  re-bind). Keep it a single domain method invoked from all write paths.
- **JSONField** columns (`config`, `ai_overrides`, `context`, credentials) →
  EF Core `.HasConversion` to/from JSON, or `jsonb`-style text columns on
  SQLite with `System.Text.Json`.
- **wamid dedup** partial unique constraint (crm migration 0003) → filtered
  unique index.
- Seed data (`seed_demo`) → an EF Core seeding routine or a
  `dotnet run -- seed` command (superadmin/super123 etc.).

Migration data path: point EF Core at the **existing SQLite file** — the
schema is already there, so mostly you're mapping entities onto existing
tables rather than creating new ones. Verify with a read-only pass before
enabling writes.

## Auth & RBAC

- `/api/auth/login` issues a bearer token per session, stored in the
  `AuthToken` table (multiple concurrent tokens per profile). Reproduce
  exactly: custom `AuthenticationHandler` that hashes/looks up the presented
  bearer, resolves `Profile`, and sets claims (`role`, `tenant`, `brands`).
- Roles: `superadmin / admin / agent`. Map to authorization policies:
  - global settings & credentials → `superadmin`
  - account management, audit log, user creation → `admin+`
  - everything else → authenticated
- Keep the legacy quirks the frontend relies on: the `X-Role` bootstrap
  header works **only** while zero Profile rows exist; `'collector'` legacy
  session normalizes to `agent`.
- `user_service.py` holds the tiered creation rules (superadmin manages all;
  admin manages only agents in their tenant) → one `UserService` class,
  single source of truth, called by both `/api/v1/users` and
  `/api/auth/users`.

## Background workers

Five Django apps start tickers in `ready()`. Each becomes a hosted service:

| Today | C# replacement | Cadence |
|---|---|---|
| `flows` engine ticker | `FlowEngineWorker : BackgroundService` | time triggers per tick |
| `flows` queue drain | `OutboundQueueWorker` | paced (min/max gap, caps) |
| `whatsapp` health checker | `ChannelHealthWorker` | `WHATSAPP_HEALTH_INTERVAL` (300s) |
| `aicore` summary ticker | `ConversationSummaryWorker` | `AICORE_SUMMARY_INTERVAL` (600s) |
| `calls` (poll/live helpers) | on-demand, no persistent ticker | — |
| daemon-thread AI replies | in-process `Channel<T>` consumer | fire-and-forget off webhook ack |

Keep the concurrency guards the Python code fought for: the queue's
**atomic job claim** (`queued → sending` UPDATE + stale-claim reaper) →
EF Core optimistic concurrency or `UPDATE ... WHERE status='queued'` with
rowcount check. The double-send bug (two tickers) is real; preserve the lock.

## Sidecar contract — DO NOT change these

The whole reason voice + Baileys survive untouched is that the interface is
files and small HTTP calls. Reimplement the *caller* side in C#, keep the
*wire format* identical:

**Voice agent (file IPC, `storage.py` shapes):**
- write dispatch metadata / read `data/live/{room}.json` (live transcript)
- write `data/inbox/{room}.json` (operator instructions, consume-once)
- read `data/calls.json` (final records)
- WhatsApp call signals: `data/whatsapp/calls/{call_id}.json`

C# side = plain `System.IO` + `System.Text.Json` on the **same paths and
same JSON keys**. No LiveKit SDK needed in C# for the poll paths (that's why
`wa_calls.live_transcript` imports `storage` directly today).

To actually *dispatch* a LiveKit room/token from C#, use the LiveKit
**server** SDK for .NET (room + token creation is supported) — only the
*Agents realtime framework* is Python-only, and that stays in the sidecar.

**Baileys gateway (HTTP, X-Baileys-Secret):**
- inbound → `POST /api/whatsapp/baileys/inbound`, media → `.../media`,
  status → `.../status-event`, calls → `.../call-event`
- outbound → C# calls the gateway's `call/start`, send endpoints

These are ordinary authenticated HTTP handlers/clients in C#. The Node
gateway does not change.

## Shared-inbox parity backlog (optional, post-migration)

The comparison found you're ahead on AI/voice/flows and behind on the classic
helpdesk surface. If you want to close the gap, these are the adds — none
block the migration, and doing them **in C# after** the port is cleaner than
porting-then-adding:

1. **Website live-chat widget** (embeddable JS SDK + a websocket/SSE channel)
   — the classic shared-inbox headline feature; you have no web-widget channel.
2. **Help Center** (public customer-facing KB portal).
3. **CSAT surveys** (post-conversation rating → a report).
4. **Teams + team assignment / round-robin** (you have account routing, not
   agent teams).
5. **SLA policies, macros, saved views/folders, participants & @mentions.**
6. **Channels you lack:** SMS/Twilio, LINE, generic API channel.

## Phased roadmap

Each phase ends green (xUnit port of the relevant Django tests) and the
Next.js app runs against it unchanged.

- **Phase 0 — Skeleton (1 wk).** Solution, DI, SQLite `DbContext` pointed at
  the existing DB (read-only smoke test), auth handler, CORS, one live
  endpoint (`/api/whatsapp/status`) to prove the frontend connects.
- **Phase 1 — Foundation: `crm` + `core` + scoping + RBAC.** 28 entities,
  auth/login, settings, scoping verbs, seed command. Nothing else works until
  this is solid.
- **Phase 2 — Inbox core: `messaging` + `whatsapp` stores/threads/messages**
  (text send/receive, webhooks, SSE `/stream`, 24h-window, sandbox mode).
  Frontend `/chats` should light up here.
- **Phase 3 — `aicore`** (assistant, memory, persona, knowledge, skills,
  usage metering) + AI auto-reply wiring.
- **Phase 4 — `flows` engine + outbound queue** (tickers, atomic claim,
  pacing, queued AI replies, template/text/call jobs).
- **Phase 5 — `calls`** (contacts, analytics, channel hub, script templates)
  + sidecar dispatch bridges (voice_agent file IPC, Baileys HTTP, LiveKit
  server-SDK token mint).
- **Phase 6 — `integrations`** (Google OAuth/PKCE, Shopify/Stripe HMAC
  webhooks, flow action nodes) + `api`/`ingest` v1 surfaces.
- **Phase 7 — Cutover.** Run C# and Django side-by-side on the same DB,
  diff responses per endpoint, flip the frontend's `NEXT_PUBLIC_API_URL`.

## Risks & gotchas

- **Behavioral parity is the hard part, not syntax.** The value is in the edge
  cases the Python code already handles: 131047 window-close, webhook wamid
  dedup, entity-scope leakage, queue double-send, failover routing, per-account
  AI ticks. The 863 tests encode these — port them first as the spec, then make
  them pass. Treat any test you *can't* port as a parity risk to flag.
- **Snake_case ↔ camelCase.** The frontend expects specific casings
  (`to_contact`, camelCase `Contact`). Configure `System.Text.Json` policies
  per-DTO; don't assume a global policy fits every endpoint.
- **SQLite concurrency.** Django serialized writes; EF Core + SQLite needs WAL
  mode and care around the queue workers. Enable WAL, keep write scopes short.
- **LiveKit .NET** covers server/token, **not** realtime agents — confirmed
  constraint, already handled by keeping the Python agent.
- **`Setting` global reads** — the single most common latent bug: forgetting
  `entity == null` on a global read. Centralize every settings read through the
  scoping service so it can't be bypassed.
- **Lazy imports** (`lk`, `openai`, `httpx`) exist to keep cheap endpoints
  fast. In C#, use `IHttpClientFactory` typed clients + lazy `Lazy<T>` service
  resolution so the LiveKit/OpenAI dependencies don't load on `/contacts`.

## Next step

Say the word and I'll start **Phase 0 + Phase 1** on this branch: scaffold the
solution, define the 28 EF Core entities against the existing SQLite schema,
port `crm/scoping.py` and the auth handler, and stand up the login +
settings endpoints with an xUnit port of `tests/test_entity_scoping.py` as the
first green gate.
