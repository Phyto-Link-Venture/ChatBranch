"""Channel-account health monitor — the ONLY writer of ChannelAccount health
state (crm.ChannelAccount.health_* fields).

Every send path reports here (record_send_ok / record_send_failure), inbound
webhooks/gateway pushes prove liveness (record_inbound / record_gateway_event),
and a background checker (start_checker, WHATSAPP_HEALTH_INTERVAL seconds,
default 300) pings each configured Cloud account + the SMTP host. All entry
points are exception-safe: a health-recording bug must never break a send.

Status transitions go through set_status(), which fans out ONE notification
to admin+ users and writes one AuditLog row per actual change (never for
unconfigured/sandbox accounts).

Meta Graph error-code classification (record_send_failure):

  ACCOUNT BANNED/LOCKED -> "banned"
    368     Temporarily blocked for policies violations
    131031  Business Account locked (integrity policy review)

  AUTH / TOKEN -> "disconnected"
    0       AuthException — could not authenticate the app user
    3       API Method — capability or permissions issue
    10      API Permission denied
    190     Access token expired / invalidated (OAuthException)
    200-299 API Permission family
    33      Object (phone_number_id) missing or no permission to view it
    133010  Account not registered on the Cloud API
    HTTP 401 (and 403 with token/OAuth wording)

  PER-RECIPIENT / PER-MESSAGE — recorded NOWHERE (account health untouched)
    131047  Re-engagement message (24h contact-service window closed)
    131026  Message undeliverable (recipient blocked us / not on WhatsApp)
    131021  Recipient is the sender (bad recipient)
    131030  Recipient phone number not in allowed list (dev-mode allowlist)
    131049  Meta chose not to deliver (per-user marketing limits)
    130472  Recipient's number is part of a Meta experiment
    131056  (Business, Consumer) pair rate limit hit
    100 / 131008 / 131009  bad request parameters (our payload, not the account)
    131051 / 131052 / 131053  unsupported type / media download / media upload
    132000 / 132001 / 132005 / 132007 / 132012 / 132015 / 132016 / 132068 /
    132069  template errors (mismatch / not found / paused / disabled / format)

  ACCOUNT-LEVEL BUT RECOVERABLE -> "degraded" immediately
    131048  Spam rate limit hit (quality-tier restriction on the number)
    131042  Business eligibility / payment issue

  TRANSIENT -> fail_streak++, streak >= 3 -> "degraded"
    1       API Unknown (possible temporary downtime / throttling)
    2       API Service (temporary downtime)
    4       API Too Many Calls (app-level throughput)
    80007   WABA rate limit issues
    130429  Cloud API throughput rate limit hit
    131000  Something went wrong (generic internal error)
    131016  Service overloaded / unavailable
    131057  Account in maintenance mode
    plus HTTP 5xx, timeouts, connection errors, and anything unclassified.

Baileys accounts are event-driven (the gateway pushes connection lifecycle to
/api/whatsapp/baileys/status-event): connected -> healthy, disconnected ->
degraded, logged_out (DisconnectReason.loggedOut=401) -> disconnected,
forbidden (403) -> banned. run_checks() skips them.

A 401 is deliberately NOT "banned": it means this linked-device session was
revoked (manual unlink, the 14-day primary-offline rule, an app reinstall, or
— sometimes — enforcement). The WhatsApp account itself is usually fine and
still works on the phone. Both statuses are ineligible for routing, so sends
steer away either way; the distinction is purely what the operator is told.

Email (SMTP): each configured email ChannelAccount row is probed with a
connect/STARTTLS check against ITS OWN host (set_status per row, same
notifications/routing as every other channel). The GLOBAL config.json/env
SMTP fallback keeps its rowless probe: the result persists in the Setting row
"email_health" and /channels/health reads it back via email_health().
"""
from __future__ import annotations

import json
import logging
import os
import re
import threading
import time

from django.utils import timezone

from crm import auth as rbac

from . import wa_config

logger = logging.getLogger(__name__)

UNKNOWN = "unknown"
HEALTHY = "healthy"
DEGRADED = "degraded"
DISCONNECTED = "disconnected"
BANNED = "banned"

# routable statuses live in routing.py (INELIGIBLE_STATUSES) — health.py only
# decides WHAT the status is, routing decides what it means for sends.

DEGRADE_AFTER = 3  # consecutive transient failures before "degraded"

BAN_CODES = {368, 131031}
AUTH_CODES = {0, 3, 10, 33, 190, 133010}
RECIPIENT_CODES = {
    131047, 131026, 131021, 131030, 131049, 130472, 131056,
    100, 131008, 131009, 131051, 131052, 131053,
    132000, 132001, 132005, 132007, 132012, 132015, 132016, 132068, 132069,
}
ACCOUNT_DEGRADE_CODES = {131048, 131042}

EMAIL_HEALTH_KEY = "email_health"
VOICE_SETTINGS_KEY = "voice_twilio_settings"  # dashboard-managed Twilio SIP creds
CHECKED_AT_KEY = "channel_health_checked_at"

_HEALTH_FIELDS = ["health_status", "health_detail", "health_changed_at",
                  "last_ok_at", "last_inbound_at", "fail_streak"]


# ---- helpers ---------------------------------------------------------------------

def _load(account_id):
    """The ChannelAccount row for an account id, or None. Non-numeric ids
    (the env-default pseudo-account, blanks) have no DB row -> no-op."""
    from crm.models import ChannelAccount

    if not str(account_id or "").strip().isdigit():
        return None
    return ChannelAccount.objects.filter(pk=int(str(account_id).strip())).first()


def _is_configured(account) -> bool:
    """Configured = the account can actually reach a provider. Unconfigured /
    sandbox accounts never notify (their 'health' is meaningless noise).
    Delegates to routing._configured so every channel (incl. email rows,
    whose creds live in `config`, not access_token) uses one rule."""
    try:
        from . import routing  # lazy — keep module load light
        return routing._configured(account)
    except Exception:
        return False


_CODE_RES = (
    re.compile(r"\(code\s+(\d+)\)"),          # our client.py format
    re.compile(r"[\"']code[\"']\s*:\s*(\d+)"),  # raw Graph JSON
    re.compile(r"#(\d{2,6})\b"),               # Graph "(#131030) …" style
    re.compile(r"\bcode[\s:=]+(\d+)"),
)


def _extract_code(error_text: str) -> int | None:
    text = str(error_text or "")
    for rx in _CODE_RES:
        m = rx.search(text)
        if m:
            try:
                return int(m.group(1))
            except ValueError:
                continue
    return None


def _looks_auth(error_text: str) -> bool:
    """Textual auth-failure markers for errors that carry no usable code."""
    text = str(error_text or "").lower()
    if "meta api 401" in text:
        return True
    if "oauthexception" in text or "oauth" in text:
        return True
    if "access token" in text and any(w in text for w in ("invalid", "expired", "revoked")):
        return True
    if "meta api 403" in text and "token" in text:
        return True
    return False


# ---- the single transition point --------------------------------------------------

def set_status(account, status: str, detail: str = "") -> None:
    """Persist a health status onto a ChannelAccount. On an ACTUAL change:
    stamp health_changed_at, notify admin+ once, and write one AuditLog row.
    Exception-safe: never raises into a send path."""
    try:
        _set_status(account, status, detail)
    except Exception:
        logger.exception("whatsapp health: set_status(%s) failed",
                         getattr(account, "pk", account))


def _set_status(account, status: str, detail: str = "") -> None:
    if account is None or status not in (UNKNOWN, HEALTHY, DEGRADED, DISCONNECTED, BANNED):
        return
    old = account.health_status
    changed = old != status
    account.health_status = status
    account.health_detail = str(detail or "")[:200]
    if changed:
        account.health_changed_at = timezone.now()
    account.save(update_fields=_HEALTH_FIELDS)
    if not changed:
        return
    if not _is_configured(account):
        return  # sandbox/unconfigured accounts: track silently, never notify
    try:
        rbac.audit(None, "account.health",
                   target=f"{account.channel}:{account.name}",
                   detail=f"{old} → {status}" + (f" — {detail[:150]}" if detail else ""))
        rbac.notify(("superadmin", "admin"), "system",
                    f"Channel account {status}: {account.name}",
                    str(detail or f"Health changed from {old} to {status}.")[:500],
                    "/settings?tab=whatsapp", category="account_health")
    except Exception:
        logger.exception("whatsapp health: transition fan-out failed for %s", account.pk)
    if status in (BANNED, DISCONNECTED) and old not in (BANNED, DISCONNECTED):
        try:
            _on_conversation_account_down(account)
        except Exception:  # the recovery funnel must never break a send path
            logger.exception("whatsapp health: down-transition hook failed for %s",
                             account.pk)


# ---- conversation-account recovery funnel ------------------------------------------

def _on_conversation_account_down(account) -> None:
    """A conversation-role WhatsApp account just transitioned INTO
    banned/disconnected: its assigned contacts' conversations are stranded.
    Tell the operators how many, and — recovery_mode "auto" with a
    recovery_template configured and an eligible reminder/Cloud account to
    carry it — queue the recovery template to pull those contacts back to a
    fresh conversation number (the queue's template routing prefers
    reminder-role Cloud accounts). recovery_mode "one_click" leaves the send
    to POST /api/whatsapp/recovery/<account_id>."""
    if account.channel != "whatsapp" or wa_config.account_role(account) != "conversation":
        return
    from crm.models import AccountAssignment

    affected = AccountAssignment.objects.filter(account=account,
                                                channel="whatsapp").count()
    if not affected:
        return  # nobody stranded — the generic health notification suffices
    rbac.notify(("superadmin", "admin"), "system",
                f"Account {account.name} down — {affected} contact(s) affected",
                f"'{account.name}' is {account.health_status}. Their conversations "
                "stall until the number reconnects or the contacts are pulled back "
                "via a recovery reminder.",
                "/settings?tab=assignments", category="account_health")
    from . import routing  # lazy — keep module load light

    if (wa_config.recovery_mode() == "auto" and wa_config.recovery_template()
            and routing.eligible_accounts("whatsapp", provider="cloud")):
        queued = enqueue_recovery(account)
        rbac.audit(None, "recovery.auto", target=f"whatsapp:{account.name}",
                   detail=f"queued {queued} recovery template(s)")


def enqueue_recovery(account) -> int:
    """Queue the configured recovery template for every contact assigned to
    this (down) conversation account. The jobs ride the outbound queue, whose
    template routing sends via a reminder/Cloud account with normal pacing.
    Returns the number of jobs queued. Callers check the preconditions
    (recovery_template set, an eligible Cloud account exists)."""
    from crm.models import AccountAssignment
    from flows import queue as flows_queue

    template = wa_config.recovery_template()
    if not template:
        return 0
    queued = 0
    for asg in (AccountAssignment.objects.filter(account=account, channel="whatsapp")
                .select_related("contact")):
        contact = asg.contact
        if contact is None or not contact.phone:
            continue
        flows_queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": template, "language": "en", "variables": {}})
        queued += 1
    return queued


# ---- send/inbound recorders (exception-safe) ---------------------------------------

def record_send_ok(account_id) -> None:
    """A real provider send succeeded: proof of life."""
    try:
        account = _load(account_id)
        if account is None:
            return
        account.last_ok_at = timezone.now()
        account.fail_streak = 0
        set_status(account, HEALTHY, "")
    except Exception:
        logger.exception("whatsapp health: record_send_ok(%s) failed", account_id)


def record_send_failure(account_id, error_text, code: int | None = None) -> None:
    """Classify a send failure (see module docstring for the code map).
    Per-recipient errors never touch account health."""
    try:
        account = _load(account_id)
        if account is None:
            return
        c = code if code is not None else _extract_code(error_text)
        detail = str(error_text or "")[:200]
        if c in BAN_CODES:
            set_status(account, BANNED, detail)
            return
        if c in RECIPIENT_CODES:
            return  # the recipient's problem, not the account's
        if (c in AUTH_CODES or (c is not None and 200 <= c <= 299)
                or _looks_auth(error_text)):
            set_status(account, DISCONNECTED, detail)
            return
        if c in ACCOUNT_DEGRADE_CODES:
            set_status(account, DEGRADED, detail)
            return
        # transient: timeouts, 5xx, rate limits, anything unclassified
        account.fail_streak += 1
        if account.fail_streak >= DEGRADE_AFTER:
            set_status(account, DEGRADED, detail)
        else:
            account.save(update_fields=["fail_streak"])
    except Exception:
        logger.exception("whatsapp health: record_send_failure(%s) failed", account_id)


def record_inbound(account_id) -> None:
    """An inbound message arrived through this account — it demonstrably works."""
    try:
        account = _load(account_id)
        if account is None:
            return
        account.last_inbound_at = timezone.now()
        account.fail_streak = 0
        set_status(account, HEALTHY, "")
    except Exception:
        logger.exception("whatsapp health: record_inbound(%s) failed", account_id)


def record_gateway_event(account_id, event: str, reason: str = "") -> dict:
    """Baileys gateway connection lifecycle (pushed to /baileys/status-event):
    connected -> healthy; disconnected -> degraded (transient, the gateway
    auto-reconnects); logged_out (401) -> disconnected (session dead, needs a
    re-pair — NOT proof of a ban); a 403/forbidden close -> banned."""
    try:
        account = _load(account_id)
        if account is None or account.provider != "baileys":
            return {"ignored": True}
        low = f"{event} {reason}".lower()
        if event == "connected":
            account.last_ok_at = timezone.now()
            account.fail_streak = 0
            set_status(account, HEALTHY, "")
        elif event in ("logged_out", "disconnected"):
            if "403" in str(reason) or "forbidden" in low:
                # 403 forbidden is the clearest enforcement signal there is.
                set_status(account, BANNED, reason or "forbidden")
            elif event == "logged_out":
                # 401 loggedOut is NOT proof of a ban. Per RESEARCH.md it is
                # equally a manual unlink, the 14-day primary-offline rule, or
                # an app reinstall — none of which touch the WhatsApp ACCOUNT,
                # only this linked-device session. Calling it "banned" told the
                # operator their number was burned while WhatsApp on the phone
                # was working perfectly (2026-07-23). DISCONNECTED is equally
                # ineligible for routing (the Signal keys really are dead, so
                # nothing can send until it is re-paired) without the false
                # alarm. A real ban still surfaces via the 403 branch above.
                set_status(account, DISCONNECTED,
                           reason or "logged out — re-pair this number to reconnect")
            else:
                set_status(account, DEGRADED, reason or "gateway disconnected")
        else:
            return {"ignored": True}
        return {"status": account.health_status}
    except Exception:
        logger.exception("whatsapp health: record_gateway_event(%s) failed", account_id)
        return {"ignored": True}


# ---- periodic checks ---------------------------------------------------------------

def _graph_ping(account: dict) -> tuple[bool, str, int | None]:
    """Cheap token/liveness probe: GET /{version}/{phone_number_id}?fields=id.
    Returns (ok, error_text, error_code). Never raises."""
    import httpx  # lazy — same rule as whatsapp/client.py

    version = account.get("api_version") or "v23.0"
    url = f"{wa_config.GRAPH_BASE_URL}/{version}/{account.get('phone_number_id', '')}?fields=id"
    try:
        resp = httpx.get(url, headers={
            "Authorization": f"Bearer {account.get('access_token', '')}",
        }, timeout=15)
    except Exception as e:
        return False, f"health check failed: {e}", None
    if resp.status_code == 200:
        return True, "", None
    try:
        err = (resp.json() or {}).get("error") or {}
    except ValueError:
        err = {}
    return (False,
            f"Meta API {resp.status_code}: {err.get('message', resp.text[:150])}",
            err.get("code"))


def _smtp_ping(host: str | None = None, port: int | None = None,
               use_tls: bool | None = None) -> tuple[bool, str]:
    """SMTP connect (+ STARTTLS when configured) probe — no send, no login.
    Without arguments it probes the GLOBAL messaging SMTP settings (the
    fallback chip); pass host/port/use_tls to probe one email ChannelAccount
    row's server. Returns (ok, detail). Never raises."""
    import smtplib  # stdlib, but keep the checker import-light at module load

    if host is None:
        from messaging import msg_config  # read-only import

        s = msg_config.email_settings()
        host = s["smtp_host"]
        port = s["smtp_port"]
        use_tls = str(s["smtp_tls"]).lower() not in ("false", "0", "no")
    try:
        port = int(port or 587)
    except (TypeError, ValueError):
        port = 587
    use_tls = True if use_tls is None else bool(use_tls)
    server = None
    try:
        if port == 465:
            server = smtplib.SMTP_SSL(host, port, timeout=10)
        else:
            server = smtplib.SMTP(host, port, timeout=10)
            if use_tls:
                server.starttls()
        return True, ""
    except Exception as e:
        return False, str(e)[:200]
    finally:
        try:
            if server is not None:
                server.quit()
        except Exception:
            pass


def _store_email_health(health: str, detail: str) -> None:
    from crm.models import Setting

    Setting.objects.update_or_create(key=EMAIL_HEALTH_KEY, defaults={"value": json.dumps({
        "health": health, "detail": detail, "checked_at": timezone.now().isoformat(),
    })})


def email_health() -> dict:
    """{"configured", "health", "detail"} for the /channels/health payload.
    "unknown" until a check has run (or while SMTP is unconfigured)."""
    from crm.models import Setting

    from messaging import msg_config  # read-only import

    configured = False
    try:
        configured = msg_config.email_configured()
    except Exception:
        logger.exception("whatsapp health: email config read failed")
    stored = {}
    row = Setting.objects.filter(key=EMAIL_HEALTH_KEY).first()
    if row and row.value.strip():
        try:
            stored = json.loads(row.value)
        except ValueError:
            stored = {}
    if not configured:
        return {"configured": False, "health": UNKNOWN, "detail": ""}
    return {
        "configured": True,
        "health": stored.get("health") or UNKNOWN,
        "detail": stored.get("detail") or "",
    }


def _telegram_ping(token: str) -> tuple[str, str]:
    """Telegram bot liveness: getMe proves the token still works (revoked →
    401), getWebhookInfo proves the bot can RECEIVE (webhook set, not erroring,
    no big backlog). Returns (health, detail)."""
    import httpx  # lazy — the checker must stay import-light at module load

    base = f"https://api.telegram.org/bot{token}"
    r = httpx.get(f"{base}/getMe", timeout=15)
    if r.status_code in (401, 404):
        return DISCONNECTED, "bot token invalid or revoked"
    r.raise_for_status()
    if not r.json().get("ok"):
        return DISCONNECTED, str(r.json().get("description") or "getMe failed")[:180]
    info = httpx.get(f"{base}/getWebhookInfo", timeout=15).json().get("result") or {}
    if not info.get("url"):
        return DEGRADED, "bot alive, but no webhook set — inbound messages won't arrive"
    last_err = info.get("last_error_date") or 0
    if last_err and (time.time() - last_err) < 1800:
        return DEGRADED, f"webhook failing: {str(info.get('last_error_message') or 'unknown')[:140]}"
    pending = info.get("pending_update_count") or 0
    if pending > 20:
        return DEGRADED, f"{pending} updates stuck pending — webhook not keeping up"
    return HEALTHY, ""


def _stored_voice_settings() -> dict:
    """The raw dashboard-saved Twilio SIP creds (Setting row), WITHOUT env
    merge — save paths must write only what the operator entered, never
    bake .env in."""
    from crm.models import Setting

    row = Setting.objects.filter(key=VOICE_SETTINGS_KEY).first()
    if row and row.value.strip():
        try:
            return json.loads(row.value)
        except ValueError:
            pass
    return {}


def voice_settings() -> dict:
    """Effective Twilio SIP trunk creds {sip_username, sip_password,
    termination_uri}: the dashboard row (Settings → Channels → Voice) wins
    per field, .env is the fallback — same precedence as every other
    channel's config."""
    stored = _stored_voice_settings()

    def pick(key: str, env: str) -> str:
        return str(stored.get(key) or "").strip() or os.getenv(env, "").strip()

    return {"sip_username": pick("sip_username", "TWILIO_SIP_USERNAME"),
            "sip_password": pick("sip_password", "TWILIO_SIP_PASSWORD"),
            "termination_uri": pick("termination_uri", "TWILIO_TERMINATION_URI")}


def save_voice_settings(patch: dict) -> dict:
    """Merge {sip_username, sip_password, termination_uri} into the Setting
    row. Only keys present in patch change; an empty string clears the
    stored value back to the .env fallback. Returns the effective merged
    view."""
    from crm.models import Setting

    stored = _stored_voice_settings()
    for key in ("sip_username", "sip_password", "termination_uri"):
        if key in patch:
            stored[key] = str(patch.get(key) or "").strip()
    Setting.objects.update_or_create(key=VOICE_SETTINGS_KEY,
                                     defaults={"value": json.dumps(stored)})
    return voice_settings()


def sip_trunk_configured() -> bool:
    """The LiveKit outbound trunk id is .env-managed (one-time
    setup_trunk.py run — the voice-agent worker is a separate Django-free
    process that reads it at boot); the termination URI can come from the
    dashboard row instead."""
    return bool(os.getenv("SIP_OUTBOUND_TRUNK_ID", "").strip()
                and voice_settings()["termination_uri"])


def voice_health() -> dict:
    """{"configured", "health", "detail", "live_check"} for /channels/health.

    Configured = the LiveKit SIP trunk (SIP_OUTBOUND_TRUNK_ID env + a
    termination URI) plus the SIP username/password are all set (Settings →
    Channels → Voice, or TWILIO_SIP_USERNAME/TWILIO_SIP_PASSWORD/
    TWILIO_TERMINATION_URI in .env). There's no REST endpoint that proves a
    SIP credential works short of placing a real call, so health here is
    config-presence only (live_check always false)."""
    if not sip_trunk_configured():
        return {"configured": False, "health": UNKNOWN, "detail": "", "live_check": False}
    s = voice_settings()
    if not (s["sip_username"] and s["sip_password"]):
        return {"configured": True, "health": UNKNOWN, "live_check": False,
                "detail": "SIP trunk set — add the SIP username + password "
                          "(Settings → Channels → Voice, or TWILIO_SIP_USERNAME / "
                          "TWILIO_SIP_PASSWORD in .env) to dial"}
    return {"configured": True, "live_check": False, "health": HEALTHY, "detail": ""}


def last_checked_at() -> str | None:
    from crm.models import Setting

    row = Setting.objects.filter(key=CHECKED_AT_KEY).first()
    return row.value.strip() or None if row else None


def check_account(account) -> bool:
    """One live probe for ONE account, channel-appropriate. Returns True when
    a check actually ran (baileys/unconfigured/probe-less channels are
    skipped). Persists through set_status; safe on any thread."""
    if account.channel == "whatsapp":
        if (account.provider or "cloud") == "baileys":
            return False  # the gateway pushes connection events itself
        serialized = wa_config._serialize(account)
        if not wa_config.account_configured(serialized):
            return False
        ok, error_text, code = _graph_ping(serialized)
        if ok:
            account.last_ok_at = timezone.now()
            account.fail_streak = 0
            set_status(account, HEALTHY, "")
        else:
            record_send_failure(str(account.pk), error_text, code=code)
        return True
    if account.channel == "telegram":
        if not account.access_token:
            return False
        try:
            status, detail = _telegram_ping(account.access_token)
        except Exception as e:  # API unreachable ≠ bot dead
            status, detail = DEGRADED, f"Telegram API unreachable: {e}"
        if status == HEALTHY:
            account.last_ok_at = timezone.now()
            account.fail_streak = 0
        set_status(account, status, detail)
        return True
    if account.channel == "email":
        cfg = account.config or {}
        if not (cfg.get("host") and cfg.get("username") and cfg.get("password")):
            return False  # unconfigured/sandbox rows: never probe, never notify
        ok, detail = _smtp_ping(cfg.get("host"), cfg.get("port"),
                                cfg.get("use_tls", True))
        if ok:
            account.last_ok_at = timezone.now()
            account.fail_streak = 0
            set_status(account, HEALTHY, "")
        else:
            set_status(account, DISCONNECTED, detail)
        return True
    return False  # facebook/instagram: no cheap liveness probe — send-driven


def reset_on_reconfigure(account_id) -> None:
    """Operator just changed this account's credentials: forget the stale
    health verdict SILENTLY (their own action — no notification spam) and
    re-check on a daemon thread, so a just-fixed account becomes routable in
    seconds instead of at the next tick. Without this, a reconnected account
    stays 'disconnected' and failover keeps steering sends away from it
    (seen live: a re-tokened Cloud account failed template sends over to
    Baileys). Exception-safe."""
    try:
        account = _load(account_id)
        if account is None:
            return
        account.health_status = UNKNOWN
        account.health_detail = "credentials updated — rechecking"
        account.fail_streak = 0
        account.health_changed_at = timezone.now()
        account.save(update_fields=["health_status", "health_detail",
                                    "fail_streak", "health_changed_at"])

        def recheck():
            try:
                row = _load(account_id)
                if row is not None:
                    check_account(row)
            except Exception:
                logger.exception("whatsapp health: post-reconfigure recheck failed")
            finally:
                from django.db import close_old_connections
                close_old_connections()

        threading.Thread(target=recheck, daemon=True, name="wa-health-recheck").start()
    except Exception:
        logger.exception("whatsapp health: reset_on_reconfigure failed")


def run_checks() -> dict:
    """One monitor pass: Graph token ping per configured Cloud account,
    getMe/getWebhookInfo per Telegram bot, SMTP probe per configured email
    ChannelAccount row + one for the global SMTP fallback settings. Voice
    has no live probe (SIP creds only prove out on a real call) — its
    "configured" state is computed on read in voice_health(), nothing to
    check here. Baileys accounts are event-driven (gateway pushes) and are
    skipped. Persists everything through set_status / Setting rows."""
    from crm.models import ChannelAccount, Setting

    from messaging import msg_config  # read-only import

    summary = {"cloud_checked": 0, "telegram_checked": 0,
               "email_accounts_checked": 0, "email": None}
    counters = {"whatsapp": "cloud_checked", "telegram": "telegram_checked",
                "email": "email_accounts_checked"}
    for account in ChannelAccount.objects.filter(channel__in=list(counters)):
        try:
            if check_account(account):
                summary[counters[account.channel]] += 1
        except Exception:
            logger.exception("whatsapp health: check failed for account %s", account.pk)

    try:
        if msg_config.email_configured():
            ok, detail = _smtp_ping()
            _store_email_health(HEALTHY if ok else DISCONNECTED, detail)
            summary["email"] = HEALTHY if ok else DISCONNECTED
    except Exception:
        logger.exception("whatsapp health: email check failed")

    try:
        Setting.objects.update_or_create(
            key=CHECKED_AT_KEY, defaults={"value": timezone.now().isoformat()})
    except Exception:
        logger.exception("whatsapp health: could not stamp checked_at")
    return summary


# ---- background checker (started from whatsapp/apps.py, flows-ticker pattern) ------

def check_interval() -> int:
    try:
        return max(30, int(os.getenv("WHATSAPP_HEALTH_INTERVAL", "300") or 300))
    except ValueError:
        return 300


_checker_lock = threading.Lock()
_checker_started = False


def start_checker() -> bool:
    """Start the in-process health checker thread (idempotent)."""
    global _checker_started
    with _checker_lock:
        if _checker_started:
            return False
        _checker_started = True

    def loop():
        from django.db import close_old_connections

        # First pass shortly after boot — a restarted server must not show
        # every account "unknown · last OK never" for a whole interval.
        delay = 15
        while True:
            time.sleep(delay)
            delay = check_interval()
            try:
                run_checks()
            except Exception:
                logger.exception("whatsapp health: run_checks failed")
            finally:
                close_old_connections()

    threading.Thread(target=loop, daemon=True, name="whatsapp-health").start()
    logger.info("whatsapp health: checker started (every %ss)", check_interval())
    return True


def checker_alive() -> bool:
    return _checker_started
