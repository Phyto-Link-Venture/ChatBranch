"""WhatsApp REST API for the dashboard + the Meta webhook.

Endpoints (under /api/whatsapp/):
  GET/POST webhook                       -> Meta verification + event intake
  GET  status                            -> {configured, sandbox, ai_available}
  GET/POST settings                      -> global app settings (POST: super admin)
  GET/POST accounts                      -> list / add WhatsApp accounts (any admin)
  POST accounts/<id>                     -> update an account
  DELETE accounts/<id>                   -> remove an account
  POST accounts/<id>/test                -> verify an account against Meta
  GET  templates?account_id=…            -> approved templates (sandbox: demo set)
  GET  stream                            -> SSE `change` events (all channels)
  GET  threads?limit=N                   -> conversation summaries
  GET  threads/<wa_id>?limit=N&before=…  -> thread with newest N messages (default 50)
  POST threads  {phone,name?,account_id?}-> create/open a thread
  POST threads/<wa_id>/send {text,force?}-> operator sends a text message
  POST threads/<wa_id>/send-template     -> {name,language,variables[]} template send
  POST threads/<wa_id>/send-media        -> multipart (file, caption?) media send
  POST threads/<wa_id>/ai   {enabled}    -> toggle AI auto-reply
  POST threads/<wa_id>/simulate {text}   -> sandbox: fake an inbound contact msg
  POST threads/<wa_id>/call/permission   -> send call-permission request
  POST threads/<wa_id>/call/start        -> start a WhatsApp voice call
  POST threads/<wa_id>/call/end          -> terminate the call
  GET  media/<message_pk>                -> stream a message's media attachment
  GET  channels/health                   -> account + email health monitor
  POST channels/health                   -> probe all accounts now (admin+)
  GET/PUT voice-settings                 -> Twilio REST keys for the voice
                                            channel (PUT: super admin)
  POST voice-settings/test               -> live Twilio account/number probe
  GET/PUT assignments ?channel=<c>       -> contact→account routing, any channel
                                            (PUT: admin+)
  POST assignments/rebalance {channel}   -> even redistribution (admin+)
  POST baileys/status-event              -> gateway connection health push (secret)

Compliance gates (collections): STOP/BERHENTI/"do not contact" keywords flag the
thread opted_out — AI replies + template sends 409, free-text needs force=true.
Quiet hours (Settings → App Keys, server-local HH:MM) skip AI replies and 409
template sends; manual operator sends stay allowed. Free-text/media to a LIVE
thread outside Meta's 24h contact-service window 409s — use a template.

Multi-account: each thread belongs to a WhatsApp account (Whatomate's model);
the webhook routes inbound traffic by value.metadata.phone_number_id.

AUTH: every endpoint except `webhook` and `status` requires an authenticated
requester (bearer token via /api/auth/login — see crm/auth.py). Stricter gates
on top: global settings writes = superadmin, account mutations = admin+. The
X-Role header only works pre-seed (no Profile rows), so a fresh dev checkout
still boots.

Webhook parsing mirrors Whatomate's pkg/whatsapp/webhook.go (entry[].changes[]
with field "messages" -> messages/statuses; call events on the "calls" field).
"""
import hashlib
import hmac
import json
import logging
import mimetypes
import random
import re
import threading
import time
from datetime import timedelta
from pathlib import Path
from uuid import uuid4

from django.db import close_old_connections
from django.db.models import Count, Q
from django.http import (
    FileResponse,
    HttpResponse,
    HttpResponseNotAllowed,
    JsonResponse,
    StreamingHttpResponse,
)
from django.utils import timezone
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac
from crm.models import AccountAssignment, ChannelAccount, Contact, Message, Thread
from flows import engine as flows_engine

from . import ai, health, routing, store, wa_calls, wa_config

logger = logging.getLogger(__name__)

# Collections opt-out keywords (word-boundary, case-insensitive): EN + BM.
OPT_OUT_RE = re.compile(
    r"\b(stop|unsubscribe|berhenti|jangan\s+hubungi|do\s+not\s+contact)\b",
    re.IGNORECASE,
)

# Cold-text guard (Setting cold_text_guard): free-text into a thread with ZERO
# inbound ever, on a conversation-role (personal) number — the cold-outreach
# ban vector Baileys RESEARCH.md documents. Shared wording: the manual-send
# 409 and the queue-job failure use the same strings (flows/queue.py imports
# these).
COLD_TEXT_BLOCK_DETAIL = (
    "This is a personal (conversation) number — contacts must contact it first "
    "(send a reminder with the chat button instead). Admins can override with "
    "force.")
COLD_TEXT_WARN_NOTE = (
    "[Cold outreach warning: first message ever to this contact from a personal "
    "(conversation) number — unsolicited contact is the main ban vector. Safer: "
    "send a reminder template from the business number and let them message "
    "this number first.]")

# Outbound media rules: extension allowlist -> (whatsapp media type, mime).
MAX_MEDIA_BYTES = 16 * 1024 * 1024  # Meta's own cap for most types is 16MB
MEDIA_EXTENSIONS = {
    ".jpg": ("image", "image/jpeg"),
    ".jpeg": ("image", "image/jpeg"),
    ".png": ("image", "image/png"),
    ".webp": ("image", "image/webp"),
    ".pdf": ("document", "application/pdf"),
    ".mp4": ("video", "video/mp4"),
    ".ogg": ("audio", "audio/ogg"),
    ".mp3": ("audio", "audio/mpeg"),
}

# Sandbox template set — neutral stand-ins for the WABA's real approved
# templates so the whole template flow works with zero Meta creds. Replace
# them with your own once a live WABA is linked.
DEMO_TEMPLATES = [
    {
        "name": "order_confirmation", "language": "en",
        "category": "UTILITY", "status": "APPROVED",
        "body": ("Hi {{1}}, thanks for your order {{2}}. We'll have it ready by "
                 "{{3}}. Reply here if you have any questions."),
    },
    {
        "name": "delivery_update", "language": "en",
        "category": "UTILITY", "status": "APPROVED",
        "body": ("Hi {{1}}, an update on {{2}}: it's on the way and should arrive "
                 "on {{3}}. Reply here if anything needs to change."),
    },
    {
        "name": "appointment_reminder", "language": "en",
        "category": "UTILITY", "status": "APPROVED",
        "body": ("Hi {{1}}, a reminder about {{2}} on {{3}}. Reply here to "
                 "reschedule."),
    },
]


def _json_body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except ValueError:  # covers JSONDecodeError AND non-UTF-8 bodies
        return {}


def _auth_required(request) -> JsonResponse | None:
    """401 unless the requester carries a role (any of superadmin/admin/agent).
    Returns the error response to send, or None when authenticated."""
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _is_superadmin(request) -> bool:
    return rbac.role_from_request(request) == "superadmin"


def _can_manage(request) -> bool:
    return rbac.role_from_request(request) in ("superadmin", "admin")


def _requester(request) -> str:
    return rbac.requester_name(request)


def _thread_account(thread: dict) -> dict | None:
    """The thread's owning account (or the default), if actually configured."""
    account = wa_config.get_account(thread.get("account_id"))
    return account if wa_config.account_configured(account) else None


def _routed_account(thread: dict) -> dict | None:
    """The account this thread should SEND from: the thread's own account,
    unless the health monitor marked it banned/disconnected — then fail the
    contact over (routing.account_for_contact) and move the thread to the
    new account. No failover target -> keep the current account (the send
    fails loudly instead of silently going sandbox). Exception-safe: a
    routing bug must never break the plain send path."""
    account = _thread_account(thread)
    if account is None:
        return None  # sandbox
    pk = str(account.get("id") or "")
    if not pk.isdigit():
        return account  # env-default pseudo-account: no health row, no failover
    try:
        row = ChannelAccount.objects.filter(pk=int(pk)).first()
        if row is None or row.health_status not in routing.INELIGIBLE_STATUSES:
            return account
        contact = store.contact_for_thread(thread.get("wa_id", ""))
        # Role-constrained failover: a dead CONVERSATION number may only fail
        # over to another conversation number (the reminder cannon must not
        # start carrying AI negotiation). Reminder/cloud failover unchanged.
        role = ("conversation"
                if wa_config.account_role(account) == "conversation" else None)
        new = (routing.account_for_contact(contact, role=role)
               if contact is not None else None)
        if new is not None and str(new.pk) != pk:
            store.update_thread(thread["wa_id"], {"account_id": str(new.pk)})
            moved = wa_config.get_account(str(new.pk))
            if wa_config.account_configured(moved):
                return moved
        return account
    except Exception:
        logger.exception("whatsapp: send-time routing failed for %s", thread.get("wa_id"))
        return account


def _template_send_account(thread: dict) -> tuple[dict | None, str | None]:
    """Send-from account for a TEMPLATE send — Cloud API only (Baileys cannot
    carry templates). Healthy Cloud thread account -> use it as-is. Otherwise
    route to an eligible Cloud account (real failover when the old account is
    dead; capability override when the thread sits on a healthy Baileys
    number) and move the thread so the contact's replies stay consistent.
    No eligible Cloud account -> (None, reason): the send must fail with a
    clear message, never silently re-point to Baileys (seen live 2026-07-17)."""
    account = _thread_account(thread)
    if account is None:
        return None, None  # sandbox thread — demo template path
    pk = str(account.get("id") or "")
    if not pk.isdigit():
        return account, None  # env-default pseudo-account is always cloud
    try:
        row = ChannelAccount.objects.filter(pk=int(pk)).first()
        if (row is not None and (row.provider or "cloud") == "cloud"
                and row.health_status not in routing.INELIGIBLE_STATUSES):
            return account, None
        contact = store.contact_for_thread(thread.get("wa_id", ""))
        # Templates are the reminder cannon's job: prefer a reminder-role
        # Cloud account, fall back to ANY eligible Cloud account.
        new = None
        if contact is not None:
            new = routing.account_for_contact(contact, provider="cloud",
                                               role="reminder")
            if new is None:
                new = routing.account_for_contact(contact, provider="cloud")
        if new is None:
            pool = (routing.eligible_accounts("whatsapp", provider="cloud",
                                              role="reminder")
                    or routing.eligible_accounts("whatsapp", provider="cloud"))
            new = routing._least_loaded(pool, "whatsapp") if pool else None
        if new is not None:
            store.update_thread(thread["wa_id"], {"account_id": str(new.pk)})
            store.append_message(
                thread["wa_id"], "out",
                f"[Template requires the Cloud API — this chat now sends via {new.name}.]",
                source="system")
            moved = wa_config.get_account(str(new.pk))
            if wa_config.account_configured(moved):
                return moved, None
        return None, ("No healthy Cloud API account available — templates are "
                      "Cloud-only. Reconnect a Cloud account under Settings → "
                      "Channels, or send free-form text instead (Baileys numbers "
                      "can text anytime).")
    except Exception:
        logger.exception("whatsapp: template routing failed for %s", thread.get("wa_id"))
        return account, None  # safety net: old behavior (send fails loudly)


def _template_variable_count(body: str) -> int:
    """Highest {{n}} placeholder index in a template body (0 = none)."""
    nums = [int(n) for n in re.findall(r"\{\{\s*(\d+)\s*\}\}", body or "")]
    return max(nums) if nums else 0


def _render_template_body(body: str, variables: list) -> str:
    """Fill {{1}}/{{2}}… placeholders from the positional variables list.
    Missing variables leave the placeholder visible rather than erroring."""
    def sub(match):
        i = int(match.group(1)) - 1
        return str(variables[i]) if 0 <= i < len(variables) else match.group(0)

    return re.sub(r"\{\{\s*(\d+)\s*\}\}", sub, body or "")


def _demo_template(name: str) -> dict | None:
    """Sandbox template lookup — the built-in demo set plus any templates
    created from Settings → Message Templates while no WABA is linked."""
    found = next((t for t in DEMO_TEMPLATES if t["name"] == name), None)
    if found:
        return found
    return next((t for t in wa_config.custom_templates() if t.get("name") == name), None)


def _live_template_body(account: dict, name: str, language: str) -> str:
    """Best-effort BODY text lookup for a live template so the stored message
    shows what the contact actually received. Failures (no waba_id, network)
    degrade to a placeholder — never block the send."""
    if not account.get("waba_id"):
        return ""
    from . import client
    try:
        for t in client.list_templates(account):
            if t.get("name") == name and (not language or t.get("language") == language):
                for comp in t.get("components") or []:
                    if (comp.get("type") or "").upper() == "BODY":
                        return comp.get("text", "")
    except Exception:
        logger.info("whatsapp: template body lookup failed for %s", name)
    return ""


def _apply_opt_out(wa_id: str, text: str) -> bool:
    """Detect opt-out keywords in an inbound message. Flags the thread
    opted_out, disables AI auto-reply, and leaves a system note + audit trail.
    Returns True when the message is an opt-out (callers skip the AI reply)."""
    if not text or not OPT_OUT_RE.search(text):
        return False
    thread = store.load_thread(wa_id)
    if thread and not thread.get("opted_out"):
        store.update_thread(wa_id, {"opted_out": True, "ai_enabled": False})
        store.append_message(
            wa_id, "out",
            "[Contact opted out of WhatsApp contact — AI replies and template "
            "sends are blocked; manual sends require an explicit override.]",
            source="system",
        )
        rbac.audit(None, "thread.opt_out", target=f"whatsapp:{wa_id}", detail=text[:200])
        rbac.notify(("superadmin", "admin", "agent"), "system",
                    f"WhatsApp opt-out: {thread.get('name', wa_id)}", text[:200],
                    f"/chats?channel=whatsapp&key={wa_id}", category="messages")
    return True


def _maybe_ai_reply(thread: dict) -> dict | None:
    """If AI auto-reply is on and a provider is configured, answer the last
    inbound message; send for real when the thread's account is configured.

    Queued mode (Setting ai_reply_mode="queued", the default): on a LIVE
    conversation-role account the reply is NOT sent from here — it is
    enqueued as a kind="ai_reply" OutboundJob and the flows queue generates
    + sends it with per-account pacing (see _queue_ai_reply). Sandbox
    threads and instant mode keep today's inline behaviour."""
    if thread.get("opted_out"):
        return None  # compliance: opted-out contacts get no AI contact
    if not thread.get("contact_id"):
        return None  # unknown contact — not a contact of ours, no auto-reply
    if not thread.get("ai_enabled") or not wa_config.ai_available():
        return None
    if _flow_owns_reply(thread):
        return None  # the flow builder's logic answers this reply
    if _queue_ai_reply(thread):
        return None  # queued mode: the outbound queue answers, paced
    if wa_config.in_quiet_hours():
        logger.info("whatsapp: AI auto-reply skipped for %s — quiet hours",
                    thread.get("wa_id"))
        return None
    try:
        reply = ai.generate_reply(thread)
    except Exception as e:  # provider/network errors must never kill the webhook
        # short one-liner — a full 429 dump in the thread reads like a SEND failure
        err = str(e).splitlines()[0][:120] if str(e) else "unknown error"
        return store.append_message(
            thread["wa_id"], "out", f"[AI reply unavailable: {err}]",
            source="system", status="failed",
        )
    if not reply:
        return None
    account = _routed_account(thread)
    if account:
        from . import client
        try:
            msg_id = client.send_text(account, thread["wa_id"], reply)
        except Exception as e:
            health.record_send_failure(account.get("id"), str(e))
            return store.append_message(
                thread["wa_id"], "out", f"[send failed: {e}]", source="system", status="failed"
            )
        health.record_send_ok(account.get("id"))
        return store.append_message(thread["wa_id"], "out", reply, source="ai", msg_id=msg_id)
    return store.append_message(thread["wa_id"], "out", reply, source="ai", status="sandbox")


# Humanized auto-reply: instant bot answers read as robotic AND flag the
# account to WhatsApp's anti-spam. Randomized think-time before each reply.
AI_REPLY_DELAY_RANGE = (4.0, 12.0)  # seconds


def _queue_ai_reply(thread: dict) -> bool:
    """Queued AI-reply mode — "one human per account": each conversation-role
    number answers ONE message at a time with the queue's randomized
    think-time across all its chats. Enqueues a kind="ai_reply" job (the
    reply text is generated at SEND time, so several contact messages get
    one coherent answer). Dedupe: one pending job per thread. Quiet hours
    don't drop the reply — the job is parked until the working window opens
    (not_before = quiet end). Returns True when the reply is (already)
    queued; False = caller keeps the instant path (sandbox, instant mode,
    reminder-role accounts)."""
    try:
        if wa_config.ai_reply_mode() != "queued":
            return False
        account = _thread_account(thread)
        if account is None:
            return False  # sandbox: demos must not lag
        if wa_config.account_role(account) != "conversation":
            return False
        wa_id = str(thread.get("wa_id") or "")
        from crm.models import OutboundJob
        if OutboundJob.objects.filter(status="queued", kind="ai_reply",
                                      to_address=wa_id).exists():
            return True  # the pending job will answer this message too
        from flows import queue as flows_queue
        job = flows_queue.enqueue("whatsapp", "ai_reply",
                                  store.contact_for_thread(wa_id), wa_id, {})
        if wa_config.in_quiet_hours():
            # overnight inbound gets answered when the working window opens
            job.not_before = flows_queue._quiet_resume_at()
            job.save(update_fields=["not_before", "updated_at"])
        return True
    except Exception:
        # a queue bug must never mute the bot — fall back to the instant path
        logger.exception("whatsapp: ai-reply enqueue failed for %s",
                         thread.get("wa_id"))
        return False


# Business-number redirect (S3): the reminder number does no AI conversation —
# an inbound there gets ONE nudge per 24h pointing at the contact's assigned
# conversation number. The marker string doubles as the 24h-throttle probe.
REDIRECT_MARKER = "continue this conversation with our service number"
# Support variant (routing rule business_redirect_target="support"): point the
# contact at human contact support instead. Same probe-string throttling.
SUPPORT_MARKER = "reach our customer support team"


def _maybe_business_redirect(thread: dict) -> bool:
    """An inbound just landed on a reminder-role account with AI off: queue
    ONE auto-answer (window is open — they just wrote in), at most once per
    thread per 24h. What it says follows business_redirect_target: a wa.me
    pointer to the contact's conversation number, a contact-support pointer,
    or the operator's own approved template (business_redirect_template).
    Sent through the outbound queue (source=system, pinned to THIS account)
    so pacing + working hours apply. The normal operator notification fans
    out from the webhook regardless. Returns True when a reply was
    enqueued."""
    try:
        if not wa_config.business_redirect_enabled():
            return False
        if thread.get("opted_out") or thread.get("ai_enabled"):
            return False
        account = _thread_account(thread)
        if account is None or wa_config.account_role(account) != "reminder":
            return False
        wa_id = str(thread.get("wa_id") or "")
        cutoff = timezone.now() - timedelta(hours=24)
        if (Message.objects.filter(thread__channel="whatsapp", thread__key=wa_id,
                                   direction="out", created_at__gte=cutoff)
            .filter(Q(text__contains=REDIRECT_MARKER) |
                    Q(text__contains=SUPPORT_MARKER)).exists()):
            return False  # already redirected within 24h
        from crm.models import OutboundJob
        # one queued at a time + once per 24h — checked on the JOB rows (not
        # message text) so the template variant, whose rendered body carries
        # no marker string, throttles exactly like the text nudges
        if (OutboundJob.objects.filter(to_address=wa_id, payload__redirect=True)
                .filter(Q(status="queued") |
                        Q(status="sent", created_at__gte=cutoff)).exists()):
            return False  # one is on its way, or already answered within 24h
        contact = store.contact_for_thread(wa_id)
        if contact is None:
            return False  # unknown contact — not a contact, no auto-answer
        if wa_config.business_redirect_target() == "template":
            template = wa_config.business_redirect_template()
            if template:
                from flows import queue as flows_queue
                flows_queue.enqueue("whatsapp", "template", contact, wa_id,
                                    {"name": template, "language": "en",
                                     "variables": {}, "source": "system",
                                     "redirect": True,
                                     # answer from the number they wrote to
                                     "account_id": str(account.get("id") or "")})
                return True
            # no template picked yet — degrade to the wa.me handoff below
        if wa_config.business_redirect_target() == "support":
            # human-support variant: no conversation number involved — tell
            # the contact the line is automated and where a person answers
            from flows import queue as flows_queue
            support = wa_config.support_contact()
            text = ("Thanks for your message! This number sends automated "
                    f"reminders and isn't monitored — please {SUPPORT_MARKER}"
                    + (f": {support}" if support else " for help") + ".")
            flows_queue.enqueue("whatsapp", "text", contact, wa_id,
                                {"text": text, "source": "system", "redirect": True,
                                 "account_id": str(account.get("id") or "")})
            return True
        conv = (routing.account_for_contact(contact, role="conversation")
                if contact is not None else None)
        if conv is None:
            pool = routing.eligible_accounts("whatsapp", role="conversation")
            conv = routing._least_loaded(pool, "whatsapp") if pool else None
        if conv is None:
            return False  # nowhere to point them — the notification stands
        number = store.normalize_wa_id(conv.external_id or "")
        if not number:
            # Baileys row without a stored number: a wa.me link is impossible,
            # so skip the redirect and let the operator notification stand.
            return False
        text = ("Thanks for your message! Please "
                f"{REDIRECT_MARKER} — wa.me/{number}")
        from flows import queue as flows_queue
        flows_queue.enqueue("whatsapp", "text", contact, wa_id,
                            {"text": text, "source": "system", "redirect": True,
                             # answer from the number they actually wrote to
                             "account_id": str(account.get("id") or "")})
        return True
    except Exception:  # never break the inbound path over a nicety
        logger.exception("whatsapp: business redirect failed for %s",
                         thread.get("wa_id"))
        return False


def _flow_owns_reply(thread: dict) -> bool:
    """True when a Live flow run is parked waiting for THIS contact's reply —
    the flow engine answers per the builder's logic; the generic AI must not
    talk over it.

    Also true when a run CONSUMED the thread's latest inbound: the AI check
    runs seconds after the webhook (humanized delay / queued drain), by which
    time the resumed run may already be done — without this the contact gets
    the flow's answer (e.g. an auto call) AND an AI text for the same message
    (observed live: text landed as the call was ringing)."""
    contact_id = thread.get("contact_id")
    if not contact_id:
        return False
    from django.db.models import Q as _Q

    from crm.models import FlowRun
    if FlowRun.objects.filter(contact_id=contact_id, status="waiting",
                              wait_for="reply").exists():
        return True
    last_in = (Message.objects.filter(thread__channel="whatsapp",
                                      thread__key=str(thread.get("wa_id") or ""),
                                      direction="in")
               .order_by("-created_at").values_list("created_at", flat=True).first())
    if last_in is None:
        return False
    return (FlowRun.objects.filter(contact_id=contact_id,
                                   context__answered=True)
            .filter(_Q(status="running") | _Q(finished_at__gte=last_in))
            .exists())


def _ai_reply_async(wa_id: str) -> None:
    """Run the AI auto-reply OFF the webhook request path. Meta retries slow
    webhooks (and disables flaky ones), so the provider round-trip must never
    delay the 200 response. /simulate stays synchronous — the dashboard wants
    the reply in its response body."""
    def run():
        try:
            time.sleep(random.uniform(*AI_REPLY_DELAY_RANGE))
            thread = store.load_thread(wa_id)
            if thread:
                # reminder-number inbound with AI off → wa.me nudge instead
                _maybe_business_redirect(thread)
                _maybe_ai_reply(thread)
        except Exception:  # already logged per-message; never crash the thread
            logger.exception("whatsapp: async AI reply failed for %s", wa_id)
        finally:
            close_old_connections()

    threading.Thread(target=run, daemon=True).start()


def _inbound_side_effects(account: dict, wa_id: str, wamid: str,
                          media: dict | None = None) -> None:
    """Live-account housekeeping for one inbound message: mark_read (blue
    ticks) and media download. Both best-effort — failures are logged, never
    raised — and run OFF the webhook path (_inbound_side_effects_async) so
    they can never delay Meta's 200 ack."""
    from . import client
    if wamid:
        try:
            client.mark_read(account, wamid)
        except Exception as e:
            logger.info("whatsapp: mark_read failed for %s: %s", wamid, e)
    if media and media.get("id"):
        try:
            path = client.download_media(account, media["id"], store.MEDIA_DIR / wa_id)
            store.update_message_media(wa_id, wamid, media.get("type", ""),
                                       f"{wa_id}/{path.name}")
        except Exception as e:
            logger.warning("whatsapp: media download failed for %s: %s", media.get("id"), e)


def _inbound_side_effects_async(account: dict, wa_id: str, wamid: str,
                                media: dict | None = None) -> None:
    def run():
        try:
            _inbound_side_effects(account, wa_id, wamid, media)
        finally:
            close_old_connections()

    threading.Thread(target=run, daemon=True).start()


# ---- webhook -----------------------------------------------------------------

def _signature_ok(request) -> bool:
    """X-Hub-Signature-256 check against any account's app secret (Meta signs
    with the app-level secret). Once ANY account is configured a valid
    signature is REQUIRED — live accounts must set app_secret (SETUP.md).
    Sandbox (no configured account) stays permissive so /simulate-style local
    posts keep working."""
    secrets = [a.get("app_secret", "") for a in wa_config.list_accounts() if a.get("app_secret")]
    if not secrets:
        # No secret anywhere: fine in sandbox; a hard fail when live, because
        # an unsigned webhook would accept forged events from anyone.
        return not wa_config.is_configured()
    header = request.headers.get("X-Hub-Signature-256", "")
    if not header.startswith("sha256="):
        return False
    given = header[len("sha256="):]
    return any(
        hmac.compare_digest(given, hmac.new(s.encode(), request.body, hashlib.sha256).hexdigest())
        for s in secrets
    )


@csrf_exempt
def webhook(request):
    if request.method == "GET":  # Meta verification handshake
        params = request.GET
        if (
            params.get("hub.mode") == "subscribe"
            and params.get("hub.verify_token") == wa_config.verify_token()
        ):
            return HttpResponse(params.get("hub.challenge", ""))
        return HttpResponse("verification failed", status=403)

    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _signature_ok(request):
        return JsonResponse({"detail": "bad signature"}, status=403)

    payload = _json_body(request)
    for entry in payload.get("entry", []):
        for change in entry.get("changes", []):
            # One malformed event must not 500 the whole batch — Meta disables
            # webhooks that keep failing, which would kill ALL inbound traffic.
            try:
                _process_change(change)
            except Exception:
                logger.exception(
                    "whatsapp webhook: failed processing change (field=%s)",
                    (change or {}).get("field"),
                )

    return JsonResponse({"ok": True})


def _process_change(change: dict) -> None:
    """Handle one entry[].changes[] block from a webhook POST."""
    value = change.get("value", {})
    field = change.get("field")
    # route to the owning account by the receiving business number
    pnid = (value.get("metadata") or {}).get("phone_number_id", "")
    account = wa_config.find_account_by_phone_number_id(pnid) if pnid else None
    account_id = account.get("id") if account else None
    if pnid and account is None:
        # an unknown business number — creating threads here would misattribute
        # conversations (and replies would go out via the wrong account)
        logger.warning("whatsapp webhook: no account for phone_number_id=%s — skipping", pnid)
        return

    if field == "messages":
        if account is not None and value.get("messages"):
            # an inbound through this account proves it works (channel monitor)
            health.record_inbound(account_id)
        contact_name = ""
        contacts = value.get("contacts") or []
        if contacts:
            contact_name = (contacts[0].get("profile") or {}).get("name", "")

        for msg in value.get("messages", []):
            wa_id = msg.get("from", "")
            mtype = msg.get("type")
            media_info = None
            if mtype == "text":
                text = (msg.get("text") or {}).get("body", "")
            elif mtype == "interactive":
                inter = msg.get("interactive") or {}
                reply = inter.get("button_reply") or inter.get("list_reply") or {}
                text = reply.get("title", "") or f"[{inter.get('type')}]"
            elif mtype in ("image", "document", "audio", "video"):
                blob = msg.get(mtype) or {}
                media_info = {"id": blob.get("id", ""), "type": mtype}
                text = blob.get("caption") or f"[{mtype}]"
            else:
                text = f"[{mtype} message]"
            if not wa_id:
                continue
            wamid = msg.get("id", "")
            if wamid and store.message_exists(wa_id, wamid):
                continue  # Meta redelivery of a message we already stored
            thread = store.get_or_create_thread(wa_id, name=contact_name, account_id=account_id)
            # Meta's own send time (epoch s) — webhooks may arrive hours late,
            # and the 24h window must be computed from when the contact sent it
            store.append_message(wa_id, "in", text, source="customer", msg_id=wamid,
                                 provider_ts=msg.get("timestamp"))
            rbac.notify(("superadmin", "admin", "agent"), "message",
                        f"WhatsApp: {thread.get('name', wa_id)}", text[:200],
                        f"/chats?channel=whatsapp&key={wa_id}")
            rbac.audit(None, "message.received", target=f"whatsapp:{wa_id}", detail=text[:200])
            if wa_config.account_configured(account):
                # blue ticks + media download, off the ack path, best-effort
                _inbound_side_effects_async(account, store.normalize_wa_id(wa_id),
                                            wamid, media_info)
            if _apply_opt_out(wa_id, text):
                continue  # STOP/BERHENTI/…: never answer an opt-out with AI
            _ai_reply_async(wa_id)
            # resume any flow runs parked on this contact's reply (off the
            # ack path, same rule as the AI reply)
            flows_engine.on_inbound_async("whatsapp", store.normalize_wa_id(wa_id), text)

        for status_evt in value.get("statuses", []):
            wa_id = status_evt.get("recipient_id", "")
            wamid = status_evt.get("id", "")
            if not (wa_id and wamid):
                continue
            errors = status_evt.get("errors") or []
            if errors:
                # delivery failure (e.g. 131047 window closed) — mark the
                # message failed and tell the operator why in the thread
                store.update_message_status(wa_id, wamid, "failed")
                err = errors[0] or {}
                summary = ": ".join(part for part in (
                    err.get("title", ""),
                    (err.get("error_data") or {}).get("details") or err.get("message", ""),
                ) if part) or f"error {err.get('code', 'unknown')}"
                store.append_message(wa_id, "out", f"[delivery failed — {summary}]",
                                     source="system", status="failed")
                if err.get("code") == 131047:
                    # Meta says the 24h window is CLOSED — authoritative proof
                    # our newest stored inbound time is stale (late webhook).
                    # Snap the local window shut so the composer flips to
                    # template mode instead of failing again.
                    store.close_window(wa_id)
                if account is not None:
                    # account-level failure codes (bans, token) surface here
                    # too; per-recipient codes (131047 etc.) are no-ops inside
                    health.record_send_failure(account_id, summary,
                                               code=err.get("code"))
            else:
                store.update_message_status(wa_id, wamid, status_evt.get("status", ""))

    elif field == "calls":
        for call in value.get("calls", []):
            wa_id = call.get("from") or call.get("to") or ""
            # accepted/connect events carry the contact's SDP answer,
            # which the media bridge (whatsapp/bridge.py) is waiting on
            session = call.get("session") or {}
            sdp = session.get("sdp", "") if session.get("sdp_type") == "answer" else ""
            if wa_id:
                wa_calls.handle_call_event(
                    wa_id, call.get("event", "unknown"), call.get("id", ""), sdp
                )

    elif field == "call_permissions":
        perm = value.get("call_permission") or value.get("permission") or {}
        wa_id = value.get("user_wa_id") or perm.get("user_wa_id") or ""
        status_val = perm.get("status") or value.get("status") or ""
        if wa_id and status_val:
            granted = status_val in ("temporary", "permanent", "accepted")
            store.update_thread(
                wa_id, {"call": {"permission": "granted" if granted else "none"}}
            )


# ---- status + global settings --------------------------------------------------

def status(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    return JsonResponse({
        "configured": wa_config.is_configured(),
        "sandbox": not wa_config.is_configured(),
        "ai_available": wa_config.ai_available(),
        "accounts": len(wa_config.list_accounts()),
    })


def _mask(secret: str) -> str:
    if not secret:
        return ""
    return f"••••{secret[-4:]}" if len(secret) >= 8 else "••••"


def _global_payload() -> dict:
    s = wa_config.global_settings()
    return {
        # accessor, not the raw merge: it swaps the insecure shipped default
        # for the auto-generated secret (wa_config.verify_token docstring)
        "verify_token": wa_config.verify_token(),
        "chat_model": s["chat_model"],
        # secrets never leave the server in full — masked for display only
        "openai_api_key_masked": _mask(s["openai_api_key"]),
        "openrouter_api_key_masked": _mask(s["openrouter_api_key"]),
        "google_api_key_masked": _mask(s["google_api_key"]),
        "ilmu_api_key_masked": _mask(s["ilmu_api_key"]),
        "chat_provider_pref": s["chat_provider_pref"],
        # quiet hours are not secrets — full values, "HH:MM" server-local time
        "quiet_start": s["quiet_start"],
        "quiet_end": s["quiet_end"],
        "configured": wa_config.is_configured(),
        "ai_available": wa_config.ai_available(),
        "webhook_path": "/api/whatsapp/webhook",
    }


@csrf_exempt
def wa_settings(request):
    """Global app settings (AI keys, webhook verify token, chat model).
    Reads need any authenticated role; writes are SUPER ADMIN only."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_global_payload())
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _is_superadmin(request):
        return JsonResponse({"detail": "Super admin role required to change app settings."}, status=403)
    body = _json_body(request)
    patch = {}
    for key in wa_config.GLOBAL_KEYS:
        value = body.get(key)
        # ignore untouched masked placeholders so re-saving doesn't clobber secrets
        if isinstance(value, str) and "••" not in value:
            patch[key] = value
    for key in ("quiet_start", "quiet_end"):
        value = (patch.get(key) or "").strip()
        if value and wa_config.parse_hhmm(value) is None:
            return JsonResponse({"detail": f"{key} must be 'HH:MM' (24h) or empty."}, status=400)
    pref = (patch.get("chat_provider_pref") or "").strip().lower()
    if pref and pref not in wa_config.CHAT_PROVIDERS:
        return JsonResponse({"detail": "chat_provider_pref must be one of "
                                       f"{', '.join(wa_config.CHAT_PROVIDERS)} or empty (auto)."},
                            status=400)
    wa_config.save_global_settings(patch)
    rbac.audit(request, "settings.update", target="app-keys", detail=f"keys: {sorted(patch)}")
    rbac.notify(("superadmin",), "system", "App settings updated",
                f"Changed by {_requester(request)}: {', '.join(sorted(patch)) or 'nothing'}", "/settings?tab=appkeys")
    return JsonResponse(_global_payload())


# ---- WhatsApp account registry --------------------------------------------------

def _account_payload(a: dict) -> dict:
    return {
        "id": a.get("id"),
        "provider": a.get("provider", "cloud"),
        # operating role (reminder cannon vs conversation layer) — always
        # resolved, so unset config defaults by provider
        "role": wa_config.account_role(a),
        "name": a.get("name"),
        "phone_number_id": a.get("phone_number_id"),
        "waba_id": a.get("waba_id", ""),  # not a secret; needed for templates
        "api_version": a.get("api_version"),
        "owner": a.get("owner"),
        "created": a.get("created"),
        "access_token_masked": _mask(a.get("access_token", "")),
        "app_secret_masked": _mask(a.get("app_secret", "")),
        "configured": wa_config.account_configured(a),
        "env_managed": a.get("id") == wa_config.ENV_ACCOUNT_ID,
    }


def _invalid_role(body: dict) -> JsonResponse | None:
    """400 when the payload carries a bad account role, else None."""
    if "role" in body and str(body.get("role") or "").strip().lower() \
            not in wa_config.ACCOUNT_ROLES:
        return JsonResponse({"detail": "role must be 'reminder' or 'conversation'."},
                            status=400)
    return None


@csrf_exempt
def accounts(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"accounts": [_account_payload(a) for a in wa_config.list_accounts()]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage accounts."}, status=403)
    body = _json_body(request)
    if body.get("provider") == "baileys":
        if not body.get("name"):
            return JsonResponse({"detail": "name is required for a Baileys account."}, status=400)
    elif not (body.get("phone_number_id") and body.get("access_token")):
        return JsonResponse({"detail": "phone_number_id and access_token are required."}, status=400)
    denied = _invalid_role(body)
    if denied:
        return denied
    body.setdefault("owner", _requester(request))
    account = wa_config.create_account(body)
    rbac.audit(request, "account.create", target=f"whatsapp:{account['name']}")
    rbac.notify(("superadmin", "admin"), "system",
                f"WhatsApp account linked: {account['name']}",
                f"By {_requester(request)}", "/settings?tab=whatsapp")
    return JsonResponse(_account_payload(account))


@csrf_exempt
def account_detail(request, account_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method in ("POST", "DELETE") and not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage accounts."}, status=403)
    if request.method == "POST":
        body = _json_body(request)
        denied = _invalid_role(body)
        if denied:
            return denied
        patch = {k: v for k, v in body.items()
                 if k in wa_config.ACCOUNT_KEYS and isinstance(v, str) and "••" not in v}
        if isinstance(body.get("role"), str):
            patch["role"] = body["role"]  # stored in config; no health reset
        old_role = wa_config.account_role(wa_config.get_account(account_id))
        updated = wa_config.update_account(account_id, patch)
        if updated is None:
            detail = ("This account comes from backend/.env — edit the .env file instead."
                      if account_id == wa_config.ENV_ACCOUNT_ID else "Account not found.")
            return JsonResponse({"detail": detail}, status=400)
        # Becoming the reminder cannon retires the chatbot across the account's
        # existing threads too. The AI tick is per account (Thread.ai_overrides
        # + role default): stamp an explicit OFF tick for THIS account on every
        # thread that was effectively on (old conversation role default = on),
        # so the retirement is durable — flipping the role back later never
        # re-enables anything by itself. (updated_at bump keeps SSE fresh.)
        if (wa_config.account_role(updated) == wa_config.ROLE_REMINDER
                and old_role != wa_config.ROLE_REMINDER):
            flipped = 0
            for t in Thread.objects.filter(channel="whatsapp", account_id=account_id):
                overrides = dict(t.ai_overrides or {})
                override = overrides.get(str(account_id))
                was_on = override if isinstance(override, bool) else True
                if not was_on:
                    continue
                overrides[str(account_id)] = False
                flipped += 1
                Thread.objects.filter(pk=t.pk).update(
                    ai_overrides=overrides, ai_enabled=False,
                    updated_at=timezone.now())
            if flipped:
                rbac.audit(request, "account.role", target=f"whatsapp:{updated['name']}",
                           detail=f"reminder role — chatbot off on {flipped} thread(s)")
        return JsonResponse(_account_payload(updated))
    if request.method == "DELETE":
        if not wa_config.delete_account(account_id):
            detail = ("This account comes from backend/.env — clear the env keys instead."
                      if account_id == wa_config.ENV_ACCOUNT_ID else "Account not found.")
            return JsonResponse({"detail": detail}, status=400)
        rbac.audit(request, "account.delete", target=f"whatsapp:{account_id}")
        return JsonResponse({"ok": True})
    return HttpResponseNotAllowed(["POST", "DELETE"])


@csrf_exempt
def account_test(request, account_id: str):
    """Prove an account's credentials work by fetching its number profile."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    account = wa_config.get_account(account_id)
    if not wa_config.account_configured(account):
        return JsonResponse({"detail": "Account not configured."}, status=400)
    from . import client
    if account.get("provider") == "baileys":
        try:
            state = client.baileys_request(f"/accounts/{account['id']}/status", method="GET")
        except Exception as e:
            return JsonResponse({"detail": f"Connection failed: {e}"}, status=502)
        connected = state.get("state") == "connected"
        return JsonResponse({
            "ok": connected,
            "display_phone_number": f"+{state.get('me')}" if state.get("me") else "",
            "verified_name": account.get("name", ""),
            "quality_rating": state.get("state", ""),
        })
    try:
        info = client.get_phone_number_info(account)
    except Exception as e:
        return JsonResponse({"detail": f"Connection failed: {e}"}, status=502)
    return JsonResponse({
        "ok": True,
        "display_phone_number": info.get("display_phone_number", ""),
        "verified_name": info.get("verified_name", ""),
        "quality_rating": info.get("quality_rating", ""),
    })


# ---- Baileys (unofficial WhatsApp Web) — pairing control + inbound push ---------

def _baileys_account_or_error(account_id: str):
    account = wa_config.get_account(account_id)
    if account is None or account.get("provider") != "baileys":
        return None, JsonResponse({"detail": "Not a Baileys account."}, status=404)
    return account, None


@csrf_exempt
def baileys_control(request, account_id: str, action: str):
    """Proxy pairing control to the local gateway so the browser never needs
    direct access (or the shared secret). Actions: status | start | logout."""
    denied = _auth_required(request)
    if denied:
        return denied
    if action == "status":
        if request.method != "GET":
            return HttpResponseNotAllowed(["GET"])
    else:
        if request.method != "POST":
            return HttpResponseNotAllowed(["POST"])
        if not _can_manage(request):
            return JsonResponse({"detail": "Admin role required."}, status=403)
    account, err = _baileys_account_or_error(account_id)
    if err:
        return err
    from . import client
    try:
        data = client.baileys_request(
            f"/accounts/{account['id']}/{action}",
            method="GET" if action == "status" else "POST",
        )
    except Exception as e:
        return JsonResponse({"detail": str(e)}, status=502)
    if action == "logout":
        rbac.audit(request, "account.baileys_logout", target=f"whatsapp:{account['name']}")
    return JsonResponse(data)


@csrf_exempt
def baileys_inbound(request):
    """Inbound push from the Baileys gateway — the webhook-equivalent for
    provider="baileys" accounts. Secured by the shared gateway secret."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if request.headers.get("X-Baileys-Secret") != wa_config.BAILEYS_SECRET:
        return JsonResponse({"detail": "bad secret"}, status=403)
    body = _json_body(request)
    wa_id = store.normalize_wa_id(body.get("wa_id", ""))
    text = str(body.get("text") or "").strip() or "[message]"
    msg_id = str(body.get("msg_id") or "")
    account_id = str(body.get("account_id") or "")
    if not wa_id:
        return JsonResponse({"ok": True, "ignored": True})
    if account_id:
        health.record_inbound(account_id)  # inbound = proof the account works
    if msg_id and store.message_exists(wa_id, msg_id):
        return JsonResponse({"ok": True, "duplicate": True})
    thread = store.get_or_create_thread(wa_id, name=str(body.get("name") or ""),
                                        account_id=account_id)
    store.append_message(wa_id, "in", text, source="customer", msg_id=msg_id)
    rbac.notify(("superadmin", "admin", "agent"), "message",
                f"WhatsApp: {thread.get('name', wa_id)}", text[:200],
                f"/chats?channel=whatsapp&key={wa_id}")
    rbac.audit(None, "message.received", target=f"whatsapp:{wa_id}", detail=text[:200])
    if _apply_opt_out(wa_id, text):
        return JsonResponse({"ok": True, "opted_out": True})
    _ai_reply_async(wa_id)
    flows_engine.on_inbound_async("whatsapp", wa_id, text)
    return JsonResponse({"ok": True})


MAX_BAILEYS_MEDIA_BYTES = 20 * 1024 * 1024  # matches the gateway-side cap


@csrf_exempt
def baileys_media(request):
    """Inbound media push from the Baileys gateway (multipart: wa_id, msg_id,
    media_type, file). The gateway does the decrypt+download — it owns the
    Signal session keys — and Django files the bytes exactly like the Cloud
    webhook's downloads, then attaches them to the already-stored message
    (same two-phase pattern as _inbound_side_effects)."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if request.headers.get("X-Baileys-Secret") != wa_config.BAILEYS_SECRET:
        return JsonResponse({"detail": "bad secret"}, status=403)
    wa_id = store.normalize_wa_id(request.POST.get("wa_id", ""))
    msg_id = str(request.POST.get("msg_id") or "")
    media_type = str(request.POST.get("media_type") or "") or "document"
    upload = request.FILES.get("file")
    if not (wa_id and msg_id and upload):
        return JsonResponse({"ok": True, "ignored": True})
    if upload.size > MAX_BAILEYS_MEDIA_BYTES:
        return JsonResponse({"detail": "File too large."}, status=413)
    safe_name = re.sub(r"[^A-Za-z0-9._-]", "_", upload.name or "file.bin")[-120:] \
        or "file.bin"
    dest_dir = store.MEDIA_DIR / wa_id
    dest_dir.mkdir(parents=True, exist_ok=True)
    dest = dest_dir / safe_name
    stem, dot, ext = safe_name.rpartition(".")
    n = 1
    while dest.exists():
        dest = dest_dir / (f"{stem}-{n}.{ext}" if dot else f"{safe_name}-{n}")
        n += 1
    dest.write_bytes(upload.read())
    store.update_message_media(wa_id, msg_id, media_type, f"{wa_id}/{dest.name}")
    return JsonResponse({"ok": True})


@csrf_exempt
def baileys_call_event(request):
    """Call lifecycle push from the Baileys gateway (ringing/connected/ended)
    — the calling counterpart of baileys_inbound, same shared-secret gate."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if request.headers.get("X-Baileys-Secret") != wa_config.BAILEYS_SECRET:
        return JsonResponse({"detail": "bad secret"}, status=403)
    from . import baileys_calls
    return JsonResponse(baileys_calls.handle_event(_json_body(request)))


@csrf_exempt
def baileys_status_event(request):
    """Connection lifecycle push from the Baileys gateway (connected /
    disconnected / logged_out) — feeds the channel health monitor. Same
    shared-secret gate as /baileys/inbound; the mapping lives in
    health.record_gateway_event (connected→healthy, disconnected→degraded,
    logged_out or 403/forbidden→banned)."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if request.headers.get("X-Baileys-Secret") != wa_config.BAILEYS_SECRET:
        return JsonResponse({"detail": "bad secret"}, status=403)
    body = _json_body(request)
    account_id = str(body.get("account_id") or "")
    # Persist the gateway's OWN paired number. A Baileys account has no
    # external_id (nothing to import it from — the number only exists inside
    # the paired session), so this push is the single place the CRM can learn
    # it. Backs {{account_number}} in flow templates; best-effort, and never
    # allowed to break the health push it rides on.
    number = "".join(ch for ch in str(body.get("number") or "") if ch.isdigit())
    if account_id and number:
        try:
            (ChannelAccount.objects.filter(channel="whatsapp", pk=account_id)
             .exclude(external_id=number).update(external_id=number))
        except Exception:  # bad pk / race — health still matters more
            pass
    result = health.record_gateway_event(
        account_id,
        str(body.get("event") or ""),
        str(body.get("reason") or ""),
    )
    return JsonResponse({"ok": True, **result})


@csrf_exempt
def baileys_message_status(request):
    """Delivery/read receipt push from the Baileys gateway — advances an
    outbound message's tick (sent → delivered → read) exactly like the Cloud
    webhook's `statuses` events do. Same shared-secret gate as /inbound.
    Body: {wa_id, msg_id, status: "delivered"|"read"}."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if request.headers.get("X-Baileys-Secret") != wa_config.BAILEYS_SECRET:
        return JsonResponse({"detail": "bad secret"}, status=403)
    body = _json_body(request)
    wa_id = store.normalize_wa_id(str(body.get("wa_id") or ""))
    msg_id = str(body.get("msg_id") or "")
    status = str(body.get("status") or "")
    if wa_id and msg_id and status in ("delivered", "read", "failed"):
        store.update_message_status(wa_id, msg_id, status)
        return JsonResponse({"ok": True})
    return JsonResponse({"ok": True, "ignored": True})


# ---- threads -------------------------------------------------------------------

STREAM_TICK_SECONDS = 1.5
STREAM_MAX_TICKS = 200  # ~5 min per connection, then the client reconnects


def _inbox_change_token() -> str:
    """Cheap fingerprint of the whole unified inbox. WhatsApp AND messaging
    threads share the crm tables, and every store write path bumps the
    thread's updated_at, so two aggregate queries cover everything."""
    from django.db.models import Count, Max

    t = Thread.objects.aggregate(n=Count("id"), u=Max("updated_at"))
    m = Message.objects.aggregate(n=Count("id"), x=Max("id"))
    u = t["u"].isoformat() if t["u"] else ""
    return f"{t['n']}:{u}:{m['n']}:{m['x'] or 0}"


def stream(request):
    """GET /api/whatsapp/stream — Server-Sent Events. Emits `event: change`
    whenever any inbox thread/message changes so the dashboard re-fetches on
    demand instead of polling the threads endpoints every few seconds. Covers
    all channels (crm.Thread), not just WhatsApp — it lives in this app for
    historical reasons but serves the whole unified inbox.
    Connections are bounded to ~5 min; the client reconnects. Holds one
    dev-server thread per open dashboard tab (WSGI streaming) — fine for the
    POC's handful of operators."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])

    def events():
        last = _inbox_change_token()
        yield f"event: change\ndata: {last}\n\n"
        for _ in range(STREAM_MAX_TICKS):
            time.sleep(STREAM_TICK_SECONDS)
            token = _inbox_change_token()
            if token != last:
                last = token
                yield f"event: change\ndata: {token}\n\n"
            else:
                # keep-alive; also makes a vanished client raise on write so
                # the request thread is reclaimed promptly
                yield ": ping\n\n"

    resp = StreamingHttpResponse(events(), content_type="text/event-stream")
    resp["Cache-Control"] = "no-cache"
    resp["X-Accel-Buffering"] = "no"  # if this ever sits behind nginx
    return resp


@csrf_exempt
def threads(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        limit = None
        raw = (request.GET.get("limit") or "").strip()
        if raw.isdigit() and int(raw) > 0:
            limit = int(raw)
        return JsonResponse({"threads": store.list_threads(limit=limit)})
    if request.method == "POST":
        body = _json_body(request)
        phone = (body.get("phone") or "").strip()
        if not phone:
            return JsonResponse({"detail": "phone is required."}, status=400)
        # Malaysian local format ("011-683 8203") -> E.164 digits. A wrong
        # wa_id sends into the void, so normalize the common typo here.
        digits_only = store.normalize_wa_id(phone)
        if digits_only.startswith("0"):
            phone = f"+60{digits_only[1:]}"
        elif not phone.startswith("+"):
            phone = f"+{digits_only}"
        account = wa_config.get_account(body.get("account_id"))
        account_id = account.get("id") if account else None
        if not body.get("account_id") and store.load_thread(phone) is None:
            # entity-new thread with no explicit account: route by the
            # contact's sticky assignment (health-aware, least-loaded on
            # first contact). Falls back to the single-configured-account
            # rule above when the phone matches no CRM contact.
            try:
                contact = store.contact_for_thread(phone)
                routed = routing.account_for_contact(contact) if contact else None
                if routed is not None:
                    account_id = str(routed.pk)
            except Exception:
                logger.exception("whatsapp: new-thread routing failed for %s", phone)
        thread = store.get_or_create_thread(
            phone, name=body.get("name", ""), account_id=account_id,
        )
        return JsonResponse(thread)
    return HttpResponseNotAllowed(["GET", "POST"])


def thread_detail(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    limit = 50  # newest N messages; older pages via ?before=<cursor>
    raw = (request.GET.get("limit") or "").strip()
    if raw.isdigit() and int(raw) > 0:
        limit = min(int(raw), 500)
    before = (request.GET.get("before") or "").strip() or None
    thread = store.load_thread(wa_id, limit=limit, before=before)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    return JsonResponse(thread)


@csrf_exempt
def send_message(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    text = (body.get("text") or "").strip()
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    thread = store.load_thread(wa_id)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    if thread.get("opted_out") and not body.get("force"):
        return JsonResponse({
            "detail": "Contact opted out of WhatsApp contact (STOP). "
                      "Resend with force=true to override as a manual exception.",
        }, status=409)

    account = _routed_account(thread)
    # Cold-text guard: zero-inbound thread on a conversation-role number.
    cold_note = ""
    if (account and wa_config.account_role(account) == "conversation"
            and not store.thread_has_inbound(wa_id)):
        guard = wa_config.cold_text_guard()
        if guard == "block" and not (body.get("force") and _can_manage(request)):
            return JsonResponse({"detail": COLD_TEXT_BLOCK_DETAIL}, status=409)
        if guard == "warn":
            cold_note = COLD_TEXT_WARN_NOTE
    if account and not thread.get("window_open"):
        return JsonResponse({
            "detail": "WhatsApp 24h window closed — use a template.",
        }, status=409)
    if account:
        from . import client
        try:
            msg_id = client.send_text(account, store.normalize_wa_id(wa_id), text)
        except Exception as e:
            health.record_send_failure(account.get("id"), str(e))
            return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
        health.record_send_ok(account.get("id"))
        msg = store.append_message(wa_id, "out", text, source="agent", msg_id=msg_id)
    else:
        msg = store.append_message(wa_id, "out", text, source="agent", status="sandbox")
    if cold_note:
        store.append_message(wa_id, "out", cold_note, source="system")
    rbac.audit(request, "message.send", target=f"whatsapp:{wa_id}", detail=text[:200])
    return JsonResponse({"message": msg})


@csrf_exempt
def send_template(request, wa_id: str):
    """Send a pre-approved template message — the only way to (re)open a
    conversation outside Meta's 24h window. Sandbox: renders the demo template
    body locally. Blocked while the contact is opted out or in quiet hours."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    name = (body.get("name") or "").strip()
    if not name:
        return JsonResponse({"detail": "name is required."}, status=400)
    language = (body.get("language") or "en").strip()
    variables = [str(v) for v in (body.get("variables") or [])]
    thread = store.load_thread(wa_id)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    if thread.get("opted_out"):
        return JsonResponse({
            "detail": "Contact opted out of WhatsApp contact — template sends are blocked.",
        }, status=409)
    if wa_config.in_quiet_hours():
        return JsonResponse({
            "detail": "Quiet hours are in effect (Settings → App Keys) — "
                      "template sends resume when they end.",
        }, status=409)

    account, route_error = _template_send_account(thread)
    if route_error:
        return JsonResponse({"detail": route_error}, status=409)
    if not variables:
        # No explicit values — fill bound slots from the CRM contact (the
        # Settings → Message Templates bindings), so operators and automations
        # don't retype data the system already has.
        slots = wa_config.template_bindings(_bindings_scope(account)).get(name) or []
        if any(slots):
            variables = _resolve_bindings(wa_id, slots)
    demo = _demo_template(name)
    if account:
        from . import client
        try:
            msg_id = client.send_template(
                account, store.normalize_wa_id(wa_id), name, language, variables)
        except Exception as e:
            health.record_send_failure(account.get("id"), str(e))
            return JsonResponse({"detail": f"Template send failed: {e}"}, status=502)
        health.record_send_ok(account.get("id"))
        body_text = (demo["body"] if demo else "") or _live_template_body(account, name, language)
        rendered = (_render_template_body(body_text, variables) if body_text
                    else f"[template: {name}]" + (f" — {', '.join(variables)}" if variables else ""))
        msg = store.append_message(wa_id, "out", rendered, source="agent", msg_id=msg_id)
    else:
        if demo is None:
            return JsonResponse(
                {"detail": f"Unknown sandbox template '{name}'."}, status=400)
        rendered = _render_template_body(demo["body"], variables)
        msg = store.append_message(wa_id, "out", rendered, source="agent", status="sandbox")
    rbac.audit(request, "message.send", target=f"whatsapp:{wa_id}", detail=f"template:{name}")
    return JsonResponse({"message": msg})


@csrf_exempt
def send_media(request, wa_id: str):
    """Send a media attachment (multipart: file, caption?, force?). Live ->
    upload to Meta then send by media id; sandbox -> stored locally only.
    The file is always kept under data/whatsapp/media/ so the inbox can
    render it either way."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    upload = request.FILES.get("file")
    if upload is None:
        return JsonResponse({"detail": "file is required (multipart/form-data)."}, status=400)
    caption = (request.POST.get("caption") or "").strip()
    force = str(request.POST.get("force") or "").lower() in ("1", "true", "yes")
    ext = Path(upload.name or "").suffix.lower()
    if ext not in MEDIA_EXTENSIONS:
        allowed = ", ".join(sorted(e.lstrip(".") for e in MEDIA_EXTENSIONS))
        return JsonResponse(
            {"detail": f"File type '{ext or '?'}' not allowed. Allowed: {allowed}."}, status=400)
    if upload.size > MAX_MEDIA_BYTES:
        return JsonResponse({"detail": "File too large (max 16 MB)."}, status=400)
    thread = store.load_thread(wa_id)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    if thread.get("opted_out") and not force:
        return JsonResponse({
            "detail": "Contact opted out of WhatsApp contact (STOP). "
                      "Resend with force=true to override as a manual exception.",
        }, status=409)
    account = _routed_account(thread)
    if account and not thread.get("window_open"):
        return JsonResponse({
            "detail": "WhatsApp 24h window closed — use a template.",
        }, status=409)

    media_type, mime = MEDIA_EXTENSIONS[ext]
    content = upload.read()
    key = store.normalize_wa_id(wa_id)
    filename = f"{uuid4().hex}{ext}"
    dest_dir = store.MEDIA_DIR / key
    dest_dir.mkdir(parents=True, exist_ok=True)
    (dest_dir / filename).write_bytes(content)
    rel_path = f"{key}/{filename}"
    text = caption or f"[{media_type}]"

    if account:
        from . import client
        try:
            media_id = client.upload_media(account, content, mime, upload.name or filename)
            msg_id = client.send_media(account, key, media_type, media_id, caption)
        except Exception as e:
            health.record_send_failure(account.get("id"), str(e))
            return JsonResponse({"detail": f"Media send failed: {e}"}, status=502)
        health.record_send_ok(account.get("id"))
        msg = store.append_message(wa_id, "out", text, source="agent", msg_id=msg_id,
                                   media_type=media_type, media_path=rel_path)
    else:
        msg = store.append_message(wa_id, "out", text, source="agent", status="sandbox",
                                   media_type=media_type, media_path=rel_path)
    rbac.audit(request, "message.send", target=f"whatsapp:{wa_id}",
               detail=f"media:{media_type} {text[:150]}")
    return JsonResponse({"message": msg})


def media_file(request, message_pk: int):
    """Stream a message's media attachment (inbound downloads + outbound
    uploads). Auth-gated: attachments are PII, same as message text."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    info = store.get_message_media(message_pk)
    if info is None or not info["path"].exists():
        return JsonResponse({"detail": "Media not found."}, status=404)
    content_type = mimetypes.guess_type(str(info["path"]))[0] or "application/octet-stream"
    return FileResponse(open(info["path"], "rb"), content_type=content_type,
                        filename=info["filename"])


TEMPLATE_NAME_RE = re.compile(r"[a-z0-9_]{1,120}")
TEMPLATE_CATEGORIES = ("UTILITY", "MARKETING", "AUTHENTICATION")
# The flows-engine variable catalog (flows.engine.variables_for) — the CRM
# fields a template {{n}} slot may bind to. "" = the operator fills it by hand.
TEMPLATE_BINDING_KEYS = (
    # contact
    "name", "first_name", "phone", "email", "language", "company",
    # record
    "record_id", "record_type", "record_status", "record_title",
    "amount", "currency", "due_date", "days_until_due", "days_overdue",
    "tracking_url", "link",
    # business
    "business_name", "agent_name", "support_number", "support_email", "website",
    # memory / free-form
    "best_contact_time", "preferred_channel", "last_contact_date", "situation",
    # Resolved from the contact's assigned CONVERSATION number rather than the
    # CRM row — a reminder template blasted from the business number quotes the
    # personal number the contact should actually reply to. Filled by
    # flows.queue._account_vars, which _resolve_bindings merges in below.
    "whatsapp_assignment_account_number", "whatsapp_assignment_account_name",
    "account_number", "account_name",
)


def _template_account(request, body: dict | None = None) -> dict | None:
    """The live template account (configured + waba_id), else None = sandbox.

    Without an explicit ?account_id, resolve from the TEMPLATE-CAPABLE pool
    only (Cloud accounts with a waba_id) — Baileys conversation accounts can
    never hold Meta templates, so they must not make the pick "ambiguous"
    (field case: adding a 2nd personal number silently degraded the flow
    builder's picker to the sandbox demo set). Multiple capable accounts:
    prefer role=reminder, mirroring how template SENDS route."""
    account_id = (body or {}).get("account_id") or request.GET.get("account_id")
    if account_id:
        account = wa_config.get_account(account_id)
        if wa_config.account_configured(account) and account.get("waba_id"):
            return account
        return None
    capable = [a for a in wa_config.list_accounts()
               if wa_config.account_configured(a) and a.get("waba_id")]
    if not capable:
        return None
    preferred = [a for a in capable
                 if wa_config.account_role(a) == wa_config.ROLE_REMINDER]
    return (preferred or capable)[0]


def _bindings_scope(account: dict | None) -> str:
    """Bindings are stored per owning account (template names are per-WABA);
    everything without a live WABA shares the "sandbox" scope."""
    if account and account.get("waba_id"):
        return str(account.get("id"))
    return "sandbox"


def _valid_bindings(raw) -> list[str] | None:
    """Normalize a bindings payload ([key-or-empty per {{n}} slot]); None on
    an unknown key."""
    bindings = [str(b or "").strip() for b in (raw or [])]
    if any(b and b not in TEMPLATE_BINDING_KEYS for b in bindings):
        return None
    return bindings


def _resolve_bindings(wa_id: str, bindings: list[str]) -> list[str]:
    """Fill each bound slot from the thread's CRM contact (same variable
    catalog the flows engine renders). Unbound/unknown slots resolve to ""."""
    contact = store.contact_for_thread(wa_id)
    variables = flows_engine.variables_for(contact)
    # The account-scoped keys are not in variables_for (they depend on routing,
    # not the CRM row). Without this merge a bound slot would resolve to "" and
    # the message would ship a silent blank where a phone number should be.
    from flows.queue import _account_vars

    try:
        variables.update(_account_vars(contact, {}))
    except Exception:  # routing hiccup must never break a template send
        pass
    return [variables.get(b, "") if b else "" for b in bindings]


TEMPLATE_BUTTON_TYPES = ("QUICK_REPLY", "URL", "PHONE_NUMBER")


def _valid_buttons(raw) -> list[dict] | None:
    """Normalize a buttons payload into Meta button dicts; None when invalid.
    Static buttons only (no {{n}} in URLs) — sends then need no button params."""
    if not raw:
        return []
    if not isinstance(raw, list) or len(raw) > 3:
        return None
    buttons = []
    for b in raw:
        if not isinstance(b, dict):
            return None
        btype = str(b.get("type") or "").strip().upper()
        text = str(b.get("text") or "").strip()
        if btype not in TEMPLATE_BUTTON_TYPES or not text or len(text) > 25:
            return None
        button = {"type": btype, "text": text}
        if btype == "URL":
            url = str(b.get("url") or "").strip()
            if not url.startswith(("http://", "https://")) or "{{" in url:
                return None
            button["url"] = url
        elif btype == "PHONE_NUMBER":
            phone = str(b.get("phone_number") or "").strip()
            if not phone:
                return None
            button["phone_number"] = phone
        buttons.append(button)
    return buttons


@csrf_exempt
def templates_list(request):
    """GET: approved WABA templates (?account_id=…) — sandbox (no configured
    account / no waba_id) returns the built-in demo set plus locally created
    ones. POST (admin+): create a template — live goes to Meta's review queue
    (status PENDING); sandbox stores it locally as APPROVED so the flow stays
    demoable offline."""
    denied = _auth_required(request)
    if denied:
        return denied

    if request.method == "GET":
        # ?wa_id=… resolves each template's bound slots against that thread's
        # contact so the picker can pre-fill instead of asking the operator.
        account = _template_account(request)
        bindings_map = wa_config.template_bindings(_bindings_scope(account))
        wa_id = store.normalize_wa_id(request.GET.get("wa_id") or "")
        resolved_vars = (flows_engine.variables_for(store.contact_for_thread(wa_id))
                         if wa_id else None)

        def _decorate(row: dict) -> dict:
            slots = bindings_map.get(row["name"]) or []
            row["bindings"] = slots
            row.setdefault("buttons", [])
            if resolved_vars is not None and any(slots):
                row["resolved"] = [resolved_vars.get(b, "") if b else "" for b in slots]
            return row

        if account:
            from . import client
            try:
                raw = client.list_templates(account)
            except Exception as e:
                return JsonResponse({"detail": f"Template fetch failed: {e}"}, status=502)
            templates = []
            for t in raw:
                template_body = ""
                buttons: list[dict] = []
                for comp in t.get("components") or []:
                    ctype = (comp.get("type") or "").upper()
                    if ctype == "BODY":
                        template_body = comp.get("text", "")
                    elif ctype == "BUTTONS":
                        buttons = comp.get("buttons") or []
                templates.append(_decorate({
                    "id": t.get("id", ""),
                    "name": t.get("name", ""),
                    "language": t.get("language", ""),
                    "category": t.get("category", ""),
                    "status": t.get("status", ""),
                    "body": template_body,
                    "buttons": buttons,
                    "variables": _template_variable_count(template_body),
                    "custom": True,  # live templates are editable/deletable on Meta
                }))
            return JsonResponse({"templates": templates, "sandbox": False})
        templates = [_decorate(dict(t, variables=_template_variable_count(t["body"]),
                                    custom=False))
                     for t in DEMO_TEMPLATES]
        templates += [_decorate(dict(t, variables=_template_variable_count(t.get("body", "")),
                                     custom=True))
                      for t in wa_config.custom_templates()]
        return JsonResponse({"templates": templates, "sandbox": True})

    if request.method == "POST":
        if not _can_manage(request):
            return JsonResponse({"detail": "Requires the admin role."}, status=403)
        body = _json_body(request)
        name = (body.get("name") or "").strip().lower()
        language = (body.get("language") or "en").strip()
        category = (body.get("category") or "UTILITY").strip().upper()
        template_body = (body.get("body") or "").strip()
        example = [str(v) for v in (body.get("example") or [])]
        if not TEMPLATE_NAME_RE.fullmatch(name):
            return JsonResponse({"detail": "name must be lowercase letters, digits and "
                                           "underscores (max 120 chars)."}, status=400)
        if category not in TEMPLATE_CATEGORIES:
            return JsonResponse({"detail": f"category must be one of "
                                           f"{', '.join(TEMPLATE_CATEGORIES)}."}, status=400)
        if not template_body:
            return JsonResponse({"detail": "body is required."}, status=400)
        if len(template_body) > 1024:
            return JsonResponse({"detail": "body exceeds WhatsApp's 1024-character limit."},
                                status=400)
        bindings = _valid_bindings(body.get("bindings"))
        if bindings is None:
            return JsonResponse({"detail": f"Unknown binding key — valid keys: "
                                           f"{', '.join(TEMPLATE_BINDING_KEYS)}."}, status=400)
        buttons = _valid_buttons(body.get("buttons"))
        if buttons is None:
            return JsonResponse({"detail": "buttons must be up to 3 of QUICK_REPLY / URL / "
                                           "PHONE_NUMBER, each with text ≤ 25 chars (URL "
                                           "buttons need a static http(s) url)."}, status=400)

        account = _template_account(request, body)
        scope = _bindings_scope(account)

        def _save_bindings():
            if any(bindings):
                mapping = wa_config.template_bindings(scope)
                mapping[name] = bindings
                wa_config.save_template_bindings(scope, mapping)

        if account:
            from . import client
            try:
                result = client.create_template(account, name, language, category,
                                                template_body, example, buttons)
            except Exception as e:
                return JsonResponse({"detail": f"Template create failed: {e}"}, status=502)
            _save_bindings()
            template = {
                "id": result.get("id", ""),
                "name": name, "language": language, "category": category,
                "status": result.get("status", "PENDING"), "body": template_body,
                "buttons": buttons,
                "variables": _template_variable_count(template_body), "custom": True,
                "bindings": bindings,
            }
            rbac.audit(request, "template.create", target=f"whatsapp:{name}",
                       detail=f"live status={template['status']}")
            return JsonResponse({"template": template, "sandbox": False})

        if _demo_template(name):
            return JsonResponse({"detail": f"A template named '{name}' already exists."},
                                status=400)
        template = {"name": name, "language": language, "category": category,
                    "status": "APPROVED", "body": template_body, "buttons": buttons}
        wa_config.save_custom_templates(wa_config.custom_templates() + [template])
        _save_bindings()
        rbac.audit(request, "template.create", target=f"whatsapp:{name}", detail="sandbox")
        return JsonResponse({"template": dict(template, variables=_template_variable_count(
            template_body), custom=True, bindings=bindings), "sandbox": True})

    return HttpResponseNotAllowed(["GET", "POST"])


def _drop_bindings(scope: str, name: str) -> None:
    mapping = wa_config.template_bindings(scope)
    if name in mapping:
        mapping.pop(name)
        wa_config.save_template_bindings(scope, mapping)


@csrf_exempt
def template_detail(request, name: str):
    """PATCH (admin+): edit a template — content (body/category/buttons/
    example; live needs the Meta template "id" and goes back through review)
    and/or its variable bindings ({"bindings": [...]}, stored locally per
    account). DELETE (admin+): remove a template — live deletes it on Meta
    (every language variant); sandbox removes a locally created one (the
    built-in demo set is fixed)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Requires the admin role."}, status=403)

    body = _json_body(request) if request.method == "PATCH" else {}
    account = _template_account(request, body)
    scope = _bindings_scope(account)

    if request.method == "PATCH":
        edits_content = any(k in body for k in ("body", "category", "buttons"))
        if edits_content:
            template_body = (body.get("body") or "").strip()
            category = (body.get("category") or "UTILITY").strip().upper()
            example = [str(v) for v in (body.get("example") or [])]
            if not template_body:
                return JsonResponse({"detail": "body is required."}, status=400)
            if len(template_body) > 1024:
                return JsonResponse({"detail": "body exceeds WhatsApp's 1024-character "
                                               "limit."}, status=400)
            if category not in TEMPLATE_CATEGORIES:
                return JsonResponse({"detail": f"category must be one of "
                                               f"{', '.join(TEMPLATE_CATEGORIES)}."}, status=400)
            buttons = _valid_buttons(body.get("buttons"))
            if buttons is None:
                return JsonResponse({"detail": "buttons must be up to 3 of QUICK_REPLY / "
                                               "URL / PHONE_NUMBER, each with text ≤ 25 "
                                               "chars (URL buttons need a static http(s) "
                                               "url)."}, status=400)
            if account:
                from . import client
                template_id = str(body.get("id") or "").strip()
                if not template_id:
                    return JsonResponse({"detail": "id (the Meta template id) is required "
                                                   "to edit a live template."}, status=400)
                try:
                    client.update_template(account, template_id, category,
                                           template_body, example, buttons)
                except Exception as e:
                    return JsonResponse({"detail": f"Template edit failed: {e}"}, status=502)
            else:
                if any(t["name"] == name for t in DEMO_TEMPLATES):
                    return JsonResponse({"detail": "Built-in demo templates can't be "
                                                   "edited."}, status=400)
                existing = wa_config.custom_templates()
                match = next((t for t in existing if t.get("name") == name), None)
                if match is None:
                    return JsonResponse({"detail": "Template not found."}, status=404)
                match.update(body=template_body, category=category, buttons=buttons)
                wa_config.save_custom_templates(existing)

        if "bindings" in body:
            bindings = _valid_bindings(body.get("bindings"))
            if bindings is None:
                return JsonResponse({"detail": f"Unknown binding key — valid keys: "
                                               f"{', '.join(TEMPLATE_BINDING_KEYS)}."},
                                    status=400)
            if any(bindings):
                mapping = wa_config.template_bindings(scope)
                mapping[name] = bindings
                wa_config.save_template_bindings(scope, mapping)
            else:
                _drop_bindings(scope, name)

        rbac.audit(request, "template.update", target=f"whatsapp:{name}",
                   detail="content" if edits_content else "bindings")
        return JsonResponse({"ok": True})

    if account:
        from . import client
        try:
            client.delete_template(account, name)
        except Exception as e:
            return JsonResponse({"detail": f"Template delete failed: {e}"}, status=502)
        _drop_bindings(scope, name)
        rbac.audit(request, "template.delete", target=f"whatsapp:{name}", detail="live")
        return JsonResponse({"ok": True})

    if any(t["name"] == name for t in DEMO_TEMPLATES):
        return JsonResponse({"detail": "Built-in demo templates can't be deleted."},
                            status=400)
    existing = wa_config.custom_templates()
    remaining = [t for t in existing if t.get("name") != name]
    if len(remaining) == len(existing):
        return JsonResponse({"detail": "Template not found."}, status=404)
    wa_config.save_custom_templates(remaining)
    _drop_bindings(scope, name)
    rbac.audit(request, "template.delete", target=f"whatsapp:{name}", detail="sandbox")
    return JsonResponse({"ok": True})


@csrf_exempt
def set_labels(request, wa_id: str):
    """POST — replace this WhatsApp thread's label list (deduped/lowercased in
    the store). Mirrors messaging.set_labels so tags work on every channel."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    labels = _json_body(request).get("labels")
    if not isinstance(labels, list):
        return JsonResponse({"detail": "labels must be a list of strings."}, status=400)
    thread = store.update_thread(wa_id, {"labels": labels})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "thread.labels", target=f"whatsapp:{wa_id}", detail=",".join(thread["labels"]))
    return JsonResponse({"labels": thread["labels"]})


@csrf_exempt
def toggle_ai(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    enabled = bool(_json_body(request).get("enabled"))
    thread = store.update_thread(wa_id, {"ai_enabled": enabled})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "thread.ai_toggle", target=f"whatsapp:{wa_id}", detail=f"enabled={enabled}")
    return JsonResponse({"ai_enabled": thread["ai_enabled"]})


@csrf_exempt
def switch_account(request, wa_id: str):
    """POST (admin+): re-point this chat's sending account — {"account_id"}.

    Meta's 24h window is per OUR number, so a switch force-closes the local
    window (template-only until the contact replies to the new number), and
    the contact's sticky assignment is pinned to the chosen account so the
    queue/flows follow the operator's choice too."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to switch accounts."}, status=403)
    account_id = str(_json_body(request).get("account_id") or "")
    if not account_id.isdigit():
        detail = ("The env-default account is not assignable — add it as a "
                  "dashboard account first."
                  if account_id == wa_config.ENV_ACCOUNT_ID else "account_id required.")
        return JsonResponse({"detail": detail}, status=400)
    row = ChannelAccount.objects.filter(pk=int(account_id), channel="whatsapp").first()
    if row is None:
        return JsonResponse({"detail": "Account not found."}, status=404)
    thread = store.load_thread(wa_id, limit=1)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    if str(thread.get("account_id") or "") == account_id:
        return JsonResponse({"account_id": account_id, "account_name": row.name})

    # What does the system already know about (this contact × the new number)?
    # Message.account records every carrier, so we can tell whether the new
    # number ever contacted them and whether they replied to it recently —
    # that decides between "text freely", "open with a template" (Cloud) and
    # "cold contact = ban risk" (Baileys, no templates).
    t_obj = Thread.objects.filter(channel="whatsapp",
                                  key=store.normalize_wa_id(wa_id)).first()
    is_baileys = (row.provider or "cloud") == "baileys"
    prior_contact = Message.objects.filter(thread=t_obj, account=row).exists()
    recent_reply = Message.objects.filter(
        thread=t_obj, account=row, direction="in",
        created_at__gte=timezone.now() - timedelta(hours=store.WINDOW_HOURS),
    ).exists()

    store.update_thread(wa_id, {"account_id": account_id})
    if is_baileys:
        # no 24h window on WhatsApp Web; the risk profile is bans, not templates
        note = (f"[Sender account switched to {row.name} (personal number via "
                "Baileys) — this number has messaged the contact before; no "
                "template needed.]" if prior_contact else
                f"[Sender account switched to {row.name} (personal number via "
                "Baileys) — first contact from this number: cold messages from "
                "an unknown personal number carry ban risk. Safest is to have "
                "the contact message this number first.]")
    elif recent_reply:
        # the contact replied to the NEW number within 24h — window is open
        note = (f"[Sender account switched to {row.name} — the contact replied "
                "to this number recently, so the 24h window is open; free text "
                "is fine.]")
    else:
        # window is per our-number: keep only the new number's recent inbounds
        store.close_window(wa_id, except_account_id=row.pk)
        note = (f"[Sender account switched to {row.name} — the contact knows "
                "this number but hasn't replied to it in 24h; open with an "
                "approved template.]" if prior_contact else
                f"[Sender account switched to {row.name} — entity-new number for "
                "this contact (their side shows a new chat); open with an "
                "approved template.]")
    contact = store.contact_for_thread(wa_id)
    if contact is not None:
        AccountAssignment.objects.update_or_create(
            contact=contact, channel="whatsapp",
            defaults={"account": row, "pinned": True})
    store.append_message(wa_id, "out", note, source="system")
    rbac.audit(request, "thread.account_switch", target=f"whatsapp:{wa_id}", detail=row.name)
    return JsonResponse({"account_id": account_id, "account_name": row.name,
                         "provider": row.provider or "cloud",
                         "prior_contact": prior_contact,
                         "window_open_with_new_account": is_baileys or recent_reply})


@csrf_exempt
def simulate_inbound(request, wa_id: str):
    """Sandbox helper: fake a contact message so the AI loop can be demoed
    without real WhatsApp credentials. Only for threads with no live account."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    thread = store.get_or_create_thread(wa_id)
    if _thread_account(thread):
        return JsonResponse({"detail": "Simulation is sandbox-only (thread has a live account)."}, status=400)
    text = (_json_body(request).get("text") or "").strip()
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    store.append_message(wa_id, "in", text, source="customer")
    if _apply_opt_out(wa_id, text):  # STOP etc. must be demoable in sandbox too
        return JsonResponse({"ok": True, "ai_reply": None, "opted_out": True})
    reply = _maybe_ai_reply(store.load_thread(wa_id) or thread)
    # synchronous so the demo sees flow effects as soon as the call returns
    flows_resumed = flows_engine.on_inbound_message(
        "whatsapp", store.normalize_wa_id(wa_id), text)
    return JsonResponse({"ok": True, "ai_reply": reply, "flows_resumed": flows_resumed})


@csrf_exempt
def call_permission(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "call.permission_request", target=f"whatsapp:{wa_id}")
    try:
        return JsonResponse(wa_calls.request_permission(store.normalize_wa_id(wa_id)))
    except Exception as e:
        return JsonResponse({"detail": f"Permission request failed: {e}"}, status=502)


@csrf_exempt
def call_start(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "call.start", target=f"whatsapp:{wa_id}")
    body = _json_body(request)
    voice_provider = str(body.get("voice_provider") or "").strip()
    voice_id = str(body.get("voice_id") or "").strip()
    # transcript meta: who placed this call
    wa_calls.set_call_party("initiator", store.normalize_wa_id(wa_id),
                            f"operator ({rbac.requester_name(request) or 'unknown'})")
    try:
        # admin-only cold-call override, same shape as the free-text guard's
        # {"force": true}. Opt-out is NOT overridable — see _compliance_error.
        force = bool(body.get("force")) and _can_manage(request)
        result = wa_calls.start_call(store.normalize_wa_id(wa_id), voice_provider,
                                     voice_id, force=force)
    except NotImplementedError as e:
        return JsonResponse({"detail": str(e)}, status=501)
    except Exception as e:
        return JsonResponse({"detail": f"Call failed: {e}"}, status=502)
    if "error" in result:
        return JsonResponse({"detail": result["error"]}, status=409)
    return JsonResponse(result)


@csrf_exempt
def call_end(request, wa_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "call.end", target=f"whatsapp:{wa_id}")
    # transcript meta: the operator pressed End (beats the agent's own record,
    # which would otherwise blame the room teardown on the contact)
    wa_calls.set_call_party("ender", store.normalize_wa_id(wa_id),
                            f"operator ({rbac.requester_name(request) or 'unknown'})")
    try:
        return JsonResponse(wa_calls.end_call(store.normalize_wa_id(wa_id)))
    except Exception as e:
        return JsonResponse({"detail": f"Hangup failed: {e}"}, status=502)


def call_live(request, wa_id: str):
    """Live transcript for an in-progress WhatsApp call — same store the
    /live-calls page polls, so the operator can watch the AI on a WhatsApp
    call too. Cheap file read; safe to poll every second or two."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    return JsonResponse(wa_calls.live_transcript(store.normalize_wa_id(wa_id)))


@csrf_exempt
def call_say(request, wa_id: str):
    """Steer (guide) or speak-for (takeover) the live agent mid-call."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    body = _json_body(request)
    text = (body.get("text") or "").strip()
    mode = body.get("mode") or "guide"
    if mode not in ("guide", "takeover"):
        return JsonResponse({"detail": "mode must be 'guide' or 'takeover'."}, status=400)
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    result = wa_calls.push_say(store.normalize_wa_id(wa_id), mode, text)
    if "error" in result:
        return JsonResponse({"detail": result["error"]}, status=409)
    rbac.audit(request, "call.say", target=f"whatsapp:{wa_id}", detail=f"[{mode}] {text[:120]}")
    return JsonResponse(result)


@csrf_exempt
def call_escalate(request, wa_id: str):
    """Instruct the live agent to promise a human follow-up and wrap up."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    result = wa_calls.push_say(store.normalize_wa_id(wa_id), "guide", wa_calls.ESCALATE_TEXT)
    if "error" in result:
        return JsonResponse({"detail": result["error"]}, status=409)
    rbac.audit(request, "call.escalate", target=f"whatsapp:{wa_id}")
    return JsonResponse(result)


def call_recordings(request, wa_id: str):
    """List this contact's call audio recordings (newest first)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if store.load_thread(wa_id) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    recs = wa_calls.list_recordings(store.normalize_wa_id(wa_id))
    for r in recs:
        r["url"] = f"/api/whatsapp/calls/recording/{r['name']}"
    return JsonResponse({"recordings": recs})


def call_recording_file(request, name: str):
    """Stream a call recording WAV — auth-gated (call audio is PII)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    p = wa_calls.recording_path(name)
    if p is None:
        return JsonResponse({"detail": "Recording not found."}, status=404)
    return FileResponse(open(p, "rb"), content_type="audio/wav", filename=p.name)


# ---- channel health monitor + contact→account assignments -------------------------

def _iso(dt) -> str | None:
    return dt.isoformat() if dt else None


@csrf_exempt
def channels_health(request):
    """GET /api/whatsapp/channels/health — every ChannelAccount row (any
    channel) with its monitor state, the env-default pseudo-account, and the
    email (SMTP) probe result. Any authenticated role may read.
    POST (admin+) — probe every account NOW (the dashboard card's "Check
    now" button; the periodic checker otherwise runs every ~5 minutes) and
    return the refreshed payload."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "POST":
        if not _can_manage(request):
            return JsonResponse({"detail": "Admin role required to run a health check."},
                                status=403)
        health.run_checks()
        rbac.audit(request, "account.health_check", detail="manual run from dashboard")
    elif request.method != "GET":
        return HttpResponseNotAllowed(["GET", "POST"])
    assigned = {row["account_id"]: row["n"]
                for row in AccountAssignment.objects.values("account_id").annotate(n=Count("id"))}
    accounts = []
    for a in ChannelAccount.objects.all().order_by("pk"):
        # routing._configured knows every channel's rule (whatsapp via
        # wa_config, email via config host/login, social via access_token).
        configured = routing._configured(a)
        accounts.append({
            "id": str(a.pk),
            "name": a.name,
            "channel": a.channel,
            "provider": a.provider or "cloud",
            "configured": configured,
            "health": a.health_status,
            "detail": a.health_detail,
            "last_ok_at": _iso(a.last_ok_at),
            "last_inbound_at": _iso(a.last_inbound_at),
            "fail_streak": a.fail_streak,
            "changed_at": _iso(a.health_changed_at),
            "assigned_contacts": assigned.get(a.pk, 0),
        })
    # The env-default pseudo-account only belongs in the health picture when
    # backend/.env actually configures a usable transport — an empty env row
    # is noise on the dashboard ("channel health is for channels").
    env = wa_config._env_account()
    if env and wa_config.account_configured(env):
        accounts.append({
            "id": wa_config.ENV_ACCOUNT_ID,
            "name": env.get("name") or "From backend/.env",
            "channel": "whatsapp",
            "provider": "cloud",
            "configured": True,
            "health": "unknown",  # no DB row -> the monitor never tracks it
            "detail": "",
            "last_ok_at": None,
            "last_inbound_at": None,
            "fail_streak": 0,
            "changed_at": None,
            "assigned_contacts": 0,
        })
    return JsonResponse({
        "accounts": accounts,
        "email": health.email_health(),
        "voice": health.voice_health(),
        "checked_at": health.last_checked_at() or timezone.now().isoformat(),
    })


# ---- voice (Twilio) channel settings ------------------------------------------------

def _voice_settings_payload() -> dict:
    effective = health.voice_settings()
    stored = health._stored_voice_settings()
    return {
        "sip_username": effective["sip_username"],
        "sip_password_masked": _mask(effective["sip_password"]),
        "termination_uri": effective["termination_uri"],
        # where each effective value comes from: "settings" (dashboard row),
        # "env" (backend/.env fallback), or "" (not set anywhere)
        "source": {key: ("settings" if str(stored.get(key) or "").strip()
                         else "env" if effective[key] else "")
                   for key in ("sip_username", "sip_password", "termination_uri")},
        "sip_trunk_configured": health.sip_trunk_configured(),
        "health": health.voice_health(),
    }


@csrf_exempt
def voice_settings(request):
    """Twilio SIP trunk creds for the voice channel. GET = any authenticated
    role (password masked); PUT = SUPER ADMIN, same rule as the other global
    app settings. SIP_OUTBOUND_TRUNK_ID stays .env-only — the voice-agent
    worker is a separate Django-free process that reads it once at boot, so
    it can't be hot-swapped from here."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_voice_settings_payload())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _is_superadmin(request):
        return JsonResponse({"detail": "Super admin role required to change voice settings."},
                            status=403)
    body = _json_body(request)
    patch = {}
    for key in ("sip_username", "sip_password", "termination_uri"):
        value = body.get(key)
        # untouched masked placeholders round-trip from GET — keep the stored secret
        if isinstance(value, str) and "••" not in value:
            patch[key] = value.strip()
    if not patch:
        return JsonResponse({"detail": "Nothing to update."}, status=400)
    health.save_voice_settings(patch)
    rbac.audit(request, "settings.update", target="voice-twilio",
               detail=f"keys: {sorted(patch)}")
    return JsonResponse(_voice_settings_payload())


@csrf_exempt
def voice_settings_test(request):
    """POST: there's no REST endpoint that proves a SIP credential works
    short of placing a real call, so this just reports the computed
    configured/health state instead of live-probing anything."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    v = health.voice_health()
    if not v["configured"] or v["health"] != health.HEALTHY:
        return JsonResponse({"detail": v["detail"] or "SIP username and password are required."},
                            status=400)
    return JsonResponse({"ok": True, "health": v["health"], "detail": "SIP trunk creds are set."})


def _assignment_payload(asg: AccountAssignment) -> dict:
    return {
        "contact_id": asg.contact_id,
        "contact_name": asg.contact.name if asg.contact_id else "",
        "phone": asg.contact.phone if asg.contact_id else "",
        "channel": asg.channel,
        "account_id": str(asg.account_id) if asg.account_id else None,
        "account_name": asg.account.name if asg.account_id else "",
        "pinned": asg.pinned,
        "updated_at": asg.updated_at.isoformat(),
    }


@csrf_exempt
def assignments(request):
    """GET ?channel=<c> (default whatsapp): that channel's account pool +
    its sticky assignments (any role), plus the list of channels that have
    ≥1 ChannelAccount row (the dashboard's channel switcher).
    PUT (admin+): set/pin one contact's account — {"contact_id", "channel",
    "account_id" (null = back to auto), "pinned"}."""
    denied = _auth_required(request)
    if denied:
        return denied

    if request.method == "GET":
        channel = (request.GET.get("channel") or "whatsapp").strip() or "whatsapp"
        counts = {row["account_id"]: row["n"]
                  for row in (AccountAssignment.objects.filter(channel=channel)
                              .values("account_id").annotate(n=Count("id")))}
        accounts = [{
            "id": str(a.pk),
            "name": a.name,
            "provider": a.provider or "cloud",
            "health": a.health_status,
            "contacts": counts.get(a.pk, 0),
        } for a in ChannelAccount.objects.filter(channel=channel).order_by("pk")]
        # .order_by() clears the model's default created_at ordering — with it,
        # DISTINCT covers (channel, created_at) and every account row leaks
        # through as a duplicate channel entry
        channels = sorted(ChannelAccount.objects.order_by()
                          .values_list("channel", flat=True).distinct())
        rows = [_assignment_payload(asg)
                for asg in (AccountAssignment.objects.filter(channel=channel)
                            .select_related("contact", "account")
                            .order_by("contact_id"))]
        return JsonResponse({"channel": channel, "channels": channels,
                             "accounts": accounts, "assignments": rows})

    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage assignments."}, status=403)
    body = _json_body(request)
    channel = (body.get("channel") or "whatsapp").strip() or "whatsapp"
    contact = Contact.objects.filter(pk=body.get("contact_id")).first() \
        if str(body.get("contact_id") or "").isdigit() else None
    if contact is None:
        return JsonResponse({"detail": "Contact not found."}, status=404)

    account_id = body.get("account_id")
    if account_id is None or account_id == "":
        AccountAssignment.objects.filter(contact=contact, channel=channel).delete()
        rbac.audit(request, "assignment.clear", target=f"{channel}:{contact.name}")
        return JsonResponse({"ok": True})
    if str(account_id) == wa_config.ENV_ACCOUNT_ID or not str(account_id).isdigit():
        return JsonResponse({"detail": "The env-default account is not assignable — "
                                       "add it as a dashboard account first."}, status=400)
    account = ChannelAccount.objects.filter(pk=int(account_id), channel=channel).first()
    if account is None:
        return JsonResponse({"detail": "Account not found."}, status=404)
    asg, _created = AccountAssignment.objects.update_or_create(
        contact=contact, channel=channel,
        defaults={"account": account, "pinned": bool(body.get("pinned"))},
    )
    rbac.audit(request, "assignment.set", target=f"{channel}:{contact.name}",
               detail=f"{account.name} pinned={asg.pinned}")
    return JsonResponse(_assignment_payload(asg))


@csrf_exempt
def assignments_rebalance(request):
    """POST (admin+) {"channel": <c>} (default whatsapp): evenly redistribute
    non-pinned assignments across that channel's eligible accounts, and
    auto-assign contacts who have threads on the channel but no assignment
    yet. Pinned rows stay put (they still count toward their account's
    load)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to rebalance."}, status=403)

    channel = (_json_body(request).get("channel") or "whatsapp").strip() or "whatsapp"
    eligible = routing.eligible_accounts(channel)
    if not eligible:
        return JsonResponse({"detail": "No eligible accounts to assign."}, status=409)

    counts = {a.pk: 0 for a in eligible}
    for asg in AccountAssignment.objects.filter(channel=channel, pinned=True):
        if asg.account_id in counts:
            counts[asg.account_id] += 1

    moved = 0
    movable = list(AccountAssignment.objects.filter(channel=channel, pinned=False)
                   .order_by("contact_id"))
    for asg in movable:
        # least-loaded, preferring the current account on ties (fewest moves)
        target = min(eligible, key=lambda a: (counts[a.pk],
                                              0 if a.pk == asg.account_id else 1, a.pk))
        counts[target.pk] += 1
        if target.pk != asg.account_id:
            asg.account = target
            asg.save(update_fields=["account", "updated_at"])
            moved += 1

    assigned_ids = set(AccountAssignment.objects.filter(channel=channel)
                       .values_list("contact_id", flat=True))
    orphans = (Contact.objects.filter(threads__channel=channel)
               .exclude(pk__in=assigned_ids).distinct().order_by("pk"))
    for contact in orphans:
        target = min(eligible, key=lambda a: (counts[a.pk], a.pk))
        counts[target.pk] += 1
        AccountAssignment.objects.create(contact=contact, channel=channel,
                                         account=target, pinned=False)
        moved += 1

    rbac.audit(request, "assignment.rebalance", target=channel, detail=f"moved={moved}")
    return JsonResponse({"moved": moved,
                         "accounts": {str(pk): n for pk, n in counts.items()}})


# ---- conversation-account recovery funnel -----------------------------------------

def recovery(request):
    """GET /api/whatsapp/recovery (any role): conversation-role accounts
    currently banned/disconnected with at least one assigned contact — the
    dashboard's one-click recovery list."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    assigned = {row["account_id"]: row["n"]
                for row in (AccountAssignment.objects.filter(channel="whatsapp")
                            .values("account_id").annotate(n=Count("id")))}
    accounts = []
    for a in (ChannelAccount.objects
              .filter(channel="whatsapp",
                      health_status__in=routing.INELIGIBLE_STATUSES)
              .order_by("pk")):
        if wa_config.account_role(a) != "conversation":
            continue
        affected = assigned.get(a.pk, 0)
        if affected < 1:
            continue
        accounts.append({"account_id": str(a.pk), "name": a.name,
                         "health": a.health_status, "affected": affected})
    return JsonResponse({"accounts": accounts})


@csrf_exempt
def recovery_start(request, account_id: str):
    """POST /api/whatsapp/recovery/<account_id> (admin+): queue the recovery
    template for every contact assigned to this down conversation account —
    the manual counterpart of recovery_mode="auto" (health.enqueue_recovery).
    The jobs ride the outbound queue via a reminder/Cloud account."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to start recovery."},
                            status=403)
    row = (ChannelAccount.objects.filter(channel="whatsapp", pk=int(account_id)).first()
           if str(account_id).isdigit() else None)
    if row is None:
        return JsonResponse({"detail": "Account not found."}, status=404)
    if not wa_config.recovery_template():
        return JsonResponse({
            "detail": "No recovery template configured — set 'recovery_template' "
                      "under Settings → Routing rules first.",
        }, status=409)
    if not routing.eligible_accounts("whatsapp", provider="cloud"):
        return JsonResponse({
            "detail": "No healthy reminder (Cloud API) account to carry the "
                      "recovery template — reconnect one under Settings → Channels.",
        }, status=409)
    queued = health.enqueue_recovery(row)
    rbac.audit(request, "recovery.start", target=f"whatsapp:{row.name}",
               detail=f"queued {queued} recovery template(s)")
    return JsonResponse({"queued": queued})


# ---- routing rules (client operating model) ----------------------------------------

def _rules_payload() -> dict:
    from flows import queue as flows_queue
    rules = wa_config.routing_rules()
    pacing = flows_queue.queue_settings()
    return {
        "ai_reply_mode": rules["ai_reply_mode"],
        "business_redirect": wa_config.business_redirect_enabled(),
        "business_redirect_target": rules["business_redirect_target"],
        "support_contact": rules["support_contact"],
        "business_redirect_template": rules["business_redirect_template"],
        "recovery_mode": rules["recovery_mode"],
        "recovery_template": rules["recovery_template"],
        "cold_text_guard": rules["cold_text_guard"],
        "cold_call_guard": rules["cold_call_guard"],
        "reminder_ai_default": rules["reminder_ai_default"],
        # outbound-queue pacing (Baileys sends) — effective values from the
        # Setting > QUEUE_* env > default merge, editable from AI Configuration
        "queue_min_gap_s": pacing["queue_min_gap_s"],
        "queue_max_gap_s": pacing["queue_max_gap_s"],
    }


@csrf_exempt
def routing_rules(request):
    """GET/PUT /api/whatsapp/routing-rules — the operating-model knobs
    (queued AI replies, business redirect, recovery funnel, cold-text guard),
    backed by crm.Setting rows. GET: any role. PUT: admin+ (matches the
    assignments gating)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_rules_payload())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to change routing rules."},
                            status=403)
    body = _json_body(request)
    patch: dict[str, str] = {}
    for key, choices in wa_config.RULE_CHOICES.items():
        if key in body:
            value = str(body.get(key) or "").strip().lower()
            if value not in choices:
                return JsonResponse({"detail": f"{key} must be one of: "
                                               f"{', '.join(choices)}."}, status=400)
            patch[key] = value
    if "business_redirect" in body:
        value = body["business_redirect"]
        if isinstance(value, bool):
            patch["business_redirect"] = "true" if value else "false"
        elif str(value).strip().lower() in ("true", "false"):
            patch["business_redirect"] = str(value).strip().lower()
        else:
            return JsonResponse({"detail": "business_redirect must be true or false."},
                                status=400)
    if "recovery_template" in body:
        if not isinstance(body["recovery_template"], str):
            return JsonResponse({"detail": "recovery_template must be a template "
                                           "name string (empty = none)."}, status=400)
        patch["recovery_template"] = body["recovery_template"].strip()
    if "business_redirect_template" in body:
        if not isinstance(body["business_redirect_template"], str):
            return JsonResponse({"detail": "business_redirect_template must be a "
                                           "template name string (empty = none)."},
                                status=400)
        patch["business_redirect_template"] = body["business_redirect_template"].strip()
    if "support_contact" in body:
        if not isinstance(body["support_contact"], str):
            return JsonResponse({"detail": "support_contact must be a string "
                                           "(empty = generic wording)."}, status=400)
        patch["support_contact"] = body["support_contact"].strip()[:200]
    queue_patch: dict[str, int] = {}
    for key in ("queue_min_gap_s", "queue_max_gap_s"):
        if key in body:
            try:
                value = int(str(body[key]).strip())
            except (TypeError, ValueError):
                value = -1
            if value < 0:
                return JsonResponse({"detail": f"{key} must be a whole number of "
                                               "seconds (0 or more)."}, status=400)
            queue_patch[key] = value
    from flows import queue as flows_queue
    if queue_patch:
        # cross-field check against the EFFECTIVE other bound, so patching one
        # key can't silently invert the min<=max pacing window
        effective = {**flows_queue.queue_settings(), **queue_patch}
        if effective["queue_min_gap_s"] > effective["queue_max_gap_s"]:
            return JsonResponse({"detail": "queue_min_gap_s cannot exceed "
                                           "queue_max_gap_s."}, status=400)
        flows_queue.save_queue_settings(queue_patch)
    wa_config.save_routing_rules(patch)
    rbac.audit(request, "settings.update", target="routing-rules",
               detail=f"keys: {sorted([*patch, *queue_patch])}")
    return JsonResponse(_rules_payload())
