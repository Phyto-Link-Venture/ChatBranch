# ChatBranch — AI tool-calling (the assistant can DO things)

> **Historical.** The Django-era plan for AI tool calling. Today's C# implementation is skills of kind `tool`/`check` plus built-in date tools (`ChatBranch.Data/Ai/Skills/`) — see [`CLAUDE.md`](../CLAUDE.md) and [`vertical-guide.md`](vertical-guide.md).

Turn the chat brain from "talks" into "acts": mid-conversation it can check the
calendar, offer real open slots, and book — and the framework generalizes to
order lookups, payment links, etc.

## Where it plugs in

`aicore/assistant.py generate_reply()` is today a single `chat.completions.create`
(system prompt + history → text) used by EVERY text channel. Tool-calling is an
**additive function-calling loop** around that call. When tools apply and the
provider supports them, the model may emit tool calls; we execute them, feed the
results back, and loop until it returns prose. When they don't apply, behaviour
is byte-for-byte what it is today — this path is critical and exception-safe, so
that fallback is non-negotiable.

Scope this pass = **text channels** (WhatsApp/Cloud+Baileys, Telegram, Messenger,
IG, email — all flow through `generate_reply`). **Voice is out of scope**: the
LiveKit agent is a separate Django-free process; giving it tools is a later,
bigger task. Note it as follow-up, don't attempt it here.

## Pieces

### 1. `aicore/tools.py` (new) — the tool registry
- Each tool = a JSON schema (OpenAI `tools` format) + a Python dispatcher that
  calls into `integrations/actions.py` and returns a compact result dict.
- Ship the calendar set first, but structure it so adding a tool is one schema +
  one dispatch entry:
  - `check_calendar_availability({duration_minutes?, within_days?})` → a LIST of
    real open slots (human phrasing + ISO), so the AI can offer choices.
  - `book_appointment({start_iso, duration_minutes?, title?})` → books it,
    returns confirmation + calendar link.
- `tool_specs(contact, channel)` returns ONLY the tools whose integration is
  actually connected (calendar connected → calendar tools; else empty). Empty →
  the loop never offers tools → today's behaviour. This is also what keeps the
  zero-credential sandbox working: nothing connected, nothing offered.

### 2. `integrations/actions.py` — a multi-slot finder
- Add `find_free_slots(...)` (plural): reuse the freeBusy call, but walk
  `_first_gap` repeatedly to return N (default 3–5) open slots, constrained to
  business hours (e.g. 09:00–18:00 local, configurable), skipping weekends by
  default. Keep it deterministic so tests can assert on it — same discipline as
  `_first_gap` today. Leave the existing single-slot `find_free_slot` intact (the
  flow builder still uses it).
- `book_appointment` dispatch reuses `create_calendar_event` (attach the contact
  as attendee, as it already does) AND records a `crm.Record` of type "booking"
  + a `RecordEvent`, so the appointment is tracked in the CRM and shows on the
  contact — this is what makes it a first-class action, not a fire-and-forget.

### 3. The loop in `generate_reply()`
- Build `tools = tools_mod.tool_specs(contact, channel)`. If non-empty AND the
  provider supports function-calling, pass `tools=` + `tool_choice="auto"`.
- Loop (bounded, **max ~4 tool rounds** — a hard stop against runaways): while the
  response has `tool_calls`, append the assistant tool-call message, execute each
  call via the registry, append each `{"role":"tool", "tool_call_id", "content"}`
  result, and re-call. Exit on a normal message → return its text (through the
  existing `<think>` scrub + `{{placeholder}}` render).
- **Provider capability**: only offer tools for OpenAI-compatible chat providers
  that support them. Be defensive: wrap the tools call so a provider that 400s on
  an unknown `tools` param is retried once WITHOUT tools (never let tool support
  break a reply). ilmu/reasoning models: keep today's `reasoning_effort=low`
  path; do not offer tools unless verified.
- Every tool execution is try/except → an error becomes a tool result the model
  can read ("calendar lookup failed, offer to have the team confirm"), never an
  exception out of `generate_reply`.

### 4. Guardrails (booking is a real side effect)
- **Operator kill-switch**: a Setting `ai_tool_use` (default on) — off → no tools
  offered, ever. Surface it on the AI & Providers or Routing Rules tab.
- **Confirm-before-book**: the `book_appointment` tool description instructs the
  model to confirm the specific slot with the customer in conversation before
  calling it; the system prompt gets one line to the same effect. (The tool still
  executes when called — we're steering, not hard-gating, to keep it usable.)
- **Audit + notify**: a successful booking writes an `AuditLog` (`ai.booking`)
  and a Notification, so operators see what the AI did.
- **Opt-out honoured**: never run tools for an opted-out / do-not-contact thread
  (the reply path already gates AI there; make sure tools sit inside that gate).

## Tests (`backend/tests/`)
- The tool loop with a mocked OpenAI client: first response returns a tool_call,
  second returns prose → assert the tool ran and the final text is returned.
- `find_free_slots` returns multiple in-hours slots; respects busy blocks and the
  business-hours/weekend constraint; deterministic.
- `book_appointment` creates the calendar event AND a `Record`+`RecordEvent`;
  writes the AuditLog.
- Sandbox: calendar not connected → `tool_specs` empty → `generate_reply`
  behaves exactly as the no-tools baseline (existing reply tests still pass).
- Provider fallback: a client that raises on `tools=` is retried without tools
  and still returns a reply.
- `ai_tool_use` off → no tools offered.
- The loop is bounded (a client that always returns tool_calls stops at the cap).

## Gates
```
cd backend; venv\Scripts\python manage.py check
cd backend; venv\Scripts\python manage.py makemigrations --check --dry-run   # a Record "booking" type may need a migration if choices are constrained — check
cd backend; venv\Scripts\python -m pytest tests/ calls/tests -q              # must stay >= 886, 0 failed
```

## Follow-ups (not this pass)
- Voice tool-calling (agent process).
- More tools: order status (Shopify), create Stripe payment link, send doc to
  Drive — all just new registry entries once the loop exists.
