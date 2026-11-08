# Transactional send — `POST /api/messaging/notify`

**Status: specification. Not implemented.** This document is the contract for
one new endpoint; it is written to be implementable without further decisions.
The first consumer, ONEL, is already built against it (see
[Appendix A](#appendix-a--the-onel-side-already-shipped)) and falls back to the
existing thread endpoints until this ships.

---

## 1. Why

A business app that is not a chat app — an order system, a booking system, a
shop back office — needs to reach a customer with a message it originated: a
one-time login code, an order update, a payment reminder. ChatBranch owns the
number and the conversation, so those messages should go out through ChatBranch
and be filed in its inbox. Today such an app must:

1. `POST /api/messaging/threads` to open the conversation, then
2. `POST /api/messaging/threads/{channel}/{key}/send` to deliver,

and it must reinvent what ChatBranch already knows: that a WhatsApp free text
into a closed 24-hour window needs a template instead, that the cold-text guard
exists, that a retried request must not double-send. Two round trips, and the
app carries the rules.

`POST /api/messaging/notify` is **one call per message** that applies those
rules itself. It is not a new transport and not a new guard set: it is a
composition of `ThreadStart` + `ThreadStore` + `Routing` + `OutboundSender`
behind one request, with idempotency added.

**Non-goals.** It does not send media (an app that needs that uses
`…/send-media`), does not queue (see §7), does not do bulk (one recipient per
call), and is not a marketing endpoint — §5.4 is deliberately strict about that.

---

## 2. Route, auth, gates

```
POST /api/messaging/notify
```

* `.RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send")`
* The conversation is named in the BODY, so the entity scope is checked with
  `ThreadAccessFilter.InScope` — exactly as `POST /api/messaging/threads` and
  `POST /api/aicore/reply` already do. An existing conversation outside the
  caller's scope is refused (**not** 404 — see §4.1); a new one never links a
  contact or a number from outside that scope.
* Callers are normally **API keys** (`cbk_…`), which act as their owning user,
  so a scoped read-only key is refused by the capability filter already. A
  session may call it too (no reason to forbid it).
* Rate limiting: `RateLimiting.Classify` puts it in the signed-in `User` class
  (per session / per key). No new class.
* It writes, so `RequestAuditMiddleware` would cover it — but it writes its own
  domain row (§6), so no `[SkipRequestAudit]` is needed.

---

## 3. Request

```jsonc
{
  "channel": "whatsapp",            // required; MessagingEndpoints.ValidChannels
  "to": "60123456789",              // required; phone digits / email / chat id
  "text": "123456 is your code.",   // required unless `template` is given
  "purpose": "otp",                 // "otp" | "notification" (default)

  "idempotency_key": "onel-otp-60123456789-9f2c…",  // optional, ≤ 128 chars
  "name": "Aisha",                  // optional display name for a NEW conversation
  "entity": "kl-outlet",            // optional; ThreadStart hint (entity slug/id)
  "company": "acme",                // optional; ThreadStart hint
  "thread_id": 7,                   // optional; disambiguates an existing conversation
  "account_id": 3,                  // optional; pin the sending number
  "ai": "default",                  // "default" | "on" | "off" — a NEW conversation only

  "template": {                     // optional; WhatsApp only
    "name": "order_update",
    "language": "en",
    "variables": ["INV-0012", "Friday"],
    "header_variables": [],
    "button_variables": [],
    "media_url": ""
  },
  "template_fallback": true         // default true when `template` is present
}
```

Validation:

| Field | Rule |
| --- | --- |
| `channel` | must be in `ValidChannels`, else 422 `bad_channel` |
| `to` | non-empty; `PhoneMatch.Digits` for phone-keyed channels (whatsapp, sms); must contain `@` for email. Else 422 `bad_recipient` |
| `text` / `template` | at least one; `text` ≤ 4096 chars. Else 422 `nothing_to_send` |
| `purpose` | `otp` or `notification`; anything else 422 `bad_purpose` |
| `template` | `channel == "whatsapp"` only; on any other channel 422 `template_unsupported` |
| `idempotency_key` | ≤ 128 chars, printable ASCII |

`ai` decides the per-conversation chatbot tick for a **brand-new** conversation
only (an existing one is never changed): `default` keeps today's behaviour (the
inbox's `ai_default`, stamped by `ThreadStore`), `off` writes the tick through
`AiEligibility.Set(thread, false)`, `on` writes it true. Rationale: a shop that
only sends OTPs does not necessarily want its chatbot answering a customer who
replies "?" to a code, and a shop that sends order updates usually does.

---

## 4. Behaviour, in order

### 4.1 Never answer 404

This route must not return 404 for anything about the request's content.
Clients use 404 to detect "this ChatBranch does not have `/notify` yet" and
silently fall back to the older two-call path (ONEL does exactly that). So:

| Situation | Status, code |
| --- | --- |
| unknown entity / company hint | 422 `unknown_entity` |
| entity, company or existing conversation outside the caller's scope | 403 `out_of_scope` |
| several conversations match and none was named | 409 `company_required` (with the candidates, like `ThreadResolver.Refusal`) |

### 4.2 Idempotency

With an `idempotency_key`, look for an existing **outbound** message carrying
it. If one exists, return `200` with that message's current payload and
`"idempotent": true`, and send nothing.

Storage: a new nullable `crm_message.idempotency_key` column.

* add it to `Message` in `ChatBranch.Domain/Channels.cs` and to the
  `AppDbContext` mapping, with a **partial unique index** on non-empty values
  (`"idempotency_key" <> ''`, the portable `"col"` quoting — never `[col]`);
* add the `ADD COLUMN` + `CREATE INDEX IF NOT EXISTS` to `Seed.UpgradeSchema`
  (append-only, idempotent) — Postgres picks it up from the model via
  `PostgresSchema.Sync`;
* document it in `docs/DATABASE_SCHEMA.md`.

The key is global, not per-caller: callers are expected to prefix theirs
(`onel-otp-…`). Two different apps colliding on a key is a caller bug and the
second one legitimately reads back the first one's message.

Write the key on the row in the SAME save as the message, so a crash between
sending and storing cannot produce a key that resolves to nothing. If the unique
index rejects the insert (two concurrent identical requests), re-read the
existing row and answer as above — the second caller must not send.

### 4.3 Find or open the conversation

Identical to `POST /api/messaging/threads`, and it should literally share the
code — lift that handler's body into a helper both call rather than copying it:

1. `ThreadResolver.ResolveAsync(db, sc, me, channel, [to], thread_id, company)`
   → the caller's own conversation with that address, or the 409 above.
2. No existing one → `ThreadStart.PlaceAsync(...)` for the company/entity, then
   `store.GetOrCreateInCompany(channel, to, company, name, contactId, entityHint)`.
3. Contact matching exactly as there: email exact, phone by last-8
   (`PhoneMatch`), only a contact the caller may see, `ContactCompanies.Pick`
   for the entity, backfill + `sc.SyncThreadEntity` on an unlinked thread.

A conversation opened by `/notify` is a normal conversation: it shows in the
inbox, in the contact's timeline, in analytics.

### 4.4 Opt-out — always honoured, no override

`thread.OptedOut` → **409 `opted_out`**. There is no `force` on this route.

An app cannot judge consent, and STOP is per-channel consent (see `Channels/OptOut.cs`).
An operator can still make an audited manual exception from the inbox, which is
a person's decision, on the record. A caller that needs the customer anyway —
including for a login code — must use another channel of its own; the refusal
says so plainly:

```json
{"detail": "This contact sent STOP on whatsapp, so ChatBranch will not message them there. Use another channel, or have them send START.",
 "code": "opted_out"}
```

### 4.5 Routing

`RouteNeed.For(channel, template: sending a template)`, then
`new Routing(db, sc).ForThread(thread, need, preferredAccountId: account_id, apply: true)`.
`!route.Ok` → **409 `no_account`** with `route.Error` as the detail (that text is
already written for humans). This is also what answers "no WhatsApp number is
connected at all".

`account_id` is a preference, not a command: if that number is not eligible
(wrong company, banned, wrong provider for a template) routing picks a usable
one, exactly as everywhere else.

### 4.6 The cold-text guard

| `purpose` | Guard |
| --- | --- |
| `otp` | **skipped.** The customer asked for the code seconds ago; it is the most customer-initiated message there is, and blocking it breaks a login. |
| `notification` | the configured `cold_text_guard` applies (`block` / `warn` / `allow`, read with `ScopingService.SettingValue` for the thread's entity), same condition as `WhatsAppThreadsEndpoints`: a conversation-role number with zero inbound messages on the thread. `block` → 409 `cold_text`, whose detail names `template` as the way in. `warn` → send, and append the existing `ColdTextWarnNote` system message. |

That split is the whole point of `purpose`. Without it, either logins break or
a shop can push cold bulk text through an API key and get its number banned.

### 4.7 WhatsApp's 24-hour window

Only for a **live official** number (the existing `WindowOpen(db, thread, account)`
check; Baileys and sandbox read as open):

* window open → send `text`;
* window closed **and** `template` given and `template_fallback` is true → send
  the TEMPLATE (the one way into a closed window; the existing
  `send-template` path, `WhatsAppTemplateCatalog` render, stored rendered body),
  and report `"sent_as": "template"`;
* window closed, no template → **409 `window_closed`**, detail as today
  ("WhatsApp 24h window closed — use a template.").

A template given while the window is OPEN: send the free `text` if there is one
(cheaper, no approval surprises), else the template. `template_fallback: false`
means "never silently switch": refuse with `window_closed` instead.

### 4.8 Send

`OutboundSender` is the only seam, as everywhere:

* text → `sender.Send(thread, text, MessageSource.System, account.Id)`
* template → the same call the `send-template` endpoint makes

Use **`MessageSource.System`**, not `Agent`:

* it is not a person typing, so it must NOT run
  `AiControlsEndpoints.HumanReplied` — an OTP is not a reply and must never
  close an open handover or stand the AI down;
* the inbox already renders system messages distinctly, which is the truth here.

Do not touch `thread.Status`: a transactional send does not resolve or reopen a
conversation.

### 4.9 Answer

```jsonc
{
  "message": {
    "id": 91,
    "external_id": "wamid.HBgL…",
    "status": "sent",          // sent | queued | sandbox | failed
    "error": "",               // the provider's own words when failed
    "source": "system",
    "created_at": "2026-11-08T06:12:03Z"
  },
  "thread": {
    "thread_id": 7,
    "channel": "whatsapp",
    "key": "60123456789",
    "company": {"id": 2, "name": "Acme"},
    "url": "/chats?thread=whatsapp:60123456789"
  },
  "account": {"id": 3, "name": "Shop number", "provider": "baileys"},
  "sent_as": "text",           // text | template
  "idempotent": false
}
```

**`status` must be the truth.** With no live account `OutboundSender` stores the
row as `sandbox` — nothing left the building. Answer 200 with
`"status": "sandbox"` (the message IS filed in the inbox, so this is not an
error at the HTTP level), and let the caller decide: ONEL treats `sandbox` and
`failed` as a failed send and refuses to tell a customer their code is on the
way. Never relabel a sandbox or failed row as `sent`.

### 4.10 Error summary

| HTTP | `code` | When |
| --- | --- | --- |
| 422 | `bad_channel`, `bad_recipient`, `nothing_to_send`, `bad_purpose`, `template_unsupported`, `unknown_entity` | request validation |
| 403 | `out_of_scope` | the entity / company / conversation is outside the caller's scope |
| 403 | — | the capability filter (a read-only key) refuses before the handler |
| 409 | `company_required` | several conversations match; name one |
| 409 | `opted_out` | STOP on this channel |
| 409 | `no_account` | nothing eligible to send from (detail = `route.Error`) |
| 409 | `cold_text` | `purpose: "notification"` into a never-inbound conversation under `cold_text_guard=block` |
| 409 | `window_closed` | WhatsApp 24h window shut and no usable template |
| 429 | `rate_limited` | the existing limiter |
| 200 | — | stored; read `message.status` for what actually happened |

Every body carries `detail` (human text) and, where listed, `code`.

---

## 5. What this endpoint must NOT become

1. **Not a bulk sender.** One recipient per call. Bulk belongs in the outbound
   queue / journeys, where pacing, daily caps and quiet hours live.
2. **Not an opt-out override.** §4.4.
3. **Not a cold-marketing path.** §4.6 keeps the ban guard for
   `purpose: "notification"`.
4. **Not a second transport.** If `OutboundSender` cannot do it, `/notify` does
   not do it either.

---

## 6. Audit

One `AuditLog` row per accepted call (`AuditTrail.Add`), action
**`message.notify`**, object `"{channel}:{key}"`, detail
`"{purpose}: {text capped at 200}"`, `Source` = the request's actor (`api` for a
key — the ambient `AuditContext` stamps it). An idempotent replay writes **no**
new row; a refusal writes none either (the request audit middleware is not
reached for a 4xx by design elsewhere, keep that behaviour).

---

## 7. Deliberately out of scope (possible follow-ups)

* **`queue: true`** — go through `OutboundJob` so account pacing, the daily cap
  and quiet hours apply. Right for bulk-ish order notifications, wrong for an
  OTP (a code must arrive now, not after quiet hours). Add it when something
  asks for it; the queue already has the semantics.
* **Media.** `…/send-media` exists; adding media here means multipart on a JSON
  route for no gain yet.
* **Delivery callbacks.** A caller that wants "was it read?" can poll
  `GET /api/messaging/threads/{channel}/{key}` or read the message row; a
  webhook out of ChatBranch is a bigger feature.

---

## 8. Work items

1. `Endpoints/MessagingEndpoints.cs` (or a new `MessagingNotifyEndpoints.cs`):
   the route, plus lifting the find-or-open block out of
   `POST /api/messaging/threads` into a shared helper so the two cannot drift.
2. `crm_message.idempotency_key`: entity + `AppDbContext` mapping + partial
   unique index + `Seed.UpgradeSchema` + `docs/DATABASE_SCHEMA.md`.
3. MCP: `McpCoverageTests` fails on any signed-in `/api` route that is neither a
   tool nor a documented exclusion. Add a tool in
   `Tools/ToolCatalog.Conversations.cs` next to `conversations_start`:

   ```csharp
   yield return T(g, "conversations_notify", "Send a transactional message",
       "Send one app-originated message (a login code, an order update) to an address on a channel: " +
       "finds or opens the conversation, picks a number, and falls back to a template when WhatsApp's " +
       "24-hour window is shut. Opt-out always applies; purpose=otp skips the cold-text guard.",
       "POST", "/api/messaging/notify", send,
       args: new[] { /* channel, to, text, purpose, template…, idempotency_key */ });
   ```

   If you would rather an AI caller could not originate cold outbound, make it an
   `McpExclusions` entry with that reason instead — but pick one, the test
   insists.
4. Docs in the same change: `docs/API_CONVENTIONS.md` (the REST surface),
   `docs/mcp.md` (regenerate with `UPDATE_MCP_DOCS=1`), `CLAUDE.md` (the unified
   inbox section), `README.md`.
5. Tests — a new `NotifyTests` class. At minimum:

   | Case | Expectation |
   | --- | --- |
   | first send to an unknown number | conversation created in the caller's company, message stored, `status` reflects the account |
   | second send to the same number | the SAME conversation, no duplicate |
   | same `idempotency_key` twice | one message, second answer `idempotent: true` |
   | two concurrent identical keys | one message (the unique index path) |
   | opted-out conversation | 409 `opted_out`, nothing stored |
   | `purpose: "otp"` into a never-inbound Baileys conversation, `cold_text_guard=block` | delivered |
   | `purpose: "notification"`, same setup | 409 `cold_text` |
   | official number, window closed, `template` given | template sent, `sent_as: "template"` |
   | official number, window closed, no template | 409 `window_closed` |
   | no WhatsApp account configured | 200, `status: "sandbox"` (never `sent`) |
   | no eligible account (all banned) | 409 `no_account` |
   | read-only API key | 403 |
   | key whose user cannot see the entity | 403 `out_of_scope`, never 404 |
   | unknown entity slug | 422 `unknown_entity`, never 404 |
   | `ai: "off"` on a new conversation | the per-conversation tick is off |
   | the send | writes `message.notify`, does NOT close an open handover |

   Fakes exist: `TransportFactory` / `TransportFakes.cs` for Graph/SMTP/Telegram,
   `MoreChannelsFakes.cs` for the newer channels.

---

## Appendix A — the ONEL side (already shipped)

ONEL (`liewxen/ONEL`, `backend/messaging/chatbranch.py`) sends:

```http
POST /api/messaging/notify
Authorization: Bearer cbk_…
Content-Type: application/json

{"channel": "whatsapp", "to": "60123456789", "text": "123456 is your …",
 "purpose": "otp", "idempotency_key": "onel-otp-60123456789-<code hash prefix>"}
```

and reads `message.status` + `message.external_id` from the answer. It treats
`sandbox` and `failed` as failures (an OTP must not look delivered), reports a
401/403 as "ChatBranch refused the API key", and shows any 4xx `detail`
verbatim to the ONEL admin.

Until this endpoint exists, a **404** makes ONEL fall back to
`POST /api/messaging/threads` + `POST /api/messaging/threads/{channel}/{key}/send`
with `force: true`. Two consequences for the implementer:

* **404 must mean "no such route"** on this path (§4.1), or ONEL will quietly
  take the fallback and the new logic will never run;
* once `/notify` answers, the fallback goes cold by itself — nothing to
  coordinate, no ONEL deploy needed.

ONEL's side of the integration, including the credentials and the reverse link
(ChatBranch reading ONEL's orders through ONEL's MCP server), is documented in
that repo at `docs/chatbranch-integration.md`.
