"""Integration actions — what a flow-builder Action step can DO with a
connected third-party account.

The engine (flows/engine.py `_do_integration`) calls `run_action(name, …)` with
the builder's channel label. Every action returns the same envelope:

    {"ok": bool, "skipped": bool, "detail": str, "data": {...}}

**Sandbox contract (mandatory for a template).** With no credentials
configured, `run_action` returns `ok=False, skipped=True` and a human message
("Google Calendar is not connected — step skipped."). It never raises, never
opens a socket and never blocks: every live call carries an explicit timeout,
and the "is it connected" check happens before any network work. A developer
cloning this repo with zero API keys gets a working app whose integration
steps log a clear skip.
"""
from __future__ import annotations

import json
import logging
import math
import re
from datetime import datetime, time as dt_time, timedelta

from . import catalog, health, oauth

logger = logging.getLogger(__name__)

HTTP_TIMEOUT = 20

# Builder channel label -> (provider, handler name). Keep in step with
# frontend/src/lib/flow/nodeConfig.ts `channels` and catalog.PROVIDERS[…].actions.
ACTIONS: dict[str, str] = {
    "Create calendar event": "google_calendar",
    "Find free slot": "google_calendar",
    "Upload file to Drive": "google_drive",
    "Append row to Sheet": "google_sheets",
    "Send via Gmail": "gmail",
    "Update order status": "shopify",
    "Create Stripe payment link": "stripe",
}


def _skip(detail: str) -> dict:
    return {"ok": False, "skipped": True, "detail": detail, "data": {}}


def _fail(detail: str) -> dict:
    return {"ok": False, "skipped": False, "detail": detail, "data": {}}


def _done(detail: str, data: dict | None = None) -> dict:
    return {"ok": True, "skipped": False, "detail": detail, "data": data or {}}


def active_integration(provider: str, entity=None):
    """The connected row for a provider, or None. Prefers a healthy one so a
    revoked duplicate never shadows a working connection.

    ENTITY BINDING: when `entity` is given, that outlet's OWN connection wins;
    an outlet that never connected one falls back to the organization-wide
    (unbound) connection. So each studio's bookings land in its own Google
    Calendar, and branches without one keep using the company account."""
    from .models import Integration

    def _pick(rows):
        rows = [r for r in rows if r.connected]
        if not rows:
            return None
        return (next((r for r in rows if r.status not in health.INELIGIBLE), None)
                or rows[0])

    qs = Integration.objects.filter(provider=provider)
    if entity is not None:
        own = _pick(qs.filter(entity=entity))
        if own is not None:
            return own
        return _pick(qs.filter(entity__isnull=True))
    return _pick(qs)


def _label(provider: str) -> str:
    return (catalog.provider_meta(provider) or {}).get("label", provider)


def _google_ready(provider: str, entity=None):
    """(integration, token) or (None, "") — with the not-connected reason
    already turned into a skip by the caller. `entity` picks that outlet's own
    connection when it has one (see active_integration)."""
    row = active_integration(provider, entity)
    if row is None:
        return None, ""
    return row, oauth.access_token(row)


def _request(method: str, url: str, *, token: str = "", headers: dict | None = None,
             json_body=None, params: dict | None = None, data=None):
    """One HTTP call with a hard timeout. Returns (ok, payload_or_error)."""
    import httpx  # lazy — keep the flows ticker import-light

    hdrs = dict(headers or {})
    if token:
        hdrs["Authorization"] = f"Bearer {token}"
    try:
        resp = httpx.request(method, url, headers=hdrs, json=json_body,
                             params=params, data=data, timeout=HTTP_TIMEOUT)
    except Exception as e:
        return False, f"{type(e).__name__} contacting the provider"
    if resp.status_code >= 400:
        detail = ""
        try:
            body = resp.json()
            detail = str((body.get("error") or {}).get("message")
                         or body.get("message") or "")[:160]
        except Exception:
            detail = ""
        return False, f"HTTP {resp.status_code}" + (f": {detail}" if detail else "")
    try:
        return True, resp.json()
    except ValueError:
        return True, {}


def _record_result(row, ok: bool, detail: str) -> None:
    if ok:
        health.record_ok(row)
    else:
        status = (health.DISCONNECTED
                  if detail.startswith(("HTTP 401", "HTTP 403")) else health.DEGRADED)
        health.record_failure(row, detail, status=status)


# ---- Google Calendar ---------------------------------------------------------------

def create_calendar_event(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_calendar", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Calendar is not connected — step skipped.")
    calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                      or "primary")
    start = _parse_when(cfg.get("eventStart"))
    try:
        minutes = max(5, int(cfg.get("eventMinutes") or 30))
    except (TypeError, ValueError):
        minutes = 30
    body = {
        "summary": str(cfg.get("eventTitle") or text or "Appointment")[:200],
        "description": str(text or "")[:2000],
        "start": {"dateTime": start.isoformat()},
        "end": {"dateTime": (start + timedelta(minutes=minutes)).isoformat()},
    }
    if contact is not None and contact.email:
        body["attendees"] = [{"email": contact.email}]
    ok, payload = _request(
        "POST",
        f"https://www.googleapis.com/calendar/v3/calendars/{calendar_id}/events",
        token=token, json_body=body)
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Calendar: {payload}")
    return _done(f"calendar event created ({body['summary']})",
                 {"event_id": payload.get("id"), "html_link": payload.get("htmlLink")})


def validate_booking_time(contact=None, config=None) -> dict:
    """Real availability check for a candidate booking time — opening hours
    AND an actual calendar conflict — WITHOUT creating anything. `{"ok":
    True}` when bookable; otherwise the same failure shape book_appointment
    has always returned (outside_hours / conflict), so callers can reuse it
    unchanged.

    Shared by book_appointment (the execution path) and the AI tool layer's
    PRE-confirmation check (aicore/tools.py _precheck_book_appointment) — the
    model has no reliable way to learn whether a specific time is actually
    free unless something checks it automatically the moment it tries to
    book, since it does not always separately call
    check_calendar_availability's optional requested_start_iso first
    (observed live: the model just asserted a genuinely-free time was
    unavailable, with no tool call backing that claim at all)."""
    cfg = config or {}
    start = _parse_when(cfg.get("eventStart")) if cfg.get("eventStart") else None
    if start is None:
        return _fail("no appointment time given")
    minutes = _int(cfg.get("eventMinutes"), 30, lo=5, hi=480)
    schedule = _parse_schedule(cfg["businessHours"]) if cfg.get("businessHours") \
        else booking_schedule(contact_entity(contact))
    if not within_schedule(start, minutes, schedule):
        return {"ok": False, "outside_hours": True,
                "detail": "requested time is outside the bookable opening hours",
                "hours": booking_hours_raw(contact_entity(contact)),
                "hint": ("tell the customer that time is outside opening hours, "
                         "then call check_calendar_availability and offer a valid "
                         "slot instead")}
    if _check_conflict(contact, cfg, start, minutes):
        return {"ok": False, "conflict": True,
                "detail": "that time is already booked on the calendar",
                "hint": ("tell the customer that slot was just taken, then call "
                         "check_calendar_availability and offer a different slot")}
    return {"ok": True}


def book_appointment(contact=None, config=None, text="") -> dict:
    """Book an appointment: create the calendar event (reusing
    create_calendar_event) AND record it as a crm.Record (type "booking") + a
    RecordEvent so the appointment is tracked and shows on the contact.

    Used by the AI tool-calling loop (aicore/tools.py); the AuditLog +
    Notification a booking fans out live in that layer (it is an AI action).
    The CRM write is best-effort — a booking that succeeded on the calendar is
    never failed by a bookkeeping error, and with no contact linked it degrades
    to just the calendar event (the same skip/fail envelope as everything else).

    GUARDS: the requested time is validated against the operator's booking
    schedule AND checked for a real calendar conflict BEFORE any calendar
    write — see `validate_booking_time`, shared with the AI tool layer's
    pre-confirmation check so the same truth is available even before the
    customer agrees (aicore/tools.py _precheck_book_appointment).
    """
    cfg = config or {}
    validity = validate_booking_time(contact=contact, config=cfg)
    if not validity.get("ok"):
        return validity
    result = create_calendar_event(contact=contact, config=config, text=text)
    if not result.get("ok"):
        return result  # not-connected skip or a calendar failure — pass through
    if contact is not None:
        try:
            _record_booking(contact, config or {}, text, result.get("data") or {})
        except Exception:
            logger.exception("integrations: booking CRM write failed")
    return result


def _check_conflict(contact, cfg: dict, start: datetime, minutes: int) -> bool:
    """True if `start`..`start+minutes` overlaps an existing calendar event.
    Best-effort: any failure here (not-connected, network) reports NO
    conflict — the booking guard's job is to catch a REAL double-book, not to
    invent a new way to block a booking that would otherwise succeed."""
    try:
        row, token = _google_ready("google_calendar", contact_entity(contact))
        if row is None or not token:
            return False
        calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                          or "primary")
        end = start + timedelta(minutes=minutes)
        ok, payload = _request(
            "POST", "https://www.googleapis.com/calendar/v3/freeBusy", token=token,
            json_body={"timeMin": start.isoformat(), "timeMax": end.isoformat(),
                       "items": [{"id": calendar_id}]})
        if not ok:
            return False
        busy = (((payload.get("calendars") or {}).get(calendar_id) or {}).get("busy") or [])
        return _slot_busy(start, end, _busy_spans(busy))
    except Exception:
        logger.exception("integrations: booking conflict check failed — proceeding")
        return False


def _record_booking(contact, cfg: dict, text: str, data: dict) -> None:
    """The CRM half of a booking: a `booking` Record + a `note` RecordEvent."""
    from crm.models import Record

    from .mapping import book_event

    start = _parse_when(cfg.get("eventStart"))
    title = str(cfg.get("eventTitle") or text or "Appointment")[:190]
    # Stamp the service provider (teacher/coach) this customer is bound to, so a
    # reschedule/cancel keeps the same provider. Best-effort: no binding → None.
    resource = None
    try:
        from crm import resources as res_mod

        providers = res_mod.resources_for_contact(contact)
        resource = providers[0] if providers else None
    except Exception:
        resource = None
    record = Record.objects.create(
        contact=contact,
        tenant=getattr(contact, "tenant", None),
        type="booking",
        title=title,
        status="scheduled",
        due_date=start.date(),
        resource=resource,
        data={"source": "ai", "start": start.isoformat(),
              "event_id": data.get("event_id"), "html_link": data.get("html_link"),
              "resource_id": resource.pk if resource else None,
              "resource_name": resource.name if resource else ""},
    )
    book_event(record, "note", at=start,
               data={"kind": "booking", "start": start.isoformat(),
                     "source": "ai", "event_id": data.get("event_id")})


# ---- cancel / reschedule an existing appointment ----------------------------

def _upcoming_bookings(contact) -> list:
    """(Record, event_id, start_dt) for the contact's not-cancelled future
    bookings that carry a calendar event id, soonest first. Best-effort — a
    lookup error degrades to 'no bookings' rather than breaking the reply."""
    if contact is None:
        return []
    from django.utils import timezone as dj_tz

    try:
        from crm.models import Record

        rows = Record.objects.filter(contact=contact, type="booking").exclude(
            status__in=("cancelled", "completed"))
    except Exception:
        logger.exception("integrations: booking lookup failed")
        return []
    cutoff = dj_tz.now() - timedelta(hours=2)  # keep a just-started one addressable
    out = []
    for r in rows:
        data = r.data or {}
        eid = data.get("event_id")
        start = _parse_when(data.get("start")) if data.get("start") else None
        if eid and start and start >= cutoff:
            out.append((r, eid, start))
    out.sort(key=lambda x: x[2])
    return out


def _select_booking(bookings: list, cfg: dict):
    """Pick which upcoming booking to act on: match cfg['eventId'] or a
    cfg['currentStart'] (the existing time the customer named); else the only
    one; else None (ambiguous — the caller asks which)."""
    cfg = cfg or {}
    want_id = str(cfg.get("eventId") or "").strip()
    if want_id:
        return next((b for b in bookings if b[1] == want_id), None)
    if cfg.get("currentStart"):
        target = _parse_when(cfg["currentStart"])
        near = next((b for b in bookings
                     if abs((b[2] - target).total_seconds()) <= 3600), None)
        if near:
            return near
    return bookings[0] if len(bookings) == 1 else None


def _ambiguous(bookings: list, verb: str) -> dict:
    return {"ok": False, "ambiguous": True,
            "appointments": [{"start": s.isoformat(), "title": r.title}
                             for r, _, s in bookings],
            "detail": f"more than one upcoming appointment — ask which one to {verb}",
            "hint": f"list the upcoming appointments and ask which to {verb}"}


def list_appointments(contact=None, config=None, text="") -> dict:
    """Read-only: the contact's upcoming appointments so the assistant can tell
    the customer what is on file before cancelling or moving one."""
    bookings = _upcoming_bookings(contact)
    return _done(f"{len(bookings)} upcoming appointment(s)", {
        "appointments": [{"start": s.isoformat(), "human": _slot_human(s),
                          "title": r.title} for r, _, s in bookings]})


def cancel_appointment(contact=None, config=None, text="") -> dict:
    """Cancel the contact's upcoming appointment: delete the calendar event and
    mark the booking Record cancelled. >1 upcoming → returns the list to ask."""
    cfg = config or {}
    row, token = _google_ready("google_calendar", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Calendar is not connected — step skipped.")
    bookings = _upcoming_bookings(contact)
    if not bookings:
        return {"ok": False, "detail": "no upcoming appointment on file for this contact",
                "hint": "tell the customer we have no upcoming booking recorded for them"}
    target = _select_booking(bookings, cfg)
    if target is None:
        return _ambiguous(bookings, "cancel")
    record, event_id, start = target
    calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                      or "primary")
    ok, payload = _request(
        "DELETE",
        f"https://www.googleapis.com/calendar/v3/calendars/{calendar_id}/events/{event_id}",
        token=token)
    _record_result(row, ok, "" if ok else str(payload))
    # 404/410 = already gone on the calendar; still mark the CRM cancelled so the
    # two never drift out of sync.
    if not ok and "404" not in str(payload) and "410" not in str(payload):
        return _fail(f"Google Calendar: {payload}")
    try:
        record.status = "cancelled"
        record.save()
    except Exception:
        logger.exception("integrations: cancel Record update failed")
    return {"ok": True, "cancelled": True, "start": start.isoformat(),
            "note": "appointment cancelled — confirm the cancellation to the customer"}


def reschedule_appointment(contact=None, config=None, text="") -> dict:
    """Move the contact's upcoming appointment to a new time: validate the new
    time against the booking schedule (GUARD), PATCH the calendar event and
    update the Record. >1 upcoming → returns the list to ask which."""
    cfg = config or {}
    row, token = _google_ready("google_calendar", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Calendar is not connected — step skipped.")
    new_start = _parse_when(cfg.get("eventStart")) if cfg.get("eventStart") else None
    if new_start is None:
        return _fail("no new time given — eventStart is required")
    minutes = _int(cfg.get("eventMinutes"), 30, lo=5, hi=480)
    schedule = _parse_schedule(cfg["businessHours"]) if cfg.get("businessHours") \
        else booking_schedule(contact_entity(contact))
    if not within_schedule(new_start, minutes, schedule):
        return {"ok": False, "outside_hours": True,
                "detail": "the new time is outside the bookable opening hours",
                "hours": booking_hours_raw(contact_entity(contact)),
                "hint": ("tell the customer that time is outside opening hours, then "
                         "call check_calendar_availability and offer a valid slot")}
    bookings = _upcoming_bookings(contact)
    if not bookings:
        return {"ok": False, "detail": "no upcoming appointment on file to reschedule",
                "hint": "tell the customer we have no upcoming booking; offer to make one"}
    target = _select_booking(bookings, cfg)
    if target is None:
        return _ambiguous(bookings, "move")
    record, event_id, old_start = target
    calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                      or "primary")
    body = {"start": {"dateTime": new_start.isoformat()},
            "end": {"dateTime": (new_start + timedelta(minutes=minutes)).isoformat()}}
    ok, payload = _request(
        "PATCH",
        f"https://www.googleapis.com/calendar/v3/calendars/{calendar_id}/events/{event_id}",
        token=token, json_body=body)
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Calendar: {payload}")
    try:
        data = dict(record.data or {})
        data["start"] = new_start.isoformat()
        if payload.get("htmlLink"):
            data["html_link"] = payload["htmlLink"]
        record.data = data
        record.due_date = new_start.date()
        record.save()
    except Exception:
        logger.exception("integrations: reschedule Record update failed")
    return {"ok": True, "rescheduled": True, "from": old_start.isoformat(),
            "to": new_start.isoformat(), "calendar_link": payload.get("htmlLink", ""),
            "note": "appointment moved — confirm the new time to the customer"}


def find_free_slot(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_calendar", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Calendar is not connected — step skipped.")
    calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                      or "primary")
    start = _parse_when(cfg.get("eventStart"))
    try:
        horizon = max(1, int(cfg.get("searchDays") or 7))
    except (TypeError, ValueError):
        horizon = 7
    try:
        minutes = max(5, int(cfg.get("eventMinutes") or 30))
    except (TypeError, ValueError):
        minutes = 30
    end = start + timedelta(days=horizon)
    ok, payload = _request(
        "POST", "https://www.googleapis.com/calendar/v3/freeBusy", token=token,
        json_body={"timeMin": start.isoformat(), "timeMax": end.isoformat(),
                   "items": [{"id": calendar_id}]})
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Calendar: {payload}")
    busy = (((payload.get("calendars") or {}).get(calendar_id) or {}).get("busy") or [])
    slot = _first_gap(start, end, busy, minutes)
    if slot is None:
        return _done("no free slot found in the search window", {"slot": None})
    return _done(f"free slot found at {slot.isoformat(timespec='minutes')}",
                 {"slot": slot.isoformat()})


def _parse_when(value) -> datetime:
    from django.utils import timezone as dj_tz
    from django.utils.dateparse import parse_datetime

    raw = str(value or "").strip()
    parsed = parse_datetime(raw) if raw else None
    if parsed is None:
        parsed = dj_tz.localtime() + timedelta(days=1)
    if dj_tz.is_naive(parsed):
        parsed = dj_tz.make_aware(parsed)
    return parsed


def _first_gap(start: datetime, end: datetime, busy: list, minutes: int):
    """First `minutes`-long gap not covered by a busy block. Deterministic —
    no clever heuristics, so the tests can assert on it."""
    from django.utils.dateparse import parse_datetime

    spans = []
    for b in busy:
        if not isinstance(b, dict):
            continue
        s, e = parse_datetime(str(b.get("start") or "")), parse_datetime(str(b.get("end") or ""))
        if s and e:
            spans.append((s, e))
    spans.sort()
    cursor = start
    need = timedelta(minutes=minutes)
    for s, e in spans:
        if s - cursor >= need:
            return cursor
        cursor = max(cursor, e)
    return cursor if end - cursor >= need else None


def find_free_slots(contact=None, config=None, text="", *, count: int = 3) -> dict:
    """Plural sibling of `find_free_slot`: up to `count` (default 3) open slots,
    constrained to business hours (09:00–18:00 local, configurable) and Mon–Fri.

    Reuses the same freeBusy call, then walks the business-hours grid returning
    the first N gaps that clear every busy block. Deterministic — same
    discipline as `_first_gap`, so tests can assert on it. `find_free_slot`
    stays untouched (the flow builder uses it). Data shape:
    `{"slots": [{"start": iso, "human": "...", "duration_minutes": n}, …]}`.

    A specific candidate can ALSO be checked directly via `config["requestedStart"]`
    (an ISO time) — without it, the only signal the caller has is "here are the
    earliest few open slots," and inferring that an unlisted time is therefore
    busy is wrong (it may just not be among the earliest few). The freeBusy
    window widens to cover the requested time if needed, and the response
    gains `requested_start`/`requested_available`/`requested_reason`
    ("outside_hours" | "busy" | None).
    """
    from django.utils import timezone as dj_tz

    cfg = config or {}
    row, token = _google_ready("google_calendar", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Calendar is not connected — step skipped.")
    calendar_id = str(cfg.get("calendarId") or (row.config or {}).get("calendar_id")
                      or "primary")
    minutes = _int(cfg.get("eventMinutes"), 30, lo=5, hi=480)
    requested = _parse_when(cfg["requestedStart"]) if cfg.get("requestedStart") else None
    raw_start = cfg.get("eventStart")
    if raw_start:
        start = _parse_when(raw_start)
    elif requested is not None:
        # Anchor the "alternatives" search to the DAY being asked about, not
        # "right now" — a customer asking about a specific day (especially
        # named relative to a still-unresolved "tomorrow") must get THAT
        # day's alternatives, not whichever slots happen to be earliest from
        # the current moment (which can be a completely different, unrelated
        # day — observed live: a customer asked about tomorrow and got
        # offered slots from today because the search started from "now").
        start = dj_tz.localtime(requested).replace(hour=0, minute=0, second=0, microsecond=0)
    else:
        start = dj_tz.localtime()
    horizon = _int(cfg.get("searchDays"), 7, lo=1, hi=60)
    want = _int(cfg.get("slotCount"), count, lo=1, hi=10)
    schedule = _schedule_from_cfg(cfg)
    end = start + timedelta(days=horizon)

    fetch_start, fetch_end = start, end
    if requested is not None:
        fetch_start = min(fetch_start, requested)
        fetch_end = max(fetch_end, requested + timedelta(minutes=minutes))

    ok, payload = _request(
        "POST", "https://www.googleapis.com/calendar/v3/freeBusy", token=token,
        json_body={"timeMin": fetch_start.isoformat(), "timeMax": fetch_end.isoformat(),
                   "items": [{"id": calendar_id}]})
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Calendar: {payload}")
    busy = (((payload.get("calendars") or {}).get(calendar_id) or {}).get("busy") or [])
    slots = _walk_business_slots(start, end, busy, minutes, want, schedule)
    data = {"slots": [{"start": s.isoformat(), "human": _slot_human(s),
                       "duration_minutes": minutes} for s in slots]}

    if requested is not None:
        hours_ok = within_schedule(requested, minutes, schedule)
        conflict = hours_ok and _slot_busy(
            requested, requested + timedelta(minutes=minutes), _busy_spans(busy))
        data["requested_start"] = requested.isoformat()
        data["requested_available"] = hours_ok and not conflict
        data["requested_reason"] = (
            None if data["requested_available"]
            else ("outside_hours" if not hours_ok else "busy"))

    if not slots and requested is None:
        return _done("no free business-hours slot in the search window", data)
    return _done(f"{len(slots)} free slot(s) found", data)


_DAY_NAMES = {"mon": 0, "tue": 1, "wed": 2, "thu": 3, "fri": 4, "sat": 5, "sun": 6}


def _parse_days(value) -> set:
    """Allowed weekday ints (0=Mon … 6=Sun) from a config value. Accepts
    None/'' (→ Mon–Fri), 'weekdays', 'all'/'everyday'/'daily', a list of ints,
    or a CSV of day names/numbers ('mon,tue' or '0,1,5'). Unknown → Mon–Fri."""
    if value is None or value == "":
        return {0, 1, 2, 3, 4}
    if isinstance(value, (list, tuple, set)):
        out = {int(v) for v in value if str(v).strip().isdigit() and 0 <= int(v) <= 6}
        return out or {0, 1, 2, 3, 4}
    s = str(value).strip().lower()
    if s in ("weekday", "weekdays", "mon-fri", "monfri"):
        return {0, 1, 2, 3, 4}
    if s in ("all", "everyday", "every day", "daily", "mon-sun", "week", "7"):
        return {0, 1, 2, 3, 4, 5, 6}
    out = set()
    for tok in re.split(r"[,\s]+", s):
        if not tok:
            continue
        if tok.isdigit() and 0 <= int(tok) <= 6:
            out.add(int(tok))
        elif tok[:3] in _DAY_NAMES:
            out.add(_DAY_NAMES[tok[:3]])
    return out or {0, 1, 2, 3, 4}


# ---- per-day booking schedule (operator-configurable) -----------------------
#
# The bookable window can differ per weekday (e.g. Mon–Fri 09:00–21:00, Sat
# 10:00–14:00, Sun closed). It lives in Setting `booking_hours` as a name-keyed
# map {"mon":[9,21], ... , "sat":null}; a missing/null/empty day is CLOSED.
# Both the availability suggester (find_free_slots) and the booking guard
# (within_schedule → book_appointment) read the SAME schedule, so what the bot
# offers and what it will actually book can never drift apart.

_DAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]
# Widened default: weekday evenings open, weekends closed. Override in Settings.
_DEFAULT_HOURS = {"mon": [9, 21], "tue": [9, 21], "wed": [9, 21],
                  "thu": [9, 21], "fri": [9, 21]}


def _combine_aware(date, hour: int):
    """An aware datetime at `hour`:00 local on `date`; hour==24 → next midnight."""
    from django.utils import timezone as dj_tz

    if hour >= 24:
        return dj_tz.make_aware(datetime.combine(date, dt_time(0))) + timedelta(days=1)
    return dj_tz.make_aware(datetime.combine(date, dt_time(hour)))


def _parse_schedule(value) -> dict:
    """{weekday_int:(open_h,close_h)} from a per-day map like
    {"mon":[9,18],"tue":[9,21],"sat":null}. Missing/null/empty/invalid day =
    CLOSED. Whole-hour opens 0–24. Accepts a dict or a JSON string."""
    data = value
    if isinstance(value, str):
        try:
            data = json.loads(value)
        except (ValueError, TypeError):
            return {}
    if not isinstance(data, dict):
        return {}
    out: dict = {}
    for i, key in enumerate(_DAY_KEYS):
        hrs = data.get(key)
        if not isinstance(hrs, (list, tuple)) or len(hrs) < 2:
            continue  # closed
        try:
            o, c = int(hrs[0]), int(hrs[1])
        except (TypeError, ValueError):
            continue
        o, c = max(0, min(24, o)), max(0, min(24, c))
        if o < c:
            out[i] = (o, c)
    return out


def booking_hours_raw(entity=None) -> dict:
    """The per-day booking hours (name-keyed) governing `entity`: that outlet's
    own schedule if it set one, else the organization default, else the widened
    built-in. Exception-safe.

    This is the per-outlet case in its purest form — two branches of the same
    business genuinely open at different times, and a booking offered against
    the wrong branch's hours is a real customer-facing error."""
    try:
        from crm import scoping

        raw = scoping.setting_value("booking_hours", entity)
        if raw:
            data = json.loads(raw) if isinstance(raw, str) else raw
            if isinstance(data, dict):
                return data
    except Exception:
        logger.exception("integrations: booking_hours read failed")
    return dict(_DEFAULT_HOURS)


def booking_schedule(entity=None) -> dict:
    """Parsed {weekday_int:(open,close)} of the booking hours for `entity`."""
    return _parse_schedule(booking_hours_raw(entity))


def contact_entity(contact):
    """The outlet a contact's booking belongs to — the seam that gets each
    branch's own opening hours in front of the AI. Ambiguous (multi-entity)
    contacts resolve to None, i.e. the organization default: better to quote
    the company-wide hours than to confidently quote the wrong branch's."""
    try:
        from crm import scoping

        return scoping.thread_entity(contact=contact)
    except Exception:
        logger.exception("integrations: contact_entity failed")
        return None


def _schedule_from_cfg(cfg: dict) -> dict:
    """The schedule find_free_slots should use. Precedence: an explicit
    `businessHours` map in cfg (the AI tool passes the operator's schedule) →
    the legacy uniform `businessOpen`/`businessClose`/`businessDays` (flow
    builder + existing callers, default 09:00–18:00 Mon–Fri)."""
    if cfg.get("businessHours"):
        parsed = _parse_schedule(cfg["businessHours"])
        if parsed:
            return parsed
    open_h = _int(cfg.get("businessOpen"), 9, lo=0, hi=24)
    close_h = _int(cfg.get("businessClose"), 18, lo=1, hi=24)
    days = _parse_days(cfg.get("businessDays"))
    return {d: (open_h, close_h) for d in days}


def within_schedule(start_dt, minutes: int, schedule=None) -> bool:
    """The booking GUARD: True only when a `minutes`-long appointment starting
    at `start_dt` falls entirely inside that day's open window. Closed day →
    False. `schedule` defaults to the operator's configured booking hours."""
    from django.utils import timezone as dj_tz

    if schedule is None:
        schedule = booking_schedule()
    local = dj_tz.localtime(start_dt)
    hours = schedule.get(local.weekday())
    if not hours:
        return False
    day_open = _combine_aware(local.date(), hours[0])
    day_close = _combine_aware(local.date(), hours[1])
    return day_open <= start_dt and start_dt + timedelta(minutes=minutes) <= day_close


def _int(value, default: int, *, lo: int | None = None, hi: int | None = None) -> int:
    try:
        n = int(value)
    except (TypeError, ValueError):
        n = default
    if lo is not None:
        n = max(lo, n)
    if hi is not None:
        n = min(hi, n)
    return n


def _busy_spans(busy: list) -> list:
    from django.utils.dateparse import parse_datetime

    spans = []
    for b in busy:
        if not isinstance(b, dict):
            continue
        s = parse_datetime(str(b.get("start") or ""))
        e = parse_datetime(str(b.get("end") or ""))
        if s and e:
            spans.append((s, e))
    spans.sort()
    return spans


def _slot_busy(slot_start: datetime, slot_end: datetime, spans: list) -> bool:
    return any(s < slot_end and slot_start < e for s, e in spans)


def _walk_business_slots(start: datetime, end: datetime, busy: list, minutes: int,
                         count: int, schedule: dict) -> list:
    """The first `count` `minutes`-long slots that fall inside each day's open
    window (per `schedule`: {weekday_int: (open_h, close_h)}, closed days absent)
    and clear every busy block. Deterministic: candidates step by the slot length
    from each day's opening time (aligned to the grid on day one)."""
    from django.utils import timezone as dj_tz

    spans = _busy_spans(busy)
    need = timedelta(minutes=minutes)
    results: list = []
    cur_date = dj_tz.localtime(start).date()
    last_date = dj_tz.localtime(end).date()
    while cur_date <= last_date and len(results) < count:
        hours = schedule.get(cur_date.weekday())
        if hours:
            day_open = _combine_aware(cur_date, hours[0])
            day_close = _combine_aware(cur_date, hours[1])
            cand = day_open
            if cand < start:  # first day may begin mid-session — align to the grid
                steps = math.ceil((start - day_open) / need)
                cand = day_open + steps * need
            while cand + need <= day_close and cand < end and len(results) < count:
                if not _slot_busy(cand, cand + need, spans):
                    results.append(cand)
                cand += need
        cur_date += timedelta(days=1)
    return results


def _slot_human(dt: datetime) -> str:
    from django.utils import timezone as dj_tz

    return dj_tz.localtime(dt).strftime("%a %d %b, %I:%M %p")


# ---- Google Drive ---------------------------------------------------------------

def upload_drive_file(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_drive", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Drive is not connected — step skipped.")
    filename = str(cfg.get("fileName") or "chatbranch-note.txt")[:120]
    folder = str(cfg.get("folderId") or (row.config or {}).get("folder_id") or "")
    metadata = {"name": filename}
    if folder:
        metadata["parents"] = [folder]
    # Simple two-step upload: create the file, then PATCH its media body.
    ok, created = _request(
        "POST", "https://www.googleapis.com/drive/v3/files", token=token,
        json_body=metadata)
    _record_result(row, ok, "" if ok else str(created))
    if not ok:
        return _fail(f"Google Drive: {created}")
    file_id = created.get("id")
    ok, _payload = _request(
        "PATCH", f"https://www.googleapis.com/upload/drive/v3/files/{file_id}",
        token=token, params={"uploadType": "media"},
        headers={"Content-Type": "text/plain"},
        data=str(text or "").encode("utf-8"))
    if not ok:
        return _fail(f"Google Drive upload: {_payload}")
    return _done(f"uploaded {filename} to Drive", {"file_id": file_id})


# ---- Google Sheets ----------------------------------------------------------------

def append_sheet_row(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_sheets", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Sheets is not connected — step skipped.")
    sheet_id = str(cfg.get("spreadsheetId") or (row.config or {}).get("spreadsheet_id") or "")
    if not sheet_id:
        return _skip("No spreadsheet id configured on the step or the account — skipped.")
    rng = str(cfg.get("sheetRange") or (row.config or {}).get("sheet_range") or "Sheet1!A:Z")
    from django.utils import timezone as dj_tz

    values = [[
        dj_tz.localtime().isoformat(timespec="seconds"),
        contact.name if contact is not None else "",
        contact.phone if contact is not None else "",
        contact.email if contact is not None else "",
        str(text or "")[:500],
    ]]
    ok, payload = _request(
        "POST",
        f"https://sheets.googleapis.com/v4/spreadsheets/{sheet_id}/values/{rng}:append",
        token=token, params={"valueInputOption": "USER_ENTERED"},
        json_body={"values": values})
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Sheets: {payload}")
    return _done("row appended to the sheet",
                 {"updated_range": (payload.get("updates") or {}).get("updatedRange")})


def read_sheet(contact=None, config=None, text="") -> dict:
    """READ values from a Google Sheet range (read-only). Used by the AI to look
    something up — e.g. a teacher's availability sheet named on the contact's
    Resource. `spreadsheetId` + `range` come from config; falls back to the
    account's configured sheet. Same skip/fail envelope + entity binding as the
    write actions."""
    cfg = config or {}
    row, token = _google_ready("google_sheets", contact_entity(contact))
    if row is None or not token:
        return _skip("Google Sheets is not connected — step skipped.")
    sheet_id = str(cfg.get("spreadsheetId") or (row.config or {}).get("spreadsheet_id") or "")
    if not sheet_id:
        return _skip("No spreadsheet id provided.")
    rng = str(cfg.get("range") or cfg.get("sheetRange")
              or (row.config or {}).get("sheet_range") or "Sheet1!A:Z")
    ok, payload = _request(
        "GET",
        f"https://sheets.googleapis.com/v4/spreadsheets/{sheet_id}/values/{rng}",
        token=token)
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Google Sheets: {payload}")
    return _done("sheet read", {"range": payload.get("range"),
                                "values": payload.get("values") or []})


# ---- Gmail -------------------------------------------------------------------------

def send_gmail(contact=None, config=None, text="") -> dict:
    import base64

    cfg = config or {}
    row, token = _google_ready("gmail", contact_entity(contact))
    if row is None or not token:
        return _skip("Gmail is not connected — step skipped.")
    to = str(cfg.get("to") or (contact.email if contact is not None else "")).strip()
    if not to:
        return _skip("No recipient email for this contact — Gmail step skipped.")
    subject = str(cfg.get("subject") or "A message from us")[:200]
    raw = f"To: {to}\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n{text or ''}"
    encoded = base64.urlsafe_b64encode(raw.encode("utf-8")).decode("ascii")
    ok, payload = _request(
        "POST", "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
        token=token, json_body={"raw": encoded})
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Gmail: {payload}")
    return _done(f"email sent via Gmail → {to}", {"message_id": payload.get("id")})


# ---- Shopify -----------------------------------------------------------------------

def update_order_status(contact=None, config=None, text="") -> dict:
    """Move the contact's primary order Record to a new status and book the
    RecordEvent. This half always runs — it is a CRM operation. When a Shopify
    account IS connected the same step also writes the note back to the shop."""
    from flows.engine import primary_record

    from .mapping import book_event

    cfg = config or {}
    new_status = str(cfg.get("orderStatus") or "").strip()
    if contact is None:
        return _skip("no contact on this run — order status step skipped")
    record = primary_record(contact)
    if record is None:
        return _skip("contact has no record — order status step skipped")
    old = record.status
    if new_status and new_status != old:
        record.status = new_status[:32]
        record.save(update_fields=["status", "updated_at"])
        book_event(record, "status", data={"from": old, "to": record.status,
                                           "source": "flow"})
    row = active_integration("shopify", contact_entity(contact))
    if row is None:
        return _done(f"record status {old} → {record.status} (Shopify not connected)",
                     {"record_id": record.pk, "remote": False})
    shop = str((row.config or {}).get("shop_domain") or "").strip()
    version = str((row.config or {}).get("api_version") or "2024-10").strip()
    order_id = str(record.external_id or "").strip()
    api_key = str((row.credentials or {}).get("api_key") or "")
    if not (shop and order_id and api_key):
        return _done(f"record status {old} → {record.status} "
                     "(no shop domain / order id — nothing pushed to Shopify)",
                     {"record_id": record.pk, "remote": False})
    ok, payload = _request(
        "PUT", f"https://{shop}/admin/api/{version}/orders/{order_id}.json",
        headers={"X-Shopify-Access-Token": api_key},
        json_body={"order": {"id": order_id,
                             "note": (text or f"Status: {record.status}")[:500]}})
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Shopify: {payload}")
    return _done(f"record status {old} → {record.status}, note pushed to Shopify",
                 {"record_id": record.pk, "remote": True})


# ---- Stripe ------------------------------------------------------------------------

def create_stripe_payment_link(contact=None, config=None, text="") -> dict:
    """A hosted Checkout Session for the contact's primary record amount. The
    URL lands on `record.data["payment_link"]`, and is also copied to
    `record.data["link"]` (unless one is already set) — that second key is what
    the flow builder's {{link}} variable actually renders."""
    from flows.engine import primary_record

    cfg = config or {}
    row = active_integration("stripe", contact_entity(contact))
    if row is None:
        return _skip("Stripe is not connected — step skipped.")
    api_key = str((row.credentials or {}).get("api_key") or "")
    if not api_key:
        return _skip("Stripe is not connected — step skipped.")
    record = primary_record(contact) if contact is not None else None
    amount = None
    currency = "myr"
    if record is not None and record.amount is not None:
        amount = int(round(float(record.amount) * 100))
        currency = (record.currency or "MYR").lower()
    try:
        override = cfg.get("amountCents")
        if override not in (None, ""):
            amount = int(override)
    except (TypeError, ValueError):
        pass
    if not amount or amount <= 0:
        return _skip("no amount on the record — Stripe payment link skipped")
    success_url = str(cfg.get("successUrl") or "https://example.com/thanks")
    form = {
        "mode": "payment",
        "success_url": success_url,
        "line_items[0][quantity]": "1",
        "line_items[0][price_data][currency]": currency,
        "line_items[0][price_data][unit_amount]": str(amount),
        "line_items[0][price_data][product_data][name]":
            (record.title if record is not None and record.title else
             str(cfg.get("productName") or "Payment"))[:120],
    }
    if record is not None:
        form["metadata[record_external_id]"] = str(record.external_id or "")
        form["metadata[record_type]"] = record.type
    if contact is not None and contact.email:
        form["customer_email"] = contact.email
    ok, payload = _request("POST", "https://api.stripe.com/v1/checkout/sessions",
                           headers={"Authorization": f"Bearer {api_key}"}, data=form)
    _record_result(row, ok, "" if ok else str(payload))
    if not ok:
        return _fail(f"Stripe: {payload}")
    url = payload.get("url") or ""
    if record is not None and url:
        data = dict(record.data or {})
        data["payment_link"] = url
        data.setdefault("link", url)
        record.data = data
        record.save(update_fields=["data", "updated_at"])
    return _done("Stripe payment link created", {"url": url, "session_id": payload.get("id")})


_HANDLERS = {
    "Create calendar event": create_calendar_event,
    "Find free slot": find_free_slot,
    "Upload file to Drive": upload_drive_file,
    "Append row to Sheet": append_sheet_row,
    "Send via Gmail": send_gmail,
    "Update order status": update_order_status,
    "Create Stripe payment link": create_stripe_payment_link,
}


def run_action(name: str, contact=None, config=None, text: str = "") -> dict:
    """Entry point for the flow engine. Never raises."""
    handler = _HANDLERS.get(name)
    if handler is None:
        return _skip(f"unknown integration action {name!r}")
    try:
        return handler(contact=contact, config=config, text=text)
    except Exception as e:
        logger.exception("integrations: action %r failed", name)
        return _fail(f"{name} failed: {type(e).__name__}: {e}")


def test_connection(integration) -> dict:
    """`POST /accounts/<id>/test` — one cheap live probe per provider family.
    Sandbox rows report "not connected" instead of attempting anything."""
    meta = catalog.provider_meta(integration.provider) or {}
    family = meta.get("family")
    if not integration.connected:
        return _skip(f"{_label(integration.provider)} has no credentials yet.")
    if family == "google":
        token = oauth.access_token(integration)
        if not token:
            return _fail("Stored Google credentials could not be refreshed — reconnect.")
        ok, payload = _request("GET", "https://www.googleapis.com/oauth2/v3/userinfo",
                               token=token)
        _record_result(integration, ok, "" if ok else str(payload))
        if not ok:
            return _fail(f"Google: {payload}")
        return _done(f"connected as {payload.get('email') or payload.get('sub') or 'this account'}",
                     {"email": payload.get("email", "")})
    if family == "shopify":
        shop = str((integration.config or {}).get("shop_domain") or "").strip()
        version = str((integration.config or {}).get("api_version") or "2024-10").strip()
        api_key = str((integration.credentials or {}).get("api_key") or "")
        if not shop:
            return _fail("Set the shop domain (my-store.myshopify.com) first.")
        ok, payload = _request("GET", f"https://{shop}/admin/api/{version}/shop.json",
                               headers={"X-Shopify-Access-Token": api_key})
        _record_result(integration, ok, "" if ok else str(payload))
        if not ok:
            return _fail(f"Shopify: {payload}")
        return _done(f"connected to {(payload.get('shop') or {}).get('name') or shop}")
    if family == "stripe":
        api_key = str((integration.credentials or {}).get("api_key") or "")
        ok, payload = _request("GET", "https://api.stripe.com/v1/account",
                               headers={"Authorization": f"Bearer {api_key}"})
        _record_result(integration, ok, "" if ok else str(payload))
        if not ok:
            return _fail(f"Stripe: {payload}")
        return _done(f"connected to {payload.get('id') or 'this Stripe account'}")
    return _skip("No live probe for this provider.")
