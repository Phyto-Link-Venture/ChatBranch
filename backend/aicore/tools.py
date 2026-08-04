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
default on), the AuditLog + Notification a real booking fans out, and the
DOUBLE-CONFIRMATION gate for state-changing tools (`_MUTATING`): the first
call never executes — it records a pending action and instructs the model to
ask the customer; only a later reply round (after the customer could actually
answer) may execute it by repeating the call with `confirmed=true`. Enforced
here in dispatch, not just in the prompt. Tools are only ever reached through
`generate_reply`, which the channel layers already skip for opted-out /
do-not-contact threads — so tools sit INSIDE that gate.
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
            "This is a TWO-STEP action: your first call never books — it returns "
            "an instruction to confirm with the customer. After the customer "
            "explicitly agrees in a later message, call again with confirmed=true "
            "and identical arguments. Returns a confirmation and a calendar link."
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
                "confirmed": {
                    "type": "boolean",
                    "description": (
                        "Double-confirmation flag. Omit (or false) on the first "
                        "call — nothing is booked and you will be told to ask the "
                        "customer. Set true ONLY after the customer explicitly "
                        "agreed in a LATER message, keeping the other arguments "
                        "identical."
                    ),
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
    specs.extend(_mcp_specs())
    return specs


def _mcp_specs() -> list[dict]:
    """Tools advertised by enabled MCP servers, or [] — exception-safe so a
    misbehaving server can never break the reply path."""
    try:
        from aicore import mcp_tools

        return mcp_tools.tool_specs()
    except Exception:
        logger.exception("aicore.tools: mcp tool_specs failed")
        return []


# ---- double-confirmation gate for state-changing tools ----------------------
#
# Tools listed in _MUTATING create/change/cancel something real. The customer's
# explicit go-ahead is REQUIRED and enforced here, not just requested in the
# prompt: the first call records a pending `crm.PendingToolAction` row and
# refuses with an instruction to ask; only a call from a LATER reply round (a
# different `session`, i.e. the customer actually had a chance to answer) with
# confirmed=true and identical arguments executes it. DB-backed so it survives
# restarts and works across processes/workers; the confirmed execution CONSUMES
# the row (atomic delete), so a replayed confirm can never double-fire.

_MUTATING = frozenset({"book_appointment"})
_CONFIRM_TTL_S = 30 * 60  # a "yes" older than this re-asks


def _fingerprint(args: dict) -> str:
    """Stable identity of the requested action, ignoring the confirmed flag."""
    return json.dumps({k: v for k, v in (args or {}).items() if k != "confirmed"},
                      sort_keys=True, default=str)


def _confirmation_gate(name: str, args: dict, *, contact, channel: str,
                       session=None) -> dict | None:
    """None = confirmed, proceed. Otherwise the refusal result the model reads.

    State machine per (contact, channel, tool): an unconfirmed call always
    (re)records the pending ask; confirmed=true executes ONLY against a
    pending ask with the same arguments recorded in an earlier session —
    same-session confirms and detail changes are refused, expired asks are
    re-asked. Callers wrap this in try/except and FAIL CLOSED on error."""
    if contact is None:
        return {"ok": False,
                "error": "cannot make changes for an unidentified customer",
                "hint": "offer to have the team follow up instead"}
    from datetime import timedelta

    from django.utils import timezone

    from crm.models import PendingToolAction

    chan = channel or ""
    fp = _fingerprint(args)
    now = timezone.now()
    ask = {"ok": False, "needs_confirmation": True, "action": name,
           "details": {k: v for k, v in (args or {}).items() if k != "confirmed"},
           "instruction": (
               "NOT DONE YET. Tell the customer exactly what you are about to do "
               "(include the specific details) and ask them to confirm. After "
               "they clearly agree in their NEXT message, call this tool again "
               "with confirmed=true and the same arguments.")}
    # Opportunistic prune — expired asks anywhere die here, cheap indexed delete.
    PendingToolAction.objects.filter(
        asked_at__lt=now - timedelta(seconds=_CONFIRM_TTL_S)).delete()

    def _record():
        PendingToolAction.objects.update_or_create(
            contact=contact, channel=chan, tool=name,
            defaults={"fingerprint": fp, "session": session or "",
                      "asked_at": now})

    pending = PendingToolAction.objects.filter(
        contact=contact, channel=chan, tool=name).first()
    if not args.get("confirmed"):
        _record()
        return ask
    if pending is None:              # confirmed out of nowhere — ask first
        _record()
        return ask
    if pending.fingerprint != fp:    # details changed since the ask — re-confirm
        _record()
        ask["instruction"] = (
            "NOT DONE YET — the details changed since the customer was asked. "
            "Re-state the new details, get their confirmation again, then call "
            "this tool again with confirmed=true.")
        return ask
    if session is not None and pending.session == session:
        ask["instruction"] = (
            "NOT DONE YET. You have not given the customer a chance to reply. "
            "Ask them to confirm and END this reply; when their next message "
            "clearly agrees, call this tool again with confirmed=true.")
        return ask
    # Claim by atomic delete — if another worker consumed it first, re-ask
    # instead of double-executing.
    deleted, _detail = PendingToolAction.objects.filter(
        pk=pending.pk, fingerprint=fp).delete()
    if not deleted:
        _record()
        return ask
    return None


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


def dispatch(name: str, args: dict | None = None, *, contact=None,
             channel: str = "", session=None) -> dict:
    """Run a tool by name and return a compact JSON-serializable result dict.
    Never raises — an unknown tool or a handler error becomes an `ok: False`
    result the model reads back. `session` identifies one reply-generation
    round (see _confirmation_gate); state-changing tools never execute in the
    same round that asked for confirmation."""
    args = args or {}
    if _is_mcp(name):
        return _dispatch_mcp(name, args, contact=contact, channel=channel,
                             session=session)
    fn = _DISPATCH.get(name)
    if fn is None:
        return {"ok": False, "error": f"unknown tool {name!r}"}
    if name in _MUTATING:
        refusal = _gate_or_refusal(name, args, contact=contact, channel=channel,
                                   session=session)
        if refusal is not None:
            return refusal
    try:
        return fn(args, contact=contact, channel=channel)
    except Exception as exc:  # dispatcher itself blew up — degrade to a result
        logger.exception("aicore.tools: dispatch %r failed", name)
        return {"ok": False, "error": f"{name} failed: {type(exc).__name__}"}


def _gate_or_refusal(name: str, args: dict, *, contact, channel: str, session):
    """Run the single-confirmation gate, failing CLOSED on any internal error.
    Returns the refusal result to send back, or None to proceed."""
    try:
        return _confirmation_gate(name, args, contact=contact, channel=channel,
                                  session=session)
    except Exception:  # never mutate if the gate itself broke
        logger.exception("aicore.tools: confirmation gate failed for %r", name)
        return {"ok": False, "error": "confirmation check failed — do not retry",
                "hint": "offer to have the team follow up instead"}


def _is_mcp(name: str) -> bool:
    try:
        from aicore import mcp_tools

        return mcp_tools.is_mcp_tool(name)
    except Exception:
        return False


def _dispatch_mcp(name: str, args: dict, *, contact, channel: str, session) -> dict:
    """Route a namespaced MCP tool call. Mutating MCP tools (anything not
    annotated read-only) go through the SAME single-confirmation gate as the
    built-ins; the synthetic `confirmed` flag is stripped before the arguments
    reach the server."""
    from aicore import mcp_tools

    try:
        mutating = mcp_tools.is_mutating(name)
    except Exception:  # unknown → treat as mutating (safe)
        mutating = True
    if mutating:
        refusal = _gate_or_refusal(name, args, contact=contact, channel=channel,
                                   session=session)
        if refusal is not None:
            return refusal
    call_args = {k: v for k, v in (args or {}).items() if k != "confirmed"}
    try:
        return mcp_tools.dispatch(name, call_args)
    except Exception:
        logger.exception("aicore.tools: mcp dispatch %r failed", name)
        return {"ok": False, "error": f"{name} failed",
                "hint": "offer to have the team follow up instead"}


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
