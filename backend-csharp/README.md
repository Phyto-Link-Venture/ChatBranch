# ChatBranch — C# / ASP.NET Core backend

The in-progress C# rewrite of the Django API (`../backend/`). Same JSON
contract, so the existing Next.js dashboard (`../frontend/`) and the voice /
Baileys sidecars stay unchanged. See `../docs/CSHARP_MIGRATION_PLAN.md` for the
full plan and phasing.

## Status: Phases 0–6 complete (52 endpoints, 38 tests green)

Built so far: the foundation (below) plus the **unified inbox** (threads /
messages / send / simulate / SSE / canned), the **AI brain** (persona /
knowledge / skills / reply via a pluggable `ILlmClient`), the **outbound queue**
(pacing + atomic claim, as a `BackgroundService`), **contacts / channel hub /
analytics / script templates**, **sidecar bridges** (voice-agent file IPC +
Baileys HTTP contract), and **integrations** (sandbox + webhook HMAC) with the
**`/api/v1`** DRF-twin surface + bulk record sync. Full coverage matrix and the
deferred long tail: `../docs/CSHARP_PARITY.md`.

### Foundation (Phase 0 + 1)

**Phase 0 — Skeleton**
- Solution with four projects (`Domain`, `Data`, `Api`, `Tests`).
- EF Core + SQLite, DI, bearer-token auth, RBAC policies, CORS for the dashboard.
- Open liveness probe: `GET /api/whatsapp/status`.

**Phase 1 — Foundation (crm + scoping + auth)**
- **All 28 CRM entities** + `ingest` Tenant/Entity, defined in
  `src/ChatBranch.Domain/`.
- **`AppDbContext`** (`src/ChatBranch.Data/`) maps them to Django's table names
  (`crm_*`, `ingest_*`, `auth_user`) with **snake_case columns**, JSON-column
  converters, and the **partial unique indexes** that replicate Django's
  filtered constraints — most importantly the Setting rules (one global row per
  key; one override per `(key, entity)`).
- **`ScopingService`** — a faithful port of `crm/scoping.py`: the three verbs
  (`SettingValue`, `ScopeInherited`, `ScopeOwned`), the entity-scoped-settings
  allowlist, and thread attribution.
- **`DjangoPasswordHasher`** — PBKDF2-SHA256 interop so this backend can
  authenticate users created by the Django app against the **same** database.
- **Auth**: `TokenAuthenticationHandler` (Bearer → `AuthToken` row, with the
  `X-Role` pre-seed bootstrap), `superadmin` / `admin+` policies.
- **Endpoints**: `POST /api/auth/login`, `POST /api/auth/logout`,
  `GET /api/auth/whoami`, `GET/PUT /api/settings` (entity-scoped, superadmin
  write gate).
- **Seed**: demo tenant + entity + roles + users (superadmin/super123,
  admin/admin123, agent/agent123 — POC creds).

**Tests (19, all green):** scoping parity ported from
`backend/tests/test_entity_scoping.py`, password-hash round-trips, and
end-to-end HTTP smoke (login → whoami, the 403/400 gates) via
`WebApplicationFactory`.

## Prerequisites

.NET SDK 8.0+ (`dotnet --version`).

## Build · test · run

```bash
cd backend-csharp
dotnet build
dotnet test

# run against a local SQLite file (created + seeded on first boot)
dotnet run --project src/ChatBranch.Api --no-launch-profile --urls http://127.0.0.1:8000
```

Point it at the **existing Django database** instead of a fresh one by setting
the connection string (the schema already matches — table + column names are
Django's):

```bash
ConnectionStrings__Default="Data Source=../backend/db.sqlite3" \
  dotnet run --project src/ChatBranch.Api --no-launch-profile
```

> ⚠️ Column-level fidelity against a live Django DB still needs a verification
> pass (Phase 7 cutover): a few columns may need explicit mapping where the
> snake_case guess differs from Django's actual name. Do a read-only diff
> before enabling writes on a real database.

## Layout

```
src/ChatBranch.Domain   entities (28 + Tenant/Entity), enums, IEntityScoped
src/ChatBranch.Data     AppDbContext, ScopingService, DjangoPasswordHasher, Seed, JSON converters
src/ChatBranch.Api      Program, auth handler, RBAC policies, endpoints
tests/ChatBranch.Tests  xUnit — scoping parity, hasher, HTTP smoke
```

## Next

The deferred long tail (live per-provider webhooks, media serving, a real
send/LLM client, the flow node interpreter, voice dialling, integrations OAuth,
full RBAC admin) is tracked in `../docs/CSHARP_PARITY.md`. Each item is isolated
and additive — plug a real `IWhatsAppClient`/`ILlmClient` behind the store the
same way `SandboxLlmClient` slots in today.
