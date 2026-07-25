"""The outbound conversation queue — DB rows (crm.OutboundJob) plus the pacing
worker that drains them.

Flow Action nodes no longer blast WhatsApp messages inline: the engine
enqueues (engine._queue_whatsapp -> enqueue()) and drain() — called from
engine.tick(), so the background ticker / manage.py flows_tick / POST
/api/flows/tick all pump it — sends jobs out with human pacing, per the
anti-ban evidence in backend/baileys_service/RESEARCH.md ("Recommendations"):

  - ONE real send per account per drain pass, plus a randomized minimum gap
    between an account's sends (queue_min_gap_s / queue_max_gap_s, default
    45–180s of jitter),
  - a daily per-account cap (queue_daily_cap, default 200 sent jobs per
    calendar day),
  - an active-conversation cap (queue_active_cap, default 8): a contact is
    "active" for an account while their thread awaits a reply (last message
    outbound and newer than the last inbound, within queue_active_window_h,
    default 24h). At the cap only jobs for conversations already in flight
    (recent thread traffic in either direction) proceed — new conversation
    starts wait. An inbound reply naturally frees the slot, because the
    thread stops being awaiting-reply: that's the pause/resume behaviour.

The sending ACCOUNT is resolved at SEND time via whatsapp/routing.py (sticky
assignment + health failover), never at enqueue time — ROLE-aware (client
model): text jobs route with role="conversation" (personal numbers carry the
conversations), template jobs with provider="cloud" PREFERRING role="reminder"
(the business number is the reminder cannon), falling back to any eligible
Cloud account. A job may pin its account via payload["account_id"] (the
business-redirect responder must answer from the number the contact wrote to).
Compliance gates run at send time too: quiet hours push not_before to the
quiet end, opted-out threads fail the job outright (no force in automation),
a text job into a closed Cloud 24h window fails with "window closed (needs
template)" — template jobs deliberately pass a closed window (that is their
point), and Baileys threads have no window (the store reports them always
open) — and the cold-text guard (Setting cold_text_guard) blocks/flags text
jobs into a zero-inbound thread on a conversation-role account. Sandbox (no
configured account anywhere): the message is stored locally exactly like the
old inline engine sends, and the job is marked sent with detail "sandbox".

kind="ai_reply" jobs are the "one human per account" AI replies (Setting
ai_reply_mode="queued", whatsapp/views._queue_ai_reply): the reply text is
generated AT SEND TIME from the thread's fresh state — several contact
messages get ONE coherent answer — then sent via the thread's own account
with the same per-account pacing, and the opt-out/ai_enabled/flow-owns-reply
guards are re-checked here before generating.

kind="call" jobs DIAL instead of sending, and pace on a different axis: a
WhatsApp number carries exactly one call at a time (the gateway 502s a second
dial), so a call job waits for the number to be free rather than for a send
gap. "Busy" is a WAIT, never an attempt — only compliance refusals (opt-out,
cold-call guard, do-not-call) fail a call for good. "sent" means DIALLED, not
completed: start_call returns while the phone is still ringing, and the number
stays blocked for later jobs by baileys_calls.account_call_busy, not by the
job's status. When a call CAN'T be placed, the job also un-parks its flow run
(_unpark_run) — the run is waiting for a reply that only the call could have
prompted, and a parked run blocks the trigger from ever re-enrolling that
contact. SIP call jobs (transport="sip") have no liveness endpoint to poll,
so they pace by gap + one dial per pass.

Settings keys (crm.Setting rows override QUEUE_* env keys, wa_config-style):
queue_min_gap_s, queue_max_gap_s, queue_daily_cap, queue_active_cap,
queue_active_window_h, queue_call_gap_s, queue_call_daily_cap.

All pacing state is DERIVED from OutboundJob rows (last sent_at per account),
so a ticker/process restart never forgets the gap clock. WhatsApp modules are
imported lazily inside functions — same rule as the engine.
"""
from __future__ import annotations

import logging
import os
import random
import re
import threading
from datetime import datetime, timedelta

from django.db.models import Max, Q
from django.utils import timezone

from crm.models import Message, OutboundJob, Thread

from . import engine

logger = logging.getLogger(__name__)

MAX_ATTEMPTS = 3       # send attempts per job before it fails for good
RETRY_BACKOFF_S = 120  # first retry delay; doubles per further attempt

SETTING_DEFAULTS = {
    "queue_min_gap_s": 10,        # min seconds between an account's sends
    "queue_max_gap_s": 30,        # jitter upper bound for that gap
    "queue_daily_cap": 200,       # sent jobs per account per calendar day
    "queue_active_cap": 8,        # awaiting-reply conversations per account
    "queue_active_window_h": 24,  # hours a conversation stays "active"
    # CLOUD accounts (official Business API) need no human camouflage — Meta
    # governs template volume by messaging tier, not send rhythm. The whole
    # queue-pacing machinery (jittered gap, one-send-per-pass, conversation
    # caps) exists ONLY for the unofficial Baileys numbers' ban risk; a Cloud
    # reminder blast should go out together, immediately. gap=0 means a batch of
    # reminders drains in a SINGLE pass instead of trickling one-per-tick — a
    # non-zero gap forced one Cloud send per drain because the whole pass runs
    # in well under that gap (field report 2026-07-23: reminders "not sent at
    # the same time", stuck queueing). Set queue_cloud_gap_s > 0 only to
    # deliberately throttle an official number; Meta's messaging tier
    # (queue_cloud_daily_cap) is the real ceiling.
    "queue_cloud_gap_s": 0,       # 0 = no intra-pass gap (batch drains together)
    "queue_cloud_daily_cap": 1000,  # Cloud sends/day (Meta's tier is the real cap)
    # CALL jobs don't pace like messages — a number carries one call at a time,
    # so the real gate is "is this number free?". This is just the breather
    # after one call before the next dial on the same number.
    "queue_call_gap_s": 20,
    "queue_call_daily_cap": 50,   # dialled calls per account per calendar day
}


def _to_int(raw, default: int) -> int:
    try:
        return max(0, int(str(raw).strip()))
    except (TypeError, ValueError):
        return default


def queue_settings() -> dict:
    """Effective pacing knobs: crm.Setting rows override QUEUE_* env keys,
    env overrides the shipped defaults (wa_config-style accessor)."""
    from crm.models import Setting

    merged = {k: _to_int(os.getenv(k.upper()) or v, v)
              for k, v in SETTING_DEFAULTS.items()}
    for row in Setting.objects.filter(key__in=SETTING_DEFAULTS):
        if row.value.strip():
            merged[row.key] = _to_int(row.value, merged[row.key])
    return merged


def save_queue_settings(patch: dict) -> dict:
    """Persist dashboard-edited pacing knobs as Setting rows — the top layer
    of queue_settings()'s Setting > env > default merge. Unknown keys are
    ignored; values are stored as strings like every other Setting."""
    from crm.models import Setting

    for key in SETTING_DEFAULTS:
        if key in patch:
            Setting.objects.update_or_create(key=key,
                                             defaults={"value": str(patch[key])})
    return queue_settings()


# ---- enqueue ---------------------------------------------------------------------

def enqueue(channel: str, kind: str, contact, to_address: str, payload: dict,
            flow_run=None, flow_node: str = "") -> OutboundJob:
    """Create a queued OutboundJob. The account is NOT chosen here — routing
    resolves it at send time, so a failover between enqueue and send is
    honoured and follow-ups keep coming from the contact's sticky number."""
    return OutboundJob.objects.create(
        channel=str(channel or "whatsapp"),
        kind=kind if kind in ("text", "template", "ai_reply", "call") else "text",
        contact=contact,
        to_address=str(to_address or ""),
        payload=payload if isinstance(payload, dict) else {},
        flow_run=flow_run,
        flow_node=str(flow_node or ""),
    )


# ---- the pacing worker -------------------------------------------------------------

# The ticker thread and a manual POST /api/flows/tick can call drain()
# concurrently in the same process; without this guard both read the same
# queued rows before either marks them sent → the contact gets the message
# twice (observed in the field). Non-blocking: the second caller just skips —
# the pass in flight is already sending those jobs.
_drain_lock = threading.Lock()


def drain(limit: int = 25) -> int:
    """One pacing pass over the queue: send due jobs oldest-first, at most one
    real send per account per pass. Returns the number of jobs sent (sandbox
    included). Called from engine.tick() — never sleeps, never raises."""
    if not _drain_lock.acquire(blocking=False):
        return 0  # another pass is mid-flight (ticker vs manual tick)
    try:
        return _drain_locked(limit)
    finally:
        _drain_lock.release()


SENDING_STALE_MINUTES = 10  # a "sending" claim older than this = crashed sender


def _drain_locked(limit: int) -> int:
    now = timezone.now()
    # reaper: a process that died mid-send leaves its claim behind — requeue
    OutboundJob.objects.filter(
        status="sending",
        updated_at__lt=now - timedelta(minutes=SENDING_STALE_MINUTES),
    ).update(status="queued")
    jobs = list(
        OutboundJob.objects.filter(status="queued")
        .filter(Q(not_before__isnull=True) | Q(not_before__lte=now))
        .select_related("contact", "flow_run")
        .order_by("created_at", "pk")[: max(1, int(limit))]
    )
    if not jobs:
        return 0

    from whatsapp import wa_config
    if wa_config.in_quiet_hours():
        resume_at = _quiet_resume_at()
        for job in jobs:
            job.not_before = resume_at
            job.save(update_fields=["not_before", "updated_at"])
        logger.info("queue: quiet hours — %s job(s) deferred to %s",
                    len(jobs), resume_at)
        return 0

    cfg = queue_settings()
    sent = 0
    sent_accounts: set[str] = set()  # one real send per account per pass
    for job in jobs:
        # CROSS-PROCESS claim (the in-process lock can't see a second server —
        # observed live: a mid-reload zombie runserver ran a second ticker and
        # the same job went out twice). The atomic queued→sending UPDATE makes
        # the DB the arbiter; the loser of the race just skips the job. Every
        # outcome path (_mark_sent/_fail/_retry/_cancel) writes a final status.
        if not OutboundJob.objects.filter(pk=job.pk, status="queued") \
                .update(status="sending", updated_at=timezone.now()):
            continue  # another process claimed it first
        job.status = "sending"
        try:
            if _process(job, cfg, sent_accounts):
                sent += 1
        except Exception as e:  # a job bug must never kill the ticker
            logger.exception("queue: job %s crashed", job.pk)
            _retry(job, f"internal error: {e}")
        finally:
            # every resolved outcome wrote a terminal status (_mark_sent /
            # _fail / _retry / _cancel); a job merely POSTPONED by pacing is
            # still "sending" — release the claim for the next pass
            OutboundJob.objects.filter(pk=job.pk, status="sending") \
                .update(status="queued")
    return sent


def _quiet_resume_at():
    """When quiet hours end, as an aware timestamp. wa_config's quiet hours
    run on the server-local wall clock (datetime.now()), so compute the
    wall-clock delta to quiet_end and apply it to timezone.now() — correct
    even when Django's TIME_ZONE differs from the system clock."""
    from whatsapp import wa_config

    end = wa_config.parse_hhmm(wa_config.global_settings().get("quiet_end", ""))
    if end is None:  # shouldn't happen while in_quiet_hours() is True
        return timezone.now() + timedelta(minutes=30)
    local_now = datetime.now()
    candidate = local_now.replace(hour=end // 60, minute=end % 60,
                                  second=0, microsecond=0)
    if candidate <= local_now:
        candidate += timedelta(days=1)  # overnight window: ends tomorrow
    return timezone.now() + (candidate - local_now)


# ---- account resolution (send time, not enqueue time) ------------------------------

def _resolve_account(contact, kind: str = "text") -> dict | None:
    """The account dict to send through: routing's sticky/failover pick first,
    else wa_config.get_account's sole-configured-account fallback (which
    covers the env-default pseudo-account and single-account setups without
    role config) — but never a health-ineligible DB account. None = nothing
    routable right now.

    Role-aware (client model): text jobs are conversational, so they route
    with role="conversation"; template jobs stay on the Cloud API and PREFER
    role="reminder", falling back to any eligible Cloud account."""
    from whatsapp import routing, wa_config

    row = None
    if contact is not None:
        if kind == "template":
            row = routing.account_for_contact(contact, provider="cloud",
                                               role="reminder")
            if row is None:
                row = routing.account_for_contact(contact, provider="cloud")
        else:
            row = routing.account_for_contact(contact, role="conversation")
    if row is not None:
        account = wa_config.get_account(str(row.pk))
        if wa_config.account_configured(account):
            return account
    account = wa_config.get_account(None)
    if not wa_config.account_configured(account):
        return None
    account_id = str(account.get("id") or "")
    if not account_id.isdigit():
        return account  # env-default pseudo-account: no health row to consult
    from crm.models import ChannelAccount

    db_row = ChannelAccount.objects.filter(pk=int(account_id)).first()
    if db_row is not None and db_row.health_status in routing.INELIGIBLE_STATUSES:
        return None  # the sole account is banned/disconnected — don't use it
    return account


def _pinned_account(account_id: str) -> dict | None:
    """A job's payload["account_id"] pin: THAT account or nothing — never a
    silent substitute (the redirect responder must answer from the number the
    contact actually wrote to). None when unknown/unconfigured/down."""
    from whatsapp import routing, wa_config

    account = wa_config.get_account(str(account_id))
    if account is None or str(account.get("id")) != str(account_id):
        return None  # get_account's sole-account fallback picked something else
    if not wa_config.account_configured(account):
        return None
    if str(account_id).isdigit():
        from crm.models import ChannelAccount

        db_row = ChannelAccount.objects.filter(pk=int(account_id)).first()
        if db_row is not None and db_row.health_status in routing.INELIGIBLE_STATUSES:
            return None
    return account


# ---- per-account accounting (all derived from the DB) -------------------------------

def _sent_today(account_pk: int) -> int:
    return OutboundJob.objects.filter(
        account_id=account_pk, status="sent",
        sent_at__date=timezone.localdate()).count()


def _next_allowed_at(account_pk: int, cfg: dict):
    """Last real send by this account + a randomized human gap. Derived from
    OutboundJob.sent_at so a restart never forgets the clock. None = never
    sent, go ahead."""
    last = (OutboundJob.objects.filter(account_id=account_pk, status="sent")
            .aggregate(m=Max("sent_at"))["m"])
    if last is None:
        return None
    lo = cfg["queue_min_gap_s"]
    hi = max(lo, cfg["queue_max_gap_s"])
    return last + timedelta(seconds=random.uniform(lo, hi))


def _active_count(account_pk: int, cfg: dict) -> int:
    """Awaiting-reply conversations on this account: threads whose last
    message is outbound, newer than the last inbound, within the window."""
    cutoff = timezone.now() - timedelta(hours=cfg["queue_active_window_h"])
    threads = (Thread.objects.filter(channel="whatsapp", account_id=account_pk)
               .annotate(last_out=Max("messages__created_at",
                                      filter=Q(messages__direction="out")),
                         last_in=Max("messages__created_at",
                                     filter=Q(messages__direction="in"))))
    return sum(1 for t in threads
               if t.last_out and t.last_out >= cutoff
               and (t.last_in is None or t.last_out > t.last_in))


def _in_flight(to_address: str, cfg: dict) -> bool:
    """Recent traffic (either direction) on the contact's thread — an active
    or replying conversation may continue even when the account is at cap."""
    from whatsapp import store as wa_store

    cutoff = timezone.now() - timedelta(hours=cfg["queue_active_window_h"])
    t = (Thread.objects.filter(channel="whatsapp",
                               key=wa_store.normalize_wa_id(to_address))
         .annotate(last=Max("messages__created_at")).first())
    return bool(t and t.last and t.last >= cutoff)


# ---- job terminal transitions -------------------------------------------------------

def _run_log(job: OutboundJob, event: str) -> None:
    run = job.flow_run
    if run is None:
        return
    engine._log(run, job.flow_node or run.current_node, event)
    run.save(update_fields=["log", "updated_at"])


def _fail(job: OutboundJob, reason: str) -> bool:
    """Permanent failure — compliance or configuration, retrying can't help."""
    job.status = "failed"
    job.last_error = str(reason)[:500]
    job.save(update_fields=["status", "last_error", "updated_at"])
    _run_log(job, f"queue: {job.channel} {job.kind} failed — {reason}")
    return False


def _cancel(job: OutboundJob, reason: str) -> bool:
    """The job is no longer wanted (guard re-check at send time said no) —
    not a failure, nothing to retry."""
    job.status = "cancelled"
    job.last_error = str(reason)[:500]
    job.save(update_fields=["status", "last_error", "updated_at"])
    _run_log(job, f"queue: {job.channel} {job.kind} cancelled — {reason}")
    return False


def _retry(job: OutboundJob, error: str) -> bool:
    """Transient failure: exponential backoff via not_before, then failed for
    good after MAX_ATTEMPTS."""
    job.attempts += 1
    job.last_error = str(error)[:500]
    if job.attempts >= MAX_ATTEMPTS:
        job.status = "failed"
        job.save(update_fields=["status", "attempts", "last_error", "updated_at"])
        _run_log(job, f"queue: {job.channel} {job.kind} failed after "
                      f"{job.attempts} attempts — {error}")
    else:
        job.not_before = timezone.now() + timedelta(
            seconds=RETRY_BACKOFF_S * (2 ** (job.attempts - 1)))
        job.save(update_fields=["attempts", "last_error", "not_before", "updated_at"])
    return False


def _mark_sent(job: OutboundJob, msg: dict, external_id: str = "",
               detail: str = "") -> bool:
    """Stamp the job sent and record the delivery on the flow run's context
    (context["deliveries"]) so per-run delivery status is inspectable via
    GET /api/flows/runs; later status updates keep flowing through the
    webhook -> Message.status path."""
    job.status = "sent"
    job.sent_at = timezone.now()
    job.last_error = detail  # "" | "sandbox" — a status detail, not an error
    message_pk = msg.get("pk")
    job.sent_message = Message.objects.filter(pk=message_pk).first() if message_pk else None
    job.save()
    run = job.flow_run
    if run is not None:
        context = dict(run.context or {})
        deliveries = list(context.get("deliveries") or [])
        deliveries.append({"job_id": job.pk, "message_id": message_pk,
                           "external_id": external_id or ""})
        context["deliveries"] = deliveries
        run.context = context
        engine._log(run, job.flow_node or run.current_node,
                    f"queue: {job.channel} {job.kind} sent"
                    + (f" ({detail})" if detail else ""))
        run.save(update_fields=["context", "log", "updated_at"])
    return True


# ---- template rendering helpers -----------------------------------------------------

def _positional(variables) -> list[str]:
    """{"1": "a", "3": "c"} -> ["a", "", "c"] — the positional body params
    client.send_template expects. Non-numeric keys are ignored; gaps render
    as empty strings so later slots stay aligned."""
    if not isinstance(variables, dict):
        return []
    slots: dict[int, str] = {}
    for k, v in variables.items():
        try:
            i = int(str(k).strip())
        except (TypeError, ValueError):
            continue
        if i >= 1:
            slots[i] = str(v)
    if not slots:
        return []
    out = [""] * max(slots)
    for i, v in slots.items():
        out[i - 1] = v
    return out


def _template_text(account: dict | None, name: str, language: str,
                   params: list[str]) -> str:
    """The rendered body stored on the thread — what the contact actually
    sees. Demo/custom sandbox templates render locally; live Cloud accounts
    fall back to a Graph body lookup; last resort a readable placeholder."""
    from whatsapp import views as wa_views  # lazy: reuses the demo-template helpers

    demo = wa_views._demo_template(name)
    body = (demo or {}).get("body") or ""
    if not body and account is not None and account.get("provider") != "baileys":
        body = wa_views._live_template_body(account, name, language)
    if body:
        return wa_views._render_template_body(body, params)
    return f"[template: {name}]" + (f" — {', '.join(params)}" if params else "")


# ---- sending one job ----------------------------------------------------------------

def _process(job: OutboundJob, cfg: dict, sent_accounts: set) -> bool:
    from whatsapp import wa_config

    if job.kind == "call":
        # Calls pick their own transport and account and serialise on the
        # number being FREE — none of the message pacing below applies.
        return _place_call(job, cfg, sent_accounts)
    if job.channel != "whatsapp":
        return _fail(job, f"unsupported channel '{job.channel}'")
    if job.kind == "ai_reply":
        return _send_ai_reply(job, cfg, sent_accounts)
    pinned = str((job.payload or {}).get("account_id") or "")
    if pinned:
        account = _pinned_account(pinned)
        if account is None:
            return _retry(job, "pinned account not available")
    else:
        account = _resolve_account(job.contact, kind=job.kind)
    if account is None:
        if wa_config.is_configured():
            # accounts exist but none is routable right now (banned/…)
            return _retry(job, "no eligible account")
        return _send_sandbox(job)
    _apply_account_vars(job, account)
    # Safety net: never ship a literal {{account_number}} to a contact. Reaching
    # here means every conversation number is down AND no support_contact is
    # configured, so there is genuinely nothing truthful to say — retry (the
    # numbers may come back) instead of sending a broken message.
    if _unrendered_account_var(job):
        return _retry(job, "no healthy conversation number to quote "
                           "(set a support contact under routing rules)")
    return _send_live(job, account, cfg, sent_accounts)


# Both spellings are live: the builder's chips emit the explicit
# whatsapp_assignment_* names, while older saved flows (and the AI-reply
# renderer) still carry the short ones. Accept either — a placeholder that
# silently fails to render goes out to a real contact.
_ACCOUNT_VAR_RE = re.compile(
    r"\{\{\s*((?:whatsapp_assignment_)?account_(?:number|name))\s*\}\}")


def _account_vars(contact, account: dict) -> dict:
    """{{placeholders}} resolved at SEND time, not enqueue time.

    account_number is the contact's assigned CONVERSATION number — the Baileys
    personal number they should actually talk to — NOT the account carrying
    this particular message. That distinction matters: under the operating
    model the Cloud business number is a template cannon that contacts cannot
    usefully reply to, so a reminder blasted from it needs to quote the
    conversation number ("check more information from +60..."). Resolving the
    sending account instead left the placeholder unfilled on exactly the
    template sends that need it most, because Cloud accounts store Meta's
    phone_number_id (not dialable) in that field.

    Routing is the same seam the redirect and text jobs use, so the quoted
    number is the one that will really answer, including after a failover.
    Falls back to the sending account when it is itself Baileys.
    """
    from whatsapp import routing

    out = {"account_name": str(account.get("name") or "")}

    def _msisdn(row_or_dict) -> str:
        raw = (getattr(row_or_dict, "external_id", None)
               if not isinstance(row_or_dict, dict) else row_or_dict.get("phone_number_id"))
        return "".join(ch for ch in str(raw or "") if ch.isdigit())

    number, name = "", ""
    try:
        conv = routing.account_for_contact(contact, "whatsapp", role="conversation")
    except Exception:  # routing must never break a send
        conv = None
    if conv is not None and (conv.provider or "") == "baileys":
        number, name = _msisdn(conv), conv.name
    elif str(account.get("provider") or "") == "baileys":
        number, name = _msisdn(account), str(account.get("name") or "")

    if number:
        out["account_number"] = "+" + number
        out["account_name"] = name or out["account_name"]
    else:
        # Every conversation number is down (the recovery case: the one message
        # you most want to send is the one that has no healthy number to quote).
        # support_contact is exactly the human fallback for this — better a
        # support line than a dead number, and far better than shipping a raw
        # {{placeholder}} to a contact. If it is unset the placeholder survives
        # and _apply_account_vars fails the job rather than send it broken.
        from whatsapp import wa_config

        support = str(wa_config.routing_rules().get("support_contact") or "").strip()
        if support:
            out["account_number"] = support
    # Alias every key under the builder's explicit spelling too.
    return {**out, **{f"whatsapp_assignment_{k}": v for k, v in out.items()}}


def _apply_account_vars(job: OutboundJob, account: dict) -> None:
    """Substitute the send-time account placeholders into this job's payload.

    Mutates the payload IN MEMORY only: the row keeps the unrendered text so a
    retry (or a failover onto a different number) re-resolves against whatever
    account actually wins that pass, instead of shipping a stale number.
    """
    variables = _account_vars(job.contact, account)

    def sub(text: str) -> str:
        return _ACCOUNT_VAR_RE.sub(
            lambda m: variables.get(m.group(1), m.group(0)), text)

    payload = job.payload or {}
    if isinstance(payload.get("text"), str):
        payload["text"] = sub(payload["text"])
    params = payload.get("variables")
    if isinstance(params, dict):
        payload["variables"] = {k: sub(v) if isinstance(v, str) else v
                                for k, v in params.items()}
    elif isinstance(params, list):
        payload["variables"] = [sub(v) if isinstance(v, str) else v for v in params]
    job.payload = payload


def _unrendered_account_var(job: OutboundJob) -> bool:
    """True when an account placeholder survived substitution — i.e. we are one
    step away from sending a contact a literal "{{account_number}}"."""
    payload = job.payload or {}
    blobs = [payload.get("text") or ""]
    params = payload.get("variables")
    if isinstance(params, dict):
        blobs += [v for v in params.values() if isinstance(v, str)]
    elif isinstance(params, list):
        blobs += [v for v in params if isinstance(v, str)]
    return any(_ACCOUNT_VAR_RE.search(b) for b in blobs)


def _source(job: OutboundJob) -> str:
    """Who the stored message is attributed to: payload["source"] ("system"
    for the redirect responder), default "ai" (automation)."""
    source = str((job.payload or {}).get("source") or "")
    return source if source in ("ai", "agent", "system") else "ai"


def _send_sandbox(job: OutboundJob) -> bool:
    """No configured account anywhere: store the message locally, exactly like
    the engine's old inline sandbox sends (Message.status == "sandbox")."""
    from whatsapp import store as wa_store

    thread = wa_store.get_or_create_thread(
        job.to_address, name=job.contact.name if job.contact else "")
    if thread.get("opted_out"):
        return _fail(job, "opted out")
    payload = job.payload or {}
    if job.kind == "template":
        from whatsapp import views as wa_views

        name = str(payload.get("name") or "")
        if wa_views._demo_template(name) is None:
            return _fail(job, f"unknown sandbox template '{name}'")
        text = _template_text(None, name, str(payload.get("language") or "en"),
                              _positional(payload.get("variables")))
    else:
        text = str(payload.get("text") or "")
        if not text:
            return _fail(job, "empty text payload")
    msg = wa_store.append_message(thread["wa_id"], "out", text,
                                  source=_source(job), status="sandbox")
    return _mark_sent(job, msg, detail="sandbox")


def _pacing_blocked(account_pk: int | None, account_id: str, to_address: str,
                    cfg: dict, sent_accounts: set, provider: str = "baileys") -> bool:
    """Per-account pacing gates (DB accounts; env-default has no row).
    True = this job must wait for a later pass.

    Cloud (official API) accounts skip the human camouflage entirely: no
    one-per-pass gate, no jittered gap, no conversation cap — just a small
    anti-hammer gap and a generous daily cap (Meta's messaging tier is the
    real limit). The full ban-avoidance pacing applies to Baileys numbers."""
    if account_pk is None:
        return False
    if provider == "cloud":
        if _sent_today(account_pk) >= cfg["queue_cloud_daily_cap"]:
            return True
        last = (OutboundJob.objects.filter(account_id=account_pk, status="sent")
                .aggregate(m=Max("sent_at"))["m"])
        return bool(last and timezone.now() < last + timedelta(
            seconds=cfg["queue_cloud_gap_s"]))
    if account_id in sent_accounts:
        return True  # one real send per account per drain pass
    if _sent_today(account_pk) >= cfg["queue_daily_cap"]:
        return True  # daily cap — job waits for the next calendar day
    ready_at = _next_allowed_at(account_pk, cfg)
    if ready_at is not None and timezone.now() < ready_at:
        return True  # randomized human gap not elapsed yet
    if (_active_count(account_pk, cfg) >= cfg["queue_active_cap"]
            and not _in_flight(to_address, cfg)):
        return True  # at cap: only in-flight conversations proceed
    return False


def _send_live(job: OutboundJob, account: dict, cfg: dict,
               sent_accounts: set) -> bool:
    from whatsapp import health, wa_config
    from whatsapp import store as wa_store

    account_id = str(account.get("id") or "")
    account_pk = int(account_id) if account_id.isdigit() else None

    if _pacing_blocked(account_pk, account_id, job.to_address, cfg, sent_accounts,
                       provider=str(account.get("provider") or "cloud")):
        return False

    # Stamp the routed account onto the thread (mirrors views._routed_account)
    # so window state + message attribution follow the sender.
    thread = wa_store.get_or_create_thread(
        job.to_address, name=job.contact.name if job.contact else "",
        account_id=account_id if account_pk is not None else None)
    wa_id = thread["wa_id"]
    if thread.get("opted_out"):
        return _fail(job, "opted out")

    # Cold-text guard: free-text into a thread the contact NEVER wrote to,
    # on a conversation-role (personal) number = the cold-outreach ban vector.
    cold_note = ""
    if (job.kind == "text"
            and wa_config.account_role(account) == "conversation"
            and not wa_store.thread_has_inbound(wa_id)):
        guard = wa_config.cold_text_guard()
        if guard == "block":
            from whatsapp import views as wa_views  # lazy: shared wording

            return _fail(job, wa_views.COLD_TEXT_BLOCK_DETAIL)
        if guard == "warn":
            from whatsapp import views as wa_views

            cold_note = wa_views.COLD_TEXT_WARN_NOTE

    if job.kind == "text" and not thread.get("window_open"):
        # Cloud 24h window. Baileys + sandbox threads report always-open,
        # so this only bites live Cloud threads — use a template there.
        return _fail(job, "window closed (needs template)")

    from whatsapp import client

    payload = job.payload or {}
    try:
        if job.kind == "template":
            name = str(payload.get("name") or "")
            language = str(payload.get("language") or "en")
            params = _positional(payload.get("variables"))
            if not name:
                return _fail(job, "template payload missing name")
            if account.get("provider") == "baileys":
                # Baileys has no template registry (nor a window to reopen):
                # render the body locally and send it as free-form text.
                text = _template_text(account, name, language, params)
                if text.startswith("[template:"):
                    return _fail(job, f"no local body for template '{name}' "
                                      "(baileys sends templates as text)")
                msg_id = client.send_text(account, wa_id, text)
            else:
                msg_id = client.send_template(account, wa_id, name, language, params)
                text = _template_text(account, name, language, params)
        else:
            text = str(payload.get("text") or "")
            if not text:
                return _fail(job, "empty text payload")
            msg_id = client.send_text(account, wa_id, text)
    except Exception as e:
        health.record_send_failure(account_id, str(e))
        return _retry(job, f"send failed: {e}")

    health.record_send_ok(account_id)
    if account_pk is not None:
        job.account_id = account_pk
        sent_accounts.add(account_id)
    msg = wa_store.append_message(wa_id, "out", text, source=_source(job),
                                  msg_id=msg_id)
    if cold_note:
        wa_store.append_message(wa_id, "out", cold_note, source="system")
    return _mark_sent(job, msg, external_id=msg_id)


# ---- queued AI replies ("one human per account") ------------------------------------

def _send_ai_reply(job: OutboundJob, cfg: dict, sent_accounts: set) -> bool:
    """Drain a kind="ai_reply" job: re-check the guards, then generate the
    reply from the thread's CURRENT state (late generation — several contact
    messages since enqueue get one coherent answer) and send it via the
    thread's own account with the normal per-account pacing."""
    from whatsapp import health, wa_config
    from whatsapp import store as wa_store
    from whatsapp import views as wa_views

    thread = wa_store.load_thread(job.to_address)
    if thread is None:
        return _fail(job, "thread not found")
    # guards re-checked at send time (they were checked at enqueue too)
    if thread.get("opted_out"):
        return _cancel(job, "opted out")
    if not thread.get("contact_id"):
        return _cancel(job, "no linked contact")
    if not thread.get("ai_enabled"):
        return _cancel(job, "AI disabled on thread")
    if wa_views._flow_owns_reply(thread):
        return _cancel(job, "a flow owns this reply")
    if not wa_config.ai_available():
        return _retry(job, "no AI provider configured")

    wa_id = thread["wa_id"]
    account = wa_views._routed_account(thread)  # thread account + failover
    if account is None:
        # sandbox thread (normally answered instantly at inbound time — this
        # only happens when the account was deleted after enqueue)
        reply = _generate_ai_reply(job, thread)
        if not isinstance(reply, str):
            return reply
        msg = wa_store.append_message(wa_id, "out", reply, source="ai",
                                      status="sandbox")
        return _mark_sent(job, msg, detail="sandbox")

    account_id = str(account.get("id") or "")
    account_pk = int(account_id) if account_id.isdigit() else None
    if _pacing_blocked(account_pk, account_id, job.to_address, cfg, sent_accounts):
        return False
    if not thread.get("window_open"):
        return _fail(job, "window closed (needs template)")

    reply = _generate_ai_reply(job, thread)
    if not isinstance(reply, str):
        return reply

    from whatsapp import client

    try:
        msg_id = client.send_text(account, wa_id, reply)
    except Exception as e:
        health.record_send_failure(account_id, str(e))
        return _retry(job, f"send failed: {e}")
    health.record_send_ok(account_id)
    if account_pk is not None:
        job.account_id = account_pk
        sent_accounts.add(account_id)
    msg = wa_store.append_message(wa_id, "out", reply, source="ai", msg_id=msg_id)
    return _mark_sent(job, msg, external_id=msg_id)


def _generate_ai_reply(job: OutboundJob, thread: dict):
    """The reply text, or the job's terminal bool (retry on provider errors,
    cancel when the model has nothing to say)."""
    from whatsapp import ai

    try:
        reply = ai.generate_reply(thread)
    except Exception as e:
        err = str(e).splitlines()[0][:200] if str(e) else "unknown error"
        return _retry(job, f"AI reply failed: {err}")
    if not reply:
        return _cancel(job, "empty AI reply")
    return reply


# ---- queued calls (one at a time per number) -----------------------------------------
#
# Why calls are queued at all: a WhatsApp number carries exactly ONE call, and
# the Baileys gateway 502s a second dial. The flow engine used to ring inline,
# so two runs reaching their call step in the same tick collided — the loser
# recorded a permanent step failure and its run stayed parked forever on a
# reply that could only have followed the call it never got. Queued, the second
# call simply waits for the first to hang up.
#
# "busy" is therefore a WAIT, not a failure; compliance refusals are the
# opposite — no amount of retrying makes an opted-out contact callable.

_CALL_BUSY_MARKERS = ("already has a live call", "call is already in progress")
_CALL_PERMANENT_MARKERS = ("opted out", "cold call blocked", "not allowed",
                           "do-not-call")


def _call_bucket(account_pk: int | None):
    """Call jobs already dialled on this number. account_pk None = the SIP
    trunk, which has no ChannelAccount row of its own."""
    qs = OutboundJob.objects.filter(kind="call", status="sent")
    return (qs.filter(account_id=account_pk) if account_pk
            else qs.filter(account__isnull=True))


def _call_gap_blocked(account_pk: int | None, cfg: dict) -> bool:
    """Breather between one dial and the next on the same number. The
    one-call-at-a-time check does the real work; this stops a run of very short
    or unanswered calls from machine-gunning a contact list."""
    last = _call_bucket(account_pk).aggregate(m=Max("sent_at"))["m"]
    return bool(last and timezone.now() < last + timedelta(
        seconds=cfg["queue_call_gap_s"]))


def _call_cap_reached(account_pk: int | None, cfg: dict) -> bool:
    return (_call_bucket(account_pk).filter(sent_at__date=timezone.localdate())
            .count() >= cfg["queue_call_daily_cap"])


def _postpone_call(job: OutboundJob, reason: str) -> bool:
    """Leave the job queued for the next pass — NOT an attempt, NOT a failure.
    Returning without writing a terminal status is what makes drain()'s
    finally-block release the claim back to "queued"."""
    logger.info("queue: call job %s waiting — %s", job.pk, reason)
    return False


def _unpark_run(job: OutboundJob, reason: str) -> None:
    """A call that will never be placed must not leave its run parked on a
    reply: that reply can only follow the call, and while the run sits in
    "waiting" the trigger refuses to re-enrol the contact
    (engine._already_ran), which wedges the flow permanently."""
    run = job.flow_run
    if run is not None and run.status == "waiting" and run.wait_for == "reply":
        engine._finish(run, "done", f"call never placed — {reason}")


def _fail_call(job: OutboundJob, reason: str) -> bool:
    _fail(job, reason)
    _unpark_run(job, reason)
    return False


def _retry_call(job: OutboundJob, error: str) -> bool:
    _retry(job, error)
    if job.status == "failed":  # attempts exhausted — the call is never coming
        _unpark_run(job, error)
    else:
        # _retry only writes to the run log on the FINAL failure; a call that
        # keeps not going out is worth showing the operator each time.
        _run_log(job, f"queue: call retry {job.attempts} — {error}")
    return False


def _classify_call_error(error: str) -> str:
    low = str(error or "").lower()
    if any(m in low for m in _CALL_BUSY_MARKERS):
        return "busy"
    if any(m in low for m in _CALL_PERMANENT_MARKERS):
        return "permanent"
    return "transient"


def _mark_call_placed(job: OutboundJob, account_pk: int | None,
                      account_name: str, room: str) -> bool:
    """"sent" here means DIALLED, not completed — start_call returns while the
    phone is still ringing. What blocks the next job is the live-call check on
    the number, not this job's status."""
    job.status = "sent"
    job.account_id = account_pk
    job.sent_at = timezone.now()
    payload = dict(job.payload or {})
    payload["room"] = room
    job.payload = payload
    job.save(update_fields=["status", "account", "sent_at", "payload", "updated_at"])
    run = job.flow_run
    if run is not None:
        context = run.context or {}
        context["call_room"] = room
        run.context = context
        run.save(update_fields=["context", "updated_at"])
    _run_log(job, f"queue: call dialled via {account_name} (room {room})")
    return True


def _place_call(job: OutboundJob, cfg: dict, sent_accounts: set) -> bool:
    contact = job.contact
    if contact is None:
        return _fail_call(job, "no contact on the call job")
    if contact.do_not_call:  # re-checked at dial time, not just at enqueue
        return _fail_call(job, "do-not-call flag set")
    payload = job.payload or {}
    if str(payload.get("transport") or "baileys") == "sip":
        return _place_sip_call(job, payload, cfg, sent_accounts)
    return _place_whatsapp_call(job, payload, cfg, sent_accounts)


def _place_whatsapp_call(job: OutboundJob, payload: dict, cfg: dict,
                         sent_accounts: set) -> bool:
    from whatsapp import baileys_calls, routing, wa_config
    from whatsapp import store as wa_store

    contact = job.contact
    account_row = routing.account_for_contact(contact, provider="baileys")
    if account_row is None:
        pool = routing.eligible_accounts("whatsapp", provider="baileys")
        account_row = routing._least_loaded(pool, "whatsapp") if pool else None
    if account_row is None:
        return _retry_call(job, "no healthy Baileys account")

    slot = f"call:{account_row.pk}"
    if slot in sent_accounts:
        return _postpone_call(job, "this number already dialled this pass")
    account = wa_config.get_account(str(account_row.pk))
    if baileys_calls.account_call_busy(account):
        return _postpone_call(job, f"{account.get('name')} is already on a call")
    if _call_cap_reached(account_row.pk, cfg):
        return _postpone_call(job, "daily call cap reached for this number")
    if _call_gap_blocked(account_row.pk, cfg):
        return _postpone_call(job, "call gap has not elapsed")

    thread = wa_store.get_or_create_thread(job.to_address, name=contact.name)
    result = baileys_calls.start_call(
        wa_store.normalize_wa_id(job.to_address), thread, account,
        voice_provider=str(payload.get("voice_provider") or ""),
        voice_id=str(payload.get("voice") or ""),
        script=str(payload.get("script") or ""))
    error = result.get("error")
    if error:
        verdict = _classify_call_error(error)
        if verdict == "busy":
            return _postpone_call(job, error)
        if verdict == "permanent":
            return _fail_call(job, error)
        return _retry_call(job, error)
    sent_accounts.add(slot)
    return _mark_call_placed(job, account_row.pk,
                             account.get("name") or "WhatsApp", result.get("room") or "")


def _place_sip_call(job: OutboundJob, payload: dict, cfg: dict,
                    sent_accounts: set) -> bool:
    """The SIP trunk exposes no liveness endpoint we can poll, so it paces by
    gap + one dial per pass instead of a busy check; concurrent channels are
    bounded by the trunk's own limit."""
    if "call:sip" in sent_accounts:
        return _postpone_call(job, "a SIP call was already dialled this pass")
    if _call_cap_reached(None, cfg):
        return _postpone_call(job, "daily call cap reached for the SIP trunk")
    if _call_gap_blocked(None, cfg):
        return _postpone_call(job, "call gap has not elapsed")

    contact = job.contact
    extra = dict(payload.get("extra") or {})
    if payload.get("script"):
        extra["script"] = str(payload["script"])[:2000]
    if payload.get("voice_provider"):
        extra["voice_provider"] = str(payload["voice_provider"])
        extra["voice"] = str(payload.get("voice") or "")
    try:
        from calls import lk  # lazy: pulls the whole LiveKit SDK (seconds)
        room = lk.dial(job.to_address, contact.name, contact.language, extra)
    except Exception as e:
        return _retry_call(job, f"SIP dial failed: {e}")
    sent_accounts.add("call:sip")
    return _mark_call_placed(job, None, "SIP trunk", room)
