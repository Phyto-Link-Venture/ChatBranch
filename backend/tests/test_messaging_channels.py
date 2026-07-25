"""Messaging inbox features: the Telegram channel (webhook intake, token
routing, outbound dispatch, account auto-fill), the email multi-account
channel (accounts CRUD with masked SMTP passwords, per-account SMTP test
probe, per-account routed sending with global-config fallback) and the
channel-agnostic inbox management layer (status lifecycle + auto-reopen,
assignee, labels, private notes, canned responses, agents list).
"""
import json
import types

import pytest
from django.contrib.auth.models import User

from crm.models import (
    AccountAssignment,
    AuditLog,
    CannedResponse,
    ChannelAccount,
    Contact,
    Message,
    Profile,
    Thread,
)
from messaging import views as msg_views
from whatsapp import store as wa_store

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
AGENT_TOKEN = "test-agent-token"
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}
BOT_TOKEN = "123456:ABC-tg-test"


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
def quiet_hooks(monkeypatch):
    """No AI replies, flow threads or media downloads out of webhook/simulate
    handlers."""
    monkeypatch.setattr(msg_views, "_maybe_ai_reply", lambda channel, key: None)
    # webhooks now use the delayed async wrapper — must not spawn threads in tests
    monkeypatch.setattr(msg_views, "_ai_reply_async", lambda channel, key: None)
    monkeypatch.setattr(msg_views, "_attach_media_async", lambda label, worker: None)
    monkeypatch.setattr(msg_views.flows_engine, "on_inbound_async",
                        lambda channel, key, text: None)
    monkeypatch.setattr(msg_views.flows_engine, "on_inbound_message",
                        lambda channel, key, text: [])


@pytest.fixture
def tg_account(db):
    return ChannelAccount.objects.create(
        channel="telegram", name="Test Bot", external_id="42", access_token=BOT_TOKEN)


def _tg_update(chat_id="777", text="hello", message_id=1, first_name="Aina", last_name="Binti"):
    return {
        "update_id": 900 + message_id,
        "message": {
            "message_id": message_id,
            "from": {"id": int(chat_id), "is_bot": False,
                     "first_name": first_name, "last_name": last_name},
            "chat": {"id": int(chat_id), "type": "private"},
            "text": text,
        },
    }


def _post_update(client, payload, token=BOT_TOKEN):
    return client.post(f"/api/messaging/webhook/telegram/{token}",
                       data=json.dumps(payload), content_type="application/json")


@pytest.mark.django_db
class TestTelegramWebhook:
    def test_unknown_token_403(self, client, quiet_hooks):
        assert _post_update(client, _tg_update(), token="wrong").status_code == 403

    def test_inbound_creates_thread_and_message(self, client, tg_account, quiet_hooks):
        r = _post_update(client, _tg_update(text="saya nak bayar"))
        assert r.status_code == 200
        t = Thread.objects.get(channel="telegram", key="777")
        assert t.name == "Aina Binti"
        msg = t.messages.get()
        assert msg.text == "saya nak bayar"
        assert msg.direction == "in"
        assert msg.external_id == "1"

    def test_redelivery_dedupes_on_message_id(self, client, tg_account, quiet_hooks):
        _post_update(client, _tg_update(message_id=5))
        _post_update(client, _tg_update(message_id=5))
        t = Thread.objects.get(channel="telegram", key="777")
        assert t.messages.count() == 1

    def test_non_message_update_ignored(self, client, tg_account, quiet_hooks):
        r = _post_update(client, {"update_id": 1, "edited_message": {"text": "x"}})
        assert r.status_code == 200
        assert Thread.objects.filter(channel="telegram").count() == 0

    def test_media_message_gets_placeholder(self, client, tg_account, quiet_hooks):
        update = _tg_update(message_id=9)
        del update["message"]["text"]
        update["message"]["photo"] = [{"file_id": "abc"}]
        _post_update(client, update)
        assert Thread.objects.get(channel="telegram", key="777").messages.get().text == "[photo]"

    def test_inbound_reopens_resolved_thread(self, client, tg_account, quiet_hooks):
        _post_update(client, _tg_update(message_id=1))
        Thread.objects.filter(channel="telegram", key="777").update(status="resolved")
        _post_update(client, _tg_update(message_id=2, text="hello again"))
        assert Thread.objects.get(channel="telegram", key="777").status == "open"


@pytest.mark.django_db
class TestTelegramSend:
    def test_sandbox_send_without_account(self, client, superadmin, quiet_hooks):
        client.post("/api/messaging/threads", data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        r = client.post("/api/messaging/threads/telegram/777/send",
                        data=json.dumps({"text": "hi"}), content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["message"]["status"] == "sandbox"

    def test_live_send_uses_telegram_client(self, client, superadmin, tg_account,
                                            quiet_hooks, monkeypatch):
        sent = {}

        def fake_send(account, chat_id, text):
            sent.update(account_id=account["id"], chat_id=chat_id, text=text)
            return "555"

        monkeypatch.setattr(msg_views.telegram_client, "send_message", fake_send)
        client.post("/api/messaging/threads", data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        r = client.post("/api/messaging/threads/telegram/777/send",
                        data=json.dumps({"text": "bayar sini"}), content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert sent == {"account_id": str(tg_account.pk), "chat_id": "777", "text": "bayar sini"}
        assert r.json()["message"]["id"] == "555"

    def test_account_create_autofills_via_get_me(self, client, superadmin, monkeypatch):
        monkeypatch.setattr(msg_views.telegram_client, "get_me",
                            lambda body: {"id": "424242", "name": "DemoBot"})
        r = client.post("/api/messaging/accounts",
                        data=json.dumps({"channel": "telegram", "access_token": "tok-x"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["external_id"] == "424242"

    def test_status_flag_reflects_linked_bot(self, client, tg_account):
        assert client.get("/api/messaging/status").json()["telegram_configured"] is True


@pytest.mark.django_db
class TestConversationManagement:
    @pytest.fixture
    def thread(self, client, superadmin, quiet_hooks):
        client.post("/api/messaging/threads", data=json.dumps({"channel": "facebook", "to": "psid-1"}),
                    content_type="application/json", **AUTH)
        return Thread.objects.get(channel="facebook", key="psid-1")

    def _post(self, client, path, body):
        return client.post(f"/api/messaging/threads/facebook/psid-1/{path}",
                           data=json.dumps(body), content_type="application/json", **AUTH)

    def test_status_roundtrip_and_validation(self, client, thread):
        assert self._post(client, "status", {"status": "resolved"}).json()["status"] == "resolved"
        assert self._post(client, "status", {"status": "nope"}).status_code == 400

    def test_simulate_reopens_resolved_thread(self, client, thread):
        self._post(client, "status", {"status": "resolved"})
        self._post(client, "simulate", {"text": "contact is back"})
        assert Thread.objects.get(pk=thread.pk).status == "open"

    def test_assign_and_clear(self, client, thread):
        assert self._post(client, "assign", {"assignee": "super"}).json()["assignee"] == "super"
        assert self._post(client, "assign", {"assignee": ""}).json()["assignee"] == ""

    def test_labels_normalized(self, client, thread):
        r = self._post(client, "labels", {"labels": ["PTP", "ptp", "  Dispute ", ""]})
        assert r.json()["labels"] == ["ptp", "dispute"]

    def test_private_note_stored_not_sent(self, client, thread):
        r = self._post(client, "note", {"text": "contact disputes the fee"})
        assert r.status_code == 200
        msg = Message.objects.get(thread=thread)
        assert msg.private is True
        assert msg.status == "note"
        detail = client.get("/api/messaging/threads/facebook/psid-1", **AUTH).json()
        assert detail["messages"][0]["private"] is True

    def test_agents_list(self, client, superadmin, thread):
        r = client.get("/api/messaging/agents", **AUTH)
        assert {"username": "super", "name": "super", "role": "superadmin"} in r.json()["agents"]

    def test_endpoints_401_without_token(self, client, superadmin):
        for path in ("status", "assign", "labels", "note"):
            r = client.post(f"/api/messaging/threads/facebook/psid-1/{path}",
                            data="{}", content_type="application/json")
            assert r.status_code == 401, path


@pytest.mark.django_db
class TestEmailInboundSync:
    @pytest.fixture
    def imap_on(self, monkeypatch):
        monkeypatch.setattr(msg_views.msg_config, "imap_configured", lambda: True)
        msg_views._email_sync_state["last"] = 0.0

    @pytest.fixture
    def contact(self, db):
        # the sender filter only ingests mail from known contacts
        return Contact.objects.create(name="Cust Omer", phone="+60111222333",
                                       email="cust@example.com")

    def _mail(self, message_id="<m1@x>", body="I can pay on Friday", subject="Re: Regarding your account",
              from_address="cust@example.com", from_name="Cust Omer"):
        return {"from_address": from_address, "from_name": from_name,
                "subject": subject, "body": body, "message_id": message_id}

    def _sync(self, client, force=True):
        return client.post("/api/messaging/email/sync",
                           data=json.dumps({"force": force}), content_type="application/json", **AUTH)

    def test_not_configured_is_noop(self, client, superadmin):
        r = self._sync(client)
        assert r.json() == {"ok": True, "configured": False, "fetched": 0}

    def test_reply_lands_in_thread(self, client, superadmin, imap_on, contact, monkeypatch):
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages", lambda: [self._mail()])
        assert self._sync(client).json()["fetched"] == 1
        t = Thread.objects.get(channel="email", key="cust@example.com")
        assert t.name == "Cust Omer"
        msg = t.messages.get()
        assert msg.direction == "in" and msg.text == "I can pay on Friday"  # Re: subject dropped

    def test_fresh_subject_kept_and_dedup_by_message_id(self, client, superadmin, imap_on, contact, monkeypatch):
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages",
                            lambda: [self._mail(subject="Dispute on invoice 42")])
        self._sync(client)
        self._sync(client)  # same Message-ID again
        t = Thread.objects.get(channel="email", key="cust@example.com")
        assert t.messages.count() == 1
        assert t.messages.get().text.startswith("Dispute on invoice 42\n\n")

    def test_inbound_reopens_resolved_email_thread(self, client, superadmin, imap_on, contact, monkeypatch):
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages", lambda: [self._mail()])
        self._sync(client)
        Thread.objects.filter(channel="email").update(status="resolved")
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages",
                            lambda: [self._mail(message_id="<m2@x>")])
        self._sync(client)
        assert Thread.objects.get(channel="email").status == "open"

    def test_throttle_without_force(self, client, superadmin, imap_on, contact, monkeypatch):
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages", lambda: [self._mail()])
        self._sync(client)
        r = self._sync(client, force=False)
        assert r.json().get("throttled") is True

    def test_unknown_sender_is_skipped(self, client, superadmin, imap_on, monkeypatch):
        """Newsletters / platform notifications never reach the inbox — no CRM
        match, no prior correspondence."""
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages",
                            lambda: [self._mail(from_address="news@shopping-deals.example",
                                                from_name="Deals Weekly",
                                                subject="50% off everything!")])
        r = self._sync(client).json()
        assert r["fetched"] == 0 and r["skipped"] == 1
        assert not Thread.objects.filter(channel="email").exists()

    def test_previously_emailed_sender_is_kept(self, client, superadmin, imap_on, monkeypatch):
        """No CRM email match, but we wrote to them first (e.g. a flow send) —
        their reply belongs in the thread."""
        from messaging import store as msg_store
        msg_store.get_or_create_thread("email", "manual@example.com")
        msg_store.append_message("email", "manual@example.com", "out",
                                 "Payment reminder", source="agent")
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages",
                            lambda: [self._mail(from_address="manual@example.com",
                                                from_name="Manual Contact")])
        r = self._sync(client).json()
        assert r["fetched"] == 1 and r["skipped"] == 0
        t = Thread.objects.get(channel="email", key="manual@example.com")
        assert t.messages.filter(direction="in").count() == 1

    def test_401_without_token(self, client, superadmin):
        assert client.post("/api/messaging/email/sync", data="{}",
                           content_type="application/json").status_code == 401


@pytest.mark.django_db
class TestCustomerIdentity:
    """One contact, many channels: auto-link + manual link + the combined view."""

    @pytest.fixture
    def contact(self, db):
        return Contact.objects.create(name="Aina", phone="+60123456789",
                                       email="aina@example.com")

    def test_whatsapp_thread_autolinks_by_phone(self, contact):
        t = wa_store.get_or_create_thread("+60123456789")
        assert t["contact_id"] == contact.pk
        assert t["contact_name"] == "Aina"

    def test_email_thread_autolinks_by_address(self, client, superadmin, contact, quiet_hooks):
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "email", "to": "AINA@example.com".lower()}),
                    content_type="application/json", **AUTH)
        assert Thread.objects.get(channel="email").contact_id == contact.pk

    def test_manual_link_and_unlink_social_thread(self, client, superadmin, contact, quiet_hooks):
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        r = client.post("/api/messaging/threads/telegram/777/link",
                        data=json.dumps({"contact_id": contact.pk}),
                        content_type="application/json", **AUTH)
        assert r.json() == {"contact_id": contact.pk, "contact_name": "Aina"}
        r = client.post("/api/messaging/threads/telegram/777/link",
                        data=json.dumps({"contact_id": None}),
                        content_type="application/json", **AUTH)
        assert r.json()["contact_id"] is None

    def test_combined_conversation_merges_channels_in_order(self, client, superadmin,
                                                            contact, quiet_hooks):
        wa_store.get_or_create_thread("+60123456789")  # autolinks
        wa_store.append_message("60123456789", "in", "wa first", source="customer")
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        client.post("/api/messaging/threads/telegram/777/link",
                    data=json.dumps({"contact_id": contact.pk}),
                    content_type="application/json", **AUTH)
        client.post("/api/messaging/threads/telegram/777/send",
                    data=json.dumps({"text": "tg second"}),
                    content_type="application/json", **AUTH)

        r = client.get(f"/api/messaging/contacts/{contact.pk}/conversation", **AUTH)
        data = r.json()
        assert {c["channel"] for c in data["channels"]} == {"whatsapp", "telegram"}
        texts = [(m["channel"], m["text"]) for m in data["messages"]]
        assert texts == [("whatsapp", "wa first"), ("telegram", "tg second")]

    def test_contacts_vcard_export(self, client, superadmin, contact):
        r = client.get("/api/messaging/contacts/vcard", **AUTH)
        assert r.status_code == 200
        assert r["Content-Type"].startswith("text/vcard")
        body = r.content.decode()
        assert "FN:Aina" in body and "TEL;TYPE=CELL:+60123456789" in body
        assert client.get("/api/messaging/contacts/vcard").status_code == 401

    def test_contacts_list(self, client, superadmin, contact):
        r = client.get("/api/messaging/contacts", **AUTH)
        assert r.json()["contacts"] == [{"id": contact.pk, "name": "Aina",
                                         "phone": "+60123456789", "email": "aina@example.com"}]

    def test_link_endpoints_401_without_token(self, client, superadmin, contact):
        assert client.get("/api/messaging/contacts").status_code == 401
        assert client.get(f"/api/messaging/contacts/{contact.pk}/conversation").status_code == 401


@pytest.mark.django_db
class TestThreadSummary:
    """The always-on chat summary card (sandbox = heuristic, no AI key)."""

    @pytest.fixture
    def summaries_tmp(self, tmp_path, monkeypatch):
        from messaging import timeline
        monkeypatch.setattr(timeline, "SUMMARIES_DIR", tmp_path / "summaries")
        # deterministic regardless of AI keys in the developer's .env
        monkeypatch.setattr(timeline.wa_config, "ai_available", lambda: False)

    @pytest.fixture
    def thread(self, client, superadmin, quiet_hooks, summaries_tmp):
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        client.post("/api/messaging/threads/telegram/777/simulate",
                    data=json.dumps({"text": "boleh bayar jumaat"}),
                    content_type="application/json", **AUTH)

    def _get(self, client):
        return client.get("/api/messaging/threads/telegram/777/summary", **AUTH)

    def test_stale_before_first_generate(self, client, thread):
        data = self._get(client).json()
        assert data["summary"] is None
        assert data["stale"] is True
        assert data["message_count"] == 1

    def test_generate_then_fresh_then_stale_again(self, client, thread):
        r = client.post("/api/messaging/threads/telegram/777/summary", **AUTH)
        body = r.json()
        assert body["summary"]["heuristic"] is True  # no AI key in tests
        assert "boleh bayar jumaat" in body["summary"]["summary"]
        assert self._get(client).json()["stale"] is False
        client.post("/api/messaging/threads/telegram/777/simulate",
                    data=json.dumps({"text": "eh tunggu"}),
                    content_type="application/json", **AUTH)
        assert self._get(client).json()["stale"] is True

    def test_unknown_thread_404(self, client, superadmin, summaries_tmp):
        assert client.get("/api/messaging/threads/telegram/nope/summary", **AUTH).status_code == 404

    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/messaging/threads/telegram/777/summary").status_code == 401


class _FakeLLM:
    """Captures chat.completions.create kwargs; returns `reply`."""
    captured: list = []
    reply = "AI summary v1"

    def __init__(self, base_url=None, api_key=None):
        class _Completions:
            def create(_self, **kwargs):
                _FakeLLM.captured.append(kwargs)
                msg = types.SimpleNamespace(content=_FakeLLM.reply)
                return types.SimpleNamespace(choices=[types.SimpleNamespace(message=msg)])

        self.chat = types.SimpleNamespace(completions=_Completions())


@pytest.fixture
def fake_ai(tmp_path, monkeypatch):
    """Prompt-capturing LLM + isolated summaries dir + AI forced available."""
    import openai

    from messaging import timeline
    _FakeLLM.captured = []
    _FakeLLM.reply = "AI summary v1"
    monkeypatch.setattr(openai, "OpenAI", _FakeLLM)
    monkeypatch.setattr(timeline, "SUMMARIES_DIR", tmp_path / "summaries")
    monkeypatch.setattr(timeline.wa_config, "ai_available", lambda: True)
    monkeypatch.setattr(timeline.wa_config, "chat_provider",
                        lambda: ("http://llm", "key", "model-x"))
    return _FakeLLM.captured


class TestSummaryDelta:
    """_delta() decides when the rolling (previous summary + new messages only)
    path is safe; anything irregular falls back to a full pass."""

    CACHED = {"summary": "prev", "heuristic": False, "as_of": "t2", "message_count": 2}
    MSGS = [{"ts": "t1"}, {"ts": "t2"}, {"ts": "t3"}]

    def _delta(self, cached, items):
        from messaging import timeline
        return timeline._delta(cached, items, count_key="message_count")

    def test_good_delta(self):
        assert self._delta(self.CACHED, self.MSGS) == ("prev", [{"ts": "t3"}])

    def test_full_without_a_trusted_previous_summary(self):
        assert self._delta(None, self.MSGS) == ("", None)
        assert self._delta({**self.CACHED, "heuristic": True}, self.MSGS) == ("", None)
        assert self._delta({**self.CACHED, "as_of": ""}, self.MSGS) == ("", None)

    def test_full_when_nothing_new(self):
        cached = {**self.CACHED, "as_of": "t3", "message_count": 3}
        assert self._delta(cached, self.MSGS) == ("", None)

    def test_full_when_backdated_message_slipped_behind_anchor(self):
        # cache saw 2 messages, only 1 is newer than the anchor, yet there are
        # 4 now — a late webhook (provider_ts) landed BEHIND as_of; an
        # incremental pass would silently miss it, so it must go full
        msgs = [{"ts": "t1"}, {"ts": "t1b"}, {"ts": "t2"}, {"ts": "t3"}]
        assert self._delta(self.CACHED, msgs) == ("", None)

    def test_media_marker_in_transcript_lines(self):
        from messaging import timeline
        line = timeline._thread_lines(
            [{"ts": "2026-07-22T10:00:00", "dir": "in", "text": "resit",
              "media": {"type": "image", "url": "/x"}}])[0]
        assert "[image attachment]" in line and "resit" in line


@pytest.mark.django_db
class TestIncrementalThreadSummary:
    """Rolling chat summary: after the first AI pass, a refresh sends the
    previous summary + only the new messages (token saver)."""

    @pytest.fixture
    def thread(self, client, superadmin, quiet_hooks, fake_ai):
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        client.post("/api/messaging/threads/telegram/777/simulate",
                    data=json.dumps({"text": "boleh bayar jumaat"}),
                    content_type="application/json", **AUTH)

    def _regen(self, client):
        return client.post("/api/messaging/threads/telegram/777/summary", **AUTH).json()

    def _simulate(self, client, text):
        client.post("/api/messaging/threads/telegram/777/simulate",
                    data=json.dumps({"text": text}),
                    content_type="application/json", **AUTH)

    def _prompt(self, captured, i):
        return captured[i]["messages"][-1]["content"]

    def test_first_full_then_incremental(self, client, thread, fake_ai):
        body = self._regen(client)
        assert body["summary"]["heuristic"] is False
        assert body["summary"]["incremental"] is False
        assert "boleh bayar jumaat" in self._prompt(fake_ai, 0)

        self._simulate(client, "gaji masuk 25hb")
        _FakeLLM.reply = "AI summary v2"
        body = self._regen(client)
        assert body["summary"]["incremental"] is True
        assert body["summary"]["summary"] == "AI summary v2"
        prompt = self._prompt(fake_ai, 1)
        assert "AI summary v1" in prompt           # previous summary handed back
        assert "gaji masuk 25hb" in prompt         # the delta
        assert "boleh bayar jumaat" not in prompt  # history NOT re-sent

    def test_forced_regen_with_nothing_new_is_full(self, client, thread, fake_ai):
        self._regen(client)
        body = self._regen(client)  # operator hits refresh with no new messages
        assert body["summary"]["incremental"] is False
        assert "boleh bayar jumaat" in self._prompt(fake_ai, 1)

    def test_heuristic_previous_never_seeds_incremental(self, client, thread, fake_ai,
                                                        monkeypatch):
        from messaging import timeline
        monkeypatch.setattr(timeline.wa_config, "ai_available", lambda: False)
        self._regen(client)  # caches a heuristic summary (no AI key)
        monkeypatch.setattr(timeline.wa_config, "ai_available", lambda: True)
        self._simulate(client, "eh tunggu")
        body = self._regen(client)
        assert body["summary"]["heuristic"] is False
        assert body["summary"]["incremental"] is False  # full pass, not delta-on-heuristic
        assert "boleh bayar jumaat" in self._prompt(fake_ai, 0)


class TestIncrementalCustomerSummary:
    """generate_summary (contact page) rolls the same way off event_count/as_of."""

    def test_rolls_from_previous_summary(self, fake_ai, monkeypatch):
        from messaging import timeline
        events = [{"channel": "whatsapp", "dir": "in", "source": "customer",
                   "text": f"m{i}", "ts": f"2026-07-2{i}T00:00:00"} for i in (1, 2)]
        monkeypatch.setattr(timeline, "collect_events",
                            lambda phone="", email="": list(events))
        first = timeline.generate_summary("Aina", phone="60123456789")
        assert first["incremental"] is False
        assert first["as_of"] == "2026-07-22T00:00:00"

        events.append({"channel": "email", "dir": "out", "source": "agent",
                       "text": "m3", "ts": "2026-07-23T00:00:00"})
        _FakeLLM.reply = "AI summary v2"
        second = timeline.generate_summary("Aina", phone="60123456789")
        assert second["incremental"] is True
        assert second["summary"] == "AI summary v2"
        prompt = fake_ai[1]["messages"][-1]["content"]
        assert "AI summary v1" in prompt and "m3" in prompt
        assert "m1" not in prompt  # old events NOT re-sent


@pytest.mark.django_db
class TestCustomerTimeline:
    """The contact-page cross-channel log. `?limit=N` previews only the N most
    recent contacts (with the full `total`); no limit returns everything."""

    @pytest.fixture
    def four_events(self, tmp_path, monkeypatch):
        from messaging import timeline
        monkeypatch.setattr(timeline, "SUMMARIES_DIR", tmp_path / "summaries")
        monkeypatch.setattr(
            timeline, "collect_events",
            lambda phone="", email="": [{"channel": "whatsapp", "dir": "in",
                                         "source": "customer", "text": f"m{i}", "ts": f"t{i}"}
                                        for i in range(1, 5)])

    def test_limit_returns_most_recent_and_total(self, client, superadmin, four_events):
        data = client.get("/api/messaging/timeline?phone=60123456789&limit=2", **AUTH).json()
        assert [e["text"] for e in data["events"]] == ["m3", "m4"]
        assert data["total"] == 4

    def test_bad_limit_ignored(self, client, superadmin, four_events):
        data = client.get("/api/messaging/timeline?phone=60123456789&limit=abc", **AUTH).json()
        assert len(data["events"]) == 4

    def test_full_timeline_returns_events(self, client, superadmin, four_events):
        data = client.get("/api/messaging/timeline?phone=60123456789", **AUTH).json()
        assert [e["text"] for e in data["events"]] == ["m1", "m2", "m3", "m4"]
        assert data["total"] == 4
        assert "summary" in data

    def test_requires_phone_or_email(self, client, superadmin):
        assert client.get("/api/messaging/timeline", **AUTH).status_code == 400

    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/messaging/timeline?phone=123").status_code == 401


@pytest.mark.django_db
class TestAiFlowCoexistence:
    """When a Live flow run is parked on this contact's reply, the generic AI
    stands down — the flow builder's logic answers instead."""

    def test_ai_stands_down_for_parked_flow(self, client, superadmin, quiet_hooks):
        from crm.models import Contact, FlowRun, Workflow
        contact = Contact.objects.create(name="Aina", phone="+60123456789")
        wf = Workflow.objects.create(flow_id="wf-test", name="Reminder flow")
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "telegram", "to": "777"}),
                    content_type="application/json", **AUTH)
        client.post("/api/messaging/threads/telegram/777/link",
                    data=json.dumps({"contact_id": contact.pk}),
                    content_type="application/json", **AUTH)
        thread = msg_views.store.load_thread("telegram", "777")
        assert msg_views._flow_owns_reply(thread) is False
        FlowRun.objects.create(workflow=wf, contact=contact,
                               status="waiting", wait_for="reply")
        assert msg_views._flow_owns_reply(thread) is True


# ---- email as a multi-account channel ------------------------------------------------

def _email_body(**overrides):
    body = {"channel": "email", "name": "Support mailbox",
            "from_email": "collect@northwind.example", "host": "smtp.example.com",
            "port": 2525, "username": "collect@northwind.example",
            "password": "s3cretword", "use_tls": True}
    body.update(overrides)
    return body


def _create_email(client, auth=AUTH, **overrides):
    return client.post("/api/messaging/accounts", data=json.dumps(_email_body(**overrides)),
                       content_type="application/json", **auth)


@pytest.mark.django_db
class TestEmailAccounts:
    def test_create_shape_and_masking(self, client, superadmin):
        r = _create_email(client)
        assert r.status_code == 200
        data = r.json()
        assert data["channel"] == "email"
        assert data["name"] == "Support mailbox"
        assert data["external_id"] == "collect@northwind.example"  # from-address convention
        assert data["configured"] is True
        cfg = data["config"]
        assert cfg["host"] == "smtp.example.com"
        assert cfg["port"] == 2525
        assert cfg["username"] == "collect@northwind.example"
        assert cfg["use_tls"] is True
        assert cfg["from_email"] == "collect@northwind.example"
        assert cfg["password_masked"] == "••••word"
        assert "password" not in cfg  # the raw secret never leaves the API
        row = ChannelAccount.objects.get(channel="email")
        assert row.config["password"] == "s3cretword"
        assert row.access_token == ""

    def test_create_validates_required_fields(self, client, superadmin):
        for missing in ("host", "username", "password", "from_email"):
            r = _create_email(client, **{missing: ""})
            assert r.status_code == 400, missing
            assert missing in r.json()["detail"]
        assert _create_email(client, from_email="not-an-address").status_code == 400
        assert _create_email(client, port="nope").status_code == 400

    def test_port_defaults_to_587(self, client, superadmin):
        r = _create_email(client, port=None)
        assert r.status_code == 200
        assert r.json()["config"]["port"] == 587
        assert ChannelAccount.objects.get(channel="email").config["port"] == 587

    def test_listed_alongside_social(self, client, superadmin, tg_account):
        _create_email(client)
        rows = client.get("/api/messaging/accounts", **AUTH).json()["accounts"]
        assert {row["channel"] for row in rows} == {"telegram", "email"}
        mail = next(row for row in rows if row["channel"] == "email")
        assert mail["config"]["password_masked"] == "••••word"

    def test_patch_partial_and_masked_password_roundtrip(self, client, superadmin):
        acct_id = _create_email(client).json()["id"]
        listed = client.get("/api/messaging/accounts", **AUTH).json()["accounts"][0]
        r = client.patch(f"/api/messaging/accounts/{acct_id}",
                         data=json.dumps({"host": "smtp2.example.com",
                                          "password": listed["config"]["password_masked"]}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["config"]["host"] == "smtp2.example.com"
        row = ChannelAccount.objects.get(pk=int(acct_id))
        assert row.config["host"] == "smtp2.example.com"
        assert row.config["password"] == "s3cretword"  # masked placeholder kept the secret
        assert row.config["username"] == "collect@northwind.example"  # untouched keys survive

    def test_patch_password_from_email_and_name(self, client, superadmin):
        acct_id = _create_email(client).json()["id"]
        r = client.patch(f"/api/messaging/accounts/{acct_id}",
                         data=json.dumps({"password": "newpass99", "name": "Billing",
                                          "from_email": "billing@northwind.example"}),
                         content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["name"] == "Billing"
        row = ChannelAccount.objects.get(pk=int(acct_id))
        assert row.config["password"] == "newpass99"
        assert row.config["from_email"] == "billing@northwind.example"
        assert row.external_id == "billing@northwind.example"  # from-address moves with it

    def test_patch_validation(self, client, superadmin, tg_account):
        acct_id = _create_email(client).json()["id"]
        def patch(target, body):
            return client.patch(f"/api/messaging/accounts/{target}", data=json.dumps(body),
                                content_type="application/json", **AUTH)
        assert patch(acct_id, {"host": ""}).status_code == 400
        assert patch(acct_id, {"port": "abc"}).status_code == 400
        assert patch(acct_id, {"from_email": "nope"}).status_code == 400
        assert patch(acct_id, {}).status_code == 400  # nothing to update
        assert patch(99999, {"host": "x"}).status_code == 404
        assert patch(tg_account.pk, {"host": "x"}).status_code == 400  # email rows only

    def test_delete(self, client, superadmin):
        acct_id = _create_email(client).json()["id"]
        assert client.delete(f"/api/messaging/accounts/{acct_id}", **AUTH).json()["ok"] is True
        assert ChannelAccount.objects.filter(channel="email").count() == 0

    def test_mutations_require_admin(self, client, superadmin, agent_user):
        assert _create_email(client, auth=AGENT_AUTH).status_code == 403
        acct_id = _create_email(client).json()["id"]
        assert client.patch(f"/api/messaging/accounts/{acct_id}",
                            data=json.dumps({"host": "x"}),
                            content_type="application/json", **AGENT_AUTH).status_code == 403
        assert client.delete(f"/api/messaging/accounts/{acct_id}",
                             **AGENT_AUTH).status_code == 403

    def test_status_flag_reflects_email_account(self, client, superadmin):
        _create_email(client)
        assert client.get("/api/messaging/status").json()["email_configured"] is True

    def test_probe_uses_this_accounts_config(self, client, superadmin, monkeypatch):
        acct_id = _create_email(client).json()["id"]
        seen = {}
        monkeypatch.setattr(msg_views.email_client, "probe", lambda s: seen.update(s))
        r = client.post(f"/api/messaging/accounts/{acct_id}/test", data="{}",
                        content_type="application/json", **AUTH)
        assert r.status_code == 200 and r.json()["ok"] is True
        assert seen["smtp_host"] == "smtp.example.com"
        assert seen["smtp_port"] == "2525"
        assert seen["smtp_username"] == "collect@northwind.example"
        assert seen["smtp_password"] == "s3cretword"

    def test_probe_failure_502(self, client, superadmin, monkeypatch):
        acct_id = _create_email(client).json()["id"]
        def boom(s):
            raise RuntimeError("535 auth failed")
        monkeypatch.setattr(msg_views.email_client, "probe", boom)
        r = client.post(f"/api/messaging/accounts/{acct_id}/test", data="{}",
                        content_type="application/json", **AUTH)
        assert r.status_code == 502
        assert "535" in r.json()["detail"]

    def test_probe_with_to_sends_test_mail(self, client, superadmin, monkeypatch):
        acct_id = _create_email(client).json()["id"]
        monkeypatch.setattr(msg_views.email_client, "probe", lambda s: None)
        sent = {}
        def fake_send(to, body, subject=None, settings=None):
            sent.update(to=to, host=(settings or {}).get("smtp_host"))
            return "<test@id>"
        monkeypatch.setattr(msg_views.email_client, "send_email", fake_send)
        r = client.post(f"/api/messaging/accounts/{acct_id}/test",
                        data=json.dumps({"to": "kx@example.com"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert sent == {"to": "kx@example.com", "host": "smtp.example.com"}


class _FakeSMTP:
    """Stand-in for smtplib.SMTP/SMTP_SSL — records who connected/logged in."""
    sessions: list = []

    def __init__(self, host, port, timeout=None):
        self.host, self.port = host, port
        self.tls = False
        self.login_args = None
        self.sent = []
        _FakeSMTP.sessions.append(self)

    def starttls(self):
        self.tls = True

    def login(self, username, password):
        self.login_args = (username, password)

    def send_message(self, msg):
        self.sent.append(msg)

    def quit(self):
        pass


@pytest.mark.django_db
class TestEmailPerAccountSend:
    """Outbound email resolution: thread account → routed account → global
    config fallback → sandbox. Real per-account sends record health."""

    @pytest.fixture
    def smtp(self, monkeypatch):
        _FakeSMTP.sessions = []
        monkeypatch.setattr("smtplib.SMTP", _FakeSMTP)
        monkeypatch.setattr("smtplib.SMTP_SSL", _FakeSMTP)
        return _FakeSMTP

    def _account(self, name="Mailbox A", host="smtp-a.example.com",
                 from_email="a@northwind.example", password="passA"):
        return ChannelAccount.objects.create(
            channel="email", name=name, external_id=from_email,
            config={"host": host, "port": 587, "username": from_email,
                    "password": password, "use_tls": True, "from_email": from_email})

    def _open_thread(self, client, to="cust@example.com"):
        client.post("/api/messaging/threads",
                    data=json.dumps({"channel": "email", "to": to}),
                    content_type="application/json", **AUTH)

    def _send(self, client, to="cust@example.com", text="hello"):
        return client.post(f"/api/messaging/threads/email/{to}/send",
                           data=json.dumps({"text": text}),
                           content_type="application/json", **AUTH)

    def test_thread_account_config_wins(self, client, superadmin, quiet_hooks, smtp):
        self._account("A")
        b = self._account("B", host="smtp-b.example.com",
                          from_email="b@northwind.example", password="passB")
        self._open_thread(client)
        Thread.objects.filter(channel="email", key="cust@example.com").update(account=b)
        r = self._send(client)
        assert r.status_code == 200
        session = smtp.sessions[-1]
        assert session.host == "smtp-b.example.com"
        assert session.login_args == ("b@northwind.example", "passB")
        b.refresh_from_db()
        assert b.health_status == "healthy"       # real send recorded on the row
        assert b.last_ok_at is not None
        msg = Message.objects.get(direction="out")
        assert msg.account_id == b.pk             # append_message stamped the account

    def test_routed_by_contact_email_match(self, client, superadmin, quiet_hooks, smtp):
        a = self._account("A")
        contact = Contact.objects.create(name="Aina", phone="+60123456789",
                                           email="cust@example.com")
        self._open_thread(client)  # autolinks the contact by address
        r = self._send(client)
        assert r.status_code == 200
        assert smtp.sessions[-1].host == "smtp-a.example.com"
        t = Thread.objects.get(channel="email", key="cust@example.com")
        assert t.account_id == a.pk  # routing stamped the thread
        asg = AccountAssignment.objects.get(contact=contact, channel="email")
        assert asg.account_id == a.pk  # sticky assignment created on first send

    def test_banned_thread_account_fails_over(self, client, superadmin, quiet_hooks, smtp):
        from whatsapp import health as wa_health
        a = self._account("A")
        b = self._account("B", host="smtp-b.example.com",
                          from_email="b@northwind.example", password="passB")
        Contact.objects.create(name="Aina", phone="+60123456789",
                                email="cust@example.com")
        self._open_thread(client)
        Thread.objects.filter(channel="email", key="cust@example.com").update(account=a)
        wa_health.set_status(a, "banned", "smtp blacklisted")
        r = self._send(client)
        assert r.status_code == 200
        assert smtp.sessions[-1].host == "smtp-b.example.com"
        assert Thread.objects.get(channel="email",
                                  key="cust@example.com").account_id == b.pk

    def test_global_fallback_without_account_rows(self, client, superadmin,
                                                  quiet_hooks, smtp, monkeypatch):
        monkeypatch.setattr(msg_views.msg_config, "email_settings", lambda: {
            "smtp_host": "smtp-global.example.com", "smtp_port": "587",
            "smtp_username": "global@x.com", "smtp_password": "gpass",
            "smtp_from": "global@x.com", "smtp_tls": "true",
            "email_subject": "Regarding your account",
            "imap_host": "", "imap_port": "993"})
        self._open_thread(client)
        r = self._send(client)
        assert r.status_code == 200
        assert smtp.sessions[-1].host == "smtp-global.example.com"
        msg = Message.objects.get(direction="out")
        assert msg.status == "sent"
        assert msg.account_id is None  # global fallback: no account attribution

    def test_sandbox_without_any_config(self, client, superadmin, quiet_hooks, monkeypatch):
        monkeypatch.setattr(msg_views.msg_config, "email_configured", lambda: False)
        self._open_thread(client)
        r = self._send(client)
        assert r.status_code == 200
        assert r.json()["message"]["status"] == "sandbox"

    def test_send_failure_records_on_account(self, client, superadmin,
                                             quiet_hooks, monkeypatch):
        a = self._account("A")
        self._open_thread(client)
        Thread.objects.filter(channel="email", key="cust@example.com").update(account=a)
        def boom(*args, **kwargs):
            raise RuntimeError("connection refused")
        monkeypatch.setattr(msg_views.email_client, "send_email", boom)
        r = self._send(client)
        assert r.status_code == 502
        a.refresh_from_db()
        assert a.fail_streak == 1  # transient SMTP error counted on the row


@pytest.mark.django_db
class TestCannedResponses:
    def _save(self, client, code, content, auth=AUTH):
        return client.post("/api/messaging/canned",
                           data=json.dumps({"short_code": code, "content": content}),
                           content_type="application/json", **auth)

    def _patch(self, client, canned_id, body, auth=AUTH):
        return client.patch(f"/api/messaging/canned/{canned_id}",
                            data=json.dumps(body),
                            content_type="application/json", **auth)

    def test_create_list_patch_delete(self, client, superadmin):
        assert self._save(client, "greet", "Hello from Northwind").status_code == 200
        assert self._save(client, "greet", "Hello again").status_code == 409  # duplicate
        rows = client.get("/api/messaging/canned", **AUTH).json()["canned"]
        assert len(rows) == 1 and rows[0]["content"] == "Hello from Northwind"
        r = self._patch(client, rows[0]["id"], {"content": "Hello again"})
        assert r.status_code == 200 and r.json()["content"] == "Hello again"
        assert client.delete(f"/api/messaging/canned/{rows[0]['id']}", **AUTH).json()["ok"] is True
        assert CannedResponse.objects.count() == 0

    def test_patch_short_code_rename_and_collision(self, client, superadmin):
        self._save(client, "one", "First")
        self._save(client, "two", "Second")
        two = CannedResponse.objects.get(short_code="two")
        assert self._patch(client, two.pk, {"short_code": "one"}).status_code == 409
        assert self._patch(client, two.pk, {"short_code": "/renamed"}).status_code == 200
        two.refresh_from_db()
        assert two.short_code == "renamed"  # leading / stripped, like POST
        assert self._patch(client, two.pk, {}).status_code == 400  # nothing to update
        assert self._patch(client, two.pk, {"content": ""}).status_code == 400
        assert self._patch(client, 99999, {"content": "x"}).status_code == 404

    def test_mutations_require_admin(self, client, superadmin, agent_user):
        self._save(client, "greet", "Hello")
        row = CannedResponse.objects.get()
        assert self._save(client, "x", "y", auth=AGENT_AUTH).status_code == 403
        assert self._patch(client, row.pk, {"content": "z"},
                           auth=AGENT_AUTH).status_code == 403
        assert client.delete(f"/api/messaging/canned/{row.pk}",
                             **AGENT_AUTH).status_code == 403
        # …but reading stays open to every role (the composer needs it)
        assert client.get("/api/messaging/canned", **AGENT_AUTH).status_code == 200

    def test_mutations_write_audit_rows(self, client, superadmin):
        self._save(client, "greet", "Hello")
        row = CannedResponse.objects.get()
        self._patch(client, row.pk, {"content": "Hi"})
        client.delete(f"/api/messaging/canned/{row.pk}", **AUTH)
        actions = set(AuditLog.objects.filter(action__startswith="canned.")
                      .values_list("action", flat=True))
        assert {"canned.create", "canned.update", "canned.delete"} <= actions

    def test_short_code_validation(self, client, superadmin):
        assert self._save(client, "Bad Code!", "x").status_code == 400
        assert self._save(client, "/slash-ok", "x").status_code == 200  # leading / stripped

    def test_rendered_with_contact_variables(self, client, superadmin):
        from crm.models import Record
        contact = Contact.objects.create(name="Aina", phone="+60123456789")
        Record.objects.create(contact=contact, type="order", external_id="ORD-5",
                              status="packed", amount=500, currency="RM")
        self._save(client, "status",
                   "Hi {{name}}, {{record_id}} is {{record_status}} ({{amount}}).")
        row = client.get(f"/api/messaging/canned?contact_id={contact.pk}", **AUTH).json()["canned"][0]
        assert row["rendered"] == "Hi Aina, ORD-5 is packed (RM 500.00)."
        raw = client.get("/api/messaging/canned", **AUTH).json()["canned"][0]
        assert raw["rendered"] == raw["content"]  # unlinked = raw template

    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/messaging/canned").status_code == 401


@pytest.mark.django_db
class TestWhatsappAssign:
    """Assignment is channel-agnostic: the messaging /assign endpoint works on
    whatsapp threads too (shared crm.Thread), and the whatsapp store surfaces
    the assignee so the chats UI can render it."""

    def test_assign_whatsapp_thread_via_messaging(self, client, superadmin):
        wa_store.get_or_create_thread("60123456789")
        r = client.post("/api/messaging/threads/whatsapp/60123456789/assign",
                        data=json.dumps({"assignee": "colle"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert r.json()["assignee"] == "colle"
        assert wa_store.load_thread("60123456789")["assignee"] == "colle"
        assert wa_store.list_threads()[0]["assignee"] == "colle"


# ---- AI coverage gate (only OUR contacts get auto-replies) ---------------------------

@pytest.mark.django_db
class TestAiCoverageGate:
    def test_unlinked_contact_gets_no_ai_reply(self, monkeypatch):
        from messaging import store as msg_store
        monkeypatch.setattr(msg_views.wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(
            msg_views.wa_ai, "generate_reply",
            lambda thread: pytest.fail("AI must not answer unknown contacts"))
        msg_store.get_or_create_thread("telegram", "555001")
        msg_store.append_message("telegram", "555001", "in", "hi", source="customer")
        assert msg_views._maybe_ai_reply("telegram", "555001") is None

    def test_linked_contact_still_answered(self, monkeypatch):
        from messaging import store as msg_store
        monkeypatch.setattr(msg_views.wa_config, "ai_available", lambda: True)
        monkeypatch.setattr(msg_views.wa_ai, "generate_reply", lambda thread: "hello!")
        cust = Contact.objects.create(name="Linked", phone="+60110000001")
        msg_store.get_or_create_thread("telegram", "555002")
        Thread.objects.filter(channel="telegram", key="555002").update(contact=cust)
        msg_store.append_message("telegram", "555002", "in", "hi", source="customer")
        msg = msg_views._maybe_ai_reply("telegram", "555002")
        assert msg is not None and msg["text"] == "hello!"


# ---- inbound media (download + display on every channel) ----------------------------

@pytest.mark.django_db
class TestInboundMedia:
    @pytest.fixture
    def media_dir(self, tmp_path, monkeypatch):
        from messaging import store as msg_store
        monkeypatch.setattr(msg_store, "MEDIA_DIR", tmp_path)
        return tmp_path

    @pytest.fixture
    def inline_media(self, monkeypatch):
        """Run the async media worker synchronously — tests must not spawn
        threads against the test DB. Applied AFTER quiet_hooks so it wins."""
        monkeypatch.setattr(msg_views, "_attach_media_async",
                            lambda label, worker: worker())

    def test_telegram_photo_downloads_and_serves(self, client, tg_account, superadmin,
                                                 quiet_hooks, inline_media, media_dir,
                                                 monkeypatch):
        fetched = []
        monkeypatch.setattr(
            msg_views.telegram_client, "download_file",
            lambda account, file_id: fetched.append(file_id) or (b"JPGBYTES", "file_9.jpg"))
        update = _tg_update(message_id=9)
        del update["message"]["text"]
        update["message"]["photo"] = [{"file_id": "small"}, {"file_id": "big"}]
        _post_update(client, update)
        assert fetched == ["big"]  # largest size variant wins
        msg = Thread.objects.get(channel="telegram", key="777").messages.get()
        assert msg.text == "[photo]"
        assert msg.media_type == "image"
        assert msg.media_path.endswith(".jpg")
        r = client.get(f"/api/messaging/media/{msg.pk}", **AUTH)
        assert r.status_code == 200
        assert b"".join(r.streaming_content) == b"JPGBYTES"
        # attachments are PII — no bearer role, no bytes
        assert client.get(f"/api/messaging/media/{msg.pk}").status_code == 401

    def test_telegram_document_pdf(self, client, tg_account, superadmin, quiet_hooks,
                                   inline_media, media_dir, monkeypatch):
        monkeypatch.setattr(msg_views.telegram_client, "download_file",
                            lambda account, file_id: (b"%PDF-1.4", "file_10.bin"))
        update = _tg_update(message_id=10)
        del update["message"]["text"]
        update["message"]["document"] = {"file_id": "doc1", "file_name": "statement.pdf"}
        _post_update(client, update)
        msg = Thread.objects.get(channel="telegram", key="777").messages.get()
        assert msg.media_type == "document"

    def test_animated_sticker_keeps_placeholder(self, client, tg_account, quiet_hooks,
                                                inline_media, media_dir, monkeypatch):
        monkeypatch.setattr(
            msg_views.telegram_client, "download_file",
            lambda account, file_id: pytest.fail("animated stickers must not download"))
        update = _tg_update(message_id=11)
        del update["message"]["text"]
        update["message"]["sticker"] = {"file_id": "st", "is_animated": True}
        _post_update(client, update)
        msg = Thread.objects.get(channel="telegram", key="777").messages.get()
        assert msg.media_path == "" and msg.text == "[sticker]"

    def test_messenger_attachments_first_on_row_extras_append(
            self, client, superadmin, quiet_hooks, inline_media, media_dir, monkeypatch):
        monkeypatch.setattr(msg_views.meta_client, "download_attachment",
                            lambda url: b"PDFBYTES" if url.endswith(".pdf") else b"IMGBYTES")
        payload = {"object": "page", "entry": [{"messaging": [{
            "sender": {"id": "psid1"},
            "message": {"mid": "m1", "attachments": [
                {"type": "image", "payload": {"url": "https://cdn.example/pic.png?sig=1"}},
                {"type": "file", "payload": {"url": "https://cdn.example/receipt.pdf"}},
            ]},
        }]}]}
        client.post("/api/messaging/webhook", data=json.dumps(payload),
                    content_type="application/json")
        msgs = list(Thread.objects.get(channel="facebook", key="psid1")
                    .messages.order_by("pk"))
        assert len(msgs) == 2
        assert msgs[0].text == "[attachment]"
        assert msgs[0].media_type == "image" and msgs[0].media_path.endswith(".png")
        assert msgs[1].text == "[document]"
        assert msgs[1].media_type == "document" and msgs[1].media_path.endswith(".pdf")
        r = client.get(f"/api/messaging/media/{msgs[1].pk}", **AUTH)
        assert b"".join(r.streaming_content) == b"PDFBYTES"

    def test_email_attachment_becomes_media_row(self, client, superadmin, quiet_hooks,
                                                media_dir, monkeypatch):
        Contact.objects.create(name="Cust", phone="+60111222333",
                                email="cust@example.com")
        monkeypatch.setattr(msg_views.msg_config, "imap_configured", lambda: True)
        msg_views._email_sync_state["last"] = 0.0
        mail = {"from_address": "cust@example.com", "from_name": "Cust",
                "subject": "Payment", "body": "receipt attached", "message_id": "<a1@x>",
                "attachments": [{"filename": "receipt.pdf", "content": b"%PDF-1.4"}]}
        monkeypatch.setattr(msg_views.imap_client, "fetch_new_messages", lambda: [mail])
        client.post("/api/messaging/email/sync", data=json.dumps({"force": True}),
                    content_type="application/json", **AUTH)
        msgs = list(Thread.objects.get(channel="email", key="cust@example.com")
                    .messages.order_by("pk"))
        assert len(msgs) == 2
        att = msgs[1]
        assert att.text == "[attachment] receipt.pdf"
        assert att.media_type == "document"
        r = client.get(f"/api/messaging/media/{att.pk}", **AUTH)
        assert r.status_code == 200
        assert b"".join(r.streaming_content) == b"%PDF-1.4"

    def test_media_endpoint_never_serves_whatsapp_rows(self, client, superadmin,
                                                       media_dir):
        # whatsapp files live under the whatsapp app's own dir + endpoint —
        # the messaging endpoint must not resolve their pks at all
        wa_store.get_or_create_thread("60123123123")
        m = wa_store.append_message("60123123123", "in", "[image]", source="customer",
                                    media_type="image", media_path="60123123123/x.jpg")
        assert client.get(f"/api/messaging/media/{m['pk']}", **AUTH).status_code == 404
