"""External-system sync pipeline tests: intake auth/validation/idempotency,
normalizer upsert (Contact + Record + RecordEvent) + row isolation, tenant
separation, retry/dead-letter, retention and the health endpoint."""
import json
from datetime import timedelta
from decimal import Decimal

import pytest
from django.core.management import call_command
from django.test import Client
from django.utils import timezone

from crm.models import Contact, Record
from ingest.models import Tenant, DataSource, SyncBatch, RecordSyncStaging
from ingest.normalizer import process_batch, process_pending, MAX_ATTEMPTS
from ingest.security import hash_api_key

RAW_KEY = "sk_testkey_abc123"


def _record(external_id="ORD-1", phone="+60123450001", name="Michael Tan",
            amount="1598.00", status="open", rec_type="order", due_date=None,
            data=None):
    return {
        "external_id": external_id,
        "type": rec_type,
        "title": "3× Kopi O",
        "status": status,
        "amount": amount,
        "currency": "MYR",
        "due_date": due_date,
        "data": data or {"tracking_url": "https://track.example/1"},
        "contact": {"external_id": "CUST-1", "name": name, "phone": phone,
                    "email": "m@example.com", "tags": ["vip"],
                    "attributes": {"tier": "gold"}},
    }


@pytest.fixture
def source(db):
    tenant = Tenant.objects.create(name="Northwind", slug="northwind")
    return DataSource.objects.create(tenant=tenant, name="Northwind Shop",
                                     api_key_hash=hash_api_key(RAW_KEY), is_active=True)


def _post(client, body, key=RAW_KEY):
    headers = {"content_type": "application/json"}
    if key is not None:
        headers["HTTP_X_API_KEY"] = key
    return client.post("/api/v1/sync/records", data=json.dumps(body), **headers)


# ── intake auth + validation ────────────────────────────────────────────────

def test_intake_requires_api_key(source):
    r = _post(Client(), {"records": []}, key=None)
    assert r.status_code == 401


def test_intake_rejects_bad_api_key(source):
    r = _post(Client(), {"records": []}, key="sk_wrong")
    assert r.status_code == 401


def test_intake_rejects_non_list_records(source):
    r = _post(Client(), {"records": {}})
    assert r.status_code == 422


def test_intake_lands_staging(source):
    r = _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    assert r.status_code in (200, 202)
    batch = SyncBatch.objects.get()
    assert batch.rows_received == 1
    assert RecordSyncStaging.objects.filter(sync_batch=batch).count() == 1
    assert batch.tenant == source.tenant


def test_intake_idempotent_on_source_timestamp(source):
    body = {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]}
    _post(Client(), body)
    r2 = _post(Client(), body)
    assert r2.status_code == 200
    assert r2.json().get("duplicate") is True
    assert SyncBatch.objects.count() == 1  # not duplicated


# ── normalizer ───────────────────────────────────────────────────────────────

def test_normalizer_creates_contact_and_record(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z",
                     "records": [_record(due_date="2026-08-02")]})
    batch = SyncBatch.objects.get()
    summary = process_batch(batch)
    assert summary["upserted"] == 1 and summary["failed"] == 0

    c = Contact.objects.get(external_id="CUST-1")
    assert c.tenant == source.tenant
    assert c.name == "Michael Tan" and c.tags == ["vip"]
    assert c.attributes == {"tier": "gold"}

    r = Record.objects.get(external_id="ORD-1")
    assert r.contact == c and r.type == "order" and r.status == "open"
    assert r.amount == Decimal("1598.00") and r.currency == "MYR"
    assert r.due_date.isoformat() == "2026-08-02"
    assert r.data["tracking_url"] == "https://track.example/1"
    # creation books an opening timeline entry
    assert r.events.filter(kind="status").count() == 1


def test_normalizer_books_status_and_amount_events(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    process_batch(SyncBatch.objects.latest("id"))
    # Second sync: status moved on and the balance dropped.
    _post(Client(), {"sync_timestamp": "2026-07-09T07:00:00Z",
                     "records": [_record(status="packed", amount="1000.00")]})
    process_batch(SyncBatch.objects.latest("id"))

    r = Record.objects.get(external_id="ORD-1")
    assert r.status == "packed" and r.amount == Decimal("1000.00")
    status_events = list(r.events.filter(kind="status").order_by("id"))
    assert len(status_events) == 2
    assert status_events[-1].data == {"from": "open", "to": "packed"}
    pay = r.events.get(kind="payment")
    assert pay.amount == Decimal("598.00")


def test_normalizer_merges_contact_attributes(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    process_batch(SyncBatch.objects.latest("id"))
    row = _record()
    row["contact"]["attributes"] = {"seats": 3}
    _post(Client(), {"sync_timestamp": "2026-07-09T07:00:00Z", "records": [row]})
    process_batch(SyncBatch.objects.latest("id"))
    c = Contact.objects.get(external_id="CUST-1")
    assert c.attributes == {"tier": "gold", "seats": 3}  # merged, not clobbered


def test_normalizer_isolates_bad_rows(source):
    good = _record(external_id="OK-1", phone="+60123450010")
    bad = _record(external_id="", phone="+60123450011")  # no external_id -> row error
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [good, bad]})
    batch = SyncBatch.objects.get()
    summary = process_batch(batch)
    assert summary["upserted"] == 1 and summary["failed"] == 1
    assert batch.status == "partial"
    assert Record.objects.filter(external_id="OK-1").exists()
    assert batch.rows.exclude(process_error="").count() == 1


def test_normalizer_rejects_contact_without_phone(source):
    row = _record()
    row["contact"]["phone"] = ""
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [row]})
    summary = process_batch(SyncBatch.objects.get())
    assert summary["upserted"] == 0 and summary["failed"] == 1
    assert not Contact.objects.exists()


def test_unknown_record_type_falls_back_to_custom(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z",
                     "records": [_record(rec_type="spaceship")]})
    process_batch(SyncBatch.objects.get())
    assert Record.objects.get(external_id="ORD-1").type == "custom"


# ── tenant separation ────────────────────────────────────────────────────────

def test_two_tenants_stay_separate(source):
    other_tenant = Tenant.objects.create(name="Other Co", slug="other")
    other_key = "sk_other_key"
    DataSource.objects.create(tenant=other_tenant, name="Other Shop",
                              api_key_hash=hash_api_key(other_key), is_active=True)
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z",
                     "records": [_record(external_id="R1", phone="+60111111111")]})
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z",
                     "records": [_record(external_id="R1", phone="+60122222222")]},
          key=other_key)
    for b in SyncBatch.objects.all():
        process_batch(b)
    c1 = Contact.objects.get(phone="+60111111111")
    c2 = Contact.objects.get(phone="+60122222222")
    assert c1.tenant == source.tenant
    assert c2.tenant == other_tenant
    assert c1.pk != c2.pk
    assert Record.objects.filter(external_id="R1").count() == 2  # one per tenant


# ── rate limiting ────────────────────────────────────────────────────────────

def test_intake_rate_limited(source, settings):
    # Pre-fill the last hour up to the cap, then the next push is rejected.
    from ingest.views import MAX_SYNC_PER_HOUR
    for _ in range(MAX_SYNC_PER_HOUR):
        SyncBatch.objects.create(tenant=source.tenant, data_source=source, status="completed")
    r = _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    assert r.status_code == 429
    assert r["Retry-After"] == "3600"


# ── retry / dead-letter ──────────────────────────────────────────────────────

def test_bad_row_dead_letters_after_max_attempts(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z",
                     "records": [_record(external_id="")]})  # always errors
    batch = SyncBatch.objects.get()
    row = batch.rows.get()
    for _ in range(MAX_ATTEMPTS):
        process_batch(batch)
    row.refresh_from_db()
    assert row.attempts == MAX_ATTEMPTS
    assert row.dead_lettered is True
    assert row.process_error.startswith("[dead-letter]")
    # Dead-lettered rows are no longer picked up by the drain.
    assert process_pending()["upserted"] == 0


# ── retention ────────────────────────────────────────────────────────────────

def test_purge_staging_deletes_old_processed(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    batch = SyncBatch.objects.get()
    process_batch(batch)
    # Age the (now processed) staging row past the cutoff.
    old = timezone.now() - timedelta(days=100)
    RecordSyncStaging.objects.update(created_at=old)
    call_command("purge_staging", older_than_days=90, yes=True)
    assert RecordSyncStaging.objects.count() == 0


# ── health endpoint ──────────────────────────────────────────────────────────

def test_health_endpoint(source):
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": [_record()]})
    process_batch(SyncBatch.objects.get())
    r = Client().get("/api/v1/sync/health")  # bootstrap role = admin (no profiles)
    assert r.status_code == 200
    data = r.json()
    assert set(data) >= {"backlog_rows", "dead_lettered_rows", "batches_last_24h",
                         "last_synced_at", "sources_active"}
    assert data["backlog_rows"] == 0


# ── volume sanity ────────────────────────────────────────────────────────────

def test_larger_payload(source):
    rows = [_record(external_id=f"R{i}", phone=f"+6012{i:07d}") for i in range(200)]
    for i, row in enumerate(rows):
        row["contact"]["external_id"] = f"CUST-{i}"
    _post(Client(), {"sync_timestamp": "2026-07-08T07:00:00Z", "records": rows})
    summary = process_batch(SyncBatch.objects.get())
    assert summary["upserted"] == 200 and summary["failed"] == 0
    assert Contact.objects.count() == 200
    assert Record.objects.count() == 200
