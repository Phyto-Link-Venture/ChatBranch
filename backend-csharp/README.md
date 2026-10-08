# ChatBranch — C# / ASP.NET Core backend

THE backend of ChatBranch (the Django API it replaced was removed on
2026-08-16): ASP.NET Core 8 minimal APIs over SQLite via EF Core. It serves the
Next.js dashboard (`../frontend/`), drives the Baileys gateway over HTTP and the
voice agent through LiveKit + shared files, and exposes an MCP server. The
repo-level guide is [`../CLAUDE.md`](../CLAUDE.md) — read its **Port status**
table before relying on a channel transport.

## Layout

```
src/ChatBranch.Domain   entities (Contact, Thread, Message, Tag, AiDraft, …), wire-value constants
src/ChatBranch.Data     AppDbContext + Seed (schema bootstrap/upgrade), ScopingService,
                        Stores/ThreadStore, Ai/ (assistant, gate, composer, responder,
                        follow-ups, skills sandbox), Flows/ (engine, queue, call placer,
                        automations), Channels/, Integrations/, Notifications/, Tags/,
                        Audit/, Mcp/ (client), Sidecars/ (LiveKit, voice-agent bridge, Baileys)
src/ChatBranch.Api      Program.cs, Endpoints/ (one file per area), Auth/ (bearer + API keys,
                        policies), Audit/ (request audit middleware), Tools/ (the MCP/assistant
                        tool registry), Assistant/ (Ask ChatBranch), Workers/ (queue, flows, AI ops)
tests/ChatBranch.Tests  xUnit — 218 tests, mostly end-to-end over WebApplicationFactory
```

## Build · test · run

Requires the .NET 8 SDK.

```bash
cd backend-csharp
dotnet build src/ChatBranch.Api -c Release
dotnet test tests/ChatBranch.Tests        # the test host sets its own env (see below)

# run (from the repo root `npm run dev:api` does the same)
dotnet run --project src/ChatBranch.Api --urls http://0.0.0.0:8090
```

- **`--urls` vs `ASPNETCORE_URLS`:** `Properties/launchSettings.json` (profile
  `http`, port 5223) overrides an `ASPNETCORE_URLS` environment variable; a
  `--urls` argument overrides the profile. Use `--no-launch-profile` when you
  configure the URL through the environment.
- **Database:** `ConnectionStrings:Default`, default `Data Source=chatbranch.sqlite3`
  relative to the content root — under `dotnet run` that is
  `src/ChatBranch.Api/chatbranch.sqlite3`; Docker uses `/data/chatbranch.sqlite3`.
  `Seed.Run` creates and seeds it on boot; `Seed.UpgradeSchema` adds new
  columns/tables to an existing file (there are no EF migrations — add every new
  column there too).
- **Seed:** the Demo tenant, the `main` entity, the built-in roles and a
  `superadmin` login (`SUPERADMIN_PASSWORD`, else `super123`). `SEED_DEMO=1`
  adds `admin/admin123`, `agent/agent123` and a sample contact.
- **Data dir:** `CHATBRANCH_DATA_DIR` (default `../backend/data` in a checkout) —
  shared with the voice agent.
- **Config:** Setting rows (edited in the dashboard) → process environment. The
  API does not load `.env` files, except that it reads the LiveKit keys from
  `../backend/.env` per call.
- **Workers** (`src/ChatBranch.Api/Workers/`): outbound queue every 5 s
  (`QUEUE_WORKER=0` disables), flow engine every 10 s (`FLOWS_WORKER=0`), AI ops
  every 30 s (`AI_FOLLOWUP_WORKER=0`). `AI_HUMANIZE=0` switches the human-like
  AI defaults off. The test host sets all four to `0`, plus `SEED_DEMO=1`, a
  throwaway database and data dir, and an unconfigured LiveKit client.

## Conventions

- Every route needs `RequireAuthorization(...)` or an explicit `AllowAnonymous()`
  — there is no fallback policy. Capability checks go through
  `AdminEndpoints.HasCapabilityAsync` / `SettingsEndpoints.RequireCapability`.
- Entity scoping lives in `ScopingService` only (`SettingValue`,
  `ScopeInherited`, `ScopeOwned`).
- Table and column names match the old Django schema (`crm_*`, snake_case) so
  the context could attach to the previous database; keep the naming when you
  add tables.
- Nothing on a webhook, send or AI path may throw; sandbox mode must answer
  every endpoint with zero credentials.

More: [`../docs/DATABASE_SCHEMA.md`](../docs/DATABASE_SCHEMA.md),
[`../docs/API_CONVENTIONS.md`](../docs/API_CONVENTIONS.md),
[`../docs/RBAC_AND_BRANDS.md`](../docs/RBAC_AND_BRANDS.md).
