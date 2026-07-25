# Bulk record sync

How to feed ChatBranch from an external system of record — an ERP, a shop
backend, a ticketing system, a partner API.

This is the **push** path: the source system POSTs to us on its own schedule. We
never pull. It complements the two other ways records get in — by hand from the
dashboard, and automatically from a connected Shopify/Stripe webhook (Settings →
Integrations).

Everything lives in the `ingest` Django app.

---

## 1. The contract

```
POST /api/v1/sync/records
Headers:  X-API-Key: <the key you issued>
          Content-Type: application/json
```

```json
{
  "sync_timestamp": "2026-08-01T07:00:00Z",
  "active_record_count": 1,
  "records": [
    {
      "external_id": "ORD-10233",
      "type": "order",
      "title": "3× Ceramic mug",
      "status": "packed",
      "amount": 48.90,
      "currency": "MYR",
      "due_date": "2026-08-05",
      "data": {"tracking_url": "https://track/…", "items": [ … ]},
      "contact": {
        "external_id": "CUST-77",
        "name": "Siti Rahman",
        "phone": "+60123456789",
        "email": "siti@example.com",
        "company_name": "Kedai Siti",
        "language": "ms",
        "tags": ["vip"],
        "attributes": {"tier": "gold"}
      }
    }
  ]
}
```

**Required:** `external_id` on the record, and `contact.name` + `contact.phone`.
`type` must be one of `order | shipment | invoice | booking | ticket | custom`.
`due_date` matters more than it looks — **every time-based flow trigger is an
offset from it**.

The authoritative version of this shape is the module docstring of
`backend/ingest/normalizer.py`. Read it before writing an exporter.

### Match order

| Object | Matched on |
|---|---|
| Contact | `(tenant, contact.external_id)` → then `(tenant, phone)` |
| Record | `(tenant, type, external_id)` |

A status change or an amount change books a `crm.RecordEvent`, so the timeline
shows what moved.

---

## 2. Onboard a source (issue the API key)

```bash
cd backend
python manage.py create_data_source \
  --tenant-slug acme --tenant-name "Acme Services" \
  --source-name "Acme Shopify"
```

Creates (or reuses) the tenant, creates the data source, and prints the key
**once**:

```
  API key (shown once — store it now):
    sk_9f2c…<48 hex>
```

Only `sha256(key)` is stored, in `DataSource.api_key_hash`, and inbound keys are
compared in constant time. Hand the raw key over through a secure channel — it
cannot be recovered.

Rotate (which invalidates the old key immediately):

```bash
python manage.py create_data_source --tenant-slug acme \
  --source-name "Acme Shopify" --rotate
```

The data source can be attached to a `Brand`, which is how synced records land
in the right brand scope.

---

## 3. Responses and idempotency

| Status | Meaning |
|---|---|
| `200` + `{"duplicate": true}` | this `sync_timestamp` already landed for this source — no-op |
| `200` (dev, inline normalization) | accepted **and** normalized; body includes `rows_upserted` |
| `202` (production, deferred) | accepted; raw rows landed, a worker will normalize |
| `401` | missing or invalid `X-API-Key` |
| `403` | tenant is suspended |
| `413` | payload bytes or record count over the limit |
| `422` | malformed JSON, or `records` is not an array |
| `429` | per-source hourly rate limit hit (`Retry-After: 3600`) |

**Idempotency is keyed on `(data_source, source_timestamp)`** by a partial unique
constraint, so a source retrying the identical push is safe. The response body
carries the batch id.

---

## 4. Normalization: inline vs deferred

- **Dev (default):** `INGEST_PROCESS_INLINE` defaults to `DEBUG`, so intake
  normalizes immediately and the response reports `rows_upserted`.
- **Production:** set `INGEST_PROCESS_INLINE = False`. Intake then only lands raw
  rows — fast and reliable — and returns `202`. Drain with a worker:

  ```bash
  python manage.py process_sync              # all pending batches
  python manage.py process_sync --source 3   # one data source
  python manage.py process_sync --limit 10   # cap the batches per run
  ```

  Schedule it just after the source's push window (cron, a platform scheduler,
  or Celery beat — Celery is **not** a dependency here; add it when you want
  concurrency).

**Rows are isolated.** Each row normalizes in its own savepoint: one bad row sets
`process_error` on its staging row, the batch finishes `partial`, and the good
rows still land.

---

## 5. Monitoring and recovery

- **Pipeline health** — `GET /api/v1/sync/health` (bearer, admin+) returns the
  unprocessed backlog, dead-letter count, last-synced time and the 24h batch
  outcome mix. This is the endpoint to alert on.
- **One batch** — `GET /api/v1/sync/batches/<id>` returns status, counts and
  `error_detail` (per-row failures with attempt counts).
- **Django admin** — *Ingest → Sync batches* and *Record sync staging* (raw
  payload read-only; `attempts` / `dead_lettered` visible).
- Every processed batch writes an `AuditLog` row; a `failed` or `partial` batch
  also notifies superadmins and admins in-app.

### Retries and dead-letters

A row that errors is retried on each subsequent drain up to
`INGEST_MAX_ROW_ATTEMPTS` (default 3), then **dead-lettered** — skipped by the
drain, with a `[dead-letter]` marker on its `process_error`. After fixing the
cause:

```bash
python manage.py replay_batch 12                # replay the whole batch from raw staging
python manage.py replay_batch 12 --failed-only  # only the errored rows; resets attempts
```

Replay reads the **immutable raw payload**, so it is always faithful to what the
source actually sent.

### Retention

```bash
python manage.py purge_staging --older-than-days 90 --yes
```

Deletes old *processed* staging rows. Unprocessed and dead-lettered rows are kept
unless you pass `--include-unprocessed`. Run it past your audit-retention window
— staging is append-only and will otherwise grow forever.

---

## 6. Settings knobs

Django settings (defaults in `backend/ingest/views.py` and `normalizer.py`;
override in `core/settings.py`):

| Setting | Default | Purpose |
|---|---|---|
| `INGEST_PROCESS_INLINE` | `DEBUG` | normalize during intake (dev) vs defer to a worker (prod) |
| `INGEST_MAX_SYNC_BYTES` | 32 MB | reject oversized payloads (413) |
| `INGEST_MAX_RECORDS` | 100 000 | reject oversized record counts (413) |
| `INGEST_MAX_SYNC_PER_HOUR` | 12 | per-source intake rate limit (429). `0` disables |
| `INGEST_MAX_ROW_ATTEMPTS` | 3 | failed-row retries before dead-lettering |

---

## 7. Local smoke test

```bash
cd backend
python manage.py create_data_source --tenant-slug demo --tenant-name Demo \
  --source-name "Demo ERP"
# copy the printed key into KEY below

curl -sS -X POST http://localhost:8000/api/v1/sync/records \
  -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"sync_timestamp":"2026-08-01T07:00:00Z","active_record_count":1,
       "records":[{"external_id":"ORD-1","type":"order","title":"Test order",
       "status":"packed","amount":48.90,"due_date":"2026-08-05",
       "contact":{"name":"Michael Tan","phone":"+60123456789"}}]}'
# dev (inline): the response shows rows_upserted: 1 — the contact + record now exist.
```

---

## 8. Before production

- **Tenant isolation is application-layer only.** The API key resolves the
  tenant on ingest and every scoped query filters by it, but SQLite has no
  row-level security and none was added for Postgres. On a Postgres move, RLS
  policies on `tenant_id` are the obvious defence in depth, and `raw_payload`
  should become real `JSONB`.
- **Raw payloads are stored verbatim and unencrypted.** If your source sends
  identity data you must not retain, filter it before it reaches us, or shorten
  the `purge_staging` window aggressively. There is no PII vault, no column
  encryption and no PII access log in this codebase.
- **Keys are hashed at rest here** — keep the raw key in the source system's own
  secret store, never in this repo.
