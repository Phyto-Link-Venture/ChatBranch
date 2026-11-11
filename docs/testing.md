# Testing ChatBranch

Four layers, from fastest to most real:

| Layer | What it proves | Run it |
|---|---|---|
| Unit + API tests | every endpoint, guard and pipeline against fake providers | `npm run test:api` (xUnit, `backend-csharp/tests`) |
| **Test lab** | realistic use cases end to end through the REAL pipelines on a running server | `/test-lab` page · `npm run test:scenarios` · `TestLabTests` |
| Manual checklist | what each role sees and may do, in the browser | [below](#manual-checklist-per-role) |
| Real channels | the transports themselves, against the providers | [below](#real-channels) |

---

## The Test lab

A catalog of scripted use cases (`backend-csharp/src/ChatBranch.Api/TestLab/`)
that run inside the API process through the same code a webhook, an agent or a
worker runs: `InboundProcessor.ProcessInbound` (the webhook path), the AI
responder + AI gate + AI monitor, the outbound queue, `Routing.ForThread` and
failover, the flow engine, automations, team round-robin, the identity exchange,
calendar bookings, the dashboard's own endpoints called in-process as a given
user (`InternalApi` — so RBAC, scope filters and validation all apply) and the
read gates. Each scenario reports named checks, pass/fail, and links to what it
made (the conversation, the contact, the AI monitor, the flow).

### The fence

Everything the lab creates lives under ONE top-level entity, **Test lab**
(slug `test-lab`, branches `test-lab-north` / `test-lab-south`):

- its own sandbox inboxes (a QR line, a business line, a Telegram bot, a website
  widget, one line per branch) — "configured" for routing, so lab conversations
  never route to your real numbers, but with no credentials, so nothing leaves
  the machine (`status="sandbox"`);
- contacts tagged `test-lab` with `@testlab.invalid` emails and unique numbers
  per run; users `testlab_*`; teams "Test lab · …"; custom fields `testlab_*`;
- its own entity-level Settings (AI on, instant replies, no review, no human-like
  delays) — the org-wide settings are never changed.

`POST /api/testlab/reset` (the page's **Reset test data**) deletes everything
inside the fence — conversations, messages, contacts, drafts, bookings, queued
jobs, flow runs, inboxes, teams, settings, the `testlab_*` users and roles, the
lab's notifications — and nothing else. `TestLabTests.Reset_deletes_the_lab_and_nothing_else`
proves it by counting every table outside the fence before and after. The run
history and the audit trail are kept.

### The AI brain

With no AI key the lab's deterministic test brain writes the AI's words, so a
scenario always says the same thing. Scenarios marked **AI key** (today
`ai-faq-knowledge`) also run against the configured model when one exists and
check the meaning of its answer; without a key they run their deterministic
variant. Everything else is model-independent on purpose.

Global seams the lab drives (the outbound queue drain, the flow tick, the AI
monitor tick) are the same ones the background workers and the manual
`/tick` endpoints call — on a live server they process whatever is due, the
lab's jobs included.

### Categories

| Category | Covers |
|---|---|
| `ai` | AI replies, knowledge grounding, acknowledgement skip, the master switch + schedule, per-contact "never", follow-ups, reply checks |
| `review` | AI monitor review modes: review all (approve / reject), shadow, review flagged (risky keywords) |
| `handover` | the customer asks for a person; the AI promises one |
| `identity` | unknown customers (ask, ask and create, several matches, one match), invite links |
| `compliance` | STOP / START, the cold-text guard |
| `routing` | business redirect off a reminder number, failover off a banned number, quiet hours |
| `access` | entity scope (branch agents), linking out of scope, read gates for a login with no capabilities |
| `automation` | flows (welcome series, keyword menu), automation rules, team round-robin |
| `contacts` | CSV import duplicates, exclusive tag groups, template variables (`{{cf_*}}`, `{{tag_*}}`) |
| `booking` | the calendar skill books and cancels at delivery; a held reply releases its slot |
| `platform` | Ask ChatBranch history isolation, a user's language preference, the website widget, the AI playground never touching real data |

The full list with "what it proves" is on the page and in
`npm run test:scenarios -- --list`.

### The page — `/test-lab`

Sidebar → Review → **Test lab** (the `testlab.run` capability — superadmin only by default). Tick scenarios (or a whole
category) and **Run selected**, or **Run all**: the page runs them one at a time
and appends each result to one run, so you watch progress live and can stop
after the current one. Each row expands into its steps, the expected outcome,
every check with its detail, and links to open what it made. **Run history**
keeps the last 50 runs from the page, the CLI and the test suite.

**Load demo data** fills the fence with a realistic dataset to click through:
30 contacts across head office and two branches (tags, a tier group, custom
fields), knowledge, a calendar skill, rules, three paused flows, and
conversations in every state — AI-answered, agent-answered, needs human,
resolved, snoozed, opted out, a held draft in the AI monitor, a business-line
redirect, an unknown customer being identified, bookings (one cancelled), a
Telegram chat and a widget visitor. Switch the entity picker to **Test lab** to
see only them.

### The CLI — `npm run test:scenarios`

```bash
npm run test:scenarios                                   # everything
npm run test:scenarios -- --only stop-opts-out,widget-visitor
npm run test:scenarios -- --category ai,routing
npm run test:scenarios -- --list                         # catalog only
npm run test:scenarios -- --reset                        # wipe the fence first
```

`scripts/test-lab.mjs` (Node 18+, no dependencies) signs in, runs the selection
one scenario at a time into one run (source `cli`, visible on the page), prints a
table, writes `test-lab-report.json` and exits 1 on any failure (skips are not
failures; 2 = bad arguments or the API is unreachable). Env: `TESTLAB_API`
(default `http://localhost:8090/api`), `TESTLAB_USER` / `TESTLAB_PASS` (default
`superadmin` / `super123` — use your rotated password), `TESTLAB_REPORT`.

### The API

All need `testlab.run` (superadmin only by default, never a person bound to entities); the whole surface is 404 when the server runs with
`TEST_LAB=0` (default on — set it to `0` in production if you don't want it).

| Route | Shape |
|---|---|
| `GET /api/testlab/scenarios` | `{total, has_ai_key, categories:[{id, label, covers, scenarios:[{id, title, proves, steps[], expect[], needs_ai_key}]}]}` |
| `GET /api/testlab/status` | `{exists, entity, entity_name, demo_loaded, counts:{entities, accounts, users, contacts, conversations}}` |
| `POST /api/testlab/run` | body `{ids?: string[], run_id?: number, source?: "page"\|"cli"\|"api"\|"test"}` (no ids = all; `run_id` appends to that run) → `{run, results:[{id, title, category, passed, skipped, skip_reason, error, checks:[{name, ok, detail}], links:[{label, url}], duration_ms, brain, ran_at}]}`; 400 unknown id, 404 unknown run |
| `GET /api/testlab/runs` | `{runs:[{id, started_by, source, total, passed, failed, skipped, duration_ms, created_at, updated_at}]}` (last 50, `crm_testlabrun`) |
| `GET /api/testlab/runs/{id}` | `{run, results}` |
| `POST /api/testlab/reset` | `{ok, deleted:{table: rows}}` |
| `POST /api/testlab/demo` | `{ok, already_loaded, created:{…}, links:[…]}` |

Runs, resets and demo loads are written to the activity log (`testlab.run`,
`testlab.reset`, `testlab.demo`).

### In the test suite

`backend-csharp/tests/ChatBranch.Tests/TestLabTests.cs` runs the whole catalog
(every scenario must pass, carry checks and links), proves reset touches only
the fence, that admin/agent get 403 and anonymous 401, and that `TEST_LAB=0`
hides the API (404).

### Adding a scenario

Add a `LabScenario` to one of the `Scenarios.*.cs` files (and to `LabCatalog`'s
list): an id, a category, a title, one sentence of what it proves, steps,
expectations and a `Run(LabContext ctx)` that drives real seams
(`ctx.Say(...)` = an inbound message through the webhook path, `ctx.Api(...)` =
a dashboard endpoint as a given user, `ctx.Drain()` / `ctx.FlowTick()`, the
`LabKit` helpers for knowledge, skills, rules, flows and custom fields) and
records `ctx.Check(name, ok, detail)` plus `ctx.Link(...)`. Create data only
inside the fence (`ctx.NewContact`, `ctx.Inbox`, `ctx.SetEntitySetting` —
restored afterwards) and call `ctx.Skip(reason)` when the server's own config
makes the scenario meaningless (e.g. org-wide quiet hours right now).

---

## Manual checklist per role

Sign in as each (with `SEED_DEMO=1`: `superadmin/super123`, `admin/admin123`,
`agent/agent123`; create a "no capability" custom role at Admin → Permissions
with every box off and a login bound to it at Users). **Load demo data** first so there is
something to see; the `testlab_*` users are created by the lab with random
passwords — reset one at Users if you want to sign in as a branch agent.

**Superadmin**

- [ ] Every sidebar group is visible, including Review → Test lab, Settings → App & provider keys and AI keys & models.
- [ ] Test lab: Run all is green; Load demo data, open three conversations from its links; Reset empties the "Test lab" entity and leaves everything else.
- [ ] Users: can create an admin; Permissions: can edit the capability matrix.
- [ ] Channels: can add an inbox and see masked credentials round-trip.

**Admin**

- [ ] No Test lab, no Permissions, no App & provider keys / AI keys & models (they render "no access" if opened by URL).
- [ ] Users lists only the admin's tenant and lets them manage agents only.
- [ ] Chats: can assign, resolve, bulk-select, take a handover; AI monitor: approve / edit / reject a held draft.
- [ ] Flow builder: can create, pause and arm a flow; Tags / Templates / Knowledge editable.

**Agent**

- [ ] Sidebar shows Dashboard, Notifications, Chats, Contacts, Bookings, AI monitor, Tags (read-only), Activity, Call logs — no Flow builder, Channels, Users or Test lab.
- [ ] Chats: sees only conversations in their assigned entities (assign one to a branch to check); can reply, add a note, take a handover; cannot bulk-act or manage accounts.
- [ ] Opening `/users`, `/channels`, `/settings/appkeys` by URL shows "no access".

**No capability** (custom role, every box off)

- [ ] Lands on the first page they can see (or an empty shell), every sidebar page is hidden.
- [ ] Every deep link (`/chats`, `/contacts/1`, `/dashboard`) renders "no access"; the API answers 403 (the `read-gates-no-capability` scenario checks the API side).

**Every role**

- [ ] Ask ChatBranch (bottom-right): history shows only your own chats.
- [ ] Notifications bell and `/notifications` work; Settings → Appearance changes theme and language.

---

## Faking traffic without a provider

- **Simulate an inbound message** — `POST /api/whatsapp/simulate` (any signed-in
  user) runs the webhook's side effects: contact link, flows, team routing,
  automations, notifications; add `?ai=1` for the invite binder, the
  unknown-customer step and the AI reply:

  ```bash
  TOKEN=$(curl -s localhost:8090/api/auth/login -H 'Content-Type: application/json' \
    -d '{"username":"admin","password":"admin123"}' | node -pe 'JSON.parse(require("fs").readFileSync(0)).token')
  curl -s 'localhost:8090/api/whatsapp/simulate?ai=1' -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d '{"channel":"whatsapp","key":"60123456789","name":"Test customer","text":"Hi, what are your hours?"}'
  ```

- **Website widget test page** — the widget is the one channel that needs no
  provider. Add a Website widget inbox at Channels, copy its snippet and save a
  local page:

  ```html
  <!doctype html><html><body><h1>Widget test</h1>
  <script src="http://localhost:8090/widget.js" data-token="PASTE_TOKEN" async></script>
  </body></html>
  ```

  Serve it (`npx serve .` or `python3 -m http.server 8000`) and chat as a
  visitor; the conversation appears in `/chats` live. The `widget-visitor`
  scenario covers the same path automatically.

---

## Real channels

Inbound webhooks need a public HTTPS URL — tunnel port 8090 (`cloudflared
tunnel --url http://localhost:8090` or `ngrok http 8090`) and set
`PUBLIC_BASE_URL` to it.

- **Telegram bot** — the easiest real channel: create a bot with @BotFather, add
  a Telegram inbox with the token, press **Register webhook**, message the bot
  from your phone. Check: the message arrives, the AI or an agent answers, the
  typing indicator shows, a photo comes through, STOP opts out.
- **WhatsApp Cloud test number** — in a Meta developer app, WhatsApp → API Setup
  gives a free test number and lets you add up to five recipient numbers. Add a
  WhatsApp (Cloud) inbox with the phone number id, a token, the app secret and
  the WABA id; point the app's webhook at `/api/whatsapp/webhook` with the verify
  token from Settings → App & provider keys. Check: inbound, delivery ticks
  (sent → delivered → read), a template send, a reply inside the 24h window and
  the 409 outside it.
- **Baileys QR on a dev phone** — `npm run dev:baileys`, add a "QR" WhatsApp
  inbox, scan with a SPARE phone (⚠ unofficial transport, ban risk — read
  `backend/baileys_service/README.md`). Check: text + media both ways, status
  ticks, a voice call if the voice agent is running. Also **"Fetch older
  messages"** in the same drawer: one run, watch the paced progress (a request
  every 20–45 s), press Stop mid-run, then — after the 15-minute cooldown — start
  it again and confirm it CONTINUES rather than re-asking. Never point it at a number that matters — it
  is real WhatsApp traffic on an unofficial transport, so a spare number only,
  and the paired phone must be online or nothing comes back.
- **Website widget on a local HTML page** — as above; also try it on a phone on
  the same network (`http://<your-ip>:8090/widget.js`, add the page's origin to
  CORS if you serve it elsewhere).

## Languages

The dashboard ships English, Bahasa Melayu and 简体中文. Switch on `/login`
(the switcher under the form) or Settings → Appearance → Language; a user's
choice wins over the org default (Settings → Appearance, admin). Check a page in
each language for untranslated English and for layout overflow (Malay strings
run ~30% longer). The `language-preference` scenario proves the API side (a
user's saved locale round-trips, an unknown code is refused, reset falls back to
the org default).

## The Playwright tour

`scripts/test-lab-tour.mjs` drives the dashboard in a real browser the way a
person would: sign in, open `/test-lab`, **Run all** (fails if any scenario
fails), **Load demo data**, open the conversations and contacts it links to, and
with `--reset` reset the lab — a screenshot per step in `./test-lab-tour/`.

```bash
npm i -g playwright-core && npx playwright-core install chromium   # once
TESTLAB_WEB=http://localhost:3000 node scripts/test-lab-tour.mjs --reset
# or: PLAYWRIGHT_CORE=/path/to/node_modules/playwright-core CHROMIUM_PATH=/path/to/chrome …
```

`--skip-run` skips Run all; `HEADED=1` shows the browser; `--out dir` changes
where the screenshots go. The dashboard must be built with
`NEXT_PUBLIC_API_URL` pointing at the API you are testing, and the API's CORS
origins must include the dashboard's origin.
