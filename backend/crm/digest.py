"""Daily ops digest for admins.

Computes a summary of the last day's activity and delivers it to every
superadmin + admin — in-app (a `crm.Notification`, always) and optionally by
email (`messaging.email_client.send_email`, gated by Setting `digest_email`).

Two firing paths, both call `deliver`:
  * `manage.py send_digest` — for external cron (recommended for production);
  * an opt-in in-process ticker (`start_ticker`, enabled by env
    `CRM_DIGEST_TICKER=1`) that fires once a day after Setting `digest_hour`
    (server-local), tracked idempotently via Setting `digest_last_sent`.

Every entry point is exception-safe: a digest failure must never take down the
serving process or a cron run.
"""
from __future__ import annotations

import logging
import threading
import time
from collections import Counter
from datetime import timedelta

from django.utils import timezone

logger = logging.getLogger(__name__)

DEFAULT_HOUR = 8          # server-local hour the ticker fires after
TICKER_INTERVAL_S = 300   # how often the ticker re-checks the clock

_ticker_lock = threading.Lock()
_ticker_started = False


def _setting(key: str, default: str = "") -> str:
    from .models import Setting

    row = Setting.objects.filter(key=key).first()
    return (row.value if row and row.value else "") or default


def _set_setting(key: str, value: str) -> None:
    from .models import Setting

    Setting.objects.update_or_create(key=key, defaults={"value": value})


def _truthy(value: str) -> bool:
    return str(value or "").strip().lower() in ("1", "true", "on", "yes")


def build_digest(since, until, *, tenant=None) -> dict:
    """A compact summary dict of activity in [since, until). Tenant-scopes the
    contact/record counts when a tenant is given; messaging/call counts are
    installation-wide (Message/CallRecord carry no tenant FK — the well-tested
    single-tenant path)."""
    from .models import AuditLog, CallRecord, Contact, Message, Record

    msgs = Message.objects.filter(created_at__gte=since, created_at__lt=until, private=False)
    inbound = msgs.filter(direction="in").count()
    outbound = msgs.filter(direction="out").count()
    ai_out = msgs.filter(direction="out", source="ai").count()

    contacts_q = Contact.objects.filter(created_at__gte=since, created_at__lt=until)
    records_q = Record.objects.filter(created_at__gte=since, created_at__lt=until)
    if tenant is not None:
        contacts_q = contacts_q.filter(tenant=tenant)
        records_q = records_q.filter(tenant=tenant)

    # CallRecord.started_at is an ISO-8601 string (file-store provenance), so we
    # compare lexicographically — correct for same-format timestamps. Guarded.
    calls = 0
    try:
        calls = CallRecord.objects.filter(
            started_at__gte=since.isoformat(), started_at__lt=until.isoformat()).count()
    except Exception:
        calls = 0

    audit = AuditLog.objects.filter(created_at__gte=since, created_at__lt=until)
    action_counts = Counter(audit.values_list("action", flat=True))
    health = list(audit.filter(action="account.health").values_list("target", "detail")[:10])

    return {
        "since": since,
        "until": until,
        "date_label": timezone.localtime(until).strftime("%d %b %Y"),
        "messages": {"inbound": inbound, "outbound": outbound, "ai": ai_out},
        "new_contacts": contacts_q.count(),
        "new_records": records_q.count(),
        "new_bookings": records_q.filter(type="booking").count(),
        "calls": calls,
        "health_events": [{"target": t, "detail": d} for t, d in health],
        "top_actions": action_counts.most_common(6),
    }


def summary_line(d: dict) -> str:
    """One-line summary for the in-app notification body."""
    m = d["messages"]
    return (f"{m['inbound']} in / {m['outbound']} out ({m['ai']} by AI) · "
            f"{d['calls']} calls · {d['new_bookings']} bookings · "
            f"{d['new_contacts']} new customers")


def render_text(d: dict) -> str:
    """Full plain-text body (email + `manage.py send_digest --print`)."""
    m = d["messages"]
    lines = [
        f"Daily summary — {d['date_label']}",
        "",
        f"Messages:     {m['inbound']} inbound, {m['outbound']} outbound "
        f"({m['ai']} sent by the AI)",
        f"Calls:        {d['calls']}",
        f"New bookings: {d['new_bookings']}",
        f"New records:  {d['new_records']}",
        f"New customers:{d['new_contacts']}",
    ]
    if d["health_events"]:
        lines.append("")
        lines.append("Account health changes:")
        for ev in d["health_events"]:
            lines.append(f"  - {ev['target']}: {ev['detail']}")
    if d["top_actions"]:
        lines.append("")
        lines.append("Activity:")
        for action, n in d["top_actions"]:
            lines.append(f"  - {action}: {n}")
    return "\n".join(lines)


def _admin_profiles():
    from .models import Profile

    return Profile.objects.filter(
        role__in=("superadmin", "admin"), user__is_active=True).select_related("user")


def _email_admins(subject: str, body: str) -> int:
    """Best-effort email to each admin with an address. Returns the count sent.
    A per-recipient or SMTP failure is logged and skipped, never raised."""
    try:
        from messaging.email_client import send_email
    except Exception:
        logger.exception("crm.digest: email client unavailable")
        return 0
    sent = 0
    for p in _admin_profiles():
        addr = (getattr(p.user, "email", "") or "").strip()
        if not addr:
            continue
        try:
            send_email(addr, body, subject=subject)
            sent += 1
        except Exception:
            logger.warning("crm.digest: email to %s failed", addr)
    return sent


def deliver(d: dict, *, email: bool = False) -> dict:
    """Fan the digest out to admins in-app (always) and by email (if asked).
    Returns {notified, emailed}. Never raises."""
    from crm import auth as rbac

    title = f"Daily summary — {d['date_label']}"
    notified = 0
    try:
        rbac.notify(("superadmin", "admin"), "system", title, summary_line(d),
                    "/analytics", category="admin")
        notified = _admin_profiles().count()
    except Exception:
        logger.exception("crm.digest: in-app notify failed")
    emailed = _email_admins(title, render_text(d)) if email else 0
    return {"notified": notified, "emailed": emailed}


def maybe_send(now=None) -> bool:
    """Ticker tick: send the digest once per day after `digest_hour`
    (server-local), idempotent via `digest_last_sent`. Returns True if it sent."""
    now = now or timezone.now()
    local = timezone.localtime(now)
    try:
        hour = int(_setting("digest_hour", str(DEFAULT_HOUR)))
    except ValueError:
        hour = DEFAULT_HOUR
    if local.hour < hour:
        return False
    today = local.date().isoformat()
    if _setting("digest_last_sent") == today:
        return False
    since = now - timedelta(days=1)
    d = build_digest(since, now)
    deliver(d, email=_truthy(_setting("digest_email", "off")))
    _set_setting("digest_last_sent", today)
    logger.info("crm.digest: daily digest sent for %s", today)
    return True


def start_ticker() -> bool:
    """Start the in-process daily-digest thread (idempotent) — same pattern as
    aicore.memory.start_ticker. Opt-in via env CRM_DIGEST_TICKER=1 (see
    crm/apps.py); external cron via `manage.py send_digest` is the alternative."""
    global _ticker_started
    with _ticker_lock:
        if _ticker_started:
            return False
        _ticker_started = True

    def loop():
        from django.db import close_old_connections

        while True:
            time.sleep(TICKER_INTERVAL_S)
            try:
                maybe_send()
            except Exception:
                logger.exception("crm.digest: tick failed")
            finally:
                close_old_connections()

    threading.Thread(target=loop, daemon=True, name="crm-digest").start()
    logger.info("crm: daily-digest ticker started (checks every %ss)", TICKER_INTERVAL_S)
    return True
