"""WhatsApp production-parity feature tests.

Covers: template listing + sending (sandbox demo set and rendering), Meta's
24h contact-service window (open after inbound, closed when stale, 409 on
free-text), opt-out keyword handling (STOP/BERHENTI + force override),
quiet-hours gating, message pagination (limit/before/has_more), media
(serve 404, sandbox send-media, inbound download attach), and mark_read on
live inbound.

Same fixture pattern as tests/test_whatsapp.py.
"""
import hashlib
import hmac
import json
from datetime import datetime, timedelta

import pytest
from django.contrib.auth.models import User
from django.core.files.uploadedfile import SimpleUploadedFile
from django.utils import timezone

from crm.models import (ChannelAccount, Contact as CrmContact, Message,
                        Profile, Record)
from whatsapp import client as wa_client
from whatsapp import store as wa_store
from whatsapp import views as wa_views
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
WA_ID = "60123456789"


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def no_env_account(monkeypatch):
    """Keep tests deterministic: a developer's real WHATSAPP_* .env keys must
    not turn sandbox tests into live ones."""
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def no_async_ai(monkeypatch):
    """Webhook tests must not spawn AI-reply/flow threads (network + test-DB
    access from another thread)."""
    monkeypatch.setattr(wa_views, "_ai_reply_async", lambda wa_id: None)
    monkeypatch.setattr(wa_views.flows_engine, "on_inbound_async",
                        lambda channel, key, text: None)


@pytest.fixture
def no_side_effects(monkeypatch):
    """Capture (instead of spawning) the inbound side-effects worker."""
    calls = []
    monkeypatch.setattr(
        wa_views, "_inbound_side_effects_async",
        lambda account, wa_id, wamid, media=None: calls.append(
            {"account": account, "wa_id": wa_id, "wamid": wamid, "media": media}),
    )
    return calls


@pytest.fixture
def tmp_media_dir(monkeypatch, tmp_path):
    """Keep test attachments out of the real backend/data tree."""
    monkeypatch.setattr(wa_store, "MEDIA_DIR", tmp_path)
    return tmp_path


@pytest.fixture
def no_quiet_hours(monkeypatch):
    """A developer's local clock must not flip quiet-hours tests."""
    monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)


def _mk_thread(client, phone=f"+{WA_ID}", name="Tester"):
    r = client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": phone, "name": name}),
                    content_type="application/json", **AUTH)
    assert r.status_code == 200
    return r.json()


def _live_account(pnid="1234567890", secret="s3cret", waba=""):
    return ChannelAccount.objects.create(
        channel="whatsapp", name="Live", external_id=pnid,
        access_token="tok", app_secret=secret, waba_id=waba,
    )


# ---- templates -----------------------------------------------------------------

@pytest.mark.django_db
class TestTemplates:
    def test_list_requires_auth(self, client, superadmin, no_env_account):
        assert client.get("/api/whatsapp/templates").status_code == 401

    def test_sandbox_list(self, client, superadmin, no_env_account):
        r = client.get("/api/whatsapp/templates", **AUTH)
        assert r.status_code == 200
        data = r.json()
        assert data["sandbox"] is True
        names = {t["name"] for t in data["templates"]}
        assert names == {"order_confirmation", "delivery_update", "appointment_reminder"}
        reminder = next(t for t in data["templates"] if t["name"] == "order_confirmation")
        assert reminder["variables"] == 3
        assert "{{1}}" in reminder["body"]
        assert all(t["status"] == "APPROVED" and t["language"] for t in data["templates"])

    def test_sandbox_send_renders_variables(self, client, superadmin, no_env_account,
                                            no_quiet_hours):
        _mk_thread(client)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation", "language": "en",
                                         "variables": ["Ahmad", "RM 500", "20 July"]}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        msg = r.json()["message"]
        assert "Ahmad" in msg["text"] and "RM 500" in msg["text"] and "20 July" in msg["text"]
        assert "{{" not in msg["text"]
        assert msg["status"] == "sandbox"

    def test_sandbox_unknown_template_400(self, client, superadmin, no_env_account,
                                          no_quiet_hours):
        _mk_thread(client)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "nope", "variables": []}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_live_send_uses_client_and_stores_wamid(self, client, superadmin,
                                                    no_env_account, no_quiet_hours,
                                                    monkeypatch):
        account = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(account.pk))
        sent = {}

        def fake_send_template(acct, to, name, language, params):
            sent.update(acct=acct["id"], to=to, name=name, params=params)
            return "wamid.tpl.1"

        monkeypatch.setattr(wa_client, "send_template", fake_send_template)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation", "language": "en",
                                         "variables": ["A", "B", "C"]}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert sent == {"acct": str(account.pk), "to": WA_ID,
                        "name": "order_confirmation", "params": ["A", "B", "C"]}
        assert r.json()["message"]["id"] == "wamid.tpl.1"


# ---- template management (create / delete from the dashboard) ---------------------

AGENT_TOKEN = "test-agent-token"
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="agent", password="collect123")
    Profile.objects.create(user=user, role="agent", token=AGENT_TOKEN)
    return user


@pytest.mark.django_db
class TestTemplateManagement:
    CREATE = {"name": "book_now", "language": "en", "category": "UTILITY",
              "body": "Hi {{1}}, please pay {{2}} today."}

    def _post(self, client, payload, auth=AUTH):
        return client.post("/api/whatsapp/templates", data=json.dumps(payload),
                           content_type="application/json", **auth)

    def test_create_requires_admin(self, client, superadmin, agent_user, no_env_account):
        assert self._post(client, self.CREATE, AGENT_AUTH).status_code == 403
        assert client.delete("/api/whatsapp/templates/book_now",
                             **AGENT_AUTH).status_code == 403

    def test_sandbox_create_lists_and_sends(self, client, superadmin, no_env_account,
                                            no_quiet_hours):
        r = self._post(client, self.CREATE)
        assert r.status_code == 200
        data = r.json()
        assert data["sandbox"] is True
        assert data["template"]["status"] == "APPROVED"
        assert data["template"]["variables"] == 2

        listed = client.get("/api/whatsapp/templates", **AUTH).json()["templates"]
        mine = next(t for t in listed if t["name"] == "book_now")
        assert mine["custom"] is True
        assert all(t["custom"] is False for t in listed if t["name"] != "book_now")

        _mk_thread(client)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "book_now", "language": "en",
                                         "variables": ["Ahmad", "RM 500"]}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert "Ahmad" in r.json()["message"]["text"]
        assert "RM 500" in r.json()["message"]["text"]

    def test_create_validation(self, client, superadmin, no_env_account):
        assert self._post(client, dict(self.CREATE, name="Bad Name!")).status_code == 400
        assert self._post(client, dict(self.CREATE, category="SPAM")).status_code == 400
        assert self._post(client, dict(self.CREATE, body="")).status_code == 400
        assert self._post(client, dict(self.CREATE,
                                       name="order_confirmation")).status_code == 400  # built-in
        assert self._post(client, self.CREATE).status_code == 200
        assert self._post(client, self.CREATE).status_code == 400  # duplicate custom

    def test_sandbox_delete(self, client, superadmin, no_env_account):
        self._post(client, self.CREATE)
        assert client.delete("/api/whatsapp/templates/book_now", **AUTH).status_code == 200
        names = {t["name"] for t in
                 client.get("/api/whatsapp/templates", **AUTH).json()["templates"]}
        assert "book_now" not in names
        assert client.delete("/api/whatsapp/templates/order_confirmation",
                             **AUTH).status_code == 400  # built-in
        assert client.delete("/api/whatsapp/templates/nope", **AUTH).status_code == 404

    def test_live_create_calls_client(self, client, superadmin, no_env_account,
                                      monkeypatch):
        _live_account(waba="wb-1")
        created = {}

        def fake_create(acct, name, language, category, body, example=None, buttons=None):
            created.update(waba=acct["waba_id"], name=name, language=language,
                           category=category, body=body, example=example, buttons=buttons)
            return {"id": "t1", "status": "PENDING", "category": category}

        monkeypatch.setattr(wa_client, "create_template", fake_create)
        r = self._post(client, dict(self.CREATE, example=["Ahmad", "RM 300"]))
        assert r.status_code == 200
        data = r.json()
        assert data["sandbox"] is False
        assert data["template"]["status"] == "PENDING"
        assert created == {"waba": "wb-1", "name": "book_now", "language": "en",
                           "category": "UTILITY", "body": self.CREATE["body"],
                           "example": ["Ahmad", "RM 300"], "buttons": []}

    def test_live_delete_calls_client(self, client, superadmin, no_env_account,
                                      monkeypatch):
        _live_account(waba="wb-1")
        deleted = {}
        monkeypatch.setattr(wa_client, "delete_template",
                            lambda acct, name: deleted.update(name=name))
        assert client.delete("/api/whatsapp/templates/book_now", **AUTH).status_code == 200
        assert deleted == {"name": "book_now"}

    # ---- variable bindings (auto-fill from the CRM) -------------------------------

    def test_bindings_autofill_on_send(self, client, superadmin, no_env_account,
                                       no_quiet_hours):
        c = CrmContact.objects.create(name="Ahmad", phone=f"+{WA_ID}")
        Record.objects.create(contact=c, type="order", external_id="ORD-9",
                              status="open", amount=500, currency="RM")
        assert self._post(client, dict(self.CREATE,
                                       bindings=["name", "amount"])).status_code == 200
        _mk_thread(client)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "book_now", "language": "en"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        text = r.json()["message"]["text"]
        assert "Ahmad" in text and "RM 500.00" in text and "{{" not in text

    def test_templates_list_resolves_for_thread(self, client, superadmin, no_env_account):
        c = CrmContact.objects.create(name="Siti", phone=f"+{WA_ID}")
        Record.objects.create(contact=c, type="order", external_id="ORD-8",
                              status="open", amount=250, currency="RM")
        _mk_thread(client)
        self._post(client, dict(self.CREATE, bindings=["name", "amount"]))
        r = client.get(f"/api/whatsapp/templates?wa_id={WA_ID}", **AUTH)
        row = next(t for t in r.json()["templates"] if t["name"] == "book_now")
        assert row["bindings"] == ["name", "amount"]
        assert row["resolved"] == ["Siti", "RM 250.00"]

    def test_unknown_binding_key_400(self, client, superadmin, no_env_account):
        assert self._post(client, dict(self.CREATE,
                                       bindings=["name", "nope"])).status_code == 400

    # ---- editing + buttons ----------------------------------------------------------

    def test_patch_updates_content_and_bindings(self, client, superadmin, no_env_account):
        self._post(client, self.CREATE)
        r = client.patch("/api/whatsapp/templates/book_now",
                         data=json.dumps({
                             "body": "Hello {{1}}, {{2}} days overdue.",
                             "category": "UTILITY",
                             "buttons": [{"type": "QUICK_REPLY", "text": "Pay now"}],
                             "bindings": ["name", "days_overdue"],
                         }),
                         content_type="application/json", **AUTH)
        assert r.status_code == 200
        row = next(t for t in
                   client.get("/api/whatsapp/templates", **AUTH).json()["templates"]
                   if t["name"] == "book_now")
        assert row["body"] == "Hello {{1}}, {{2}} days overdue."
        assert row["bindings"] == ["name", "days_overdue"]
        assert row["buttons"] == [{"type": "QUICK_REPLY", "text": "Pay now"}]

    def test_patch_builtin_400_missing_404(self, client, superadmin, no_env_account):
        r = client.patch("/api/whatsapp/templates/order_confirmation",
                         data=json.dumps({"body": "x"}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 400
        r = client.patch("/api/whatsapp/templates/nope",
                         data=json.dumps({"body": "x"}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 404

    def test_button_validation(self, client, superadmin, no_env_account):
        for bad in ([{"type": "URL", "text": "Pay", "url": "ftp://x"}],
                    [{"type": "WEIRD", "text": "Pay"}],
                    [{"type": "QUICK_REPLY", "text": "x" * 26}]):
            assert self._post(client, dict(self.CREATE, buttons=bad)).status_code == 400
        ok = [{"type": "URL", "text": "Pay now", "url": "https://pay.example.com"},
              {"type": "QUICK_REPLY", "text": "Call me back"}]
        r = self._post(client, dict(self.CREATE, buttons=ok))
        assert r.status_code == 200
        assert r.json()["template"]["buttons"] == ok
        row = next(t for t in
                   client.get("/api/whatsapp/templates", **AUTH).json()["templates"]
                   if t["name"] == "book_now")
        assert row["buttons"] == ok

    def test_live_edit_calls_update(self, client, superadmin, no_env_account, monkeypatch):
        _live_account(waba="wb-1")
        called = {}

        def fake_update(acct, template_id, category, body, example=None, buttons=None):
            called.update(id=template_id, category=category, body=body, buttons=buttons)
            return {"success": True}

        monkeypatch.setattr(wa_client, "update_template", fake_update)
        r = client.patch("/api/whatsapp/templates/book_now",
                         data=json.dumps({"id": "t9", "body": "New {{1}}",
                                          "category": "MARKETING", "buttons": []}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert called == {"id": "t9", "category": "MARKETING", "body": "New {{1}}",
                          "buttons": []}
        # editing a live template without its Meta id is a 400, not a Meta call
        r = client.patch("/api/whatsapp/templates/book_now",
                         data=json.dumps({"body": "New {{1}}"}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 400


# ---- 24h contact-service window --------------------------------------------------

@pytest.mark.django_db
class TestWindow:
    def test_sandbox_thread_always_open(self, client, superadmin, no_env_account):
        _mk_thread(client)
        r = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH)
        body = r.json()
        assert body["window_open"] is True
        assert body["window_expires_at"] is None

    def test_live_open_after_inbound(self, client, superadmin, no_env_account):
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["window_open"] is True
        assert body["window_expires_at"] is not None

    def test_live_closed_when_stale_and_send_409(self, client, superadmin, no_env_account,
                                                 no_quiet_hours):
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        Message.objects.update(created_at=timezone.now() - timedelta(hours=25))
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["window_open"] is False
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "free text"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        assert "24h window" in r.json()["detail"]

    def test_live_no_inbound_is_closed(self, client, superadmin, no_env_account):
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["window_open"] is False

    def test_live_send_inside_window_ok(self, client, superadmin, no_env_account,
                                        no_quiet_hours, monkeypatch):
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "wamid.txt.1")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "still inside"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["message"]["id"] == "wamid.txt.1"

    def test_provider_ts_backdate_closes_window(self, client, superadmin, no_env_account):
        # a webhook that arrives NOW for a message the contact sent 30h ago
        # must not open the window (window math uses Meta's clock, not arrival)
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        sent_30h_ago = (timezone.now() - timedelta(hours=30)).timestamp()
        wa_store.append_message(WA_ID, "in", "old reply", source="customer",
                                provider_ts=str(int(sent_30h_ago)))
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["window_open"] is False
        # a future/garbage provider_ts is ignored (arrival time kept)
        wa_store.append_message(WA_ID, "in", "fresh", source="customer",
                                provider_ts=str(int(timezone.now().timestamp() + 9999)))
        wa_store.append_message(WA_ID, "in", "junk ts", source="customer",
                                provider_ts="not-a-number")
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["window_open"] is True

    def test_close_window_forces_template_mode(self, client, superadmin, no_env_account):
        # Meta 131047 = authoritative "window closed": close_window caps the
        # stale-but-too-new inbound rows so the UI flips to template mode
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        assert client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()["window_open"] is True
        wa_store.close_window(WA_ID)
        assert client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()["window_open"] is False


# ---- opt-out (STOP / BERHENTI) -----------------------------------------------------

@pytest.mark.django_db
class TestOptOut:
    def _simulate(self, client, text):
        return client.post(f"/api/whatsapp/threads/{WA_ID}/simulate",
                           data=json.dumps({"text": text}),
                           content_type="application/json", **AUTH)

    def test_stop_keyword_flips_flags(self, client, superadmin, no_env_account):
        _mk_thread(client)
        r = self._simulate(client, "Please STOP contacting me")
        assert r.status_code == 200
        assert r.json().get("opted_out") is True
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert body["opted_out"] is True
        assert body["ai_enabled"] is False
        assert any(m["source"] == "system" and "opted out" in m["text"]
                   for m in body["messages"])

    def test_malay_phrase_triggers(self, client, superadmin, no_env_account):
        _mk_thread(client)
        self._simulate(client, "tolong jangan hubungi saya lagi")
        assert client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()["opted_out"] is True

    def test_word_boundary_no_false_positive(self, client, superadmin, no_env_account):
        _mk_thread(client)
        self._simulate(client, "my progress is unstoppable")
        assert client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()["opted_out"] is False

    def test_blocks_template_and_freetext_but_force_overrides(self, client, superadmin,
                                                              no_env_account, no_quiet_hours):
        _mk_thread(client)
        self._simulate(client, "STOP")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation", "variables": []}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "hi"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "manual override", "force": True}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["message"]["text"] == "manual override"


# ---- quiet hours -------------------------------------------------------------------

@pytest.mark.django_db
class TestQuietHours:
    def _enable_now(self):
        now = datetime.now()
        start = (now - timedelta(hours=1)).strftime("%H:%M")
        end = (now + timedelta(hours=1)).strftime("%H:%M")
        wa_config.save_global_settings({"quiet_start": start, "quiet_end": end})

    def test_template_send_409_during_quiet_hours(self, client, superadmin, no_env_account):
        _mk_thread(client)
        self._enable_now()
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation", "variables": []}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        assert "Quiet hours" in r.json()["detail"]

    def test_manual_send_still_allowed(self, client, superadmin, no_env_account):
        _mk_thread(client)
        self._enable_now()
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "manual is fine"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200

    def test_ai_reply_skipped(self, superadmin, no_env_account, monkeypatch):
        self._enable_now()
        monkeypatch.setattr(wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(
            wa_views.ai, "generate_reply",
            lambda thread: pytest.fail("AI must not be called during quiet hours"))
        # contact_id set: the coverage gate must not be what skips the reply
        thread = {"wa_id": WA_ID, "ai_enabled": True, "opted_out": False,
                  "contact_id": 1, "messages": []}
        assert wa_views._maybe_ai_reply(thread) is None

    def test_overnight_window_and_parse(self, db, no_env_account):
        wa_config.save_global_settings({"quiet_start": "21:00", "quiet_end": "08:00"})
        assert wa_config.in_quiet_hours(now=datetime(2026, 1, 1, 23, 30)) is True
        assert wa_config.in_quiet_hours(now=datetime(2026, 1, 2, 7, 59)) is True
        assert wa_config.in_quiet_hours(now=datetime(2026, 1, 1, 12, 0)) is False
        wa_config.save_global_settings({"quiet_start": "", "quiet_end": ""})
        assert wa_config.in_quiet_hours(now=datetime(2026, 1, 1, 23, 30)) is False
        assert wa_config.parse_hhmm("21:30") == 21 * 60 + 30
        assert wa_config.parse_hhmm("25:00") is None
        assert wa_config.parse_hhmm("nope") is None


# ---- AI coverage gate (only OUR contacts get auto-replies) ---------------------------

@pytest.mark.django_db
class TestAiCoverageGate:
    def test_unlinked_contact_gets_no_ai_reply(self, no_env_account,
                                               no_quiet_hours, monkeypatch):
        monkeypatch.setattr(wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(
            wa_views.ai, "generate_reply",
            lambda thread: pytest.fail("AI must not answer unknown contacts"))
        wa_store.get_or_create_thread(WA_ID)  # matches NO CRM contact
        wa_store.update_thread(WA_ID, {"ai_enabled": True})
        thread = wa_store.load_thread(WA_ID)
        assert thread["contact_id"] is None
        assert wa_views._maybe_ai_reply(thread) is None

    def test_linked_contact_still_answered(self, no_env_account,
                                          no_quiet_hours, monkeypatch):
        monkeypatch.setattr(wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(wa_views.ai, "generate_reply", lambda thread: "hello!")
        CrmContact.objects.create(name="Linked", phone=f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID)  # creation links by phone match
        wa_store.update_thread(WA_ID, {"ai_enabled": True})
        msg = wa_views._maybe_ai_reply(wa_store.load_thread(WA_ID))
        assert msg is not None and msg["text"] == "hello!"

    def test_settings_roundtrip_and_validation(self, client, superadmin, no_env_account):
        r = client.get("/api/whatsapp/settings", **AUTH)
        assert "quiet_start" in r.json() and "quiet_end" in r.json()
        r = client.post("/api/whatsapp/settings",
                        data=json.dumps({"quiet_start": "22:00", "quiet_end": "08:00"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["quiet_start"] == "22:00"
        r = client.post("/api/whatsapp/settings",
                        data=json.dumps({"quiet_start": "26:99"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400


# ---- pagination --------------------------------------------------------------------

@pytest.mark.django_db
class TestPagination:
    def test_limit_before_has_more(self, client, superadmin, no_env_account):
        _mk_thread(client)
        for i in range(5):
            wa_store.append_message(WA_ID, "in", f"msg {i}", source="customer")
        body = client.get(f"/api/whatsapp/threads/{WA_ID}?limit=2", **AUTH).json()
        assert [m["text"] for m in body["messages"]] == ["msg 3", "msg 4"]
        assert body["has_more"] is True

        cursor = body["messages"][0]["pk"]  # oldest visible message
        body = client.get(f"/api/whatsapp/threads/{WA_ID}?limit=2&before={cursor}",
                          **AUTH).json()
        assert [m["text"] for m in body["messages"]] == ["msg 1", "msg 2"]
        assert body["has_more"] is True

        cursor = body["messages"][0]["id"]  # "db-<pk>" form works too
        body = client.get(f"/api/whatsapp/threads/{WA_ID}?limit=2&before={cursor}",
                          **AUTH).json()
        assert [m["text"] for m in body["messages"]] == ["msg 0"]
        assert body["has_more"] is False

    def test_default_limit_50(self, client, superadmin, no_env_account):
        _mk_thread(client)
        for i in range(55):
            wa_store.append_message(WA_ID, "in", f"m{i}", source="customer")
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        assert len(body["messages"]) == 50
        assert body["has_more"] is True
        assert body["messages"][-1]["text"] == "m54"

    def test_thread_list_limit_and_summaries(self, client, superadmin, no_env_account):
        for n in range(3):
            wa_store.get_or_create_thread(f"6011111111{n}")
            wa_store.append_message(f"6011111111{n}", "in", f"hi {n}", source="customer")
        body = client.get("/api/whatsapp/threads?limit=2", **AUTH).json()
        assert len(body["threads"]) == 2
        t = body["threads"][0]
        assert t["last_message"] == "hi 2" and t["message_count"] == 1
        assert {"window_open", "window_expires_at", "opted_out"} <= set(t)


# ---- media ---------------------------------------------------------------------------

@pytest.mark.django_db
class TestMedia:
    def test_serve_missing_404(self, client, superadmin, no_env_account, tmp_media_dir):
        assert client.get("/api/whatsapp/media/999999", **AUTH).status_code == 404

    def test_serve_requires_auth(self, client, superadmin, no_env_account, tmp_media_dir):
        assert client.get("/api/whatsapp/media/1").status_code == 401

    def test_sandbox_send_media_and_serve(self, client, superadmin, no_env_account,
                                          no_quiet_hours, tmp_media_dir):
        _mk_thread(client)
        upload = SimpleUploadedFile("photo.jpg", b"jpeg-bytes", content_type="image/jpeg")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-media",
                        {"file": upload, "caption": "invoice attached"}, **AUTH)
        assert r.status_code == 200
        msg = r.json()["message"]
        assert msg["status"] == "sandbox"
        assert msg["text"] == "invoice attached"
        assert msg["media"]["type"] == "image"
        assert msg["media"]["url"] == f"/api/whatsapp/media/{msg['pk']}"
        served = client.get(msg["media"]["url"], **AUTH)
        assert served.status_code == 200
        assert b"".join(served.streaming_content) == b"jpeg-bytes"
        assert served["Content-Type"] == "image/jpeg"

    def test_extension_denylist(self, client, superadmin, no_env_account,
                                no_quiet_hours, tmp_media_dir):
        _mk_thread(client)
        upload = SimpleUploadedFile("evil.exe", b"MZ", content_type="application/x-msdownload")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-media", {"file": upload}, **AUTH)
        assert r.status_code == 400

    def test_size_limit(self, client, superadmin, no_env_account,
                        no_quiet_hours, tmp_media_dir):
        _mk_thread(client)
        upload = SimpleUploadedFile("big.jpg", b"x" * (16 * 1024 * 1024 + 1),
                                    content_type="image/jpeg")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-media", {"file": upload}, **AUTH)
        assert r.status_code == 400

    def test_inbound_media_download_attaches(self, client, superadmin, no_env_account,
                                             tmp_media_dir, monkeypatch):
        a = _live_account()
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "[image]", source="customer",
                                msg_id="wamid.media.1")

        def fake_download(account, media_id, dest_dir):
            from pathlib import Path
            dest = Path(dest_dir)
            dest.mkdir(parents=True, exist_ok=True)
            path = dest / f"{media_id}.jpg"
            path.write_bytes(b"downloaded")
            return path

        monkeypatch.setattr(wa_client, "download_media", fake_download)
        monkeypatch.setattr(wa_client, "mark_read", lambda acct, mid: None)
        wa_views._inbound_side_effects(
            wa_config.get_account(str(a.pk)), WA_ID, "wamid.media.1",
            {"id": "MEDIA123", "type": "image"})
        body = client.get(f"/api/whatsapp/threads/{WA_ID}", **AUTH).json()
        msg = next(m for m in body["messages"] if m["id"] == "wamid.media.1")
        assert msg["media"]["type"] == "image"
        served = client.get(msg["media"]["url"], **AUTH)
        assert served.status_code == 200
        assert b"".join(served.streaming_content) == b"downloaded"


# ---- mark_read + webhook wiring ------------------------------------------------------

@pytest.mark.django_db
class TestInboundSideEffects:
    PNID = "1234567890"
    SECRET = "s3cret-app-secret"

    def _signed_post(self, client, payload):
        body = json.dumps(payload).encode()
        sig = "sha256=" + hmac.new(self.SECRET.encode(), body, hashlib.sha256).hexdigest()
        return client.post("/api/whatsapp/webhook", data=body,
                           content_type="application/json",
                           HTTP_X_HUB_SIGNATURE_256=sig)

    def _payload(self, msg):
        return {"entry": [{"changes": [{"field": "messages", "value": {
            "metadata": {"phone_number_id": self.PNID},
            "contacts": [{"profile": {"name": "Tester"}, "wa_id": WA_ID}],
            "messages": [msg],
        }}]}]}

    def test_mark_read_called(self, no_env_account, monkeypatch, db):
        _live_account(pnid=self.PNID, secret=self.SECRET)
        seen = []
        monkeypatch.setattr(wa_client, "mark_read",
                            lambda acct, mid: seen.append((acct["id"], mid)))
        account = wa_config.get_account(None)
        wa_views._inbound_side_effects(account, WA_ID, "wamid.read.1", None)
        assert seen == [(account["id"], "wamid.read.1")]

    def test_mark_read_failure_swallowed(self, no_env_account, monkeypatch, db):
        _live_account(pnid=self.PNID, secret=self.SECRET)

        def boom(acct, mid):
            raise RuntimeError("network down")

        monkeypatch.setattr(wa_client, "mark_read", boom)
        wa_views._inbound_side_effects(wa_config.get_account(None), WA_ID, "wamid.x", None)

    def test_webhook_schedules_side_effects_for_live_inbound(self, client, no_env_account,
                                                             no_async_ai, no_side_effects):
        _live_account(pnid=self.PNID, secret=self.SECRET)
        r = self._signed_post(client, self._payload(
            {"from": WA_ID, "id": "wamid.live.1", "type": "text", "text": {"body": "hello"}}))
        assert r.status_code == 200
        assert len(no_side_effects) == 1
        assert no_side_effects[0]["wamid"] == "wamid.live.1"
        assert no_side_effects[0]["media"] is None

    def test_webhook_media_message_stores_caption_and_media_ref(self, client, no_env_account,
                                                                no_async_ai, no_side_effects):
        _live_account(pnid=self.PNID, secret=self.SECRET)
        r = self._signed_post(client, self._payload(
            {"from": WA_ID, "id": "wamid.img.1", "type": "image",
             "image": {"id": "MEDIA9", "mime_type": "image/jpeg", "caption": "my receipt"}}))
        assert r.status_code == 200
        m = Message.objects.get(external_id="wamid.img.1")
        assert m.text == "my receipt"
        assert no_side_effects[0]["media"] == {"id": "MEDIA9", "type": "image"}

    def test_sandbox_webhook_skips_side_effects(self, client, no_env_account,
                                                no_async_ai, no_side_effects):
        payload = {"entry": [{"changes": [{"field": "messages", "value": {
            "contacts": [{"profile": {"name": "T"}, "wa_id": WA_ID}],
            "messages": [{"from": WA_ID, "id": "wamid.sbx.1", "type": "text",
                          "text": {"body": "hi"}}],
        }}]}]}
        r = client.post("/api/whatsapp/webhook", data=json.dumps(payload),
                        content_type="application/json")
        assert r.status_code == 200
        assert no_side_effects == []

    def test_status_error_marks_failed_and_notes(self, client, no_env_account,
                                                 no_async_ai, no_side_effects):
        _live_account(pnid=self.PNID, secret=self.SECRET)
        wa_store.get_or_create_thread(WA_ID)
        wa_store.append_message(WA_ID, "out", "outside window", source="agent",
                                msg_id="wamid.out.1")
        payload = {"entry": [{"changes": [{"field": "messages", "value": {
            "metadata": {"phone_number_id": self.PNID},
            "statuses": [{"id": "wamid.out.1", "recipient_id": WA_ID, "status": "failed",
                          "errors": [{"code": 131047, "title": "Re-engagement message",
                                      "message": "24h window closed"}]}],
        }}]}]}
        assert self._signed_post(client, payload).status_code == 200
        assert Message.objects.get(external_id="wamid.out.1").status == "failed"
        note = Message.objects.filter(source="system").last()
        assert note is not None
        assert "Re-engagement message" in note.text


# ---- SSE change stream ---------------------------------------------------------------

@pytest.mark.django_db
class TestChangeStream:
    """GET /api/whatsapp/stream — the SSE change feed the dashboard subscribes
    to instead of polling the threads endpoints every few seconds."""

    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/whatsapp/stream").status_code == 401

    def test_first_frame_is_immediate_change_event(self, client, superadmin):
        r = client.get("/api/whatsapp/stream", **AUTH)
        assert r.status_code == 200
        assert r["Content-Type"] == "text/event-stream"
        first = next(iter(r.streaming_content)).decode()
        assert first.startswith("event: change\n")
        r.close()  # stop the generator before its keep-alive sleep loop

    def test_token_moves_on_every_store_write_path(self, superadmin):
        t0 = wa_views._inbox_change_token()
        wa_store.get_or_create_thread(f"+{WA_ID}", name="Tester")
        t1 = wa_views._inbox_change_token()
        assert t1 != t0  # new thread
        wa_store.append_message(WA_ID, "in", "hello", source="customer",
                                msg_id="wamid.stream.1")
        t2 = wa_views._inbox_change_token()
        assert t2 != t1  # new message
        wa_store.update_message_status(WA_ID, "wamid.stream.1", "read")
        t3 = wa_views._inbox_change_token()
        assert t3 != t2  # delivery-status ticks bump the thread too

    def test_flow_consumed_reply_blocks_ai_double_answer(self, no_env_account,
                                                         no_quiet_hours, monkeypatch):
        # observed live: flow resumed on "I cannot pay it" and dispatched a
        # call; the humanized-delay AI check ran seconds later, saw no WAITING
        # run, and texted an answer while the phone was ringing
        from crm.models import FlowRun, Workflow
        monkeypatch.setattr(wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(
            wa_views.ai, "generate_reply",
            lambda thread: pytest.fail("AI must not double-answer a flow-handled reply"))
        c = CrmContact.objects.create(name="Linked", phone=f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID)
        wa_store.update_thread(WA_ID, {"ai_enabled": True})
        wa_store.append_message(WA_ID, "in", "cannot pay", source="customer")
        wf = Workflow.objects.create(flow_id="wf-x", name="x", nodes=[], edges=[],
                                     active=True)
        FlowRun.objects.create(workflow=wf, contact=c, trigger_key="t1",
                               current_node="e1", status="done",
                               context={"answered": True},
                               finished_at=timezone.now())
        assert wa_views._maybe_ai_reply(wa_store.load_thread(WA_ID)) is None
        # a NEWER message after the run finished is the AI's to answer again
        wa_store.append_message(WA_ID, "in", "how much do I owe?", source="customer")
        monkeypatch.setattr(wa_views.ai, "generate_reply", lambda thread: "RM100")
        msg = wa_views._maybe_ai_reply(wa_store.load_thread(WA_ID))
        assert msg is not None and msg["text"] == "RM100"
