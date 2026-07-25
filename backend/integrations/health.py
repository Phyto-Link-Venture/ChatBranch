"""Integration status — the ONLY writer of `Integration.status`.

Mirrors whatsapp/health.py `set_status` exactly: one transition point, which
stamps `status_changed_at`, writes ONE AuditLog row and fans out ONE
notification to admin+ per ACTUAL change. Unconnected rows (no credentials —
the template's default state) transition silently: a sandbox integration's
"health" is meaningless noise.

Every entry point is exception-safe. A status bookkeeping bug must never break
a flow step, an OAuth callback or a webhook ack.
"""
from __future__ import annotations

import logging

from django.utils import timezone

from crm import auth as rbac

logger = logging.getLogger(__name__)

UNKNOWN = "unknown"
HEALTHY = "healthy"
DEGRADED = "degraded"
DISCONNECTED = "disconnected"
REVOKED = "revoked"

STATUSES = (UNKNOWN, HEALTHY, DEGRADED, DISCONNECTED, REVOKED)
# Statuses that mean "do not attempt calls through this row".
INELIGIBLE = (DISCONNECTED, REVOKED)

_FIELDS = ["status", "status_detail", "status_changed_at", "last_ok_at"]

SETTINGS_LINK = "/settings/integrations"


def set_status(integration, status: str, detail: str = "") -> None:
    """Persist a status onto an Integration. On an ACTUAL change: stamp
    status_changed_at, notify admin+ once, write one AuditLog row.
    Exception-safe — never raises into a caller."""
    try:
        _set_status(integration, status, detail)
    except Exception:
        logger.exception("integrations: set_status(%s) failed",
                         getattr(integration, "pk", integration))


def _set_status(integration, status: str, detail: str = "") -> None:
    if integration is None or status not in STATUSES:
        return
    old = integration.status
    changed = old != status
    integration.status = status
    integration.status_detail = str(detail or "")[:200]
    if changed:
        integration.status_changed_at = timezone.now()
    integration.save(update_fields=_FIELDS + ["updated_at"])
    if not changed:
        return
    if not integration.connected:
        return  # never-connected/sandbox rows: track silently, never notify
    try:
        rbac.audit(None, "integration.status",
                   target=f"{integration.provider}:{integration.name}",
                   detail=f"{old} → {status}" + (f" — {detail[:150]}" if detail else ""))
        rbac.notify(("superadmin", "admin"), "system",
                    f"Integration {status}: {integration.name}",
                    str(detail or f"Status changed from {old} to {status}.")[:500],
                    SETTINGS_LINK, category="account_health")
    except Exception:
        logger.exception("integrations: transition fan-out failed for %s",
                         integration.pk)


def record_ok(integration) -> None:
    """A live provider call succeeded — proof of life."""
    try:
        if integration is None:
            return
        integration.last_ok_at = timezone.now()
        set_status(integration, HEALTHY, "")
    except Exception:
        logger.exception("integrations: record_ok failed")


def record_failure(integration, detail: str, status: str = DEGRADED) -> None:
    """A provider call failed. Auth failures should pass status=DISCONNECTED
    (see oauth.access_token) — everything else degrades."""
    try:
        set_status(integration, status, str(detail or "")[:200])
    except Exception:
        logger.exception("integrations: record_failure failed")
