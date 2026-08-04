"""AI Google Sheets READ tool (#7) — closes the reschedule-flow gap where the
AI was told to 'use the sheet tools' that didn't exist.

Offline — the Sheets HTTP GET is mocked.
"""
from datetime import timedelta

import pytest
from django.utils import timezone

from aicore import tools as tools_mod
from crm.models import Contact
from integrations import actions
from integrations.models import Integration


@pytest.fixture
def sheets(db):
    return Integration.objects.create(
        provider="google_sheets", name="Sheets", status="healthy",
        credentials={"access_token": "tok",
                     "expires_at": (timezone.now() + timedelta(hours=2)).isoformat()},
        config={})


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


class _Spy:
    def __call__(self, method, url, **kw):
        assert method == "GET" and "/values/" in url
        return True, {"range": "Availability!A1:B2",
                      "values": [["Mon", "9am"], ["Tue", "10am"]]}


@pytest.mark.django_db
class TestReadSheetAction:
    def test_reads_values(self, sheets, monkeypatch):
        monkeypatch.setattr(actions, "_request", _Spy())
        res = actions.read_sheet(config={"spreadsheetId": "S1", "range": "Availability!A1:B2"})
        assert res["ok"] is True
        assert res["data"]["values"] == [["Mon", "9am"], ["Tue", "10am"]]

    def test_skips_when_not_connected(self, db, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("no network in sandbox"))
        res = actions.read_sheet(config={"spreadsheetId": "S1"})
        assert res["ok"] is False and res["skipped"] is True

    def test_skips_without_sheet_id(self, sheets, monkeypatch):
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: pytest.fail("should not call without an id"))
        res = actions.read_sheet(config={})
        assert res["ok"] is False and res["skipped"] is True


@pytest.mark.django_db
class TestReadSheetTool:
    def test_offered_only_when_sheets_connected(self, calendar, student):
        # calendar only → no read_sheet
        names = {t["function"]["name"] for t in tools_mod.tool_specs(student, "whatsapp")}
        assert "read_sheet" not in names
        Integration.objects.create(
            provider="google_sheets", name="Sheets", status="healthy",
            credentials={"access_token": "tok",
                         "expires_at": (timezone.now() + timedelta(hours=2)).isoformat()})
        names = {t["function"]["name"] for t in tools_mod.tool_specs(student, "whatsapp")}
        assert "read_sheet" in names

    def test_dispatch_returns_rows(self, sheets, student, monkeypatch):
        monkeypatch.setattr(actions, "_request", _Spy())
        out = tools_mod.dispatch("read_sheet", {"spreadsheet_id": "S1"},
                                 contact=student, channel="whatsapp")
        assert out["ok"] is True and out["rows"] == [["Mon", "9am"], ["Tue", "10am"]]

    def test_requires_spreadsheet_id(self, sheets, student):
        out = tools_mod.dispatch("read_sheet", {}, contact=student, channel="whatsapp")
        assert out["ok"] is False and "spreadsheet_id" in out["error"]

    def test_read_tool_no_confirmation_needed(self, sheets, student, monkeypatch):
        monkeypatch.setattr(actions, "_request", _Spy())
        out = tools_mod.dispatch("read_sheet", {"spreadsheet_id": "S1"},
                                 contact=student, channel="whatsapp")
        assert out["ok"] is True and "needs_confirmation" not in out
