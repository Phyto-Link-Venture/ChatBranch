# AGENTS.md

Working rules for coding agents in this repo. **`CLAUDE.md` is the long form** —
read it before any non-trivial change (especially its **Port status** table).
This file is the short version.

## What this is

**ChatBranch** — an industry-agnostic multi-channel conversation platform, shipped
as a template: WhatsApp (Cloud + Baileys), a website widget, email, Telegram,
Messenger, Instagram and AI voice calls, behind one operator dashboard and one
AI brain. `README.md` is the user-facing entry point; `docs/vertical-guide.md`
explains how it gets configured for a business.

## Four processes (no Python anywhere)

| # | Process | Path | Port |
|---|---|---|---|
| 1 | C# API (ASP.NET Core 8, EF Core + SQLite) | `backend-csharp/` | 8090 |
| 2 | Voice agent worker (LiveKit, Node/TypeScript, database-free) | `backend/voice_agent/` | — |
| 3 | Next.js dashboard | `frontend/` | 3000 |
| 4 | Baileys WhatsApp gateway (Node, optional) | `backend/baileys_service/` | 3020 |

```bash
npm install && npm run setup        # once
SEED_DEMO=1 npm run dev:all         # all four; SEED_DEMO adds admin/agent demo logins
npm run dev:api | dev:agent | dev:web | dev:baileys
```

## Verify before you claim done

```bash
cd backend-csharp && dotnet build src/ChatBranch.Api -c Release
cd backend-csharp && dotnet test tests/ChatBranch.Tests     # 218, keep green
cd frontend && npx tsc --noEmit && npx eslint src
cd backend/voice_agent && npx tsc --noEmit && npm run selftest
node backend/baileys_service/calls.js --selftest
```

The test host sets `QUEUE_WORKER=0 FLOWS_WORKER=0 AI_FOLLOWUP_WORKER=0
AI_HUMANIZE=0 SEED_DEMO=1` and a throwaway data dir itself.

## The rules that actually bite

- **Every route needs an explicit gate.** There is no fallback authorization
  policy: add `RequireAuthorization(AuthPolicies.…)` (plus a capability check
  via `AdminEndpoints.HasCapabilityAsync` / `SettingsEndpoints.RequireCapability`
  where the matrix applies) or a deliberate `AllowAnonymous()`. A new capability
  goes in `AdminEndpoints.Catalog` AND `frontend/src/lib/auth.ts`.
- **Schema changes go in `Seed.UpgradeSchema` too.** There are no EF migrations;
  `EnsureCreated()` never alters an existing SQLite file.
- **One organization can hold unrelated companies, so the org level leaks.**
  A live install may carry several *different businesses* as top-level entities,
  not branches of one. Settings resolve nearest-level-wins, but knowledge items
  **add up down the tree**, so an organization-wide item reaches every company.
  Write a persona, a knowledge item or an AI behaviour value at the org level
  only when it is genuinely true for every business in the install (the
  plain-text/no-Markdown house rule is one of the few). Otherwise pass the
  entity. An org-level persona is inherited by every entity whose own persona is
  blank — which silently makes one client's AI introduce itself as another's.
  Prove containment before you call it done: `knowledge_test_retrieval` (or
  `POST /api/aicore/knowledge/preview`) against a DIFFERENT entity must return
  zero items.
- **AI behaviour resolves inbox → entity → organization → default.** The inbox
  level is `ChannelAccount.Config[key]`, read first by
  `AiBehaviourSettings.Resolve` and editable via `/api/ai/behaviour?account=<id>`
  (`InboxSettings.SetBehaviour`; a blank value clears the override). When one
  channel needs different pacing from another — a website widget answering in
  seconds while WhatsApp waits out a burst — set it on the inbox. Changing the
  organization default to fix one channel changes every channel.
- **A slow AI reply is not a broken one.** `ai_wait_seconds` defaults to **25**
  ("wait for the customer to finish"), plus an `ai_typing` pause proportional to
  the reply, so ~40 s end-to-end is the designed behaviour, and `ai_split=auto`
  can deliver a long answer as up to three messages. The website widget polls
  `/api/widget/<token>/messages` every 4 s **only while the panel is open**, so a
  visitor who closes it never sees the answer. Measure before "fixing" latency.
- **Write messages through `ThreadStore`** (`GetOrCreateThread`, `AppendMessage`):
  it dedupes on external id, stamps the account, syncs entity attribution and
  bumps `Thread.UpdatedAt`, which the `/chats` SSE change token watches. A bulk
  `ExecuteUpdate` that skips `UpdatedAt` silently freezes the dashboard.
- **Entity scoping has ONE home: `ScopingService`** — `SettingValue` (entity →
  global), `ScopeInherited` (libraries), `ScopeOwned` (records). A raw `Setting`
  read must filter `EntityId == null`. Frontend pages build `?entity=` only via
  `useEntityQuery()` and put it in the effect's dependency array.
- **Single writers:** `AccountHealth.SetStatus` for `ChannelAccount.HealthStatus`;
  `NotificationService` for notifications; `GET/PUT /api/aicore/skills` for
  `AgentSkill`; `TagService` for tag rules and the `Contact.Tags` cache;
  `AiGate.Submit` for every AI-written outbound.
- **`AssistantService` only generates** (`AiReplyResult`); `AiComposer` is the
  one seam the AI-ops layer calls. Nothing in the AI or inbound path may throw
  into a webhook.
- **Sends are queued, not inline** (`QueueService`, atomic claim). Calls are
  queued too, and a busy WhatsApp number is a wait, not a failure.
- **Sandbox must never raise.** With no credentials, sends store with
  `status="sandbox"` and integration actions return `{ok:false, skipped:true}`.
- **The voice agent never touches the database.** Its contract with the API is
  files under `CHATBRANCH_DATA_DIR` (`storage.ts` ↔ `VoiceAgentBridge.cs`) and
  the dispatch metadata (`metadata.ts` ↔ `VoiceDispatch.cs`) — change both sides.
- **Frontend:** never call `getRole()` / `getAuth()` during render (use
  `useAccess()` / `useRole()`); never a native `<select>` (use
  `components/ui/Select`); never hardcode palette colours (use the theme tokens
  or `tint()`); never put `var(--…)` in a lucide `color=`; a modal's errors
  render inside the modal; a new page = one entry in `components/shell/nav.ts`.

## Naming that looks wrong but is not

- Table names are Django's (`crm_*`, `ingest_*`, `auth_user`) — the C# port kept
  them. "Brands" in `/api/v1/brands` are entities.
- `Message.source` has the **value** `"customer"` — a speaker role, not a model.
- `/api/whatsapp/stream` and `/api/whatsapp/simulate` cover every channel.
- `/persona` is "Voice & calls" (voices, opening line, pronunciation), not the
  chat persona (which has no UI — `PUT /api/aicore/persona`).

## Maturity — do not oversell in code comments or docs

- Email, Telegram replies and Messenger/Instagram are **sandbox-only** in the C#
  API; WhatsApp Cloud sends templates as rendered text and doesn't send media;
  inbound media isn't stored; STOP opt-out detection isn't ported. See the Port
  status table in `CLAUDE.md`.
- Baileys is an **unofficial, ToS-violating** WhatsApp transport: dev/demo only.
- Shopify/Stripe webhook mappings and the Google action HTTP shapes are
  **fixture-tested only**.
- SQLite and demo passwords (`SEED_DEMO=1`) are the shipped defaults.

## Docs map

`README.md` · `CLAUDE.md` · `docs/vertical-guide.md` · `docs/DOMAIN_MODEL.md` ·
`docs/DATABASE_SCHEMA.md` · `docs/API_CONVENTIONS.md` · `docs/RBAC_AND_BRANDS.md`
· `docs/record-sync.md` · `docs/channel-adapter.md` ·
`backend/voice_agent/README.md` · `backend/baileys_service/README.md` +
`RESEARCH.md`. Docs headed "historical" describe how the code got here.
