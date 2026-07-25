"""AI tool-calling — the chat brain can check the calendar, offer real open
slots and book an appointment mid-conversation (docs/AI-TOOLCALLING-PLAN.md).

Fully offline. Every Google HTTP call is mocked (`actions._request`); no live
provider is ever contacted. Covers:

* the additive tool loop in aicore.assistant.generate_reply (tool_call → prose),
  its bound, the provider-without-tools fallback, and the ai_tool_use switch;
* the SANDBOX contract: no calendar connected → tool_specs empty → generate_reply
  is byte-for-byte the no-tools completion and makes NO network call;
* integrations.actions.find_free_slots (multi-slot, busy blocks, business hours,
  weekend skip, deterministic);
* integrations.actions.book_appointment (calendar event + crm.Record + RecordEvent)
  and the ai.booking AuditLog + Notification the tool dispatch fans out.
"""
import types
from datetime import date, timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone
from django.utils.dateparse import parse_datetime

from aicore import assistant
from aicore import tools as tools_mod
from crm.models import AuditLog, Contact, Notification, Profile, Record, RecordEvent, Setting
from integrations import actions
from integrations.models import Integration

MON_9 = "2026-07-27T09:00:00+00:00"   # 2026-07-27 is a Monday (2026-07-24 = Fri)


# ---- fixtures ---------------------------------------------------------------

@pytest.fixture
def calendar(db):
    """A connected Google Calendar integration (valid, far-future token)."""
    return Integration.objects.create(
        provider="google_calendar", name="Ops Calendar", status="healthy",
        credentials={"access_token": "tok-1",
                     "expires_at": (timezone.now() + timedelta(hours=2)).isoformat()},
        config={"calendar_id": "primary"})


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="tool_admin", password="x")
    Profile.objects.create(user=u, role="admin", token="tool-admin-token")
    return u


@pytest.fixture
def contact(db):
    return Contact.objects.create(name="Aminah", phone="+60123456789",
                                  email="aminah@example.com", status="active")


class _RequestSpy:
    """Stands in for actions._request — records calls, returns canned calendar
    responses, never touches the network."""

    def __init__(self, busy=None):
        self.calls = []
        self.busy = busy or []

    def __call__(self, method, url, **kw):
        self.calls.append((method, url))
        if "freeBusy" in url:
            return True, {"calendars": {"primary": {"busy": self.busy}}}
        if "/events" in url:
            return True, {"id": "evt-1", "htmlLink": "https://calendar.example/evt-1"}
        raise AssertionError(f"unexpected calendar URL {url}")


# ---- scripted OpenAI client -------------------------------------------------

def _prose(content):
    return types.SimpleNamespace(content=content, tool_calls=None)


def _tool_call(call_id, name, arguments="{}"):
    return types.SimpleNamespace(
        id=call_id, function=types.SimpleNamespace(name=name, arguments=arguments))


def _with_tools(*tool_calls):
    return types.SimpleNamespace(content=None, tool_calls=list(tool_calls))


class ScriptedOpenAI:
    """Returns a programmed sequence of chat.completions responses and captures
    every create() kwargs. Set `raise_on_tools` to simulate a provider that
    400s on `tools=`; set `always_tool_calls` to simulate a runaway model."""

    scripts: list = []
    calls: list = []
    raise_on_tools = False
    always_tool_calls = False

    def __init__(self, base_url=None, api_key=None):
        class _Completions:
            def create(self, **kwargs):
                ScriptedOpenAI.calls.append(kwargs)
                if ScriptedOpenAI.raise_on_tools and "tools" in kwargs:
                    raise RuntimeError("this provider does not support tools")
                if ScriptedOpenAI.always_tool_calls:
                    msg = _with_tools(_tool_call("c", "check_calendar_availability", "{}"))
                elif ScriptedOpenAI.scripts:
                    msg = ScriptedOpenAI.scripts.pop(0)
                else:
                    msg = _prose("ok")
                return types.SimpleNamespace(choices=[types.SimpleNamespace(message=msg)])

        self.chat = types.SimpleNamespace(completions=_Completions())


@pytest.fixture
def fake_openai(monkeypatch):
    import openai

    ScriptedOpenAI.scripts = []
    ScriptedOpenAI.calls = []
    ScriptedOpenAI.raise_on_tools = False
    ScriptedOpenAI.always_tool_calls = False
    monkeypatch.setattr(openai, "OpenAI", ScriptedOpenAI)
    monkeypatch.setattr(assistant, "_provider",
                        lambda: ("http://llm", "key-1", "model-x"))
    return ScriptedOpenAI


def _reply(contact=None):
    return assistant.generate_reply(
        [{"role": "user", "content": "can I book an appointment?"}],
        contact_name="Aminah",
        phone="+60123456789", channel="whatsapp",
        contact_id=contact.pk if contact is not None else None)


# ---- find_free_slots --------------------------------------------------------

@pytest.mark.django_db
class TestFindFreeSlots:
    def _find(self, monkeypatch, busy=None, **cfg):
        spy = _RequestSpy(busy=busy)
        monkeypatch.setattr(actions, "_request", spy)
        base = {"eventStart": MON_9, "eventMinutes": 30}
        base.update(cfg)
        return actions.find_free_slots(config=base), spy

    def test_skips_when_not_connected(self, db, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no network in sandbox"))
        res = actions.find_free_slots(config={"eventStart": MON_9})
        assert res["ok"] is False and res["skipped"] is True

    def test_returns_multiple_in_hours_slots(self, calendar, monkeypatch):
        res, _ = self._find(monkeypatch)
        slots = res["data"]["slots"]
        assert res["ok"] is True and len(slots) == 3
        assert [s["start"] for s in slots] == [
            "2026-07-27T09:00:00+00:00",
            "2026-07-27T09:30:00+00:00",
            "2026-07-27T10:00:00+00:00",
        ]
        assert slots[0]["human"] and slots[0]["duration_minutes"] == 30

    def test_respects_busy_blocks(self, calendar, monkeypatch):
        busy = [{"start": "2026-07-27T09:00:00+00:00", "end": "2026-07-27T10:00:00+00:00"}]
        res, _ = self._find(monkeypatch, busy=busy)
        # 09:00 and 09:30 overlap the busy hour — first free slot is 10:00.
        assert res["data"]["slots"][0]["start"] == "2026-07-27T10:00:00+00:00"

    def test_deterministic(self, calendar, monkeypatch):
        a, _ = self._find(monkeypatch)
        b, _ = self._find(monkeypatch)
        assert a["data"]["slots"] == b["data"]["slots"]

    def test_stays_within_business_hours(self, calendar, monkeypatch):
        # 60-minute slots: none may start at 17:30 (would end 18:30 > 18:00).
        res, _ = self._find(monkeypatch, eventMinutes=60, searchDays=1, slotCount=10)
        for s in res["data"]["slots"]:
            start = parse_datetime(s["start"])
            assert 9 <= start.hour < 18
            assert (start + timedelta(minutes=60)).hour <= 18

    def test_skips_weekends(self, calendar, monkeypatch):
        # Start late Friday: after one Friday slot the search must jump to Monday,
        # never Saturday/Sunday.
        res, _ = self._find(monkeypatch, eventStart="2026-07-24T17:30:00+00:00",
                            slotCount=3)
        days = [parse_datetime(s["start"]).weekday() for s in res["data"]["slots"]]
        assert all(d < 5 for d in days)           # no Sat(5)/Sun(6)
        assert parse_datetime(res["data"]["slots"][0]["start"]).date() == date(2026, 7, 24)
        assert parse_datetime(res["data"]["slots"][1]["start"]).date() == date(2026, 7, 27)

    def test_no_slot_when_fully_busy(self, calendar, monkeypatch):
        busy = [{"start": "2026-07-27T00:00:00+00:00", "end": "2026-07-28T00:00:00+00:00"}]
        res, _ = self._find(monkeypatch, busy=busy, searchDays=1)
        assert res["ok"] is True and res["data"]["slots"] == []

    def test_single_slot_finder_left_intact(self, calendar, monkeypatch):
        # find_free_slot (singular, used by the flow builder) still returns a
        # single slot dict — unchanged behaviour.
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        res = actions.find_free_slot(config={"eventStart": MON_9})
        assert res["ok"] is True and "slot" in res["data"]


# ---- book_appointment -------------------------------------------------------

@pytest.mark.django_db
class TestBookAppointment:
    def test_creates_event_record_and_event(self, calendar, contact, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        res = actions.book_appointment(
            contact=contact,
            config={"eventStart": "2026-07-27T14:00:00+00:00", "eventTitle": "Consult"},
            text="Consult")
        assert res["ok"] is True
        assert res["data"]["event_id"] == "evt-1"
        assert res["data"]["html_link"] == "https://calendar.example/evt-1"

        rec = Record.objects.get(type="booking")
        assert rec.contact_id == contact.pk
        assert rec.status == "scheduled"
        assert rec.title == "Consult"
        assert rec.due_date == date(2026, 7, 27)
        assert rec.data["event_id"] == "evt-1"

        ev = RecordEvent.objects.get(record=rec)
        assert ev.kind == "note"
        assert ev.data["kind"] == "booking"

    def test_book_without_contact_still_makes_the_event(self, calendar, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        res = actions.book_appointment(config={"eventStart": "2026-07-27T14:00:00+00:00"})
        assert res["ok"] is True
        assert Record.objects.filter(type="booking").count() == 0

    def test_book_skips_when_not_connected(self, db, contact, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no network in sandbox"))
        res = actions.book_appointment(contact=contact,
                                       config={"eventStart": MON_9})
        assert res["ok"] is False and res["skipped"] is True
        assert Record.objects.filter(type="booking").count() == 0

    def test_dispatch_writes_audit_and_notification(self, calendar, contact, admin, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        result = tools_mod.dispatch(
            "book_appointment",
            {"start_iso": "2026-07-27T14:00:00+00:00", "title": "Consult"},
            contact=contact, channel="whatsapp")
        assert result["ok"] is True and result["confirmed"] is True
        assert result["calendar_link"] == "https://calendar.example/evt-1"
        assert AuditLog.objects.filter(action="ai.booking").count() == 1
        assert Notification.objects.filter(user=admin,
                                           title="AI booked an appointment").count() == 1


# ---- tool_specs (registry + sandbox gate) -----------------------------------

@pytest.mark.django_db
class TestToolSpecs:
    def test_empty_without_calendar(self, db):
        assert tools_mod.tool_specs() == []

    def test_calendar_connected_offers_two_tools(self, calendar):
        names = {t["function"]["name"] for t in tools_mod.tool_specs()}
        assert names == {"check_calendar_availability", "book_appointment"}

    def test_kill_switch_off_returns_empty(self, calendar):
        Setting.objects.create(key="ai_tool_use", value="off")
        assert tools_mod.tool_specs() == []

    def test_unconnected_integration_row_is_not_offered(self, db):
        # A row with no access token is not "connected" — must not be offered.
        Integration.objects.create(provider="google_calendar", name="half", credentials={})
        assert tools_mod.tool_specs() == []

    def test_dispatch_unknown_tool_is_a_result_not_a_crash(self, db):
        out = tools_mod.dispatch("teleport", {})
        assert out["ok"] is False and "unknown tool" in out["error"]


# ---- the loop in generate_reply ---------------------------------------------

@pytest.mark.django_db
class TestToolLoop:
    def test_tool_call_then_prose(self, calendar, contact, fake_openai, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        fake_openai.scripts = [
            _with_tools(_tool_call("c1", "check_calendar_availability",
                                   '{"duration_minutes": 30}')),
            _prose("I have Monday 9am or 9:30am — which suits you?"),
        ]
        out = _reply(contact)
        assert out == "I have Monday 9am or 9:30am — which suits you?"
        # the tool actually ran (freeBusy was queried) and we made two LLM calls
        assert any("freeBusy" in url for _m, url in spy.calls)
        assert len(fake_openai.calls) == 2
        assert "tools" in fake_openai.calls[0]

    def test_booking_through_the_loop(self, calendar, contact, admin, fake_openai, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        fake_openai.scripts = [
            _with_tools(_tool_call("c1", "book_appointment",
                                   '{"start_iso": "2026-07-27T14:00:00+00:00", "title": "Consult"}')),
            _prose("Booked! See you Monday at 2pm."),
        ]
        out = _reply(contact)
        assert out == "Booked! See you Monday at 2pm."
        assert Record.objects.filter(type="booking").count() == 1
        assert AuditLog.objects.filter(action="ai.booking").count() == 1

    def test_loop_is_bounded(self, calendar, contact, fake_openai, monkeypatch):
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        fake_openai.always_tool_calls = True   # model never stops asking for tools
        out = _reply(contact)                  # must not hang or raise
        assert isinstance(out, str)
        # initial + MAX_TOOL_ROUNDS re-calls + one final tool-free completion.
        assert len(fake_openai.calls) == assistant.MAX_TOOL_ROUNDS + 2

    def test_provider_without_tools_falls_back(self, calendar, contact, fake_openai, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("tool must not run on fallback"))
        fake_openai.raise_on_tools = True
        fake_openai.scripts = [_prose("Sure, the team will confirm a time with you.")]
        out = _reply(contact)
        assert out == "Sure, the team will confirm a time with you."
        # first call offered tools (raised), the retry dropped them.
        assert "tools" in fake_openai.calls[0]
        assert "tools" not in fake_openai.calls[1]

    def test_ai_tool_use_off_offers_no_tools(self, calendar, contact, fake_openai, monkeypatch):
        Setting.objects.create(key="ai_tool_use", value="false")
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no tool call when disabled"))
        fake_openai.scripts = [_prose("Happy to help.")]
        out = _reply(contact)
        assert out == "Happy to help."
        assert len(fake_openai.calls) == 1
        assert "tools" not in fake_openai.calls[0]


# ---- sandbox: no calendar → byte-for-byte the no-tools completion -----------

@pytest.mark.django_db
class TestSandboxBaseline:
    def test_no_calendar_is_the_plain_completion_no_network(self, contact, fake_openai, monkeypatch):
        # No Integration rows. A tripwire on _request proves NO calendar call.
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no network without a connected calendar"))
        fake_openai.scripts = [_prose("I'll have the team confirm a time.")]
        out = _reply(contact)
        assert out == "I'll have the team confirm a time."
        assert len(fake_openai.calls) == 1
        kwargs = fake_openai.calls[0]
        # Additive: the sandbox call carries NO tools/tool_choice and the exact
        # same kwargs the reply path has always sent.
        assert "tools" not in kwargs and "tool_choice" not in kwargs
        assert set(kwargs) == {"model", "messages", "max_tokens", "temperature"}

    def test_ilmu_provider_is_not_offered_tools(self, calendar, contact, fake_openai, monkeypatch):
        # ilmu takes the reasoning_effort path and is not offered tools.
        monkeypatch.setattr(assistant, "_provider",
                            lambda: ("https://api.ilmu.example/v1", "k", "ilmu-v3.1"))
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no tool call for ilmu"))
        fake_openai.scripts = [_prose("Baik, saya bantu.")]
        out = _reply(contact)
        assert out == "Baik, saya bantu."
        kwargs = fake_openai.calls[0]
        assert "tools" not in kwargs
        assert kwargs["reasoning_effort"] == "low"
