"""Communication-hub endpoint tests — GET /api/contacts/<id>/channels.

Covers: the bearer-auth gate, response shape, whatsapp identity aggregation
(message/inbound/outbound counts, last_direction, last_active), the
digits-endswith phone fallback for threads created before the CRM link,
account health -> connected logic, email-on-file with no threads, and the
voice section built from CallRecord (FK-linked + phone-matched rows).

Same fixture pattern as tests/test_whatsapp_features.py.
"""
from datetime import timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from crm.models import CallRecord, ChannelAccount, Contact, Message, Profile, Thread
from messaging import msg_config
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
WA_ID = "60123456789"

IDENTITY_KEYS = {
    "key", "address", "name", "account_id", "account_name", "account_provider",
    "account_health", "last_active", "message_count", "inbound_count",
    "outbound_count", "last_direction", "opted_out", "window_open",
}


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def no_env_account(monkeypatch):
    """A developer's real WHATSAPP_* .env keys must not flip sandbox tests."""
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def contact(db):
    return Contact.objects.create(
        name="Ahmad Faiz", phone="+60 12-345 6789", email="ahmad@example.com")


def _get(client, cid):
    return client.get(f"/api/contacts/{cid}/channels", **AUTH)


def _channel(payload, name):
    return next((c for c in payload["channels"] if c["channel"] == name), None)


def _msg(thread, direction, text, ts=None, source=None):
    m = Message.objects.create(
        thread=thread, direction=direction,
        source=source or ("customer" if direction == "in" else "agent"), text=text)
    if ts is not None:  # auto_now_add can't be set at create time
        Message.objects.filter(pk=m.pk).update(created_at=ts)
    return m


# ---- auth gate -------------------------------------------------------------------

@pytest.mark.django_db
class TestAuth:
    def test_requires_bearer(self, client, superadmin, contact):
        assert client.get(f"/api/contacts/{contact.pk}/channels").status_code == 401

    def test_bad_token_rejected(self, client, superadmin, contact):
        r = client.get(f"/api/contacts/{contact.pk}/channels",
                       HTTP_AUTHORIZATION="Bearer wrong-token")
        assert r.status_code == 401

    def test_get_and_post_only(self, client, superadmin, contact):
        r = client.put(f"/api/contacts/{contact.pk}/channels", **AUTH)
        assert r.status_code == 405
        # POST is the add-identity seam — an empty body is a 400, not a 405
        r = client.post(f"/api/contacts/{contact.pk}/channels", **AUTH)
        assert r.status_code == 400

    def test_unknown_customer_404(self, client, superadmin):
        assert _get(client, 999999).status_code == 404


# ---- response shape --------------------------------------------------------------

@pytest.mark.django_db
class TestShape:
    def test_top_level_and_voice_always_last(self, client, superadmin, no_env_account,
                                              contact):
        r = _get(client, contact.pk)
        assert r.status_code == 200
        data = r.json()
        assert data["contact_id"] == contact.pk
        assert data["name"] == "Ahmad Faiz"
        assert data["phone"] == "+60 12-345 6789"
        assert data["email"] == "ahmad@example.com"
        # no threads yet: only email (address on file) + voice appear
        assert [c["channel"] for c in data["channels"]] == ["email", "voice"]
        voice = data["channels"][-1]
        assert voice["connected"] is True  # phone on file
        assert voice["calls"] == 0
        assert voice["last_call"] is None
        assert voice["last_outcome"] is None

    def test_channels_without_threads_are_omitted(self, client, superadmin,
                                                  no_env_account):
        c = Contact.objects.create(name="No Mail", phone="+60111222333")
        data = _get(client, c.pk).json()
        assert [ch["channel"] for ch in data["channels"]] == ["voice"]


# ---- whatsapp identity aggregation -----------------------------------------------

@pytest.mark.django_db
class TestWhatsAppIdentities:
    def test_counts_last_direction_and_sandbox_defaults(self, client, superadmin,
                                                        no_env_account, contact):
        t = Thread.objects.create(channel="whatsapp", key=WA_ID,
                                  address=f"+{WA_ID}", name="Ahmad", contact=contact)
        now = timezone.now()
        _msg(t, "out", "hello", now - timedelta(hours=3))
        _msg(t, "in", "hi", now - timedelta(hours=2))
        _msg(t, "in", "can pay friday", now - timedelta(hours=1))

        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert wa is not None
        assert len(wa["identities"]) == 1
        ident = wa["identities"][0]
        assert set(ident) == IDENTITY_KEYS
        assert ident["key"] == WA_ID
        assert ident["address"] == f"+{WA_ID}"
        assert ident["message_count"] == 3
        assert ident["inbound_count"] == 2
        assert ident["outbound_count"] == 1
        assert ident["last_direction"] == "in"
        assert ident["last_active"] is not None
        assert wa["last_active"] == ident["last_active"]
        assert ident["opted_out"] is False
        # sandbox thread: no account resolves -> unknown health, not connected,
        # and the 24h window never closes (nothing real is sent)
        assert ident["account_id"] is None
        assert ident["account_health"] == "unknown"
        assert ident["window_open"] is True
        assert wa["connected"] is False
        assert wa["linked_accounts"] == 0

    def test_phone_fallback_links_pre_link_threads(self, client, superadmin,
                                                   no_env_account, contact):
        # created before the CRM link existed — contact FK is NULL
        t = Thread.objects.create(channel="whatsapp", key=WA_ID, address=f"+{WA_ID}")
        _msg(t, "in", "hello")
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert wa is not None
        assert [i["key"] for i in wa["identities"]] == [WA_ID]

    def test_phone_fallback_ignores_other_numbers(self, client, superadmin,
                                                  no_env_account, contact):
        Thread.objects.create(channel="whatsapp", key="60987654321")
        data = _get(client, contact.pk).json()
        assert _channel(data, "whatsapp") is None

    def test_identities_sorted_newest_first(self, client, superadmin,
                                            no_env_account, contact):
        old = Thread.objects.create(channel="whatsapp", key=WA_ID,
                                    address=f"+{WA_ID}", contact=contact)
        fresh = Thread.objects.create(channel="whatsapp", key="60777888999",
                                      address="+60777888999", contact=contact)
        now = timezone.now()
        _msg(old, "in", "old", now - timedelta(days=3))
        _msg(fresh, "in", "new", now - timedelta(hours=1))
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert [i["key"] for i in wa["identities"]] == ["60777888999", WA_ID]


# ---- account health -> connected --------------------------------------------------

@pytest.mark.django_db
class TestAccountsAndConnected:
    def test_healthy_baileys_account(self, client, superadmin, no_env_account,
                                     contact):
        acct = ChannelAccount.objects.create(channel="whatsapp", provider="baileys",
                                             name="QR Number", health_status="healthy")
        Thread.objects.create(channel="whatsapp", key=WA_ID, address=f"+{WA_ID}",
                              contact=contact, account=acct)
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        ident = wa["identities"][0]
        assert ident["account_id"] == str(acct.pk)
        assert ident["account_name"] == "QR Number"
        assert ident["account_provider"] == "baileys"
        assert ident["account_health"] == "healthy"
        assert ident["window_open"] is True  # Meta's 24h window n/a over WhatsApp Web
        assert wa["connected"] is True
        assert wa["linked_accounts"] == 1

    def test_banned_account_is_not_connected(self, client, superadmin,
                                             no_env_account, contact):
        acct = ChannelAccount.objects.create(channel="whatsapp", provider="baileys",
                                             name="Dead", health_status="banned")
        Thread.objects.create(channel="whatsapp", key=WA_ID, address=f"+{WA_ID}",
                              contact=contact, account=acct)
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert wa["identities"][0]["account_health"] == "banned"
        assert wa["connected"] is False

    def test_cloud_window_follows_last_inbound(self, client, superadmin,
                                               no_env_account, contact):
        acct = ChannelAccount.objects.create(
            channel="whatsapp", provider="cloud", name="Main",
            external_id="123456", access_token="tok", health_status="healthy")
        t = Thread.objects.create(channel="whatsapp", key=WA_ID, address=f"+{WA_ID}",
                                  contact=contact, account=acct)
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        # live account but the contact never wrote in -> template territory
        assert wa["identities"][0]["window_open"] is False
        assert wa["connected"] is True

        _msg(t, "in", "hi")  # fresh inbound opens the 24h window
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert wa["identities"][0]["window_open"] is True


# ---- email + social ---------------------------------------------------------------

@pytest.mark.django_db
class TestEmailAndSocial:
    def test_email_on_file_without_threads(self, client, superadmin, no_env_account,
                                           contact):
        email = _channel(_get(client, contact.pk).json(), "email")
        assert email is not None
        assert email["connected"] is False
        assert email["identities"] == []
        assert email["last_active"] is None

    def test_email_thread_counts_and_connected(self, client, superadmin,
                                               no_env_account, contact, monkeypatch):
        monkeypatch.setattr(msg_config, "email_configured", lambda: True)
        t = Thread.objects.create(channel="email", key="ahmad@example.com",
                                  address="ahmad@example.com", contact=contact)
        _msg(t, "out", "Your invoice is overdue.", source="agent")
        email = _channel(_get(client, contact.pk).json(), "email")
        assert email["connected"] is True
        ident = email["identities"][0]
        assert ident["address"] == "ahmad@example.com"
        assert ident["message_count"] == 1
        assert ident["outbound_count"] == 1
        assert ident["last_direction"] == "out"
        assert ident["window_open"] is None  # WhatsApp-only concept

    def test_social_thread_without_account_not_connected(self, client, superadmin,
                                                         no_env_account, contact):
        t = Thread.objects.create(channel="facebook", key="psid-123",
                                  address="psid-123", contact=contact)
        _msg(t, "in", "hello from messenger")
        fb = _channel(_get(client, contact.pk).json(), "facebook")
        assert fb is not None
        assert fb["connected"] is False
        assert fb["identities"][0]["inbound_count"] == 1

    def test_social_thread_with_token_account_connected(self, client, superadmin,
                                                        no_env_account, contact):
        ChannelAccount.objects.create(channel="facebook", name="Page",
                                      external_id="pg-1", access_token="tok")
        Thread.objects.create(channel="facebook", key="psid-123",
                              address="psid-123", contact=contact)
        fb = _channel(_get(client, contact.pk).json(), "facebook")
        assert fb["connected"] is True


# ---- voice (CallRecord) -----------------------------------------------------------

@pytest.mark.django_db
class TestVoice:
    def test_counts_fk_and_phone_matched_records(self, client, superadmin,
                                                 no_env_account, contact):
        CallRecord.objects.create(call_id="c1", contact=contact, phone=contact.phone,
                                  ended_at="2026-07-10T10:00:00",
                                  outcome="scheduled")
        # mirrored from data/calls.json before any FK link existed
        CallRecord.objects.create(call_id="c2", phone=contact.phone,
                                  ended_at="2026-07-14T09:00:00", outcome="no_answer")
        # someone else's call must not leak in
        CallRecord.objects.create(call_id="c3", phone="+60999888777",
                                  ended_at="2026-07-15T09:00:00", outcome="paid")
        voice = _channel(_get(client, contact.pk).json(), "voice")
        assert voice["calls"] == 2
        assert voice["last_call"] == "2026-07-14T09:00:00"
        assert voice["last_outcome"] == "no_answer"
        assert voice["connected"] is True


# ---- POST: add a channel identity from the contact page ----------------------------

@pytest.mark.django_db
class TestAddChannelIdentity:
    def _post(self, client, cid, body):
        import json
        return client.post(f"/api/contacts/{cid}/channels",
                           data=json.dumps(body),
                           content_type="application/json", **AUTH)

    def test_401_without_token(self, client, superadmin, contact):
        import json
        r = client.post(f"/api/contacts/{contact.pk}/channels",
                        data=json.dumps({"channel": "whatsapp",
                                         "address": "60111222333"}),
                        content_type="application/json")
        assert r.status_code == 401

    def test_add_whatsapp_number_creates_linked_thread(self, client, superadmin,
                                                       no_env_account, contact):
        r = self._post(client, contact.pk,
                       {"channel": "whatsapp", "address": "+60 11-122 2333"})
        assert r.status_code == 201
        assert r.json()["key"] == "60111222333"
        t = Thread.objects.get(channel="whatsapp", key="60111222333")
        assert t.contact == contact
        # and it shows up in the hub straight away
        wa = _channel(_get(client, contact.pk).json(), "whatsapp")
        assert any(i["key"] == "60111222333" for i in wa["identities"])

    def test_add_email_and_telegram_identities(self, client, superadmin,
                                               no_env_account, contact):
        assert self._post(client, contact.pk,
                          {"channel": "email",
                           "address": "Second@Example.com"}).status_code == 201
        assert Thread.objects.get(channel="email",
                                  key="second@example.com").contact == contact
        assert self._post(client, contact.pk,
                          {"channel": "telegram",
                           "address": "123456789"}).status_code == 201
        assert Thread.objects.get(channel="telegram",
                                  key="123456789").contact == contact

    def test_conflict_when_linked_to_another_contact(self, client, superadmin,
                                                    no_env_account, contact):
        other = Contact.objects.create(name="Someone Else", phone="+60999888777")
        Thread.objects.create(channel="whatsapp", key="60111222333",
                              address="60111222333", contact=other)
        r = self._post(client, contact.pk,
                       {"channel": "whatsapp", "address": "60111222333"})
        assert r.status_code == 409
        assert "Someone Else" in r.json()["detail"]

    def test_relink_same_contact_is_idempotent(self, client, superadmin,
                                              no_env_account, contact):
        for _ in range(2):
            r = self._post(client, contact.pk,
                           {"channel": "whatsapp", "address": "60111222333"})
            assert r.status_code == 201
        assert Thread.objects.filter(channel="whatsapp",
                                     key="60111222333").count() == 1

    def test_validation_errors(self, client, superadmin, no_env_account, contact):
        cases = [
            ({"channel": "sms", "address": "60111222333"}, 400),
            ({"channel": "whatsapp", "address": "12345"}, 400),
            ({"channel": "email", "address": "not-an-email"}, 400),
            ({"channel": "whatsapp"}, 400),
        ]
        for body, expected in cases:
            assert self._post(client, contact.pk, body).status_code == expected, body
