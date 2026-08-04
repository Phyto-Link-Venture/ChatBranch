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
from crm.models import (AuditLog, Contact, Notification, PendingToolAction,
                        Profile, Record, RecordEvent, Setting)
from integrations import actions
from integrations.models import Integration
from whatsapp import wa_config

MON_9 = "2026-07-27T09:00:00+08:00"   # 2026-07-27 is a Monday (2026-07-24 = Fri)
# +08:00 = Asia/Kuala_Lumpur, this app's real TIME_ZONE (backend/settings.py) —
# a bare +00:00/UTC timestamp here is 8 hours off from local business hours
# and was the exact bug behind an AI booking silently failing (see
# aicore/assistant.py _temporal_context and the aicore/tools.py start_iso
# schema fix).


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

class _FakeBadRequest(Exception):
    """Stands in for openai.BadRequestError's shape (a `status_code` attribute)
    without needing the real SDK exception class — a provider genuinely
    rejecting the `tools=` param is a 400, distinct from a quota/5xx/timeout
    failure that should propagate to the per-candidate failover instead."""

    def __init__(self, msg):
        super().__init__(msg)
        self.status_code = 400


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
    fail_for_models: set = set()

    def __init__(self, base_url=None, api_key=None):
        class _Completions:
            def create(self, **kwargs):
                ScriptedOpenAI.calls.append(kwargs)
                if kwargs.get("model") in ScriptedOpenAI.fail_for_models:
                    # A genuine outage (quota/5xx/timeout) — no status_code,
                    # same shape as openai's connection/rate-limit errors —
                    # must NOT be treated as "retry without tools" and must
                    # propagate so the per-candidate failover moves on.
                    raise RuntimeError("simulated provider failure")
                if ScriptedOpenAI.raise_on_tools and "tools" in kwargs:
                    raise _FakeBadRequest("this provider does not support tools")
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
    ScriptedOpenAI.fail_for_models = set()
    monkeypatch.setattr(openai, "OpenAI", ScriptedOpenAI)
    monkeypatch.setattr(assistant, "_provider",
                        lambda: ("http://llm", "key-1", "model-x"))
    return ScriptedOpenAI


def _reply(contact=None, text="can I book an appointment?"):
    return assistant.generate_reply(
        [{"role": "user", "content": text}],
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
            "2026-07-27T09:00:00+08:00",
            "2026-07-27T09:30:00+08:00",
            "2026-07-27T10:00:00+08:00",
        ]
        assert slots[0]["human"] and slots[0]["duration_minutes"] == 30

    def test_respects_busy_blocks(self, calendar, monkeypatch):
        busy = [{"start": "2026-07-27T09:00:00+08:00", "end": "2026-07-27T10:00:00+08:00"}]
        res, _ = self._find(monkeypatch, busy=busy)
        # 09:00 and 09:30 overlap the busy hour — first free slot is 10:00.
        assert res["data"]["slots"][0]["start"] == "2026-07-27T10:00:00+08:00"

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
        res, _ = self._find(monkeypatch, eventStart="2026-07-24T17:30:00+08:00",
                            slotCount=3)
        days = [parse_datetime(s["start"]).weekday() for s in res["data"]["slots"]]
        assert all(d < 5 for d in days)           # no Sat(5)/Sun(6)
        assert parse_datetime(res["data"]["slots"][0]["start"]).date() == date(2026, 7, 24)
        assert parse_datetime(res["data"]["slots"][1]["start"]).date() == date(2026, 7, 27)

    def test_no_slot_when_fully_busy(self, calendar, monkeypatch):
        busy = [{"start": "2026-07-27T00:00:00+08:00", "end": "2026-07-28T00:00:00+08:00"}]
        res, _ = self._find(monkeypatch, busy=busy, searchDays=1)
        assert res["ok"] is True and res["data"]["slots"] == []

    def test_requested_time_available_when_free(self, calendar, monkeypatch):
        # The exact live bug: a customer asking for an afternoon time got told
        # "not available" just because it wasn't among the 3 earliest morning
        # slots — the calendar was actually completely free. requestedStart
        # must give a definitive per-slot answer, not force an inference from
        # the general slots list.
        res, _ = self._find(monkeypatch, requestedStart="2026-07-27T13:00:00+08:00")
        assert res["data"]["requested_available"] is True
        assert res["data"]["requested_reason"] is None
        assert res["data"]["requested_start"] == "2026-07-27T13:00:00+08:00"
        # the general slots list is still returned too, as alternatives
        assert len(res["data"]["slots"]) == 3

    def test_requested_time_busy(self, calendar, monkeypatch):
        busy = [{"start": "2026-07-27T13:00:00+08:00", "end": "2026-07-27T13:30:00+08:00"}]
        res, _ = self._find(monkeypatch, busy=busy,
                            requestedStart="2026-07-27T13:00:00+08:00")
        assert res["data"]["requested_available"] is False
        assert res["data"]["requested_reason"] == "busy"

    def test_requested_time_outside_hours(self, calendar, monkeypatch):
        res, _ = self._find(monkeypatch, requestedStart="2026-07-27T22:00:00+08:00")
        assert res["data"]["requested_available"] is False
        assert res["data"]["requested_reason"] == "outside_hours"

    def test_alternatives_anchor_to_the_requested_day_not_now(self, calendar, monkeypatch):
        # A customer asking about a future day must get THAT day's
        # alternatives — with no eventStart pinning the search, it used to
        # default to "right now," which can be a completely different,
        # unrelated day (observed live: a customer asked about tomorrow,
        # after midnight, and was offered slots that were actually for a
        # different day entirely).
        from django.utils import timezone as dj_tz

        requested = (dj_tz.localtime() + timedelta(days=10)).replace(
            hour=13, minute=0, second=0, microsecond=0)
        while requested.weekday() >= 5:  # keep it a weekday regardless of run date
            requested += timedelta(days=1)
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        res = actions.find_free_slots(config={
            "eventMinutes": 30, "requestedStart": requested.isoformat()})
        slots = res["data"]["slots"]
        assert slots
        for s in slots:
            assert parse_datetime(s["start"]).date() == requested.date()

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
            config={"eventStart": "2026-07-27T14:00:00+08:00", "eventTitle": "Consult"},
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
        res = actions.book_appointment(config={"eventStart": "2026-07-27T14:00:00+08:00"})
        assert res["ok"] is True
        assert Record.objects.filter(type="booking").count() == 0

    def test_refuses_a_real_conflict(self, calendar, contact, monkeypatch):
        # book_appointment used to only validate business HOURS, never
        # checking whether the calendar was actually busy — meaning it could
        # silently double-book over a real event. A pre-flight freeBusy check
        # must catch that before create_calendar_event is ever called.
        busy = [{"start": "2026-07-27T14:00:00+08:00", "end": "2026-07-27T14:30:00+08:00"}]
        spy = _RequestSpy(busy=busy)
        monkeypatch.setattr(actions, "_request", spy)
        res = actions.book_appointment(
            contact=contact, config={"eventStart": "2026-07-27T14:00:00+08:00"})
        assert res["ok"] is False and res["conflict"] is True
        assert Record.objects.filter(type="booking").count() == 0
        assert not any("/events" in url for _, url in spy.calls)  # never even tried to create it

    def test_conflict_check_failure_does_not_block_booking(self, calendar, contact, monkeypatch):
        # The conflict check is a SAFETY NET, not a new way to fail a booking
        # that would otherwise succeed — a freeBusy lookup error must not
        # block the booking itself.
        calls = {"n": 0}

        def flaky(method, url, **kw):
            if "freeBusy" in url:
                calls["n"] += 1
                return False, "network error"
            if "/events" in url:
                return True, {"id": "evt-1", "htmlLink": "https://calendar.example/evt-1"}
            raise AssertionError(f"unexpected calendar URL {url}")

        monkeypatch.setattr(actions, "_request", flaky)
        res = actions.book_appointment(
            contact=contact, config={"eventStart": "2026-07-27T14:00:00+08:00"})
        assert res["ok"] is True
        assert calls["n"] == 1

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
        # First call: the double-confirm gate refuses and asks — nothing booked.
        first = tools_mod.dispatch(
            "book_appointment",
            {"start_iso": "2026-07-27T14:00:00+08:00", "title": "Consult"},
            contact=contact, channel="whatsapp")
        assert first["ok"] is False and first["needs_confirmation"] is True
        assert AuditLog.objects.filter(action="ai.booking").count() == 0
        # Confirmed re-call with the same arguments: books + audits + notifies.
        result = tools_mod.dispatch(
            "book_appointment",
            {"start_iso": "2026-07-27T14:00:00+08:00", "title": "Consult",
             "confirmed": True},
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

    def test_calendar_connected_offers_all_appointment_tools(self, calendar):
        names = {t["function"]["name"] for t in tools_mod.tool_specs()}
        assert names == {"check_calendar_availability", "book_appointment",
                         "list_appointments", "reschedule_appointment",
                         "cancel_appointment"}

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


# ---- the double-confirmation gate (state-changing tools) --------------------

@pytest.mark.django_db
class TestConfirmationGate:
    ARGS = {"start_iso": "2026-07-27T14:00:00+08:00", "title": "Consult"}

    def _book(self, contact, args=None, session=None):
        return tools_mod.dispatch("book_appointment", dict(args or self.ARGS),
                                  contact=contact, channel="whatsapp",
                                  session=session)

    def test_first_call_asks_and_does_not_book(self, calendar, contact, monkeypatch):
        # A read-only freeBusy check is now allowed pre-confirmation — the
        # precheck validates the requested time for real (see
        # _precheck_book_appointment) — but a WRITE (creating the event) must
        # still never happen until the customer explicitly confirms.
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        out = self._book(contact)
        assert out["ok"] is False and out["needs_confirmation"] is True
        assert out["details"]["start_iso"] == self.ARGS["start_iso"]
        assert Record.objects.filter(type="booking").count() == 0
        assert not any("/events" in url for _, url in spy.calls)

    def test_precheck_refuses_a_real_conflict_before_asking_to_confirm(
            self, calendar, contact, monkeypatch):
        # The exact live incident: the model must not be able to ask "shall I
        # book that?" for a time that can never succeed — a REAL conflict is
        # reported immediately, on the very first (unconfirmed) call, instead
        # of only being discovered after the customer already agreed.
        busy = [{"start": "2026-07-27T14:00:00+08:00", "end": "2026-07-27T14:30:00+08:00"}]
        spy = _RequestSpy(busy=busy)
        monkeypatch.setattr(actions, "_request", spy)
        out = self._book(contact)
        assert out["ok"] is False
        assert "needs_confirmation" not in out  # never even reaches the ask
        assert out["error"] == "that time is already booked on the calendar"
        assert not any("/events" in url for _, url in spy.calls)

    def test_precheck_refuses_outside_hours_before_asking_to_confirm(
            self, calendar, contact, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        out = self._book(contact, {"start_iso": "2026-07-27T22:00:00+08:00"})
        assert out["ok"] is False
        assert "needs_confirmation" not in out
        assert "outside the bookable opening hours" in out["error"]
        assert not any("/events" in url for _, url in spy.calls)

    def test_confirmed_out_of_nowhere_is_refused(self, calendar, contact, monkeypatch):
        # confirmed=true with no prior ask must not execute — it becomes the ask.
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        out = self._book(contact, {**self.ARGS, "confirmed": True})
        assert out["ok"] is False and out["needs_confirmation"] is True
        assert not any("/events" in url for _, url in spy.calls)

    def test_same_session_confirm_is_refused(self, calendar, contact, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        self._book(contact, session="round-1")
        out = self._book(contact, {**self.ARGS, "confirmed": True}, session="round-1")
        assert out["ok"] is False and out["needs_confirmation"] is True
        assert not any("/events" in url for _, url in spy.calls)

    def test_later_session_confirm_executes(self, calendar, contact, monkeypatch):
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        self._book(contact, session="round-1")
        out = self._book(contact, {**self.ARGS, "confirmed": True}, session="round-2")
        assert out["ok"] is True and out["confirmed"] is True
        assert Record.objects.filter(type="booking").count() == 1
        # the pending ask is consumed — a replayed confirm must re-ask, not re-book
        again = self._book(contact, {**self.ARGS, "confirmed": True}, session="round-3")
        assert again["ok"] is False and again["needs_confirmation"] is True

    def test_changed_details_must_reconfirm(self, calendar, contact, monkeypatch):
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        self._book(contact, session="round-1")                       # asked for 14:00
        other = {"start_iso": "2026-07-28T10:00:00+08:00", "confirmed": True}
        out = self._book(contact, other, session="round-2")          # confirms 10:00?!
        assert out["ok"] is False and "changed" in out["instruction"]
        assert Record.objects.filter(type="booking").count() == 0
        # after re-asking, the new details confirm fine in a later round
        out = self._book(contact, {**other}, session="round-3")
        assert out["ok"] is True

    def test_expired_ask_reasks(self, calendar, contact, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        self._book(contact, session="round-1")
        PendingToolAction.objects.filter(contact=contact, tool="book_appointment").update(
            asked_at=timezone.now() - timedelta(seconds=tools_mod._CONFIRM_TTL_S + 1))
        out = self._book(contact, {**self.ARGS, "confirmed": True}, session="round-2")
        assert out["ok"] is False and out["needs_confirmation"] is True
        assert not any("/events" in url for _, url in spy.calls)
        # the expired row was pruned and the confirm re-recorded a fresh ask
        row = PendingToolAction.objects.get(contact=contact, tool="book_appointment")
        assert row.session == "round-2"

    def test_mutation_without_contact_is_refused(self, calendar, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("must not book without a contact"))
        out = tools_mod.dispatch("book_appointment", dict(self.ARGS),
                                 contact=None, channel="whatsapp")
        assert out["ok"] is False and "unidentified" in out["error"]

    def test_read_only_tool_needs_no_confirmation(self, calendar, contact, monkeypatch):
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        out = tools_mod.dispatch("check_calendar_availability", {},
                                 contact=contact, channel="whatsapp")
        assert out["ok"] is True and "slots" in out

    def test_requested_start_iso_surfaces_a_definitive_answer(self, calendar, contact,
                                                              monkeypatch):
        # The exact live incident, at the dispatch layer: the model asks about
        # a specific time and must get a definitive requested_available
        # answer, not just a generic slots list it has to guess from.
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        out = tools_mod.dispatch(
            "check_calendar_availability",
            {"requested_start_iso": "2026-07-27T13:00:00+08:00"},
            contact=contact, channel="whatsapp")
        assert out["ok"] is True
        assert out["requested_available"] is True
        assert "requested_reason" not in out  # only present when unavailable
        # The generic "earliest slots" list must NOT be shown alongside a
        # definitive "yes, available" answer — a live test (gpt-4o-mini, a
        # competent model) showed the model reading the requested time's
        # absence from that list as "not available" and contradicting its
        # own requested_available=true, even with zero conversation history.
        assert "slots" not in out
        assert "AVAILABLE" in out["note"].upper()

    def test_requested_start_iso_busy_includes_a_note(self, calendar, contact, monkeypatch):
        busy = [{"start": "2026-07-27T13:00:00+08:00", "end": "2026-07-27T13:30:00+08:00"}]
        monkeypatch.setattr(actions, "_request", _RequestSpy(busy=busy))
        out = tools_mod.dispatch(
            "check_calendar_availability",
            {"requested_start_iso": "2026-07-27T13:00:00+08:00"},
            contact=contact, channel="whatsapp")
        assert out["requested_available"] is False
        assert out["requested_reason"] == "busy"
        assert "already booked" in out["note"]


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

    def test_booking_intent_forces_a_tool_call_on_the_first_round(
            self, calendar, contact, fake_openai, monkeypatch):
        # The exact live incident: asked the SAME booking question repeatedly
        # in one conversation, the model just pattern-matched its own earlier
        # (wrong) reply from history and never called a tool at all —
        # tool_choice="auto" never obligates a fresh check. A message that
        # plainly signals booking intent must force the first round.
        monkeypatch.setattr(actions, "_request", _RequestSpy())
        fake_openai.scripts = [
            _with_tools(_tool_call("c1", "check_calendar_availability", "{}")),
            _prose("Here's what's open tomorrow."),
        ]
        _reply(contact, text="Can you help me book a timeslot for tomorrow 11am?")
        assert fake_openai.calls[0]["tool_choice"] == "required"
        # the SECOND round must not stay forced — the model needs to be able
        # to conclude with plain prose once it has what it needs.
        assert fake_openai.calls[1]["tool_choice"] == "auto"

    def test_plain_chat_does_not_force_tool_choice(self, calendar, contact, fake_openai):
        # A message with no booking-ish language must not force a pointless
        # tool call — "auto" stays the default for ordinary conversation.
        fake_openai.scripts = [_prose("We're open Mon-Fri, 9am to 6pm!")]
        _reply(contact, text="What are your business hours?")
        assert fake_openai.calls[0]["tool_choice"] == "auto"

    def test_booking_through_the_loop(self, calendar, contact, admin, fake_openai, monkeypatch):
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        # Reply round 1: the model tries to book — the gate refuses and the
        # model relays the confirmation question. Nothing is booked yet.
        fake_openai.scripts = [
            _with_tools(_tool_call("c1", "book_appointment",
                                   '{"start_iso": "2026-07-27T14:00:00+08:00", "title": "Consult"}')),
            _prose("Just to confirm — Monday 2pm for a consult, shall I book it?"),
        ]
        out = _reply(contact)
        assert out == "Just to confirm — Monday 2pm for a consult, shall I book it?"
        assert Record.objects.filter(type="booking").count() == 0
        assert AuditLog.objects.filter(action="ai.booking").count() == 0
        # Reply round 2 (customer said yes): confirmed re-call books for real.
        fake_openai.scripts = [
            _with_tools(_tool_call("c2", "book_appointment",
                                   '{"start_iso": "2026-07-27T14:00:00+08:00", "title": "Consult", "confirmed": true}')),
            _prose("Booked! See you Monday at 2pm."),
        ]
        out = _reply(contact)
        assert out == "Booked! See you Monday at 2pm."
        assert Record.objects.filter(type="booking").count() == 1
        assert AuditLog.objects.filter(action="ai.booking").count() == 1

    def test_same_reply_confirm_bypass_is_blocked(self, calendar, contact, fake_openai, monkeypatch):
        # A runaway model that asks and immediately "confirms" inside ONE reply
        # round must not book — the gate keys pending asks to the reply session.
        spy = _RequestSpy()
        monkeypatch.setattr(actions, "_request", spy)
        fake_openai.scripts = [
            _with_tools(_tool_call("c1", "book_appointment",
                                   '{"start_iso": "2026-07-27T14:00:00+08:00"}')),
            _with_tools(_tool_call("c2", "book_appointment",
                                   '{"start_iso": "2026-07-27T14:00:00+08:00", "confirmed": true}')),
            _prose("Can you confirm Monday 2pm first?"),
        ]
        out = _reply(contact)
        assert out == "Can you confirm Monday 2pm first?"
        assert Record.objects.filter(type="booking").count() == 0
        assert AuditLog.objects.filter(action="ai.booking").count() == 0

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
        # The model must NOT be told it "can take real actions using tools"
        # when no tools= is actually attached to this call — that mismatch is
        # exactly what made a Gemini→ilmu failover hallucinate a fake booking
        # confirmation with no calendar event and no crm.Record ever created.
        assert not any("can take real actions" in m["content"]
                       for m in kwargs["messages"] if m["role"] == "system")

    def test_failover_skips_ilmu_in_favor_of_a_later_tool_capable_provider(
            self, calendar, contact, fake_openai, monkeypatch):
        # The exact live fix: ilmu sits between two tool-capable providers in
        # priority order (google, ilmu, openai, openrouter). When the top pick
        # fails and tools matter for this conversation, ilmu must be tried
        # LAST, not second — the other tool-capable provider is healthy and
        # should be reached first, avoiding the hallucination risk entirely
        # rather than just mitigating it with a prompt instruction.
        ScriptedOpenAI.fail_for_models = {"model-x"}
        monkeypatch.setattr(
            wa_config, "chat_provider_candidates",
            lambda: [("http://llm", "key-1", "model-x"),
                     ("https://api.ilmu.example/v1", "key-2", "ilmu-v3.1"),
                     ("http://llm-3", "key-3", "model-z")])
        fake_openai.scripts = [_prose("Sure, I can help with that.")]
        out = _reply(contact)
        assert out == "Sure, I can help with that."
        assert len(fake_openai.calls) == 2  # model-x failed, model-z succeeded — ilmu untouched
        assert fake_openai.calls[1]["model"] == "model-z"
        assert "tools" in fake_openai.calls[1]

    def test_failover_to_tool_incapable_provider_does_not_claim_tool_access(
            self, calendar, contact, fake_openai, monkeypatch):
        # Reproduces the exact live incident: EVERY tool-capable provider is
        # down, forcing a fall-through to ilmu (no tool support) — the reply
        # must not claim tool access it doesn't have for THIS attempt.
        ScriptedOpenAI.fail_for_models = {"model-x"}
        monkeypatch.setattr(
            wa_config, "chat_provider_candidates",
            lambda: [("http://llm", "key-1", "model-x"),
                     ("https://api.ilmu.example/v1", "key-2", "ilmu-v3.1")])
        fake_openai.scripts = [_prose("Sure, I can help with that.")]
        out = _reply(contact)
        assert out == "Sure, I can help with that."
        assert len(fake_openai.calls) == 2
        fallback_kwargs = fake_openai.calls[1]
        assert "tools" not in fallback_kwargs
        assert not any("can take real actions" in m["content"]
                       for m in fallback_kwargs["messages"] if m["role"] == "system")
        # The explicit backstop instruction must be present telling the model
        # it has no tool access THIS turn and must not claim otherwise.
        assert any("have NO access to the calendar" in m["content"]
                  for m in fallback_kwargs["messages"] if m["role"] == "system")

    def test_gemini_provider_takes_the_reasoning_effort_path(self, calendar, contact,
                                                             fake_openai, monkeypatch):
        # Gemini's default "thinking" draws from the SAME max_tokens budget as
        # the visible reply — on a live booking reply this ate the whole
        # budget and cut the customer-facing text off mid-sentence.
        # reasoning_effort="low" is the verified-working knob (an
        # extra_body={"google": {"thinking_config": ...}} shape 400s on the
        # real endpoint — "Request contains an invalid argument" — do not
        # reintroduce it here).
        monkeypatch.setattr(
            assistant, "_provider",
            lambda: ("https://generativelanguage.googleapis.com/v1beta/openai/",
                     "k", "gemini-flash-latest"))
        fake_openai.scripts = [_prose("Sure, let me check.")]
        out = _reply(contact)
        assert out == "Sure, let me check."
        kwargs = fake_openai.calls[0]
        assert kwargs["reasoning_effort"] == "low"

    def test_temporal_context_is_injected(self, contact, fake_openai):
        # The model has no other way to know today's date or this business's
        # UTC offset — without this it defaults to +00:00, which silently
        # books/rejects appointments hours off from what the customer meant.
        fake_openai.scripts = [_prose("Sure.")]
        _reply(contact)
        messages = fake_openai.calls[0]["messages"]
        assert any("Current date/time where this business operates" in m["content"]
                  for m in messages if m["role"] == "system")

    def test_failing_provider_falls_through_to_the_next_one(self, contact, fake_openai,
                                                            monkeypatch):
        # "priority order" used to only decide which provider got PICKED, never
        # retried a different one once picked — a provider with a valid key
        # that started erroring (quota/5xx/timeout) kept failing forever
        # (observed live: Gemini hit a 429 and every retry hit Gemini again).
        # The top pick (patched via _provider) must fall through to the next
        # provider in chat_provider_candidates() when its call raises.
        ScriptedOpenAI.fail_for_models = {"model-x"}
        monkeypatch.setattr(
            wa_config, "chat_provider_candidates",
            lambda: [("http://llm", "key-1", "model-x"),
                     ("http://llm-2", "key-2", "model-y")])
        fake_openai.scripts = [_prose("Handled by the backup provider.")]
        out = _reply(contact)
        assert out == "Handled by the backup provider."
        # first call was the failing provider, second succeeded on the fallback
        assert len(fake_openai.calls) == 2
        assert fake_openai.calls[0]["model"] == "model-x"
        assert fake_openai.calls[1]["model"] == "model-y"

    def test_all_providers_failing_raises_the_last_error(self, contact, fake_openai,
                                                         monkeypatch):
        ScriptedOpenAI.fail_for_models = {"model-x", "model-y"}
        monkeypatch.setattr(
            wa_config, "chat_provider_candidates",
            lambda: [("http://llm", "key-1", "model-x"),
                     ("http://llm-2", "key-2", "model-y")])
        with pytest.raises(RuntimeError, match="simulated provider failure"):
            _reply(contact)
        assert len(fake_openai.calls) == 2
