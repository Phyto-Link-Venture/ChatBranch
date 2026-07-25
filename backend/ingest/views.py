"""External-system sync HTTP API (under /api/v1/sync/).

  POST /records        intake a batch of records (x-api-key)  -> 202 + batch id
  GET  /batches/<id>   batch status (dashboard admin auth)

Intake is deliberately thin: authenticate, validate the envelope, and land raw
rows in staging fast. Normalization runs either inline (dev default) or via the
`process_sync` worker/command, so a slow transform never blocks the caller.
See ingest/normalizer.py for the per-record payload shape.
"""
from __future__ import annotations

import json
import logging
from datetime import timedelta

from django.conf import settings
from django.http import HttpResponseNotAllowed, JsonResponse
from django.utils import timezone
from django.utils.dateparse import parse_datetime
from django.views.decorators.csrf import csrf_exempt

from django.db.models import Count

from crm.auth import role_from_request
from .models import RecordSyncStaging, SyncBatch
from .security import source_from_request

log = logging.getLogger("ingest")

# Guardrails (overridable in settings).
MAX_SYNC_BYTES = getattr(settings, "INGEST_MAX_SYNC_BYTES", 32 * 1024 * 1024)  # 32 MB
MAX_RECORDS = getattr(settings, "INGEST_MAX_RECORDS", 100_000)
MAX_SYNC_PER_HOUR = getattr(settings, "INGEST_MAX_SYNC_PER_HOUR", 12)  # per data source
PROCESS_INLINE = getattr(settings, "INGEST_PROCESS_INLINE", settings.DEBUG)


@csrf_exempt
def sync_records(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])

    source = source_from_request(request)
    if source is None:
        return JsonResponse({"error": {"code": "unauthorized",
                                       "message": "Missing or invalid X-API-Key."}}, status=401)
    if source.tenant.status != "active":
        return JsonResponse({"error": {"code": "tenant_suspended",
                                       "message": "Tenant is not active."}}, status=403)

    # Per-source rate limit: cap pushes per rolling hour.
    if MAX_SYNC_PER_HOUR:
        since = timezone.now() - timedelta(hours=1)
        recent = SyncBatch.objects.filter(data_source=source, created_at__gte=since).count()
        if recent >= MAX_SYNC_PER_HOUR:
            log.warning("sync rate-limited source=%s recent=%s", source.pk, recent)
            resp = JsonResponse({"error": {"code": "rate_limited",
                                           "message": "Too many syncs this hour."}}, status=429)
            resp["Retry-After"] = "3600"
            return resp

    # Size guard before parsing.
    body = request.body or b""
    if len(body) > MAX_SYNC_BYTES:
        return JsonResponse({"error": {"code": "payload_too_large",
                                       "message": "Sync payload exceeds the size limit."}}, status=413)

    try:
        payload = json.loads(body or b"{}")
    except ValueError:
        return JsonResponse({"error": {"code": "invalid_json",
                                       "message": "Body is not valid JSON."}}, status=422)

    if not isinstance(payload, dict):
        return JsonResponse({"error": {"code": "invalid_envelope",
                                       "message": "Expected a JSON object."}}, status=422)

    records = payload.get("records")
    if not isinstance(records, list):
        return JsonResponse({"error": {"code": "invalid_envelope",
                                       "message": "'records' must be an array."}}, status=422)
    if len(records) > MAX_RECORDS:
        return JsonResponse({"error": {"code": "too_many_records",
                                       "message": f"Max {MAX_RECORDS} records per sync."}}, status=413)

    source_ts = parse_datetime(payload.get("sync_timestamp") or "") if payload.get("sync_timestamp") else None
    active_count = payload.get("active_record_count")
    if not isinstance(active_count, int):
        active_count = None

    # Idempotency: the same source push (identified by source_timestamp) lands once.
    if source_ts is not None:
        existing = SyncBatch.objects.filter(data_source=source, source_timestamp=source_ts).first()
        if existing is not None:
            return JsonResponse({
                "batch": existing.pk, "status": existing.status,
                "duplicate": True,
                "rows_received": existing.rows_received,
            }, status=200)

    batch = SyncBatch.objects.create(
        tenant=source.tenant, data_source=source, source_timestamp=source_ts,
        active_record_count=active_count, status="received",
        rows_received=len(records),
    )
    RecordSyncStaging.objects.bulk_create([
        RecordSyncStaging(
            tenant=source.tenant, sync_batch=batch,
            record_external_id=str((rec or {}).get("external_id", ""))[:64],
            raw_payload=rec if isinstance(rec, dict) else {"_invalid": rec},
        )
        for rec in records
    ], batch_size=1000)

    log.info("sync landed source=%s batch=%s rows=%s", source.pk, batch.pk, len(records))
    result = {"batch": batch.pk, "status": batch.status, "rows_received": batch.rows_received}

    if PROCESS_INLINE:
        # Local/dev convenience: normalize now so data shows up immediately.
        # Import here to avoid a circular import at module load.
        from .normalizer import process_batch
        summary = process_batch(batch)
        result.update({"status": batch.status, "rows_upserted": summary["upserted"],
                       "rows_failed": summary["failed"]})
        return JsonResponse(result, status=200)

    return JsonResponse(result, status=202)


def sync_health(request):
    """Operational health for the sync pipeline (dashboard admin auth).

    Surfaces the numbers you'd alert on: unprocessed backlog, dead-letter count,
    recent batch outcomes, and the last successful sync time.
    """
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    role = role_from_request(request)
    if not role or role == "agent":
        return JsonResponse({"error": {"code": "forbidden",
                                       "message": "Admin role required."}}, status=403)

    day_ago = timezone.now() - timedelta(hours=24)
    recent = (SyncBatch.objects.filter(created_at__gte=day_ago)
              .values("status").annotate(n=Count("id")))
    last_completed = (SyncBatch.objects.filter(status__in=["completed", "partial"])
                      .order_by("-finished_at").first())
    return JsonResponse({
        "backlog_rows": RecordSyncStaging.objects.filter(
            processed=False, dead_lettered=False).count(),
        "dead_lettered_rows": RecordSyncStaging.objects.filter(dead_lettered=True).count(),
        "batches_last_24h": {r["status"]: r["n"] for r in recent},
        "last_synced_at": (last_completed.finished_at.isoformat()
                           if last_completed and last_completed.finished_at else None),
        "sources_active": SyncBatch.objects.values("data_source").distinct().count(),
    })


def batch_status(request, batch_id: int):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    role = role_from_request(request)
    if not role or role == "agent":
        return JsonResponse({"error": {"code": "forbidden",
                                       "message": "Admin role required."}}, status=403)
    batch = SyncBatch.objects.filter(pk=batch_id).select_related("data_source", "tenant").first()
    if batch is None:
        return JsonResponse({"error": {"code": "not_found", "message": "Batch not found."}}, status=404)
    return JsonResponse({
        "batch": batch.pk,
        "tenant": batch.tenant.slug,
        "data_source": batch.data_source.name,
        "status": batch.status,
        "source_timestamp": batch.source_timestamp.isoformat() if batch.source_timestamp else None,
        "active_record_count": batch.active_record_count,
        "rows_received": batch.rows_received,
        "rows_upserted": batch.rows_upserted,
        "rows_failed": batch.rows_failed,
        "error_detail": batch.error_detail,
        "created_at": batch.created_at.isoformat(),
        "finished_at": batch.finished_at.isoformat() if batch.finished_at else None,
    })
