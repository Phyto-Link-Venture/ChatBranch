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


# ── brand update / delete via /api/v1/brands/<id> ────────────────────────────

def _patch(path, body, tok):
    return Client().patch(path, data=json.dumps(body),
                          content_type="application/json", **auth(tok))


def test_admin_patches_brand_in_own_tenant(world):
    b = world["b1a"]
    r = _patch(f"/api/v1/brands/{b.id}", {"name": "Renamed A"}, "t-a1")
    assert r.status_code == 200 and r.json()["name"] == "Renamed A"
    b.refresh_from_db()
    assert b.name == "Renamed A"


def test_admin_patch_is_active_soft_archive(world):
    b = world["b1b"]
    r = _patch(f"/api/v1/brands/{b.id}", {"is_active": False}, "t-a1")
    assert r.status_code == 200 and r.json()["is_active"] is False
    b.refresh_from_db()
    assert b.is_active is False


def test_patch_brand_duplicate_slug_409(world):
    b = world["b1a"]  # slug "a"; b1b is slug "b" in the same tenant
    r = _patch(f"/api/v1/brands/{b.id}", {"slug": "b"}, "t-a1")
    assert r.status_code == 409


def test_agent_cannot_patch_brand(world):
    b = world["b1a"]
    r = _patch(f"/api/v1/brands/{b.id}", {"name": "Nope"}, "t-c1")  # agent
    assert r.status_code == 403


def test_admin_cannot_patch_other_tenant_brand(world):
    b = world["b2"]  # T2's brand
    r = _patch(f"/api/v1/brands/{b.id}", {"name": "X"}, "t-a1")  # a1 is T1 admin
    assert r.status_code == 404


def test_delete_brand_blocked_when_in_use_409(world):
    """b1a has a contact (Cust A) AND an assigned agent (c1) — 409 with counts."""
    b = world["b1a"]
    r = Client().delete(f"/api/v1/brands/{b.id}", **auth("t-a1"))
    assert r.status_code == 409
    body = r.json()
    assert body["contacts"] == 1 and body["agents"] == 1
    assert Brand.objects.filter(pk=b.id).exists()  # not deleted


def test_delete_unused_brand_ok(world):
    fresh = Brand.objects.create(tenant=world["t1"], name="Empty", slug="empty")
    r = Client().delete(f"/api/v1/brands/{fresh.id}", **auth("t-a1"))
    assert r.status_code == 200 and r.json()["ok"] is True
    assert not Brand.objects.filter(pk=fresh.id).exists()


def test_delete_brand_blocked_when_knowledge_attached_409(world):
    from crm.models import KnowledgeItem
    fresh = Brand.objects.create(tenant=world["t1"], name="KB Brand", slug="kbb")
    KnowledgeItem.objects.create(tenant=world["t1"], brand=fresh,
                                 title="Hours", content="9-5")
    r = Client().delete(f"/api/v1/brands/{fresh.id}", **auth("t-a1"))
    assert r.status_code == 409 and r.json()["knowledge"] == 1
    assert Brand.objects.filter(pk=fresh.id).exists()


def test_agent_cannot_delete_brand(world):
    fresh = Brand.objects.create(tenant=world["t1"], name="Empty", slug="empty2")
    r = Client().delete(f"/api/v1/brands/{fresh.id}", **auth("t-c1"))  # agent
    assert r.status_code == 403
    assert Brand.objects.filter(pk=fresh.id).exists()


def test_admin_cannot_delete_other_tenant_brand(world):
    """Tenancy: a T1 admin can't delete (or even see) a T2 brand -> 404."""
    fresh = Brand.objects.create(tenant=world["t2"], name="T2 Empty", slug="t2empty")
    r = Client().delete(f"/api/v1/brands/{fresh.id}", **auth("t-a1"))
    assert r.status_code == 404
    assert Brand.objects.filter(pk=fresh.id).exists()


# ── contact create via /api/v1/contacts (POST) ───────────────────────────────

def test_admin_creates_contact(world):
    r = post("/api/v1/contacts", {"name": "New Person", "phone": "+60999"}, "t-a1")
    assert r.status_code == 201, r.content
    assert r.json()["name"] == "New Person"
    assert Contact.objects.get(phone="+60999").tenant == world["t1"]


def test_agent_cannot_create_contact(world):
    r = post("/api/v1/contacts", {"name": "X", "phone": "+60888"}, "t-c1")
    assert r.status_code == 403
    assert not Contact.objects.filter(phone="+60888").exists()


# ── contact delete via /api/v1/contacts/<id> ─────────────────────────────────

def test_admin_deletes_contact_in_tenant(world):
    c = world["cust_a"]
    r = Client().delete(f"/api/v1/contacts/{c.id}", **auth("t-a1"))
    assert r.status_code == 200 and r.json()["ok"] is True
    assert not Contact.objects.filter(pk=c.id).exists()


def test_superadmin_deletes_any_contact(world):
    c = world["cust_t2"]
    r = Client().delete(f"/api/v1/contacts/{c.id}", **auth("t-su"))
    assert r.status_code == 200
    assert not Contact.objects.filter(pk=c.id).exists()


def test_agent_cannot_delete_contact(world):
    c = world["cust_a"]  # agent c1 CAN see it, but lacks contacts.manage
    r = Client().delete(f"/api/v1/contacts/{c.id}", **auth("t-c1"))
    assert r.status_code == 403
    assert Contact.objects.filter(pk=c.id).exists()


def test_admin_cannot_delete_other_tenant_contact(world):
    c = world["cust_t2"]  # T2's contact
    r = Client().delete(f"/api/v1/contacts/{c.id}", **auth("t-a1"))  # T1 admin
    assert r.status_code == 404
    assert Contact.objects.filter(pk=c.id).exists()


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
