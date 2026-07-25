"""Conversation-queue tests (crm.OutboundJob + flows/queue.py + the engine's
enqueue path + GET /api/flows/queue).

Covers: sandbox enqueue/drain, per-account pacing gap, the daily cap, the
active-conversation cap (blocks new starts, lets in-flight conversations
continue, frees the slot on an inbound reply), quiet-hours deferral, opt-out
failure, the 24h-window rule (text fails / template passes), template
rendering from the variables_for catalog, send-time routing (sticky
assignment + banned failover), retry/backoff exhaustion, the engine action
enqueue (text + template) with run-context deliveries, and the queue endpoint
auth + payload shape.

Same fixture pattern as tests/test_flows.py / test_health_routing.py.
"""
import json
from datetime import date, timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from crm.models import (
    AccountAssignment,
    ChannelAccount,
    Contact,
    FlowRun,
    Message,
    OutboundJob,
    Profile,
    Record,
    Setting,
    Thread,
    Workflow,
)
from flows import engine, queue
from whatsapp import client as wa_client
from whatsapp import store as wa_store
from whatsapp import views as wa_views
from whatsapp import wa_config

SUPER_TOKEN = "test-super-token"
AGENT_TOKEN = "test-agent-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {SUPER_TOKEN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=SUPER_TOKEN)
    return user


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="agent", password="collect123")
    Profile.objects.create(user=user, role="agent", token=AGENT_TOKEN)
    return user


@pytest.fixture
def contact(db):
    """A contact with one open order, due 1 day ago — so the default
    "Days after due date >= 1" trigger matches."""
    c = Contact.objects.create(
        name="Ahmad Faiz", phone="+60123456789", company_name="Northwind")
    Record.objects.create(
        contact=c, type="order", external_id="ORD-500", title="Weekly box",
        status="open", amount=500, currency="RM",
        due_date=date.today() - timedelta(days=1),
        data={"tracking_url": "https://track.example/abc"})
    return c


@pytest.fixture
def calm(monkeypatch):
    """Deterministic environment: no quiet hours (a dev .env may set them),
    no env-default WhatsApp account leaking in from the developer's .env."""
    monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def zero_gaps(db):
    """Disable the pacing gap so tests exercise the OTHER gates in isolation."""
    Setting.objects.create(key="queue_min_gap_s", value="0")
    Setting.objects.create(key="queue_max_gap_s", value="0")


def _cloud(name="Cloud", pnid="1234567890"):
    return ChannelAccount.objects.create(
        channel="whatsapp", provider="cloud", name=name,
        external_id=pnid, access_token="tok")


def _baileys(name="QR A"):
    return ChannelAccount.objects.create(
        channel="whatsapp", provider="baileys", name=name)


def _open_window(phone, account=None, name=""):
    """Give the phone's thread a fresh inbound so a live 24h window is open."""
    thread = wa_store.get_or_create_thread(
        phone, name=name, account_id=str(account.pk) if account else None)
    wa_store.append_message(thread["wa_id"], "in", "hello", source="customer")
    return thread["wa_id"]


@pytest.fixture
def sent_texts(monkeypatch):
    calls = []

    def fake_send_text(account, to, text, reply_to_msg_id=None):
        calls.append({"account": account, "to": to, "text": text})
        return f"wamid.text{len(calls)}"

    monkeypatch.setattr(wa_client, "send_text", fake_send_text)
    return calls


@pytest.fixture
def sent_templates(monkeypatch):
    calls = []

    def fake_send_template(account, to, name, language, body_params=None):
        calls.append({"account": account, "to": to, "name": name,
                      "language": language, "params": list(body_params or [])})
        return f"wamid.tpl{len(calls)}"

    monkeypatch.setattr(wa_client, "send_template", fake_send_template)
    return calls


# ---- workflow builders (same JSON the builder saves) --------------------------------

def _trigger(node_id="t1", days=1):
    return {"id": node_id, "type": "trigger", "position": {"x": 0, "y": 0},
            "data": {"label": "overdue", "config": {
                "when": "Days after due date", "days": days,
                "window": "Any allowed hours", "repeat": "Once"}}}


def _text_action(node_id="a1",
                 script="Hi {{name}}, {{amount}} is due. {{tracking_url}}"):
    return {"id": node_id, "type": "action", "position": {"x": 0, "y": 100},
            "data": {"label": "remind", "config": {
                "channels": ["WhatsApp"], "script": script}}}


def _template_action(node_id="a1"):
    return {"id": node_id, "type": "action", "position": {"x": 0, "y": 100},
            "data": {"label": "remind", "config": {
                "channels": ["WhatsApp"], "waMode": "template",
                "templateName": "order_confirmation", "templateLanguage": "en",
                "templateVariables": {"1": "{{name}}", "2": "{{amount}}",
                                      "3": "{{due_date}}"}}}}


def _end(node_id="e1"):
    return {"id": node_id, "type": "end", "position": {"x": 0, "y": 300},
            "data": {"label": "end", "config": {"logCode": "DONE"}}}


def _edge(source, target):
    return {"id": f"e-{source}-{target}", "source": source, "target": target}


def _workflow(nodes, edges, flow_id="wf-test"):
    return Workflow.objects.create(
        flow_id=flow_id, name="Test flow", nodes=nodes, edges=edges, active=True)


# ---- sandbox happy path --------------------------------------------------------------

@pytest.mark.django_db
class TestSandbox:
    def test_enqueue_drain_stores_locally(self, contact, calm):
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "Hello Ahmad"})
        assert job.status == "queued"
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"
        assert job.last_error == "sandbox"  # status detail, not an error
        assert job.sent_at is not None
        msg = Message.objects.get(pk=job.sent_message_id)
        assert msg.text == "Hello Ahmad"
        assert msg.status == "sandbox"
        assert msg.source == "ai"

    def test_unknown_sandbox_template_fails(self, contact, calm):
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "no_such_template", "language": "en",
                             "variables": {}})
        queue.drain()
        job.refresh_from_db()
        assert job.status == "failed"
        assert "unknown sandbox template" in job.last_error

    def test_opt_out_fails_job(self, contact, calm):
        wa_id = wa_store.get_or_create_thread(contact.phone)["wa_id"]
        wa_store.update_thread(wa_id, {"opted_out": True})
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "hi"})
        queue.drain()
        job.refresh_from_db()
        assert job.status == "failed"
        assert "opted out" in job.last_error


# ---- pacing + caps --------------------------------------------------------------------

@pytest.mark.django_db
class TestPacing:
    def test_one_send_per_account_per_pass_then_gap(self, calm, sent_texts):
        # One-send-per-pass + the jittered gap are BAILEYS ban-avoidance pacing —
        # official Cloud accounts are exempt (they batch back-to-back, see
        # test_cloud_accounts_skip_human_pacing), so this must run on baileys.
        account = _baileys()
        a = Contact.objects.create(name="Alpha", phone="+60111000001")
        b = Contact.objects.create(name="Beta", phone="+60111000002")
        _open_window(a.phone, account)
        _open_window(b.phone, account)
        job_a = queue.enqueue("whatsapp", "text", a, a.phone, {"text": "one"})
        job_b = queue.enqueue("whatsapp", "text", b, b.phone, {"text": "two"})

        assert queue.drain() == 1  # one real send per account per pass
        job_a.refresh_from_db(); job_b.refresh_from_db()
        assert (job_a.status, job_b.status) == ("sent", "queued")

        # default randomized gap (queue_min/max_gap_s) since job_a's send has
        # not elapsed
        assert queue.drain() == 0
        job_b.refresh_from_db()
        assert job_b.status == "queued"
        assert len(sent_texts) == 1

    def test_daily_cap_blocks_further_sends(self, calm, zero_gaps, sent_texts):
        # ban-avoidance caps are for the unofficial transport — official Cloud
        # accounts are exempt (tier limits are Meta's job), so test on baileys
        _baileys()
        Setting.objects.create(key="queue_daily_cap", value="1")
        a = Contact.objects.create(name="Alpha", phone="+60111000001")
        b = Contact.objects.create(name="Beta", phone="+60111000002")
        _open_window(a.phone)
        _open_window(b.phone)
        queue.enqueue("whatsapp", "text", a, a.phone, {"text": "one"})
        job_b = queue.enqueue("whatsapp", "text", b, b.phone, {"text": "two"})

        assert queue.drain() == 1
        assert queue.drain() == 0  # cap of 1 sent job today reached
        job_b.refresh_from_db()
        assert job_b.status == "queued"

        Setting.objects.filter(key="queue_daily_cap").update(value="10")
        assert queue.drain() == 1  # proves the cap was the blocker
        job_b.refresh_from_db()
        assert job_b.status == "sent"

    def test_cloud_accounts_skip_human_pacing(self, calm, sent_texts):
        """Official-API accounts blast back-to-back: no jittered gap, no
        one-send-per-pass, no baileys daily cap — only queue_cloud_* apply."""
        _cloud()
        Setting.objects.create(key="queue_min_gap_s", value="9999")
        Setting.objects.create(key="queue_daily_cap", value="1")
        Setting.objects.create(key="queue_cloud_gap_s", value="0")
        a = Contact.objects.create(name="Alpha", phone="+60111000001")
        b = Contact.objects.create(name="Beta", phone="+60111000002")
        _open_window(a.phone)
        _open_window(b.phone)
        queue.enqueue("whatsapp", "text", a, a.phone, {"text": "one"})
        queue.enqueue("whatsapp", "text", b, b.phone, {"text": "two"})
        assert queue.drain() == 2  # both in ONE pass despite baileys-style caps
        assert len(sent_texts) == 2


@pytest.mark.django_db
class TestActiveConversationCap:
    def _awaiting_thread(self, account, phone="+60111000001", name="Alpha"):
        """A thread on this account awaiting a reply (last message outbound)."""
        thread = wa_store.get_or_create_thread(phone, name=name,
                                               account_id=str(account.pk))
        wa_store.append_message(thread["wa_id"], "out", "reminder", source="ai")
        return thread["wa_id"]

    def test_blocks_new_start_until_reply_frees_slot(self, calm, zero_gaps,
                                                     sent_texts):
        # conversation-slot caps are baileys pacing (cloud is exempt); pin the
        # template job to the baileys account (payload account_id) — it sends
        # as rendered text there, which is also why sent_texts is the mock
        account = _baileys()
        Setting.objects.create(key="queue_active_cap", value="1")
        Contact.objects.create(name="Alpha", phone="+60111000001")
        waiting_wa_id = self._awaiting_thread(account)

        b = Contact.objects.create(name="Beta", phone="+60111000002")
        job_b = queue.enqueue("whatsapp", "template", b, b.phone,
                              {"name": "order_confirmation", "language": "en",
                               "variables": {"1": "Beta", "2": "RM 1", "3": "today"},
                               "account_id": str(account.pk)})
        assert queue.drain() == 0  # at cap: a NEW conversation start must wait
        job_b.refresh_from_db()
        assert job_b.status == "queued"

        # the awaited contact replies -> thread stops awaiting -> slot freed
        wa_store.append_message(waiting_wa_id, "in", "ok noted", source="customer")
        assert queue.drain() == 1
        job_b.refresh_from_db()
        assert job_b.status == "sent"

    def test_in_flight_conversation_proceeds_at_cap(self, calm, zero_gaps,
                                                    sent_texts):
        account = _cloud()
        Setting.objects.create(key="queue_active_cap", value="1")
        Contact.objects.create(name="Alpha", phone="+60111000001")
        self._awaiting_thread(account)  # cap reached

        c = Contact.objects.create(name="Carol", phone="+60111000003")
        _open_window(c.phone, account, name="Carol")  # replying contact
        job_c = queue.enqueue("whatsapp", "text", c, c.phone, {"text": "follow up"})
        assert queue.drain() == 1  # reply-in-flight is allowed through the cap
        job_c.refresh_from_db()
        assert job_c.status == "sent"


# ---- quiet hours -----------------------------------------------------------------------

@pytest.mark.django_db
class TestQuietHours:
    def test_due_jobs_deferred_to_quiet_end(self, contact, monkeypatch):
        monkeypatch.setattr(wa_config, "_env_account", lambda: None)
        monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: True)
        Setting.objects.create(key="quiet_end", value="08:00")
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "hi"})
        assert queue.drain() == 0
        job.refresh_from_db()
        assert job.status == "queued"
        assert job.not_before is not None
        assert job.not_before > timezone.now()
        # and it stays parked: the not_before gate skips it next pass too
        assert queue.drain() == 0


# ---- 24h window rule ---------------------------------------------------------------------

@pytest.mark.django_db
class TestWindowRule:
    def test_text_fails_closed_window_template_passes(self, calm, zero_gaps,
                                                      contact, sent_templates):
        _cloud()
        # live thread, contact never wrote in -> window closed
        wa_store.get_or_create_thread(contact.phone, name=contact.name)
        text_job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                                 {"text": "hi"})
        tpl_job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                                {"name": "order_confirmation", "language": "en",
                                 "variables": {"1": "A", "2": "B", "3": "C"}})
        assert queue.drain() == 1
        text_job.refresh_from_db(); tpl_job.refresh_from_db()
        assert text_job.status == "failed"
        assert "window closed" in text_job.last_error
        assert tpl_job.status == "sent"
        assert sent_templates[0]["name"] == "order_confirmation"

    def test_baileys_thread_is_window_exempt(self, calm, zero_gaps, contact,
                                             sent_texts):
        _baileys()
        # this test is about the WINDOW rule, not the cold-text guard —
        # disarm the guard so "no inbound ever" isolates the window exemption
        Setting.objects.create(key="cold_text_guard", value="allow")
        # no inbound ever, but baileys threads have no 24h window
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "hi from qr"})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"
        assert sent_texts[0]["text"] == "hi from qr"

    def test_baileys_template_sends_rendered_body_as_text(self, calm, zero_gaps,
                                                          contact, sent_texts):
        _baileys()
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "order_confirmation", "language": "en",
                             "variables": {"1": "Ahmad", "2": "RM 500.00",
                                           "3": "01 Aug 2026"}})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"
        assert "Hi Ahmad" in sent_texts[0]["text"]
        assert "RM 500.00" in sent_texts[0]["text"]


# ---- routing (send-time account resolution) -------------------------------------------------

@pytest.mark.django_db
class TestRouting:
    def test_assigned_account_used(self, calm, zero_gaps, contact, sent_templates):
        _cloud("Cloud 1", pnid="111")
        acct2 = _cloud("Cloud 2", pnid="222")
        AccountAssignment.objects.create(contact=contact, channel="whatsapp",
                                         account=acct2)
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "order_confirmation", "language": "en",
                             "variables": {}})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.account_id == acct2.pk
        assert sent_templates[0]["account"]["id"] == str(acct2.pk)

    def test_banned_assignment_fails_over(self, calm, zero_gaps, contact,
                                          sent_templates):
        acct1 = _cloud("Cloud 1", pnid="111")
        acct2 = _cloud("Cloud 2", pnid="222")
        AccountAssignment.objects.create(contact=contact, channel="whatsapp",
                                         account=acct1)
        acct1.health_status = "banned"
        acct1.save()
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "order_confirmation", "language": "en",
                             "variables": {}})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.account_id == acct2.pk
        assert sent_templates[0]["account"]["id"] == str(acct2.pk)
        assignment = AccountAssignment.objects.get(contact=contact)
        assert assignment.account_id == acct2.pk  # sticky failover persisted

    def test_no_eligible_account_retries(self, calm, zero_gaps, contact):
        acct = _cloud()
        acct.health_status = "banned"
        acct.save()
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "hi"})
        assert queue.drain() == 0
        job.refresh_from_db()
        assert job.status == "queued"
        assert job.attempts == 1
        assert "no eligible account" in job.last_error
        assert job.not_before is not None


# ---- retries / backoff ------------------------------------------------------------------------

@pytest.mark.django_db
class TestRetries:
    def test_backoff_then_failed_after_three_attempts(self, calm, zero_gaps,
                                                      contact, monkeypatch):
        _cloud()

        def boom(account, to, name, language, body_params=None):
            raise wa_client.WhatsAppError("Meta API 500: boom (code 131000)")

        monkeypatch.setattr(wa_client, "send_template", boom)
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "order_confirmation", "language": "en",
                             "variables": {}})
        for attempt in (1, 2):
            queue.drain()
            job.refresh_from_db()
            assert job.status == "queued"
            assert job.attempts == attempt
            assert "boom" in job.last_error
            assert job.not_before is not None and job.not_before > timezone.now()
            job.not_before = timezone.now() - timedelta(seconds=1)
            job.save(update_fields=["not_before"])

        queue.drain()
        job.refresh_from_db()
        assert job.status == "failed"
        assert job.attempts == 3
        assert "boom" in job.last_error


# ---- engine integration -------------------------------------------------------------------------

@pytest.mark.django_db
class TestEngineEnqueues:
    def test_text_action_enqueues_then_delivery_recorded(self, contact, calm):
        _workflow([_trigger(), _text_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        assert engine.evaluate_triggers() == 1
        job = OutboundJob.objects.get()
        assert (job.channel, job.kind, job.status) == ("whatsapp", "text", "queued")
        assert job.contact == contact
        assert job.to_address == contact.phone
        assert "Ahmad Faiz" in job.payload["text"]
        assert "RM 500.00" in job.payload["text"]
        run = FlowRun.objects.get()
        assert job.flow_run == run
        assert job.flow_node == "a1"
        assert any("whatsapp queued (text" in e["event"] for e in run.log)

        assert queue.drain() == 1  # sandbox send
        run.refresh_from_db(); job.refresh_from_db()
        deliveries = run.context.get("deliveries")
        assert deliveries == [{"job_id": job.pk,
                               "message_id": job.sent_message_id,
                               "external_id": ""}]

    def test_template_action_renders_catalog_variables(self, contact, calm):
        _workflow([_trigger(), _template_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        job = OutboundJob.objects.get()
        assert job.kind == "template"
        expected = engine.variables_for(contact)
        assert job.payload["name"] == "order_confirmation"
        assert job.payload["language"] == "en"
        assert job.payload["variables"] == {"1": "Ahmad Faiz",
                                            "2": "RM 500.00",
                                            "3": expected["due_date"]}

        queue.drain()  # sandbox: demo body rendered with those values
        msg = Message.objects.filter(direction="out", status="sandbox").get()
        assert "Hi Ahmad Faiz" in msg.text
        assert "RM 500.00" in msg.text
        assert expected["due_date"] in msg.text
        run = FlowRun.objects.get()
        assert len(run.context["deliveries"]) == 1

    def test_live_template_send_uses_positional_params(self, contact, calm,
                                                       zero_gaps, sent_templates):
        _cloud()
        _workflow([_trigger(), _template_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()  # enroll + drain in one pass
        assert sent_templates[0]["name"] == "order_confirmation"
        assert sent_templates[0]["params"][0] == "Ahmad Faiz"
        assert sent_templates[0]["params"][1] == "RM 500.00"
        job = OutboundJob.objects.get()
        assert job.status == "sent"
        assert job.sent_message.external_id == "wamid.tpl1"
        run = FlowRun.objects.get()
        assert run.context["deliveries"][0]["external_id"] == "wamid.tpl1"

    def test_tick_reports_drained(self, contact, calm):
        _workflow([_trigger(), _text_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        result = engine.tick()
        assert result["started"] == 1
        assert result["drained"] == 1
        assert Message.objects.filter(direction="out", status="sandbox").exists()

    def test_linked_numbers_ignored_without_all_numbers_flag(self, contact, calm):
        """Default: one job to the CRM primary — a second linked WhatsApp
        number changes nothing until the node opts in."""
        wa_store.get_or_create_thread("60111222333")
        Thread.objects.filter(channel="whatsapp", key="60111222333").update(
            contact=contact)
        _workflow([_trigger(), _text_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        job = OutboundJob.objects.get()
        assert job.to_address == contact.phone

    def test_all_linked_fans_out_deduped(self, contact, calm):
        """config.allLinked: one job per linked WhatsApp number, and a linked
        thread that IS the primary number never doubles the send."""
        for key in ("60111222333", "60123456789"):  # extra + dup-of-primary
            wa_store.get_or_create_thread(key)
            Thread.objects.filter(channel="whatsapp", key=key).update(
                contact=contact)
        action = _text_action()
        action["data"]["config"]["allLinked"] = True
        _workflow([_trigger(), action, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        jobs = list(OutboundJob.objects.order_by("pk"))
        assert [j.to_address for j in jobs] == [contact.phone, "60111222333"]
        run = FlowRun.objects.get()
        assert sum("whatsapp queued (text" in e["event"] for e in run.log) == 2

    def test_legacy_wa_all_numbers_key_still_honoured(self, contact, calm):
        """Flows saved while the switch was WhatsApp-only carry waAllNumbers —
        the engine treats it exactly like allLinked."""
        wa_store.get_or_create_thread("60111222333")
        Thread.objects.filter(channel="whatsapp", key="60111222333").update(
            contact=contact)
        action = _text_action()
        action["data"]["config"]["waAllNumbers"] = True
        _workflow([_trigger(), action, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        assert OutboundJob.objects.count() == 2

    def test_email_defaults_to_primary_only(self, contact, calm, monkeypatch):
        from messaging import msg_config as messaging_config
        from messaging import store as msg_store
        monkeypatch.setattr(messaging_config, "email_configured", lambda: False)
        contact.email = "ahmad@example.com"
        contact.save(update_fields=["email"])
        msg_store.get_or_create_thread("email", "ahmad.work@example.com")
        Thread.objects.filter(channel="email", key="ahmad.work@example.com").update(
            contact=contact)
        action = _text_action()
        action["data"]["config"]["channels"] = ["Email"]
        _workflow([_trigger(), action, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        sent_to = list(Message.objects.filter(direction="out", status="sandbox")
                       .values_list("thread__key", flat=True))
        assert sent_to == ["ahmad@example.com"]

    def test_all_linked_fans_out_email_too(self, contact, calm, monkeypatch):
        """The generic switch covers email: every linked email thread gets the
        send, deduped case-insensitively against the CRM address."""
        from messaging import msg_config as messaging_config
        from messaging import store as msg_store
        monkeypatch.setattr(messaging_config, "email_configured", lambda: False)
        contact.email = "ahmad@example.com"
        contact.save(update_fields=["email"])
        for key in ("ahmad.work@example.com", "ahmad@example.com"):  # extra + dup
            msg_store.get_or_create_thread("email", key)
            Thread.objects.filter(channel="email", key=key).update(contact=contact)
        action = _text_action()
        action["data"]["config"]["channels"] = ["Email"]
        action["data"]["config"]["allLinked"] = True
        _workflow([_trigger(), action, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.evaluate_triggers()
        sent_to = set(Message.objects.filter(direction="out", status="sandbox")
                      .values_list("thread__key", flat=True))
        assert sent_to == {"ahmad@example.com", "ahmad.work@example.com"}
        run = FlowRun.objects.get()
        assert sum("email sent (sandbox)" in e["event"] for e in run.log) == 2


# ---- /api/flows/queue endpoint --------------------------------------------------------------------

@pytest.mark.django_db
class TestQueueEndpoint:
    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/flows/queue").status_code == 401

    def test_any_role_reads_jobs(self, client, agent_user, contact, calm):
        _workflow([_trigger(), _text_action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        r = client.get("/api/flows/queue", **AGENT)
        assert r.status_code == 200
        jobs = r.json()["jobs"]
        assert len(jobs) == 1
        job = jobs[0]
        assert job["status"] == "sent"
        assert job["kind"] == "text"
        assert job["channel"] == "whatsapp"
        assert job["contact"] == "Ahmad Faiz"
        assert job["to"] == contact.phone
        assert job["account"] is None  # sandbox: no account resolved
        assert job["lastError"] == "sandbox"
        assert job["sentAt"] is not None
        assert job["flow"] == {"run": FlowRun.objects.get().pk,
                               "workflow": "Test flow", "node": "a1"}

    def test_limit_param(self, client, agent_user, contact, calm):
        for i in range(3):
            queue.enqueue("whatsapp", "text", contact, contact.phone,
                          {"text": f"msg {i}"})
        r = client.get("/api/flows/queue?limit=2", **AGENT)
        assert len(r.json()["jobs"]) == 2


# ---- queued AI replies ("one human per account") ----------------------------------

@pytest.fixture
def ai_ready(monkeypatch):
    """Pretend a chat-AI provider key is configured (both the views guard and
    the queue's drain-time re-check read the same accessor)."""
    monkeypatch.setattr(wa_config, "ai_available", lambda: True)


@pytest.fixture
def ai_replies(monkeypatch):
    """Capture generate_reply calls; each call records the thread's inbound
    count so tests can prove the reply is generated at SEND time."""
    calls = []

    def fake_generate(thread):
        calls.append({"wa_id": thread["wa_id"],
                      "inbound": len([m for m in thread["messages"]
                                      if m["dir"] == "in"])})
        return "Sure — let me check your account."

    monkeypatch.setattr("whatsapp.ai.generate_reply", fake_generate)
    return calls


def _conversation_thread(account, phone, text="can I pay next week?"):
    thread = wa_store.get_or_create_thread(phone, account_id=str(account.pk))
    wa_store.append_message(thread["wa_id"], "in", text, source="customer")
    return wa_store.load_thread(thread["wa_id"])


@pytest.mark.django_db
class TestQueuedAiReply:
    def test_inbound_enqueues_dedupes_and_drain_sends(self, contact, calm,
                                                      zero_gaps, ai_ready,
                                                      ai_replies, sent_texts):
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        assert wa_views._maybe_ai_reply(thread) is None  # queued, not sent
        job = OutboundJob.objects.get()
        assert (job.channel, job.kind, job.status) == ("whatsapp", "ai_reply", "queued")
        assert job.to_address == thread["wa_id"]
        assert job.contact == contact
        assert job.payload == {}
        assert ai_replies == []  # nothing generated at enqueue time
        # dedupe: a second contact message keeps the single pending job
        wa_store.append_message(thread["wa_id"], "in", "hello??", source="customer")
        assert wa_views._maybe_ai_reply(wa_store.load_thread(thread["wa_id"])) is None
        assert OutboundJob.objects.count() == 1

        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"
        assert job.account_id == account.pk
        # generated ONCE at send time, seeing BOTH inbound messages
        assert ai_replies == [{"wa_id": thread["wa_id"], "inbound": 2}]
        assert sent_texts[0]["text"] == "Sure — let me check your account."
        assert sent_texts[0]["account"]["id"] == str(account.pk)
        msg = Message.objects.get(pk=job.sent_message_id)
        assert msg.source == "ai" and msg.direction == "out"

    def test_quiet_hours_enqueue_parks_until_window_opens(self, contact, calm,
                                                          ai_ready, ai_replies,
                                                          monkeypatch):
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        Setting.objects.create(key="quiet_end", value="08:00")
        monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: True)
        assert wa_views._maybe_ai_reply(thread) is None
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.not_before is not None
        assert job.not_before > timezone.now()  # parked to the quiet end
        assert queue.drain() == 0  # and the drain gate holds it meanwhile
        assert ai_replies == []

    def test_drain_rechecks_guards(self, contact, calm, zero_gaps, ai_ready,
                                   ai_replies):
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        wa_views._maybe_ai_reply(thread)
        # the operator turns AI off between enqueue and drain
        wa_store.update_thread(thread["wa_id"], {"ai_enabled": False})
        assert queue.drain() == 0
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.status == "cancelled"
        assert "AI disabled" in job.last_error
        assert ai_replies == []  # no provider call for a dead job

    def test_drain_cancels_opted_out(self, contact, calm, zero_gaps, ai_ready,
                                     ai_replies):
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        wa_views._maybe_ai_reply(thread)
        wa_store.update_thread(thread["wa_id"], {"opted_out": True})
        queue.drain()
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.status == "cancelled"
        assert "opted out" in job.last_error

    def test_drain_cancels_unlinked_contact(self, contact, calm, zero_gaps,
                                            ai_ready, ai_replies):
        # coverage rule: the contact link is unpicked between enqueue and drain
        # (e.g. operator unlinks a mis-matched chat) — no AI answer goes out
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        wa_views._maybe_ai_reply(thread)
        Thread.objects.filter(channel="whatsapp",
                              key=thread["wa_id"]).update(contact=None)
        assert queue.drain() == 0
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.status == "cancelled"
        assert "no linked contact" in job.last_error
        assert ai_replies == []  # no provider call for a dead job

    def test_provider_error_retries(self, contact, calm, zero_gaps, ai_ready,
                                    monkeypatch):
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        wa_views._maybe_ai_reply(thread)

        def boom(thread):
            raise RuntimeError("429 rate limited")

        monkeypatch.setattr("whatsapp.ai.generate_reply", boom)
        queue.drain()
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.status == "queued"  # transient — backoff, not dead
        assert job.attempts == 1
        assert "429" in job.last_error

    def test_instant_mode_unchanged(self, contact, calm, ai_ready, ai_replies,
                                    sent_texts):
        Setting.objects.create(key="ai_reply_mode", value="instant")
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        reply = wa_views._maybe_ai_reply(thread)
        assert reply is not None  # sent inline, exactly like before
        assert OutboundJob.objects.count() == 0
        assert len(sent_texts) == 1
        assert ai_replies[0]["wa_id"] == thread["wa_id"]

    def test_sandbox_stays_instant_in_queued_mode(self, contact, calm, ai_ready,
                                                  ai_replies):
        # no accounts at all: demos must not lag behind queue pacing
        thread = _conversation_thread_sandbox(contact.phone)
        reply = wa_views._maybe_ai_reply(thread)
        assert reply is not None
        assert reply["status"] == "sandbox"
        assert OutboundJob.objects.count() == 0

    def test_reminder_role_account_keeps_instant_path(self, contact, calm,
                                                      ai_ready, ai_replies,
                                                      sent_texts):
        account = _cloud()  # reminder by default
        thread = _conversation_thread(account, contact.phone)
        wa_store.update_thread(thread["wa_id"], {"ai_enabled": True})  # operator opt-in
        reply = wa_views._maybe_ai_reply(wa_store.load_thread(thread["wa_id"]))
        assert reply is not None  # queued mode only paces conversation numbers
        assert OutboundJob.objects.count() == 0
        assert len(sent_texts) == 1


def _conversation_thread_sandbox(phone, text="can I pay next week?"):
    thread = wa_store.get_or_create_thread(phone)
    wa_store.append_message(thread["wa_id"], "in", text, source="customer")
    return wa_store.load_thread(thread["wa_id"])


# ---- cold-text guard (queue jobs) --------------------------------------------------

@pytest.mark.django_db
class TestQueueColdTextGuard:
    def test_cold_text_job_blocked(self, contact, calm, zero_gaps, sent_texts):
        _baileys()  # conversation role; the contact never wrote in
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "cold nudge"})
        assert queue.drain() == 0
        job.refresh_from_db()
        assert job.status == "failed"
        assert "personal (conversation) number" in job.last_error
        assert sent_texts == []

    def test_cold_text_job_warn_sends_with_note(self, contact, calm, zero_gaps,
                                                sent_texts):
        _baileys()
        Setting.objects.create(key="cold_text_guard", value="warn")
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "cold nudge"})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"
        assert Message.objects.filter(source="system",
                                      text__contains="Cold outreach warning").exists()

    def test_warm_thread_and_templates_unaffected(self, contact, calm, zero_gaps,
                                                  sent_texts, sent_templates):
        account = _baileys()
        _open_window(contact.phone, account)  # contact-initiated: warm
        job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                            {"text": "follow up"})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.status == "sent"


# ---- role-aware queue routing -------------------------------------------------------

@pytest.mark.django_db
class TestQueueRoleRouting:
    def test_text_goes_conversation_template_goes_reminder(self, contact, calm,
                                                           zero_gaps, sent_texts,
                                                           sent_templates):
        conv = _baileys("Personal")
        biz = _cloud("Biz", pnid="222")
        _open_window(contact.phone, conv)  # warm conversation thread
        text_job = queue.enqueue("whatsapp", "text", contact, contact.phone,
                                 {"text": "negotiation ping"})
        tpl_job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                                {"name": "order_confirmation", "language": "en",
                                 "variables": {"1": "A", "2": "B", "3": "C"}})
        assert queue.drain() == 2  # two different accounts, one send each
        text_job.refresh_from_db(); tpl_job.refresh_from_db()
        assert text_job.account_id == conv.pk       # conversation layer
        assert tpl_job.account_id == biz.pk         # reminder cannon
        assert sent_texts[0]["account"]["id"] == str(conv.pk)
        assert sent_templates[0]["account"]["id"] == str(biz.pk)
        # the sticky assignment stays on the conversation number — the
        # template send was a capability override, not a failover
        asg = AccountAssignment.objects.get(contact=contact, channel="whatsapp")
        assert asg.account_id == conv.pk

    def test_template_prefers_reminder_over_conversation_cloud(self, contact, calm,
                                                               zero_gaps,
                                                               sent_templates):
        conv_cloud = ChannelAccount.objects.create(
            channel="whatsapp", provider="cloud", name="Conv Cloud",
            external_id="111", access_token="tok", config={"role": "conversation"})
        biz = _cloud("Biz", pnid="222")
        assert conv_cloud.pk < biz.pk  # preference must beat pk order
        job = queue.enqueue("whatsapp", "template", contact, contact.phone,
                            {"name": "order_confirmation", "language": "en",
                             "variables": {}})
        assert queue.drain() == 1
        job.refresh_from_db()
        assert job.account_id == biz.pk
        assert sent_templates[0]["account"]["id"] == str(biz.pk)


@pytest.mark.django_db
class TestFlowOwnedReplyAtDrain:
    def test_drain_cancels_when_flow_consumed_the_reply(self, contact, calm,
                                                        zero_gaps, ai_ready,
                                                        ai_replies):
        from django.utils import timezone as tz
        account = _baileys()
        thread = _conversation_thread(account, contact.phone)
        wa_views._maybe_ai_reply(thread)  # enqueues the queued-mode ai_reply
        wf = Workflow.objects.create(flow_id="wf-x", name="x", nodes=[], edges=[],
                                     active=True)
        FlowRun.objects.create(workflow=wf, contact=contact, trigger_key="t1",
                               current_node="e2", status="done",
                               context={"answered": True}, finished_at=tz.now())
        assert queue.drain() == 0
        job = OutboundJob.objects.get(kind="ai_reply")
        assert job.status == "cancelled"
        assert "flow owns" in job.last_error
        assert ai_replies == []  # no provider call for a flow-handled reply


# ---- {{account_number}}: the number a template tells the contact to reply to ----------

@pytest.mark.django_db
class TestAccountNumberVariable:
    """Resolved at SEND time from the contact's assigned CONVERSATION number, so
    a reminder blasted from the Cloud business number quotes the personal
    number that will actually answer — and keeps quoting a live one after a
    failover moves the contact."""

    CLOUD_SENDER = {"provider": "cloud", "phone_number_id": "1234567890", "name": "Cloud"}

    def _assign(self, contact, account, pinned=True):
        AccountAssignment.objects.update_or_create(
            contact=contact, channel="whatsapp",
            defaults={"account": account, "pinned": pinned})

    def test_quotes_the_assigned_conversation_number(self, contact, calm):
        a = _baileys("Personal A")
        a.external_id = "60111111111"
        a.save(update_fields=["external_id"])
        self._assign(contact, a)
        assert queue._account_vars(contact, self.CLOUD_SENDER)["account_number"] == "+60111111111"

    def test_a_pin_does_not_keep_quoting_a_dead_number(self, contact, calm):
        """The pin is deliberately NOT honoured once the account is down —
        routing fails the contact over and clears it, and the template must
        follow, or we would print a dead number to the contact."""
        dead = _baileys("Personal A")
        dead.external_id = "60111111111"
        dead.save(update_fields=["external_id"])
        alive = _baileys("Personal B")
        alive.external_id = "60122222222"
        alive.save(update_fields=["external_id"])
        self._assign(contact, dead, pinned=True)
        dead.health_status = "banned"
        dead.save(update_fields=["health_status"])

        assert queue._account_vars(contact, self.CLOUD_SENDER)["account_number"] == "+60122222222"
        moved = AccountAssignment.objects.get(contact=contact, channel="whatsapp")
        assert moved.account_id == alive.pk and moved.pinned is False

    def test_all_numbers_down_falls_back_to_support_contact(self, contact, calm):
        a = _baileys("Personal A")
        a.external_id = "60111111111"
        a.health_status = "banned"
        a.save(update_fields=["external_id", "health_status"])
        self._assign(contact, a)
        Setting.objects.create(key="support_contact", value="+60 3-1234 5678")
        assert queue._account_vars(contact, self.CLOUD_SENDER)["account_number"] == "+60 3-1234 5678"

    def test_no_number_and_no_support_contact_never_ships_the_placeholder(self, contact, calm):
        """The recovery case with nothing truthful to say — retry rather than
        send a contact a literal {{account_number}}."""
        a = _baileys("Personal A")
        a.health_status = "banned"
        a.save(update_fields=["health_status"])
        self._assign(contact, a)
        cloud = _cloud()
        job = OutboundJob.objects.create(
            channel="whatsapp", kind="template", contact=contact,
            to_address="60123456789", status="queued",
            payload={"name": "auto_pay_reminder", "language": "en",
                     "variables": {"1": "{{whatsapp_assignment_account_number}}"}})
        queue._apply_account_vars(job, wa_config._serialize(cloud))
        assert queue._unrendered_account_var(job) is True


@pytest.mark.django_db
class TestCallQueue:
    """kind="call" jobs: a WhatsApp number carries exactly ONE call, so the
    queue serialises dials on it instead of letting two flow runs collide (the
    gateway 502s the loser). "Busy" is a WAIT; only compliance refusals fail a
    call for good, and a call that will never be placed un-parks its run."""

    @pytest.fixture(autouse=True)
    def _fake_gateway(self, db, monkeypatch):
        """Nothing may reach the real gateway or LiveKit. The fake mirrors the
        real start_call closely enough to matter: a placed call marks the
        thread live and the gateway then reports the number busy, which is what
        the queue serialises on. `self.hang_up()` ends it.

        The post-call breather is zeroed here so tests exercise the busy gate
        in isolation; test_call_gap_holds_the_next_dial sets it back."""
        from whatsapp import baileys_calls
        Setting.objects.update_or_create(key="queue_call_gap_s",
                                         defaults={"value": "0"})
        self.dials = []
        self.busy = {"on": False}
        monkeypatch.setattr(baileys_calls, "_gateway_call_active",
                            lambda account: self.busy["on"])

        def fake_start(wa_id, thread, account, voice_provider="", voice_id="",
                       force=False, script=""):
            room = f"wa-call-{len(self.dials) + 1}"
            self.dials.append({"wa_id": wa_id, "account": str(account.get("id")),
                               "script": script, "room": room})
            wa_store.update_thread(wa_id, {"call": {"call_id": "c1", "room": room,
                                                    "status": "connecting"}})
            self.busy["on"] = True
            return {"call_id": "c1", "status": "connecting", "room": room}

        monkeypatch.setattr(baileys_calls, "start_call", fake_start)

    def hang_up(self):
        """End whatever call is live — the gateway goes quiet and the thread
        rows stop reading busy."""
        self.busy["on"] = False
        for t in Thread.objects.filter(channel="whatsapp"):
            if t.call_status in ("connecting", "ringing", "in_call"):
                wa_store.update_thread(t.key, {"call": {"status": "ended"}})

    def _call_job(self, contact, to="60123456789", **payload):
        body = {"transport": "baileys", "label": "AI call", "script": "",
                "voice_provider": "", "voice": "", "extra": {}}
        body.update(payload)
        return OutboundJob.objects.create(
            channel="whatsapp", kind="call", contact=contact,
            to_address=to, status="queued", payload=body)

    def _second_contact(self):
        return Contact.objects.create(name="Siti", phone="+60127654321")

    def test_one_call_per_number_the_second_waits(self, contact, calm, zero_gaps):
        """The whole point: two contacts, one number — the second dial is held
        for a later pass rather than colliding with the first."""
        _baileys("Personal A")
        other = self._second_contact()
        self._call_job(contact)
        self._call_job(other, to="60127654321")
        queue.drain()
        assert len(self.dials) == 1                       # only one went out
        placed = OutboundJob.objects.filter(status="sent")
        waiting = OutboundJob.objects.filter(status="queued")
        assert placed.count() == 1 and waiting.count() == 1
        # the held job burned no attempt — waiting is not failing
        assert waiting.first().attempts == 0

    def test_waiting_call_goes_out_once_the_number_is_free(self, contact, calm, zero_gaps):
        """"After the call finishes, the next one goes" — the behaviour the
        queue exists for."""
        _baileys("Personal A")
        other = self._second_contact()
        self._call_job(contact)
        second = self._call_job(other, to="60127654321")

        queue.drain()
        second.refresh_from_db()
        assert second.status == "queued"      # held — the number is on a call

        queue.drain()                         # still on the call: still held
        second.refresh_from_db()
        assert second.status == "queued" and len(self.dials) == 1

        self.hang_up()
        queue.drain()
        second.refresh_from_db()
        assert second.status == "sent"
        assert len(self.dials) == 2
        assert self.dials[1]["wa_id"] == "60127654321"

    def test_gateway_busy_reply_is_a_wait_not_a_failure(self, contact, calm, zero_gaps, monkeypatch):
        """The exact 502 that used to kill the step for good."""
        _baileys("Personal A")
        from whatsapp import baileys_calls
        monkeypatch.setattr(
            baileys_calls, "start_call",
            lambda *a, **k: {"error": "Baileys gateway 502: account 3 already has a "
                                      "live call (+60111) — one call at a time"})
        job = self._call_job(contact)
        queue.drain()
        job.refresh_from_db()
        assert (job.status, job.attempts) == ("queued", 0)

    def test_opt_out_fails_the_call_and_unparks_the_run(self, contact, calm, zero_gaps, monkeypatch):
        """A call that can never be placed must not leave its run parked on a
        reply — a waiting run blocks the trigger from re-enrolling forever."""
        _baileys("Personal A")
        from whatsapp import baileys_calls
        monkeypatch.setattr(
            baileys_calls, "start_call",
            lambda *a, **k: {"error": "This contact opted out (STOP) — calling "
                                      "them is not allowed."})
        wf = Workflow.objects.create(flow_id="wf-c", name="C", nodes=[], edges=[])
        run = FlowRun.objects.create(workflow=wf, contact=contact, trigger_key="t1",
                                     current_node="d1", status="waiting", wait_for="reply")
        job = self._call_job(contact)
        job.flow_run = run
        job.save(update_fields=["flow_run"])
        queue.drain()
        job.refresh_from_db()
        run.refresh_from_db()
        assert job.status == "failed"
        assert run.status == "done" and run.wait_for == ""

    def test_do_not_call_is_rechecked_at_dial_time(self, contact, calm, zero_gaps):
        _baileys("Personal A")
        job = self._call_job(contact)
        contact.do_not_call = True          # set AFTER the job was queued
        contact.save(update_fields=["do_not_call"])
        queue.drain()
        job.refresh_from_db()
        assert job.status == "failed" and not self.dials

    def test_script_rides_the_queued_call(self, contact, calm, zero_gaps):
        _baileys("Personal A")
        self._call_job(contact, script="Hello Ahmad, please pay.")
        queue.drain()
        assert self.dials[0]["script"] == "Hello Ahmad, please pay."

    def test_call_gap_holds_the_next_dial(self, contact, calm):
        """Back-to-back dials on one number get a breather even when it's free."""
        _baileys("Personal A")
        Setting.objects.update_or_create(key="queue_call_gap_s",
                                         defaults={"value": "600"})
        other = self._second_contact()
        self._call_job(contact)
        self._call_job(other, to="60127654321")
        queue.drain()
        self.hang_up()  # number is free again, but the gap has not elapsed
        queue.drain()
        assert len(self.dials) == 1

    def test_stale_call_row_does_not_wedge_the_number(self, contact, calm, zero_gaps):
        """A crash can leave a thread reading in_call forever. The gateway is
        the ground truth, so a busy-looking row it disagrees with is cleared."""
        account = _baileys("Personal A")
        thread = wa_store.get_or_create_thread("+60129999999",
                                               account_id=str(account.pk))
        wa_store.update_thread(thread["wa_id"], {"call": {"status": "in_call"}})
        from whatsapp import baileys_calls
        assert baileys_calls.account_call_busy(wa_config._serialize(account)) is False
        assert (Thread.objects.get(key=thread["wa_id"]).call_status) == "idle"
