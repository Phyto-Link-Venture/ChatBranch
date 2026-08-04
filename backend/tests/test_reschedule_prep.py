"""Reschedule-flow prep — server-independent parts of #4.

* A booking stamps crm.Record.resource with the customer's bound provider
  (teacher), so a later reschedule/cancel keeps the same provider.
* list_my_meetings reads the customer's upcoming bookings from the CRM, and is
  offered as a tool ONLY when the customer actually has bookings (the sandbox
  stays empty).

Offline — the calendar HTTP call is mocked.
"""
import types
from datetime import timedelta

import pytest
from django.utils import timezone

from aicore import tools as tools_mod
from crm import resources as res_mod
from crm.models import Contact, Record, Resource
from integrations import actions
from integrations.models import Integration


@pytest.fixture
def calendar(db):
    return Integration.objects.create(
        provider="google_calendar", name="Cal", status="healthy",
        credentials={"access_token": "tok",
                     "expires_at": (timezone.now() + timedelta(hours=2)).isoformat()},
        config={"calendar_id": "primary"})


@pytest.fixture
def student(db):
    return Contact.objects.create(name="Aminah", phone="+60123456789", status="active")


@pytest.fixture
def teacher(db):
    return Resource.objects.create(name="Ms Lee", config={"availability_sheet_id": "S1"})


class _ReqSpy:
    def __call__(self, method, url, **kw):
        if "/events" in url:
            return True, {"id": "evt-1", "htmlLink": "https://cal/evt-1"}
        return True, {"calendars": {"primary": {"busy": []}}}


@pytest.mark.django_db
class TestBookingStampsResource:
    def test_booking_records_bound_teacher(self, calendar, student, teacher, monkeypatch):
        res_mod.link(teacher, student)
        monkeypatch.setattr(actions, "_request", _ReqSpy())
        res = actions.book_appointment(
            contact=student, config={"eventStart": "2026-08-10T09:00:00+00:00"})
        assert res["ok"] is True
        rec = Record.objects.get(type="booking")
        assert rec.resource_id == teacher.pk
        assert rec.data["resource_name"] == "Ms Lee"

    def test_booking_without_binding_has_no_resource(self, calendar, student, monkeypatch):
        monkeypatch.setattr(actions, "_request", _ReqSpy())
        actions.book_appointment(contact=student,
                                 config={"eventStart": "2026-08-10T09:00:00+00:00"})
        rec = Record.objects.get(type="booking")
        assert rec.resource_id is None


@pytest.mark.django_db
class TestListMyMeetings:
    def _booking(self, contact, when, resource=None, status="scheduled"):
        return Record.objects.create(
            contact=contact, type="booking", title="Lesson", status=status,
            due_date=when, resource=resource,
            data={"start": f"{when.isoformat()}T09:00:00+00:00"})

    def test_lists_upcoming_with_provider(self, student, teacher):
        self._booking(student, timezone.localdate() + timedelta(days=2), resource=teacher)
        self._booking(student, timezone.localdate() - timedelta(days=5))  # past — excluded
        out = tools_mod.dispatch("list_my_meetings", {}, contact=student, channel="whatsapp")
        assert out["ok"] is True and len(out["meetings"]) == 1
        assert out["meetings"][0]["provider"] == "Ms Lee"

    def test_offered_only_when_bookings_exist(self, calendar, student):
        # calendar connected but no bookings → the two calendar tools only.
        names = {t["function"]["name"] for t in tools_mod.tool_specs(student, "whatsapp")}
        assert "list_my_meetings" not in names
        self._booking(student, timezone.localdate() + timedelta(days=1))
        names = {t["function"]["name"] for t in tools_mod.tool_specs(student, "whatsapp")}
        assert "list_my_meetings" in names

    def test_sandbox_stays_empty(self, db):
        # no contact, no bookings → no tools (unchanged sandbox contract)
        assert tools_mod.tool_specs() == []

    def test_read_tool_needs_no_confirmation(self, student):
        self._booking(student, timezone.localdate() + timedelta(days=1))
        out = tools_mod.dispatch("list_my_meetings", {}, contact=student, channel="whatsapp")
        assert out["ok"] is True and "meetings" in out
