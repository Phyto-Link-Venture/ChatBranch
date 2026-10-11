# Incident report: one customer's chat crossed from Phytolink venture into King Kong music

**Date:** 2026-10-11 · **Environment:** production (chatbranch.phytolink-venture.com, PostgreSQL)
**Severity:** high: data from one company was shown to another company's agent and fed to that company's AI.
**Status:** found, not fixed. Production data has NOT been changed.

## Summary

A customer (contact #1, phone `60113319****`) is a Phytolink venture contact with a long
WhatsApp chat on Phytolink's number. At 16:19 UTC he sent "Hi" to **King Kong music's**
new WhatsApp number. ChatBranch does not open a second conversation for King Kong. It
**moves Phytolink's whole existing conversation** (117 messages, invoices included) into King
Kong music. As a result:

1. **King Kong's agent (`kingkong`) can read Phytolink's history.** That covers the full merged
   conversation, the contact's calls and their bookings (all returned 200).
2. **King Kong's AI answered using Phytolink's context.** It referred to Phytolink's invoices
   (INV-0001) in its first reply to King Kong's customer. The send failed only because King
   Kong's WhatsApp had just disconnected (message #139, `qr_expired`), so the customer never
   received it.
3. **Phytolink loses the conversation.** It now sits in King Kong's entity, so
   Phytolink's agent (`liewxen`, entity 4) no longer sees it in their inbox.
4. **The contact is now claimed by two companies.** It's linked to Phytolink
   (`crm_contactentity` → entity 4) but becomes visible to King Kong only through the moved
   chat. This is probably the "contact crash" (clash) that was reported.

## Evidence (production, read-only)

Entities: `4 = Phytolink venture`, `6 = King Kong music` (created 2026-10-11 16:12).

| Thing | Value |
|---|---|
| Contact #1 | linked to entity **4** only (`crm_contactentity` id 2) |
| Account 3 | WhatsApp (QR, Baileys), entity **4**, connected as `+601125412…` |
| Account 5 | WhatsApp (QR, Baileys), entity **6**, created by `kingkong` 16:15, connected as `+60149720…` |
| Thread #1 (`whatsapp`, `60113319****`) | now `account_id = 5`, `entity_id = 6`, `contact_id = 1`, AI on |
| Messages in thread #1 | 117 on account 3 (2026-10-10 16:10 → 2026-10-11 12:06), then #137, #138 (in) and #139 (out, failed) on account 5 |

Timeline (from `crm_auditlog`):

- 16:12:00: superadmin creates entity `king-kong-music`; 16:12:33 creates agent `kingkong`
- 16:15:18: `kingkong` adds WhatsApp (QR) account 5 and pairs it
- **16:19:25: inbound "Hi" from contact #1 on account 5. Thread #1 is re-attributed to account 5 / entity 6**
- 16:19:32: `kingkong`'s browser loads `/api/messaging/contacts/1/conversation`, `/api/contacts/1/calls`,
  `/api/bookings?contact_id=1`, all **200**
- 16:19:36: `kingkong` turns AI on for the thread
- 16:20:45: `ai.reply` "Hi Mr Liew, Mary here… flag INV-0001…" (Phytolink invoice data)
- 16:21:00: send fails, `gateway 502: account 5 is not connected (state: qr_expired)`

The API has no 5xx and no exceptions for this, and nginx shows no 4xx/5xx for the contact
calls. If "contact crash" means a page actually crashed in the browser, see **Open question**.

## Root cause

### 1. A chat is unique per (channel, phone) for the whole tenant, not per company

- `backend-csharp/src/ChatBranch.Data/AppDbContext.cs:284`: unique index
  `uniq_thread_channel_key` on `(channel, key)`.
- `backend-csharp/src/ChatBranch.Data/Stores/ThreadStore.cs:45` `GetOrCreateThread`: looks up
  by `channel + key` only, so the same phone number always lands in the same thread, whichever
  company's number it wrote to.

### 2. `Reattribute()` moves a chat across companies

`backend-csharp/src/ChatBranch.Data/Channels/InboundProcessor.cs:154`, called from
`ProcessInbound` (line ~70):

```csharp
if (accountId is not int aid || thread.AccountId is null || thread.AccountId == aid) return;
...
thread.AccountId = aid;
_scoping.SyncThreadEntity(thread, save: false);   // entity follows the new account
```

It was written for "a conversation moving between **the business and personal numbers**" of
one business. It never checks that the new account is in the same entity or company, so any
inbound on another company's number takes over the chat.

### 3. Contact visibility follows the moved chat

`backend-csharp/src/ChatBranch.Api/Endpoints/ContactsEndpoints.cs` `ScopeContacts`: a contact
is in scope when it is *linked* to the caller's entity **or has a conversation there**. Once
the thread moved, contact #1 (and everything hung off it: `/api/messaging/contacts/{id}/conversation`
in `MessagingEndpoints.cs:409`, calls, bookings) became visible to King Kong.

### 4. The AI's customer context is not company-scoped

The AI composes from the thread's history and the contact's shared notes and summary
(`crm_contact` summary: "Mr Liew wants a conduit-for-PV-solar invoice…"). Both are
Phytolink's, and nothing limits them to the entity that is answering.

## What to fix

Do it on a branch, test on the **demo** stack, and do not touch the production DB until the
data repair step.

### Fix 1: one conversation per customer **per company** (the real fix)
- A chat's identity becomes `(channel, key, company)`, where company is the receiving
  account's top-level entity (use the existing company helpers in
  `ChatBranch.Api/Endpoints/ShellScope.cs` / `ScopingService`; a thread with no account or
  entity keeps today's behaviour).
- Replace `uniq_thread_channel_key` with a unique index that includes the company. Add a
  stored `company_entity_id` column if needed, filled on create and backfilled. It must work
  on both SQLite (`Seed.UpgradeSchema`) and PostgreSQL (`PostgresSchema.cs`).
- `GetOrCreateThread` takes the receiving account and finds or creates the thread **for
  that company**. A new company → a **new** thread. The contact can still be matched and
  linked, but only if the contact is linked to that company (`MatchContact` already gets
  `ReceivingEntity`, so check it really filters by company).
- There are about **37 `(channel, key)` thread lookups in 24 files**
  (`grep -rnE 't\.Channel == [a-zA-Z.]+ && t\.Key ==' backend-csharp/src`). Each one must
  resolve the thread within the caller's or account's company. URLs like
  `/api/whatsapp/threads/{key}` and `/chats?thread=channel:key`, and the MCP tools that address
  conversations by `channel + key`, need the company (or the thread id) to disambiguate.
  Keep old links working when there is only one match.

### Fix 2: `Reattribute()` only inside one company
Even before Fix 1 lands, this guard alone stops the leak:
only re-attribute when the new account's company equals the thread's company. Otherwise do
nothing here and let Fix 1 create the other company's own thread. As a stop-gap without
Fix 1, log a warning and keep the thread where it is.

### Fix 3: scope the contact's merged views
`/api/messaging/contacts/{id}/conversation`, `/api/contacts/{id}/calls`, `/api/bookings?contact_id=`
and the contact timeline must only include threads, calls and bookings in the **caller's**
entities (superadmin and org-wide admin unchanged). "Has a conversation there" may make a
contact visible, but must never reveal another company's messages.

### Fix 4: company-scoped AI context
When the AI answers a thread, use only that thread's messages plus contact data the
answering company is allowed to see. Contact-level summary and notes written from another
company's chats must not be included. Check `AiComposer`, `AssistantService`,
`SummaryService` and the AI tools that read invoices, bookings and contact data.

### Tests (backend-csharp/tests)
1. Contact linked to company A writes to company B's number → B gets a **new** thread; A's
   thread keeps its account, entity and all messages.
2. Same customer writes to two numbers of the **same** company → it still re-attributes
   (existing behaviour kept).
3. B's agent: `/api/messaging/contacts/{id}/conversation`, calls and bookings contain none of A's data.
4. B's AI reply prompt contains none of A's messages or contact notes.
5. Schema upgrade on an existing DB (SQLite and Postgres) keeps all threads, with the new unique key in place.

## Data repair (production, AFTER the code fix is deployed)

Thread #1 has to be split back. Write it as a one-off script, run it on a copy first, and take a
`pg_dump` immediately before:

1. Create a new thread for company 6 (King Kong): channel `whatsapp`, same key,
   `account_id = 5`, `entity_id = 6`, **no contact** (contact #1 is not linked to King Kong),
   AI off.
2. Move messages **#137, #138, #139** to it (anything else with `account_id = 5` in thread #1).
3. Restore thread #1 to `account_id = 3`, `entity_id = 4` (Phytolink).
4. Check any AI drafts, queue jobs (#84, #85), summaries or notes created for thread #1 after
   2026-10-11 16:19 and move or remove them the same way.
5. Verify: `liewxen` sees thread #1 with 117 messages; `kingkong` sees only the new thread
   (3 messages) and cannot open contact #1.

## Open question

"Contact crash": the server shows no errors (no 5xx, no exceptions, no 403/404 on the
contact calls). If a page actually crashed in the browser, note the page and the clicks
(e.g. Chats → this chat → contact panel, as `kingkong`) and reproduce it on demo with the
browser console open. Otherwise treat it as the contact **clash** described above.

## Side note

King Kong's WhatsApp (account 5) is currently **disconnected** (QR expired). It needs to be paired
again in Channels.
