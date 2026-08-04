"""Phase 2 Workstream B (PR-2) — user->tenant scoping.

Two tenants, each with its own contact + admin. Assert:
- whoami returns the caller's tenant
- GET /api/v1/contacts returns only the caller's tenant's rows
- the retrofitted legacy GET /api/contacts is likewise scoped
- no tenant -> fail-closed on the v1 endpoint
"""
import pytest
from django.contrib.auth.models import User
from django.test import Client

from crm.models import Contact, Profile
from ingest.models import Tenant


@pytest.fixture
def two_tenants(db):
    t1 = Tenant.objects.create(name="T One", slug="t1")
    t2 = Tenant.objects.create(name="T Two", slug="t2")
    u1 = User.objects.create_user(username="a1", password="x")
    u2 = User.objects.create_user(username="a2", password="x")
    Profile.objects.create(user=u1, role="admin", token="tok1", tenant=t1)
    Profile.objects.create(user=u2, role="admin", token="tok2", tenant=t2)
    Contact.objects.create(name="Alice T1", phone="+60111111111", tenant=t1)
    Contact.objects.create(name="Bob T2", phone="+60122222222", tenant=t2)
    return t1, t2


def _auth(tok):
    return {"HTTP_AUTHORIZATION": f"Bearer {tok}"}


def test_whoami_returns_tenant(two_tenants):
    r = Client().get("/api/v1/whoami", **_auth("tok1"))
    assert r.status_code == 200
    assert r.json()["tenant"] == "t1"


def test_v1_contacts_scoped_to_tenant(two_tenants):
    r = Client().get("/api/v1/contacts", **_auth("tok1"))
    assert r.status_code == 200
    names = [c["name"] for c in r.json()["results"]]
    assert names == ["Alice T1"]  # not Bob


def test_v1_contacts_other_tenant(two_tenants):
    r = Client().get("/api/v1/contacts", **_auth("tok2"))
    names = [c["name"] for c in r.json()["results"]]
    assert names == ["Bob T2"]


def test_v1_contacts_requires_auth(two_tenants):
    r = Client().get("/api/v1/contacts")
    assert r.status_code == 401


def test_legacy_contacts_endpoint_scoped(two_tenants):
    """The retrofitted /api/contacts filters by the caller's tenant too."""
    r = Client().get("/api/contacts", **_auth("tok1"))
    assert r.status_code == 200
    names = [c["name"] for c in r.json()["contacts"]]
    assert "Alice T1" in names and "Bob T2" not in names


def test_paginated_envelope_shape(two_tenants):
    r = Client().get("/api/v1/contacts", **_auth("tok1"))
    body = r.json()
    assert set(body) >= {"count", "page", "num_pages", "page_size", "results"}


def test_legacy_contacts_entity_filter(db):
    """?entity=<slug> narrows the contact list to that entity (topbar switcher),
    and the payload exposes each contact's entity slugs."""
    from crm.models import ContactEntity
    from ingest.models import Entity

    t = Tenant.objects.create(name="T Entity", slug="tb")
    u = User.objects.create_user(username="ab", password="x")
    Profile.objects.create(user=u, role="admin", token="tokb", tenant=t)
    ba = Entity.objects.create(tenant=t, name="Entity A", slug="entity-a")
    bb = Entity.objects.create(tenant=t, name="Entity B", slug="entity-b")
    ca = Contact.objects.create(name="Cust A", phone="+60130000001", tenant=t)
    cb = Contact.objects.create(name="Cust B", phone="+60130000002", tenant=t)
    ContactEntity.objects.create(contact=ca, entity=ba)
    ContactEntity.objects.create(contact=cb, entity=bb)

    r = Client().get("/api/contacts?entity=entity-a", **_auth("tokb"))
    assert r.status_code == 200
    rows = r.json()["contacts"]
    assert [c["name"] for c in rows] == ["Cust A"]
    assert rows[0]["entities"] == ["entity-a"]

    # no entity param -> both contacts in the tenant
    r_all = Client().get("/api/contacts", **_auth("tokb"))
    assert {c["name"] for c in r_all.json()["contacts"]} == {"Cust A", "Cust B"}
