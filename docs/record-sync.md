# Bulk record sync

How to feed ChatBranch from an external system of record — an ERP, a shop
backend, a ticketing system.

This is the **push** path: your system POSTs to ChatBranch on its own schedule;
ChatBranch never pulls. It complements the other ways records get in — by hand
(`/contacts`), CSV import (`POST /api/contacts/import`), and automatically from a
connected Shopify/Stripe webhook (`/integrations`).

> **Changed with the C# backend.** The Django pipeline this document used to
> describe (`X-API-Key` data sources, `sync_timestamp` batches, an immutable
> staging table, normalizer savepoints, dead-lettering, `/api/v1/sync/batches`,
> the `manage.py` commands) does **not** exist any more. The C# endpoint below is
> a simple synchronous upsert. Implementation:
> `backend-csharp/src/ChatBranch.Api/Endpoints/ApiV1Endpoints.cs`.

---

## 1. The contract

```
POST /api/v1/sync/records
Authorization: Bearer <token>          (an admin/superadmin session, or)
X-Api-Key: cbk_<prefix>_<secret>       (an API key owned by an admin)
Content-Type: application/json
```

The route requires the **admin+** policy, so the caller (or the API key's
owner) must be an admin or superadmin. Create a key at **API keys & MCP**
(`/developers`) — a key is easier to rotate than a password and shows up in the
activity log as source `api`.

The body is **camelCase**:

```json
{
  "records": [
    {
      "externalId": "ORD-10233",
      "type": "order",
      "title": "3× Ceramic mug",
      "status": "packed",
      "amount": 48.90,
      "dueDate": "2026-08-05",
      "contact": {
        "phone": "+60123456789",
        "name": "Siti Rahman",
        "email": "siti@example.com"
      }
    }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `externalId` | yes | the record's id in your system (order #, tracking #, invoice #) |
| `type` | no | `order` \| `shipment` \| `invoice` \| `booking` \| `ticket` \| `custom` (default `custom`) |
| `title`, `status`, `amount`, `dueDate` | no | only fields you send are updated; `status` is your own vocabulary |
| `contact.phone` | **yes, in practice** | a record without a contact phone is skipped (records need an identity) |
| `contact.name`, `contact.email` | no | used only when the contact is created |

Response: `{"created": n, "updated": n, "total": n}` (`total` counts every row
you sent, including skipped ones).

## 2. Matching rules

- **Tenant** — the caller's tenant (from the session or the key's owner). Rows
  never cross tenants.
- **Contact** — matched on the exact `phone` string within the tenant; created
  when no contact has it (name falls back to the phone). Send E.164 consistently
  (`+60…`) or you will get twins. Note that the dashboard's own add-contact path
  refuses a phone that shares its last 8 digits with an existing contact; this
  endpoint matches exactly.
- **Record** — matched on `(type, externalId)` within the tenant. A match is
  updated, otherwise a record is created for the matched contact. An existing
  record is NOT moved to a different contact.

Re-sending the same payload is idempotent: the second call reports `updated`.

## 3. What this endpoint does not do (yet)

- It does not set `currency` (default `MYR`), `data` (tracking URL, links, line
  items) or custom fields — use `PATCH /api/v1/contacts/{id}` (`attributes`) and
  the contact/record UI for those, or extend `SyncRecord`.
- It does not book `RecordEvent` rows on status or amount changes (the
  Shopify/Stripe mappers do).
- It is synchronous and unbatched: one transaction per row, no staging, no
  replay. Keep payloads to a few hundred records and retry a failed call whole.
- It does not tag contacts or start flows. To react to a sync, either tag the
  contact (`PUT /api/contacts/{id}/tags`, `tags.manage`) so a "Tag added" flow
  fires, or start a "Manual / API" flow with
  `POST /api/flows/{flowId}/start {"contactIds": [...]}`.

## 4. Recommended shape for an exporter

1. Map your entities to `type`, your states to a status vocabulary, and your
   "when" to `dueDate` (see [vertical-guide.md](vertical-guide.md) §2.1).
2. Normalise phones to E.164 before sending.
3. Send changed records every few minutes (or nightly), in chunks.
4. Log the `{created, updated, total}` response; a `total` larger than
   `created + updated` means rows were skipped for lack of a contact phone.
