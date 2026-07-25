"""Channel-health monitor + contact→account routing/failover tests.

Covers: Meta error-code classification (bans, auth, per-recipient no-ops,
transient streaks), one-notification-per-transition fan-out, inbound healing,
sticky least-loaded auto-assignment, health-aware failover (pin override),
the send-path/webhook/gateway wiring, the channel-health + assignments +
rebalance endpoints (with role gates), and the Baileys status-event push.

Same fixture pattern as tests/test_whatsapp_features.py.
"""
import hashlib
import hmac
import json

import pytest
from django.contrib.auth.models import User

from crm.models import (
    AccountAssignment,
    AuditLog,
    ChannelAccount,
    Contact as CrmContact,
    Message as CrmMessage,
    Notification,
    OutboundJob,
    Profile,
    Setting,
    Thread,
)
from flows import queue as flows_queue
from whatsapp import client as wa_client
from whatsapp import health, routing
from whatsapp import store as wa_store
from whatsapp import views as wa_views
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
AGENT_TOKEN = "test-agent-token"
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}
SECRET_HDR = {"HTTP_X_BAILEYS_SECRET": wa_config.BAILEYS_SECRET}
WA_ID = "60123456789"


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="agent", password="collect123")
    Profile.objects.create(user=user, role="agent", token=AGENT_TOKEN)
    return user


@pytest.fixture
def no_env_account(monkeypatch):
    """A developer's real WHATSAPP_* .env keys must not leak into tests."""
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def no_async_ai(monkeypatch):
    monkeypatch.setattr(wa_views, "_ai_reply_async", lambda wa_id: None)
    monkeypatch.setattr(wa_views.flows_engine, "on_inbound_async",
                        lambda channel, key, text: None)


@pytest.fixture
def no_quiet_hours(monkeypatch):
    monkeypatch.setattr(wa_config, "in_quiet_hours", lambda: False)


@pytest.fixture
def no_side_effects(monkeypatch):
    monkeypatch.setattr(wa_views, "_inbound_side_effects_async",
                        lambda account, wa_id, wamid, media=None: None)


def _baileys(name="QR A"):
    """A configured account with no Meta creds (provider=baileys)."""
    return ChannelAccount.objects.create(channel="whatsapp", provider="baileys", name=name)


def _cloud(name="Cloud", pnid="1234567890", secret=""):
    return ChannelAccount.objects.create(channel="whatsapp", provider="cloud", name=name,
                                         external_id=pnid, access_token="tok",
                                         app_secret=secret)


def _telegram(name="Bot", token="123:abc"):
    """A configured telegram bot (non-whatsapp channels need only a token)."""
    return ChannelAccount.objects.create(channel="telegram", name=name,
                                         external_id="42", access_token=token)


def _email_account(name="Mailbox", host="smtp.example.com", password="pw", **cfg):
    """A configured email account (SMTP creds live in config, not access_token)."""
    config = {"host": host, "port": 587, "username": "u@x.com",
              "password": password, "use_tls": True, "from_email": "u@x.com"}
    config.update(cfg)
    return ChannelAccount.objects.create(channel="email", name=name,
                                         external_id=config["from_email"], config=config)


def _contact(name="Ali", phone="+60111000001"):
    return CrmContact.objects.create(name=name, phone=phone)


# ---- per-chat sending-account switch ------------------------------------------------

@pytest.mark.django_db
class TestSwitchAccount:
    URL = f"/api/whatsapp/threads/{WA_ID}/account"

    def _post(self, client, account_id, auth=AUTH):
        return client.post(self.URL, data=json.dumps({"account_id": account_id}),
                           content_type="application/json", **auth)

    def _thread_on(self, account):
        wa_store.get_or_create_thread(WA_ID, account_id=str(account.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")

    def test_auth_gates(self, client, superadmin, agent_user, no_env_account):
        a, b = _cloud("A", "111"), _cloud("B", "222")
        self._thread_on(a)
        assert client.post(self.URL, data=json.dumps({"account_id": str(b.pk)}),
                           content_type="application/json").status_code == 401
        assert self._post(client, str(b.pk), auth=AGENT_AUTH).status_code == 403

    def test_switch_moves_thread_closes_window_pins_assignment(self, client, superadmin,
                                                               no_env_account):
        a, b = _cloud("A", "111"), _cloud("B", "222")
        self._thread_on(a)
        contact = _contact(phone=f"+{WA_ID}")
        Thread.objects.filter(channel="whatsapp", key=WA_ID).update(contact=contact)
        assert wa_store.load_thread(WA_ID)["window_open"] is True

        r = self._post(client, str(b.pk))
        assert r.status_code == 200
        body = r.json()
        assert body["account_id"] == str(b.pk) and body["account_name"] == "B"
        assert body["window_open_with_new_account"] is False
        t = wa_store.load_thread(WA_ID)
        assert t["account_id"] == str(b.pk)
        # Meta's window is per OUR number — the new number has none until the
        # contact replies to it
        assert t["window_open"] is False
        asg = AccountAssignment.objects.get(contact=contact, channel="whatsapp")
        assert asg.account_id == b.pk and asg.pinned is True
        # the operator-visible system note landed in the thread, with the
        # cold-number guidance (contact never talked to B)
        note = next(m for m in t["messages"] if "Sender account switched to B" in m["text"])
        assert "brand-new number" in note["text"]
        # per-message attribution: new outbound rows carry account B
        assert t["messages"][-1].get("account_id") == str(b.pk)

    def test_switch_to_number_with_open_window_keeps_it(self, client, superadmin,
                                                        no_env_account):
        from crm.models import Message as CrmMessage

        a, b = _cloud("A", "111"), _cloud("B", "222")
        self._thread_on(a)
        t_obj = Thread.objects.get(channel="whatsapp", key=WA_ID)
        # the contact replied to B recently (e.g. an earlier campaign)
        CrmMessage.objects.create(thread=t_obj, direction="in", source="customer",
                                  text="hi on B", status="received", account=b)
        r = self._post(client, str(b.pk))
        assert r.status_code == 200
        assert r.json()["window_open_with_new_account"] is True
        t = wa_store.load_thread(WA_ID)
        assert t["window_open"] is True  # B's own window survives the switch
        assert any("window is open" in m["text"] for m in t["messages"])

    def test_switch_to_baileys_needs_no_template(self, client, superadmin,
                                                 no_env_account):
        a, b = _cloud("A", "111"), _baileys("QR B")
        self._thread_on(a)
        r = self._post(client, str(b.pk))
        assert r.status_code == 200
        assert r.json()["provider"] == "baileys"
        assert r.json()["window_open_with_new_account"] is True
        t = wa_store.load_thread(WA_ID)
        assert t["window_open"] is True  # WhatsApp Web: no 24h window at all
        note = next(m for m in t["messages"] if "Sender account switched" in m["text"])
        assert "ban risk" in note["text"]  # cold first contact warning instead

    def test_same_account_is_a_noop(self, client, superadmin, no_env_account):
        a = _cloud("A", "111")
        self._thread_on(a)
        before = len(wa_store.load_thread(WA_ID)["messages"])
        assert self._post(client, str(a.pk)).status_code == 200
        assert len(wa_store.load_thread(WA_ID)["messages"]) == before

    def test_bad_targets(self, client, superadmin, no_env_account):
        a = _cloud("A", "111")
        self._thread_on(a)
        assert self._post(client, "9999").status_code == 404
        assert self._post(client, wa_config.ENV_ACCOUNT_ID).status_code == 400
        assert self._post(client, "").status_code == 400


# ---- reconnect reset + capability-aware template routing ---------------------------

class _InlineThread:
    """threading.Thread stand-in that runs the target synchronously."""
    def __init__(self, target=None, args=(), kwargs=None, daemon=None, name=None):
        self._target, self._args, self._kwargs = target, args, kwargs or {}

    def start(self):
        if self._target:
            self._target(*self._args, **self._kwargs)


@pytest.mark.django_db
class TestReconnectReset:
    def test_reset_clears_verdict_and_rechecks(self, monkeypatch):
        a = _cloud("A", "111")
        health.set_status(a, "disconnected", "token expired")
        monkeypatch.setattr(health.threading, "Thread", _InlineThread)
        monkeypatch.setattr(health, "_graph_ping", lambda acct: (True, "", None))
        health.reset_on_reconfigure(str(a.pk))
        a.refresh_from_db()
        # the inline recheck ran and the fresh token validated
        assert a.health_status == "healthy"
        assert a.fail_streak == 0

    def test_update_account_token_triggers_reset(self, monkeypatch):
        a = _cloud("A", "111")
        health.set_status(a, "disconnected", "token expired")
        called = []
        monkeypatch.setattr(health, "reset_on_reconfigure",
                            lambda pk: called.append(pk))
        wa_config.update_account(str(a.pk), {"access_token": "brand-new-token"})
        assert called == [str(a.pk)]
        # a no-credential edit (name) must NOT reset
        called.clear()
        wa_config.update_account(str(a.pk), {"name": "renamed"})
        assert called == []


@pytest.mark.django_db
class TestTemplateCloudOnlyRouting:
    def _thread_on(self, account):
        wa_store.get_or_create_thread(WA_ID, account_id=str(account.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")

    def _send(self, client, monkeypatch, captured):
        monkeypatch.setattr(
            "whatsapp.client.send_template",
            lambda acct, to, name, lang, variables: captured.append(acct) or "wamid.tpl.1")
        return client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                           data=json.dumps({"name": "order_confirmation"}),
                           content_type="application/json", **AUTH)

    def test_dead_cloud_fails_over_to_cloud_not_baileys(self, client, superadmin,
                                                        no_env_account, no_quiet_hours,
                                                        monkeypatch):
        dead = _cloud("TEST", "111")
        _baileys("KX Office")           # must never carry a template
        good = _cloud("Cloud B", "222")
        self._thread_on(dead)
        Thread.objects.filter(channel="whatsapp", key=WA_ID).update(contact=_contact())
        health.set_status(dead, "disconnected", "token expired")
        captured = []
        r = self._send(client, monkeypatch, captured)
        assert r.status_code == 200
        assert captured and captured[0]["id"] == str(good.pk)
        t = wa_store.load_thread(WA_ID)
        assert t["account_id"] == str(good.pk)

    def test_no_eligible_cloud_is_409_and_thread_untouched(self, client, superadmin,
                                                           no_env_account, no_quiet_hours,
                                                           monkeypatch):
        dead = _cloud("TEST", "111")
        _baileys("KX Office")  # healthy, but templates must not fall to it
        self._thread_on(dead)
        health.set_status(dead, "disconnected", "token expired")
        captured = []
        r = self._send(client, monkeypatch, captured)
        assert r.status_code == 409
        assert "Cloud-only" in r.json()["detail"]
        assert captured == []
        assert wa_store.load_thread(WA_ID)["account_id"] == str(dead.pk)

    def test_healthy_baileys_thread_routes_template_via_cloud(self, client, superadmin,
                                                              no_env_account,
                                                              no_quiet_hours, monkeypatch):
        qr = _baileys("KX Office")
        good = _cloud("Cloud B", "222")
        self._thread_on(qr)
        contact = _contact()
        Thread.objects.filter(channel="whatsapp", key=WA_ID).update(contact=contact)
        AccountAssignment.objects.create(contact=contact, channel="whatsapp",
                                         account=qr, pinned=True)
        captured = []
        r = self._send(client, monkeypatch, captured)
        assert r.status_code == 200
        assert captured and captured[0]["id"] == str(good.pk)
        # capability override, NOT failover: the sticky assignment stays put
        asg = AccountAssignment.objects.get(contact=contact, channel="whatsapp")
        assert asg.account_id == qr.pk and asg.pinned is True
        # operator sees why the chat moved
        t = wa_store.load_thread(WA_ID)
        assert any("Template requires the Cloud API" in m["text"] for m in t["messages"])


# ---- telegram bot health ------------------------------------------------------------

@pytest.mark.django_db
class TestTelegramHealth:
    def _bot(self, token="123:abc"):
        return ChannelAccount.objects.create(channel="telegram", name="TestBot",
                                             external_id="123", access_token=token)

    def test_run_checks_pings_telegram(self, monkeypatch, superadmin):
        a = self._bot()
        monkeypatch.setattr(health, "_telegram_ping", lambda token: ("healthy", ""))
        monkeypatch.setattr(health, "_graph_ping", lambda acct: (True, "", None))
        summary = health.run_checks()
        assert summary["telegram_checked"] == 1
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_ok_at is not None

    def test_revoked_token_marks_disconnected(self, monkeypatch, superadmin):
        a = self._bot()
        monkeypatch.setattr(health, "_telegram_ping",
                            lambda token: ("disconnected", "bot token invalid or revoked"))
        health.run_checks()
        a.refresh_from_db()
        assert a.health_status == "disconnected"
        assert "revoked" in a.health_detail

    def test_webhook_trouble_marks_degraded(self, monkeypatch, superadmin):
        a = self._bot()
        monkeypatch.setattr(health, "_telegram_ping",
                            lambda token: ("degraded",
                                           "bot alive, but no webhook set — inbound "
                                           "messages won't arrive"))
        health.run_checks()
        a.refresh_from_db()
        assert a.health_status == "degraded"
        assert "webhook" in a.health_detail

    def test_tokenless_bot_skipped(self, monkeypatch, superadmin):
        ChannelAccount.objects.create(channel="telegram", name="Empty")
        called = []
        monkeypatch.setattr(health, "_telegram_ping",
                            lambda token: called.append(token) or ("healthy", ""))
        summary = health.run_checks()
        assert summary["telegram_checked"] == 0 and called == []

    def test_inbound_webhook_heals(self, client, superadmin, monkeypatch):
        a = self._bot(token="tok-in")
        health.set_status(a, "degraded", "flaky")
        r = client.post("/api/messaging/webhook/telegram/tok-in",
                        data=json.dumps({"message": {"message_id": 1,
                                                     "chat": {"id": 777},
                                                     "from": {"first_name": "KX"},
                                                     "text": "hi"}}),
                        content_type="application/json")
        assert r.status_code == 200
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_inbound_at is not None


# ---- voice (Twilio SIP) health ------------------------------------------------------

@pytest.mark.django_db
class TestVoiceHealth:
    def test_unconfigured(self, monkeypatch):
        monkeypatch.delenv("SIP_OUTBOUND_TRUNK_ID", raising=False)
        monkeypatch.delenv("TWILIO_TERMINATION_URI", raising=False)
        assert health.voice_health() == {"configured": False, "health": "unknown",
                                         "detail": "", "live_check": False}

    def test_configured_without_sip_creds(self, monkeypatch):
        monkeypatch.setenv("SIP_OUTBOUND_TRUNK_ID", "ST_x")
        monkeypatch.setenv("TWILIO_TERMINATION_URI", "x.pstn.twilio.com")
        monkeypatch.delenv("TWILIO_SIP_USERNAME", raising=False)
        monkeypatch.delenv("TWILIO_SIP_PASSWORD", raising=False)
        v = health.voice_health()
        assert v["configured"] is True and v["live_check"] is False
        assert "TWILIO_SIP_USERNAME" in v["detail"]

    def test_healthy_once_sip_creds_present(self, monkeypatch):
        monkeypatch.setenv("SIP_OUTBOUND_TRUNK_ID", "ST_x")
        monkeypatch.setenv("TWILIO_TERMINATION_URI", "x.pstn.twilio.com")
        monkeypatch.setenv("TWILIO_SIP_USERNAME", "automation")
        monkeypatch.setenv("TWILIO_SIP_PASSWORD", "secret")
        assert health.voice_health() == {"configured": True, "live_check": False,
                                         "health": "healthy", "detail": ""}

    def test_endpoint_includes_voice(self, client, superadmin, monkeypatch):
        monkeypatch.delenv("SIP_OUTBOUND_TRUNK_ID", raising=False)
        r = client.get("/api/whatsapp/channels/health", **AUTH)
        assert r.status_code == 200
        assert set(r.json()["voice"]) == {"configured", "health", "detail", "live_check"}


# ---- voice (Twilio) settings — dashboard-managed SIP creds with .env fallback -------

@pytest.mark.django_db
class TestVoiceSettings:
    URL = "/api/whatsapp/voice-settings"

    @pytest.fixture(autouse=True)
    def clean_env(self, monkeypatch):
        """A developer's real Twilio/SIP .env keys must not leak into tests."""
        for key in ("TWILIO_SIP_USERNAME", "TWILIO_SIP_PASSWORD",
                    "SIP_OUTBOUND_TRUNK_ID", "TWILIO_TERMINATION_URI"):
            monkeypatch.delenv(key, raising=False)

    def _put(self, client, body, auth=AUTH):
        return client.put(self.URL, data=json.dumps(body),
                          content_type="application/json", **auth)

    def test_requires_auth(self, client, superadmin):
        assert client.get(self.URL).status_code == 401

    def test_settings_row_overrides_env(self, monkeypatch):
        monkeypatch.setenv("TWILIO_SIP_USERNAME", "env-user")
        monkeypatch.setenv("TWILIO_SIP_PASSWORD", "env-pass")
        health.save_voice_settings({"sip_username": "db-user", "sip_password": "db-pass"})
        assert health.voice_settings()["sip_username"] == "db-user"
        # empty string clears the stored value back to the .env fallback
        health.save_voice_settings({"sip_username": ""})
        assert health.voice_settings()["sip_username"] == "env-user"

    def test_get_masks_password(self, client, superadmin):
        health.save_voice_settings({"sip_username": "automation",
                                    "sip_password": "secret-password-value",
                                    "termination_uri": "x.pstn.twilio.com"})
        r = client.get(self.URL, **AUTH)
        assert r.status_code == 200
        body = r.json()
        assert body["sip_username"] == "automation"
        assert "secret-password-value" not in json.dumps(body)
        assert "••" in body["sip_password_masked"]
        assert body["source"]["sip_password"] == "settings"
        assert body["sip_trunk_configured"] is False  # SIP_OUTBOUND_TRUNK_ID still unset

    def test_put_superadmin_only(self, client, superadmin, agent_user):
        r = self._put(client, {"sip_username": "u"}, auth=AGENT_AUTH)
        assert r.status_code == 403

    def test_put_saves_and_audits(self, client, superadmin):
        r = self._put(client, {"sip_username": "automation",
                               "sip_password": "tok-abcdefgh",
                               "termination_uri": "chatbranch.pstn.twilio.com"})
        assert r.status_code == 200
        assert health.voice_settings()["sip_password"] == "tok-abcdefgh"
        assert AuditLog.objects.filter(action="settings.update",
                                       target="voice-twilio").exists()

    def test_masked_placeholder_keeps_stored_password(self, client, superadmin):
        health.save_voice_settings({"sip_username": "automation", "sip_password": "keep-me-please"})
        r = self._put(client, {"sip_username": "automation", "sip_password": "••••ease"})
        assert r.status_code == 200
        assert health.voice_settings()["sip_password"] == "keep-me-please"

    def test_put_validates(self, client, superadmin):
        assert self._put(client, {}).status_code == 400

    def test_healthy_from_settings_row_without_env_creds(self, monkeypatch):
        monkeypatch.setenv("SIP_OUTBOUND_TRUNK_ID", "ST_x")
        health.save_voice_settings({"sip_username": "automation", "sip_password": "tok",
                                    "termination_uri": "x.pstn.twilio.com"})
        assert health.voice_health() == {"configured": True, "live_check": False,
                                         "health": "healthy", "detail": ""}

    def test_test_endpoint(self, client, superadmin):
        r = client.post(self.URL + "/test", **AUTH)
        assert r.status_code == 400  # no creds anywhere yet
        health.save_voice_settings({"sip_username": "automation", "sip_password": "tok"})
        r = client.post(self.URL + "/test", **AUTH)
        assert r.status_code == 400  # still no SIP_OUTBOUND_TRUNK_ID / termination uri
        with pytest.MonkeyPatch.context() as mp:
            mp.setenv("SIP_OUTBOUND_TRUNK_ID", "ST_x")
            mp.setenv("TWILIO_TERMINATION_URI", "x.pstn.twilio.com")
            r = client.post(self.URL + "/test", **AUTH)
        assert r.status_code == 200
        assert r.json() == {"ok": True, "health": "healthy", "detail": "SIP trunk creds are set."}


# ---- health classification ---------------------------------------------------------

@pytest.mark.django_db
class TestHealthClassification:
    def test_ban_code_in_text_marks_banned(self):
        a = _baileys()
        health.record_send_failure(str(a.pk), "Meta API 403: Account locked (code 131031)")
        a.refresh_from_db()
        assert a.health_status == "banned"
        assert "131031" in a.health_detail
        assert a.health_changed_at is not None

    def test_policy_block_368_banned(self):
        a = _baileys()
        health.record_send_failure(str(a.pk), "temporarily blocked for policies", code=368)
        a.refresh_from_db()
        assert a.health_status == "banned"

    def test_oauth_190_disconnected(self):
        a = _cloud()
        health.record_send_failure(str(a.pk), "Error validating access token", code=190)
        a.refresh_from_db()
        assert a.health_status == "disconnected"

    def test_http_401_text_disconnected(self):
        a = _cloud()
        health.record_send_failure(str(a.pk), "Meta API 401: Invalid OAuth access token")
        a.refresh_from_db()
        assert a.health_status == "disconnected"

    def test_window_closed_does_not_hurt_health(self):
        a = _cloud()
        health.record_send_ok(str(a.pk))
        health.record_send_failure(
            str(a.pk), "Meta API 400: Re-engagement message (code 131047)")
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.fail_streak == 0

    def test_recipient_codes_do_no_damage(self):
        a = _cloud()
        for code in (131026, 131021, 131030, 131049):
            health.record_send_failure(str(a.pk), "recipient problem", code=code)
        a.refresh_from_db()
        assert a.health_status == "unknown"  # untouched
        assert a.fail_streak == 0

    def test_three_transient_failures_degrade(self):
        a = _cloud()
        health.record_send_failure(str(a.pk), "rate limit hit", code=130429)
        health.record_send_failure(str(a.pk), "Meta API 500: server error")
        a.refresh_from_db()
        assert a.health_status == "unknown"  # not yet
        assert a.fail_streak == 2
        health.record_send_failure(str(a.pk), "timeout talking to graph.facebook.com")
        a.refresh_from_db()
        assert a.health_status == "degraded"
        assert a.fail_streak == 3

    def test_send_ok_resets(self):
        a = _cloud()
        for _ in range(3):
            health.record_send_failure(str(a.pk), "timeout")
        health.record_send_ok(str(a.pk))
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.fail_streak == 0
        assert a.last_ok_at is not None
        assert a.health_detail == ""

    def test_record_inbound_heals(self):
        a = _baileys()
        health.set_status(a, "degraded", "flaky")
        health.record_inbound(str(a.pk))
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_inbound_at is not None
        assert a.fail_streak == 0

    def test_pseudo_and_garbage_ids_are_noops(self):
        # env-default has no DB row; junk must never raise into a send path
        health.record_send_ok("env-default")
        health.record_send_failure("env-default", "boom", code=131031)
        health.record_inbound("")
        health.record_send_failure(None, None)
        health.set_status(None, "banned")


# ---- notification fan-out ---------------------------------------------------------

@pytest.mark.django_db
class TestHealthNotifications:
    def test_notification_once_per_transition(self, superadmin):
        a = _baileys()  # configured (baileys needs no creds)
        health.record_send_failure(str(a.pk), "locked", code=131031)
        a.refresh_from_db()
        health.record_send_failure(str(a.pk), "still locked", code=131031)  # same status
        notes = Notification.objects.filter(user=superadmin,
                                            title__icontains="banned")
        assert notes.count() == 1
        assert AuditLog.objects.filter(action="account.health").count() == 1

    def test_heal_transition_notifies_again(self, superadmin):
        a = _baileys()
        health.record_send_failure(str(a.pk), "locked", code=131031)
        a.refresh_from_db()
        health.record_inbound(str(a.pk))
        assert Notification.objects.filter(user=superadmin).count() == 2

    def test_unconfigured_account_never_notifies(self, superadmin):
        a = ChannelAccount.objects.create(channel="whatsapp", provider="cloud",
                                          name="empty")  # no token -> sandbox
        health.set_status(a, "banned", "whatever")
        a.refresh_from_db()
        assert a.health_status == "banned"  # tracked silently…
        assert Notification.objects.count() == 0  # …but nobody is paged


# ---- routing -----------------------------------------------------------------------

@pytest.mark.django_db
class TestRouting:
    def test_even_auto_assignment(self, no_env_account):
        accounts = [_baileys(f"QR {i}") for i in range(3)]
        for i in range(6):
            routing.account_for_contact(_contact(f"C{i}", f"+6011100000{i}"))
        counts = sorted(
            AccountAssignment.objects.filter(account__in=accounts)
            .values_list("account_id", flat=True))
        per_account = {pk: counts.count(pk) for pk in set(counts)}
        assert sorted(per_account.values()) == [2, 2, 2]

    def test_sticky_assignment(self, no_env_account):
        _baileys("A"), _baileys("B")
        c = _contact()
        first = routing.account_for_contact(c)
        for _ in range(3):
            assert routing.account_for_contact(c).pk == first.pk
        assert AccountAssignment.objects.filter(contact=c).count() == 1

    def test_failover_moves_assignment_and_notifies(self, superadmin, no_env_account):
        a, b = _baileys("A"), _baileys("B")
        c = _contact()
        assert routing.account_for_contact(c).pk == a.pk  # least-loaded = lowest pk
        health.set_status(a, "banned", "test ban")
        moved = routing.account_for_contact(c)
        assert moved.pk == b.pk
        asg = AccountAssignment.objects.get(contact=c)
        assert asg.account_id == b.pk
        assert asg.pinned is False
        assert Notification.objects.filter(title__startswith="Failover:").count() == 1
        assert AuditLog.objects.filter(action="account.failover").exists()

    def test_pinned_does_not_survive_failover(self, no_env_account):
        a, b = _baileys("A"), _baileys("B")
        c = _contact()
        AccountAssignment.objects.create(contact=c, channel="whatsapp",
                                         account=a, pinned=True)
        health.set_status(a, "disconnected", "token dead")
        moved = routing.account_for_contact(c)
        assert moved.pk == b.pk
        asg = AccountAssignment.objects.get(contact=c)
        assert asg.pinned is False  # a dead account never blocks follow-up

    def test_no_eligible_accounts_returns_none(self, no_env_account):
        a = _baileys("A")
        health.set_status(a, "banned", "gone")
        assert routing.eligible_accounts() == []
        assert routing.account_for_contact(_contact()) is None

    def test_env_default_is_not_assignable(self, monkeypatch):
        monkeypatch.setattr(wa_config, "_env_account", lambda: {
            "id": wa_config.ENV_ACCOUNT_ID, "provider": "cloud", "name": "env",
            "phone_number_id": "1", "access_token": "tok", "api_version": "v23.0",
        })
        # env-default is configured, but it has no DB row -> not in the pool
        assert routing.eligible_accounts() == []
        assert routing.account_for_contact(_contact()) is None

    def test_telegram_eligibility_and_assignment(self, no_env_account):
        ChannelAccount.objects.create(channel="telegram", name="Tokenless")
        bot = _telegram("Bot A")
        _baileys("WA")  # other channels never leak into the telegram pool
        assert [a.pk for a in routing.eligible_accounts("telegram")] == [bot.pk]
        c = _contact()
        assert routing.account_for_contact(c, "telegram").pk == bot.pk
        asg = AccountAssignment.objects.get(contact=c)
        assert asg.channel == "telegram"  # no whatsapp row was created

    def test_facebook_failover(self, no_env_account):
        a = ChannelAccount.objects.create(channel="facebook", name="Page A",
                                          external_id="pg1", access_token="tok")
        b = ChannelAccount.objects.create(channel="facebook", name="Page B",
                                          external_id="pg2", access_token="tok")
        c = _contact()
        assert routing.account_for_contact(c, "facebook").pk == a.pk
        health.set_status(a, "disconnected", "token expired")
        assert routing.account_for_contact(c, "facebook").pk == b.pk
        assert AccountAssignment.objects.get(contact=c,
                                             channel="facebook").account_id == b.pk


# ---- email accounts: routing eligibility + health checks ---------------------------

@pytest.mark.django_db
class TestEmailAccountHealthRouting:
    def test_routing_eligibility_needs_smtp_creds(self, no_env_account):
        a = _email_account()
        ChannelAccount.objects.create(channel="email", name="hollow")  # no config
        _email_account(name="No password", password="")                # incomplete
        assert [x.pk for x in routing.eligible_accounts("email")] == [a.pk]
        c = _contact()
        assert routing.account_for_contact(c, "email").pk == a.pk
        assert AccountAssignment.objects.get(contact=c).channel == "email"

    def test_email_failover(self, no_env_account):
        a, b = _email_account("A"), _email_account("B")
        c = _contact()
        assert routing.account_for_contact(c, "email").pk == a.pk
        health.set_status(a, "disconnected", "smtp down")
        assert routing.account_for_contact(c, "email").pk == b.pk
        assert AccountAssignment.objects.get(contact=c,
                                             channel="email").account_id == b.pk

    def test_run_checks_probes_email_rows(self, superadmin, monkeypatch):
        a = _email_account()
        skipped = ChannelAccount.objects.create(channel="email", name="empty")
        seen = []
        monkeypatch.setattr(
            health, "_smtp_ping",
            lambda host=None, port=None, use_tls=None:
            (seen.append((host, port, use_tls)), (True, ""))[1])
        monkeypatch.setattr("messaging.msg_config.email_configured", lambda: False)
        summary = health.run_checks()
        assert summary["email_accounts_checked"] == 1
        assert seen == [("smtp.example.com", 587, True)]  # the row's own server
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_ok_at is not None
        skipped.refresh_from_db()
        assert skipped.health_status == "unknown"  # unconfigured rows never probed

    def test_run_checks_marks_dead_smtp_disconnected(self, superadmin, monkeypatch):
        a = _email_account()
        monkeypatch.setattr(health, "_smtp_ping",
                            lambda host=None, port=None, use_tls=None:
                            (False, "connect refused"))
        monkeypatch.setattr("messaging.msg_config.email_configured", lambda: False)
        health.run_checks()
        a.refresh_from_db()
        assert a.health_status == "disconnected"
        assert "refused" in a.health_detail

    def test_configured_email_account_notifies_on_transition(self, superadmin):
        # _is_configured must read the SMTP config, not access_token (which is
        # "" on email rows) — otherwise transitions would stay silent.
        a = _email_account()
        health.set_status(a, "banned", "blocked by provider")
        assert Notification.objects.filter(title__icontains="banned").count() == 1

    def test_channels_health_configured_flag(self, client, superadmin, no_env_account):
        a = _email_account()
        empty = ChannelAccount.objects.create(channel="email", name="empty")
        r = client.get("/api/whatsapp/channels/health", **AUTH)
        assert r.status_code == 200
        by_id = {row["id"]: row for row in r.json()["accounts"]}
        assert by_id[str(a.pk)]["configured"] is True    # config-based, not access_token
        assert by_id[str(empty.pk)]["configured"] is False

    def test_assignments_lists_email_channel(self, client, superadmin):
        a = _email_account()
        data = client.get("/api/whatsapp/assignments?channel=email", **AUTH).json()
        assert "email" in data["channels"]
        assert [row["id"] for row in data["accounts"]] == [str(a.pk)]


# ---- wiring into the send / inbound paths ------------------------------------------

def _mk_thread(client, phone=f"+{WA_ID}", name="Tester"):
    r = client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": phone, "name": name}),
                    content_type="application/json", **AUTH)
    assert r.status_code == 200
    return r.json()


@pytest.mark.django_db
class TestSendPathWiring:
    def test_send_ok_records_health(self, client, superadmin, no_env_account,
                                    no_async_ai, monkeypatch):
        a = _baileys()
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "ID1")
        _mk_thread(client)
        # inbound-first: a cold personal-number thread would hit the guard
        wa_store.append_message(WA_ID, "in", "hi", source="customer")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "hello"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_ok_at is not None

    def test_send_failure_classifies_account(self, client, superadmin, no_env_account,
                                             no_async_ai, monkeypatch):
        a = _baileys()

        def boom(acct, to, text):
            raise wa_client.WhatsAppError("Meta API 401: Invalid OAuth access token (code 190)")

        monkeypatch.setattr(wa_client, "send_text", boom)
        _mk_thread(client)
        wa_store.append_message(WA_ID, "in", "hi", source="customer")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "hello"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 502
        a.refresh_from_db()
        assert a.health_status == "disconnected"

    def test_send_fails_over_to_eligible_account(self, client, superadmin, no_env_account,
                                                 no_async_ai, monkeypatch):
        a, b = _baileys("A"), _baileys("B")
        contact = _contact("Ahmad", f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hi", source="customer")
        health.set_status(a, "banned", "dead number")
        sent = {}

        def fake_send(acct, to, text):
            sent["account_id"] = acct["id"]
            return "ID2"

        monkeypatch.setattr(wa_client, "send_text", fake_send)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "hello"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert sent["account_id"] == str(b.pk)  # rerouted off the banned account
        assert Thread.objects.get(channel="whatsapp", key=WA_ID).account_id == b.pk
        assert AccountAssignment.objects.get(contact=contact).account_id == b.pk

    def test_webhook_inbound_records_health(self, client, no_env_account, no_async_ai,
                                            no_side_effects):
        secret = "whsec"
        a = _cloud(pnid="555000111", secret=secret)
        payload = {"entry": [{"changes": [{"field": "messages", "value": {
            "metadata": {"phone_number_id": "555000111"},
            "contacts": [{"profile": {"name": "T"}, "wa_id": WA_ID}],
            "messages": [{"from": WA_ID, "id": "wamid.h1", "type": "text",
                          "text": {"body": "hi"}}],
        }}]}]}
        body = json.dumps(payload).encode()
        sig = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
        r = client.post("/api/whatsapp/webhook", data=body,
                        content_type="application/json", HTTP_X_HUB_SIGNATURE_256=sig)
        assert r.status_code == 200
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_inbound_at is not None

    def test_baileys_inbound_records_health(self, client, no_async_ai):
        a = _baileys()
        r = client.post("/api/whatsapp/baileys/inbound",
                        data=json.dumps({"account_id": str(a.pk), "wa_id": "60111222333",
                                         "name": "Aina", "text": "hi", "msg_id": "H1"}),
                        content_type="application/json", **SECRET_HDR)
        assert r.status_code == 200
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_inbound_at is not None

    def test_new_thread_routes_by_assignment(self, client, superadmin, no_env_account):
        _baileys("A")
        b = _baileys("B")
        contact = _contact("Siti", "+60129998877")
        AccountAssignment.objects.create(contact=contact, channel="whatsapp",
                                         account=b, pinned=True)
        r = client.post("/api/whatsapp/threads",
                        data=json.dumps({"phone": "+60129998877"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        t = Thread.objects.get(channel="whatsapp", key="60129998877")
        assert t.account_id == b.pk


# ---- baileys gateway status events -------------------------------------------------

def _status_event(client, account_id, event, reason="", **extra):
    return client.post("/api/whatsapp/baileys/status-event",
                       data=json.dumps({"account_id": account_id, "event": event,
                                        "reason": reason}),
                       content_type="application/json", **{**SECRET_HDR, **extra})


@pytest.mark.django_db
class TestBaileysStatusEvent:
    def test_requires_secret(self, client, db):
        r = client.post("/api/whatsapp/baileys/status-event", data="{}",
                        content_type="application/json",
                        HTTP_X_BAILEYS_SECRET="wrong")
        assert r.status_code == 403

    def test_logged_out_disconnects_but_does_not_ban(self, client):
        """401 is a dead linked-device SESSION, not a banned account.

        It is equally a manual unlink, the 14-day primary-offline rule or an
        app reinstall, and the number keeps working on the phone throughout.
        Reporting "banned" sent the operator hunting a WhatsApp enforcement
        action that had not happened. Still ineligible for routing either way
        — only the operator-facing label changes. 403 remains the ban signal
        (see test_forbidden_close_bans).
        """
        a = _baileys()
        r = _status_event(client, str(a.pk), "logged_out", "loggedOut (401)")
        assert r.status_code == 200
        a.refresh_from_db()
        assert a.health_status == "disconnected"
        assert "401" in a.health_detail

        from whatsapp import routing
        assert a.health_status in routing.INELIGIBLE_STATUSES

    def test_forbidden_close_bans(self, client):
        a = _baileys()
        _status_event(client, str(a.pk), "disconnected", "forbidden (403)")
        a.refresh_from_db()
        assert a.health_status == "banned"

    def test_plain_disconnect_degrades(self, client):
        a = _baileys()
        _status_event(client, str(a.pk), "disconnected", "connectionClosed (428)")
        a.refresh_from_db()
        assert a.health_status == "degraded"

    def test_connected_heals(self, client):
        a = _baileys()
        health.set_status(a, "degraded", "flaky")
        _status_event(client, str(a.pk), "connected")
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_ok_at is not None

    def test_cloud_accounts_ignored(self, client):
        a = _cloud()
        r = _status_event(client, str(a.pk), "logged_out", "loggedOut (401)")
        assert r.json().get("ignored") is True
        a.refresh_from_db()
        assert a.health_status == "unknown"


# ---- channels/health + assignments endpoints ---------------------------------------

@pytest.mark.django_db
class TestChannelsHealthEndpoint:
    def test_requires_auth(self, client, superadmin):
        assert client.get("/api/whatsapp/channels/health").status_code == 401

    def test_shape(self, client, superadmin, no_env_account):
        a = _baileys("QR")
        c = _contact()
        AccountAssignment.objects.create(contact=c, channel="whatsapp", account=a)
        health.set_status(a, "healthy", "")
        ChannelAccount.objects.create(channel="facebook", name="Page",
                                      external_id="pg1", access_token="tok")
        r = client.get("/api/whatsapp/channels/health", **AUTH)
        assert r.status_code == 200
        data = r.json()
        by_id = {row["id"]: row for row in data["accounts"]}
        mine = by_id[str(a.pk)]
        assert mine["channel"] == "whatsapp"
        assert mine["provider"] == "baileys"
        assert mine["configured"] is True
        assert mine["health"] == "healthy"
        assert mine["assigned_contacts"] == 1
        assert set(mine) >= {"id", "name", "channel", "provider", "configured",
                             "health", "detail", "last_ok_at", "last_inbound_at",
                             "fail_streak", "changed_at", "assigned_contacts"}
        # non-whatsapp channels are included too
        page = next(row for row in data["accounts"] if row["channel"] == "facebook")
        assert page["configured"] is True
        # env-default pseudo-account is hidden while backend/.env configures
        # nothing — an unconfigured env row is noise on the dashboard
        assert wa_config.ENV_ACCOUNT_ID not in by_id
        assert set(data["email"]) == {"configured", "health", "detail"}
        assert data["checked_at"]

    def test_env_default_shown_only_when_configured(self, client, superadmin,
                                                    monkeypatch):
        monkeypatch.setattr(wa_config, "_env_account", lambda: {
            "id": wa_config.ENV_ACCOUNT_ID, "provider": "cloud", "name": "env",
            "phone_number_id": "1", "access_token": "tok", "api_version": "v23.0",
        })
        r = client.get("/api/whatsapp/channels/health", **AUTH)
        assert r.status_code == 200
        by_id = {row["id"]: row for row in r.json()["accounts"]}
        env = by_id[wa_config.ENV_ACCOUNT_ID]
        assert env["configured"] is True
        assert env["health"] == "unknown"
        assert env["assigned_contacts"] == 0

    def test_email_health_after_check(self, client, superadmin, no_env_account,
                                      monkeypatch):
        monkeypatch.setattr("messaging.msg_config.email_configured", lambda: True)
        monkeypatch.setattr(health, "_smtp_ping", lambda: (False, "connect refused"))
        health.run_checks()
        r = client.get("/api/whatsapp/channels/health", **AUTH)
        assert r.json()["email"] == {"configured": True, "health": "disconnected",
                                     "detail": "connect refused"}

    def test_run_checks_pings_cloud_accounts(self, superadmin, no_env_account,
                                             monkeypatch):
        a = _cloud()
        monkeypatch.setattr(health, "_graph_ping", lambda d: (True, "", None))
        health.run_checks()
        a.refresh_from_db()
        assert a.health_status == "healthy"
        assert a.last_ok_at is not None
        monkeypatch.setattr(health, "_graph_ping",
                            lambda d: (False, "Meta API 401: bad token", 190))
        health.run_checks()
        a.refresh_from_db()
        assert a.health_status == "disconnected"
        assert health.last_checked_at()


@pytest.mark.django_db
class TestAssignmentsEndpoint:
    def test_get_requires_auth_but_any_role(self, client, superadmin, agent_user):
        assert client.get("/api/whatsapp/assignments").status_code == 401
        r = client.get("/api/whatsapp/assignments", **AGENT_AUTH)
        assert r.status_code == 200
        assert set(r.json()) == {"channel", "channels", "accounts", "assignments"}

    def test_get_shape(self, client, superadmin):
        a = _baileys()
        c = _contact()
        AccountAssignment.objects.create(contact=c, channel="whatsapp",
                                         account=a, pinned=True)
        data = client.get("/api/whatsapp/assignments", **AUTH).json()
        assert data["channel"] == "whatsapp"  # the default
        assert data["channels"] == ["whatsapp"]
        acct = next(row for row in data["accounts"] if row["id"] == str(a.pk))
        assert set(acct) == {"id", "name", "provider", "health", "contacts"}
        assert acct["contacts"] == 1
        row = data["assignments"][0]
        assert set(row) == {"contact_id", "contact_name", "phone", "channel",
                            "account_id", "account_name", "pinned", "updated_at"}
        assert row["pinned"] is True
        assert row["account_id"] == str(a.pk)

    def test_get_is_channel_scoped(self, client, superadmin):
        wa = _baileys("WA")
        tg = _telegram("Bot A")
        AccountAssignment.objects.create(contact=_contact(), channel="whatsapp",
                                         account=wa)
        AccountAssignment.objects.create(contact=_contact("Tan", "+60111000002"),
                                         channel="telegram", account=tg)

        data = client.get("/api/whatsapp/assignments?channel=telegram", **AUTH).json()
        assert data["channel"] == "telegram"
        assert data["channels"] == ["telegram", "whatsapp"]
        assert [row["id"] for row in data["accounts"]] == [str(tg.pk)]
        assert data["accounts"][0]["contacts"] == 1
        assert [row["channel"] for row in data["assignments"]] == ["telegram"]

        # the default view stays whatsapp-only — telegram rows never leak in
        data = client.get("/api/whatsapp/assignments", **AUTH).json()
        assert [row["id"] for row in data["accounts"]] == [str(wa.pk)]
        assert [row["channel"] for row in data["assignments"]] == ["whatsapp"]

    def test_put_gates(self, client, superadmin, agent_user):
        body = json.dumps({"contact_id": 1, "account_id": "1"})
        assert client.put("/api/whatsapp/assignments", data=body,
                          content_type="application/json").status_code == 401
        assert client.put("/api/whatsapp/assignments", data=body,
                          content_type="application/json",
                          **AGENT_AUTH).status_code == 403

    def test_put_set_and_clear(self, client, superadmin):
        a = _baileys()
        c = _contact()
        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": c.pk, "channel": "whatsapp",
                                        "account_id": str(a.pk), "pinned": True}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["pinned"] is True
        assert r.json()["account_id"] == str(a.pk)
        assert AccountAssignment.objects.get(contact=c).pinned is True

        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": c.pk, "channel": "whatsapp",
                                        "account_id": None}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json() == {"ok": True}
        assert not AccountAssignment.objects.filter(contact=c).exists()

    def test_put_telegram_assignment(self, client, superadmin):
        tg = _telegram()
        wa = _baileys()
        c = _contact()
        # the account must belong to the requested channel
        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": c.pk, "channel": "telegram",
                                        "account_id": str(wa.pk)}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 404
        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": c.pk, "channel": "telegram",
                                        "account_id": str(tg.pk), "pinned": True}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["channel"] == "telegram"
        asg = AccountAssignment.objects.get(contact=c, channel="telegram")
        assert asg.account_id == tg.pk and asg.pinned is True

    def test_put_env_default_rejected(self, client, superadmin):
        c = _contact()
        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": c.pk,
                                        "account_id": wa_config.ENV_ACCOUNT_ID}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_put_unknown_contact_404(self, client, superadmin):
        r = client.put("/api/whatsapp/assignments",
                       data=json.dumps({"contact_id": 99999, "account_id": "1"}),
                       content_type="application/json", **AUTH)
        assert r.status_code == 404


@pytest.mark.django_db
class TestRebalanceEndpoint:
    def test_gates(self, client, superadmin, agent_user, no_env_account):
        assert client.post("/api/whatsapp/assignments/rebalance").status_code == 401
        assert client.post("/api/whatsapp/assignments/rebalance",
                           **AGENT_AUTH).status_code == 403
        # no eligible accounts at all -> 409
        assert client.post("/api/whatsapp/assignments/rebalance",
                           **AUTH).status_code == 409

    def test_rebalance_evens_out_and_respects_pins(self, client, superadmin,
                                                   no_env_account):
        a, b = _baileys("A"), _baileys("B")
        contacts = [_contact(f"C{i}", f"+6011200000{i}") for i in range(5)]
        AccountAssignment.objects.create(contact=contacts[0], channel="whatsapp",
                                         account=a, pinned=True)
        for c in contacts[1:]:
            AccountAssignment.objects.create(contact=c, channel="whatsapp", account=a)
        r = client.post("/api/whatsapp/assignments/rebalance", **AUTH)
        assert r.status_code == 200
        data = r.json()
        assert data["moved"] == 2
        assert data["accounts"] == {str(a.pk): 3, str(b.pk): 2}
        pinned = AccountAssignment.objects.get(contact=contacts[0])
        assert pinned.account_id == a.pk and pinned.pinned is True
        counts = [AccountAssignment.objects.filter(account=x).count() for x in (a, b)]
        assert counts == [3, 2]

    def test_rebalance_assigns_thread_contacts(self, client, superadmin,
                                                no_env_account):
        _baileys("A")
        contact = _contact("Lim", f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID)  # links the contact by phone
        assert not AccountAssignment.objects.filter(contact=contact).exists()
        r = client.post("/api/whatsapp/assignments/rebalance", **AUTH)
        assert r.status_code == 200
        assert r.json()["moved"] == 1
        assert AccountAssignment.objects.filter(contact=contact).exists()

    def _rebalance(self, client, channel):
        return client.post("/api/whatsapp/assignments/rebalance",
                           data=json.dumps({"channel": channel}),
                           content_type="application/json", **AUTH)

    def test_rebalance_is_channel_scoped(self, client, superadmin, no_env_account):
        a, b = _telegram("Bot A"), _telegram("Bot B", token="456:def")
        wa = _baileys("WA")
        contacts = [_contact(f"T{i}", f"+6011300000{i}") for i in range(4)]
        for c in contacts:
            AccountAssignment.objects.create(contact=c, channel="telegram", account=a)
        # a whatsapp assignment on the same pool must not be disturbed
        AccountAssignment.objects.create(contact=contacts[0], channel="whatsapp",
                                         account=wa)
        r = self._rebalance(client, "telegram")
        assert r.status_code == 200
        data = r.json()
        assert data["moved"] == 2
        assert data["accounts"] == {str(a.pk): 2, str(b.pk): 2}
        assert AccountAssignment.objects.filter(channel="telegram",
                                                account=b).count() == 2
        assert AccountAssignment.objects.get(channel="whatsapp").account_id == wa.pk

    def test_rebalance_assigns_telegram_thread_contacts(self, client, superadmin,
                                                         no_env_account):
        bot = _telegram("Bot A")
        contact = _contact("Lim", "+60129990001")
        Thread.objects.create(channel="telegram", key="777", contact=contact)
        r = self._rebalance(client, "telegram")
        assert r.status_code == 200
        assert r.json()["moved"] == 1
        asg = AccountAssignment.objects.get(contact=contact, channel="telegram")
        assert asg.account_id == bot.pk

    def test_rebalance_unknown_channel_409(self, client, superadmin, no_env_account):
        _baileys("A")  # eligible, but only for whatsapp
        assert self._rebalance(client, "telegram").status_code == 409


# ---- account roles (reminder cannon vs conversation layer) --------------------------

def _set_role(account, role):
    account.config = {**(account.config or {}), "role": role}
    account.save(update_fields=["config"])
    return account


@pytest.mark.django_db
class TestAccountRoles:
    def test_role_defaults_by_provider(self):
        assert wa_config.account_role(_cloud()) == "reminder"
        assert wa_config.account_role(_baileys()) == "conversation"
        # dict form (serialized accounts) and garbage config values
        assert wa_config.account_role({"provider": "cloud"}) == "reminder"
        assert wa_config.account_role({"provider": "baileys"}) == "conversation"
        assert wa_config.account_role(_set_role(_cloud("X", "999"), "bogus")) == "reminder"
        assert wa_config.account_role(None) == "reminder"

    def test_explicit_role_wins_over_provider(self):
        assert wa_config.account_role(_set_role(_cloud("C", "111"),
                                                "conversation")) == "conversation"
        assert wa_config.account_role(_set_role(_baileys("B"), "reminder")) == "reminder"

    def test_accounts_api_surfaces_role(self, client, superadmin, no_env_account):
        a, b = _cloud("Biz", "111"), _baileys("Personal")
        by_id = {row["id"]: row for row in
                 client.get("/api/whatsapp/accounts", **AUTH).json()["accounts"]}
        assert by_id[str(a.pk)]["role"] == "reminder"
        assert by_id[str(b.pk)]["role"] == "conversation"

    def test_create_account_with_role(self, client, superadmin, no_env_account):
        r = client.post("/api/whatsapp/accounts",
                        data=json.dumps({"name": "QR", "provider": "baileys",
                                         "role": "reminder"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["role"] == "reminder"
        row = ChannelAccount.objects.get(name="QR")
        assert row.config["role"] == "reminder"

    def test_create_account_invalid_role_400(self, client, superadmin, no_env_account):
        r = client.post("/api/whatsapp/accounts",
                        data=json.dumps({"name": "QR", "provider": "baileys",
                                         "role": "broadcast"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_patch_role_no_health_reset(self, client, superadmin, no_env_account,
                                        monkeypatch):
        a = _cloud("Biz", "111")
        resets = []
        monkeypatch.setattr(health, "reset_on_reconfigure",
                            lambda pk: resets.append(pk))
        r = client.post(f"/api/whatsapp/accounts/{a.pk}",
                        data=json.dumps({"role": "conversation"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["role"] == "conversation"
        a.refresh_from_db()
        assert a.config["role"] == "conversation"
        assert resets == []  # role changes never reset the health verdict
        # invalid value is rejected before anything is written
        r = client.post(f"/api/whatsapp/accounts/{a.pk}",
                        data=json.dumps({"role": "nope"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_reminder_thread_ai_off_by_default(self, no_env_account):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        t = wa_store.get_or_create_thread("60110000001", account_id=str(biz.pk))
        assert t["ai_enabled"] is False  # the reminder cannon does no AI chat
        t = wa_store.get_or_create_thread("60110000002", account_id=str(qr.pk))
        assert t["ai_enabled"] is True
        t = wa_store.get_or_create_thread("60110000003")  # sandbox
        assert t["ai_enabled"] is True

    def test_first_attribution_to_reminder_turns_ai_off(self, no_env_account):
        # compose/pre-role threads start account-less with AI on; the FIRST
        # attribution is the creation-default moment that never happened —
        # landing on the reminder number retires the chatbot
        biz = _cloud("Biz", "111")
        wa_store.get_or_create_thread(WA_ID)  # pre-role thread, AI on
        t = wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        assert t["ai_enabled"] is False

    def test_ai_tick_is_per_account(self, no_env_account):
        # The Chatbot tick belongs to the SENDING ACCOUNT, per chat: a template
        # blast re-stamping the thread to the cannon shows the cannon's tick
        # (off), and the conversation number's tick is untouched — it comes
        # back the moment the chat returns there. Nothing is "killed".
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is True   # conversation default
        wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is False  # cannon default
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is True   # QR tick survived

    def test_operator_tick_sticks_per_account(self, no_env_account):
        # explicit operator choices are remembered per (chat, account) and
        # survive any number of sender switches
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        wa_store.update_thread(WA_ID, {"ai_enabled": True})   # AI ON on the cannon
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        wa_store.update_thread(WA_ID, {"ai_enabled": False})  # human takeover on QR
        wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is True   # cannon remembers ON
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is False  # QR remembers OFF

    def test_business_override_on_silences_redirect(self, no_env_account):
        # ticking Chatbot ON for a business-number chat: the AI answers there
        # and the redirect/template responder stands down for that chat
        biz = _cloud("Biz", "111")
        _contact(phone=f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        wa_store.update_thread(WA_ID, {"ai_enabled": True})
        assert wa_views._maybe_business_redirect(wa_store.load_thread(WA_ID)) is False
        assert OutboundJob.objects.count() == 0

    def test_role_change_to_reminder_flips_existing_threads(self, client, superadmin,
                                                            no_env_account):
        a = _set_role(_cloud("Biz", "111"), "conversation")
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))  # AI on
        wa_store.get_or_create_thread("60111222333", account_id=str(a.pk))
        wa_store.update_thread("60111222333", {"ai_enabled": False})  # manual off
        r = client.post(f"/api/whatsapp/accounts/{a.pk}",
                        data=json.dumps({"role": "reminder"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is False
        log = AuditLog.objects.get(action="account.role")
        assert "1 thread(s)" in log.detail  # only the flipped one is counted
        # flipping BACK to conversation never re-enables anything
        r = client.post(f"/api/whatsapp/accounts/{a.pk}",
                        data=json.dumps({"role": "conversation"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is False
        assert wa_store.load_thread("60111222333")["ai_enabled"] is False


# ---- role-aware routing --------------------------------------------------------------

@pytest.mark.django_db
class TestRoleRouting:
    def test_eligible_accounts_role_filter(self, no_env_account):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        conv_cloud = _set_role(_cloud("Conv Cloud", "222"), "conversation")
        assert [a.pk for a in routing.eligible_accounts(role="conversation")] == \
            [qr.pk, conv_cloud.pk]
        assert [a.pk for a in routing.eligible_accounts(role="reminder")] == [biz.pk]
        assert [a.pk for a in routing.eligible_accounts(
            provider="cloud", role="reminder")] == [biz.pk]

    def test_conversation_failover_ignores_cloud(self, no_env_account):
        a = _baileys("A")            # conversation (dies)
        _cloud("Biz", "111")         # reminder — must never catch the failover
        c2 = _baileys("C")           # the only valid target
        contact = _contact()
        assert routing.account_for_contact(contact, role="conversation").pk == a.pk
        health.set_status(a, "banned", "dead number")
        moved = routing.account_for_contact(contact, role="conversation")
        assert moved.pk == c2.pk
        assert AccountAssignment.objects.get(contact=contact).account_id == c2.pk

    def test_conversation_failover_without_target_returns_none(self, no_env_account):
        a = _baileys("A")
        _cloud("Biz", "111")  # healthy, but reminder-role
        contact = _contact()
        routing.account_for_contact(contact, role="conversation")
        health.set_status(a, "banned", "dead number")
        assert routing.account_for_contact(contact, role="conversation") is None
        # the assignment still points at the dead account — nothing eligible
        assert AccountAssignment.objects.get(contact=contact).account_id == a.pk

    def test_send_path_failover_is_role_constrained(self, client, superadmin,
                                                    no_env_account, no_async_ai,
                                                    monkeypatch):
        a = _baileys("A")
        _cloud("Biz", "111")  # eligible generally, wrong role
        c2 = _baileys("C")
        _contact("Ahmad", f"+{WA_ID}")
        wa_store.get_or_create_thread(WA_ID, account_id=str(a.pk))
        wa_store.append_message(WA_ID, "in", "hi", source="customer")
        health.set_status(a, "banned", "dead number")
        sent = {}
        monkeypatch.setattr(wa_client, "send_text",
                            lambda acct, to, text: sent.update(id=acct["id"]) or "ID9")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send",
                        data=json.dumps({"text": "hello"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert sent["id"] == str(c2.pk)  # conversation → conversation only

    def test_template_routing_prefers_reminder_role(self, client, superadmin,
                                                    no_env_account, no_quiet_hours,
                                                    monkeypatch):
        qr = _baileys("Personal")
        conv_cloud = _set_role(_cloud("Conv Cloud", "111"), "conversation")
        biz = _cloud("Biz", "222")  # reminder by default — must win
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        Thread.objects.filter(channel="whatsapp", key=WA_ID).update(contact=_contact())
        captured = []
        monkeypatch.setattr(
            "whatsapp.client.send_template",
            lambda acct, to, name, lang, variables: captured.append(acct) or "wamid.t1")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert captured[0]["id"] == str(biz.pk)  # NOT the lower-pk conversation cloud
        assert conv_cloud.pk < biz.pk  # the preference did the work, not pk order

    def test_template_routing_falls_back_to_any_cloud(self, client, superadmin,
                                                      no_env_account, no_quiet_hours,
                                                      monkeypatch):
        qr = _baileys("Personal")
        conv_cloud = _set_role(_cloud("Conv Cloud", "111"), "conversation")
        wa_store.get_or_create_thread(WA_ID, account_id=str(qr.pk))
        wa_store.append_message(WA_ID, "in", "hello", source="customer")
        captured = []
        monkeypatch.setattr(
            "whatsapp.client.send_template",
            lambda acct, to, name, lang, variables: captured.append(acct) or "wamid.t2")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/send-template",
                        data=json.dumps({"name": "order_confirmation"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert captured[0]["id"] == str(conv_cloud.pk)  # only cloud there is


# ---- business-number redirect responder ---------------------------------------------

@pytest.mark.django_db
class TestBusinessRedirect:
    def _reminder_thread(self, biz):
        """A contact writes in to the business (reminder) number: thread is
        created with AI off (role default) and an open window."""
        wa_store.get_or_create_thread(WA_ID, account_id=str(biz.pk))
        wa_store.append_message(WA_ID, "in", "how do I pay?", source="customer")
        return wa_store.load_thread(WA_ID)

    def test_redirect_enqueued_once(self, no_env_account):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        _contact(phone=f"+{WA_ID}")
        thread = self._reminder_thread(biz)
        assert thread["ai_enabled"] is False
        assert wa_views._maybe_business_redirect(thread) is True
        job = OutboundJob.objects.get()
        assert (job.channel, job.kind, job.status) == ("whatsapp", "text", "queued")
        assert job.to_address == WA_ID
        assert job.payload["source"] == "system"
        assert job.payload["redirect"] is True
        assert job.payload["account_id"] == str(biz.pk)  # answers from the biz number
        assert "wa.me/60199990000" in job.payload["text"]
        # dedupe: a second inbound while the job is queued adds nothing
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 1

    def test_redirect_sends_via_queue_and_throttles_24h(self, no_env_account,
                                                        monkeypatch):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        _contact(phone=f"+{WA_ID}")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        Setting.objects.create(key="queue_min_gap_s", value="0")
        Setting.objects.create(key="queue_max_gap_s", value="0")
        monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)
        sent = []
        monkeypatch.setattr(wa_client, "send_text",
                            lambda acct, to, text: sent.append((acct["id"], text)) or "R1")
        from flows import queue as flows_queue
        assert flows_queue.drain() == 1
        assert sent[0][0] == str(biz.pk)  # pinned: the number they wrote to
        msg = CrmMessage.objects.get(direction="out")
        assert msg.source == "system"
        assert "wa.me/60199990000" in msg.text
        # throttle: the sent system message blocks a re-send for 24h
        assert wa_views._maybe_business_redirect(wa_store.load_thread(WA_ID)) is False

    def test_redirect_respects_setting_off(self, no_env_account):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        Setting.objects.create(key="business_redirect", value="false")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 0

    def test_redirect_skipped_when_ai_on_or_conversation_role(self, no_env_account):
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        thread = self._reminder_thread(biz)
        wa_store.update_thread(WA_ID, {"ai_enabled": True})
        assert wa_views._maybe_business_redirect(wa_store.load_thread(WA_ID)) is False
        # a conversation-role thread never redirects (the AI lives there)
        t2 = wa_store.get_or_create_thread("60111222333", account_id=str(qr.pk))
        wa_store.update_thread("60111222333", {"ai_enabled": False})
        assert wa_views._maybe_business_redirect(
            wa_store.load_thread("60111222333")) is False
        assert OutboundJob.objects.count() == 0

    def test_redirect_needs_a_known_conversation_number(self, no_env_account):
        biz = _cloud("Biz", "111")
        _baileys("Personal")  # external_id empty — wa.me target unknown
        _contact(phone=f"+{WA_ID}")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 0

    def test_unknown_contact_gets_no_auto_answer(self, no_env_account):
        # coverage rule: a number that matches NO CRM contact gets nothing —
        # not the wa.me nudge, not the support text, not the template
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        thread = self._reminder_thread(biz)  # no _contact() created
        assert thread["contact_id"] is None
        assert wa_views._maybe_business_redirect(thread) is False
        Setting.objects.create(key="business_redirect_target", value="template")
        Setting.objects.create(key="business_redirect_template",
                               value="order_confirmation")
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 0

    def test_support_target_points_at_human_support(self, no_env_account):
        # business_redirect_target=support: no conversation number involved —
        # the reminder number names the human support contact instead
        biz = _cloud("Biz", "111")
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="support")
        Setting.objects.create(key="support_contact", value="+60 3-1234 5678")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        job = OutboundJob.objects.get()
        assert (job.channel, job.kind, job.status) == ("whatsapp", "text", "queued")
        assert job.payload["source"] == "system"
        assert job.payload["account_id"] == str(biz.pk)  # answers from the biz number
        assert wa_views.SUPPORT_MARKER in job.payload["text"]
        assert "+60 3-1234 5678" in job.payload["text"]
        assert "wa.me" not in job.payload["text"]
        # dedupe while queued, same as the conversation variant
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 1

    def test_support_target_works_without_contact_or_conversation_number(
            self, no_env_account):
        biz = _cloud("Biz", "111")  # NO conversation account exists at all
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="support")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        assert "for help" in OutboundJob.objects.get().payload["text"]

    def test_support_reply_throttles_24h(self, no_env_account, monkeypatch):
        biz = _cloud("Biz", "111")
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="support")
        Setting.objects.create(key="queue_min_gap_s", value="0")
        Setting.objects.create(key="queue_max_gap_s", value="0")
        monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "S1")
        from flows import queue as flows_queue
        assert flows_queue.drain() == 1
        # the sent support message blocks a re-send for 24h ("keep texting")
        assert wa_views._maybe_business_redirect(wa_store.load_thread(WA_ID)) is False

    def test_template_target_sends_the_operator_template(self, no_env_account):
        # business_redirect_target=template: the business number answers with
        # the operator's own approved template instead of a redirect text
        biz = _cloud("Biz", "111")
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="template")
        Setting.objects.create(key="business_redirect_template",
                               value="order_confirmation")
        thread = self._reminder_thread(biz)
        assert thread["ai_enabled"] is False  # reminder role: AI off by default
        assert wa_views._maybe_business_redirect(thread) is True
        job = OutboundJob.objects.get()
        assert (job.channel, job.kind, job.status) == ("whatsapp", "template", "queued")
        assert job.payload["name"] == "order_confirmation"
        assert job.payload["source"] == "system"
        assert job.payload["redirect"] is True
        assert job.payload["account_id"] == str(biz.pk)  # from the biz number
        # dedupe while queued, same as the text variants
        assert wa_views._maybe_business_redirect(thread) is False
        assert OutboundJob.objects.count() == 1

    def test_template_target_throttles_24h_after_send(self, no_env_account,
                                                      monkeypatch):
        # a SENT template job blocks a re-send for 24h even though its body
        # carries no marker text (the throttle reads job rows, not messages)
        biz = _cloud("Biz", "111")
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="template")
        Setting.objects.create(key="business_redirect_template",
                               value="order_confirmation")
        Setting.objects.create(key="queue_min_gap_s", value="0")
        Setting.objects.create(key="queue_max_gap_s", value="0")
        monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        sent = []
        monkeypatch.setattr(
            wa_client, "send_template",
            lambda acct, to, name, lang, variables: sent.append((acct["id"], name))
            or "wamid.bt1")
        from flows import queue as flows_queue
        assert flows_queue.drain() == 1
        assert sent == [(str(biz.pk), "order_confirmation")]
        assert wa_views._maybe_business_redirect(wa_store.load_thread(WA_ID)) is False
        assert OutboundJob.objects.count() == 1

    def test_template_target_without_template_falls_back_to_wa_me(
            self, no_env_account):
        # target=template but no template picked yet — degrade to the wa.me
        # conversation handoff so the contact still gets an answer
        biz = _cloud("Biz", "111")
        qr = _baileys("Personal")
        qr.external_id = "60199990000"
        qr.save(update_fields=["external_id"])
        _contact(phone=f"+{WA_ID}")
        Setting.objects.create(key="business_redirect_target", value="template")
        thread = self._reminder_thread(biz)
        assert wa_views._maybe_business_redirect(thread) is True
        job = OutboundJob.objects.get()
        assert job.kind == "text"
        assert "wa.me/60199990000" in job.payload["text"]

    def test_inbound_notification_still_fans_out(self, client, superadmin,
                                                 no_env_account, no_async_ai,
                                                 no_side_effects):
        # the redirect responder ADDS to, never replaces, the operator ping
        secret = "whsec"
        _cloud("Biz", "555000111", secret=secret)
        payload = {"entry": [{"changes": [{"field": "messages", "value": {
            "metadata": {"phone_number_id": "555000111"},
            "contacts": [{"profile": {"name": "T"}, "wa_id": WA_ID}],
            "messages": [{"from": WA_ID, "id": "wamid.r1", "type": "text",
                          "text": {"body": "how do I pay?"}}],
        }}]}]}
        body = json.dumps(payload).encode()
        sig = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
        r = client.post("/api/whatsapp/webhook", data=body,
                        content_type="application/json", HTTP_X_HUB_SIGNATURE_256=sig)
        assert r.status_code == 200
        assert Notification.objects.filter(user=superadmin, kind="message").count() == 1
        # …and the reminder-account thread came up with AI off (role default)
        assert wa_store.load_thread(WA_ID)["ai_enabled"] is False


# ---- recovery funnel -----------------------------------------------------------------

@pytest.mark.django_db
class TestRecovery:
    def _down_conversation_account(self, name="Personal", contacts=1):
        qr = _baileys(name)
        for i in range(contacts):
            AccountAssignment.objects.create(
                contact=_contact(f"C{i}", f"+6011180000{i}"),
                channel="whatsapp", account=qr)
        health.set_status(qr, "banned", "loggedOut (401)")
        return qr

    def test_get_lists_down_conversation_accounts(self, client, superadmin,
                                                  no_env_account):
        assert client.get("/api/whatsapp/recovery").status_code == 401
        qr = self._down_conversation_account(contacts=2)
        _baileys("Healthy")                            # up — not listed
        empty = _baileys("No contacts")                 # down but nobody assigned
        health.set_status(empty, "disconnected", "x")
        biz = _cloud("Biz", "111")                     # reminder — never listed
        AccountAssignment.objects.create(contact=_contact("R", "+60111999999"),
                                         channel="whatsapp", account=biz)
        health.set_status(biz, "disconnected", "token dead")
        data = client.get("/api/whatsapp/recovery", **AUTH).json()
        assert data["accounts"] == [{"account_id": str(qr.pk), "name": "Personal",
                                     "health": "banned", "affected": 2}]

    def test_post_gates_and_409s(self, client, superadmin, agent_user,
                                 no_env_account):
        qr = self._down_conversation_account()
        url = f"/api/whatsapp/recovery/{qr.pk}"
        assert client.post(url).status_code == 401
        assert client.post(url, **AGENT_AUTH).status_code == 403
        assert client.post("/api/whatsapp/recovery/99999", **AUTH).status_code == 404
        # no recovery_template configured
        r = client.post(url, **AUTH)
        assert r.status_code == 409
        assert "recovery_template" in r.json()["detail"]
        # template set, but no eligible Cloud account to carry it
        Setting.objects.create(key="recovery_template", value="order_confirmation")
        r = client.post(url, **AUTH)
        assert r.status_code == 409
        assert "Cloud" in r.json()["detail"]

    def test_post_enqueues_recovery_templates(self, client, superadmin,
                                              no_env_account):
        qr = self._down_conversation_account(contacts=2)
        _cloud("Biz", "111")
        Setting.objects.create(key="recovery_template", value="order_confirmation")
        r = client.post(f"/api/whatsapp/recovery/{qr.pk}", **AUTH)
        assert r.status_code == 200
        assert r.json() == {"queued": 2}
        jobs = list(OutboundJob.objects.all())
        assert all(j.kind == "template" and j.status == "queued" for j in jobs)
        assert all(j.payload["name"] == "order_confirmation" for j in jobs)
        assert AuditLog.objects.filter(action="recovery.start").exists()

    def test_down_transition_notifies_affected_count(self, superadmin,
                                                     no_env_account):
        qr = _baileys("Personal")
        AccountAssignment.objects.create(contact=_contact(), channel="whatsapp",
                                         account=qr)
        health.set_status(qr, "banned", "loggedOut (401)")
        note = Notification.objects.get(user=superadmin,
                                        title__contains="down — 1 contact")
        assert "Personal" in note.title
        assert note.link == "/settings?tab=assignments"
        # banned → disconnected is not a NEW down transition: no second ping
        health.set_status(qr, "disconnected", "gateway")
        assert Notification.objects.filter(user=superadmin,
                                           title__contains="contact(s) affected").count() == 1

    def test_auto_mode_enqueues_on_transition(self, superadmin, no_env_account):
        Setting.objects.create(key="recovery_mode", value="auto")
        Setting.objects.create(key="recovery_template", value="order_confirmation")
        _cloud("Biz", "111")
        qr = _baileys("Personal")
        for i in range(2):
            AccountAssignment.objects.create(
                contact=_contact(f"C{i}", f"+6011170000{i}"),
                channel="whatsapp", account=qr)
        health.set_status(qr, "banned", "loggedOut (401)")
        assert OutboundJob.objects.filter(kind="template", status="queued").count() == 2
        assert AuditLog.objects.filter(action="recovery.auto").exists()

    def test_one_click_mode_does_not_auto_enqueue(self, superadmin, no_env_account):
        Setting.objects.create(key="recovery_template", value="order_confirmation")
        _cloud("Biz", "111")
        qr = _baileys("Personal")
        AccountAssignment.objects.create(contact=_contact(), channel="whatsapp",
                                         account=qr)
        health.set_status(qr, "banned", "loggedOut (401)")
        assert OutboundJob.objects.count() == 0  # waits for the one-click POST

    def test_reminder_account_down_no_recovery_funnel(self, superadmin,
                                                      no_env_account):
        biz = _cloud("Biz", "111")
        AccountAssignment.objects.create(contact=_contact(), channel="whatsapp",
                                         account=biz)
        health.set_status(biz, "disconnected", "token dead")
        assert not Notification.objects.filter(
            title__contains="contact(s) affected").exists()


# ---- cold-text guard (manual sends) --------------------------------------------------

@pytest.mark.django_db
class TestColdTextGuard:
    URL = f"/api/whatsapp/threads/{WA_ID}/send"

    def _send(self, client, auth=AUTH, **extra):
        return client.post(self.URL, data=json.dumps({"text": "hello", **extra}),
                           content_type="application/json", **auth)

    def _cold_thread(self, account):
        wa_store.get_or_create_thread(WA_ID, account_id=str(account.pk))

    def test_block_default_and_admin_force(self, client, superadmin, agent_user,
                                           no_env_account, monkeypatch):
        self._cold_thread(_baileys("Personal"))
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "C1")
        r = self._send(client)
        assert r.status_code == 409
        assert "personal (conversation) number" in r.json()["detail"]
        # force needs admin+: an agent's force still bounces
        assert self._send(client, auth=AGENT_AUTH, force=True).status_code == 409
        r = self._send(client, force=True)  # superadmin
        assert r.status_code == 200

    def test_warn_sends_with_system_note(self, client, superadmin, no_env_account,
                                         monkeypatch):
        Setting.objects.create(key="cold_text_guard", value="warn")
        self._cold_thread(_baileys("Personal"))
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "C2")
        r = self._send(client)
        assert r.status_code == 200
        t = wa_store.load_thread(WA_ID)
        assert any(m["source"] == "system" and "Cold outreach warning" in m["text"]
                   for m in t["messages"])

    def test_allow_is_a_noop(self, client, superadmin, no_env_account, monkeypatch):
        Setting.objects.create(key="cold_text_guard", value="allow")
        self._cold_thread(_baileys("Personal"))
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "C3")
        r = self._send(client)
        assert r.status_code == 200
        assert not any("Cold outreach warning" in m["text"]
                       for m in wa_store.load_thread(WA_ID)["messages"])

    def test_reminder_role_unaffected(self, client, superadmin, no_env_account,
                                      monkeypatch):
        # role-based, not provider-based: a reminder-role Baileys number (no
        # 24h window in the way) may free-text a cold thread
        self._cold_thread(_set_role(_baileys("Biz QR"), "reminder"))
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "C4")
        assert self._send(client).status_code == 200

    def test_warm_thread_unaffected(self, client, superadmin, no_env_account,
                                    monkeypatch):
        self._cold_thread(_baileys("Personal"))
        wa_store.append_message(WA_ID, "in", "hi", source="customer")  # not cold
        monkeypatch.setattr(wa_client, "send_text", lambda acct, to, text: "C5")
        assert self._send(client).status_code == 200


# ---- cold-CALL guard (voice) ---------------------------------------------------------

@pytest.mark.django_db
class TestColdCallGuard:
    """A cold voice call is a stronger abuse signal than a cold text, so it has
    its own Setting — texts can run on "warn" while calls stay blocked."""

    def _thread_and_account(self, warm=False, opted_out=False):
        account = _baileys("Personal")
        wa_store.get_or_create_thread(WA_ID, account_id=str(account.pk))
        if warm:
            wa_store.append_message(WA_ID, "in", "hi", source="customer")
        if opted_out:
            wa_store.update_thread(WA_ID, {"opted_out": True})
        return wa_store.load_thread(WA_ID), wa_config._serialize(account)

    def _start(self, warm=False, opted_out=False, force=False):
        from whatsapp import baileys_calls
        thread, account = self._thread_and_account(warm, opted_out)
        return baileys_calls._compliance_error(WA_ID, thread, account, force)

    def test_cold_call_blocked_by_default(self):
        err = self._start()
        assert err and "never messaged" in err

    def test_warm_thread_allowed(self):
        assert self._start(warm=True) is None

    def test_admin_force_overrides_cold(self):
        assert self._start(force=True) is None

    def test_opt_out_blocks_even_with_force(self):
        """Opt-out is absolute — ringing someone who sent STOP is the loudest
        possible breach of the consent that already blocks AI text."""
        err = self._start(warm=True, opted_out=True, force=True)
        assert err and "opted out" in err.lower()

    def test_warn_lets_the_call_through_with_a_note(self):
        Setting.objects.create(key="cold_call_guard", value="warn")
        assert self._start() is None
        assert any(m["source"] == "system" and "Cold call" in m["text"]
                   for m in wa_store.load_thread(WA_ID)["messages"])

    def test_allow_is_a_noop(self):
        Setting.objects.create(key="cold_call_guard", value="allow")
        assert self._start() is None
        assert not any("Cold call" in m["text"]
                       for m in wa_store.load_thread(WA_ID)["messages"])

    def test_no_thread_at_all_is_the_coldest_case(self):
        """A contact with no WhatsApp thread has never messaged ANY of our
        numbers — treating that as "unmeasurable, so allow" let the riskiest
        call of all straight through."""
        from whatsapp import baileys_calls
        account = _baileys("Personal")
        err = baileys_calls._compliance_error(
            "60999888777", {}, wa_config._serialize(account), False)
        assert err and "never messaged" in err


# ---- routing-rules endpoint ----------------------------------------------------------

@pytest.mark.django_db
class TestRoutingRulesEndpoint:
    URL = "/api/whatsapp/routing-rules"

    def _put(self, client, body, auth=AUTH):
        return client.put(self.URL, data=json.dumps(body),
                          content_type="application/json", **auth)

    def test_get_defaults_any_role(self, client, superadmin, agent_user,
                                   monkeypatch):
        monkeypatch.delenv("QUEUE_MIN_GAP_S", raising=False)
        monkeypatch.delenv("QUEUE_MAX_GAP_S", raising=False)
        assert client.get(self.URL).status_code == 401
        r = client.get(self.URL, **AGENT_AUTH)
        assert r.status_code == 200
        assert r.json() == {"ai_reply_mode": "queued", "business_redirect": True,
                            "business_redirect_target": "conversation",
                            "support_contact": "",
                            "business_redirect_template": "",
                            "recovery_mode": "one_click", "recovery_template": "",
                            "cold_text_guard": "block",
                            "cold_call_guard": "block",
                            "queue_min_gap_s": 10, "queue_max_gap_s": 30}

    def test_put_gates(self, client, superadmin, agent_user):
        assert self._put(client, {"ai_reply_mode": "instant"},
                         auth={}).status_code == 401
        assert self._put(client, {"ai_reply_mode": "instant"},
                         auth=AGENT_AUTH).status_code == 403

    def test_put_roundtrip_and_audit(self, client, superadmin):
        r = self._put(client, {"ai_reply_mode": "instant",
                               "business_redirect": False,
                               "business_redirect_target": "support",
                               "support_contact": "+60 3-1234 5678",
                               "business_redirect_template": "hello_world",
                               "recovery_mode": "auto",
                               "recovery_template": "order_confirmation",
                               "cold_text_guard": "warn",
                               "cold_call_guard": "allow",
                               "queue_min_gap_s": 45, "queue_max_gap_s": 180})
        assert r.status_code == 200
        assert r.json() == {"ai_reply_mode": "instant", "business_redirect": False,
                            "business_redirect_target": "support",
                            "support_contact": "+60 3-1234 5678",
                            "business_redirect_template": "hello_world",
                            "recovery_mode": "auto",
                            "recovery_template": "order_confirmation",
                            "cold_text_guard": "warn",
                            "cold_call_guard": "allow",
                            "queue_min_gap_s": 45, "queue_max_gap_s": 180}
        # persisted: a fresh GET reads the Setting rows back
        assert client.get(self.URL, **AUTH).json()["cold_text_guard"] == "warn"
        assert wa_config.ai_reply_mode() == "instant"
        assert wa_config.business_redirect_enabled() is False
        assert wa_config.business_redirect_target() == "support"
        assert wa_config.support_contact() == "+60 3-1234 5678"
        assert wa_config.business_redirect_template() == "hello_world"
        assert wa_config.recovery_mode() == "auto"
        assert wa_config.recovery_template() == "order_confirmation"
        cfg = flows_queue.queue_settings()
        assert (cfg["queue_min_gap_s"], cfg["queue_max_gap_s"]) == (45, 180)
        log = AuditLog.objects.get(action="settings.update", target="routing-rules")
        assert "ai_reply_mode" in log.detail
        assert "queue_min_gap_s" in log.detail

    def test_put_validation(self, client, superadmin):
        assert self._put(client, {"ai_reply_mode": "sometimes"}).status_code == 400
        assert self._put(client, {"recovery_mode": "yolo"}).status_code == 400
        assert self._put(client, {"cold_text_guard": "maybe"}).status_code == 400
        assert self._put(client, {"business_redirect": "sort-of"}).status_code == 400
        assert self._put(client, {"business_redirect_target": "nowhere"}).status_code == 400
        assert self._put(client, {"support_contact": 42}).status_code == 400
        assert self._put(client, {"recovery_template": 42}).status_code == 400
        assert self._put(client, {"business_redirect_template": 42}).status_code == 400
        # nothing was persisted by the rejected writes
        assert client.get(self.URL, **AUTH).json()["ai_reply_mode"] == "queued"

    def test_put_queue_gap_validation(self, client, superadmin):
        assert self._put(client, {"queue_min_gap_s": -1}).status_code == 400
        assert self._put(client, {"queue_min_gap_s": "soon"}).status_code == 400
        assert self._put(client, {"queue_max_gap_s": None}).status_code == 400
        # min > max rejected in one PUT…
        assert self._put(client, {"queue_min_gap_s": 60,
                                  "queue_max_gap_s": 30}).status_code == 400
        # …and against the STORED other bound when patched alone
        assert self._put(client, {"queue_min_gap_s": 5,
                                  "queue_max_gap_s": 20}).status_code == 200
        assert self._put(client, {"queue_min_gap_s": 25}).status_code == 400
        assert self._put(client, {"queue_max_gap_s": 3}).status_code == 400
        # rejected writes persisted nothing
        cfg = flows_queue.queue_settings()
        assert (cfg["queue_min_gap_s"], cfg["queue_max_gap_s"]) == (5, 20)
        # string digits accepted (form inputs send strings)
        assert self._put(client, {"queue_max_gap_s": "90"}).status_code == 200
        assert flows_queue.queue_settings()["queue_max_gap_s"] == 90

    def test_bad_stored_value_falls_back_to_default(self, db):
        Setting.objects.create(key="ai_reply_mode", value="garbage")
        assert wa_config.ai_reply_mode() == "queued"
