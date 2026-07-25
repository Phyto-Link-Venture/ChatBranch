"""Phase 2 Workstream B (PR-1) — v1 API foundation:
token auth bridge, error envelope, RBAC, OpenAPI schema."""
import pytest
from django.contrib.auth.models import User
from django.test import Client

from crm.models import Profile

TOKEN = "tok_" + "a" * 20


@pytest.fixture
def user(db):
    u = User.objects.create_user(username="agent1", password="x", first_name="Aziz")
    Profile.objects.create(user=u, role="admin", token=TOKEN)
    return u


def auth(token=TOKEN):
    return {"HTTP_AUTHORIZATION": f"Bearer {token}"}


# ── ping (public) ────────────────────────────────────────────────────────────

def test_ping_public(db):
    r = Client().get("/api/v1/ping")
    assert r.status_code == 200
    assert r.json() == {"status": "ok", "version": "v1"}


def test_ping_method_not_allowed_envelope(db):
    r = Client().post("/api/v1/ping")
    assert r.status_code == 405
    assert r.json()["error"]["code"] == "method_not_allowed"


# ── whoami (auth) ────────────────────────────────────────────────────────────

def test_whoami_requires_auth(db):
    r = Client().get("/api/v1/whoami")
    assert r.status_code == 401
    body = r.json()
    assert "error" in body and body["error"]["code"] in ("not_authenticated", "unauthorized")


def test_whoami_rejects_bad_token(user):
    r = Client().get("/api/v1/whoami", **auth("tok_wrong"))
    assert r.status_code == 401
    assert "error" in r.json()


def test_whoami_ok_with_token(user):
    r = Client().get("/api/v1/whoami", **auth())
    assert r.status_code == 200
    data = r.json()
    assert data["username"] == "agent1"
    assert data["name"] == "Aziz"
    assert data["role"] == "admin"
    assert data["tenant"] is None  # user->tenant lands in PR-2


# ── OpenAPI schema ───────────────────────────────────────────────────────────

def test_schema_serves(db):
    r = Client().get("/api/v1/schema")
    assert r.status_code == 200
    assert b"openapi" in r.content


def test_error_envelope_shape(user):
    """Every v1 error is {error:{code,message}}."""
    r = Client().get("/api/v1/whoami")  # 401
    err = r.json()["error"]
    assert set(err) >= {"code", "message"}
    assert isinstance(err["message"], str)


# ── contact detail PATCH (language editor) ──────────────────────────────────

@pytest.fixture
def tenant_contact(db):
    from ingest.models import Tenant
    from crm.models import Contact
    t = Tenant.objects.create(name="T One", slug="t1")
    c = Contact.objects.create(name="KX", phone="+60111111111", tenant=t)
    return t, c


@pytest.fixture
def tenant_admin(tenant_contact):
    t, _ = tenant_contact
    u = User.objects.create_user(username="admin1", password="x")
    Profile.objects.create(user=u, role="admin", token="tok_admin1", tenant=t)
    return u


def test_patch_language(tenant_contact, tenant_admin):
    _, c = tenant_contact
    r = Client().patch(f"/api/v1/contacts/{c.pk}", data='{"language": "ms"}',
                       content_type="application/json", **auth("tok_admin1"))
    assert r.status_code == 200, r.json()
    assert r.json()["language"] == "ms"
    c.refresh_from_db()
    assert c.language == "ms"


def test_patch_language_auto_detect(tenant_contact, tenant_admin):
    """"auto" = no fixed language — valid stored value; the agent detects."""
    _, c = tenant_contact
    r = Client().patch(f"/api/v1/contacts/{c.pk}", data='{"language": "auto"}',
                       content_type="application/json", **auth("tok_admin1"))
    assert r.status_code == 200, r.json()
    c.refresh_from_db()
    assert c.language == "auto"


def test_patch_language_invalid_code_400(tenant_contact, tenant_admin):
    _, c = tenant_contact
    r = Client().patch(f"/api/v1/contacts/{c.pk}", data='{"language": "xx"}',
                       content_type="application/json", **auth("tok_admin1"))
    assert r.status_code == 400
    c.refresh_from_db()
    assert c.language == "en"


def test_patch_scoped_to_tenant_404(tenant_contact):
    """An admin from ANOTHER tenant must not even see the contact (404)."""
    from ingest.models import Tenant
    other = Tenant.objects.create(name="T Two", slug="t2")
    u = User.objects.create_user(username="admin2", password="x")
    Profile.objects.create(user=u, role="admin", token="tok_admin2", tenant=other)
    _, c = tenant_contact
    r = Client().patch(f"/api/v1/contacts/{c.pk}", data='{"language": "ms"}',
                       content_type="application/json", **auth("tok_admin2"))
    assert r.status_code == 404
