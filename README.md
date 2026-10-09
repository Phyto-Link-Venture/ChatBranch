# ChatBranch

An industry-agnostic **multi-channel conversation platform** you can clone and
configure for your own business. One operator dashboard, one AI brain and one
contact record shared across WhatsApp, a website chat widget, email, Telegram,
Messenger, Instagram **and real phone calls**.

It is a template, not a product: the domain logic lives in editable
configuration (AI skills, a knowledge base, visual flows, tags, custom fields),
not in the code. Configuring it for e-commerce, logistics, services or clinics
is a settings exercise, not a fork — see
**[docs/vertical-guide.md](docs/vertical-guide.md)**.

> **It runs with zero API keys.** Every channel and every integration has a
> sandbox mode: messages are stored locally, inbound traffic can be simulated
> from the UI, and integration actions report a clean "skipped, not connected".
> Clone, `npm run setup`, `npm run dev:all` and click through the whole product
> before signing up for anything.

---

## Features

- **Unified inbox** (`/chats`) — every channel in one server-filtered list with
  a filter pop-up, per-user saved views, labels, teams, assignment, private
  notes, AI summaries and a live SSE stream (no polling).
- **One AI brain for every channel** — skills (prompt instructions, sandboxed
  JavaScript **tools** the model calls, and **reply checks** that vet every
  draft), a self-expiring knowledge base, per-contact memory, built-in date/time
  tools. Describe a skill in plain words and the wizard drafts and tests the code.
- **AI monitor** (`/ai-monitor`) — every AI message is recorded; choose auto,
  review risky replies, review everything, or shadow mode. Human-like delivery:
  waits for the customer to finish, skips "ok/thanks", types, splits long
  replies, and follows up when a conversation goes quiet.
- **Flow builder** (`/flow-builder`) — a step list for most people, a canvas for
  power users, starter templates, a real execution engine (triggers, waits,
  "follow up if no reply", branches, hand-over, AI messages, calls, app actions)
  and a Runs panel showing how many people reached each step.
- **Outbound pacing queue** — jittered per-account gaps, daily caps, quiet hours,
  atomic claims; calls are queued too.
- **AI voice calls** over SIP or WhatsApp (LiveKit), with a live transcript and
  guide / take-over / escalate / end.
- **Tags** with attributes and exclusive groups for contacts, entities and
  conversations; **custom fields**; both usable as template variables.
- **Entities** (a branch tree), teams, automations, bulk **bindings**, role-based
  access with a capability matrix, API keys, an **MCP server** (`/mcp`) for AI
  apps, and an in-app assistant ("Ask ChatBranch") that acts as you.
- **Integrations** — Google Calendar / Drive / Gmail / Sheets (OAuth, per user or
  shared), Shopify and Stripe (HMAC-verified webhooks).
- **Analytics** home page, reports with CSV export, LLM usage/billing, an
  activity log of who did what from where (dashboard, API, MCP, assistant, flow).
- **Paper & Ink** design system — three themes × light/dark, per-user accent,
  text size and corners; installable PWA with a phone tab bar.

Every channel has a live transport, tested against fakes of the providers'
documented APIs but not yet against the live services — read
[Honest limitations](#honest-limitations) before you plan a launch.

## Screenshots

The design system and key screens are on the design canvas:
**https://claude.ai/artifact/7dcX3rLGAH3zXZeKu61Pdb**

---

## The four processes

| # | Process | Path | Port | What it does |
|---|---|---|---|---|
| 1 | **C# API** | `backend-csharp/` | 8090 | ASP.NET Core 8 + EF Core over SQLite. The whole REST surface, the database, the channel transports, the AI, the flow engine, the outbound queue, the LiveKit control plane for calls, the MCP server. |
| 2 | **Voice agent worker** | `backend/voice_agent/` | — | A long-lived [LiveKit Agents](https://docs.livekit.io/agents/) process (Node/TypeScript). Runs every AI call the API dispatches — SIP or WhatsApp — with a realtime speech model or an STT→LLM→TTS cascade. Shares state with the API through files, not the database. |
| 3 | **Next.js dashboard** | `frontend/` | 3000 | The operator UI. |
| 4 | **Baileys gateway** | `backend/baileys_service/` | 3020 | Node sidecar for QR-paired personal WhatsApp numbers (text + calls). Optional; the C# API is its only client. |

```
                    ┌──────────────── Next.js dashboard :3000
                    │                        │ HTTP + SSE
                    ▼                        ▼
   providers ──▶  C# API :8090  ──▶ SQLite (chatbranch.sqlite3)
   (Meta, Telegram,  │   ▲   └──▶ LiveKit (dispatch + SIP)
    Shopify, Stripe…)│   │
                     │   │  files in CHATBRANCH_DATA_DIR (live transcripts,
                     ▼   │  operator inbox, calls.json — deliberate IPC)
              Voice agent worker ──SIP──▶ phone network
                     ▲
                     │ LiveKit room (audio)
              Baileys gateway :3020 ──▶ WhatsApp Web
```

The voice agent shares state with the API through **JSON files under
`CHATBRANCH_DATA_DIR`** (default `backend/data`), not the database — the agent
stays free of the API so it can be deployed, restarted and scaled on its own. If
you move to multi-host, that file layer is the seam to replace.

---

## Prerequisites

- **.NET 8 SDK** (the API) and **Node 20+** (dashboard, gateway, voice agent).
- **Only for real AI replies:** an OpenRouter, OpenAI, Google Gemini or ILMU key
  (or a local OpenAI-compatible model).
- **Only for real phone calls:** a [LiveKit](https://cloud.livekit.io) project
  with SIP, an OpenAI key with Realtime API access (`gpt-realtime`) or an
  ElevenLabs/ILMU cascade, and a SIP trunk (e.g. Twilio Elastic SIP Trunking) —
  see [backend/voice_agent/README.md](backend/voice_agent/README.md).

Nothing above is required to explore the product.

---

## Quick start

```bash
git clone <your-fork-url> chatbranch
cd chatbranch
npm install         # root dev tooling (concurrently)
npm run setup       # npm install for the frontend, the Baileys gateway and the
                    # voice agent (the API seeds itself on first boot)
SEED_DEMO=1 npm run dev:all   # all four processes with prefixed logs
                              # (PowerShell: $env:SEED_DEMO=1; npm run dev:all)
```

Then open **http://localhost:3000** and sign in.

A clean boot seeds only one login, `superadmin` (password from
`SUPERADMIN_PASSWORD`, else `super123`). **`SEED_DEMO=1`** also creates:

| Username | Password | Role |
|---|---|---|
| `superadmin` | `super123` | everything, every tenant, global keys and the permission matrix |
| `admin` | `admin123` | their tenant: channels, users, flows, settings except keys |
| `agent` | `agent123` | the inbox, contacts, analytics, AI monitor — scoped to assigned entities |

plus one sample contact. **These are POC credentials in a public template.
Rotate them before any deployment.**

**Turn on real AI replies:** sign in as superadmin → **Settings → AI keys &
models** and paste an OpenRouter (or OpenAI) key; pick the model there. Or
export `OPENROUTER_API_KEY` / `OPENAI_API_KEY` in the API's environment before
`npm run dev:api` (the API reads its process environment and the dashboard's
Setting rows — it does not read `backend/.env` for these).

Individual processes: `npm run dev:api`, `npm run dev:agent`, `npm run dev:web`,
`npm run dev:baileys` (`npm run dev` = `dev:all`). Ports 8090, 3000 and 3020 must
be free. Running the API by hand:

```bash
dotnet run --project backend-csharp/src/ChatBranch.Api --urls http://0.0.0.0:8090
```

`--urls` beats the launch profile; an `ASPNETCORE_URLS` env var does NOT (the
`http` profile in `launchSettings.json` overrides it) — use
`dotnet run --no-launch-profile` if you configure the URL through the
environment. The dev database is `backend-csharp/src/ChatBranch.Api/chatbranch.sqlite3`.

### Docker

`docker compose up` builds the API and the dashboard in sandbox mode. Add
`--profile whatsapp` for the Baileys gateway and `--profile voice` for the voice
agent (LiveKit keys in `backend/.env`). SQLite and the shared data dir live in
the `chatbranch-data` volume — don't drop it. `docker-compose.override.yml` +
`deploy/nginx-chatbranch.conf` show one single-origin production setup.

---

## What works with zero API keys

- The whole dashboard: inbox, contacts, flow builder, AI monitor, analytics,
  tags, settings, RBAC, the assistant (in a deterministic fallback mode).
- **Chats.** Sends on any channel are stored as real messages with
  `status="sandbox"`. Fake an inbound message with **Simulate**
  (`POST /api/whatsapp/simulate`) — it runs the same inbound path as a webhook:
  contact linking, flows, team routing, automations, notifications, and (with
  `?ai=1`) the AI reply.
- **WhatsApp templates.** Three demo templates plus your own are rendered
  locally (a Cloud number with a WABA id uses Meta's registry and sends real
  template messages).
- **Flows** execute for real against your contacts; sends land in the outbound
  queue and are stored as sandbox messages.
- **AI skills** — the describe-a-skill wizard uses a built-in template library
  and code skills run in the sandbox; replies come from a sandbox echo until a
  key is set.
- **Integrations** report `sandbox: true`; every action returns
  `{ok: false, skipped: true, detail: …}` and the flow run continues.

---

## Connecting real channels

All accounts are added from **Channels** (`/channels`) in the dashboard — pick a
channel, follow the setup drawer (connect → who answers it → test). Nothing
requires a restart.

| Channel | What you need | Status in the C# API |
|---|---|---|
| **WhatsApp (Cloud API)** | A Meta app, phone number ID, access token, app secret, WABA id, and a public webhook (`/api/whatsapp/webhook`) | live: inbound (incl. media), delivery statuses, text, real template messages, media send, the WABA's template registry |
| **WhatsApp (Baileys)** | A spare phone. Add a "QR" WhatsApp inbox and scan. ⚠ Read the ToS warning below | inbound text + media, text + media send, calls + recordings live |
| **Website widget** | Nothing — paste the snippet the inbox gives you | live |
| **Telegram** | A BotFather token, then "Register webhook" in the inbox (needs a public https URL — `PUBLIC_BASE_URL` or the one you type) | live: inbound text + media, replies and attachments via the Bot API, typing indicator |
| **Email** | Per-mailbox SMTP credentials (+ an IMAP host, per mailbox or the default under Channels → Advanced) | live: SMTP send (text + escaped HTML alternative, threaded replies, mailboxes routed and failed over like WhatsApp numbers), IMAP pull from known senders (HTML-only mail flattened to text) |
| **Messenger / Instagram** | A page token, the page / IG account id, the Meta app secret, and a public webhook (`/api/messaging/webhook`) | live: signed inbound (incl. attachments), Send API text + files (URL or upload) inside Meta's 24h / 7-day human-agent window, typing indicator, receipts |
| **Voice (SIP)** | LiveKit + a SIP trunk: `npm --prefix backend/voice_agent run setup-trunk`, then Channels → Voice | live |

Inbound webhooks need a public HTTPS URL; for local development tunnel port 8090
(`ngrok http 8090` or `cloudflared tunnel --url http://localhost:8090`).

## Connecting integrations

**Integrations** (`/integrations`):

- **Google** (Calendar, Drive, Gmail, Sheets) — OAuth2 with PKCE. Register one
  Google Cloud OAuth client, enter its id/secret in **Settings → App & provider
  keys** (or `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`), set
  `GOOGLE_REDIRECT_URI` + `FRONTEND_URL` in production, then **Connect** per
  product. A connection can be yours, an entity's, or organization-wide.
- **Shopify / Stripe** — API-key providers with a **webhook signing secret**.
  Both webhooks are HMAC-verified; a bad signature is rejected with 401 and a
  webhook whose secret was never configured answers **503** — never "accept
  unsigned". Shopify `orders/*` and Stripe `payment_intent.succeeded` become
  `Record` + `RecordEvent` rows.

Flow "App action" steps: `Create calendar event` · `Find free slot` ·
`Upload file to Drive` · `Append row to Sheet` · `Send via Gmail` ·
`Update order status` · `Create Stripe payment link`.

---

## Repo layout

```
ChatBranch/
├── backend-csharp/              THE BACKEND (ASP.NET Core 8, EF Core + SQLite)
│   ├── src/ChatBranch.Api/      Program, endpoints (one file per area), auth +
│   │                            API keys, request audit, MCP tools, the operator
│   │                            assistant, background workers
│   ├── src/ChatBranch.Data/     AppDbContext + Seed, scoping, stores, AI (gate,
│   │                            composer, skills sandbox), flows + queue, channels,
│   │                            integrations, notifications, tags, sidecar clients
│   ├── src/ChatBranch.Domain/   entities
│   └── tests/ChatBranch.Tests/  218 xUnit tests (API-level + unit)
├── backend/
│   ├── baileys_service/         Node gateway for QR-paired WhatsApp numbers (:3020)
│   ├── voice_agent/             LiveKit worker (Node/TypeScript) — its own process
│   ├── .env.example             sidecar + LiveKit config (copy to backend/.env)
│   └── data/                    LIVE state: WhatsApp session, media, call files
│                                (gitignored — never delete)
├── frontend/                    Next.js dashboard (App Router, :3000)
├── deploy/                      example nginx config
└── docs/                        domain model, schema, API, RBAC, channels, guide
```

---

## Verify a change

```bash
cd backend-csharp && dotnet build src/ChatBranch.Api -c Release
cd backend-csharp && dotnet test tests/ChatBranch.Tests    # 329 tests
cd frontend && npx tsc --noEmit && npx eslint src && npx next build
cd backend/voice_agent && npx tsc --noEmit && npm run selftest
node backend/baileys_service/calls.js --selftest
```

The test host disables the background workers and the human-like AI defaults
itself (`QUEUE_WORKER=0`, `FLOWS_WORKER=0`, `AI_FOLLOWUP_WORKER=0`,
`AI_HUMANIZE=0`) and seeds the demo logins (`SEED_DEMO=1`). CI
(`.github/workflows/ci.yml`) runs all of the above on every push to master and
every pull request.

---

## Honest limitations

- **Every channel transport is wired, with a few edges left.**
  Baileys numbers send templates as rendered text; WhatsApp Cloud has no voice
  calls (Meta's calling API is a gated beta); email is composed as plain text (an escaped HTML alternative
  is rendered from it); the opt-out flag lives on the conversation, not the
  contact. Nothing has been exercised against the live providers from this
  repo's CI — expect field debugging on a first real account. The full table is
  in [CLAUDE.md](CLAUDE.md#port-status--what-is-real-what-is-sandbox).
- **WhatsApp via Baileys violates WhatsApp's Terms of Service.** It speaks the
  unofficial WhatsApp Web protocol; automated messaging — and *especially*
  automated calling — gets numbers banned. It is here for development and demos.
  Use a dedicated spare number, keep volumes low, don't ship it to production.
  Research and ban statistics:
  [`backend/baileys_service/RESEARCH.md`](backend/baileys_service/RESEARCH.md).
- **WhatsApp Cloud voice calling is not available** — the old WebRTC bridge
  went with the Django backend; WhatsApp calls run over Baileys only.
- **Integrations are fixture-tested, never provider-tested.** Treat your first
  real Shopify/Stripe/Google connection as an integration test.
- **SQLite only.** Fine for development and a single-host pilot; not a
  production database for heavy concurrent writes. There are no EF migrations —
  schema upgrades are additive statements in `Seed.UpgradeSchema`.
- **Tenant isolation is application-layer only.** No row-level security; a
  missing filter is a data leak.
- **Demo credentials ship in the repo.** Before any deployment: set
  `SUPERADMIN_PASSWORD`, don't set `SEED_DEMO`, set the CORS origins
  (`backend-csharp/src/ChatBranch.Api/appsettings.json` or `Cors__Origins__0`),
  and change `BAILEYS_SECRET`.
- **SMS has no provider.** Flow SMS steps log and skip.

---

## Where to go next

| You want to… | Read |
|---|---|
| Configure this for your industry | **[docs/vertical-guide.md](docs/vertical-guide.md)** |
| Understand the data model | [docs/DOMAIN_MODEL.md](docs/DOMAIN_MODEL.md) · [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md) |
| Call the API (or connect an AI app over MCP) | [docs/API_CONVENTIONS.md](docs/API_CONVENTIONS.md) |
| Feed records from your own system | [docs/record-sync.md](docs/record-sync.md) |
| Understand roles, capabilities and entities | [docs/RBAC_AND_BRANDS.md](docs/RBAC_AND_BRANDS.md) |
| Add or debug a channel | [docs/channel-adapter.md](docs/channel-adapter.md) |
| Run real calls | [backend/voice_agent/README.md](backend/voice_agent/README.md) |
| Understand the Baileys transport and its risk | [backend/baileys_service/README.md](backend/baileys_service/README.md) · [RESEARCH.md](backend/baileys_service/RESEARCH.md) |
| Change the code (or point a coding agent at it) | [CLAUDE.md](CLAUDE.md) · [AGENTS.md](AGENTS.md) |

## Notes

- **Dev slowness is Next.js**, not your machine: each route compiles on first
  visit. Use `next dev --turbo` or a production build for a faster loop.
- The dashboard's API base is `NEXT_PUBLIC_API_URL` (default
  `http://localhost:8090/api`), baked in at build time.
- `NEXT_PUBLIC_MAX_CONCURRENT` (default `3`) caps simultaneous live calls —
  match it to your SIP trunk's concurrent-channel limit.
- The WhatsApp module's API surface was modelled on
  [Whatomate](https://github.com/shridarpatil/whatomate) (Go, AGPL) and the
  inbox/teams/automations on Chatwoot. Protocol shapes were reimplemented, not
  copied.
