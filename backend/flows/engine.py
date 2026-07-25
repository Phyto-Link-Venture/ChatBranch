"""The flow-execution engine behind the visual flow builder (/flow-builder).

The builder saves its workflow library to crm.Workflow (React Flow JSON,
stored verbatim); this module is the only interpreter of that JSON. Active
workflows are executed automatically from two entry points:

- `tick()` — the time axis. Called by the background ticker (apps.py), the
  `manage.py flows_tick` command, and POST /api/flows/tick. Enrolls contacts
  whose Record state matches a Trigger node, resumes runs whose wait_until
  has elapsed or whose awaited event landed, and drains the outbound
  conversation queue (queue.py).
- `on_inbound_message()` — the event axis. Called (async, off the webhook ack
  path) whenever a contact message arrives on any channel; it resumes runs
  parked on a reply (Decision "Contact's answer" / edge "On contact reply").

Execution state lives in crm.FlowRun: one row per (workflow × contact)
traversal, with a per-step log for the operator. Node semantics:

  trigger  — matched against the contact's primary crm.Record: an offset from
             `due_date` ("N days before/after due"), a `status` value, or a
             Contact tag; `window`/`repeat` gate when/how often.
  stage    — visual lane only; passed through if it ever appears in a path.
  action   — renders the script's {{variables}} from the CRM and fans out to
             the step's channels (WhatsApp text/template -> the paced
             outbound conversation queue (queue.py, crm.OutboundJob — the
             pacing worker sends later, resolving the account + compliance
             gates at send time), Email send for real or sandbox, AI voice
             call, Script call = one-way "read a crm.ScriptTemplate then hang
             up" call (no conversation — distinct from AI voice call), In-app
             push = notification, SMS/Internal log = logged only — no provider
             wired). BOTH call channels honour the node's `callVia`: SIP trunk
             (calls.lk) or a linked WhatsApp number (whatsapp.baileys_calls) —
             no trunk needed for the latter. Calls are QUEUED like messages
             (kind="call" OutboundJobs, _enqueue_call): a WhatsApp number
             carries one call at a time, so the queue serialises them instead
             of letting two runs collide on the same number.
  decision — "Record status" and "Contact tag" answer from CRM data
             immediately; "Contact's answer" parks the run until the next
             inbound message, then a keyword classifier picks Yes/No.
  handover — notification fan-out to the target team's roles + audit trail.
  end      — logs the log code and completes the run.

Edge timings (edge.data.config.timing): Immediately walks on; "Wait N days"
parks with wait_until; "On contact reply"/"On record update" park with
wait_for; "No answer / timeout" edges are only taken when nothing else leaves
the node. Provider modules (whatsapp/messaging/calls.lk) are imported INSIDE
functions — same lazy-import rule as the rest of the repo, and it keeps this
module import-light for the ticker thread.
"""
from __future__ import annotations

import logging
import os
import re
import threading
import time
from datetime import date, timedelta

from django.db import close_old_connections
from django.utils import timezone

from crm import auth as rbac
from crm.models import AgentSkill, Contact, FlowRun, ScriptTemplate, Thread, Workflow

logger = logging.getLogger(__name__)

MAX_STEPS = 60      # loop guard: hard cap on nodes walked per advance
LOG_CAP = 200       # per-run log entries kept (oldest dropped)
# Background ticker cadence. Every tick resumes due runs, evaluates time
# triggers, and drains the outbound queue, so this is also how fast a flow
# reminder / a queued call goes out and how fast a parked run resumes. 10s
# (down from 60s) makes reminders feel prompt; override with FLOWS_TICK_SECONDS.
# evaluate_triggers is idempotent (_already_ran dedupes) so a fast cadence is
# safe — it just polls more often on a POC-sized contact set.
TICK_SECONDS = max(1, int(os.getenv("FLOWS_TICK_SECONDS", "10") or 10))


# ---- graph helpers ---------------------------------------------------------------

def _nodes_by_id(wf: Workflow) -> dict:
    return {n["id"]: n for n in (wf.nodes or []) if isinstance(n, dict) and n.get("id")}


def _out_edges(wf: Workflow, node_id: str) -> list[dict]:
    return [e for e in (wf.edges or []) if isinstance(e, dict) and e.get("source") == node_id]


def _cfg(node: dict) -> dict:
    return (node.get("data") or {}).get("config") or {}


def _label(node: dict) -> str:
    return str((node.get("data") or {}).get("label") or node.get("id") or "")


def _edge_cfg(edge: dict) -> dict:
    return (edge.get("data") or {}).get("config") or {}


def _log(run: FlowRun, node_id: str, event: str) -> None:
    entry = {"t": timezone.now().isoformat(timespec="seconds"),
             "node": node_id, "event": event[:300]}
    run.log = list(run.log or [])[-(LOG_CAP - 1):] + [entry]


# ---- {{variable}} rendering ------------------------------------------------------

_VAR_RE = re.compile(r"\{\{\s*([a-z0-9_]+)\s*\}\}")


def _money(value, currency: str = "MYR") -> str:
    try:
        return f"{currency} {float(value):,.2f}"
    except (TypeError, ValueError):
        return str(value or "")


_ONES = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
         "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
         "seventeen", "eighteen", "nineteen"]
_TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def _int_to_words(n: int) -> str:
    if n < 20:
        return _ONES[n]
    if n < 100:
        tens, rest = divmod(n, 10)
        return _TENS[tens] + (f"-{_ONES[rest]}" if rest else "")
    if n < 1000:
        hundreds, rest = divmod(n, 100)
        return f"{_ONES[hundreds]} hundred" + (f" {_int_to_words(rest)}" if rest else "")
    for divisor, name in ((10**9, "billion"), (10**6, "million"), (10**3, "thousand")):
        if n >= divisor:
            hi, rest = divmod(n, divisor)
            return f"{_int_to_words(hi)} {name}" + (f" {_int_to_words(rest)}" if rest else "")
    return str(n)  # unreachable — the ladder above covers every n >= 1000


def amount_to_words(value, currency: str = "ringgit") -> str:
    """Currency amount spelled out for TTS — "6900" -> "six thousand nine
    hundred ringgit" instead of a literal-read engine sounding out raw digits
    or a stray decimal ("21500.0" -> "two one five zero zero point zero").
    Cents are only mentioned when non-zero."""
    try:
        amount = float(value)
    except (TypeError, ValueError):
        return str(value or "")
    whole = int(amount)
    cents = round((amount - whole) * 100)
    out = f"{_int_to_words(whole)} {currency}"
    if cents:
        out += f" and {_int_to_words(cents)} cents"
    return out


# Business-level {{variables}}: operator-editable crm.Setting rows so a
# deployment brands its own messages without touching code. Missing rows fall
# back to a neutral value rather than leaving the placeholder visible.
BUSINESS_VARS = {
    "business_name": "our team",
    "agent_name": "",
    "support_number": "",
    "support_email": "",
    "website": "",
}

# Record statuses that mean "nothing more to do here" — used by trigger
# scoping, the Record-status decision and the primary-record pick.
CLOSED_RECORD_STATUSES = ("closed", "done", "resolved", "cancelled", "completed")


def _business_vars() -> dict:
    from crm.models import Setting

    out = dict(BUSINESS_VARS)
    rows = Setting.objects.filter(key__in=list(BUSINESS_VARS)).values_list("key", "value")
    for key, value in rows:
        if (value or "").strip():
            out[key] = value.strip()
    return out


def primary_record(contact: Contact | None):
    """The Record a flow run is about: the first still-open one, else the most
    recent of any. One record per conversation is the platform's assumption —
    verticals that need several run one flow per record."""
    if contact is None:
        return None
    return (contact.records.exclude(status__in=CLOSED_RECORD_STATUSES).first()
            or contact.records.first())


def variables_for(contact: Contact | None, record=None) -> dict:
    """The subset of the builder's variable catalog (lib/flow/variables.ts)
    the CRM can actually fill. Unknown {{keys}} stay visible in the text so
    a missing mapping is obvious rather than silently blank."""
    if contact is None:
        return {}
    record = record if record is not None else primary_record(contact)
    today = date.today()
    due = record.due_date if record is not None else None
    delta = (due - today).days if due else None
    data = (record.data or {}) if record is not None else {}
    currency = record.currency if record is not None else "MYR"
    memory = _memory_vars(contact)
    return {
        # ---- contact ----
        "name": contact.name,
        "first_name": (contact.name or "").split(" ")[0],
        "phone": contact.phone,
        "email": contact.email,
        "language": contact.language,
        "company": contact.company_name or "our company",
        # ---- record ----
        "record_id": record.external_id if record is not None else "",
        "record_type": record.type if record is not None else "",
        "record_status": record.status if record is not None else "",
        "record_title": record.title if record is not None else "",
        "amount": _money(record.amount, currency) if record is not None else "",
        "currency": currency,
        "due_date": due.strftime("%d %b %Y") if due else "",
        "days_until_due": str(delta) if delta is not None and delta >= 0 else "",
        "days_overdue": str(-delta) if delta is not None and delta < 0 else "0",
        "tracking_url": str(data.get("tracking_url") or ""),
        "link": str(data.get("link") or data.get("url") or ""),
        # ---- business ----
        **_business_vars(),
        # ---- memory ----
        **memory,
        # ---- free-form ----
        "situation": contact.notes,
    }


def _memory_vars(contact: Contact) -> dict:
    last = contact.conversation_summaries.values_list("created_at", flat=True).first()
    return {
        "best_contact_time": contact.preferred_contact_time or "",
        "preferred_channel": str((contact.attributes or {}).get("preferred_channel") or ""),
        "last_contact_date": last.strftime("%d %b %Y") if last else "",
    }


def variables_for_speech(contact: Contact | None, record=None) -> dict:
    """variables_for(), with currency amounts spelled out for TTS — a script
    call reads {{amount}} verbatim, so it needs "six thousand nine hundred
    ringgit", not the "MYR 6,900.00" digits a text channel would show."""
    record = record if record is not None else primary_record(contact)
    v = variables_for(contact, record)
    if contact is None or record is None or record.amount is None:
        return v
    v["amount"] = amount_to_words(record.amount)
    return v


def render(text: str, variables: dict) -> str:
    return _VAR_RE.sub(lambda m: str(variables.get(m.group(1), m.group(0))), text or "")


# ---- trigger matching ------------------------------------------------------------

# Exact option strings from the builder (lib/flow/nodeConfig.ts) — en dashes.
_WINDOWS = {"9:00–12:00": (9, 12), "12:00–15:00": (12, 15), "15:00–18:00": (15, 18)}


def _in_window(window: str) -> bool:
    span = _WINDOWS.get(window or "")
    if not span:
        return True  # "Any allowed hours" / unknown label
    hour = timezone.localtime().hour
    return span[0] <= hour < span[1]


def _trigger_matches(cfg: dict, contact: Contact) -> bool:
    """Match a Trigger node against a contact.

    Time triggers work off the contact's primary Record `due_date` (delivery
    ETA, invoice due date, appointment time — whatever "when" means for that
    record type). `od` below is days PAST that date: positive = overdue,
    0 = due today, negative = still ahead of it. Two non-time triggers exist
    too: a record status match and a contact tag match."""
    when = str(cfg.get("when") or "Days after due date")

    if when == "Record status is":
        record = primary_record(contact)
        wanted = str(cfg.get("status") or "").strip().lower()
        return bool(record and wanted and record.status.strip().lower() == wanted)
    if when == "Contact has tag":
        wanted = str(cfg.get("tag") or "").strip().lower()
        tags = {str(t).strip().lower() for t in (contact.tags or [])}
        return bool(wanted and wanted in tags)

    record = primary_record(contact)
    if record is None or record.due_date is None:
        return False
    try:
        days = int(cfg.get("days") or 0)
    except (TypeError, ValueError):
        days = 0
    od = (date.today() - record.due_date).days
    if when == "On the due date":
        return od == 0
    if when == "Days before due date":
        return od == -abs(days or 1)
    if when == "Days after due date (exactly)":
        # pinpoint targeting: fires only on THE day (or, with several such
        # triggers, on the exact days) — nothing enrolls before or after
        return od == (days or 1)
    return od >= (days or 1)


def _already_ran(wf: Workflow, node_id: str, contact: Contact, repeat: str) -> bool:
    """Repeat semantics double as enrollment dedupe — a trigger never starts a
    second run while one is still in flight."""
    qs = FlowRun.objects.filter(workflow=wf, trigger_key=node_id, contact=contact)
    if qs.filter(status__in=("running", "waiting")).exists():
        return True
    if repeat == "Daily until answered":
        if qs.filter(context__answered=True).exists():
            return True
        return qs.filter(started_at__date=timezone.localdate()).exists()
    if repeat == "Every 2 days":
        return qs.filter(started_at__gte=timezone.now() - timedelta(days=2)).exists()
    return qs.exists()  # "Once"


def evaluate_triggers() -> int:
    """Enroll matching contacts into every active workflow. Returns the
    number of runs started."""
    started = 0
    for wf in Workflow.objects.filter(active=True):
        triggers = [n for n in (wf.nodes or [])
                    if isinstance(n, dict) and n.get("type") == "trigger"]
        for node in triggers:
            cfg = _cfg(node)
            if not _in_window(str(cfg.get("window") or "")):
                continue
            repeat = str(cfg.get("repeat") or "Once")
            # Optional contact scope (builder field "Apply to contacts"):
            # empty or absent = every contact, which keeps every pre-existing
            # flow behaving exactly as before. A non-empty list pilots the flow
            # on just those contacts. Ids arrive as strings from the React Flow
            # JSON, so compare as strings rather than coercing to int.
            only = cfg.get("contacts")
            only = {str(x) for x in only if str(x).strip()} if isinstance(only, list) else set()
            for contact in Contact.objects.exclude(status="resolved"):
                if only and str(contact.pk) not in only:
                    continue
                if not _trigger_matches(cfg, contact):
                    continue
                if _already_ran(wf, node["id"], contact, repeat):
                    continue
                run = FlowRun.objects.create(
                    workflow=wf, contact=contact,
                    trigger_key=node["id"], current_node=node["id"],
                )
                _log(run, node["id"],
                     f"trigger fired: {cfg.get('when')} days={cfg.get('days')} ({repeat})")
                rbac.audit(None, "flow.start", target=wf.name,
                           detail=f"{contact.name} via trigger {_label(node)!r}")
                _advance(run)
                started += 1
    return started


# ---- node execution --------------------------------------------------------------

_YES_RE = re.compile(
    r"\b(yes|ya|yeah|yup|ok|okay|sure|can|will|paid|done|sudah|boleh|setuju|bayar|alright|noted)\b",
    re.I)
_NO_RE = re.compile(
    r"\b(no|not|nope|cannot|can't|cant|tak|tidak|later|busy|belum|tunggu)\b", re.I)


def classify_reply(text: str) -> str:
    """Deterministic Yes/No for Decision "Contact's answer" — earliest keyword
    wins, unmatched text falls to No (the follow-up path is the safe default)."""
    yes, no = _YES_RE.search(text or ""), _NO_RE.search(text or "")
    if yes and no:
        return "yes" if yes.start() < no.start() else "no"
    return "yes" if yes else "no"


def _decide(run: FlowRun, node: dict) -> str | None:
    """Answer a Decision node ('yes'/'no'), or None to park for a reply."""
    cfg = _cfg(node)
    basis = str(cfg.get("basedOn") or "Contact's answer")
    contact = run.contact
    if basis == "Record status":
        # "is it done?" — a closed record walks the Yes path. An explicit
        # config.status narrows it to one exact value.
        record = primary_record(contact)
        wanted = str(cfg.get("status") or "").strip().lower()
        if wanted:
            answer = "yes" if (record and record.status.strip().lower() == wanted) else "no"
        else:
            answer = "yes" if (record and record.status.strip().lower()
                               in CLOSED_RECORD_STATUSES) else "no"
        _log(run, node["id"], f"decision (record status): {answer}")
        return answer
    if basis == "Contact tag":
        wanted = str(cfg.get("tag") or "").strip().lower()
        tags = {str(t).strip().lower() for t in ((contact.tags or []) if contact else [])}
        answer = "yes" if (wanted and wanted in tags) else "no"
        _log(run, node["id"], f"decision (contact tag {wanted!r}): {answer}")
        return answer
    context = run.context or {}
    reply = context.get("last_reply")
    if reply and not context.get("reply_consumed"):
        context["reply_consumed"] = True
        run.context = context
        answer = classify_reply(str(reply))
        _log(run, node["id"], f"decision (reply {str(reply)[:60]!r}): {answer}")
        return answer
    _log(run, node["id"], "parked: waiting for contact reply")
    return None


def _fanout(cfg: dict) -> bool:
    """The Action node's "Send to" switch: all linked identities vs the CRM
    primary contact. config.allLinked is the channel-generic key; waAllNumbers
    is honoured for flows saved while the switch was WhatsApp-only."""
    return bool(cfg.get("allLinked") or cfg.get("waAllNumbers"))


def _wa_targets(contact: Contact, cfg: dict) -> list[str]:
    """The WhatsApp numbers an Action node sends to: the CRM primary phone —
    plus, when the node opts in (_fanout, the builder's "Send to" switch),
    every WhatsApp thread linked to the contact. Deduped on the normalized
    wa_id, so a linked thread that IS the primary number never doubles the
    send."""
    from whatsapp import store as wa_store  # lazy: keeps engine import-light

    targets: list[str] = []
    seen: set[str] = set()

    def add(raw: str) -> None:
        digits = wa_store.normalize_wa_id(raw or "")
        if digits and digits not in seen:
            seen.add(digits)
            targets.append(raw)  # queue normalizes at send; keep the raw form

    add(contact.phone)
    if _fanout(cfg):
        for key in (Thread.objects.filter(channel="whatsapp", contact=contact)
                    .order_by("pk").values_list("key", flat=True)):
            add(key)
    return targets


def _email_targets(contact: Contact, cfg: dict) -> list[str]:
    """Same contract for email: the CRM address, plus every linked email
    thread when the node's "Send to" switch is on all-linked. Deduped
    case-insensitively."""
    targets: list[str] = []
    seen: set[str] = set()

    def add(raw: str) -> None:
        addr = str(raw or "").strip()
        if addr and "@" in addr and addr.lower() not in seen:
            seen.add(addr.lower())
            targets.append(addr)

    add(contact.email)
    if _fanout(cfg):
        for key in (Thread.objects.filter(channel="email", contact=contact)
                    .order_by("pk").values_list("key", flat=True)):
            add(key)
    return targets


def _queue_whatsapp(run: FlowRun, node: dict, contact: Contact | None,
                    cfg: dict, script: str) -> None:
    """Enqueue the WhatsApp send onto the outbound conversation queue
    (queue.py) instead of sending inline. The queue resolves the account at
    send time (routing + health failover) and applies pacing, quiet hours,
    opt-out and the 24h-window rule THERE — template jobs deliberately pass a
    closed window (that is what templates are for), so no window guard here.

    Node contract (components/flow/WaTemplateConfig.tsx): config.waMode
    "text"|"template"; template mode carries templateName / templateLanguage /
    templateVariables ({"1": "{{name}}", …} — values rendered through the
    variables_for catalog before enqueueing); config.allLinked (legacy
    waAllNumbers) fans the send out to every linked WhatsApp number instead
    of just the primary (each number is its own job — the queue's per-thread
    compliance gates still apply per number)."""
    if contact is None:
        _log(run, node["id"], "whatsapp skipped: no contact")
        return
    targets = _wa_targets(contact, cfg)
    if not targets:
        _log(run, node["id"], "whatsapp skipped: no contact phone")
        return
    from . import queue as outbound_queue  # lazy: keeps engine import-light
    mode = str(cfg.get("waMode") or "text").strip().lower()
    template_name = str(cfg.get("templateName") or "").strip()
    if mode == "template" and template_name:
        variables = variables_for(contact)
        raw = cfg.get("templateVariables")
        rendered = ({str(k): render(str(v), variables) for k, v in raw.items()}
                    if isinstance(raw, dict) else {})
        kind = "template"
        payload = {"name": template_name,
                   "language": str(cfg.get("templateLanguage") or "").strip() or "en",
                   "variables": rendered}
    else:
        if not script:
            _log(run, node["id"], "whatsapp skipped: empty script")
            return
        kind, payload = "text", {"text": script}
    for to in targets:
        job = outbound_queue.enqueue("whatsapp", kind, contact, to,
                                     payload, flow_run=run, flow_node=node["id"])
        _log(run, node["id"], f"whatsapp queued ({kind}, job #{job.pk} → {to})")


def _send_email(run: FlowRun, node: dict, contact: Contact | None,
                cfg: dict, text: str) -> None:
    if contact is None:
        _log(run, node["id"], "email skipped: no contact")
        return
    targets = _email_targets(contact, cfg)
    if not targets:
        _log(run, node["id"], "email skipped: no email address")
        return
    if not text:
        _log(run, node["id"], "email skipped: empty script")
        return
    from messaging import email_client, msg_config
    from messaging import store as msg_store
    for addr in targets:
        msg_store.get_or_create_thread("email", addr, name=contact.name)
        if msg_config.email_configured():
            try:
                msg_id = email_client.send_email(addr, text)
                msg_store.append_message("email", addr, "out", text,
                                         source="ai", msg_id=msg_id)
                _log(run, node["id"], f"email sent → {addr}")
            except Exception as e:
                msg_store.append_message("email", addr, "out",
                                         f"[send failed: {e}]", source="system",
                                         status="failed")
                _log(run, node["id"], f"email send failed ({addr}): {e}")
        else:
            msg_store.append_message("email", addr, "out", text,
                                     source="ai", status="sandbox")
            _log(run, node["id"], f"email sent (sandbox) → {addr}")


def _send_telegram(run: FlowRun, node: dict, contact: Contact | None,
                   text: str) -> None:
    """Text every Telegram chat linked to the contact. Telegram has no phone
    addressing — a chat id only exists once the contact has messaged the bot
    and the thread was linked — so no linked chat = a logged skip, never an
    error. Live send when a bot account is configured, sandbox store
    otherwise (same contract as email)."""
    if contact is None:
        _log(run, node["id"], "telegram skipped: no contact")
        return
    if not text:
        _log(run, node["id"], "telegram skipped: empty script")
        return
    keys = list(Thread.objects.filter(channel="telegram", contact=contact)
                .order_by("pk").values_list("key", flat=True))
    if not keys:
        _log(run, node["id"], "telegram skipped: no linked telegram chat")
        return
    from messaging import msg_config, telegram_client
    from messaging import store as msg_store
    account = msg_config.find_account_for_channel("telegram")
    for key in keys:
        if account:
            try:
                msg_id = telegram_client.send_message(account, key, text)
                msg_store.append_message("telegram", key, "out", text,
                                         source="ai", msg_id=msg_id)
                _log(run, node["id"], f"telegram sent → {key}")
            except Exception as e:
                msg_store.append_message("telegram", key, "out",
                                         f"[send failed: {e}]", source="system",
                                         status="failed")
                _log(run, node["id"], f"telegram send failed ({key}): {e}")
        else:
            msg_store.append_message("telegram", key, "out", text,
                                     source="ai", status="sandbox")
            _log(run, node["id"], f"telegram sent (sandbox) → {key}")


def _flow_call_voice(cfg: dict, contact: Contact | None = None) -> tuple[str, str]:
    """(voice_provider, voice_id) for an AI-voice-call step. Precedence: the
    Action node's `voice` config (an explicit per-step choice always wins) →
    the contact's own remembered voice (data/memory/{id}.md, set after any
    manual call where the operator picked one) → the global Setting
    `flow_call_voice` → ("", "") = the agent's native realtime voice. Mirrors
    the /chats manual picker, which sends voice_provider="elevenlabs" + the id."""
    from crm.models import Setting

    voice_id = str(cfg.get("voice") or "").strip()
    if not voice_id and contact is not None:
        from calls import memory as memory_mod
        mem = memory_mod.load_memory(contact.pk)
        if (mem.get("preferred_voice_provider") or "") == "elevenlabs":
            voice_id = (mem.get("preferred_voice") or "").strip()
    if not voice_id:
        row = Setting.objects.filter(key="flow_call_voice").first()
        voice_id = (row.value or "").strip() if row else ""
    return ("elevenlabs", voice_id) if voice_id else ("", "")


def _script_call_voice(cfg: dict, contact: Contact | None) -> tuple[str, str]:
    """_flow_call_voice() for a SCRIPT call, which needs literal TTS. Native
    realtime models have no standalone speech synthesis — asked to read a
    script verbatim via generate_reply they have been observed to produce no
    audible speech at all — so when nothing above picked a voice, auto-pick any
    ready ElevenLabs clone rather than fall through to that silent path."""
    voice_provider, voice_id = _flow_call_voice(cfg, contact)
    if not voice_provider:
        from calls import voices as voices_mod
        auto_voice = voices_mod.default_elevenlabs_voice()
        if auto_voice:
            voice_provider, voice_id = "elevenlabs", auto_voice
    return voice_provider, voice_id


def _enqueue_call(run: FlowRun, node: dict, contact: Contact, cfg: dict,
                  script: str = "", extra: dict | None = None,
                  label: str = "AI call") -> None:
    """Put the dial on the outbound queue (queue.py) instead of ringing inline.

    A WhatsApp number carries exactly ONE call at a time and the gateway 502s a
    second dial, so two runs reaching their call step in the same tick used to
    collide: the loser recorded a permanent step failure and then sat parked on
    a reply that could only have followed the call it never got. Queued, the
    second call waits for the first to hang up.

    Transport comes from the node's `callVia`. The ACCOUNT deliberately does
    not: the queue resolves it at dial time (routing + health failover), the
    same rule message jobs follow, so a failover between enqueue and dial is
    honoured. A non-empty `script` (already rendered) makes it a one-way
    read-and-hang-up call and forces a literal-TTS voice."""
    from . import queue as outbound_queue  # lazy: keeps engine import-light

    whatsapp = str(cfg.get("callVia") or "").lower().startswith("whatsapp")
    transport = "baileys" if whatsapp else "sip"
    voice_provider, voice_id = (_script_call_voice(cfg, contact) if script
                                else _flow_call_voice(cfg, contact))
    job = outbound_queue.enqueue(
        "whatsapp" if whatsapp else "voice", "call", contact, contact.phone,
        {"transport": transport, "label": label, "script": script,
         "voice_provider": voice_provider, "voice": voice_id,
         "extra": extra or {}},
        flow_run=run, flow_node=node["id"])
    voice_label = f" [voice {voice_id}]" if voice_id else " [native voice]"
    _log(run, node["id"],
         f"{label} queued via {'WhatsApp' if whatsapp else 'SIP'} "
         f"(job #{job.pk}){voice_label}")


def _dial(run: FlowRun, node: dict, contact: Contact | None,
          cfg: dict, script: str) -> None:
    """Full two-way AI conversation call. `script` here is only the Action
    node's shared multi-channel message body (used for its WhatsApp/Email/
    Telegram siblings) — it is NOT sent to the agent: dispatch metadata's
    `script` key means "read verbatim, then hang up" (agent.py), which would
    silently turn this into a one-way call any time an operator also filled
    in the shared script field. Use the "Script call" channel (_dial_script)
    for that instead."""
    if contact is None or not contact.phone:
        _log(run, node["id"], "call skipped: no contact phone")
        return
    if contact.do_not_call:
        _log(run, node["id"], "call skipped: do-not-call flag")
        return
    extra = {"flow": run.workflow.name, "flow_node": node["id"]}
    skill = AgentSkill.objects.filter(name=str(cfg.get("skill") or "")).first()
    if skill and skill.instructions:
        extra["skill_instructions"] = skill.instructions[:2000]
    _enqueue_call(run, node, contact, cfg, extra=extra, label="AI call")


def _dial_script(run: FlowRun, node: dict, contact: Contact | None, cfg: dict) -> None:
    """One-way "read and hang up" call: the Action node's `scriptTemplateId`
    selects a crm.ScriptTemplate, rendered with this contact's CRM variables
    and sent verbatim (agent.py `script` dispatch key — no LLM conversation,
    no listening). Voice comes from the node's `voice` config, same as the
    other call channels.

    Honours the node's `callVia` exactly like the AI-voice-call channel, so a
    script call can ride a linked WhatsApp number instead of the SIP trunk."""
    if contact is None or not contact.phone:
        _log(run, node["id"], "script call skipped: no contact phone")
        return
    if contact.do_not_call:
        _log(run, node["id"], "script call skipped: do-not-call flag")
        return
    template_id = cfg.get("scriptTemplateId")
    template = ScriptTemplate.objects.filter(pk=template_id).first() if template_id else None
    if template is None:
        _log(run, node["id"], "script call skipped: no script template selected")
        return
    spoken = render(template.content, variables_for_speech(contact))[:2000]
    _enqueue_call(run, node, contact, cfg, script=spoken,
                  extra={"flow": run.workflow.name, "flow_node": node["id"]},
                  label=f"Script call ({template.name})")


def _integration_actions() -> dict:
    """The integration action labels the builder can put in `channels`.
    Imported lazily (same rule as every provider module) so the ticker thread
    never pulls the integrations app at engine import time."""
    try:
        from integrations.actions import ACTIONS
        return ACTIONS
    except Exception:  # app missing / mid-migration — the step just logs
        logger.exception("flows: integration action catalog unavailable")
        return {}


def _do_integration(run: FlowRun, node: dict, contact: Contact | None,
                    cfg: dict, script: str, action: str) -> None:
    """Run one third-party action (Google Calendar / Drive / Sheets / Gmail /
    Shopify / Stripe). `run_action` never raises and returns `skipped=True`
    when nothing is connected — the template's zero-credentials default — so an
    unconfigured deployment logs a clear skip instead of failing the step."""
    from integrations import actions as integration_actions

    result = integration_actions.run_action(action, contact=contact, config=cfg,
                                            text=script)
    _log(run, node["id"], f"{action}: {result.get('detail') or 'done'}")
    context = run.context or {}
    log = list(context.get("integrations") or [])[-19:]
    log.append({"action": action, "ok": bool(result.get("ok")),
                "skipped": bool(result.get("skipped")),
                "detail": str(result.get("detail") or "")[:200]})
    context["integrations"] = log
    if result.get("ok") and result.get("data"):
        context.setdefault("integration_data", {})[action] = result["data"]
    run.context = context


def _do_action(run: FlowRun, node: dict) -> bool:
    """Run an Action node's channels. Returns False when postponed (quiet
    hours) — the run parks and re-executes this node on resume."""
    cfg = _cfg(node)
    contact = run.contact
    script = render(str(cfg.get("script") or ""), variables_for(contact))
    channels = cfg.get("channels") or []
    if isinstance(channels, str):
        channels = [channels]
    if any(ch in ("WhatsApp", "SMS", "Email", "Telegram") for ch in channels):
        from whatsapp import wa_config
        if wa_config.in_quiet_hours():
            run.status = "waiting"
            run.wait_until = timezone.now() + timedelta(minutes=30)
            _log(run, node["id"], "quiet hours — action postponed 30min")
            return False
    for ch in channels:
        try:
            if ch == "WhatsApp":
                _queue_whatsapp(run, node, contact, cfg, script)
            elif ch == "Email":
                _send_email(run, node, contact, cfg, script)
            elif ch == "Telegram":
                _send_telegram(run, node, contact, script)
            elif ch == "AI voice call":
                _dial(run, node, contact, cfg, script)
            elif ch == "Script call":
                _dial_script(run, node, contact, cfg)
            elif ch == "In-app push":
                rbac.notify(("superadmin", "admin", "agent"), "system",
                            f"Flow push · {contact.name if contact else 'contact'}",
                            (script or _label(node))[:400], category="flows")
                _log(run, node["id"], "in-app push sent")
            elif ch in _integration_actions():
                _do_integration(run, node, contact, cfg, script, ch)
            else:  # SMS / Internal log / future channels — no provider wired
                _log(run, node["id"], f"{ch}: logged (no provider) — {script[:80]}")
        except Exception as e:  # one channel failing must not kill the step
            logger.exception("flows: %s channel failed on run %s", ch, run.pk)
            _log(run, node["id"], f"{ch} failed: {e}")
    rbac.audit(None, "flow.action", target=run.workflow.name,
               detail=f"{_label(node)} → {', '.join(channels) or 'no channels'}")
    return True


_TEAM_ROLES = {
    "Senior agent": ("superadmin", "admin", "agent"),
    "Compliance": ("superadmin", "admin"),
    "Legal": ("superadmin", "admin"),
    "Manager": ("superadmin", "admin"),
}


def _do_handover(run: FlowRun, node: dict) -> None:
    cfg = _cfg(node)
    contact = run.contact
    team = str(cfg.get("team") or "Senior agent")
    priority = str(cfg.get("priority") or "Normal")
    reason = render(str(cfg.get("reason") or ""), variables_for(contact)) or _label(node)
    title = f"Flow handover → {team}: {contact.name if contact else 'unknown contact'}"
    rbac.notify(_TEAM_ROLES.get(team, ("superadmin", "admin")), "system",
                title, f"[{priority}] {reason}", link="/contacts", category="flows")
    rbac.audit(None, "flow.handover", target=run.workflow.name,
               detail=f"{title} ({priority})")
    context = run.context or {}
    context["handover"] = team
    run.context = context
    _log(run, node["id"], f"handover → {team} ({priority})")


def _finish(run: FlowRun, status: str, detail: str = "") -> None:
    run.status = status
    run.wait_for = ""
    run.wait_until = None
    run.finished_at = timezone.now()
    if detail:
        _log(run, run.current_node, f"{status}: {detail}")
    run.save()


def _do_end(run: FlowRun, node: dict) -> None:
    code = str(_cfg(node).get("logCode") or "").strip() or "END"
    rbac.audit(None, "flow.end", target=run.workflow.name,
               detail=f"{run.contact.name if run.contact else '?'} → {code}")
    _finish(run, "done", code)


# ---- the walk --------------------------------------------------------------------

def _pick_edge(wf: Workflow, node: dict, branch: str | None) -> dict | None:
    edges = _out_edges(wf, node["id"])
    if not edges:
        return None
    if branch:  # decision: match the Yes/No dot or the edge label
        for e in edges:
            handle = str(e.get("sourceHandle") or "").lower()
            label = str(e.get("label") or _edge_cfg(e).get("label") or "").strip().lower()
            if handle == branch or label.startswith(branch):
                return e
        return edges[0]
    normal = [e for e in edges if _edge_cfg(e).get("timing") != "No answer / timeout"]
    return (normal or edges)[0]


def _apply_edge(run: FlowRun, edge: dict) -> bool:
    """Move the run onto edge.target. Returns False when the edge parks the
    run (wait N days / on reply / on record update)."""
    cfg = _edge_cfg(edge)
    timing = str(cfg.get("timing") or "Immediately")
    run.current_node = str(edge.get("target") or "")
    if timing == "Wait N days":
        try:
            days = float(cfg.get("waitDays") or 0)
        except (TypeError, ValueError):
            days = 0.0
        run.status = "waiting"
        run.wait_until = timezone.now() + timedelta(days=days)
        _log(run, run.current_node, f"parked: waiting {days:g}d")
        run.save()
        return False
    if timing == "On contact reply":
        run.status, run.wait_for = "waiting", "reply"
        _log(run, run.current_node, "parked: waiting for contact reply")
        run.save()
        return False
    if timing == "On record update":
        run.status, run.wait_for = "waiting", "record"
        _log(run, run.current_node, "parked: waiting for a record update")
        run.save()
        return False
    run.save()
    return True


def _advance(run: FlowRun) -> None:
    """Walk the graph from run.current_node until the run parks or ends."""
    wf = run.workflow
    nodes = _nodes_by_id(wf)
    for _ in range(MAX_STEPS):
        node = nodes.get(run.current_node)
        if node is None:
            _finish(run, "failed", f"node {run.current_node!r} not in workflow")
            return
        kind = node.get("type")
        branch = None
        if kind == "action":
            if not _do_action(run, node):
                run.save()  # postponed by quiet hours — re-runs this node later
                return
        elif kind == "decision":
            branch = _decide(run, node)
            if branch is None:
                run.status, run.wait_for = "waiting", "reply"
                run.save()
                return
        elif kind == "handover":
            _do_handover(run, node)
        elif kind == "end":
            _do_end(run, node)
            return
        # trigger + stage nodes are entry/visual markers: pass through
        edge = _pick_edge(wf, node, branch)
        if edge is None:
            _finish(run, "done", "end of path")
            return
        if not _apply_edge(run, edge):
            return
    _finish(run, "failed", f"aborted after {MAX_STEPS} steps (cycle?)")


# ---- resume: time + events -------------------------------------------------------

STALLED_RUNNING_MINUTES = 10  # a "running" run untouched this long = dead process


def resume_due_runs() -> int:
    """Wake runs whose wait elapsed, and record-waits whose record closed."""
    resumed = 0
    now = timezone.now()
    # Crash recovery: "running" is a transient state (seconds, inside one
    # _advance walk) — a run stuck there means the process died mid-step
    # (observed live: dev-server restart mid-action). Nothing would ever
    # advance it again and it silently swallows the contact's replies, so
    # fail it LOUDLY for the operator to re-trigger.
    for run in FlowRun.objects.filter(
            status="running",
            updated_at__lt=now - timedelta(minutes=STALLED_RUNNING_MINUTES)):
        _log(run, run.current_node, "stalled mid-step (process died?) — failing")
        _finish(run, "failed", "stalled mid-step")
        rbac.notify(("superadmin", "admin"), "system",
                    f"Flow run stalled: {run.workflow.name}",
                    f"{run.contact.name if run.contact else '?'} was stuck at "
                    f"{run.current_node!r} — the server likely restarted mid-step. "
                    "Re-trigger the workflow if the contact still needs it.",
                    "/flow-builder", category="flows")
    for run in FlowRun.objects.filter(status="waiting", wait_until__lte=now):
        run.status, run.wait_until = "running", None
        _log(run, run.current_node, "wait elapsed — resuming")
        _advance(run)
        resumed += 1
    for run in (FlowRun.objects.filter(status="waiting", wait_for="record")
                .select_related("contact")):
        record = primary_record(run.contact)
        if record is not None and record.status.strip().lower() in CLOSED_RECORD_STATUSES:
            run.status, run.wait_for = "running", ""
            context = run.context or {}
            context["record_closed"] = True
            run.context = context
            _log(run, run.current_node, f"record {record.status} — resuming")
            _advance(run)
            resumed += 1
    return resumed


def _digits(value: str) -> str:
    return re.sub(r"\D", "", value or "")


def _contact_for(channel: str, key: str) -> Contact | None:
    thread = (Thread.objects.filter(channel=channel, key=key)
              .select_related("contact").first())
    if thread and thread.contact:
        return thread.contact
    if channel == "email":
        return Contact.objects.filter(email__iexact=key).first()
    digits = _digits(key)
    if len(digits) < 8:
        return None
    for contact in Contact.objects.all():
        if _digits(contact.phone)[-9:] == digits[-9:]:
            return contact
    return None


def on_inbound_message(channel: str, key: str, text: str) -> int:
    """Resume every run of this contact parked on a reply. Returns the number
    of runs resumed."""
    contact = _contact_for(channel, key)
    if contact is None:
        return 0
    resumed = 0
    for run in FlowRun.objects.filter(status="waiting", wait_for="reply",
                                      contact=contact):
        context = run.context or {}
        context.update({"last_reply": str(text or "")[:500],
                        "reply_consumed": False, "answered": True,
                        "reply_channel": channel})
        run.context = context
        run.status, run.wait_for = "running", ""
        _log(run, run.current_node, f"contact replied on {channel}: {str(text)[:60]!r}")
        _advance(run)
        resumed += 1
    return resumed


def on_inbound_async(channel: str, key: str, text: str) -> None:
    """Webhook-safe wrapper: same daemon-thread pattern as the AI auto-reply —
    the provider ack must never wait on flow execution."""
    def runner():
        try:
            on_inbound_message(channel, key, text)
        except Exception:
            logger.exception("flows: inbound resume failed for %s:%s", channel, key)
        finally:
            close_old_connections()

    threading.Thread(target=runner, daemon=True).start()


# ---- tick + background ticker ----------------------------------------------------

def tick() -> dict:
    """One engine pass: resume what's due, enroll new matches, then drain the
    outbound conversation queue (queue.py — paced sends)."""
    resumed = resume_due_runs()
    started = evaluate_triggers()
    from . import queue as outbound_queue  # lazy: keeps engine import-light
    drained = outbound_queue.drain()
    if started or resumed or drained:
        logger.info("flows: tick started=%s resumed=%s drained=%s",
                    started, resumed, drained)
    return {"started": started, "resumed": resumed, "drained": drained}


_ticker_lock = threading.Lock()
_ticker_started = False


def start_ticker() -> bool:
    """Start the in-process ticker thread (idempotent). Runs tick() every
    TICK_SECONDS so time-based triggers fire without any external cron."""
    global _ticker_started
    with _ticker_lock:
        if _ticker_started:
            return False
        _ticker_started = True

    def loop():
        while True:
            time.sleep(TICK_SECONDS)
            try:
                tick()
            except Exception:
                logger.exception("flows: tick failed")
            finally:
                close_old_connections()

    threading.Thread(target=loop, daemon=True, name="flows-ticker").start()
    logger.info("flows: ticker started (every %ss)", TICK_SECONDS)
    return True


def ticker_alive() -> bool:
    return _ticker_started
