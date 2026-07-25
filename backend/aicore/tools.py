"""AI tool-calling registry — the actions the chat brain may take mid-reply.

Each tool = an OpenAI-format JSON schema + a Python dispatcher that calls into
`integrations/actions.py` and returns a compact result dict the model reads back.

Design rules (all load-bearing):

* **Additive + sandbox-safe.** `tool_specs()` returns ONLY tools whose
  integration is actually connected. With no Google Calendar linked it returns
  `[]`, so `aicore.assistant.generate_reply` behaves byte-for-byte as it does
  without tool-calling and makes NO network call — the zero-credential template
  contract. Merely offering a tool is a DB read only; the provider is contacted
  only when the model actually invokes a tool.
* **Exception-safe.** Every dispatch is wrapped: a failure becomes a tool RESULT
  the model can react to ("calendar lookup failed — offer to have the team
  confirm"), never an exception out of the reply path.
* **One tool = one schema + one dispatch entry** (`_SCHEMAS` + `_DISPATCH`), so
  adding order-lookup / payment-link tools later is a two-line change.

Guardrails live here too: the operator kill-switch (`Setting ai_tool_use`,
default on) and the AuditLog + Notification a real booking fans out. Tools are
only ever reached through `generate_reply`, which the channel layers already
skip for opted-out / do-not-contact threads — so tools sit INSIDE that gate.
"""
from __future__ import annotations

import json
import logging
import os

logger = logging.getLogger(__name__)

# ---- tool schemas (OpenAI `tools` format) -----------------------------------

_CHECK_AVAILABILITY = {
    "type": "function",
    "function": {
        "name": "check_calendar_availability",
        "description": (
            "Check the team calendar and return a list of REAL open appointment "
            "slots you can offer the customer. Call this before proposing a time "
            "so you only ever offer slots that are actually free."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "duration_minutes": {
                    "type": "integer",
                    "description": "Length of the appointment in minutes (default 30).",
                },
                "within_days": {
                    "type": "integer",
                    "description": "How many days ahead to search (default 7).",
                },
            },
        },
    },
}

_BOOK_APPOINTMENT = {
    "type": "function",
    "function": {
        "name": "book_appointment",
        "description": (
            "Book an appointment on the team calendar at a specific start time. "
            "IMPORTANT: only call this AFTER the customer has explicitly confirmed "
            "one specific slot in the conversation — never book a time they have "
            "not agreed to. Returns a confirmation and a calendar link."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "start_iso": {
                    "type": "string",
                    "description": (
                        "The appointment start time in ISO 8601 with timezone "
                        "(e.g. 2026-07-28T14:00:00+00:00). Must be a slot the "
                        "customer confirmed."
                    ),
                },
                "duration_minutes": {
                    "type": "integer",
                    "description": "Length of the appointment in minutes (default 30).",
                },
                "title": {
                    "type": "string",
                    "description": "Short title/subject for the appointment.",
                },
            },
            "required": ["start_iso"],
        },
    },
}


# ---- connectivity gates (DB-only, no network) -------------------------------

def _tool_use_enabled() -> bool:
    """Operator kill-switch — Setting `ai_tool_use` overrides the AI_TOOL_USE
    env default (on). Same Setting-over-env accessor pattern as the rest of the
    app. Fail-open to on (the default state) so a bookkeeping error never
    silently disables a connected integration."""
    try:
        from crm.models import Setting

        row = Setting.objects.filter(key="ai_tool_use").first()
        raw = row.value if row is not None else os.getenv("AI_TOOL_USE", "true")
    except Exception:
        logger.exception("aicore.tools: ai_tool_use read failed")
        return True
    return str(raw or "").strip().lower() not in ("false", "0", "no", "off", "")


def _calendar_connected() -> bool:
    """True when a usable Google Calendar integration exists. Pure DB read —
    `active_integration` never opens a socket, and we do NOT fetch an access
    token here (that happens only when a tool is actually invoked)."""
    try:
        from integrations.actions import active_integration

        return active_integration("google_calendar") is not None
    except Exception:
        logger.exception("aicore.tools: calendar connectivity check failed")
        return False


def tool_specs(contact=None, channel: str = "") -> list[dict]:
    """The OpenAI-format tool schemas for the integrations currently connected.

    Empty list = today's no-tools behaviour (also the zero-credential sandbox):
    nothing connected → nothing offered → `generate_reply` runs the exact same
    single completion it always has."""
    if not _tool_use_enabled():
        return []
    specs: list[dict] = []
    if _calendar_connected():
        specs.append(_CHECK_AVAILABILITY)
        specs.append(_BOOK_APPOINTMENT)
    return specs


# ---- dispatch ---------------------------------------------------------------

def _do_check_availability(args: dict, *, contact, channel: str) -> dict:
    from integrations import actions

    cfg: dict = {}
    if args.get("duration_minutes"):
        cfg["eventMinutes"] = args["duration_minutes"]
    if args.get("within_days"):
        cfg["searchDays"] = args["within_days"]
    res = actions.find_free_slots(contact=contact, config=cfg)
    if not res.get("ok"):
        return {"ok": False, "error": res.get("detail") or "calendar lookup failed",
                "hint": "apologise briefly and offer to have the team confirm a time"}
    slots = (res.get("data") or {}).get("slots") or []
    if not slots:
        return {"ok": True, "slots": [],
                "note": "no open slots in the search window — offer to have the "
                        "team confirm a time instead of inventing one"}
    return {"ok": True, "slots": slots}


def _do_book_appointment(args: dict, *, contact, channel: str) -> dict:
    from integrations import actions

    start_iso = str(args.get("start_iso") or "").strip()
    if not start_iso:
        return {"ok": False,
                "error": "start_iso is required — the ISO 8601 time the customer confirmed"}
    cfg: dict = {"eventStart": start_iso}
    if args.get("duration_minutes"):
        cfg["eventMinutes"] = args["duration_minutes"]
    if args.get("title"):
        cfg["eventTitle"] = args["title"]
    res = actions.book_appointment(contact=contact, config=cfg,
                                   text=str(args.get("title") or ""))
    if not res.get("ok"):
        return {"ok": False, "error": res.get("detail") or "booking failed",
                "hint": "apologise and offer to have the team confirm the appointment"}
    data = res.get("data") or {}
    _audit_booking(contact, channel, start_iso, data)
    return {"ok": True, "confirmed": True, "start": start_iso,
            "calendar_link": data.get("html_link", ""),
            "note": "appointment booked — confirm the time and details back to the customer"}


_SCHEMAS = {
    "check_calendar_availability": _CHECK_AVAILABILITY,
    "book_appointment": _BOOK_APPOINTMENT,
}

_DISPATCH = {
    "check_calendar_availability": _do_check_availability,
    "book_appointment": _do_book_appointment,
}


def dispatch(name: str, args: dict | None = None, *, contact=None, channel: str = "") -> dict:
    """Run a tool by name and return a compact JSON-serializable result dict.
    Never raises — an unknown tool or a handler error becomes an `ok: False`
    result the model reads back."""
    fn = _DISPATCH.get(name)
    if fn is None:
        return {"ok": False, "error": f"unknown tool {name!r}"}
    try:
        return fn(args or {}, contact=contact, channel=channel)
    except Exception as exc:  # dispatcher itself blew up — degrade to a result
        logger.exception("aicore.tools: dispatch %r failed", name)
        return {"ok": False, "error": f"{name} failed: {type(exc).__name__}"}


# ---- audit / notify on a real booking ---------------------------------------

def _audit_booking(contact, channel: str, start_iso: str, data: dict) -> None:
    """A successful AI booking is a real side effect — record it in the audit
    trail and notify operators. Best-effort: never breaks the reply."""
    try:
        from crm import auth as rbac

        who = getattr(contact, "name", "") or "a customer"
        target = f"contact:{contact.pk}" if contact is not None else "contact:?"
        rbac.audit(None, "ai.booking", target=target,
                   detail=f"AI booked an appointment for {who} at {start_iso} "
                          f"via {channel or 'chat'}"[:1000])
        rbac.notify(("superadmin", "admin"), "system",
                    "AI booked an appointment",
                    f"The assistant booked {who} for {start_iso}."[:500],
                    "/chats", category="admin")
    except Exception:
        logger.exception("aicore.tools: booking audit/notify failed")
