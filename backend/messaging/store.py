"""Email/social thread store — DATABASE-BACKED (crm.Thread / crm.Message),
same function signatures and dict shapes as the old file-based store so the
views and the unified inbox did not change.
"""
from __future__ import annotations

import re
from pathlib import Path

from django.utils import timezone

from crm.models import Message, Thread

# Inbound attachments for the NON-whatsapp channels (telegram/messenger/IG/
# email) live here — same layout as whatsapp's data/whatsapp/media, served
# auth-gated at /api/messaging/media/<pk>. media_path is relative to this dir.
MEDIA_DIR = Path(__file__).resolve().parent.parent / "data" / "messaging" / "media"


def safe_key(key: str) -> str:
    return re.sub(r"[^a-zA-Z0-9._+-]", "_", str(key or "").strip().lower())


def _norm(key: str) -> str:
    return str(key or "").strip()


def _serialize_message(m: Message) -> dict:
    out = {
        "id": m.external_id or f"db-{m.pk}",
        "dir": m.direction,
        "text": m.text,
        "ts": m.created_at.isoformat(),
        "status": m.status,
        "source": m.source,
        "private": m.private,
    }
    if m.account_id:
        # which provider account carried it — lets the chat UI show "via <name>"
        # (same shape as the whatsapp store's message dicts)
        out["account_id"] = str(m.account_id)
        out["account_name"] = m.account.name if m.account else ""
    if m.media_path:
        out["media"] = {"type": m.media_type or "document",
                        "url": f"/api/messaging/media/{m.pk}"}
    return out


def _serialize(t: Thread) -> dict:
    return {
        "key": t.key,
        "channel": t.channel,
        "address": t.address or t.key,
        "name": t.name or t.key,
        "ai_enabled": t.ai_enabled,
        "status": t.status,
        "assignee": t.assignee,
        "labels": list(t.labels or []),
        "contact_id": t.contact_id,
        "contact_name": t.contact.name if t.contact_id else "",
        "messages": [_serialize_message(m) for m in t.messages.all()],
        "created": t.created_at.isoformat(),
        "updated": t.updated_at.isoformat(),
    }


def _get(channel: str, key: str) -> Thread | None:
    return (
        Thread.objects.filter(channel=channel, key=_norm(key))
        .prefetch_related("messages__account")
        .first()
    )


def load_thread(channel: str, key: str) -> dict | None:
    t = _get(channel, key)
    return _serialize(t) if t else None


def _match_contact(channel: str, key: str):
    """Identity link for the unified inbox: email keys match Contact.email.
    Social ids (PSID/IGSID/TG chat id) have no CRM counterpart — those threads
    are linked manually from the chat's Contact panel."""
    from crm.models import Contact

    if channel == "email" and "@" in key:
        return Contact.objects.filter(email__iexact=key).first()
    return None


def get_or_create_thread(channel: str, key: str, name: str = "") -> dict:
    key = _norm(key)
    t, created = Thread.objects.get_or_create(
        channel=channel, key=key,
        defaults={"address": key, "name": name or key,
                  "ai_enabled": channel != "email",  # email has no auto-reply
                  "contact": _match_contact(channel, key)},
    )
    fields = []
    if name and t.name == t.address:
        t.name = name  # upgrade placeholder with webhook profile name
        fields.append("name")
    if not created and t.contact_id is None:
        contact = _match_contact(channel, key)  # backfill pre-link threads
        if contact:
            t.contact = contact
            fields.append("contact")
    if fields:
        t.save(update_fields=fields + ["updated_at"])
    return _serialize(_get(channel, key) or t)


def list_threads() -> list[dict]:
    out = []
    for t in Thread.objects.exclude(channel="whatsapp").select_related("contact").prefetch_related("messages"):
        msgs = list(t.messages.all())
        last = msgs[-1] if msgs else None
        out.append({
            "key": t.key,
            "channel": t.channel,
            "address": t.address or t.key,
            "name": t.name or t.key,
            "ai_enabled": t.ai_enabled,
            "status": t.status,
            "assignee": t.assignee,
            "labels": list(t.labels or []),
            "contact_id": t.contact_id,
            "contact_name": t.contact.name if t.contact_id else "",
            "last_message": (f"[note] {last.text}" if last.private else last.text) if last else "",
            "last_ts": last.created_at.isoformat() if last else t.updated_at.isoformat(),
            "message_count": len(msgs),
        })
    out.sort(key=lambda x: x.get("last_ts") or "", reverse=True)
    return out


def append_message(channel: str, key: str, direction: str, text: str, source: str,
                   msg_id: str = "", status: str = "", private: bool = False,
                   media_type: str = "", media_path: str = "") -> dict:
    t = _get(channel, key)
    if t is None:
        get_or_create_thread(channel, key)
        t = _get(channel, key)
    if msg_id:  # webhook redelivery — return the row the unique constraint protects
        existing = Message.objects.filter(thread=t, external_id=msg_id).first()
        if existing:
            return _serialize_message(existing)
    m = Message.objects.create(
        thread=t, external_id=msg_id, direction=direction, source=source,
        account_id=t.account_id,  # denormalized: which account carried it
        text=text, private=private,
        media_type=media_type, media_path=media_path,
        status=status or ("received" if direction == "in" else "sent"),
    )
    fields = ["updated_at"]
    # Inbox behaviour: a contact message re-opens a resolved/snoozed thread.
    if direction == "in" and t.status in ("resolved", "snoozed"):
        t.status = "open"
        fields.append("status")
    t.save(update_fields=fields)
    return _serialize_message(m)


def update_message_media(channel: str, key: str, msg_id: str,
                         media_type: str, media_path: str) -> None:
    """Attach downloaded media to an already-stored message — webhooks ack
    fast and the async worker fills these in once the file lands on disk
    (same two-phase pattern as the whatsapp store)."""
    if not msg_id:
        return
    updated = Message.objects.filter(
        thread__channel=channel, thread__key=_norm(key), external_id=msg_id,
    ).update(media_type=media_type, media_path=media_path)
    if updated:
        # queryset update skips auto_now; the SSE stream watches updated_at
        Thread.objects.filter(channel=channel, key=_norm(key)) \
            .update(updated_at=timezone.now())


def get_message_media(message_pk: int) -> dict | None:
    """Media descriptor for /api/messaging/media/<pk> — any channel EXCEPT
    whatsapp (that one serves its own files from its own media dir)."""
    m = (Message.objects.filter(pk=message_pk)
         .exclude(thread__channel="whatsapp")
         .exclude(media_path="").select_related("thread").first())
    if m is None:
        return None
    path = (MEDIA_DIR / m.media_path).resolve()
    if MEDIA_DIR.resolve() not in path.parents:  # stored path must stay inside MEDIA_DIR
        return None
    return {"type": m.media_type, "path": path, "filename": path.name}


def save_media_bytes(channel: str, key: str, filename: str, content: bytes) -> str:
    """Write an attachment under MEDIA_DIR/<channel>/<safe key>/ and return
    the media_path (relative) to store on the message row. Filenames are
    sanitized and de-collided."""
    safe_name = re.sub(r"[^A-Za-z0-9._-]", "_", filename or "file.bin")[-120:] or "file.bin"
    dest_dir = MEDIA_DIR / channel / safe_key(key)
    dest_dir.mkdir(parents=True, exist_ok=True)
    dest = dest_dir / safe_name
    stem, dot, ext = safe_name.rpartition(".")
    n = 1
    while dest.exists():
        dest = dest_dir / (f"{stem}-{n}.{ext}" if dot else f"{safe_name}-{n}")
        n += 1
    dest.write_bytes(content)
    return str(dest.relative_to(MEDIA_DIR)).replace("\\", "/")


def update_thread(channel: str, key: str, patch: dict) -> dict | None:
    t = _get(channel, key)
    if t is None:
        return None
    if "ai_enabled" in patch:
        t.ai_enabled = bool(patch["ai_enabled"])
    if patch.get("name"):
        t.name = patch["name"]
    if patch.get("status") in dict(Thread.STATUS_CHOICES):
        t.status = patch["status"]
    if "assignee" in patch:
        t.assignee = str(patch["assignee"] or "").strip()[:64]
    if "labels" in patch and isinstance(patch["labels"], list):
        seen: list[str] = []
        for raw in patch["labels"]:
            label = str(raw).strip().lower()[:48]
            if label and label not in seen:
                seen.append(label)
        t.labels = seen[:20]
    t.save()
    return _serialize(_get(channel, key) or t)
