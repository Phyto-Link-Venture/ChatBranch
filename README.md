# ChatBranch

An industry-agnostic **multi-channel conversation platform** you can clone and
configure for your own business. One operator dashboard, one AI brain, and one
contact record shared across WhatsApp, email, Telegram, Messenger, Instagram
**and real phone calls**.

It is a template, not a product: the domain logic lives in editable
configuration (persona, skills, knowledge base, visual flows, contact
attributes), not in the code. Configuring it for e-commerce, logistics,
services or clinics is a settings exercise, not a fork —
see **[docs/vertical-guide.md](docs/vertical-guide.md)**.

> **It runs with zero API keys.** Every channel and every integration has a
> sandbox mode: messages are stored locally, inbound traffic can be simulated
> from the UI, and integration actions report a clean "skipped, not connected".
> You can clone, `npm run setup`, `npm run dev:all` and click through the whole
> product before signing up for anything. See
> [What works with zero keys](#what-works-with-zero-api-keys).

---

## What's in the box

| Capability | Status |
|---|---|
| Unified inbox (`/chats`) across WhatsApp / email / Telegram / Messenger / Instagram | working |
| AI auto-reply on every text channel, from one shared brain | working |
| Operator-authored **persona**, **skills** and **knowledge base** (no redeploy) | working |
| Per-contact **AI memory** — conversation summaries + durable facts, shared by chat and voice | working |
| Visual **flow builder** with a real execution engine (triggers, waits, decisions, actions) | working |
| Outbound **pacing queue** — jitter, daily caps, active-conversation caps, quiet hours | working |
| **AI voice calls** over SIP, with live transcript, guide / take-over / escalate | working |
| **AI voice calls over WhatsApp** (two transports — see the honesty notes) | working, with caveats |
| Multi-account channels, health monitoring, sticky routing + failover | working |
| Third-party integrations: Google Calendar / Drive / Gmail / Sheets, Shopify, Stripe | working, fixture-tested only |
| RBAC (superadmin / admin / agent), tenants, brands, audit log, notifications | working |

---

## The four processes

ChatBranch is four cooperating processes. They are separate on purpose.

| # | Process | Path | Port | What it does |
|---|---|---|---|---|
| 1 | **Django API** | `backend/` | 8000 | The whole REST surface, the database, the channel transports, the flow engine, the outbound queue. |
| 2 | **Voice agent worker** | `backend/voice_agent/` | — | A long-lived [LiveKit Agents](https://docs.livekit.io/agents/) process. Dials over SIP, runs a realtime speech model, streams a live transcript. **Django-free** — it never imports the ORM. |
| 3 | **Next.js dashboard** | `frontend/` | 3000 | The operator UI. |
| 4 | **Baileys gateway** | `backend/baileys_service/` | 3020 | Node sidecar for QR-paired personal WhatsApp numbers. Optional; Django is its only client. |

How they talk to each other:

```
                    ┌──────────────── Next.js dashboard :3000
                    │                        │ HTTP + SSE
                    ▼                        ▼
   providers ──▶ Django API :8000 ──▶ SQLite (crm)
   (Meta, SMTP,      │   ▲
    Telegram…)       │   │  files in backend/data/  (live transcripts,
                     │   │  operator inbox, call records — deliberate IPC)
                     ▼   │
              Voice agent worker ──SIP──▶ phone network
                     ▲
                     │ LiveKit room (audio)
              Baileys gateway :3020 ──▶ WhatsApp Web
```

The voice agent shares state with Django through **JSON files under
`backend/data/`**, not the database. That is a deliberate choice: the agent
process stays free of Django so it can be deployed, restarted and scaled on
its own. If you move to multi-host, that file layer is the seam to replace.

---

## Prerequisites

**To run the dashboard and the messaging side (no keys needed):**

- **Python 3.11+** (3.13 is what `npm run setup` provisions)
- **Node 18+**

**Only if you want real AI replies:** an `OPENAI_API_KEY` or
`OPENROUTER_API_KEY`.

**Only if you want real phone calls:**

- A [LiveKit](https://cloud.livekit.io) project (URL + API key/secret) with SIP enabled
- An OpenAI key with Realtime API access (model `gpt-realtime`)
- A SIP trunk for outbound PSTN (e.g. Twilio Elastic SIP Trunking) — one-time
  setup via `backend/voice_agent/setup_trunk.py`

Nothing above is required to explore the product.

---

## Quick start

```bash
git clone <your-fork-url> chatbranch
cd chatbranch
npm install         # root dev tooling (concurrently)
npm run setup       # backend venv + pip install + migrate + seed_demo,
                    # frontend npm install, baileys npm install, .env from example
npm run dev:all     # starts all four processes with prefixed logs
```

Then open **http://localhost:3000** and sign in with one of the demo accounts
created by `seed_demo`:

| Username | Password | Role |
|---|---|---|
| `superadmin` | `super123` | everything, all tenants, global keys |
| `admin` | `admin123` | their tenant: accounts, users, flows |
| `agent` | `agent123` | only the brands assigned to them |

**These are POC credentials in a public template. Change them before any
deployment.** The Django admin at http://localhost:8000/admin uses the same
`superadmin` login.

### Cross-platform: Windows, macOS, Linux

`npm run setup` and `npm run dev:*` work the same on Windows, macOS and
Linux — `scripts/setup-api.js` and `scripts/run-py.js` resolve the right
Python launcher (`py -3.13` on Windows, `python3.13`/`python3`/`python`
elsewhere) and the right venv interpreter path (`venv\Scripts\python.exe` vs
`venv/bin/python`) for whichever OS runs them, instead of hardcoding one.

Ports 8000, 3000 and 3020 must be free.

Individual processes: `npm run dev:api`, `npm run dev:agent`, `npm run
dev:web`, `npm run dev:baileys`. `npm run dev` is an alias for
`npm run dev:all`. Ctrl+C stops all of them.

If you'd rather run the four processes by hand instead of through npm:

```bash
# 1. API  (first time: create the venv, install, migrate, seed)
cd backend
python3 -m venv venv && source venv/bin/activate   # Windows: venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env                                # Windows: copy .env.example .env
python manage.py migrate && python manage.py seed_demo
python manage.py runserver                          # :8000

# 2. Voice agent worker (separate terminal; only needed for calls)
cd backend/voice_agent && ../venv/bin/python agent.py dev   # Windows: ..\venv\Scripts\python agent.py dev

# 3. Dashboard (separate terminal)
cd frontend && npm install && npm run dev           # :3000

# 4. Baileys gateway (separate terminal; only for QR-paired WhatsApp)
cd backend/baileys_service && npm install && npm start      # :3020
```

---

## What works with zero API keys

This is the point of the sandbox, so it is worth being precise about it.

**Fully working offline:**

- The whole dashboard: contacts, chats, flow builder, analytics, settings, RBAC.
- **Chats.** Send a message on any channel and it is stored as a real
  `crm.Message` with `status="sandbox"` — it just never leaves the machine.
  Fake an inbound message with the **Simulate** action on a thread
  (`POST /api/whatsapp/threads/<wa_id>/simulate`, or the messaging equivalent).
- **WhatsApp templates.** Three demo templates (`order_confirmation`,
  `delivery_update`, `appointment_reminder`) are served and rendered locally, so
  the template path is demoable without a WABA.
- **Flow builder + engine.** Flows execute for real against the seeded contacts;
  sends land in the outbound queue and are stored as sandbox messages.
- **Knowledge base, skills, persona.** Editable and previewable — `POST
  /api/aicore/knowledge/preview` shows the exact prompt block the AI would get.
- **Integrations.** `GET /api/integrations/providers` reports `sandbox: true`;
  every flow action returns `{ok: false, skipped: true, detail: "<Provider> is
  not connected — step skipped."}` and the flow run continues. `oauth/start`
  returns a readable 400 instead of opening a socket.

**Needs a key:**

- AI replies and conversation summaries — set `OPENAI_API_KEY` or
  `OPENROUTER_API_KEY` in `backend/.env`. Without one, the AI paths log and skip.
- Any real send/receive (see below).
- Voice calls of any kind — they need LiveKit plus a realtime model.

---

## Connecting real channels

All accounts are added from **Settings → Channels** in the dashboard; nothing
requires a restart. `.env` keys act as a read-only fallback account.

| Channel | What you need |
|---|---|
| **WhatsApp (Cloud API)** | A Meta app, a phone number ID, an access token, an app secret, and a public webhook URL. Full walkthrough: **[backend/whatsapp/SETUP.md](backend/whatsapp/SETUP.md)**. |
| **WhatsApp (Baileys)** | Nothing but a spare phone. Add an account with provider `baileys` and scan the QR. ⚠ Read the ToS warning below. |
| **Telegram** | A BotFather token. Then **Set webhook** on the account with your public base URL. |
| **Messenger / Instagram** | A Meta page/IG-professional account + page access token; webhook at `/api/messaging/webhook`. ⚠ No signature verification yet — see below. |
| **Email** | Per-mailbox SMTP + IMAP credentials on an email account row. Inbound is pulled by `POST /api/messaging/email/sync` (the chats page nudges it). |
| **Voice (SIP)** | LiveKit + a SIP trunk. Run `backend/voice_agent/setup_trunk.py` once, then fill Settings → Channels → Voice. |

Inbound webhooks need a public HTTPS URL. For local development, tunnel port
8000 (`ngrok http 8000` or `cloudflared tunnel --url http://localhost:8000`)
and add the tunnel host to `ALLOWED_HOSTS` in `backend/.env`.

## Connecting integrations

**Settings → Integrations.** Two kinds:

- **Google** (Calendar, Drive, Gmail, Sheets) — OAuth2 with PKCE. Create one
  Google Cloud OAuth client, paste the client id/secret into the superadmin-only
  panel on that page (or set `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` in
  `.env`), then click **Connect** per product. Scopes are requested
  incrementally, per product.
- **Shopify / Stripe** — API-key providers. Shopify wants a custom-app Admin API
  token; Stripe wants a secret key. Both also take a **webhook signing secret**,
  and both webhooks are HMAC-verified. An unsigned or badly-signed payload is
  rejected with **401**, and a webhook whose secret was never configured
  answers **503** — it never falls back to accepting unsigned traffic.

Shopify `orders/*` and Stripe `payment_intent.succeeded` are mapped onto
`crm.Record` + `crm.RecordEvent`, matching the contact by phone then email. That
is what makes the e-commerce vertical work end to end without writing code.

The seven integration actions available to flow-builder Action nodes:
`Create calendar event` · `Find free slot` · `Upload file to Drive` ·
`Append row to Sheet` · `Send via Gmail` · `Update order status` ·
`Create Stripe payment link`.

---

## Repo layout

```
ChatBranch/
├── backend/
│   ├── core/              Django project: settings, root urls
│   ├── crm/               THE DATABASE — Contact, Record, RecordEvent, Thread,
│   │                      Message, ChannelAccount, KnowledgeItem, Workflow,
│   │                      FlowRun, OutboundJob, Setting, Profile, AuditLog…
│   │                      (+ /admin, `manage.py seed_demo`)
│   ├── calls/             REST: contacts, dial, live transcript, analytics,
│   │                      voices; `lk.py` bridges Django → voice_agent
│   ├── whatsapp/          WhatsApp Cloud API + Baileys transport, webhook,
│   │                      account health, routing, voice-call bridge
│   ├── messaging/         Email (SMTP/IMAP), Telegram, Messenger, Instagram
│   ├── aicore/            The AI brain: assistant.py, persona.py, memory.py,
│   │                      knowledge.py
│   ├── flows/             Flow execution engine + the outbound pacing queue
│   ├── integrations/      Google / Shopify / Stripe: OAuth, webhooks, actions
│   ├── ingest/            Tenants, brands, external data-source sync intake
│   ├── api/               DRF /api/v1 (whoami, contacts, brands, users, schema)
│   ├── voice_agent/       LiveKit worker — run as its own process
│   │   ├── agent.py       worker entrypoint (`python agent.py dev`)
│   │   ├── caller.py      SIP dial / hangup
│   │   ├── prompts.py     call persona + summary schema
│   │   ├── storage.py     the file-based IPC layer
│   │   └── setup_trunk.py one-time SIP trunk creation
│   ├── baileys_service/   Node WhatsApp Web sidecar (see its README)
│   ├── data/              runtime state + media (gitignored — contains PII)
│   └── tests/             863 backend tests
├── frontend/src/
│   ├── app/               routes: dashboard, contacts, chats, flow-builder,
│   │                      analytics, call-logs, activity, persona, settings/*
│   ├── components/        shell, flow builder, settings tabs, chat widgets
│   └── lib/               AppContext (call state), auth, flow model, mocks
├── docs/                  vertical guide, schema, API + RBAC conventions,
│                          channel notes, bulk record sync
├── package.json           root launcher (setup / dev:all)
├── CLAUDE.md              deep architecture notes for coding agents
└── AGENTS.md              short version of the same
```

---

## Verify a change

```bash
cd backend  && venv/bin/python manage.py check              # Windows: venv\Scripts\python
cd backend  && venv/bin/python manage.py makemigrations --check --dry-run
cd backend  && venv/bin/python -m pytest tests/ calls/tests -q     # 863 tests
cd frontend && npx tsc --noEmit
cd frontend && npx next build
```

`manage.py check` reports one benign `staticfiles.W004` warning (there is no
`backend/static/` directory) — that is expected.

There is **no CI workflow in this repo**. If you publish a fork, wiring the four
commands above into GitHub Actions is a five-minute job and worth doing.

---

## Honest limitations

A template that oversells wastes your time. These are the real edges:

- **WhatsApp via Baileys violates WhatsApp's Terms of Service.** It speaks the
  unofficial WhatsApp Web protocol. Automated messaging — and *especially*
  automated calling — gets numbers banned. It is here for development and demos
  because Meta's calling API is gated. Use a dedicated spare number, keep volumes
  low, and do not ship it to production. Background research, including real ban
  statistics and where the queue's pacing defaults come from:
  [`backend/baileys_service/RESEARCH.md`](backend/baileys_service/RESEARCH.md).
- **WhatsApp Cloud voice calling is untested against a live number.** Meta's
  Business Calling API is a gated beta. The bridge (`whatsapp/bridge.py`,
  aiortc ↔ LiveKit) is fully implemented and self-tests offline
  (`python -m whatsapp.bridge --selftest`), but it has never completed a call
  with a real calling-enabled Meta number. Expect field debugging.
- **The Messenger / Instagram webhook has no signature verification.** The
  WhatsApp webhook checks `X-Hub-Signature-256`; `/api/messaging/webhook` does
  not. Anyone who learns the URL can post fake inbound messages. This is a known,
  unfixed gap — see [`docs/channel-adapter.md`](docs/channel-adapter.md). Do not
  expose it publicly without fixing it first.
- **Integrations are fixture-tested, never provider-tested.** The Shopify and
  Stripe webhook mappings, and the HTTP shapes of the seven Google actions, are
  covered by tests built from documented payloads — no live Shopify store,
  Stripe account or Google Workspace has ever exercised them. Treat the first
  real connection as an integration test you are running yourself.
- **SQLite by default.** Fine for development and a single-host pilot; it is not
  a production database for concurrent writes. `DATABASE_URL` with a
  `postgres://` value switches the ORM to Postgres, but nothing beyond the ORM
  has been tuned for it.
- **Tenant isolation is enforced in the application layer only.** There is no
  row-level security. A missing filter is a data leak; the tests cover the
  current query paths, not future ones.
- **Demo credentials and a dev secret key ship in the repo.** Before any
  deployment: rotate the three demo passwords, set a real `SECRET_KEY`, set
  `DEBUG=False`, set `ALLOWED_HOSTS` / `CORS_ALLOWED_ORIGINS`, and change
  `BAILEYS_SECRET`. The WhatsApp verify token auto-generates on first use — the
  shipped default is never accepted.
- **SMS has no provider.** Flow SMS actions log and skip.
- **`docker-compose.yml` is unverified.** It exists (backend + frontend +
  Postgres) and the two Dockerfiles are real, but the compose stack was not
  built or run during this release. Treat it as a starting point, not a
  supported deployment. `backend/start.sh` is the production entrypoint
  (gunicorn; `RUN_VOICE_AGENT=1` also starts the agent worker in the same
  container so the file-based IPC stays on one filesystem).
- **The AI insights panel's sentiment/highlights are static mock data.** The
  transcript and knowledge-base references next to them are real.

---

## Where to go next

| You want to… | Read |
|---|---|
| Configure this for your industry | **[docs/vertical-guide.md](docs/vertical-guide.md)** |
| Understand the data model | [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md) |
| Call the API | [docs/API_CONVENTIONS.md](docs/API_CONVENTIONS.md) · live schema at `/api/v1/docs` |
| Feed contacts/records from your own system | [docs/record-sync.md](docs/record-sync.md) |
| Understand roles, tenants, brands | [docs/RBAC_AND_BRANDS.md](docs/RBAC_AND_BRANDS.md) |
| Add or debug a channel | [docs/channel-adapter.md](docs/channel-adapter.md) |
| Link a real WhatsApp Business number | [backend/whatsapp/SETUP.md](backend/whatsapp/SETUP.md) |
| Understand the Baileys transport and its risk | [backend/baileys_service/README.md](backend/baileys_service/README.md) · [RESEARCH.md](backend/baileys_service/RESEARCH.md) |
| Change the code (or point a coding agent at it) | [CLAUDE.md](CLAUDE.md) |

## Notes

- **Dev slowness is Next.js**, not your machine: each route compiles on first
  visit. Use `next dev --turbo` or a production build for a faster loop.
- API base for the frontend is `NEXT_PUBLIC_API_URL` (default
  `http://localhost:8000/api`). If the API is unreachable the dashboard falls
  back to the bundled mock data in `src/lib/mocks.ts`.
- `NEXT_PUBLIC_MAX_CONCURRENT` (default `3`) caps simultaneous live calls —
  match it to your SIP trunk's concurrent-channel limit.
- The WhatsApp module's API surface and the `/chats` three-pane design follow
  [Whatomate](https://github.com/shridarpatil/whatomate) (Go, AGPL). Protocol
  shapes were reimplemented, not copied.
