"""WhatsApp module security hardening tests.

Covers: endpoint auth (401 without a bearer token once Profiles exist),
authenticated sandbox sending, webhook wamid dedup, webhook signature
enforcement when an account is configured, the verify-token auto-generation,
the get_account fallback, and the call double-start guard.
"""
import hashlib
import hmac
import json

import pytest
from django.contrib.auth.models import User

from crm.models import ChannelAccount, Message, Profile, Setting, Thread
from whatsapp import views as wa_views
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}


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


def _webhook_payload(wamid="wamid.test.1", text="hello", wa_id="60123456789", pnid=""):
    value = {
        "contacts": [{"profile": {"name": "Tester"}, "wa_id": wa_id}],
        "messages": [{"from": wa_id, "id": wamid, "type": "text", "text": {"body": text}}],
    }
    if pnid:
        value["metadata"] = {"phone_number_id": pnid}
    return {"entry": [{"changes": [{"field": "messages", "value": value}]}]}


@pytest.mark.django_db
class TestAuthRequired:
    """Once a Profile row exists, every endpoint except webhook/status is 401
    without a bearer token (the X-Role bootstrap header stops working)."""

    def test_threads_401_without_token(self, client, superadmin):
        assert client.get("/api/whatsapp/threads").status_code == 401

    def test_send_401_without_token(self, client, superadmin):
        r = client.post("/api/whatsapp/threads/60123456789/send",
                        data=json.dumps({"text": "hi"}), content_type="application/json")
        assert r.status_code == 401

    def test_call_start_401_without_token(self, client, superadmin):
        assert client.post("/api/whatsapp/threads/60123456789/call/start").status_code == 401

    def test_x_role_header_rejected_once_profiles_exist(self, client, superadmin):
        r = client.get("/api/whatsapp/threads", HTTP_X_ROLE="superadmin")
        assert r.status_code == 401

    def test_status_stays_open(self, client, superadmin):
        assert client.get("/api/whatsapp/status").status_code == 200


@pytest.mark.django_db
class TestSandboxSend:
    def test_authed_sandbox_send_works(self, client, superadmin, no_env_account):
        r = client.post("/api/whatsapp/threads",
                        data=json.dumps({"phone": "+60123456789", "name": "Tester"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        r = client.post("/api/whatsapp/threads/60123456789/send",
                        data=json.dumps({"text": "hello there"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        msg = r.json()["message"]
        assert msg["text"] == "hello there"
        assert msg["status"] == "sandbox"  # no account configured -> stored locally


@pytest.mark.django_db
class TestWebhookDedup:
    def test_same_wamid_stored_once(self, client, no_env_account, no_async_ai):
        body = json.dumps(_webhook_payload(wamid="wamid.dup.1"))
        for _ in range(2):  # Meta redelivery of the exact same event
            r = client.post("/api/whatsapp/webhook", data=body, content_type="application/json")
            assert r.status_code == 200
        assert Message.objects.filter(external_id="wamid.dup.1").count() == 1

    def test_distinct_wamids_both_stored(self, client, no_env_account, no_async_ai):
        for wamid in ("wamid.a", "wamid.b"):
            client.post("/api/whatsapp/webhook",
                        data=json.dumps(_webhook_payload(wamid=wamid)),
                        content_type="application/json")
        assert Message.objects.filter(external_id__startswith="wamid.").count() == 2

    def test_malformed_change_does_not_500(self, client, no_env_account, no_async_ai):
        payload = {"entry": [{"changes": [
            {"field": "messages", "value": {"messages": "not-a-list"}},  # would crash
            {"field": "messages", "value": _webhook_payload()["entry"][0]["changes"][0]["value"]},
        ]}]}
        r = client.post("/api/whatsapp/webhook", data=json.dumps(payload),
                        content_type="application/json")
        assert r.status_code == 200  # bad event isolated, good one still processed
        assert Message.objects.filter(external_id="wamid.test.1").count() == 1


@pytest.mark.django_db
class TestWebhookSignature:
    PNID = "1234567890"
    SECRET = "s3cret-app-secret"

    def _account(self, app_secret=SECRET):
        return ChannelAccount.objects.create(
            channel="whatsapp", name="Live", external_id=self.PNID,
            access_token="tok", app_secret=app_secret,
        )

    def test_configured_bad_signature_403(self, client, no_env_account, no_async_ai):
        self._account()
        body = json.dumps(_webhook_payload(pnid=self.PNID))
        r = client.post("/api/whatsapp/webhook", data=body, content_type="application/json",
                        HTTP_X_HUB_SIGNATURE_256="sha256=deadbeef")
        assert r.status_code == 403

    def test_configured_missing_signature_403(self, client, no_env_account, no_async_ai):
        self._account()
        r = client.post("/api/whatsapp/webhook",
                        data=json.dumps(_webhook_payload(pnid=self.PNID)),
                        content_type="application/json")
        assert r.status_code == 403

    def test_configured_without_app_secret_403(self, client, no_env_account, no_async_ai):
        # a live account MUST set app_secret — unsigned webhooks are rejected
        self._account(app_secret="")
        r = client.post("/api/whatsapp/webhook",
                        data=json.dumps(_webhook_payload(pnid=self.PNID)),
                        content_type="application/json")
        assert r.status_code == 403

    def test_configured_valid_signature_200(self, client, no_env_account, no_async_ai):
        self._account()
        body = json.dumps(_webhook_payload(pnid=self.PNID)).encode()
        sig = "sha256=" + hmac.new(self.SECRET.encode(), body, hashlib.sha256).hexdigest()
        r = client.post("/api/whatsapp/webhook", data=body, content_type="application/json",
                        HTTP_X_HUB_SIGNATURE_256=sig)
        assert r.status_code == 200
        assert Message.objects.filter(external_id="wamid.test.1").count() == 1

    def test_sandbox_stays_permissive(self, client, no_env_account, no_async_ai):
        r = client.post("/api/whatsapp/webhook", data=json.dumps(_webhook_payload()),
                        content_type="application/json")
        assert r.status_code == 200

    def test_unknown_phone_number_id_skipped(self, client, no_env_account, no_async_ai):
        self._account()
        body = json.dumps(_webhook_payload(pnid="999999999")).encode()
        sig = "sha256=" + hmac.new(self.SECRET.encode(), body, hashlib.sha256).hexdigest()
        r = client.post("/api/whatsapp/webhook", data=body, content_type="application/json",
                        HTTP_X_HUB_SIGNATURE_256=sig)
        assert r.status_code == 200  # acknowledged but not misattributed
        assert Thread.objects.count() == 0


@pytest.mark.django_db
class TestVerifyToken:
    def test_default_token_is_replaced_with_generated_secret(self, client, monkeypatch,
                                                             no_env_account):
        monkeypatch.setitem(wa_config._GLOBAL_ENV, "verify_token", wa_config.DEFAULT_VERIFY_TOKEN)
        r = client.get("/api/whatsapp/webhook", {
            "hub.mode": "subscribe",
            "hub.verify_token": wa_config.DEFAULT_VERIFY_TOKEN,
            "hub.challenge": "42",
        })
        assert r.status_code == 403  # the shipped default must never verify
        row = Setting.objects.get(key="verify_token")
        assert row.value and row.value != wa_config.DEFAULT_VERIFY_TOKEN

        r = client.get("/api/whatsapp/webhook", {
            "hub.mode": "subscribe", "hub.verify_token": row.value, "hub.challenge": "42",
        })
        assert r.status_code == 200
        assert r.content == b"42"


@pytest.mark.django_db
class TestGetAccountFallback:
    def test_no_accounts_returns_none(self, no_env_account):
        assert wa_config.get_account(None) is None

    def test_sole_configured_account_is_default(self, no_env_account):
        a = ChannelAccount.objects.create(channel="whatsapp", name="Only",
                                          external_id="111", access_token="tok")
        assert wa_config.get_account(None)["id"] == str(a.pk)

    def test_multiple_configured_accounts_no_guess(self, no_env_account):
        for i in (1, 2):
            ChannelAccount.objects.create(channel="whatsapp", name=f"A{i}",
                                          external_id=str(i), access_token="tok")
        assert wa_config.get_account(None) is None

    def test_unconfigured_account_not_used_as_default(self, no_env_account):
        ChannelAccount.objects.create(channel="whatsapp", name="Draft",
                                      external_id="111", access_token="")
        assert wa_config.get_account(None) is None


@pytest.mark.django_db
class TestCallDoubleStart:
    def test_second_start_conflicts(self, client, superadmin, no_env_account):
        client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": "+60123456789"}),
                    content_type="application/json", **AUTH)
        r1 = client.post("/api/whatsapp/threads/60123456789/call/start", **AUTH)
        assert r1.status_code == 200
        assert r1.json()["status"] == "in_call"  # sandbox lifecycle
        r2 = client.post("/api/whatsapp/threads/60123456789/call/start", **AUTH)
        assert r2.status_code == 409

    def test_start_works_again_after_end(self, client, superadmin, no_env_account):
        client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": "+60123456789"}),
                    content_type="application/json", **AUTH)
        client.post("/api/whatsapp/threads/60123456789/call/start", **AUTH)
        client.post("/api/whatsapp/threads/60123456789/call/end", **AUTH)
        r = client.post("/api/whatsapp/threads/60123456789/call/start", **AUTH)
        assert r.status_code == 200
