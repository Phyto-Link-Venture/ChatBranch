"""Baileys (unofficial WhatsApp Web) provider: account creation without Meta
credentials, gateway-secured inbound push, send routing through the gateway,
and the 24h-window exemption.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm.models import ChannelAccount, Profile, Thread
from whatsapp import client as wa_client
from whatsapp import store as wa_store
from whatsapp import views as wa_views
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
SECRET_HDR = {"HTTP_X_BAILEYS_SECRET": wa_config.BAILEYS_SECRET}


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def no_env_account(monkeypatch):
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def quiet_hooks(monkeypatch):
    monkeypatch.setattr(wa_views, "_ai_reply_async", lambda wa_id: None)
    monkeypatch.setattr(wa_views.flows_engine, "on_inbound_async",
                        lambda channel, key, text: None)


@pytest.fixture
def baileys_account(db):
    return ChannelAccount.objects.create(channel="whatsapp", provider="baileys",
                                         name="QR Number")


def _inbound(client, wa_id="60111222333", text="hello", msg_id="B1", account_id="", **extra):
    return client.post("/api/whatsapp/baileys/inbound",
                       data=json.dumps({"account_id": account_id, "wa_id": wa_id,
                                        "name": "Aina", "text": text, "msg_id": msg_id}),
                       content_type="application/json", **{**SECRET_HDR, **extra})


@pytest.mark.django_db
class TestBaileysAccounts:
    def test_create_without_meta_credentials(self, client, superadmin):
        r = client.post("/api/whatsapp/accounts",
                        data=json.dumps({"name": "QR Number", "provider": "baileys"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        body = r.json()
        assert body["provider"] == "baileys"
        assert body["configured"] is True  # no token needed

    def test_cloud_still_requires_credentials(self, client, superadmin):
        r = client.post("/api/whatsapp/accounts",
                        data=json.dumps({"name": "Cloud", "provider": "cloud"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_control_proxy_rejects_cloud_accounts(self, client, superadmin):
        acct = ChannelAccount.objects.create(channel="whatsapp", provider="cloud",
                                             name="c", external_id="1", access_token="t")
        r = client.get(f"/api/whatsapp/accounts/{acct.pk}/baileys/status", **AUTH)
        assert r.status_code == 404


@pytest.mark.django_db
class TestBaileysInbound:
    def test_bad_secret_403(self, client, quiet_hooks):
        r = client.post("/api/whatsapp/baileys/inbound", data="{}",
                        content_type="application/json",
                        HTTP_X_BAILEYS_SECRET="wrong")
        assert r.status_code == 403

    def test_inbound_creates_thread_and_message(self, client, baileys_account, quiet_hooks):
        r = _inbound(client, account_id=str(baileys_account.pk))
        assert r.status_code == 200
        t = Thread.objects.get(channel="whatsapp", key="60111222333")
        assert t.name == "Aina"
        assert t.account_id == baileys_account.pk
        assert t.messages.get().text == "hello"

    def test_redelivery_dedupes(self, client, baileys_account, quiet_hooks):
        _inbound(client, msg_id="B7", account_id=str(baileys_account.pk))
        r = _inbound(client, msg_id="B7", account_id=str(baileys_account.pk))
        assert r.json().get("duplicate") is True
        assert Thread.objects.get(key="60111222333").messages.count() == 1

    def test_opt_out_applies(self, client, baileys_account, quiet_hooks):
        _inbound(client, text="STOP", msg_id="B9", account_id=str(baileys_account.pk))
        assert Thread.objects.get(key="60111222333").opted_out is True


@pytest.mark.django_db
class TestBaileysSendAndWindow:
    def test_send_routes_through_gateway(self, client, superadmin, no_env_account,
                                         baileys_account, quiet_hooks, monkeypatch):
        sent = {}

        def fake_gateway(path, payload=None, method="POST"):
            sent.update(path=path, payload=payload)
            return {"id": "BAIL-1"}

        monkeypatch.setattr(wa_client, "baileys_request", fake_gateway)
        client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": "+60111222333"}),
                    content_type="application/json", **AUTH)
        # inbound-first (client model): a contact-initiated thread — free
        # text is fine; a cold personal-number thread would hit the guard
        wa_store.append_message("60111222333", "in", "hi", source="customer")
        r = client.post("/api/whatsapp/threads/60111222333/send",
                        data=json.dumps({"text": "bayar sini"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert sent["path"] == f"/accounts/{baileys_account.pk}/send"
        assert sent["payload"] == {"to": "60111222333", "text": "bayar sini"}

    def test_no_24h_window_for_baileys_threads(self, client, superadmin, no_env_account,
                                               baileys_account, quiet_hooks):
        client.post("/api/whatsapp/threads",
                    data=json.dumps({"phone": "+60111222333"}),
                    content_type="application/json", **AUTH)
        detail = client.get("/api/whatsapp/threads/60111222333", **AUTH).json()
        # cloud accounts with no inbound message would show window_open=False
        assert detail["window_open"] is True
        assert detail["window_expires_at"] is None

    def test_templates_empty_for_baileys(self, superadmin, baileys_account):
        assert wa_client.list_templates(wa_config.get_account(str(baileys_account.pk))) == []


# ---- inbound media push (gateway downloads, Django files + attaches) ----------------

@pytest.mark.django_db
class TestBaileysInboundMedia:
    def test_bad_secret_403(self, client):
        r = client.post("/api/whatsapp/baileys/media", data={},
                        HTTP_X_BAILEYS_SECRET="wrong")
        assert r.status_code == 403

    def test_media_push_attaches_to_message(self, client, superadmin, baileys_account,
                                            quiet_hooks, tmp_path, monkeypatch):
        monkeypatch.setattr(wa_store, "MEDIA_DIR", tmp_path)
        _inbound(client, wa_id="60111222333", text="[image]", msg_id="B9",
                 account_id=str(baileys_account.pk))
        from django.core.files.uploadedfile import SimpleUploadedFile
        upload = SimpleUploadedFile("receipt.jpg", b"JPEGDATA", content_type="image/jpeg")
        r = client.post("/api/whatsapp/baileys/media",
                        data={"wa_id": "60111222333", "msg_id": "B9",
                              "media_type": "image", "file": upload}, **SECRET_HDR)
        assert r.status_code == 200
        msg = next(m for m in wa_store.load_thread("60111222333")["messages"]
                   if m["id"] == "B9")
        assert msg["media"]["type"] == "image"
        assert msg["media"]["url"].startswith("/api/whatsapp/media/")
        assert (tmp_path / "60111222333" / "receipt.jpg").read_bytes() == b"JPEGDATA"
        # and the whatsapp media endpoint streams it back auth-gated
        pk = msg["media"]["url"].rsplit("/", 1)[-1]
        served = client.get(f"/api/whatsapp/media/{pk}", **AUTH)
        assert served.status_code == 200
        assert b"".join(served.streaming_content) == b"JPEGDATA"

    def test_unknown_message_is_noop(self, client, baileys_account, tmp_path,
                                     monkeypatch):
        monkeypatch.setattr(wa_store, "MEDIA_DIR", tmp_path)
        from django.core.files.uploadedfile import SimpleUploadedFile
        upload = SimpleUploadedFile("x.jpg", b"X", content_type="image/jpeg")
        r = client.post("/api/whatsapp/baileys/media",
                        data={"wa_id": "60999888777", "msg_id": "nope",
                              "media_type": "image", "file": upload}, **SECRET_HDR)
        assert r.status_code == 200  # tolerated — the file is orphaned, no crash
