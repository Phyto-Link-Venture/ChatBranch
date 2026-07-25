"""Flow builder persistence + execution engine tests.

Covers: /api/flows/library auth + round-trip sync, trigger enrollment with
repeat dedupe, action fan-out (sandbox WhatsApp send with {{variable}}
rendering), decision parking + resume on inbound reply (yes/no paths),
Wait-N-days edges resumed by the ticker, handover notification fan-out,
and the reply classifier.
"""
import json
from datetime import date, timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from crm.models import (
    AgentSkill, Contact, FlowRun, Message, Notification, OutboundJob, Profile,
    Record, Thread, Workflow,
)
from flows import engine
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
    """Deterministic engine environment: no quiet hours (a dev .env may set
    them), no live WhatsApp account (sandbox sends only)."""
    monkeypatch.setattr(wa_config, "in_quiet_hours", lambda now=None: False)
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


def _workflow(nodes, edges, active=True, flow_id="wf-test"):
    return Workflow.objects.create(
        flow_id=flow_id, name="Test flow", nodes=nodes, edges=edges, active=active)


def _trigger(node_id="t1", days=1, repeat="Once", contacts=None):
    cfg = {"when": "Days after due date", "days": days,
           "window": "Any allowed hours", "repeat": repeat}
    if contacts is not None:
        cfg["contacts"] = contacts
    return {"id": node_id, "type": "trigger", "position": {"x": 0, "y": 0},
            "data": {"label": "overdue", "config": cfg}}


def _action(node_id="a1", script="Hi {{name}}, {{amount}} is due. {{tracking_url}}",
            channels=("WhatsApp",)):
    return {"id": node_id, "type": "action", "position": {"x": 0, "y": 100},
            "data": {"label": "remind", "config": {
                "skill": "", "channels": list(channels), "script": script}}}


def _end(node_id="e1", code="DONE"):
    return {"id": node_id, "type": "end", "position": {"x": 0, "y": 300},
            "data": {"label": "end", "config": {"logCode": code}}}


def _edge(source, target, eid=None, handle=None, timing=None, wait_days=None):
    edge = {"id": eid or f"e-{source}-{target}", "source": source, "target": target}
    if handle:
        edge["sourceHandle"] = handle
        edge["label"] = handle.title()
    if timing:
        edge["data"] = {"config": {"timing": timing, "waitDays": wait_days}}
    return edge


# ---- library API ---------------------------------------------------------------

@pytest.mark.django_db
class TestLibraryApi:
    def test_401_without_token(self, client, superadmin):
        assert client.get("/api/flows/library").status_code == 401

    def test_put_403_for_agent_role(self, client, agent_user):
        r = client.put("/api/flows/library",
                       data=json.dumps({"workflows": [], "agentSkills": []}),
                       content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_round_trip_and_prune(self, client, superadmin):
        """Workflows round-trip and prune. Agent skills do NOT: since Phase 8
        they belong to GET/PUT /api/aicore/skills, and this sync ignores them
        (a stale builder tab used to silently delete an operator's skills —
        see tests/test_knowledge.py TestSkillOwnership)."""
        AgentSkill.objects.create(skill_id="as-owned-elsewhere", name="Settings skill")
        body = {
            "workflows": [{"id": "wf-1", "name": "Pre-due", "hint": "h",
                           "nodes": [_trigger()], "edges": []}],
            "agentSkills": [{"id": "as-1", "name": "Skill",
                             "useWhen": "w", "instructions": "i"}],
        }
        r = client.put("/api/flows/library", data=json.dumps(body),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        saved = r.json()
        assert saved["workflows"][0]["id"] == "wf-1"
        assert saved["workflows"][0]["active"] is False  # default: not live
        # the payload's skill was ignored; the one owned by Settings survived
        assert [s["id"] for s in saved["agentSkills"]] == ["as-owned-elsewhere"]

        # second sync without wf-1 prunes it; active flag set explicitly
        body["workflows"] = [{"id": "wf-2", "name": "Other", "active": True,
                              "nodes": [], "edges": []}]
        r = client.put("/api/flows/library", data=json.dumps(body),
                       content_type="application/json", **SUPER)
        ids = [w["id"] for w in r.json()["workflows"]]
        assert ids == ["wf-2"]
        assert r.json()["workflows"][0]["active"] is True
        assert AgentSkill.objects.count() == 1

    def test_put_preserves_active_when_not_sent(self, client, superadmin):
        _workflow([_trigger()], [], active=True, flow_id="wf-live")
        body = {"workflows": [{"id": "wf-live", "name": "Live", "nodes": [],
                               "edges": []}], "agentSkills": []}
        r = client.put("/api/flows/library", data=json.dumps(body),
                       content_type="application/json", **SUPER)
        assert r.json()["workflows"][0]["active"] is True


# ---- engine: triggers + actions --------------------------------------------------

@pytest.mark.django_db
class TestTriggersAndActions:
    def test_tick_enrolls_and_sends_sandbox_whatsapp(self, contact, calm):
        _workflow([_trigger(days=1), _action(), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        result = engine.tick()
        assert result["started"] == 1
        run = FlowRun.objects.get()
        assert run.status == "done"
        assert run.contact == contact
        # sandbox whatsapp message with variables rendered
        msg = Message.objects.filter(thread__channel="whatsapp", direction="out").get()
        assert "Ahmad Faiz" in msg.text
        assert "RM 500.00" in msg.text
        assert "https://track.example/abc" in msg.text
        assert msg.status == "sandbox"

    def test_repeat_once_dedupes(self, contact, calm):
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 1
        assert engine.tick()["started"] == 0
        assert FlowRun.objects.count() == 1

    def test_inactive_workflow_never_fires(self, contact, calm):
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")], active=False)
        assert engine.tick()["started"] == 0

    def test_trigger_requires_enough_days_past_due(self, contact, calm):
        _workflow([_trigger(days=10), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 0

    def test_trigger_skips_contacts_without_a_record(self, calm):
        Contact.objects.create(name="No record", phone="+60123456222")
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 0

    def test_record_status_trigger(self, contact, calm):
        node = _trigger()
        node["data"]["config"].update({"when": "Record status is", "status": "open"})
        _workflow([node, _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 1

    def test_contact_tag_trigger(self, contact, calm):
        contact.tags = ["vip"]
        contact.save(update_fields=["tags"])
        Contact.objects.create(name="Untagged", phone="+60123456333")
        node = _trigger()
        node["data"]["config"].update({"when": "Contact has tag", "tag": "VIP"})
        _workflow([node, _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 1
        assert FlowRun.objects.get().contact == contact

    def _other(self, name="Siti", phone="+60123456111"):
        c = Contact.objects.create(name=name, phone=phone)
        Record.objects.create(contact=c, type="order", external_id=f"O{phone[-4:]}",
                              status="open", amount=10,
                              due_date=date.today() - timedelta(days=1))
        return c

    def test_contact_scope_absent_enrols_everyone(self, contact, calm):
        """Back-compat: every flow saved before the picker existed has no
        `contacts` key and must keep enrolling the whole book."""
        other = self._other()
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 2
        assert set(FlowRun.objects.values_list("contact_id", flat=True)) == {
            contact.pk, other.pk}

    def test_empty_contact_scope_still_means_all(self, contact, calm):
        self._other()
        _workflow([_trigger(days=1, contacts=[]), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 2

    def test_contact_scope_limits_enrolment(self, contact, calm):
        """Pilot a flow on named contacts before rolling it out."""
        other = self._other()
        _workflow([_trigger(days=1, contacts=[str(other.pk)]), _end()],
                  [_edge("t1", "e1")])
        assert engine.tick()["started"] == 1
        assert FlowRun.objects.get().contact_id == other.pk

    def test_resolved_contacts_are_skipped(self, contact, calm):
        contact.status = "resolved"
        contact.save()
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 0


# ---- engine: decisions + replies --------------------------------------------------

@pytest.mark.django_db
class TestDecisionsAndReplies:
    def _decision_flow(self):
        decision = {"id": "d1", "type": "decision", "position": {"x": 0, "y": 200},
                    "data": {"label": "will pay?",
                             "config": {"basedOn": "Contact's answer"}}}
        return _workflow(
            [_trigger(days=1), decision, _end("e-yes", "PTP-SET"), _end("e-no", "ESCALATE")],
            [_edge("t1", "d1"),
             _edge("d1", "e-yes", handle="yes"),
             _edge("d1", "e-no", handle="no")])

    def test_parks_then_yes_reply_takes_yes_edge(self, contact, calm):
        self._decision_flow()
        engine.tick()
        run = FlowRun.objects.get()
        assert (run.status, run.wait_for, run.current_node) == ("waiting", "reply", "d1")

        resumed = engine.on_inbound_message("whatsapp", "60123456789", "ok boleh, I can pay")
        assert resumed == 1
        run.refresh_from_db()
        assert run.status == "done"
        assert run.current_node == "e-yes"

    def test_no_reply_takes_no_edge(self, contact, calm):
        self._decision_flow()
        engine.tick()
        engine.on_inbound_message("whatsapp", "60123456789", "cannot la, no money now")
        run = FlowRun.objects.get()
        assert run.status == "done"
        assert run.current_node == "e-no"

    def test_unrelated_contact_message_resumes_nothing(self, contact, calm):
        self._decision_flow()
        engine.tick()
        assert engine.on_inbound_message("whatsapp", "60999999999", "yes") == 0

    def test_record_status_decision_answers_from_crm(self, contact, calm):
        decision = {"id": "d1", "type": "decision", "position": {"x": 0, "y": 200},
                    "data": {"label": "closed?", "config": {"basedOn": "Record status"}}}
        _workflow([_trigger(days=1), decision, _end("e-yes", "CLOSED"), _end("e-no", "OPEN")],
                  [_edge("t1", "d1"),
                   _edge("d1", "e-yes", handle="yes"),
                   _edge("d1", "e-no", handle="no")])
        engine.tick()
        run = FlowRun.objects.get()
        assert run.status == "done"
        assert run.current_node == "e-no"  # the order is still open

    def test_contact_tag_decision(self, contact, calm):
        contact.tags = ["vip"]
        contact.save(update_fields=["tags"])
        decision = {"id": "d1", "type": "decision", "position": {"x": 0, "y": 200},
                    "data": {"label": "vip?", "config": {"basedOn": "Contact tag",
                                                         "tag": "vip"}}}
        _workflow([_trigger(days=1), decision, _end("e-yes", "VIP"), _end("e-no", "STD")],
                  [_edge("t1", "d1"),
                   _edge("d1", "e-yes", handle="yes"),
                   _edge("d1", "e-no", handle="no")])
        engine.tick()
        assert FlowRun.objects.get().current_node == "e-yes"


# ---- engine: waits + handover ------------------------------------------------------

@pytest.mark.django_db
class TestWaitsAndHandover:
    def test_wait_edge_parks_then_ticker_resumes(self, contact, calm):
        _workflow([_trigger(days=1), _action(), _end()],
                  [_edge("t1", "a1", timing="Wait N days", wait_days=2),
                   _edge("a1", "e1")])
        engine.tick()
        run = FlowRun.objects.get()
        assert (run.status, run.current_node) == ("waiting", "a1")
        assert run.wait_until is not None

        run.wait_until = timezone.now() - timezone.timedelta(minutes=1)
        run.save()
        assert engine.tick()["resumed"] == 1
        run.refresh_from_db()
        assert run.status == "done"
        assert Message.objects.filter(direction="out").exists()

    def test_handover_notifies_roles(self, contact, calm, superadmin):
        handover = {"id": "h1", "type": "handover", "position": {"x": 0, "y": 200},
                    "data": {"label": "to human", "config": {
                        "team": "Compliance", "priority": "Urgent",
                        "reason": "Dispute from {{name}}"}}}
        _workflow([_trigger(days=1), handover, _end()],
                  [_edge("t1", "h1"), _edge("h1", "e1")])
        engine.tick()
        note = Notification.objects.get(user=superadmin)
        assert "Compliance" in note.title
        assert "Ahmad Faiz" in note.title
        assert "Dispute from Ahmad Faiz" in note.body
        assert FlowRun.objects.get().status == "done"


# ---- runs API + classifier ---------------------------------------------------------

@pytest.mark.django_db
class TestRunsApi:
    def test_runs_listing(self, client, superadmin, contact, calm):
        _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        client.post("/api/flows/tick", **SUPER)
        r = client.get("/api/flows/runs", **SUPER)
        runs = r.json()["runs"]
        assert len(runs) == 1
        assert runs[0]["workflow"]["id"] == "wf-test"
        assert runs[0]["contact"]["name"] == "Ahmad Faiz"
        assert runs[0]["status"] == "done"

    def test_tick_endpoint_needs_admin(self, client, agent_user):
        assert client.post("/api/flows/tick", **AGENT).status_code == 403


def test_classify_reply():
    assert engine.classify_reply("Yes I will pay tomorrow") == "yes"
    assert engine.classify_reply("ok boleh") == "yes"
    assert engine.classify_reply("cannot, no money") == "no"
    assert engine.classify_reply("tak boleh bayar lagi") == "no"  # "tak" before "boleh"/"bayar"
    assert engine.classify_reply("") == "no"
    assert engine.classify_reply("???") == "no"


# ---- engine: exact-day trigger, telegram channel, whatsapp call transport ------------

@pytest.mark.django_db
class TestEngineExtensions:
    def test_exact_days_after_due_trigger_hits_one_day_only(self, contact, calm):
        contact.records.update(due_date=date.today() - timedelta(days=7))
        # a contact further past due must NOT match (">= days" would catch them)
        deep = Contact.objects.create(name="Deep Overdue", phone="+60129999999")
        Record.objects.create(contact=deep, type="order", external_id="ORD-DEEP",
                              status="open",
                              due_date=date.today() - timedelta(days=30))
        node = _trigger(days=7)
        node["data"]["config"]["when"] = "Days after due date (exactly)"
        _workflow([node, _end()], [_edge("t1", "e1")])
        assert engine.tick()["started"] == 1
        assert FlowRun.objects.get().contact == contact

    def test_telegram_action_sends_to_linked_chat(self, contact, calm):
        Thread.objects.create(channel="telegram", key="777", contact=contact)
        _workflow([_trigger(days=1), _action(channels=("Telegram",)), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        msg = Message.objects.filter(thread__channel="telegram", direction="out").get()
        assert "Ahmad Faiz" in msg.text and "RM 500.00" in msg.text
        assert msg.status == "sandbox"  # no bot configured in tests

    def test_telegram_skipped_without_linked_chat(self, contact, calm):
        _workflow([_trigger(days=1), _action(channels=("Telegram",)), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        run = FlowRun.objects.get()
        assert run.status == "done"  # a missing chat id never fails the run
        assert not Message.objects.filter(thread__channel="telegram").exists()

    def test_call_via_whatsapp_uses_baileys_transport(self, contact, calm, monkeypatch):
        from crm.models import ChannelAccount
        acct = ChannelAccount.objects.create(channel="whatsapp", provider="baileys",
                                             name="QR Number")
        calls = []
        from whatsapp import baileys_calls
        monkeypatch.setattr(
            baileys_calls, "start_call",
            lambda wa_id, thread, account, voice_provider="", voice_id="", script="":
                calls.append((wa_id, str(account["id"])))
            or {"call_id": "c1", "status": "connecting", "room": "wa-call-x"})
        node = _action(channels=("AI voice call",))
        node["data"]["config"]["callVia"] = "WhatsApp (Baileys)"
        _workflow([_trigger(days=1), node, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        assert calls == [("60123456789", str(acct.pk))]
        run = FlowRun.objects.get()
        assert run.context.get("call_room") == "wa-call-x"
        assert run.status == "done"

    def test_call_via_whatsapp_without_baileys_account_retries(
            self, contact, calm):
        node = _action(channels=("AI voice call",))
        node["data"]["config"]["callVia"] = "WhatsApp (Baileys)"
        _workflow([_trigger(days=1), node, _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        run = FlowRun.objects.get()
        assert run.status == "done"  # the flow walks on — the call is queued
        # Having no number to dial from is TRANSIENT (a down account can come
        # back healthy), so the queued call waits instead of failing the step.
        job = OutboundJob.objects.get(kind="call")
        assert (job.status, job.attempts) == ("queued", 1)
        assert "no healthy Baileys account" in job.last_error


@pytest.mark.django_db
class TestStalledRunRecovery:
    def test_stale_running_run_fails_loudly(self, contact, calm, superadmin):
        from datetime import timedelta as td
        wf = _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        run = FlowRun.objects.create(workflow=wf, contact=contact,
                                     trigger_key="t1", current_node="a1",
                                     status="running")
        FlowRun.objects.filter(pk=run.pk).update(
            updated_at=timezone.now() - td(minutes=30))
        engine.resume_due_runs()
        run.refresh_from_db()
        assert run.status == "failed"
        assert any("stalled" in e["event"] for e in run.log)
        assert Notification.objects.filter(title__contains="stalled").exists()

    def test_fresh_running_run_untouched(self, contact, calm):
        wf = _workflow([_trigger(days=1), _end()], [_edge("t1", "e1")])
        run = FlowRun.objects.create(workflow=wf, contact=contact,
                                     trigger_key="t1", current_node="a1",
                                     status="running")
        engine.resume_due_runs()
        run.refresh_from_db()
        assert run.status == "running"  # transient state, not stale yet


@pytest.mark.django_db
class TestFlowCallVoice:
    @pytest.fixture(autouse=True)
    def _no_real_memory_file(self, monkeypatch):
        # _flow_call_voice now falls back to the contact's remembered voice
        # (calls/memory.py, backend/data/memory/{id}.md) — that reads REAL
        # disk state keyed by contact pk, which the `contact` fixture reuses
        # across tests (commonly pk=1). Isolate these tests from whatever a
        # real deployment happens to have saved there.
        import calls.memory as memory_mod
        monkeypatch.setattr(memory_mod, "load_memory", lambda contact_id: memory_mod._blank())

    def _call_node(self, voice=None):
        node = _action(channels=("AI voice call",))
        node["data"]["config"]["callVia"] = "WhatsApp (Baileys)"
        if voice is not None:
            node["data"]["config"]["voice"] = voice
        return node

    def _capture_start_call(self, monkeypatch):
        from crm.models import ChannelAccount
        ChannelAccount.objects.create(channel="whatsapp", provider="baileys", name="QR")
        captured = {}
        from whatsapp import baileys_calls
        monkeypatch.setattr(
            baileys_calls, "start_call",
            lambda wa_id, thread, account, voice_provider="", voice_id="", script="":
                captured.update(voice_provider=voice_provider, voice_id=voice_id,
                                script=script)
                or {"call_id": "c1", "status": "connecting", "room": "wa-x"})
        return captured

    def test_node_voice_sends_elevenlabs(self, contact, calm, monkeypatch):
        captured = self._capture_start_call(monkeypatch)
        _workflow([_trigger(days=1), self._call_node(voice="vx_abc"), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        assert captured == {"voice_provider": "elevenlabs", "voice_id": "vx_abc",
                            "script": ""}  # two-way call — never a script

    def test_blank_voice_uses_native(self, contact, calm, monkeypatch):
        captured = self._capture_start_call(monkeypatch)
        _workflow([_trigger(days=1), self._call_node(voice=""), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        assert captured == {"voice_provider": "", "voice_id": "", "script": ""}

    def test_blank_voice_falls_back_to_global_default(self, contact, calm, monkeypatch):
        from crm.models import Setting
        Setting.objects.create(key="flow_call_voice", value="vx_global")
        captured = self._capture_start_call(monkeypatch)
        _workflow([_trigger(days=1), self._call_node(voice=""), _end()],
                  [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()
        assert captured == {"voice_provider": "elevenlabs", "voice_id": "vx_global",
                            "script": ""}


@pytest.mark.django_db
class TestScriptCallTransport:
    """The Script call channel honours the node's `callVia` exactly like the
    AI-voice-call channel, so a one-way "read it, then hang up" call can ride a
    linked WhatsApp number instead of the SIP trunk."""

    @pytest.fixture(autouse=True)
    def _isolate(self, monkeypatch):
        # Same real-disk hazard as TestFlowCallVoice: the remembered-voice
        # fallback reads backend/data/memory/{id}.md keyed by contact pk.
        import calls.memory as memory_mod
        monkeypatch.setattr(memory_mod, "load_memory",
                            lambda contact_id: memory_mod._blank())
        # Script calls auto-pick a ready ElevenLabs clone when nothing else
        # chose one — default to "none on disk" so each test opts in explicitly.
        from calls import voices as voices_mod
        monkeypatch.setattr(voices_mod, "default_elevenlabs_voice", lambda: None)

    def _template(self):
        from crm.models import ScriptTemplate
        return ScriptTemplate.objects.create(
            name="Order notice", content="Hello {{name}}, {{amount}} is due.")

    def _script_node(self, template_id, call_via="Phone (SIP)", voice=""):
        node = _action(channels=("Script call",))
        node["data"]["config"].update(callVia=call_via, scriptTemplateId=template_id,
                                      voice=voice)
        return node

    def _run(self, node):
        _workflow([_trigger(days=1), node, _end()], [_edge("t1", "a1"), _edge("a1", "e1")])
        engine.tick()

    def _wa_spy(self, monkeypatch, with_account=True):
        from crm.models import ChannelAccount
        if with_account:
            ChannelAccount.objects.create(channel="whatsapp", provider="baileys", name="QR")
        captured = {}
        from whatsapp import baileys_calls
        monkeypatch.setattr(
            baileys_calls, "start_call",
            lambda wa_id, thread, account, voice_provider="", voice_id="", script="":
                captured.update(wa_id=wa_id, voice_provider=voice_provider,
                                voice_id=voice_id, script=script)
                or {"call_id": "c1", "status": "connecting", "room": "wa-x"})
        return captured

    def _sip_spy(self, monkeypatch):
        import types

        import calls as calls_pkg
        captured = {}
        stub = types.ModuleType("calls.lk")
        stub.dial = lambda phone, name, language, extra: (
            captured.update(phone=phone, extra=extra) or "sip-room")
        # `from calls import lk` resolves the package attribute first, so this
        # keeps the real (LiveKit-SDK-importing) module out of the test run.
        monkeypatch.setattr(calls_pkg, "lk", stub, raising=False)
        return captured

    def test_whatsapp_script_call_sends_rendered_script(self, contact, calm, monkeypatch):
        wa = self._wa_spy(monkeypatch)
        self._run(self._script_node(self._template().pk, "WhatsApp (Baileys)"))
        assert wa["wa_id"] == "60123456789"
        # Amount spelled out for TTS — a literal-read engine would otherwise
        # sound out "RM 500.00" digit by digit.
        assert wa["script"] == "Hello Ahmad Faiz, five hundred ringgit is due."

    def test_sip_stays_the_default_transport(self, contact, calm, monkeypatch):
        wa = self._wa_spy(monkeypatch)
        sip = self._sip_spy(monkeypatch)
        self._run(self._script_node(self._template().pk))  # callVia = Phone (SIP)
        assert sip["phone"] == "+60123456789"
        assert sip["extra"]["script"] == "Hello Ahmad Faiz, five hundred ringgit is due."
        assert wa == {}  # the WhatsApp transport was never touched

    def test_whatsapp_script_call_auto_picks_literal_tts_voice(self, contact, calm, monkeypatch):
        from calls import voices as voices_mod
        monkeypatch.setattr(voices_mod, "default_elevenlabs_voice", lambda: "vx_clone")
        wa = self._wa_spy(monkeypatch)
        self._run(self._script_node(self._template().pk, "WhatsApp (Baileys)"))
        assert (wa["voice_provider"], wa["voice_id"]) == ("elevenlabs", "vx_clone")

    def test_node_voice_beats_the_auto_pick(self, contact, calm, monkeypatch):
        from calls import voices as voices_mod
        monkeypatch.setattr(voices_mod, "default_elevenlabs_voice", lambda: "vx_clone")
        wa = self._wa_spy(monkeypatch)
        self._run(self._script_node(self._template().pk, "WhatsApp (Baileys)",
                                    voice="vx_chosen"))
        assert (wa["voice_provider"], wa["voice_id"]) == ("elevenlabs", "vx_chosen")

    def test_no_baileys_account_retries_instead_of_failing(self, contact, calm, monkeypatch):
        self._wa_spy(monkeypatch, with_account=False)
        self._run(self._script_node(self._template().pk, "WhatsApp (Baileys)"))
        job = OutboundJob.objects.get(kind="call")
        assert (job.status, job.attempts) == ("queued", 1)
        assert "no healthy Baileys account" in job.last_error
        assert any("call retry" in e["event"] for e in FlowRun.objects.get().log)

    def test_missing_template_skips_before_dialling(self, contact, calm, monkeypatch):
        wa = self._wa_spy(monkeypatch)
        self._run(self._script_node("", "WhatsApp (Baileys)"))
        assert wa == {}
        run = FlowRun.objects.get()
        assert any("no script template selected" in e["event"] for e in run.log)
