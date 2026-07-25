"""External payloads -> crm.Contact / crm.Record / crm.RecordEvent.

This is the piece that makes a vertical work end to end: a Shopify order or a
Stripe payment lands as the SAME business object the flow engine, the AI brain
and the voice agent already understand, with no per-integration code anywhere
else in the platform.

Contact matching is phone-first, email-second (a phone number is the identity
every channel here shares). No match + enough identity to talk to them later =
a new Contact, because an order from a first-time buyer is exactly the case
the e-commerce vertical exists for.
"""
from __future__ import annotations

import logging
import re
from decimal import Decimal, InvalidOperation

from django.utils import timezone

logger = logging.getLogger(__name__)


def digits(value) -> str:
    return re.sub(r"\D", "", str(value or ""))


def to_decimal(value):
    if value in (None, ""):
        return None
    try:
        return Decimal(str(value))
    except (InvalidOperation, ValueError, TypeError):
        return None


def find_contact(phone: str = "", email: str = ""):
    """crm.Contact by phone digits (suffix match — stored numbers carry
    formatting), else by email, else None."""
    from crm.models import Contact

    d = digits(phone)
    if d:
        contact = Contact.objects.filter(phone=f"+{d}").first()
        if contact is not None:
            return contact
        if len(d) >= 7:
            tail = d[-9:]
            for c in Contact.objects.exclude(phone=""):
                if digits(c.phone).endswith(tail):
                    return c
    addr = str(email or "").strip()
    if addr:
        return Contact.objects.filter(email__iexact=addr).first()
    return None


def get_or_create_contact(phone: str = "", email: str = "", name: str = "",
                          company: str = ""):
    """Match, or create when we have something to contact them on. Returns
    (contact, created). (None, False) when the payload is anonymous."""
    from crm.models import Contact

    contact = find_contact(phone, email)
    if contact is not None:
        # backfill an identity the CRM was missing — a Shopify order often
        # carries the email a phone-only record never had
        fields = []
        if email and not contact.email:
            contact.email = str(email)[:254]
            fields.append("email")
        if phone and not contact.phone:
            contact.phone = f"+{digits(phone)}"
            fields.append("phone")
        if fields:
            contact.save(update_fields=fields + ["updated_at"])
        return contact, False
    if not (digits(phone) or str(email or "").strip()):
        return None, False
    contact = Contact.objects.create(
        name=(str(name or "").strip() or str(email or "").split("@")[0] or "Unknown")[:120],
        phone=f"+{digits(phone)}" if digits(phone) else "",
        email=str(email or "").strip()[:254],
        company_name=str(company or "")[:120],
        status="active",
    )
    return contact, True


def upsert_record(contact, *, record_type: str, external_id: str, title: str = "",
                  status: str = "", amount=None, currency: str = "",
                  due_date=None, data: dict | None = None):
    """Create or update the Record this external object maps to.

    Keyed on (tenant, type, external_id) — the same uniqueness the model
    enforces. Returns (record, created, old_status).
    """
    from crm.models import Record

    ext = str(external_id or "").strip()[:64]
    qs = Record.objects.filter(type=record_type, external_id=ext) if ext else None
    record = qs.filter(contact=contact).first() or (qs.first() if qs else None)
    old_status = record.status if record is not None else ""
    if record is None:
        record = Record(contact=contact, tenant=contact.tenant if contact else None,
                        type=record_type, external_id=ext)
    fields: dict = {}
    if title:
        fields["title"] = str(title)[:190]
    if status:
        fields["status"] = str(status)[:32]
    if amount is not None:
        fields["amount"] = amount
    if currency:
        fields["currency"] = str(currency)[:8]
    if due_date is not None:
        fields["due_date"] = due_date
    if data:
        merged = dict(record.data or {})
        merged.update(data)
        fields["data"] = merged
    for key, value in fields.items():
        setattr(record, key, value)
    if contact is not None and record.contact_id != contact.pk:
        record.contact = contact
        record.tenant = contact.tenant
    created = record.pk is None
    record.save()
    return record, created, old_status


def book_event(record, kind: str, *, amount=None, data: dict | None = None,
               at=None, dedupe_key: str = ""):
    """Append a RecordEvent. `dedupe_key` (the provider's event id) makes
    webhook redelivery idempotent — the same event never books twice."""
    from crm.models import RecordEvent

    payload = dict(data or {})
    if dedupe_key:
        payload["event_id"] = dedupe_key
        if RecordEvent.objects.filter(record=record, data__event_id=dedupe_key).exists():
            return None
    return RecordEvent.objects.create(record=record, kind=kind, at=at or timezone.now(),
                                      amount=amount, data=payload)


# ---- Shopify --------------------------------------------------------------------

def shopify_order(payload: dict, topic: str = "", event_id: str = "") -> dict:
    """orders/create | orders/updated | orders/* -> Contact + Record + events.

    Returns a small result dict for the webhook response and the tests.
    """
    customer = payload.get("customer") or {}
    address = (payload.get("shipping_address") or payload.get("billing_address")
               or customer.get("default_address") or {})
    phone = (payload.get("phone") or customer.get("phone") or address.get("phone") or "")
    email = (payload.get("email") or payload.get("contact_email")
             or customer.get("email") or "")
    name = " ".join(x for x in (customer.get("first_name"), customer.get("last_name")) if x)
    contact, contact_created = get_or_create_contact(
        phone=phone, email=email, name=name or address.get("name") or "",
        company=address.get("company") or "")
    if contact is None:
        return {"ok": False, "detail": "order carried no phone or email — skipped"}

    order_id = str(payload.get("id") or payload.get("order_number") or "").strip()
    order_name = str(payload.get("name") or f"#{payload.get('order_number', '')}").strip()
    financial = str(payload.get("financial_status") or "").strip().lower()
    fulfillment = str(payload.get("fulfillment_status") or "").strip().lower()
    status = fulfillment or financial or "open"
    record, record_created, old_status = upsert_record(
        contact,
        record_type="order",
        external_id=order_id,
        title=order_name or f"Order {order_id}",
        status=status,
        amount=to_decimal(payload.get("total_price")),
        currency=str(payload.get("currency") or "") or "",
        data={
            "source": "shopify",
            "order_number": payload.get("order_number"),
            "financial_status": financial,
            "fulfillment_status": fulfillment,
            "line_items": [
                {"title": li.get("title"), "quantity": li.get("quantity"),
                 "price": li.get("price")}
                for li in (payload.get("line_items") or [])[:20]
                if isinstance(li, dict)
            ],
            "link": payload.get("order_status_url") or "",
            "tracking_url": ((payload.get("fulfillments") or [{}])[0] or {})
                            .get("tracking_url") or "",
        },
    )
    events = []
    if record_created or old_status != record.status:
        ev = book_event(record, "status",
                        data={"topic": topic or "orders", "from": old_status,
                              "to": record.status, "source": "shopify"},
                        dedupe_key=f"{event_id}:status" if event_id else "")
        if ev is not None:
            events.append("status")
    if financial == "paid":
        ev = book_event(record, "payment", amount=record.amount,
                        data={"source": "shopify", "financial_status": financial},
                        dedupe_key=f"shopify-paid:{order_id}")
        if ev is not None:
            events.append("payment")
    return {"ok": True, "contact_id": contact.pk, "contact_created": contact_created,
            "record_id": record.pk, "record_created": record_created, "events": events}


# ---- Stripe ---------------------------------------------------------------------

def stripe_payment(event: dict) -> dict:
    """payment_intent.succeeded -> a payment RecordEvent on the right Record.

    `metadata.record_external_id` (+ optional `record_type`) attaches the
    payment to an EXISTING record — that is how a Stripe payment closes the
    Shopify order it was raised for. Without it we book a standalone invoice
    record keyed on the PaymentIntent id.
    """
    obj = ((event.get("data") or {}).get("object")) or {}
    event_id = str(event.get("id") or "")
    metadata = obj.get("metadata") or {}
    charge = ((obj.get("charges") or {}).get("data") or [{}])[0] or {}
    billing = charge.get("billing_details") or {}
    email = (obj.get("receipt_email") or billing.get("email")
             or metadata.get("email") or "")
    phone = (billing.get("phone") or metadata.get("phone") or "")
    contact, contact_created = get_or_create_contact(
        phone=phone, email=email, name=billing.get("name") or metadata.get("name") or "")
    if contact is None:
        return {"ok": False, "detail": "payment carried no phone or email — skipped"}

    try:
        amount = Decimal(str(obj.get("amount_received") or obj.get("amount") or 0)) / 100
    except (InvalidOperation, ValueError, TypeError):
        amount = None
    currency = str(obj.get("currency") or "").upper()
    ext = str(metadata.get("record_external_id") or "").strip()
    record_type = str(metadata.get("record_type") or ("order" if ext else "invoice"))
    record, record_created, old_status = upsert_record(
        contact,
        record_type=record_type,
        external_id=ext or str(obj.get("id") or "")[:64],
        title=str(obj.get("description") or "").strip() or f"Payment {obj.get('id', '')}",
        status="paid",
        amount=amount if not ext else None,   # an existing order keeps its own total
        currency=currency,
        data={"source": "stripe", "payment_intent": obj.get("id"),
              "receipt_email": email},
    )
    booked = book_event(record, "payment", amount=amount,
                        data={"source": "stripe", "payment_intent": obj.get("id"),
                              "currency": currency, "from": old_status},
                        dedupe_key=event_id or str(obj.get("id") or ""))
    return {"ok": True, "contact_id": contact.pk, "contact_created": contact_created,
            "record_id": record.pk, "record_created": record_created,
            "events": ["payment"] if booked is not None else [],
            "duplicate": booked is None}
