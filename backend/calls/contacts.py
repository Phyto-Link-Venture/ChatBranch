"""Contact list for the dashboard — DATABASE-FIRST (crm.Contact), falling
back to the legacy data/contacts.json file when the table is empty (fresh
clones before `manage.py seed_demo`). Output shape is snake_case contact
dicts, mapped to the frontend's camelCase Contact by to_contact().
"""
import json
from pathlib import Path

from django.conf import settings

CONTACTS_FILE = Path(settings.BASE_DIR) / "data" / "contacts.json"

_HISTORY_LIMIT = 8


def _event_text(record, ev) -> str:
    label = record.external_id or record.title or record.get_type_display()
    if ev.kind == "payment" and ev.amount is not None:
        return f"{label}: payment {record.currency} {ev.amount:,.2f}."
    if ev.kind == "status":
        to = (ev.data or {}).get("to")
        return f"{label}: status → {to}." if to else f"{label}: status updated."
    note = (ev.data or {}).get("note")
    return f"{label}: {note}" if note else f"{label}: {ev.get_kind_display().lower()}."


def _history_for(c) -> list[dict]:
    """Recent touchpoints from the DB: call records + record events."""
    items = []
    for call in c.calls.all()[:_HISTORY_LIMIT]:
        items.append({
            "type": "ai-call",
            "date": (call.ended_at or call.started_at or "")[:16].replace("T", ", "),
            "text": call.summary or f"AI call — outcome: {call.outcome}.",
        })
    for r in c.records.all()[:_HISTORY_LIMIT]:
        for ev in r.events.all()[:_HISTORY_LIMIT]:
            items.append({
                "type": ev.kind,
                "date": ev.at.strftime("%d %b, %H:%M"),
                "text": _event_text(r, ev),
            })
    return items[:_HISTORY_LIMIT]


def _record_dict(r) -> dict:
    return {
        "id": r.pk,
        "type": r.type,
        "external_id": r.external_id,
        "title": r.title,
        "status": r.status,
        "amount": float(r.amount) if r.amount is not None else None,
        "currency": r.currency,
        "due_date": r.due_date.isoformat() if r.due_date else None,
        "data": r.data or {},
    }


def _from_model(c) -> dict:
    return {
        "id": c.pk,
        "name": c.name,
        "phone": c.phone,
        "email": c.email,
        "status": c.status,
        "method": c.method,
        "language": c.language,
        "company_name": c.company_name,
        "external_id": c.external_id,
        "tags": list(c.tags or []),
        "attributes": dict(c.attributes or {}),
        # Entity slugs this contact is linked to (a tenant's contacts are shared
        # across its entities) — powers the topbar entity switcher's scoping.
        "entities": [bl.entity.slug for bl in c.entity_links.all()],
        "history": _history_for(c),
        # The business objects a conversation with this contact may be about.
        "records": [_record_dict(r) for r in c.records.all()[:_HISTORY_LIMIT]],
    }


def load_contacts(tenant=None, entity=None) -> list[dict]:
    """Contact list. When `tenant` is given, only that tenant's contacts are
    returned; None means unscoped (legacy). When `entity` (a Entity slug) is
    given, narrows to contacts linked to that entity — a tenant's contacts are
    shared across its entities, so this scopes the view to one entity for the
    topbar switcher."""
    from crm.models import Contact

    qs = Contact.objects.prefetch_related("calls", "records__events", "entity_links__entity")
    if tenant is not None:
        qs = qs.filter(tenant=tenant)
    if entity:
        qs = qs.filter(entity_links__entity__slug=entity).distinct()
    if qs.exists():
        return [_from_model(c) for c in qs]

    # legacy fallback: pre-database JSON file (only when the DB has no rows at
    # all; a tenant/entity with zero contacts returns an empty list, not the
    # fallback).
    if tenant is not None or entity or Contact.objects.exists():
        return []
    if not CONTACTS_FILE.exists():
        return []
    try:
        with open(CONTACTS_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, list) else []
    except (json.JSONDecodeError, OSError):
        return []


def get_contact(contact_id: int, tenant=None) -> dict | None:
    from crm.models import Contact

    qs = Contact.objects.filter(pk=contact_id)
    if tenant is not None:
        qs = qs.filter(tenant=tenant)
    c = qs.prefetch_related("calls", "records__events").first()
    if c:
        return _from_model(c)
    if tenant is not None:
        return None  # scoped lookups never fall back to the legacy JSON
    for raw in load_contacts():
        if raw.get("id") == contact_id:
            return raw
    return None


def primary_record(c: dict) -> dict:
    """The record a conversation defaults to: the first still-open one, else
    the first of any. Verticals with several live records per contact should
    pick explicitly at the call site."""
    records = c.get("records") or []
    closed = ("closed", "done", "resolved", "cancelled", "completed")
    return next((r for r in records if str(r.get("status", "")).lower() not in closed),
                records[0] if records else {})


def to_contact(c: dict) -> dict:
    """Map a raw contact record (snake_case) to the frontend Contact shape."""
    return {
        "id": c.get("id"),
        "name": c.get("name", ""),
        "phone": c.get("phone", ""),
        "email": c.get("email", ""),
        "company": c.get("company_name", ""),
        "status": c.get("status", "pending"),
        "method": c.get("method"),
        "language": c.get("language", "en"),
        "tags": c.get("tags", []),
        "attributes": c.get("attributes", {}),
        "entities": c.get("entities", []),
        "records": c.get("records", []),
        "history": c.get("history", []),
    }


def dial_meta(c: dict) -> dict:
    """Build the dispatch metadata the voice agent needs to run the call.

    Everything vertical-specific rides on the contact's primary `Record`, so a
    logistics deployment sends a shipment and an invoicing one sends an invoice
    through exactly the same field names.
    """
    record = primary_record(c)
    amount = record.get("amount")
    currency = record.get("currency") or "MYR"
    meta = {
        "contact_name": c.get("name"),
        "company_name": c.get("company_name"),
        "language": c.get("language"),
        "tags": c.get("tags", []),
        "record_type": record.get("type"),
        "record_id": record.get("external_id"),
        "record_title": record.get("title"),
        "record_status": record.get("status"),
        "record_amount": f"{currency}{amount}" if amount is not None else "",
        "record_currency": currency,
        "record_due_date": record.get("due_date"),
        "record_data": record.get("data") or {},
    }
    # COMPANY KNOWLEDGE — the same operator-authored block the chat brain and
    # the WhatsApp call transports inject (aicore/knowledge.py). Bounded and
    # exception-safe: a SIP dial must never fail because of the knowledge base.
    try:
        from aicore import knowledge as ai_knowledge
        from crm.models import Contact

        contact = Contact.objects.filter(pk=c.get("id")).first() if c.get("id") else None
        block = ai_knowledge.build_knowledge_context(contact, channel="voice")
        if block:
            meta["knowledge"] = block
    except Exception:
        pass
    return meta
