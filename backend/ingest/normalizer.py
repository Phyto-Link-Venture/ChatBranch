"""Normalizer: raw staging rows -> crm.Contact + crm.Record (+ RecordEvent).

Runs inline in dev or via `manage.py process_sync` (worker/cron). Each row is
processed in its own savepoint so one bad row never poisons the batch; failures
are recorded on the staging row (`process_error`) and surfaced on the SyncBatch.

Payload shape (one object per entry in the push's `records` array):

    {
      "external_id": "ORD-10233",          # required — the source system's id
      "type": "order",                     # order|shipment|invoice|booking|ticket|custom
      "title": "3× Kopi O",
      "status": "packed",                  # vertical-defined
      "amount": 48.90,
      "currency": "MYR",
      "due_date": "2026-08-02",            # delivery ETA / due date / appointment
      "data": { ... },                     # anything else the vertical needs
      "contact": {                         # required — who the record belongs to
        "external_id": "CUST-77",
        "name": "Siti Rahman",             # required
        "phone": "+60123456789",           # required
        "email": "siti@example.com",
        "company_name": "Kedai Siti",
        "language": "ms",
        "tags": ["vip"],
        "attributes": {"tier": "gold"}
      }
    }

Contact match order: (tenant, contact.external_id) -> (tenant, phone).
Record match order: (tenant, type, external_id).
A status change or an amount change books a RecordEvent so the timeline shows
what moved — the generic replacement for a domain-specific ledger.
"""
from __future__ import annotations

import logging
from decimal import Decimal, InvalidOperation

from django.conf import settings
from django.db import transaction
from django.utils import timezone
from django.utils.dateparse import parse_date

from crm.auth import notify
from crm.models import AuditLog, Contact, Record, RecordEvent
from .models import SyncBatch

log = logging.getLogger("ingest")

# A staging row that errors this many times is dead-lettered (skipped by the
# drain) until a human resets it with `replay_batch`.
MAX_ATTEMPTS = getattr(settings, "INGEST_MAX_ROW_ATTEMPTS", 3)

RECORD_TYPES = {t for t, _ in Record.TYPE_CHOICES}


def _dec(value) -> Decimal | None:
    if value is None or value == "":
        return None
    try:
        return Decimal(str(value))
    except (InvalidOperation, TypeError, ValueError):
        raise ValueError(f"not a number: {value!r}")


def _upsert_contact(tenant, data_source, identity: dict) -> Contact:
    name = str(identity.get("name", "")).strip()
    phone = str(identity.get("phone", "")).strip()
    if not name or not phone:
        raise ValueError("contact is missing name or phone")

    ext_id = str(identity.get("external_id", "")).strip()[:64]
    email = str(identity.get("email", "")).strip()
    company = str(identity.get("company_name", "")).strip()
    language = str(identity.get("language", "")).strip()
    tags = identity.get("tags")
    attributes = identity.get("attributes")

    contact = None
    if ext_id:
        contact = Contact.objects.filter(tenant=tenant, external_id=ext_id).first()
    if contact is None:
        contact = Contact.objects.filter(tenant=tenant, phone=phone).first()

    now = timezone.now()
    if contact is None:
        return Contact.objects.create(
            tenant=tenant, external_id=ext_id, name=name, phone=phone, email=email,
            company_name=company, language=language or "en",
            tags=list(tags) if isinstance(tags, list) else [],
            attributes=dict(attributes) if isinstance(attributes, dict) else {},
            status="active", last_synced_at=now,
        )

    contact.external_id = ext_id or contact.external_id
    contact.name = name or contact.name
    contact.email = email or contact.email
    contact.company_name = company or contact.company_name
    if language:
        contact.language = language
    if isinstance(tags, list):
        contact.tags = list(tags)
    if isinstance(attributes, dict):
        # Merge, don't clobber — a partial push must not wipe known attributes.
        merged = dict(contact.attributes or {})
        merged.update(attributes)
        contact.attributes = merged
    contact.last_synced_at = now
    contact.save()
    return contact


def _normalize_row(batch: SyncBatch, row) -> None:
    p = row.raw_payload or {}
    identity = p.get("contact") or {}

    ext_id = str(p.get("external_id", "")).strip()
    if not ext_id:
        raise ValueError("missing external_id")

    rec_type = str(p.get("type", "custom")).strip() or "custom"
    if rec_type not in RECORD_TYPES:
        rec_type = "custom"

    amount = _dec(p.get("amount"))
    status = str(p.get("status", "") or "open").strip()
    title = str(p.get("title", "") or "").strip()[:190]
    currency = (str(p.get("currency", "") or "MYR").strip() or "MYR")[:8]
    due_raw = p.get("due_date")
    due_date = parse_date(str(due_raw)) if due_raw else None
    data = p.get("data")
    if not isinstance(data, dict):
        data = {}

    tenant = batch.tenant
    contact = _upsert_contact(tenant, batch.data_source, identity)

    now = timezone.now()
    record = Record.objects.filter(tenant=tenant, type=rec_type, external_id=ext_id).first()
    if record is None:
        record = Record.objects.create(
            contact=contact, tenant=tenant, type=rec_type, external_id=ext_id,
            title=title, status=status, amount=amount, currency=currency,
            due_date=due_date, data=data,
        )
        RecordEvent.objects.create(record=record, kind="status", at=now,
                                   amount=amount, data={"to": status, "created": True})
        return

    prev_status, prev_amount = record.status, record.amount
    record.contact = contact
    record.title = title or record.title
    record.status = status or record.status
    record.amount = amount if amount is not None else record.amount
    record.currency = currency
    record.due_date = due_date or record.due_date
    if data:
        merged = dict(record.data or {})
        merged.update(data)
        record.data = merged
    record.save()

    if status and status != prev_status:
        RecordEvent.objects.create(record=record, kind="status", at=now,
                                   data={"from": prev_status, "to": status})
    if amount is not None and prev_amount is not None and amount != prev_amount:
        RecordEvent.objects.create(
            record=record, kind="payment" if amount < prev_amount else "note",
            at=now, amount=abs(prev_amount - amount),
            data={"from": str(prev_amount), "to": str(amount)},
        )


def process_batch(batch: SyncBatch) -> dict:
    """Normalize all unprocessed rows of a batch. Row failures are isolated."""
    now = timezone.now()
    batch.status = "processing"
    batch.started_at = batch.started_at or now
    batch.save(update_fields=["status", "started_at"])

    upserted, failed, dead, errors = 0, 0, 0, []
    # Skip rows already dead-lettered or over the attempt cap.
    pending = batch.rows.filter(processed=False, dead_lettered=False, attempts__lt=MAX_ATTEMPTS)
    for row in pending.iterator():
        try:
            with transaction.atomic():
                _normalize_row(batch, row)
                row.processed = True
                row.processed_at = timezone.now()
                row.process_error = ""
                row.save(update_fields=["processed", "processed_at", "process_error"])
            upserted += 1
        except Exception as exc:  # isolate: one bad row never fails the batch
            row.attempts += 1
            row.dead_lettered = row.attempts >= MAX_ATTEMPTS
            prefix = "[dead-letter] " if row.dead_lettered else ""
            row.process_error = (prefix + str(exc))[:2000]
            row.processed = False
            row.save(update_fields=["attempts", "dead_lettered", "process_error"])
            failed += 1
            if row.dead_lettered:
                dead += 1
            if len(errors) < 50:
                errors.append({"staging_id": row.pk, "external_id": row.record_external_id,
                               "attempts": row.attempts, "dead_lettered": row.dead_lettered,
                               "error": str(exc)[:300]})

    batch.rows_upserted = (batch.rows_upserted or 0) + upserted
    batch.rows_failed = failed
    batch.error_detail = {"rows": errors} if errors else {}
    batch.finished_at = timezone.now()
    batch.status = ("failed" if upserted == 0 and failed > 0
                    else "partial" if failed else "completed")
    batch.save(update_fields=["rows_upserted", "rows_failed", "error_detail",
                              "finished_at", "status"])

    AuditLog.objects.create(
        actor="system", action="sync.batch.processed",
        target=f"batch#{batch.pk} {batch.data_source.name}",
        detail=f"upserted={upserted} failed={failed} dead={dead} status={batch.status}",
    )
    log.info("batch processed id=%s status=%s upserted=%s failed=%s dead=%s",
             batch.pk, batch.status, upserted, failed, dead)

    # Alert operators on anything less than a clean run (in-app notification;
    # swap for email/Slack once a channel exists).
    if batch.status in ("failed", "partial"):
        notify(("superadmin", "admin"), "system",
               f"Sync batch {batch.status}: {batch.data_source.name}",
               f"Batch #{batch.pk}: {upserted} upserted, {failed} failed, {dead} dead-lettered.",
               "/admin/ingest/syncbatch/")

    return {"upserted": upserted, "failed": failed, "dead": dead, "status": batch.status}


def process_pending(data_source=None, limit_batches: int | None = None) -> dict:
    """Drain all batches that still have unprocessed rows (worker/cron entry)."""
    qs = (SyncBatch.objects
          .filter(rows__processed=False, rows__dead_lettered=False,
                  rows__attempts__lt=MAX_ATTEMPTS)
          .distinct().order_by("created_at"))
    if data_source is not None:
        qs = qs.filter(data_source=data_source)
    if limit_batches:
        qs = qs[:limit_batches]
    totals = {"batches": 0, "upserted": 0, "failed": 0}
    for batch in qs:
        summary = process_batch(batch)
        totals["batches"] += 1
        totals["upserted"] += summary["upserted"]
        totals["failed"] += summary["failed"]
    return totals
