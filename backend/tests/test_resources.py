"""Resource (service-provider / teacher) entity + the teacher↔student binding.

Covers the model + resolver (crm/resources.py), the AI context injection
(aicore.assistant._resources_block + generate_reply), and the REST API
(/api/resources + bindings) with auth/role gating and tenant scoping.
"""
import json
import types

import pytest
from django.contrib.auth.models import User

from aicore import assistant
from crm import resources as res_mod
from crm.models import Contact, Profile, Resource, ResourceLink, Setting

TOKEN_ADMIN = "res-admin-token"
TOKEN_AGENT = "res-agent-token"
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="res_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="res_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


@pytest.fixture
def student(db):
    return Contact.objects.create(name="Aminah", phone="+60123456789", status="active")


@pytest.fixture
def teacher(db):
    return Resource.objects.create(
        name="Ms Lee", email="lee@studio.test",
        config={"availability_sheet_id": "SHEET123", "availability_range": "Avail!A1:D50"})


# ---- model + resolver -------------------------------------------------------

@pytest.mark.django_db
class TestResolver:
    def test_link_and_resolve_both_directions(self, teacher, student):
        res_mod.link(teacher, student, relationship="primary")
        assert res_mod.resources_for_contact(student) == [teacher]
        assert res_mod.contacts_for_resource(teacher) == [student]

    def test_link_is_idempotent(self, teacher, student):
        res_mod.link(teacher, student)
        res_mod.link(teacher, student, relationship="primary")
        assert ResourceLink.objects.filter(resource=teacher, contact=student).count() == 1
        assert ResourceLink.objects.get().relationship == "primary"

    def test_inactive_link_is_excluded(self, teacher, student):
        res_mod.link(teacher, student, active=False)
        assert res_mod.resources_for_contact(student) == []
        assert res_mod.resources_for_contact(student, active_only=False) == [teacher]

    def test_inactive_resource_is_excluded(self, teacher, student):
        res_mod.link(teacher, student)
        teacher.active = False
        teacher.save()
        assert res_mod.resources_for_contact(student) == []

    def test_unlink(self, teacher, student):
        res_mod.link(teacher, student)
        assert res_mod.unlink(teacher, student) == 1
        assert res_mod.resources_for_contact(student) == []

    def test_availability_ref(self, teacher):
        assert res_mod.availability_ref(teacher)["availability_sheet_id"] == "SHEET123"

    def test_resource_label_setting(self, db):
        assert res_mod.resource_label() == "provider"       # neutral default
        Setting.objects.create(key="resource_label", value="teacher")
        assert res_mod.resource_label() == "teacher"


# ---- AI context injection ---------------------------------------------------

class _CapturingOpenAI:
    last_messages = None

    def __init__(self, base_url=None, api_key=None):
        class _C:
            def create(self, **kw):
                _CapturingOpenAI.last_messages = kw.get("messages")
                msg = types.SimpleNamespace(content="ok", tool_calls=None)
                return types.SimpleNamespace(
                    choices=[types.SimpleNamespace(message=msg)], usage=None)

        self.chat = types.SimpleNamespace(completions=_C())


@pytest.mark.django_db
class TestAiInjection:
    def test_block_names_provider_and_sheet(self, teacher, student):
        res_mod.link(teacher, student)
        block = assistant._resources_block(student)
        assert "Ms Lee" in block and "SHEET123" in block

    def test_block_empty_without_binding(self, student):
        assert assistant._resources_block(student) == ""

    def test_generate_reply_injects_the_block(self, teacher, student, monkeypatch):
        import openai

        res_mod.link(teacher, student)
        Setting.objects.create(key="resource_label", value="teacher")
        monkeypatch.setattr(openai, "OpenAI", _CapturingOpenAI)
        monkeypatch.setattr(assistant, "_provider",
                            lambda: ("http://llm", "key-1", "model-x"))
        assistant.generate_reply(
            [{"role": "user", "content": "can I reschedule my class?"}],
            contact_name="Aminah", phone="+60123456789", channel="whatsapp",
            contact_id=student.pk)
        blob = json.dumps(_CapturingOpenAI.last_messages)
        assert "SHEET123" in blob and "Ms Lee" in blob and "TEACHER(S)" in blob


# ---- REST API ---------------------------------------------------------------

def _create(client, **over):
    body = {"name": "Ms Lee", "email": "lee@studio.test",
            "config": {"availability_sheet_id": "SHEET123"}}
    body.update(over)
    return client.post("/api/resources", data=json.dumps(body),
                       content_type="application/json", **ADMIN)


@pytest.mark.django_db
class TestResourceApi:
    def test_requires_auth(self, client, admin):
        assert client.get("/api/resources").status_code == 401

    def test_agent_can_view_not_manage(self, client, agent):
        assert client.get("/api/resources", **AGENT).status_code == 200
        r = client.post("/api/resources", data=json.dumps({"name": "X"}),
                        content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_create_requires_name(self, client, admin):
        r = client.post("/api/resources", data=json.dumps({"email": "x@y.z"}),
                        content_type="application/json", **ADMIN)
        assert r.status_code == 400

    def test_create_list_patch_delete(self, client, admin):
        rid = _create(client).json()["id"]
        assert any(r["id"] == rid for r in client.get("/api/resources", **ADMIN).json()["resources"])
        r = client.patch(f"/api/resources/{rid}", data=json.dumps({"active": False}),
                         content_type="application/json", **ADMIN)
        assert r.status_code == 200 and r.json()["active"] is False
        assert client.delete(f"/api/resources/{rid}", **ADMIN).status_code == 200
        assert Resource.objects.count() == 0

    def test_bind_and_unbind_contact(self, client, admin, student):
        rid = _create(client).json()["id"]
        r = client.post(f"/api/resources/{rid}/links",
                        data=json.dumps({"contact_id": student.pk, "relationship": "primary"}),
                        content_type="application/json", **ADMIN)
        assert r.status_code == 200
        link_id = r.json()["id"]
        listed = client.get(f"/api/resources/{rid}/links", **ADMIN).json()["links"]
        assert listed[0]["contact_name"] == "Aminah"
        assert client.delete(f"/api/resources/{rid}/links/{link_id}", **ADMIN).status_code == 200
        assert ResourceLink.objects.count() == 0

    def test_bind_rejects_unknown_contact(self, client, admin):
        rid = _create(client).json()["id"]
        r = client.post(f"/api/resources/{rid}/links",
                        data=json.dumps({"contact_id": 99999}),
                        content_type="application/json", **ADMIN)
        assert r.status_code == 400
