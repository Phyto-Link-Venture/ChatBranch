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

import logging
from datetime import datetime, timedelta

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


def active_integration(provider: str):
    """The connected row for a provider, or None. Prefers a healthy one so a
    revoked duplicate never shadows a working connection."""
    from .models import Integration

    rows = [r for r in Integration.objects.filter(provider=provider) if r.connected]
    if not rows:
        return None
    return (next((r for r in rows if r.status not in health.INELIGIBLE), None)
            or rows[0])


def _label(provider: str) -> str:
    return (catalog.provider_meta(provider) or {}).get("label", provider)


def _google_ready(provider: str):
    """(integration, token) or (None, "") — with the not-connected reason
    already turned into a skip by the caller."""
    row = active_integration(provider)
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
    row, token = _google_ready("google_calendar")
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


def find_free_slot(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_calendar")
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


# ---- Google Drive ---------------------------------------------------------------

def upload_drive_file(contact=None, config=None, text="") -> dict:
    cfg = config or {}
    row, token = _google_ready("google_drive")
    if row is None or not token:
        return _skip("Google Drive is not connected — step skipped.")
    filename = str(cfg.get("fileName") or "chatwood-note.txt")[:120]
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
    row, token = _google_ready("google_sheets")
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


# ---- Gmail -------------------------------------------------------------------------

def send_gmail(contact=None, config=None, text="") -> dict:
    import base64

    cfg = config or {}
    row, token = _google_ready("gmail")
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
    row = active_integration("shopify")
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
    row = active_integration("stripe")
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
