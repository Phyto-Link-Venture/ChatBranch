"""Bulk contact import endpoint (under /api/).

  POST contacts/import            -> parse + upsert, returns a summary
  POST contacts/import?preview=1  -> same, but computes the plan WITHOUT writing

Multipart form: `file` (the .xlsx/.csv), optional `mapping` (JSON {field:header}
overriding auto-detection), optional `preview`. Gated `contacts.manage` (admin+).
"""
from __future__ import annotations

import json
import logging

from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from . import auth as rbac
from . import importer
from .permissions_store import has_capability

logger = logging.getLogger(__name__)


def _is_preview(request) -> bool:
    raw = str(request.GET.get("preview") or request.POST.get("preview") or "").lower()
    return raw in ("1", "true", "yes")


@csrf_exempt
def contact_import(request):
    if not rbac.role_from_request(request):
        return JsonResponse({"detail": "Authentication required."}, status=401)
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not has_capability(rbac.role_from_request(request), "contacts.manage"):
        return JsonResponse({"detail": "Admin role required to import contacts."}, status=403)

    upload = request.FILES.get("file")
    if upload is None:
        return JsonResponse({"detail": "No file uploaded (form field 'file')."}, status=400)
    try:
        headers, rows = importer.parse(upload.read(), upload.name)
    except ValueError as e:
        return JsonResponse({"detail": str(e)}, status=400)
    except Exception:
        logger.exception("crm.import: parse failed")
        return JsonResponse({"detail": "Could not read the file — is it a valid .xlsx/.csv?"},
                            status=400)
    if not headers:
        return JsonResponse({"detail": "The file has no rows."}, status=400)

    mapping = importer.detect_mapping(headers)
    raw_map = request.POST.get("mapping")
    if raw_map:
        try:
            override = json.loads(raw_map)
        except ValueError:
            override = None
        if isinstance(override, dict):
            for field, header in override.items():
                if field in importer.FIELDS and header:
                    mapping[field] = str(header)

    preview = _is_preview(request)
    tenant = rbac.tenant_from_request(request)
    summary = importer.run_import(rows, mapping, tenant=tenant, dry_run=preview)
    if not preview:
        rbac.audit(request, "contact.import",
                   detail=(f"created={summary['created']} updated={summary['updated']} "
                           f"skipped={summary['skipped']}"))
    return JsonResponse({"preview": preview, "headers": headers, "mapping": mapping,
                         "fields": importer.FIELDS, **summary})
