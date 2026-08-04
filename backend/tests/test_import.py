"""Bulk contact import from Excel/CSV — parser, upsert, and the endpoint.

Fully offline. Builds real .xlsx (openpyxl) and .csv in memory.
"""
import io
import json

import pytest
from django.contrib.auth.models import User
from openpyxl import Workbook

from crm import importer
from crm.models import Contact, Profile

TOKEN_ADMIN = "imp-admin-token"
TOKEN_AGENT = "imp-agent-token"
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="imp_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="imp_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


def _xlsx(rows: list[list]) -> bytes:
    wb = Workbook()
    ws = wb.active
    for r in rows:
        ws.append(r)
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


CSV_TEXT = "Name,Phone,Email\nAminah,+60123,aminah@x.com\nSiti,+60999,\n"


# ---- parser + mapping -------------------------------------------------------

class TestParse:
    def test_detect_mapping_from_aliases(self):
        m = importer.detect_mapping(["Full Name", "Mobile", "E-mail", "Student ID"])
        assert m["name"] == "Full Name"
        assert m["phone"] == "Mobile"
        assert m["email"] == "E-mail"
        assert m["external_id"] == "Student ID"

    def test_parse_csv(self):
        headers, rows = importer.parse(CSV_TEXT.encode(), "roster.csv")
        assert headers == ["Name", "Phone", "Email"]
        assert len(rows) == 2 and rows[0]["Name"] == "Aminah"

    def test_parse_xlsx(self):
        data = _xlsx([["Name", "Phone"], ["Aminah", "+60123"], ["", ""], ["Siti", "+60999"]])
        headers, rows = importer.parse(data, "roster.xlsx")
        assert headers == ["Name", "Phone"]
        assert [r["Name"] for r in rows] == ["Aminah", "Siti"]  # blank row skipped

    def test_unsupported_extension(self):
        with pytest.raises(ValueError):
            importer.parse(b"x", "roster.pdf")


# ---- upsert -----------------------------------------------------------------

@pytest.mark.django_db
class TestRunImport:
    def test_creates_and_dedupes_by_phone(self):
        Contact.objects.create(name="Aminah Old", phone="+60123", status="active")
        rows = [{"Name": "Aminah", "Phone": "+60123"}, {"Name": "Siti", "Phone": "+60999"}]
        mapping = {"name": "Name", "phone": "Phone"}
        summary = importer.run_import(rows, mapping, tenant=None)
        assert summary["created"] == 1 and summary["updated"] == 1
        assert Contact.objects.get(phone="+60123").name == "Aminah"  # updated in place

    def test_missing_phone_is_skipped_with_reason(self):
        rows = [{"Name": "NoPhone", "Phone": ""}]
        summary = importer.run_import(rows, {"name": "Name", "phone": "Phone"}, tenant=None)
        assert summary["created"] == 0 and summary["skipped"] == 1
        assert summary["errors"][0]["reason"] == "missing name or phone"

    def test_dry_run_writes_nothing(self):
        rows = [{"Name": "Aminah", "Phone": "+60123"}]
        summary = importer.run_import(rows, {"name": "Name", "phone": "Phone"},
                                      tenant=None, dry_run=True)
        assert summary["created"] == 1 and Contact.objects.count() == 0

    def test_duplicate_phone_within_file_updates_not_errors(self):
        rows = [{"Name": "A", "Phone": "+60123"}, {"Name": "B", "Phone": "+60123"}]
        summary = importer.run_import(rows, {"name": "Name", "phone": "Phone"}, tenant=None)
        assert summary["created"] == 1 and summary["updated"] == 1
        assert Contact.objects.filter(phone="+60123").count() == 1


# ---- endpoint ---------------------------------------------------------------

@pytest.mark.django_db
class TestEndpoint:
    def test_requires_auth(self, client, admin):
        assert client.post("/api/contacts/import").status_code == 401

    def test_agent_forbidden(self, client, agent):
        data = _xlsx([["Name", "Phone"], ["Aminah", "+60123"]])
        r = client.post("/api/contacts/import",
                        {"file": SimpleUpload(data, "roster.xlsx")}, **AGENT)
        assert r.status_code == 403  # capability check precedes parsing
        assert Contact.objects.count() == 0

    def test_preview_then_commit(self, client, admin):
        data = _xlsx([["Name", "Phone", "Email"], ["Aminah", "+60123", "a@x.com"],
                      ["Siti", "+60999", ""]])
        up = SimpleUpload(data, "roster.xlsx")
        prev = client.post("/api/contacts/import?preview=1", {"file": up}, **ADMIN).json()
        assert prev["preview"] is True and prev["created"] == 2
        assert prev["mapping"]["name"] == "Name"
        assert Contact.objects.count() == 0  # preview wrote nothing

        up2 = SimpleUpload(data, "roster.xlsx")
        done = client.post("/api/contacts/import", {"file": up2}, **ADMIN).json()
        assert done["created"] == 2 and Contact.objects.count() == 2

    def test_rejects_non_spreadsheet(self, client, admin):
        up = SimpleUpload(b"nope", "roster.pdf")
        r = client.post("/api/contacts/import", {"file": up}, **ADMIN)
        assert r.status_code == 400


class SimpleUpload(io.BytesIO):
    """A BytesIO the Django test client treats as an uploaded file (has .name)."""

    def __init__(self, data: bytes, name: str):
        super().__init__(data)
        self.name = name
