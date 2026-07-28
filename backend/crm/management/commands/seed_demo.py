"""Seed the database: demo contacts + the orders they are about, plus any
legacy file-based data (threads/accounts/config JSON from the pre-database
era, voice-call records).

Idempotent — safe to re-run; existing rows are updated, not duplicated.

    venv\\Scripts\\python manage.py seed_demo
"""
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path

from django.contrib.auth.models import User
from django.core.management.base import BaseCommand

from crm.models import (
    CallRecord, CannedResponse, ChannelAccount, Contact, Message, Notification,
    Profile, Record, RecordEvent, Setting, Thread,
)

# Starter quick replies for the chat composer — {{vars}} fill from the linked
# contact's CRM record (same catalog as the flow builder). Idempotent upserts.
DEFAULT_CANNED = [
    ("greeting", "Hi {{name}}, this is {{business_name}} about {{record_id}}. "
                 "Is now a good time?"),
    ("status", "Hi {{name}}, an update on {{record_id}}: it's currently {{record_status}}. "
               "Expected on {{due_date}}."),
    ("confirm", "Thanks {{name}} — confirming {{record_title}} for {{due_date}}. "
                "We'll send a reminder the day before."),
    ("delay", "Hi {{name}}, apologies — {{record_id}} is running late. We'll update you "
              "as soon as we have a new date. Anything we can do meanwhile?"),
    ("handover", "Thanks {{name}} — I'm passing this to a colleague who can help further. "
                 "They'll reply here shortly."),
]

# username, password, role, display name — POC logins, change before any deploy
DEMO_USERS = [
    ("superadmin", "super123", "superadmin", "Super Admin"),
    ("admin", "admin123", "admin", "Admin"),
    ("agent", "agent123", "agent", "Agent"),
]

DATA_DIR = Path(__file__).resolve().parents[3] / "data"

# `language` drives which language the voice agent defaults to for this
# contact (voice_agent/prompts.py:_language_directive) -- set per demographic
# (Malay name -> ms, Chinese name -> zh, else -> en) so demo calls actually
# exercise all three languages instead of silently defaulting to English.
# phone must be unique per contact (it's the update_or_create key everywhere) --
# the 3 test variants need distinct fake numbers. To test your own real number
# across languages, use the Live Calls manual-dial mode with a language override
# instead of separate contact rows sharing one number.
#
# `record` is the business object the conversation is about. This demo ships an
# e-commerce shape (orders + deliveries); swap it for invoices, bookings or
# tickets when you configure the template for your own vertical.
DEMO_CONTACTS = [
    dict(contact=dict(name="Beckham (zh)", phone="+60 12-333 4401", status="active",
                      method="ai-call", language="zh", tags=["vip"]),
         record=dict(type="order", external_id="ORD-1001", title="2× Ceramic mug",
                     status="packed", amount=62.25, due_in=2)),
    dict(contact=dict(name="Beckham (ms)", phone="+60 12-333 4402", status="active",
                      method="bot-call", language="ms", tags=[]),
         record=dict(type="shipment", external_id="SHP-1002", title="Parcel to Shah Alam",
                     status="in_transit", amount=128.00, due_in=-1)),
    dict(contact=dict(name="Beckham (en)", phone="+60 12-333 4403", status="active",
                      method="ai-call", language="en", tags=["wholesale"]),
         record=dict(type="invoice", external_id="INV-1003", title="July retainer",
                     status="open", amount=2150.00, due_in=-7)),
    dict(contact=dict(name="Lim Wei Jie", phone="+60 16-778 2201", status="pending",
                      method="message", language="zh", tags=[]),
         record=dict(type="booking", external_id="BKG-1004", title="Service appointment",
                     status="confirmed", amount=None, due_in=3)),
    dict(contact=dict(name="Nurul Huda", phone="+60 19-334 1120", status="pending",
                      method="message", language="ms", tags=[]),
         record=dict(type="order", external_id="ORD-1005", title="Gift set",
                     status="awaiting_payment", amount=360.00, due_in=1)),
    dict(contact=dict(name="Chong Mei Ling", phone="+60 12-556 7788", status="resolved",
                      method="ai-call", language="zh", tags=["vip"]),
         record=dict(type="order", external_id="ORD-1006", title="Bulk stationery",
                     status="delivered", amount=980.00, due_in=-3)),
    dict(contact=dict(name="Daniel Ong", phone="+60 14-220 9981", status="message-sent",
                      method="message", language="zh", tags=[]),
         record=dict(type="ticket", external_id="TCK-1007", title="Damaged on arrival",
                     status="open", amount=None, due_in=0)),
    dict(contact=dict(name="Farah Idris", phone="+60 17-889 3345", status="pending",
                      method="bot-call", language="ms", tags=[]),
         record=dict(type="shipment", external_id="SHP-1008", title="Parcel to Ipoh",
                     status="out_for_delivery", amount=54.00, due_in=0)),
    dict(contact=dict(name="Tan Ah Kow", phone="+60 18-445 6677", status="failed",
                      method="ai-call", language="zh", tags=["wholesale"]),
         record=dict(type="invoice", external_id="INV-1009", title="Q2 supply",
                     status="overdue", amount=3000.00, due_in=-30)),
    dict(contact=dict(name="Grace Lee", phone="+60 12-101 2020", status="message-sent",
                      method="message", language="zh", tags=[]),
         record=dict(type="order", external_id="ORD-1010", title="Sample pack",
                     status="new", amount=27.50, due_in=5)),
]


def _digits(phone: str) -> str:
    return re.sub(r"\D", "", phone or "")


def _read_json(path: Path, fallback):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return fallback


class Command(BaseCommand):
    help = "Seed demo contacts + records and import legacy file-based data into the DB."

    def handle(self, *args, **options):
        from ingest.models import Tenant, Entity
        from crm.models import ContactEntity

        # ---- tenants + entities (multi-tenancy) -------------------------------
        default_tenant, _ = Tenant.objects.get_or_create(
            slug="default", defaults={"name": "Northwind Trading", "status": "active"})
        # A second tenant proves isolation: its data must never leak into the
        # default tenant's endpoints.
        acme_tenant, _ = Tenant.objects.get_or_create(
            slug="acme", defaults={"name": "Acme Services", "status": "active"})
        # Each tenant owns entities; contacts are shared across a tenant's entities.
        default_entity, _ = Entity.objects.get_or_create(
            tenant=default_tenant, slug="northwind", defaults={"name": "Northwind"})
        default_entity2, _ = Entity.objects.get_or_create(
            tenant=default_tenant, slug="northwind-pro", defaults={"name": "Northwind Pro"})
        acme_entity, _ = Entity.objects.get_or_create(
            tenant=acme_tenant, slug="acme", defaults={"name": "Acme"})

        # ---- RBAC capability matrix -----------------------------------------
        # Idempotent seed of crm.RolePermission (the data-driven permission
        # matrix). Safe on an existing DB — never clobbers operator edits.
        from crm.permissions_store import ensure_default_permissions
        ensure_default_permissions()
        self.stdout.write("permissions: RBAC capability matrix seeded")

        # ---- users + roles (RBAC) -------------------------------------------
        for username, password, role, display in DEMO_USERS:
            user, was_created = User.objects.get_or_create(
                username=username,
                defaults={"first_name": display, "is_superuser": role == "superadmin",
                          "is_staff": role in ("superadmin", "admin")},
            )
            if was_created:
                user.set_password(password)
                user.save()
            Profile.objects.update_or_create(
                user=user, defaults={"role": role, "tenant": default_tenant})
            if not user.notifications.exists():
                Notification.objects.create(
                    user=user, kind="system", title=f"Welcome, {display}!",
                    body="You'll be notified here about new contact messages, calls and system changes.",
                )
        self.stdout.write(f"users: {[u[0] for u in DEMO_USERS]} ready")

        # The agent gets access to one entity only (demonstrates entity scoping);
        # admin/superadmin need no explicit entities (all-in-tenant / all).
        agent_user = User.objects.filter(username="agent").first()
        if agent_user:
            agent_user.profile.entities.set([default_entity])

        # ---- contacts + the records their conversations are about ----------
        created = 0
        today = datetime.now(timezone.utc).date()
        for row in DEMO_CONTACTS:
            c = row["contact"]
            cust, was_created = Contact.objects.update_or_create(
                phone=c["phone"], tenant=default_tenant,
                defaults={**c, "company_name": "Northwind Trading",
                          "external_id": row["record"]["external_id"].split("-")[-1]},
            )
            created += was_created
            # Share every demo contact across both Northwind entities.
            for b in (default_entity, default_entity2):
                ContactEntity.objects.get_or_create(contact=cust, entity=b)
            r = row["record"]
            record, _ = Record.objects.update_or_create(
                tenant=default_tenant, type=r["type"], external_id=r["external_id"],
                defaults={"contact": cust, "title": r["title"], "status": r["status"],
                          "amount": r["amount"], "currency": "MYR",
                          "due_date": today + timedelta(days=r["due_in"])},
            )
            if not record.events.exists():
                RecordEvent.objects.create(
                    record=record, kind="status",
                    at=datetime.now(timezone.utc) - timedelta(days=1),
                    data={"to": r["status"]},
                )
        # Any legacy contacts without a tenant join the default tenant.
        Contact.objects.filter(tenant__isnull=True).update(tenant=default_tenant)
        self.stdout.write(f"contacts: {created} created, {len(DEMO_CONTACTS) - created} updated")

        # A lone contact in the second tenant — used to verify isolation.
        acme_cust, _ = Contact.objects.update_or_create(
            phone="+60 19-000 0001", tenant=acme_tenant,
            defaults={"name": "Acme Test Contact", "status": "pending",
                      "company_name": "Acme Services"},
        )
        ContactEntity.objects.get_or_create(contact=acme_cust, entity=acme_entity)
        acme_user, acme_new = User.objects.get_or_create(
            username="acmeadmin",
            defaults={"first_name": "Acme Admin", "is_staff": True},
        )
        if acme_new:
            acme_user.set_password("acme123")
            acme_user.save()
        Profile.objects.update_or_create(
            user=acme_user, defaults={"role": "admin", "tenant": acme_tenant})

        # ---- legacy WhatsApp threads (data/whatsapp/<digits>.json) --------
        imported = 0
        wa_dir = DATA_DIR / "whatsapp"
        if wa_dir.exists():
            for p in wa_dir.glob("*.json"):
                if not p.stem.isdigit():
                    continue
                t = _read_json(p, None)
                if not isinstance(t, dict) or not t.get("wa_id"):
                    continue
                call = t.get("call") or {}
                thread, _ = Thread.objects.update_or_create(
                    channel="whatsapp", key=t["wa_id"],
                    defaults={
                        "address": t.get("phone", ""), "name": t.get("name", ""),
                        "ai_enabled": t.get("ai_enabled", True),
                        "call_permission": call.get("permission", "none"),
                        "call_status": call.get("status", "idle"),
                        "contact": Contact.objects.filter(
                            phone__regex=r"\D*".join(list(t["wa_id"]))).first()
                        or next((c for c in Contact.objects.all()
                                 if _digits(c.phone) == t["wa_id"]), None),
                    },
                )
                if not thread.messages.exists():
                    for m in t.get("messages", []):
                        Message.objects.create(
                            thread=thread, external_id=m.get("id", ""),
                            direction=m.get("dir", "out"), source=m.get("source", "system"),
                            text=m.get("text", ""), status=m.get("status", "sent"),
                        )
                imported += 1

        # ---- legacy email/social threads ----------------------------------
        msg_dir = DATA_DIR / "messaging" / "threads"
        if msg_dir.exists():
            for p in msg_dir.glob("*.json"):
                t = _read_json(p, None)
                if not isinstance(t, dict) or not t.get("key"):
                    continue
                thread, _ = Thread.objects.update_or_create(
                    channel=t.get("channel", "email"), key=t["key"],
                    defaults={
                        "address": t.get("address", ""), "name": t.get("name", ""),
                        "ai_enabled": t.get("ai_enabled", False),
                    },
                )
                if not thread.messages.exists():
                    for m in t.get("messages", []):
                        Message.objects.create(
                            thread=thread, external_id=m.get("id", ""),
                            direction=m.get("dir", "out"), source=m.get("source", "system"),
                            text=m.get("text", ""), status=m.get("status", "sent"),
                        )
                imported += 1
        self.stdout.write(f"threads: {imported} imported from legacy files")

        # ---- legacy account registries + settings --------------------------
        for a in _read_json(DATA_DIR / "whatsapp" / "accounts.json", []) or []:
            if a.get("phone_number_id") and a.get("access_token"):
                ChannelAccount.objects.get_or_create(
                    channel="whatsapp", external_id=a["phone_number_id"],
                    defaults={"name": a.get("name", "WhatsApp account"),
                              "access_token": a.get("access_token", ""),
                              "app_secret": a.get("app_secret", ""),
                              "api_version": a.get("api_version", "v23.0"),
                              "owner": a.get("owner", "admin")},
                )
        for a in _read_json(DATA_DIR / "messaging" / "accounts.json", []) or []:
            if a.get("access_token"):
                ChannelAccount.objects.get_or_create(
                    channel=a.get("channel", "facebook"), external_id=a.get("external_id", ""),
                    defaults={"name": a.get("name", "Social account"),
                              "access_token": a.get("access_token", ""),
                              "owner": a.get("owner", "admin")},
                )
        for cfg_file in (DATA_DIR / "whatsapp" / "config.json", DATA_DIR / "messaging" / "config.json"):
            for k, v in (_read_json(cfg_file, {}) or {}).items():
                if isinstance(v, str) and v.strip():
                    Setting.objects.update_or_create(key=k, defaults={"value": v.strip()})

        # ---- voice-call records (agent writes the file; mirror to DB) -------
        calls = 0
        for c in _read_json(DATA_DIR / "calls.json", []) or []:
            if not c.get("call_id"):
                continue
            called = c.get("contact") or c.get("borrower") or {}
            phone = called.get("phone", "")
            CallRecord.objects.update_or_create(
                call_id=c["call_id"],
                defaults={
                    "phone": phone, "name": called.get("contact_name", ""),
                    "started_at": c.get("started_at", ""), "ended_at": c.get("ended_at", ""),
                    "transcript": c.get("transcript", ""), "summary": c.get("summary", ""),
                    "outcome": c.get("outcome", "unclear"),
                    "followup_date": c.get("followup_date"),
                    "needs_human": bool(c.get("needs_human")),
                    "contact": next((cu for cu in Contact.objects.all()
                                      if _digits(cu.phone) == _digits(phone)), None),
                },
            )
            calls += 1
        self.stdout.write(f"call records: {calls} mirrored")

        for short_code, content in DEFAULT_CANNED:
            CannedResponse.objects.update_or_create(
                short_code=short_code,
                defaults={"content": content, "created_by": "seed"},
            )
        self.stdout.write(f"canned responses: {len(DEFAULT_CANNED)} ready")
        self.stdout.write(self.style.SUCCESS("seed_demo complete"))
