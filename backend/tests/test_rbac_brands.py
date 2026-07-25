"""Brand + RBAC: superadmin/admin/agent_user access, brand scoping, and the
user-creation hierarchy."""
import json

import pytest
from django.contrib.auth.models import User
from django.test import Client

from crm.models import Contact, ContactBrand, Profile
from ingest.models import Brand, Tenant


@pytest.fixture
def world(db):
    t1 = Tenant.objects.create(name="T1", slug="t1")
    t2 = Tenant.objects.create(name="T2", slug="t2")
    b1a = Brand.objects.create(tenant=t1, name="T1 Brand A", slug="a")
    b1b = Brand.objects.create(tenant=t1, name="T1 Brand B", slug="b")
    b2 = Brand.objects.create(tenant=t2, name="T2 Brand", slug="c")

    def mk(username, role, tenant, token, brands=()):
        u = User.objects.create_user(username=username, password="x")
        p = Profile.objects.create(user=u, role=role, tenant=tenant, token=token)
        if brands:
            p.brands.set(brands)
        return u

    su = mk("su", "superadmin", None, "t-su")
    a1 = mk("a1", "admin", t1, "t-a1")
    c1 = mk("c1", "agent", t1, "t-c1", brands=[b1a])  # only brand A

    # contacts: one on each t1 brand, one on t2
    cust_a = Contact.objects.create(name="Cust A", phone="+60111", tenant=t1)
    ContactBrand.objects.create(contact=cust_a, brand=b1a)
    cust_b = Contact.objects.create(name="Cust B", phone="+60112", tenant=t1)
    ContactBrand.objects.create(contact=cust_b, brand=b1b)
    cust_t2 = Contact.objects.create(name="Cust T2", phone="+60222", tenant=t2)
    ContactBrand.objects.create(contact=cust_t2, brand=b2)
    return locals()


def auth(tok):
    return {"HTTP_AUTHORIZATION": f"Bearer {tok}"}


def post(path, body, tok):
    return Client().post(path, data=json.dumps(body), content_type="application/json", **auth(tok))


# ── whoami brands ────────────────────────────────────────────────────────────

def test_whoami_brands_superadmin_all(world):
    r = Client().get("/api/v1/whoami", **auth("t-su"))
    assert set(r.json()["brands"]) == {"a", "b", "c"}


def test_whoami_brands_admin_tenant(world):
    r = Client().get("/api/v1/whoami", **auth("t-a1"))
    assert set(r.json()["brands"]) == {"a", "b"}  # both t1 brands, not c


def test_whoami_brands_agent_assigned(world):
    r = Client().get("/api/v1/whoami", **auth("t-c1"))
    assert r.json()["brands"] == ["a"]  # only assigned brand


# ── contact brand scoping ───────────────────────────────────────────────────

def test_contacts_admin_sees_tenant(world):
    r = Client().get("/api/v1/contacts", **auth("t-a1"))
    names = {c["name"] for c in r.json()["results"]}
    assert names == {"Cust A", "Cust B"}  # not T2


def test_contacts_agent_sees_only_their_brand(world):
    r = Client().get("/api/v1/contacts", **auth("t-c1"))
    names = {c["name"] for c in r.json()["results"]}
    assert names == {"Cust A"}  # brand B contact hidden


def test_contacts_superadmin_sees_all(world):
    r = Client().get("/api/v1/contacts", **auth("t-su"))
    assert len(r.json()["results"]) == 3


# ── user creation hierarchy ──────────────────────────────────────────────────

def test_admin_creates_agent_with_brands(world):
    r = post("/api/v1/users",
             {"username": "newcol", "password": "password1", "role": "agent",
              "brand_ids": [world["b1a"].id]}, "t-a1")
    assert r.status_code == 201
    p = Profile.objects.get(user__username="newcol")
    assert p.role == "agent" and p.tenant == world["t1"]
    assert list(p.brands.values_list("slug", flat=True)) == ["a"]


def test_admin_cannot_create_admin(world):
    r = post("/api/v1/users", {"username": "x", "password": "password1", "role": "admin"}, "t-a1")
    assert r.status_code == 403


def test_admin_cannot_assign_other_tenant_brand(world):
    r = post("/api/v1/users",
             {"username": "y", "password": "password1", "role": "agent",
              "brand_ids": [world["b2"].id]}, "t-a1")  # b2 is T2's
    assert r.status_code == 403  # brands outside the admin's tenant


def test_agent_cannot_list_or_create_users(world):
    assert Client().get("/api/v1/users", **auth("t-c1")).status_code == 403
    assert post("/api/v1/users", {"username": "z", "password": "password1",
                                  "role": "agent"}, "t-c1").status_code == 403


def test_superadmin_creates_admin_in_tenant(world):
    r = post("/api/v1/users",
             {"username": "newadmin", "password": "password1", "role": "admin",
              "tenant": "t2"}, "t-su")
    assert r.status_code == 201
    assert Profile.objects.get(user__username="newadmin").tenant == world["t2"]


def test_admin_user_list_scoped_to_tenant(world):
    r = Client().get("/api/v1/users", **auth("t-a1"))
    tenants = {u["tenant"] for u in r.json()["users"]}
    assert tenants == {"t1"}  # admin never sees t2 users


# ── brand creation ───────────────────────────────────────────────────────────

def test_admin_creates_brand_in_own_tenant(world):
    r = post("/api/v1/brands", {"name": "New", "slug": "new"}, "t-a1")
    assert r.status_code == 201
    assert Brand.objects.filter(tenant=world["t1"], slug="new").exists()


# ── user update / delete via /api/v1/users/<id> ──────────────────────────────

def _post_id(user_id, body, tok):
    return Client().post(f"/api/v1/users/{user_id}", data=json.dumps(body),
                         content_type="application/json", **auth(tok))


def test_superadmin_updates_name_and_deactivates(world):
    c1 = User.objects.get(username="c1")
    r = _post_id(c1.id, {"name": "Renamed"}, "t-su")
    assert r.status_code == 200 and r.json()["name"] == "Renamed"
    r = Client().delete(f"/api/v1/users/{c1.id}", **auth("t-su"))
    assert r.status_code == 200 and r.json()["active"] is False


def test_admin_cannot_change_role(world):
    c1 = User.objects.get(username="c1")
    r = _post_id(c1.id, {"role": "admin"}, "t-a1")
    assert r.status_code == 403


def test_admin_cannot_touch_other_tenant_user(world):
    # a1 (tenant t1) tries to manage a t2 user -> forbidden (admins manage
    # agents in their own tenant only). Create a t2 agent_user first.
    t2 = world["t2"]
    u = User.objects.create_user(username="c2", password="x")
    from crm.models import Profile
    Profile.objects.create(user=u, role="agent", tenant=t2)
    r = _post_id(u.id, {"name": "x"}, "t-a1")
    assert r.status_code == 403


def test_cannot_deactivate_last_superadmin(world):
    su = User.objects.get(username="su")
    r = Client().delete(f"/api/v1/users/{su.id}", **auth("t-su"))
    assert r.status_code == 400  # last-superadmin guard


# ── backward-compat alias: /api/auth/users still works ───────────────────────

def test_auth_alias_list_parity(world):
    r = Client().get("/api/auth/users", **auth("t-su"))
    assert r.status_code == 200
    assert len(r.json()["users"]) == User.objects.count()
