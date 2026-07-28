"""WhatsApp conversation store — DATABASE-BACKED (crm.Thread / crm.Message).

The function signatures and returned dict shapes are identical to the old
file-based store, so views, the AI reply loop, the timeline, and the call
bridge did not change. Thread dict shape:

{
  "wa_id": "60123456789", "phone": "+60123456789", "name": "Ahmad Faiz",
  "account_id": "3" | None,          # crm.ChannelAccount pk as str
  "ai_enabled": true,
  "opted_out": false,                # contact sent STOP/BERHENTI/etc.
  "window_open": true,               # Meta 24h contact-service window
  "window_expires_at": iso | null,
  "call": {"permission", "call_id", "status", "room"},
  "messages": [{id, pk, dir, text, ts, status, source,
                account_id?, account_name?,   # which number carried it
                media?: {type, url}}],
  "has_more": false,                 # older messages exist beyond this page
  "created", "updated"
}

Pagination: load_thread(wa_id, limit=N, before=<cursor>) returns the newest N
messages (chronological order); `before` accepts a message pk, a payload id
("db-<pk>" or a wamid), or an ISO timestamp. limit=None (the default for
internal callers — bridge, timeline, AI loop) returns everything.

NOTE for non-Django processes (whatsapp/bridge.py): call django.setup() with
DJANGO_SETTINGS_MODULE=core.settings BEFORE importing this module.
"""
from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone as dt_timezone
from pathlib import Path

from django.db import IntegrityError, transaction
from django.db.models import Count, Max, OuterRef, Q, Subquery
from django.db.models.functions import Coalesce
from django.utils import timezone

from crm import scoping
from crm.models import ChannelAccount, Message, Thread

# Inbound/outbound attachments live here (gitignored PII, same as the rest of
# data/). media_path on crm.Message is relative to this directory.
MEDIA_DIR = Path(__file__).resolve().parent.parent / "data" / "whatsapp" / "media"

# Meta's contact-service window: free-form messages may only be sent within
# 24h of the contact's newest inbound message (outside it -> template only).
WINDOW_HOURS = 24


def normalize_wa_id(phone_or_wa_id: str) -> str:
    """'+60 12-345 6789' -> '60123456789' (Cloud API wa_id format)."""
    return re.sub(r"\D", "", str(phone_or_wa_id or ""))


def _serialize_message(m: Message, acct_by_id: dict | None = None) -> dict:
    out = {
        "id": m.external_id or f"db-{m.pk}",
        "pk": m.pk,  # stable pagination cursor + media URL key
        "dir": m.direction,
        "text": m.text,
        "ts": m.created_at.isoformat(),
        "status": m.status,
        "source": m.source,
    }
    if m.account_id:
        # which provider account carried this message — lets the chat UI show
        # "via <number>" and flag mid-thread account switches (failover)
        out["account_id"] = str(m.account_id)
        acct = (acct_by_id or {}).get(str(m.account_id))
        if acct:
            out["account_name"] = acct.get("name") or ""
    if m.media_path:
        out["media"] = {"type": m.media_type or "document",
                        "url": f"/api/whatsapp/media/{m.pk}"}
    return out


def _live_account_lookup() -> tuple[dict, dict | None]:
    """(accounts by id, fallback account) — mirrors wa_config.get_account():
    an unknown/missing account id falls back to the sole CONFIGURED account."""
    from . import wa_config  # lazy: avoids import-order surprises in bridge.py

    accounts = wa_config.list_accounts()
    configured = [a for a in accounts if wa_config.account_configured(a)]
    fallback = configured[0] if len(configured) == 1 else None
    return {a["id"]: a for a in accounts}, fallback


def _account_for(account_id, by_id: dict, fallback: dict | None) -> dict | None:
    return (by_id.get(str(account_id)) if account_id else None) or fallback


def _thread_is_live(account_id, by_id: dict, fallback: dict | None) -> bool:
    from . import wa_config

    return wa_config.account_configured(_account_for(account_id, by_id, fallback))


def _is_baileys_thread(account_id, by_id: dict, fallback: dict | None) -> bool:
    """Baileys = a normal WhatsApp account over WhatsApp Web — Meta's 24h
    contact-service window does not apply to it."""
    account = _account_for(account_id, by_id, fallback)
    return bool(account and account.get("provider") == "baileys")


def _window_state(last_in_ts, live: bool) -> tuple[bool, str | None]:
    """(window_open, window_expires_at) from the newest INBOUND message time.
    Sandbox threads are always open — nothing real is sent, and the demo flow
    must never dead-end on a closed window."""
    if not live:
        return True, None
    if not last_in_ts:
        return False, None  # contact never wrote in -> template territory
    expires = last_in_ts + timedelta(hours=WINDOW_HOURS)
    return timezone.now() < expires, expires.isoformat()


def _messages_before(t: Thread, qs, before):
    """Filter to messages strictly older than the cursor. Cursor forms:
    message pk, payload id ("db-<pk>" or a provider wamid), or ISO timestamp.
    An unrecognized cursor is ignored (full set) rather than erroring."""
    b = str(before).strip()
    if b.startswith("db-"):
        b = b[3:]
    anchor = None
    if b.isdigit():
        anchor = Message.objects.filter(pk=int(b), thread=t).first()
    if anchor is None:
        anchor = Message.objects.filter(thread=t, external_id=b).first() if b else None
    if anchor is not None:
        return qs.filter(Q(created_at__lt=anchor.created_at)
                         | Q(created_at=anchor.created_at, pk__lt=anchor.pk))
    try:
        dt = datetime.fromisoformat(b.replace("Z", "+00:00"))
    except ValueError:
        return qs
    if timezone.is_naive(dt):
        dt = timezone.make_aware(dt)
    return qs.filter(created_at__lt=dt)


def _serialize(t: Thread, limit: int | None = None, before=None) -> dict:
    by_id, fallback = _live_account_lookup()
    qs = Message.objects.filter(thread=t)
    if before:
        qs = _messages_before(t, qs, before)
    if limit:
        newest = list(qs.order_by("-created_at", "-pk")[: limit + 1])
        has_more = len(newest) > limit
        page = [_serialize_message(m, by_id) for m in reversed(newest[:limit])]
    else:
        has_more = False
        page = [_serialize_message(m, by_id) for m in qs.order_by("created_at", "pk")]
    live = _thread_is_live(t.account_id, by_id, fallback)
    last_in_ts = (Message.objects.filter(thread=t, direction="in")
                  .aggregate(ts=Max("created_at"))["ts"])
    is_baileys = _is_baileys_thread(t.account_id, by_id, fallback)
    if is_baileys:
        window_open, window_expires_at = True, None
    else:
        window_open, window_expires_at = _window_state(last_in_ts, live)

    return {
        "wa_id": t.key,
        "phone": t.address or f"+{t.key}",
        "name": t.name or f"+{t.key}",
        "account_id": str(t.account_id) if t.account_id else None,
        "provider": "baileys" if is_baileys else "cloud",
        "ai_enabled": _effective_ai(t, by_id),
        "assignee": t.assignee,
        "opted_out": t.opted_out,
        "window_open": window_open,
        "window_expires_at": window_expires_at,
        "contact_id": t.contact_id,
        "contact_name": t.contact.name if t.contact_id else "",
        "labels": list(t.labels or []),
        "call": {
            "permission": t.call_permission,
            "call_id": t.call_id,
            "status": t.call_status,
            "room": t.call_room,
        },
        "messages": page,
        "has_more": has_more,
        "created": t.created_at.isoformat(),
        "updated": t.updated_at.isoformat(),
    }


def _effective_ai(t: Thread, by_id: dict | None = None) -> bool:
    """The Chatbot tick for the thread's CURRENT sending account. Each account
    keeps its own per-chat override (Thread.ai_overrides[account_pk]); with no
    override the account's ROLE decides — reminder (business) off unless the
    reminder_ai_default routing rule flips it, conversation on — so switching
    the sender shows/uses that number's own setting. Account-less threads
    (sandbox, env pseudo-account) use the legacy column."""
    if not t.account_id:
        return t.ai_enabled
    override = (t.ai_overrides or {}).get(str(t.account_id))
    if isinstance(override, bool):
        return override
    from . import wa_config  # lazy: avoids import-order surprises in bridge.py

    account = (by_id or {}).get(str(t.account_id)) or t.account
    if account is None:
        return t.ai_enabled
    if wa_config.account_role(account) == wa_config.ROLE_REMINDER:
        return wa_config.reminder_ai_default_on()
    return True


def _get(wa_id: str) -> Thread | None:
    return Thread.objects.filter(channel="whatsapp", key=normalize_wa_id(wa_id)).first()


def _resolve_account(account_id: str | None) -> ChannelAccount | None:
    if not account_id or not str(account_id).isdigit():
        return None  # includes the env-default pseudo-account
    return ChannelAccount.objects.filter(pk=int(account_id)).first()


def load_thread(wa_id: str, limit: int | None = None, before=None) -> dict | None:
    t = _get(wa_id)
    return _serialize(t, limit=limit, before=before) if t else None


def _match_contact(wa_id: str):
    """The contact whose phone digits end with this wa_id — the unified-inbox
    identity link. Stored phones carry formatting ("+60 18-445 6677"), so the
    comparison is digits-to-digits, not a DB endswith."""
    from crm.models import Contact

    if not wa_id:
        return None
    return next((c for c in Contact.objects.exclude(phone="")
                 if normalize_wa_id(c.phone).endswith(wa_id[-9:])), None)


def contact_for_thread(wa_id: str):
    """The thread's linked crm.Contact (falling back to a phone match for
    threads created before the link existed), or None."""
    t = _get(normalize_wa_id(wa_id))
    if t is not None and t.contact_id is not None:
        return t.contact
    return _match_contact(normalize_wa_id(wa_id))


def call_metadata(contact, channel: str = "whatsapp") -> dict:
    """CRM -> voice-agent dispatch metadata for a contact.

    ONE definition shared by both WhatsApp call transports (bridge.py and
    baileys_calls.py) and mirrored by calls/contacts.dial_meta for SIP, so
    every transport hands the agent the same keys (voice_agent/prompts.py).
    Everything vertical-specific rides on the contact's primary open Record.
    """
    if contact is None:
        return {}
    meta: dict = {"contact_name": contact.name}
    if contact.company_name:
        meta["company_name"] = contact.company_name
    if contact.language:
        meta["language"] = contact.language
    if contact.tags:
        meta["tags"] = list(contact.tags)
    record = (contact.records.exclude(
        status__in=("closed", "done", "resolved", "cancelled", "completed")).first()
        or contact.records.first())
    if record is not None:
        meta["record_type"] = record.type
        meta["record_id"] = record.external_id
        meta["record_title"] = record.title
        meta["record_status"] = record.status
        meta["record_currency"] = record.currency
        if record.amount is not None:
            meta["record_amount"] = f"{record.currency}{record.amount:,.2f}".replace(".00", "")
        if record.due_date:
            meta["record_due_date"] = record.due_date.isoformat()
        if record.data:
            meta["record_data"] = record.data
    # Cross-channel memory: the same build_context block the chat brain
    # injects, passed as a plain string — the agent process is Django-free
    # and just appends it to its instructions (voice_agent/prompts.py).
    try:
        from aicore import memory as ai_memory
        mem = ai_memory.build_context(contact, channel=channel)
        if mem:
            meta["memory"] = mem
    except Exception:
        pass  # memory is a nice-to-have — never block a dial on it
    # Cross-channel COMPANY KNOWLEDGE: the same operator-authored block the
    # chat brain injects (aicore/knowledge.py), so a live call quotes the same
    # hours/policies/notices as a WhatsApp reply. Bounded + exception-safe.
    try:
        from aicore import knowledge as ai_knowledge
        block = ai_knowledge.build_knowledge_context(contact, channel=channel)
        if block:
            meta["knowledge"] = block
    except Exception:
        pass  # knowledge is a nice-to-have — never block a dial on it
    return meta


def get_or_create_thread(phone_or_wa_id: str, name: str = "", account_id: str | None = None) -> dict:
    wa_id = normalize_wa_id(phone_or_wa_id)
    phone = phone_or_wa_id if str(phone_or_wa_id).startswith("+") else f"+{wa_id}"
    account_row = _resolve_account(account_id)
    # Role-aware AI default: the reminder (business) number does no AI
    # conversation — new threads on a reminder-role account start with the
    # bot off (the redirect responder points contacts to their conversation
    # number instead). Conversation/sandbox threads keep AI on. Only a
    # CREATION default: existing threads are never touched.
    ai_default = True
    if account_row is not None:
        from . import wa_config  # lazy: avoids import-order surprises in bridge.py

        ai_default = wa_config.account_role(account_row) != wa_config.ROLE_REMINDER
    matched_contact = _match_contact(wa_id)
    t, created = Thread.objects.get_or_create(
        channel="whatsapp", key=wa_id,
        defaults={"address": phone, "name": name or phone, "ai_enabled": ai_default,
                  "account": account_row,
                  "contact": matched_contact,
                  # Attribute the conversation to an outlet at birth: the
                  # receiving number decides, else the contact's entity.
                  "entity": scoping.thread_entity(account=account_row,
                                                  contact=matched_contact)},
    )
    changed = False
    if name and (not t.name or t.name.startswith("+")):
        t.name = name  # upgrade placeholder with webhook profile name
        changed = True
    if account_id and str(t.account_id or "") != str(account_id):
        account = _resolve_account(account_id)
        if account:
            # The AI tick is PER ACCOUNT (_effective_ai): moving the thread to
            # another number automatically uses that number's own override or
            # role default, so attribution/switching never mutates AI state.
            t.account = account  # webhook/send tells us the true owning number
            changed = True
    if not created and t.contact_id is None:
        contact = _match_contact(wa_id)  # backfill pre-link threads on touch
        if contact:
            t.contact = contact
            changed = True
    if changed:
        # Account or contact moved — re-derive the owning entity from the same
        # rule, so the denormalized column can never drift (an inbound
        # re-attribution to another outlet's number moves the chat with it).
        scoping.sync_thread_entity(t, save=False)
        t.save()
    return _serialize(_get(wa_id) or t)


def list_threads(limit: int | None = None, entity=None) -> list[dict]:
    """Thread summaries, most recently updated first. Aggregates (last
    message, counts, newest-inbound time) come from the DB — the per-thread
    message lists are NOT materialized here.

    `entity` narrows the inbox to one outlet. Unassigned chats (a shared
    number, contact not yet linked) are INCLUDED in an entity view on purpose:
    an operator working that outlet still has to answer them, and hiding a
    conversation nobody can see from any filter is how messages go unanswered."""
    last_msg = (Message.objects.filter(thread=OuterRef("pk"))
                .order_by("-created_at", "-pk"))
    base = scoping.scope_owned(Thread.objects.filter(channel="whatsapp"), entity,
                               include_unassigned=True)
    qs = (
        base
        .select_related("contact", "account")
        .annotate(
            msg_count=Count("messages"),
            last_msg_ts=Max("messages__created_at"),
            last_in_ts=Max("messages__created_at", filter=Q(messages__direction="in")),
            last_msg_text=Subquery(last_msg.values("text")[:1]),
            # direction + delivery status of the newest message — the chat
            # list shows WhatsApp-style ticks on each row's last outbound
            last_msg_dir=Subquery(last_msg.values("direction")[:1]),
            last_msg_status=Subquery(last_msg.values("status")[:1]),
            sort_ts=Coalesce(Max("messages__created_at"), "updated_at"),
        )
        .order_by("-sort_ts")
    )
    if limit:
        qs = qs[:limit]

    by_id, fallback = _live_account_lookup()
    out = []
    for t in qs:
        live = _thread_is_live(t.account_id, by_id, fallback)
        if _is_baileys_thread(t.account_id, by_id, fallback):
            window_open, window_expires_at = True, None
        else:
            window_open, window_expires_at = _window_state(t.last_in_ts, live)
        out.append({
            "wa_id": t.key,
            "phone": t.address or f"+{t.key}",
            "name": t.name or f"+{t.key}",
            "account_id": str(t.account_id) if t.account_id else None,
            "ai_enabled": _effective_ai(t, by_id),
            "assignee": t.assignee,
            "opted_out": t.opted_out,
            "window_open": window_open,
            "window_expires_at": window_expires_at,
            "contact_id": t.contact_id,
            "contact_name": t.contact.name if t.contact_id else "",
            "labels": list(t.labels or []),
            "call": {"permission": t.call_permission, "call_id": t.call_id,
                     "status": t.call_status, "room": t.call_room},
            "last_message": t.last_msg_text or "",
            "last_dir": t.last_msg_dir or "",
            "last_status": t.last_msg_status or "",
            "last_ts": (t.last_msg_ts or t.updated_at).isoformat(),
            "message_count": t.msg_count,
        })
    return out


def thread_has_inbound(wa_id: str) -> bool:
    """Has the contact EVER written into this thread? A thread with zero
    inbound is COLD — the cold-text guard refuses/flags free-text into it on
    conversation-role (personal) numbers, where unsolicited first contact is
    the ban vector."""
    return Message.objects.filter(
        thread__channel="whatsapp", thread__key=normalize_wa_id(wa_id),
        direction="in",
    ).exists()


def message_exists(wa_id: str, msg_id: str) -> bool:
    """Is this provider message id (wamid) already stored on the thread?
    Webhook idempotency: Meta redelivers events when a response is slow."""
    if not msg_id:
        return False
    return Message.objects.filter(
        thread__channel="whatsapp", thread__key=normalize_wa_id(wa_id),
        external_id=msg_id,
    ).exists()


def append_message(wa_id: str, direction: str, text: str, source: str,
                   msg_id: str = "", status: str = "",
                   media_type: str = "", media_path: str = "",
                   provider_ts=None) -> dict:
    """provider_ts: the provider's own message timestamp (epoch seconds, as
    Meta sends it). Webhooks can arrive HOURS late (Meta retry backlog), and
    the 24h contact-service window is computed from the newest inbound — so
    inbound rows must carry when the contact actually sent the message, not
    when the webhook finally reached us. Otherwise the UI offers free-text
    sends that Meta then rejects with 131047 (window closed)."""
    t = _get(wa_id)
    if t is None:
        get_or_create_thread(wa_id)
        t = _get(wa_id)
    try:
        with transaction.atomic():  # keeps the outer transaction usable on conflict
            m = Message.objects.create(
                thread=t, external_id=msg_id, direction=direction, source=source,
                # denormalized: which account carried it (survives failover)
                account_id=t.account_id,
                text=text, status=status or ("received" if direction == "in" else "sent"),
                media_type=media_type, media_path=media_path,
            )
    except IntegrityError:
        # duplicate wamid (webhook redelivery raced past message_exists) —
        # hand back the row we already have instead of a second copy
        existing = Message.objects.filter(thread=t, external_id=msg_id).first()
        if existing is not None:
            return _serialize_message(existing)
        raise
    if provider_ts:
        try:  # only trust a sane provider time: past, never future
            dt = datetime.fromtimestamp(float(provider_ts), tz=dt_timezone.utc)
            if dt < m.created_at:
                m.created_at = dt
                m.save(update_fields=["created_at"])
        except (TypeError, ValueError, OverflowError, OSError):
            pass
    t.save(update_fields=["updated_at"])  # bump inbox ordering
    return _serialize_message(m)


def close_window(wa_id: str, except_account_id=None) -> None:
    """Force the 24h contact-service window closed for this thread.

    Called when Meta rejects a send with 131047 ("no contact reply in 24h")
    — authoritative proof that the newest stored inbound's created_at is stale
    (webhooks can arrive hours late, carrying the arrival time before
    provider_ts stamping existed). The window derives from the newest inbound
    time, so cap any too-new inbound rows at now-24h: a bounded correction
    toward the truth Meta just asserted, which flips the composer to
    template mode instead of letting sends fail repeatedly.

    except_account_id: keep inbound rows carried by THIS account untouched —
    used on sender-account switches, where the window with the NEW number is
    exactly the contact's recent replies to that number (Meta's window is
    per our-number, not per-thread)."""
    t = _get(wa_id)
    if t is None:
        return
    cutoff = timezone.now() - timedelta(hours=WINDOW_HOURS)
    stale = Message.objects.filter(thread=t, direction="in",
                                   created_at__gt=cutoff)
    if except_account_id is not None:
        stale = stale.exclude(account_id=except_account_id)
    if stale.update(created_at=cutoff):
        t.save(update_fields=["updated_at"])  # SSE change-token bump


def update_message_status(wa_id: str, msg_id: str, status: str) -> None:
    t = _get(wa_id)
    if t and Message.objects.filter(thread=t, external_id=msg_id).update(status=status):
        t.save(update_fields=["updated_at"])  # queryset update skips auto_now; the SSE stream watches updated_at


def update_message_media(wa_id: str, msg_id: str, media_type: str, media_path: str) -> None:
    """Attach downloaded media to an already-stored message — the webhook acks
    fast and the async worker fills these in once the file lands on disk."""
    if not msg_id:
        return
    updated = Message.objects.filter(
        thread__channel="whatsapp", thread__key=normalize_wa_id(wa_id),
        external_id=msg_id,
    ).update(media_type=media_type, media_path=media_path)
    if updated:
        # queryset update skips auto_now; the SSE stream watches updated_at
        Thread.objects.filter(
            channel="whatsapp", key=normalize_wa_id(wa_id),
        ).update(updated_at=timezone.now())


def get_message_media(message_pk: int) -> dict | None:
    """Media descriptor for /api/whatsapp/media/<pk>: absolute file path,
    media type and filename — or None when the message has no attachment."""
    m = (Message.objects.filter(pk=message_pk, thread__channel="whatsapp")
         .exclude(media_path="").first())
    if m is None:
        return None
    path = (MEDIA_DIR / m.media_path).resolve()
    if MEDIA_DIR.resolve() not in path.parents:  # stored path must stay inside MEDIA_DIR
        return None
    return {"type": m.media_type, "path": path, "filename": path.name}


def update_thread(wa_id: str, patch: dict) -> dict | None:
    """Shallow-merge a patch into the thread ('call' merges one level deep)."""
    t = _get(wa_id)
    if t is None:
        return None
    for k, v in patch.items():
        if k == "call" and isinstance(v, dict):
            if "permission" in v:
                t.call_permission = v["permission"]
            if "call_id" in v:
                t.call_id = v["call_id"]
            if "status" in v:
                t.call_status = v["status"]
            if "room" in v:
                t.call_room = v["room"]
        elif k == "ai_enabled":
            if t.account_id:
                # the operator's tick applies to the CURRENT sending account
                # only — other numbers carrying this chat keep their own
                overrides = dict(t.ai_overrides or {})
                overrides[str(t.account_id)] = bool(v)
                t.ai_overrides = overrides
            t.ai_enabled = bool(v)  # account-less/sandbox fallback stays in sync
        elif k == "opted_out":
            t.opted_out = bool(v)
        elif k == "labels" and isinstance(v, list):
            seen, clean = set(), []
            for x in v:
                s = str(x).strip().lower()
                if s and s not in seen:
                    seen.add(s)
                    clean.append(s)
            t.labels = clean
        elif k == "name" and v:
            t.name = v
        elif k == "account_id":
            t.account = _resolve_account(v)
    # A manual account switch (or a contact re-link) can move the chat to
    # another outlet's number — re-derive the owning entity from the same rule
    # so the inbox filter follows the switch.
    scoping.sync_thread_entity(t, save=False)
    t.save()
    return _serialize(_get(wa_id) or t)
