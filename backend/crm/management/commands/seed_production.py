"""Seed a PRODUCTION deployment: the real tenant + entity, the default role
set (superadmin/admin/agent) with known default passwords (change them
after first login!), safe runtime Settings, the starter canned-response
catalog and (optionally) a real contact book — WITHOUT any of seed_demo's
demo fixtures. Ends with a production-readiness report of the .env keys each
subsystem needs.

Idempotent — safe to re-run. Existing rows are never overwritten (operator
edits and dashboard Setting changes survive); only missing pieces are filled.

    venv\\Scripts\\python manage.py seed_production \\
        --tenant-name "Acme" --entity-name "Acme" \\
        --contacts path\\to\\contacts.json --purge-demo

Password for the superadmin: --admin-password, else env SEED_ADMIN_PASSWORD,
else the DEFAULT_PASSWORDS entry. Admin/agent always use their
DEFAULT_PASSWORDS entry. The readiness report warns while any seeded user is
still on a default password.
"""
import json
import os
from decimal import Decimal, InvalidOperation
from pathlib import Path

from django.contrib.auth.models import User
from django.core.management.base import BaseCommand, CommandError
from django.db import transaction

from crm.models import (
    CannedResponse, ChannelAccount, Contact, ContactEntity, Notification,
    Profile, Record, Setting,
)
from crm.management.commands.seed_demo import (
    DATA_DIR, DEFAULT_CANNED, DEMO_CONTACTS, DEMO_USERS,
)

# Runtime Setting rows a production deployment should start from. All of these
# match the code defaults EXCEPT quiet hours, which the code leaves disabled —
# production defaults to a polite contact window (no outreach 21:00-08:00
# server-local; blank the rows in Settings to disable). Seeded with
# get_or_create so an operator's later dashboard edits are never clobbered.
PRODUCTION_SETTINGS = {
    # Operating model knobs (Settings → routing rules; whatsapp/wa_config.py)
    "ai_reply_mode": "queued",       # one coherent AI reply per burst, paced
    "business_redirect": "true",     # reminder-number inbound → wa.me nudge
    "recovery_mode": "one_click",    # operator confirms recovery blasts
    "cold_text_guard": "block",      # no cold free-text from personal numbers
    # Outbound queue pacing (flows/queue.py — derived from Baileys ban research)
    "queue_min_gap_s": "10",
    "queue_max_gap_s": "30",
    "queue_daily_cap": "200",
    "queue_active_cap": "8",
    "queue_active_window_h": "24",
    # Compliance contact window, server-local HH:MM (whatsapp/wa_config.py)
    "quiet_start": "21:00",
    "quiet_end": "08:00",
}

# Known seed passwords — deliberately DIFFERENT from seed_demo's (the purge
# logic identifies demo users by their demo password). Change after first
# login; the readiness report nags until you do.
DEFAULT_PASSWORDS = {
    "superadmin": "superadmin@123",
    "admin": "admin@123",
    "agent": "agent@123",
}

# File-based stores that hold CALL/CHAT HISTORY, not config — the voice-agent
# IPC files and recordings that a DB reseed can't clear (they're files on
# purpose; see CLAUDE.md). --purge-files ARCHIVES these under
# data/_archive/<timestamp>/ so production starts with a clean history.
# Deliberately NOT listed: whatsapp/accounts.json + config.json (legacy config
# registries, inert at runtime). The Baileys pairing state lives in
# baileys_service/, never under data/.
PURGE_FILE_TARGETS = [
    "calls.json",                # voice-agent call records (agent process file)
    "live",                      # live transcripts (data/live/{room}.json)
    "inbox",                     # operator instruction inbox
    "whatsapp/calls",            # call signals, logs, recordings + transcripts
    "whatsapp/media",            # message attachments (their DB rows are gone)
    "messaging/threads",         # legacy email/social thread files
]

# Setting keys never worth importing from a backup: runtime caches the app
# rebuilds on its own, and keys from removed integrations.
SKIP_IMPORT_KEYS = {
    "channel_health_checked_at", "email_health", "voice_health",
    "chatwoot_webhook_token",
}

VALID_STATUS = {"active", "pending", "message-sent", "resolved", "failed"}
VALID_METHOD = {"ai-call", "bot-call", "message"}
VALID_LANGUAGE = {"en", "ms", "zh"}


class Command(BaseCommand):
    help = ("Seed baseline production data: tenant/entity, superadmin, runtime "
            "settings, canned responses; optionally import a contact book "
            "and purge seed_demo fixtures.")

    def add_arguments(self, parser):
        parser.add_argument("--tenant-name", default="ChatBranch",
                            help="Organization name (default: ChatBranch)")
        parser.add_argument("--tenant-slug", default="chatbranch",
                            help="Tenant slug (default: chatbranch)")
        parser.add_argument("--entity-name", default=None,
                            help="Entity name (default: same as tenant name)")
        parser.add_argument("--entity-slug", default=None,
                            help="Entity slug (default: same as tenant slug)")
        parser.add_argument("--admin-username", default="superadmin",
                            help="Superadmin username (default: superadmin)")
        parser.add_argument("--admin-password", default=None,
                            help="Superadmin password (else env SEED_ADMIN_PASSWORD, "
                                 "else the known default super@123). For an EXISTING "
                                 "user this resets the password only when passed "
                                 "explicitly.")
        parser.add_argument("--admin-email", default="",
                            help="Superadmin email (optional)")
        parser.add_argument("--contacts", default=None, metavar="PATH",
                            help="Import a contact book: JSON list of "
                                 "{name, phone, email, language, tags, attributes, "
                                 "record: {...}}. Upserts by (tenant, phone).")
        parser.add_argument("--purge-demo", action="store_true",
                            help="Remove seed_demo fixtures: demo users still on their "
                                 "demo passwords, demo contacts, and the acme test tenant.")
        parser.add_argument("--purge-files", action="store_true",
                            help="Archive the file-based call/chat history (recordings, "
                                 "transcripts, live/inbox IPC files, media, legacy thread "
                                 "JSONs) into data/_archive/<timestamp>/ — a DB reseed "
                                 "cannot clear these. Baileys pairing state is untouched.")
        parser.add_argument("--settings-from", default=None, metavar="SQLITE_PATH",
                            help="Copy crm_setting rows (API keys, verify token, SMTP "
                                 "config, …) AND crm_channelaccount rows (WhatsApp/"
                                 "Telegram/email provider accounts with credentials) "
                                 "from a previous database backup, e.g. db_backup.sqlite3. "
                                 "Imported values OVERWRITE same-key rows; transient "
                                 "health state is skipped/reset.")

    def handle(self, *args, **options):
        from ingest.models import Tenant, Entity

        entity_name = options["entity_name"] or options["tenant_name"]
        entity_slug = options["entity_slug"] or options["tenant_slug"]

        if options["purge_files"]:
            self._purge_files()

        with transaction.atomic():
            if options["purge_demo"]:
                self._purge_demo()

            # ---- tenant + entity -------------------------------------------
            tenant, t_new = Tenant.objects.get_or_create(
                slug=options["tenant_slug"],
                defaults={"name": options["tenant_name"], "status": "active"})
            entity, b_new = Entity.objects.get_or_create(
                tenant=tenant, slug=entity_slug, defaults={"name": entity_name})
            self.stdout.write(
                f"tenant: {tenant.name} ({tenant.slug}) "
                f"{'created' if t_new else 'exists'}; "
                f"entity: {entity.name} {'created' if b_new else 'exists'}")
            other = Tenant.objects.exclude(pk=tenant.pk)
            if other.exists():
                self.stdout.write(self.style.WARNING(
                    "  other tenants present: "
                    + ", ".join(other.values_list("slug", flat=True))
                    + " — leftover from seed_demo? (--purge-demo removes the "
                      "acme test tenant; the 'default' tenant is never touched)"))

            # ---- users (superadmin / admin / agent) ----------------------
            self._ensure_users(tenant, entity, options)

            # ---- settings from a previous DB backup (before the defaults
            # below, which are get_or_create and therefore never override
            # what the import restored) ------------------------------------
            if options["settings_from"]:
                self._import_settings(options["settings_from"])
                self._import_accounts(options["settings_from"])

            # ---- runtime settings (get_or_create — operator edits win) ----
            seeded = 0
            for key, value in PRODUCTION_SETTINGS.items():
                _, created = Setting.objects.get_or_create(
                    key=key, defaults={"value": value})
                seeded += created
            self.stdout.write(
                f"settings: {seeded} seeded, "
                f"{len(PRODUCTION_SETTINGS) - seeded} already set "
                f"(quiet hours default {PRODUCTION_SETTINGS['quiet_start']}–"
                f"{PRODUCTION_SETTINGS['quiet_end']} server-local)")

            # ---- webhook verify token (auto-generates its Setting row) ----
            from whatsapp import wa_config
            token = wa_config.verify_token()
            self.stdout.write(
                f"webhook verify token: {token}\n"
                "  paste this into the Meta app's webhook configuration "
                "(also shown in Settings → App Keys)")

            # ---- canned responses ----------------------------------------
            canned = 0
            for short_code, content in DEFAULT_CANNED:
                _, created = CannedResponse.objects.get_or_create(
                    short_code=short_code,
                    defaults={"content": content, "created_by": "seed"})
                canned += created
            self.stdout.write(f"canned responses: {canned} seeded, "
                              f"{len(DEFAULT_CANNED) - canned} already present")

            # ---- optional contact book -----------------------------------
            if options["contacts"]:
                self._import_contacts(options["contacts"], tenant, entity)

        self._readiness_report(tenant)
        self.stdout.write(self.style.SUCCESS("seed_production complete"))

    # ------------------------------------------------------------------ users
    def _ensure_users(self, tenant, entity, options):
        """The full default role set, like seed_demo but with the production
        DEFAULT_PASSWORDS (superadmin's is overridable via --admin-password /
        SEED_ADMIN_PASSWORD). Existing users are left untouched — but a user
        still on a seed_demo password is called out."""
        demo_passwords = {u[0]: u[1] for u in DEMO_USERS}
        roster = [
            (options["admin_username"], "superadmin", "Super Admin",
             options["admin_password"] or os.getenv("SEED_ADMIN_PASSWORD", "")
             or DEFAULT_PASSWORDS["superadmin"]),
            ("admin", "admin", "Admin", DEFAULT_PASSWORDS["admin"]),
            ("agent", "agent", "Agent", DEFAULT_PASSWORDS["agent"]),
        ]
        for username, role, display, password in roster:
            user = User.objects.filter(username=username).first()
            if user is None:
                user = User.objects.create(
                    username=username, first_name=display,
                    email=options["admin_email"] if role == "superadmin" else "",
                    is_superuser=role == "superadmin",
                    is_staff=role in ("superadmin", "admin"))
                user.set_password(password)
                user.save()
                Notification.objects.create(
                    user=user, kind="system", title=f"Welcome, {display}!",
                    body="You'll be notified here about new contact messages, "
                         "calls and system changes.")
                shown = password if password in DEFAULT_PASSWORDS.values() \
                    else "(the supplied password)"
                self.stdout.write(f"{role} '{username}' created — password: {shown}")
            else:
                # Never silently rotate a live credential: the superadmin's
                # password resets only on an explicit --admin-password.
                if role == "superadmin" and options["admin_password"]:
                    user.set_password(options["admin_password"])
                    user.is_superuser = True
                    user.is_staff = True
                    user.save()
                    self.stdout.write(self.style.WARNING(
                        f"{role} '{username}' exists — password RESET as requested"))
                elif username in demo_passwords and user.check_password(demo_passwords[username]):
                    self.stdout.write(self.style.WARNING(
                        f"{role} '{username}' exists and is STILL ON THE seed_demo "
                        "PASSWORD — run with --purge-demo (then re-run) or change it"))
                else:
                    self.stdout.write(f"{role} '{username}' exists — left untouched")
            Profile.objects.update_or_create(
                user=user, defaults={"role": role, "tenant": tenant})
            if role == "agent":
                user.profile.entities.add(entity)

    # ------------------------------------------------------------ file stores
    def _purge_files(self):
        """Archive (move, never delete) the file-based history stores under
        data/_archive/<timestamp>/. Run with the servers STOPPED — the voice
        agent and bridges write into these paths."""
        import shutil
        from datetime import datetime

        archive = DATA_DIR / "_archive" / datetime.now().strftime("%Y%m%d-%H%M%S")
        targets = [DATA_DIR / t for t in PURGE_FILE_TARGETS]
        # Legacy per-thread WhatsApp JSONs live flat in data/whatsapp/ with a
        # digits-only name (the wa_id) — sweep those too.
        wa_dir = DATA_DIR / "whatsapp"
        if wa_dir.exists():
            targets += [p for p in wa_dir.glob("*.json") if p.stem.isdigit()]
        moved = 0
        for src in targets:
            if not src.exists():
                continue
            dest = archive / src.relative_to(DATA_DIR)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(src), str(dest))
            moved += 1
        if moved:
            self.stdout.write(f"file history archived: {moved} item(s) → "
                              f"{archive.relative_to(DATA_DIR.parent)}")
        else:
            self.stdout.write("file history: nothing to archive")

    # -------------------------------------------------------- settings import
    def _import_settings(self, path):
        """Restore crm_setting rows (API keys, verify token, SMTP config, …)
        straight from a previous SQLite backup — values never touch the
        console. Imported values overwrite same-key rows on purpose: the
        backup is the source of truth when this flag is used."""
        import sqlite3

        p = Path(path)
        if not p.exists():
            raise CommandError(f"settings backup not found: {p}")
        try:
            con = sqlite3.connect(f"file:{p.as_posix()}?mode=ro", uri=True)
            rows = con.execute("SELECT key, value FROM crm_setting").fetchall()
            con.close()
        except sqlite3.Error as exc:
            raise CommandError(f"cannot read crm_setting from {p}: {exc}")
        imported, skipped = [], 0
        for key, value in rows:
            if key in SKIP_IMPORT_KEYS or not str(value or "").strip():
                skipped += 1
                continue
            Setting.objects.update_or_create(key=key, defaults={"value": value})
            imported.append(key)
        self.stdout.write(
            f"settings restored from {p.name}: {', '.join(sorted(imported)) or 'none'} "
            f"({skipped} transient/empty keys skipped)")

    def _import_accounts(self, path):
        """Restore provider accounts (crm_channelaccount) from the backup:
        identity + credentials + config. Health state is NOT copied — restored
        accounts start 'unknown' and the periodic checker re-probes them, so a
        stale 'disconnected' verdict from the backup can't suppress routing.
        Upsert key: (channel, external_id), falling back to (channel, name)
        for rows without an external id (e.g. Baileys)."""
        import sqlite3

        p = Path(path)
        cols = ("channel", "provider", "name", "external_id", "waba_id",
                "access_token", "app_secret", "api_version", "owner", "config")
        try:
            con = sqlite3.connect(f"file:{p.as_posix()}?mode=ro", uri=True)
            rows = con.execute(
                f"SELECT {', '.join(cols)} FROM crm_channelaccount").fetchall()
            con.close()
        except sqlite3.Error as exc:
            raise CommandError(f"cannot read crm_channelaccount from {p}: {exc}")
        restored = []
        for row in rows:
            data = dict(zip(cols, row))
            try:
                data["config"] = json.loads(data["config"]) if data["config"] else {}
            except (TypeError, ValueError):
                data["config"] = {}
            for k in cols:
                if data[k] is None:
                    data[k] = "" if k != "config" else {}
            lookup = {"channel": data["channel"]}
            if data["external_id"]:
                lookup["external_id"] = data["external_id"]
            else:
                lookup["name"] = data["name"]
            ChannelAccount.objects.update_or_create(
                **lookup, defaults={k: data[k] for k in cols if k not in lookup})
            restored.append(f"[{data['channel']}] {data['name']}")
        self.stdout.write(
            f"channel accounts restored: {', '.join(restored) or 'none'}")

    # ------------------------------------------------------------------ purge
    def _purge_demo(self):
        """Remove seed_demo fixtures. Deliberately conservative: demo users go
        only while still on their seeded password (a repurposed account with a
        changed password is warned about, not deleted); the acme tenant goes
        only if it holds nothing beyond its seeded test contact."""
        from ingest.models import Tenant

        demo_creds = list(DEMO_USERS) + [("acmeadmin", "acme123", "admin", "Acme Admin")]
        for username, password, _role, _display in demo_creds:
            user = User.objects.filter(username=username).first()
            if not user:
                continue
            if user.check_password(password):
                user.delete()
                self.stdout.write(f"purged demo user '{username}'")
            else:
                self.stdout.write(self.style.WARNING(
                    f"demo user '{username}' has a changed password — kept "
                    "(delete manually if it isn't a real account)"))

        demo_phones = {r["contact"]["phone"] for r in DEMO_CONTACTS} | {"+60 19-000 0001"}
        gone, _ = Contact.objects.filter(phone__in=demo_phones).delete()
        if gone:
            self.stdout.write(f"purged demo contacts ({gone} rows incl. related)")

        acme = Tenant.objects.filter(slug="acme").first()
        if acme:
            extra = acme.contacts.exclude(phone__in=demo_phones).count()
            if extra:
                self.stdout.write(self.style.WARNING(
                    f"tenant 'acme' holds {extra} non-demo contact(s) — kept"))
            else:
                acme.delete()
                self.stdout.write("purged demo tenant 'acme'")

    # -------------------------------------------------------------- contacts
    def _import_contacts(self, path, tenant, entity):
        p = Path(path)
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise CommandError(f"cannot read contact book {p}: {exc}")
        rows = data.get("contacts") if isinstance(data, dict) else data
        if not isinstance(rows, list):
            raise CommandError(f"{p}: expected a JSON list of contacts "
                               "(or {'contacts': [...]})")

        def _amount(value):
            if value in (None, ""):
                return None
            try:
                return Decimal(str(value))
            except InvalidOperation:
                return None

        created = updated = skipped = records = 0
        for c in rows:
            if not isinstance(c, dict):
                skipped += 1
                continue
            name = str(c.get("name") or "").strip()
            phone = str(c.get("phone") or "").strip()
            if not name or not phone:
                skipped += 1
                continue
            tags = c.get("tags")
            attributes = c.get("attributes")
            defaults = {
                "name": name,
                "email": str(c.get("email") or "").strip(),
                "language": c.get("language") if c.get("language") in VALID_LANGUAGE else "en",
                "status": c.get("status") if c.get("status") in VALID_STATUS else "pending",
                "method": c.get("method") if c.get("method") in VALID_METHOD else "ai-call",
                "company_name": str(c.get("company") or entity.name),
                "external_id": str(c.get("external_id") or "")[:64],
                "tags": list(tags) if isinstance(tags, list) else [],
                "attributes": dict(attributes) if isinstance(attributes, dict) else {},
                "notes": str(c.get("notes") or ""),
            }
            cust, was_created = Contact.objects.update_or_create(
                tenant=tenant, phone=phone, defaults=defaults)
            ContactEntity.objects.get_or_create(contact=cust, entity=entity)
            created += was_created
            updated += not was_created

            r = c.get("record")
            if isinstance(r, dict) and str(r.get("external_id") or "").strip():
                rec_type = str(r.get("type") or "custom")
                if rec_type not in {t for t, _ in Record.TYPE_CHOICES}:
                    rec_type = "custom"
                Record.objects.update_or_create(
                    tenant=tenant, type=rec_type,
                    external_id=str(r["external_id"]).strip()[:64],
                    defaults={
                        "contact": cust,
                        "title": str(r.get("title") or "")[:190],
                        "status": str(r.get("status") or "open"),
                        "amount": _amount(r.get("amount")),
                        "currency": str(r.get("currency") or "MYR")[:8],
                        "data": r.get("data") if isinstance(r.get("data"), dict) else {},
                    },
                )
                records += 1
        self.stdout.write(f"contacts: {created} created, {updated} updated, "
                          f"{records} record(s) upserted, {skipped} skipped "
                          "(missing name/phone)")
        if skipped:
            self.stdout.write(self.style.WARNING(
                "  skipped rows need at least name and phone (E.164)"))

    # ------------------------------------------------------------- readiness
    def _readiness_report(self, tenant):
        """Warn about .env gaps each subsystem needs in production. Report
        only — the seed itself never fails on these."""
        from django.conf import settings as dj

        warns, infos = [], []
        for username, default_pw in DEFAULT_PASSWORDS.items():
            user = User.objects.filter(username=username).first()
            if user and user.check_password(default_pw):
                warns.append(f"user '{username}' is on the seeded default password "
                             "— change it before going live")
        if dj.DEBUG:
            warns.append("DEBUG=True — set DEBUG=False in backend/.env")
        if "django-insecure" in dj.SECRET_KEY:
            warns.append("SECRET_KEY is the insecure default — set a real one")
        if not [h for h in dj.ALLOWED_HOSTS if h not in ("localhost", "127.0.0.1")]:
            warns.append("ALLOWED_HOSTS only allows localhost — add the real host")
        if not (os.getenv("OPENAI_API_KEY") or os.getenv("OPENROUTER_API_KEY")):
            warns.append("no OPENAI_API_KEY / OPENROUTER_API_KEY — AI replies and "
                         "the voice agent cannot run")
        missing_lk = [k for k in ("LIVEKIT_URL", "LIVEKIT_API_KEY", "LIVEKIT_API_SECRET")
                      if not os.getenv(k)]
        if missing_lk:
            warns.append(f"voice agent: missing {', '.join(missing_lk)}")
        if os.getenv("BAILEYS_SECRET", "baileys-local-dev") == "baileys-local-dev":
            warns.append("BAILEYS_SECRET is the dev default — set a real shared "
                         "secret (and note Baileys is dev/demo only — ToS risk)")

        if not os.getenv("SIP_OUTBOUND_TRUNK_ID"):
            infos.append("SIP_OUTBOUND_TRUNK_ID unset — phone dialing disabled "
                         "(run voice_agent/setup_trunk.py once)")
        if not os.getenv("CALLER_ID"):
            infos.append("CALLER_ID unset — SIP calls need an E.164 caller id")
        from whatsapp import health as wa_health
        twilio = wa_health.voice_settings()  # dashboard row or .env
        if not (twilio["sip_username"] and twilio["sip_password"]):
            infos.append("Twilio SIP creds unset — voice trunk can't dial "
                         "(Settings → Channels → Voice, or "
                         "TWILIO_SIP_USERNAME/TWILIO_SIP_PASSWORD in .env)")
        if not ChannelAccount.objects.exists() and not os.getenv("WHATSAPP_ACCESS_TOKEN"):
            infos.append("no channel accounts yet — add WhatsApp/email/social "
                         "accounts in Settings → Channels (sandbox mode until then)")
        if not tenant.data_sources.exists():
            infos.append("no data sources — contacts arrive via --contacts "
                         "import or the /api/v1 sync once a DataSource is set up")
        leftovers = [t for t in PURGE_FILE_TARGETS if (DATA_DIR / t).exists()]
        if leftovers:
            infos.append("old call/chat history files on disk "
                         f"({', '.join(leftovers)}) — old calls will still show "
                         "on /chats; re-run with --purge-files to archive them")

        self.stdout.write("\nproduction readiness:")
        for w in warns:
            self.stdout.write(self.style.WARNING(f"  ! {w}"))
        for i in infos:
            self.stdout.write(f"  - {i}")
        if not warns:
            self.stdout.write(self.style.SUCCESS("  no blocking gaps detected"))
