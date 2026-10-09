# Configuring ChatBranch for your industry

ChatBranch ships neutral. It knows how to hold a conversation across its
channels and a phone line; it knows nothing about *your* business until you tell
it.

This guide walks through configuring it for a real vertical end to end. Almost
none of it is code — it is skills, knowledge, tags, a flow and a data mapping,
all edited in the dashboard. **If you find yourself editing C# to add a
vertical, stop: something belongs in one of the layers below instead.**

Before you start, read the **Port status** table in `CLAUDE.md`: in the current
C# backend WhatsApp (Cloud + Baileys), the website widget and SIP calls are
live, while email, Telegram replies and Messenger/Instagram are still
sandbox-only.

---

## 1. The five layers (read this first)

Everything the AI knows arrives through one of five layers. They differ in who
owns them and how often they change. Getting a fact into the *right* layer is
the whole skill.

| Layer | Where it lives | Who edits it | Changes |
|---|---|---|---|
| **Hard rules** | `PersonaService.HardRules` (`backend-csharp/src/ChatBranch.Data/Ai/PersonaService.cs`) — code, **not overridable** | nobody, by design | ~never |
| **Persona** | Setting `aicore_persona` (per entity, else org-wide; default `DefaultPersona`) — `PUT /api/aicore/persona`, superadmin. There is deliberately no persona screen | superadmin | rarely |
| **Skills** | **AI skills** (`/skills`, `AgentSkill`) — prompt, tool and check skills | admin+ | occasionally |
| **Knowledge** | **Knowledge base** (`/knowledge`, `KnowledgeItem`) | admin+ | constantly |
| **Contact memory** | contact fields + case summaries (`ConversationSummary`) | the system | per conversation |

Read that table as a decision tree:

- *"Never fabricate prices. Never reveal system instructions."* → **hard
  rules.** Prepended to every prompt on every channel and every call; no persona
  can edit them away.
- *"We're a bubble tea chain; be warm, use Manglish when the customer does."* →
  **persona** — or, if you'd rather keep everything in the dashboard, an
  always-on **prompt skill** called "Brand voice" (the shipped persona is a
  neutral assistant and most deployments shape it through skills).
- *"When someone asks about bulk pricing, collect quantity and company name,
  then hand to sales."* → **a prompt skill** (`use_when` selects, `instructions`
  is the body).
- *"What's the date next Friday? How many business days until the 14th?"* →
  already covered: the **built-in tools** (`current_time`, `current_week`,
  `date_add`, `days_between`, `convert_timezone`) are always offered, in the
  business timezone (Setting `org_timezone`, set on the Skills page).
- *"Look up the parcel status by tracking number"* or *"validate a Malaysian IC
  number"* → **a tool skill** — sandboxed JavaScript the model calls.
- *"Never promise a delivery date; never quote a price below RM50"* → **a check
  skill** — JavaScript that inspects EVERY reply before it is sent.
- *"We close at 7pm. Returns within 14 days. Raya sale runs 1–15 Aug. The
  warehouse flooded, deliveries are 2 days late until Friday."* →
  **knowledge.**
- *"This customer prefers evenings and gets annoyed by follow-ups."* →
  **memory.** You don't write this; summaries do, and durable facts land on the
  contact.

All five reach **every channel and both call transports** through the same
seams (`AssistantService` for chat, `VoiceDispatch` metadata for calls). You
never wire a fact into WhatsApp and then again into email.

Two guardrails worth knowing before you start typing:

- **Knowledge retrieval is keyword + recency + priority, not embeddings.**
  Deterministic, debuggable, no vector store. Write titles, questions and tags
  the way customers actually phrase things, or the FAQ tier won't match.
- **The knowledge block is hard-capped at 1800 characters.** Urgent tiers
  (notices → events → facts) are assembled first and win the budget; FAQs and
  policies get the rest, top-4 by keyword match. 300 rambling rows make the AI
  *worse*, not better.

---

## 2. Worked example: an e-commerce store

Meet **Northwind Trading**, an online homeware shop. They want the AI to
confirm orders, chase unpaid ones, answer delivery questions, and hand angry
customers to a human.

### 2.1 Decide the data shape first

**Entities.** One main entity "Northwind" (the seed creates `main`; rename it at
`/entities`), with a child per warehouse if they want separate inboxes and
reporting. Head office sees everything beneath it; a warehouse team sees only
its own.

**`Record.type`** — what a conversation is *about*: `order`, `shipment`,
`invoice`, `booking`, `ticket`, `custom`. Northwind uses `order` and `shipment`.

**`Record.status`** — free text, **your vocabulary**. The platform cares about
one thing: `closed`, `done`, `resolved`, `cancelled`, `completed` mean "nothing
more to do", and the primary-record pick skips them. Northwind picks:

```
awaiting_payment → paid → packed → shipped → delivered
                                 ↘ cancelled   (a closed status)
```

**`Record.due_date`** — whatever "when" means for that record (delivery date,
payment due date, appointment). It renders as `{{due_date}}`. Flow **date
triggers read a date custom field on the contact**, not the record — if a flow
must fire relative to a date, also sync it into a contact custom field (below).

**`Record.data`** — a JSON bag: line items, addresses, `tracking_url`
(→ `{{tracking_url}}`), `link`/`url` (→ `{{link}}`).

**Custom fields** (Settings → Custom fields) — facts about the *person* the
fixed columns don't cover. Northwind adds `loyalty_tier` (select),
`first_order_at` (date) and `next_delivery` (date — written by their own
system through `PATCH /api/v1/contacts/{id}` `{"attributes": {...}}`, used by the
delivery-reminder flow). Values render as `{{cf_loyalty_tier}}`.

**Tags** (`/tags`) — what a contact *is*, and anything you branch on. Northwind
creates `vip`, `wholesale` and a **group** "Customer type" holding exactly one
of `retail` / `trade`. A tag can carry **attributes**: `wholesale` gets
`account_manager` (text) and `credit_limit` (number), rendered as
`{{tag_wholesale_account_manager}}`. Conversation tags (`refund`, `damaged`,
`urgent`) label threads in the inbox. The AI sees tag *names* in its contact
context; tag attributes and custom fields are not in the prompt.

### 2.2 Set the voice

Either set the persona once (`PUT /api/aicore/persona`, superadmin) or — the
dashboard-only route — create a prompt skill:

> **Brand voice** · use when: *always* · Warm, brief, practical — the friendly
> person behind the counter, not a support macro. Reply in 1–3 sentences in the
> customer's language (English, Bahasa Malaysia or Chinese). Lead with the
> answer. Always quote the order number. Never offer discounts, waive shipping
> or approve a refund — say a colleague will confirm. Hand over when someone
> asks for a human, is angry, reports a damaged item or disputes a charge.

Opening hours, the returns window and the current promo are NOT in here —
those are knowledge, and they change weekly.

### 2.3 Write the skills

**AI skills** (`/skills`). Three kinds:

| Kind | What it is | Runs |
|---|---|---|
| **Prompt** | `use_when` + `instructions` in the prompt's SKILLS section | every reply, every call |
| **Tool** | JavaScript `run(args, ctx)` with a JSON-schema of arguments; the model decides to call it | when the model calls it |
| **Check** | JavaScript `check(reply, ctx)` returning `{ok:true}` or `{ok:false, reason, fix?}` | on EVERY draft before it is sent |

Code runs in a sandbox (Jint: no network, no files, no host objects; 250 ms,
8 MB). Every code skill carries **test cases**; saving an enabled skill whose
code or tests changed requires its tests to pass. You don't have to write the
code: **describe** the skill in plain words and the wizard asks up to four
clarifying questions, drafts the code and the tests, and runs them before you
save. With no AI key configured it still works from a built-in template library
(opening hours, dates, phone numbers, email addresses, Malaysian IC numbers).

When a **check** fails, the AI rewrites the reply with the reason (at most
twice); then the check's **on fail** decides: `retry` → hand over to a human,
`handover` → hand over at once, `block` → send nothing. Handing over adds a
private note, sets the conversation to pending and notifies the team.

Northwind's skills:

| Skill | Kind | What |
|---|---|---|
| Order status | prompt | use when the customer asks where their order is: state the order number, status in plain words and the promised date; send the tracking link |
| Payment chase | prompt | when an order is `awaiting_payment` and the customer replies: confirm item and total, offer the payment link, never pressure |
| Bulk enquiry | prompt | collect quantity, item, company name and postcode, then hand over to sales; never quote a price |
| Damaged item | prompt | apologise, ask for a photo and the order number, hand over |
| Opening hours | tool | `is_open_now()` from the shop's weekly schedule |
| No invented dates | check | fail any reply that promises a delivery date not present in the order |
| No refunds promised | check (`handover`) | fail any reply that says "refund approved" — a person decides refunds |

Skills can be limited to channels (empty = all). **`/skills` is the only place
skills are edited**; the flow builder's step picker only selects them.

### 2.4 Fill the knowledge base

**Knowledge base** (`/knowledge`). The kind decides retrieval:

| Kind | Retrieval | Use for |
|---|---|---|
| **`notice`** | Tier 1 — always included while live | "Warehouse flooded, deliveries +2 days" |
| **`event`** | Tier 2 — always included while live | "Raya sale, 15% off, 1–15 Aug" |
| **`fact`** | Tier 3 — always included | hours, address, support number, shipping fees |
| **`faq`** | Tier 4 — only on a keyword match | "Do you ship to East Malaysia?" |
| **`policy`** | Tier 4 — only on a keyword match | returns, warranty, exchanges |

Every item takes a title, content, `question` (FAQ), tags (matching hints),
channels, priority, `active` and a `starts_at` / `ends_at` window — **the date
window is what makes a sudden notice self-expire**. Tick **Published** to show
an item on the public Help Center (`/help`); `active` (the AI may use it) and
`published` (the public may read it) are independent.

Then use **Preview** (`POST /api/aicore/knowledge/preview`): type a real
customer message and see the exact `COMPANY KNOWLEDGE` block the AI receives and
how much of the budget it used.

### 2.5 Load contacts and records

- **By hand** — `/contacts` → Add, or `POST /api/v1/contacts`. A phone sharing
  its last 8 digits with an existing contact is refused (409).
- **CSV import** — `/contacts` → Import (`POST /api/contacts/import`, with a
  `?preview=1` dry run that shows created/updated/skipped before you commit).
- **Bulk sync** — `POST /api/v1/sync/records` (admin role or an API key), one
  object per record with an optional contact; contacts match by phone, records
  by `(type, external_id)`. See [record-sync.md](record-sync.md).
- **Webhooks** — connect Shopify and Stripe (`/integrations`): `orders/*`
  creates/updates `order` records + events; `payment_intent.succeeded` books the
  payment (set `metadata.record_external_id` to attach it to the existing
  order). Redelivery is idempotent.

### 2.6 Connect channels and decide who answers

**Channels** (`/channels`) — add an inbox per number, mailbox, bot, page or
widget with the setup drawer. Northwind adds:

- a **WhatsApp Cloud** business number, role `reminder` (templates; AI off by
  default);
- a **website widget** for the shop (paste the snippet the inbox shows);
- optionally a **Baileys** personal number, role `conversation` (AI on by
  default) — read the ToS warning in the README first.

Step 3 of the drawer, **who answers it**, sets per inbox: the owning **team**
(new conversations go there and round-robin across its members), the **AI
default** (on/off for new conversations; an operator's per-chat tick always
wins) and the **AI review mode** (below). Teams live at `/teams`; event rules
("label refund requests and route them to Billing") at `/automations`.

Per-account **delivery** settings (edit account → Delivery & AI): quiet hours
(one window or per weekday), send pacing (min/max gap between messages), and for
calls the brain and speech-to-text engine.

Routing knobs (the **Routing rules** card on `/assignments`): `ai_reply_mode`
(`queued` waits for the customer to finish and answers once; `instant`),
`cold_text_guard` (`block` refuses free text from a personal number to someone
who never wrote to it — the most important anti-ban setting on Baileys),
`cold_call_guard`, `reminder_ai_default`.

### 2.7 Decide how much the AI does on its own — the AI monitor

**AI monitor** (`/ai-monitor`) records EVERY message the AI writes. The review
mode (organization default, overridable per entity and per inbox) decides what
happens before it is sent:

| Mode | Behaviour |
|---|---|
| `auto` | sent straight away (still recorded) |
| `review_flagged` | held when it looks risky — the first N replies of a conversation, a money amount, a date, or a word from the risky-keywords list (price, refund, deposit, appointment…) |
| `review_all` | every reply waits for a person |
| `shadow` | the AI writes drafts but never sends — for trialling it safely |

Held replies show up in the monitor and as a banner in the chat; approve, edit
and send, or reject. Optionally **auto-send after N minutes** if nobody acts.
Approving re-checks the send-time guards (opt-out, linked contact, 24h window;
quiet hours wait). Agents hold `ai.monitor` by default; changing the settings
needs `ai.behaviour.manage`.

Northwind starts in `review_all` for a week, reads the trail, adds a check skill
for the mistakes it sees, then moves to `review_flagged`.

**Human-like behaviour** (AI monitor → settings; defaults on):

- **Wait for the customer to finish** (`ai_wait_seconds`, default 25 s, capped
  by `ai_wait_max_seconds`) so three short messages get one answer.
- **Acknowledgements don't need a reply** — "ok", "thanks", 👍 after an answer
  get silence (or a short `ai_ack_text`).
- **Typing** — shows "typing…" on WhatsApp and pauses in proportion to length.
- **Split long replies** into at most 3 messages at sentence boundaries.
- **Follow-ups when the customer goes quiet** (off by default): only after the
  AI asked something or left the conversation open; first after
  `ai_followup_first_hours`, then every `ai_followup_between_hours`, at most
  `ai_followup_max` per silence; stops on any reply, opt-out, resolution or a
  person taking over. A closed WhatsApp 24h window uses `ai_followup_template`
  or is skipped with a note. `ai_followup_guidance` shapes the tone.

### 2.8 Set the business variables

`{{business_name}}`, `{{agent_name}}`, `{{support_number}}`,
`{{support_email}}`, `{{website}}` come from Setting rows of the same names. There
is no dedicated screen yet: a superadmin sets them with
`PUT /api/settings {"key": "business_name", "value": "Northwind Trading"}`.

### 2.9 Build the flows

**Flow builder** (`/flow-builder`). Start from a template (welcome series,
appointment reminder, re-engage, lead qualification, feedback request) or a
blank flow. The **Steps** view edits the flow as a list; **Advanced** shows the
same graph as a canvas. Toggle **Live** and the engine runs it; "Contact
created" and "Scheduled" triggers only act on what happens *after* the flow
went live, so arming a flow never back-fires on the whole contact book.

Triggers: a contact is created · a contact is tagged · a message arrives
(optionally with a keyword / on a channel) · a date comes round (days
before/on/after a contact's date custom field, optionally yearly) · on a
schedule · started by hand or by the API (`POST /api/flows/{flowId}/start`). The
old record-due-date triggers still run in flows that use them but are no longer
offered.

Steps: **Send message** (your words or a WhatsApp template) · **AI message**
(the AI writes it, guided by a prompt skill, and it passes the AI monitor) ·
**Wait** (a time, or until they reply) · **Follow up if no reply** (nudge up to 5
times until they answer; *yes* = replied, *no* = never did) · **Ask & branch**
(they replied / their reply contains… / they said yes — English + Malay keyword
lists / they have a tag / a field equals) · **Call** (AI voice call or a script
call, over the phone line or WhatsApp) · **Hand over** (to a team, with a
priority and reason) · **Update contact** (add/remove a tag, set a field) ·
**App action** (calendar, Sheets, Drive, Gmail, Shopify, Stripe) · **End**
(with an outcome).

Northwind's three:

- **Order confirmation.** Trigger: started by the API — their shop calls
  `POST /api/flows/wf-order-confirmation/start {"contactIds": [...]}` with an
  API key when an order is paid (or tags the contact `paid-order` through
  `PUT /api/contacts/{id}/tags` and the flow triggers on the tag) → Send
  WhatsApp template `order_confirmation` with `{{first_name}}`,
  `{{record_title}}`, `{{due_date}}` → End.
- **Payment chase.** Trigger: tagged `awaiting-payment` → Wait 2 days → Ask &
  branch "has tag `awaiting-payment`" (still unpaid?) → App action *Create
  Stripe payment link* (writes the URL to `record.data.link`) → Send "Hi
  {{first_name}}, here's the link for {{record_title}}: {{link}}" → Follow up if
  no reply (1 day, 2 nudges) → no → Hand over to Billing.
- **Delivery reminder.** Trigger: a date comes round — 1 day before
  `next_delivery` → AI message using the "Order status" skill → End.

**Sends are queued, not inline.** Every message becomes an `OutboundJob`
drained with jitter, caps and quiet hours; calls are queued too, and `sent` on a
call means *dialled*. The **Runs** panel shows how many people reached each
step, the drop-off between steps, why runs failed and each contact's path.

**Variables** (`lib/flow/variables.ts` ↔ `FlowEngine.VariablesFor`):

| Group | Keys |
|---|---|
| Contact | `first_name`, `name`, `phone`, `email`, `company`, `language` |
| Business | `business_name`, `agent_name`, `support_number`, `support_email`, `website` |
| Record | `record_title`, `record_status`, `record_id`, `record_type`, `amount`, `currency`, `due_date`, `days_until_due`, `days_overdue`, `link`, `tracking_url`, `account_number` |
| Memory | `best_contact_time`, `preferred_channel`, `last_contact_date` |
| Custom fields | `cf_<key>` |
| Tag attributes | `tag_<slug>_<key>` |
| Other | `situation` (the contact's notes) |

A missing value renders empty; an unknown `{{key}}` is left visible on purpose.

### 2.10 Connect integrations

**Integrations** (`/integrations`): Shopify (orders in), Stripe (payments in,
payment links out), Google Sheets (an exceptions log via *Append row to Sheet*),
Google Calendar (*Find free slot* / *Create calendar event*). A Google
connection can belong to one user, an entity, or the whole organization; the
acting user's own connection wins. These mappings are fixture-tested against
documented payloads, never against a live provider — treat your first real
connection as an integration test and watch the flow's Runs panel.

### 2.11 Go live

- Boot without `SEED_DEMO` (no demo logins or sample contact) and with
  `SUPERADMIN_PASSWORD` set.
- Create real users at `/users`, assign agents to entities, put them in teams.
- Set the CORS origin and `FRONTEND_URL` / `GOOGLE_REDIRECT_URI` for your host.

---

## 3. A second vertical, briefly: last-mile logistics

| Decision | Value |
|---|---|
| Entities | the company → one child per depot |
| `Record.type` | `shipment`, `external_id` = consignment number |
| `Record.status` | `picked_up → in_transit → out_for_delivery → delivered`, plus `failed_attempt`, `returned`, `cancelled` |
| `Record.due_date` | the delivery ETA |
| `Record.data` | `{tracking_url, address, driver, attempts}` |
| Contact tags | `fragile`, `signature_required`, `business_address`; a `driver` tag with `licence_no` + `vehicle` attributes for the drivers you also message |
| Custom fields | `preferred_window`, `gate_code` |

**Skills**: *Where is my parcel* (prompt) · *Failed delivery, rebook* (prompt) ·
*Validate a postcode* (tool) · *Never give out the driver's number* (check,
`block`).

**Knowledge**: facts — depot hours, service areas, what happens after three
failed attempts; policies — redelivery fees, holding period; FAQs — "Can I
collect from the depot?"; notices — "Flash floods in Klang, Zone 3 delayed"
with an end date.

**Flows**: tagged `out-for-delivery` → "arriving today" with
`{{tracking_url}}`; tagged `failed-attempt` → AI message rebooking, Follow up if
no reply, Hand over if they want a different address.

The pattern generalises to a clinic (`booking`, reminders, reschedules), a B2B
firm (`invoice`, payment follow-ups) or a support desk (`ticket`). What changes
is the noun, the status words, the tags and the prose. What doesn't change is
any code.

---

## 4. Checklist

- [ ] Entity tree set up; inboxes bound to the right entity.
- [ ] `Record.type` and its **status vocabulary** written down, closed statuses
      matching the platform's (`closed`, `done`, `resolved`, `cancelled`,
      `completed`).
- [ ] Custom fields and tags (with groups and attributes) created.
- [ ] Brand voice set (persona or an always-on prompt skill); hard rules left
      alone.
- [ ] One prompt skill per repeatable situation; check skills for the mistakes
      you can't afford; tests passing.
- [ ] Knowledge base seeded and **Preview** run against five real messages.
- [ ] Business variables set.
- [ ] Inboxes connected (or deliberately left in sandbox), each with a team, an
      AI default and a review mode.
- [ ] AI monitor reviewed for a week before moving off `review_all`.
- [ ] Flows tested against a seeded contact, then toggled Live; the Runs panel
      checked.
- [ ] No `SEED_DEMO`, `SUPERADMIN_PASSWORD` set, CORS and URLs configured.
- [ ] The limitations section of the main README read by whoever signs off.
