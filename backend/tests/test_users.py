"""User-management API tests (/api/auth/users — SUPER ADMIN only).

Covers: list/create/update happy paths, 401 unauthenticated / 403 as admin,
validation (duplicate username case-insensitively, short password, bad role),
the last-active-superadmin demote/deactivate guard, token rotation on password
change (old sessions die), and login rejection after deactivation.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm.models import AuditLog, Profile

SUPER_TOKEN = "test-super-token"
ADMIN_TOKEN = "test-admin-token"
AGENT_TOKEN = "test-agent-token"
SUPER_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {SUPER_TOKEN}"}
ADMIN_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {ADMIN_TOKEN}"}
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123",
                                    first_name="Super Admin")
    Profile.objects.create(user=user, role="superadmin", token=SUPER_TOKEN)
    return user


@pytest.fixture
def admin(db):
    user = User.objects.create_user(username="admin", password="admin123",
                                    first_name="Admin")
    Profile.objects.create(user=user, role="admin", token=ADMIN_TOKEN)
    return user


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="colle", password="collect123",
                                    first_name="Agent")
    Profile.objects.create(user=user, role="agent", token=AGENT_TOKEN)
    return user


def _post(client, path, body, **extra):
    return client.post(path, data=json.dumps(body),
                       content_type="application/json", **extra)


def _create(client, username="agent1", name="Agent One",
            password="collect-pass-1", role="agent", **extra):
    return _post(client, "/api/auth/users",
                 {"username": username, "name": name, "password": password, "role": role},
                 **extra)


@pytest.mark.django_db
class TestAccessControl:
    def test_list_401_without_token(self, client, superadmin):
        r = client.get("/api/auth/users")
        assert r.status_code == 401
        assert r.json() == {"detail": "Authentication required."}

    def test_create_401_without_token(self, client, superadmin):
        assert _create(client).status_code == 401

    def test_update_401_without_token(self, client, superadmin):
        assert _post(client, f"/api/auth/users/{superadmin.pk}", {"name": "X"}).status_code == 401

    def test_list_403_as_agent(self, client, superadmin, agent_user):
        r = client.get("/api/auth/users", **AGENT_AUTH)
        assert r.status_code == 403

    def test_create_403_as_agent(self, client, superadmin, agent_user):
        assert _create(client, **AGENT_AUTH).status_code == 403

    def test_admin_cannot_touch_superadmin(self, client, superadmin, admin):
        assert client.delete(f"/api/auth/users/{superadmin.pk}", **ADMIN_AUTH).status_code == 403


@pytest.mark.django_db
class TestAdminTier:
    """Admins manage only agents in their own tenant (RBAC hierarchy)."""

    def test_admin_lists_own_tenant_only(self, client, superadmin, admin):
        from ingest.models import Tenant
        other = Tenant.objects.create(name="Other", slug="other")
        u = User.objects.create_user(username="faraway", password="x-pass-123")
        Profile.objects.create(user=u, role="agent", tenant=other)
        names = [x["username"] for x in
                 client.get("/api/auth/users", **ADMIN_AUTH).json()["users"]]
        assert "faraway" not in names  # other tenant hidden
        assert "admin" in names        # own (None-tenant) scope still listed

    def test_admin_creates_agent_but_not_admin(self, client, superadmin, admin):
        assert _create(client, **ADMIN_AUTH).status_code == 200
        assert Profile.objects.get(user__username="agent1").role == "agent"
        r = _create(client, username="admin2", role="admin", **ADMIN_AUTH)
        assert r.status_code == 403
        # admins may create agent-level users (agent + custom roles) but never
        # admin/superadmin
        assert "agent-level" in r.json()["detail"]

    def test_admin_manages_own_agent(self, client, superadmin, admin):
        _create(client, **ADMIN_AUTH)
        uid = User.objects.get(username="agent1").pk
        assert _post(client, f"/api/auth/users/{uid}",
                     {"password": "brand-new-pw-1"}, **ADMIN_AUTH).status_code == 200
        assert client.delete(f"/api/auth/users/{uid}", **ADMIN_AUTH).status_code == 200

    def test_admin_cannot_change_roles(self, client, superadmin, admin):
        _create(client, **ADMIN_AUTH)
        uid = User.objects.get(username="agent1").pk
        r = _post(client, f"/api/auth/users/{uid}", {"role": "admin"}, **ADMIN_AUTH)
        assert r.status_code == 403

    def test_admin_cannot_manage_other_tenant_agent(self, client, superadmin, admin):
        from ingest.models import Tenant
        other = Tenant.objects.create(name="Other", slug="other")
        u = User.objects.create_user(username="faraway", password="x-pass-123")
        Profile.objects.create(user=u, role="agent", tenant=other)
        r = _post(client, f"/api/auth/users/{u.pk}", {"name": "X"}, **ADMIN_AUTH)
        assert r.status_code == 403
        assert "outside your tenant" in r.json()["detail"]


@pytest.mark.django_db
class TestListUsers:
    def test_list_ordered_payload(self, client, superadmin, admin):
        r = client.get("/api/auth/users", **SUPER_AUTH)
        assert r.status_code == 200
        users = r.json()["users"]
        assert [u["username"] for u in users] == ["admin", "super"]  # ordered by username
        me = next(u for u in users if u["username"] == "super")
        assert me["name"] == "Super Admin"
        assert me["role"] == "superadmin"
        assert me["active"] is True
        assert set(me) == {"id", "username", "name", "role", "active", "created",
                           "last_login", "tenant", "brands"}


@pytest.mark.django_db
class TestCreateUser:
    def test_create_happy_path(self, client, superadmin):
        r = _create(client, **SUPER_AUTH)
        assert r.status_code == 200
        payload = r.json()
        assert payload["username"] == "agent1"
        assert payload["name"] == "Agent One"
        assert payload["role"] == "agent"
        assert payload["active"] is True
        # user can actually log in with the set password
        r = _post(client, "/api/auth/login",
                  {"username": "agent1", "password": "collect-pass-1"})
        assert r.status_code == 200
        assert r.json()["user"]["role"] == "agent"
        assert AuditLog.objects.filter(action="user.create", target="agent1").exists()

    def test_duplicate_username_400(self, client, superadmin):
        assert _create(client, username="agent1", **SUPER_AUTH).status_code == 200
        r = _create(client, username="Agent1", **SUPER_AUTH)  # case-insensitive
        assert r.status_code == 400
        assert "taken" in r.json()["detail"]

    def test_short_password_400(self, client, superadmin):
        r = _create(client, password="short", **SUPER_AUTH)
        assert r.status_code == 400
        assert "8 characters" in r.json()["detail"]

    def test_bad_role_400(self, client, superadmin):
        assert _create(client, role="root", **SUPER_AUTH).status_code == 400

    def test_missing_username_400(self, client, superadmin):
        assert _create(client, username="  ", **SUPER_AUTH).status_code == 400


@pytest.mark.django_db
class TestUpdateUser:
    def test_update_name_and_role(self, client, superadmin, admin):
        r = _post(client, f"/api/auth/users/{admin.pk}",
                  {"name": "Promoted Person", "role": "superadmin"}, **SUPER_AUTH)
        assert r.status_code == 200
        payload = r.json()
        assert payload["name"] == "Promoted Person"
        assert payload["role"] == "superadmin"
        entry = AuditLog.objects.filter(action="user.update", target="admin").first()
        assert entry is not None
        assert "name" in entry.detail and "role" in entry.detail
        assert "password" not in entry.detail

    def test_unknown_user_404(self, client, superadmin):
        assert _post(client, "/api/auth/users/99999", {"name": "X"}, **SUPER_AUTH).status_code == 404

    def test_password_change_clears_token(self, client, superadmin, admin):
        # admin holds a live session token; superadmin resets their password
        assert client.get("/api/auth/me", **ADMIN_AUTH).status_code == 200
        r = _post(client, f"/api/auth/users/{admin.pk}",
                  {"password": "new-password-9"}, **SUPER_AUTH)
        assert r.status_code == 200
        admin.profile.refresh_from_db()
        assert admin.profile.token == ""
        # the old token no longer authenticates anything
        assert client.get("/api/auth/me", **ADMIN_AUTH).status_code == 401
        # and the new password works
        r = _post(client, "/api/auth/login", {"username": "admin", "password": "new-password-9"})
        assert r.status_code == 200

    def test_short_password_on_update_400(self, client, superadmin, admin):
        r = _post(client, f"/api/auth/users/{admin.pk}", {"password": "short"}, **SUPER_AUTH)
        assert r.status_code == 400


@pytest.mark.django_db
class TestLastSuperadminGuard:
    def test_demote_last_superadmin_blocked(self, client, superadmin):
        r = _post(client, f"/api/auth/users/{superadmin.pk}", {"role": "admin"}, **SUPER_AUTH)
        assert r.status_code == 400
        assert "super admin" in r.json()["detail"].lower()
        superadmin.profile.refresh_from_db()
        assert superadmin.profile.role == "superadmin"

    def test_deactivate_last_superadmin_blocked(self, client, superadmin):
        r = client.delete(f"/api/auth/users/{superadmin.pk}", **SUPER_AUTH)
        assert r.status_code == 400
        superadmin.refresh_from_db()
        assert superadmin.is_active is True

    def test_deactivate_via_post_blocked(self, client, superadmin):
        r = _post(client, f"/api/auth/users/{superadmin.pk}", {"active": False}, **SUPER_AUTH)
        assert r.status_code == 400

    def test_demote_allowed_with_second_active_superadmin(self, client, superadmin):
        other = User.objects.create_user(username="super2", password="super2pass")
        Profile.objects.create(user=other, role="superadmin")
        r = _post(client, f"/api/auth/users/{superadmin.pk}", {"role": "admin"}, **SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["role"] == "admin"

    def test_inactive_superadmin_does_not_count(self, client, superadmin):
        other = User.objects.create_user(username="super2", password="super2pass",
                                         is_active=False)
        Profile.objects.create(user=other, role="superadmin")
        r = client.delete(f"/api/auth/users/{superadmin.pk}", **SUPER_AUTH)
        assert r.status_code == 400  # the only OTHER superadmin is deactivated


@pytest.mark.django_db
class TestDeactivateReactivate:
    def test_deactivate_then_login_fails(self, client, superadmin, admin):
        r = client.delete(f"/api/auth/users/{admin.pk}", **SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["active"] is False
        admin.refresh_from_db()
        assert admin.is_active is False
        admin.profile.refresh_from_db()
        assert admin.profile.token == ""  # existing sessions killed
        r = _post(client, "/api/auth/login", {"username": "admin", "password": "admin123"})
        assert r.status_code == 401
        assert AuditLog.objects.filter(action="user.deactivate", target="admin").exists()

    def test_reactivate_via_post(self, client, superadmin, admin):
        client.delete(f"/api/auth/users/{admin.pk}", **SUPER_AUTH)
        r = _post(client, f"/api/auth/users/{admin.pk}", {"active": True}, **SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["active"] is True
        r = _post(client, "/api/auth/login", {"username": "admin", "password": "admin123"})
        assert r.status_code == 200


@pytest.mark.django_db
class TestBrandAccess:
    """RBAC brands on the legacy user-management API: payload exposure,
    brand_ids on create/update, tenant coherence, role-change clearing."""

    @pytest.fixture
    def brands(self, db):
        from ingest.models import Brand, Tenant
        t1 = Tenant.objects.create(name="Northwind", slug="northwind")
        t2 = Tenant.objects.create(name="Acme", slug="acme")
        return {
            "a": Brand.objects.create(tenant=t1, name="Northwind Retail", slug="nw-retail"),
            "b": Brand.objects.create(tenant=t1, name="Northwind Biz", slug="nw-biz"),
            "other": Brand.objects.create(tenant=t2, name="Acme Services", slug="acme-services"),
        }

    def _create_with_brands(self, client, brand_ids, role="agent", username="c1"):
        return _post(client, "/api/auth/users",
                     {"username": username, "name": "C", "password": "collect-pass-1",
                      "role": role, "brand_ids": brand_ids}, **SUPER_AUTH)

    def test_create_agent_with_brands(self, client, superadmin, brands):
        r = self._create_with_brands(client, [brands["a"].pk, brands["b"].pk])
        assert r.status_code == 200
        data = r.json()
        assert data["tenant"] == "northwind"
        assert {b["slug"] for b in data["brands"]} == {"nw-retail", "nw-biz"}
        p = Profile.objects.get(user__username="c1")
        assert p.tenant.slug == "northwind"
        assert p.brands.count() == 2

    def test_create_rejects_mixed_tenant_brands(self, client, superadmin, brands):
        r = self._create_with_brands(client, [brands["a"].pk, brands["other"].pk])
        assert r.status_code == 400
        assert "same tenant" in r.json()["detail"]
        assert not User.objects.filter(username="c1").exists()  # nothing half-created

    def test_create_rejects_brands_on_admin(self, client, superadmin, brands):
        r = self._create_with_brands(client, [brands["a"].pk], role="admin")
        assert r.status_code == 400
        assert "agent" in r.json()["detail"]

    def test_create_rejects_unknown_brand(self, client, superadmin, brands):
        r = self._create_with_brands(client, [999999])
        assert r.status_code == 400

    def test_update_brand_access(self, client, superadmin, brands):
        self._create_with_brands(client, [brands["a"].pk])
        uid = User.objects.get(username="c1").pk
        r = _post(client, f"/api/auth/users/{uid}",
                  {"brand_ids": [brands["b"].pk]}, **SUPER_AUTH)
        assert r.status_code == 200
        assert [b["slug"] for b in r.json()["brands"]] == ["nw-biz"]

    def test_update_rejects_other_tenant_brand(self, client, superadmin, brands):
        self._create_with_brands(client, [brands["a"].pk])
        uid = User.objects.get(username="c1").pk
        r = _post(client, f"/api/auth/users/{uid}",
                  {"brand_ids": [brands["other"].pk]}, **SUPER_AUTH)
        assert r.status_code == 400
        assert "different tenant" in r.json()["detail"]

    def test_promotion_clears_brand_access(self, client, superadmin, brands):
        self._create_with_brands(client, [brands["a"].pk])
        uid = User.objects.get(username="c1").pk
        r = _post(client, f"/api/auth/users/{uid}", {"role": "admin"}, **SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["brands"] == []
        assert Profile.objects.get(user_id=uid).brands.count() == 0

    def test_list_payload_carries_brands(self, client, superadmin, brands):
        self._create_with_brands(client, [brands["a"].pk])
        rows = client.get("/api/auth/users", **SUPER_AUTH).json()["users"]
        me = next(u for u in rows if u["username"] == "c1")
        assert me["tenant"] == "northwind"
        assert me["brands"][0]["slug"] == "nw-retail"
