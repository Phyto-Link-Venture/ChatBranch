"""Dynamic RBAC (UPGRADE-SPEC §3): the RolePermission matrix, has_capability,
the permissions GET/PUT endpoints, whoami/login capabilities, and proof that a
refactored gate stays enforced (action gated by capability, rows still scoped by
tenancy) — plus that it is now DATA-DRIVEN (revoking a grant flips the gate).
"""
import json

import pytest
from django.contrib.auth.models import User

from crm import permissions_store as ps
from crm.models import AuditLog, Profile, RolePermission

SUPER = "perm-super-token"
ADMIN = "perm-admin-token"
AGENT = "perm-agent-token"
SUPER_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {SUPER}"}
ADMIN_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {ADMIN}"}
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT}"}


@pytest.fixture
def superadmin(db):
    u = User.objects.create_user(username="p_super", password="x")
    Profile.objects.create(user=u, role="superadmin", token=SUPER)
    return u


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="p_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="p_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=AGENT)
    return u


def _put(client, body, auth):
    return client.put("/api/auth/permissions", data=json.dumps(body),
                      content_type="application/json", **auth)


# ── seed idempotency ─────────────────────────────────────────────────────────

@pytest.mark.django_db
class TestSeed:
    def test_seed_is_idempotent(self):
        RolePermission.objects.all().delete()
        ps.ensure_default_permissions()
        first = RolePermission.objects.count()
        assert first == len(ps.ROLES) * len(ps.CAPABILITY_IDS)
        ps.ensure_default_permissions()  # again
        assert RolePermission.objects.count() == first  # no duplicates

    def test_seed_never_clobbers_an_operator_edit(self):
        RolePermission.objects.all().delete()
        ps.ensure_default_permissions()
        # operator turns OFF an admin capability
        RolePermission.objects.filter(role="admin", capability="users.manage").update(allowed=False)
        ps.ensure_default_permissions()  # re-run must not restore it
        row = RolePermission.objects.get(role="admin", capability="users.manage")
        assert row.allowed is False

    def test_seed_matches_default_matrix(self):
        RolePermission.objects.all().delete()
        ps.ensure_default_permissions()
        assert RolePermission.objects.get(role="admin", capability="rbac.manage").allowed is False
        assert RolePermission.objects.get(role="admin", capability="app_keys.manage").allowed is False
        assert RolePermission.objects.get(role="admin", capability="users.manage").allowed is True
        assert RolePermission.objects.get(role="agent", capability="contacts.view").allowed is True
        assert RolePermission.objects.get(role="agent", capability="contacts.manage").allowed is False
        assert RolePermission.objects.get(role="superadmin", capability="rbac.manage").allowed is True

    def test_seed_forces_superadmin_rbac_manage_on(self):
        RolePermission.objects.all().delete()
        ps.ensure_default_permissions()
        RolePermission.objects.filter(role="superadmin", capability="rbac.manage").update(allowed=False)
        ps.ensure_default_permissions()  # anti-lockout heals it
        assert RolePermission.objects.get(role="superadmin", capability="rbac.manage").allowed is True


# ── has_capability ───────────────────────────────────────────────────────────

@pytest.mark.django_db
class TestHasCapability:
    def test_superadmin_always_true_even_unknown_capability(self):
        assert ps.has_capability("superadmin", "users.manage") is True
        assert ps.has_capability("superadmin", "does.not.exist") is True

    def test_default_deny(self):
        RolePermission.objects.all().delete()
        assert ps.has_capability("agent", "users.manage") is False
        assert ps.has_capability("admin", "rbac.manage") is False
        assert ps.has_capability("admin", "app_keys.manage") is False

    def test_default_allow_from_matrix(self):
        RolePermission.objects.all().delete()  # no rows -> code default applies
        assert ps.has_capability("admin", "users.manage") is True
        assert ps.has_capability("agent", "contacts.view") is True

    def test_allow_after_grant(self):
        RolePermission.objects.all().delete()
        assert ps.has_capability("agent", "users.manage") is False
        RolePermission.objects.create(role="agent", capability="users.manage", allowed=True)
        assert ps.has_capability("agent", "users.manage") is True

    def test_row_can_revoke_a_default_allow(self):
        RolePermission.objects.update_or_create(
            role="admin", capability="users.manage", defaults={"allowed": False})
        assert ps.has_capability("admin", "users.manage") is False

    def test_unknown_capability_is_denied_for_non_superadmin(self):
        assert ps.has_capability("admin", "nope.nope") is False

    def test_empty_role_denied(self):
        assert ps.has_capability("", "contacts.view") is False
        assert ps.has_capability(None, "contacts.view") is False

    def test_accepts_a_user_object(self, admin, agent):
        assert ps.has_capability(admin, "users.manage") is True
        assert ps.has_capability(agent, "users.manage") is False

    def test_capabilities_for_superadmin_is_everything(self):
        assert set(ps.capabilities_for("superadmin")) == set(ps.CAPABILITY_IDS)

    def test_capabilities_for_agent_is_read_mostly(self):
        RolePermission.objects.all().delete()
        caps = set(ps.capabilities_for("agent"))
        assert "contacts.view" in caps
        assert "users.manage" not in caps
        assert "contacts.manage" not in caps


# ── GET /api/auth/permissions ────────────────────────────────────────────────

@pytest.mark.django_db
class TestPermissionsGet:
    def test_requires_auth(self, client, superadmin):
        assert client.get("/api/auth/permissions").status_code == 401

    def test_agent_forbidden(self, client, agent):
        assert client.get("/api/auth/permissions", **AGENT_AUTH).status_code == 403

    def test_admin_can_read(self, client, admin):
        r = client.get("/api/auth/permissions", **ADMIN_AUTH)
        assert r.status_code == 200
        body = r.json()
        assert body["roles"] == list(ps.ROLES)
        caps = {c["capability"] for c in body["catalog"]}
        assert caps == set(ps.CAPABILITY_IDS)
        # every catalog entry carries area + label for the matrix UI
        assert all({"capability", "area", "label"} <= set(c) for c in body["catalog"])
        assert body["matrix"]["superadmin"]["rbac.manage"] is True
        assert body["matrix"]["admin"]["rbac.manage"] is False
        assert body["matrix"]["agent"]["contacts.view"] is True

    def test_matrix_reflects_stored_edits(self, client, admin):
        RolePermission.objects.update_or_create(
            role="agent", capability="contacts.manage", defaults={"allowed": True})
        body = client.get("/api/auth/permissions", **ADMIN_AUTH).json()
        assert body["matrix"]["agent"]["contacts.manage"] is True


# ── PUT /api/auth/permissions ────────────────────────────────────────────────

@pytest.mark.django_db
class TestPermissionsPut:
    def test_requires_rbac_manage_admin_forbidden(self, client, admin):
        r = _put(client, {"matrix": {"agent": {"contacts.manage": True}}}, ADMIN_AUTH)
        assert r.status_code == 403
        assert not RolePermission.objects.filter(
            role="agent", capability="contacts.manage", allowed=True).exists()

    def test_agent_forbidden(self, client, agent):
        assert _put(client, {"matrix": {"agent": {"contacts.view": False}}},
                    AGENT_AUTH).status_code == 403

    def test_superadmin_can_edit_and_audit_written(self, client, superadmin):
        r = _put(client, {"matrix": {"agent": {"contacts.manage": True}}}, SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["matrix"]["agent"]["contacts.manage"] is True
        assert ps.has_capability("agent", "contacts.manage") is True
        assert AuditLog.objects.filter(action="rbac.update", target="permissions").exists()

    def test_admin_granted_rbac_manage_can_then_edit(self, client, admin, superadmin):
        # data-driven: superadmin grants admin rbac.manage, admin can then PUT
        _put(client, {"matrix": {"admin": {"rbac.manage": True}}}, SUPER_AUTH)
        r = _put(client, {"matrix": {"agent": {"flows.manage": True}}}, ADMIN_AUTH)
        assert r.status_code == 200
        assert ps.has_capability("agent", "flows.manage") is True

    def test_rejects_unknown_capability(self, client, superadmin):
        r = _put(client, {"matrix": {"agent": {"teleport": True}}}, SUPER_AUTH)
        assert r.status_code == 400
        assert "capability" in r.json()["detail"].lower()

    def test_rejects_unknown_role(self, client, superadmin):
        r = _put(client, {"matrix": {"wizard": {"contacts.view": True}}}, SUPER_AUTH)
        assert r.status_code == 400
        assert "role" in r.json()["detail"].lower()

    def test_rejects_non_bool(self, client, superadmin):
        r = _put(client, {"matrix": {"agent": {"contacts.view": "yes"}}}, SUPER_AUTH)
        assert r.status_code == 400

    def test_anti_lockout_refuses_superadmin_rbac_manage_off(self, client, superadmin):
        r = _put(client, {"matrix": {"superadmin": {"rbac.manage": False}}}, SUPER_AUTH)
        assert r.status_code == 400
        assert "rbac.manage cannot be turned off" in r.json()["detail"]
        # superadmin keeps the capability regardless
        assert ps.has_capability("superadmin", "rbac.manage") is True


# ── whoami / login expose capabilities ───────────────────────────────────────

@pytest.mark.django_db
class TestCapabilitiesExposed:
    def test_v1_whoami_lists_capabilities(self, client, admin):
        r = client.get("/api/v1/whoami", **ADMIN_AUTH)
        assert r.status_code == 200
        caps = r.json()["capabilities"]
        assert "users.manage" in caps
        assert "rbac.manage" not in caps  # admin default

    def test_auth_me_lists_capabilities(self, client, agent):
        r = client.get("/api/auth/me", **AGENT_AUTH)
        assert r.status_code == 200
        caps = r.json()["user"]["capabilities"]
        assert "contacts.view" in caps
        assert "users.manage" not in caps

    def test_login_returns_capabilities(self, client, db):
        u = User.objects.create_user(username="login_super", password="pw-123456")
        Profile.objects.create(user=u, role="superadmin")
        r = client.post("/api/auth/login",
                        data=json.dumps({"username": "login_super", "password": "pw-123456"}),
                        content_type="application/json")
        assert r.status_code == 200
        caps = r.json()["user"]["capabilities"]
        assert set(caps) == set(ps.CAPABILITY_IDS)  # superadmin holds all


# ── a refactored gate still enforces (action + tenancy) ──────────────────────

@pytest.mark.django_db
class TestRefactoredGateStillEnforces:
    """The users-management gate is now data-driven, but: the ACTION is gated by
    users.manage, and the ROWS are still tenancy-scoped by user_service."""

    def _mk(self, username, role, token, tenant=None):
        u = User.objects.create_user(username=username, password="x")
        Profile.objects.create(user=u, role=role, token=token, tenant=tenant)
        return u

    def test_agent_denied_manage_action(self, client):
        self._mk("g_super", "superadmin", SUPER)
        self._mk("g_agent", "agent", AGENT)
        assert client.get("/api/auth/users", **AGENT_AUTH).status_code == 403

    def test_admin_allowed_manage_action(self, client):
        self._mk("g_super", "superadmin", SUPER)
        self._mk("g_admin", "admin", ADMIN)
        assert client.get("/api/auth/users", **ADMIN_AUTH).status_code == 200

    def test_tenancy_still_scopes_rows(self, client):
        from ingest.models import Tenant
        t1 = Tenant.objects.create(name="T1", slug="t1")
        t2 = Tenant.objects.create(name="T2", slug="t2")
        self._mk("g_super", "superadmin", SUPER)
        self._mk("g_admin", "admin", ADMIN, tenant=t1)
        self._mk("g_faraway", "agent", "tok-far", tenant=t2)
        names = [u["username"] for u in
                 client.get("/api/auth/users", **ADMIN_AUTH).json()["users"]]
        assert "g_faraway" not in names   # other tenant's rows stay hidden
        assert "g_admin" in names

    def test_revoking_the_capability_flips_the_gate(self, client):
        """Proof it's DATA-DRIVEN, not hardcoded: revoke admin.users.manage and
        the same admin is now denied — no code change, no restart."""
        self._mk("g_super", "superadmin", SUPER)
        self._mk("g_admin", "admin", ADMIN)
        assert client.get("/api/auth/users", **ADMIN_AUTH).status_code == 200
        RolePermission.objects.update_or_create(
            role="admin", capability="users.manage", defaults={"allowed": False})
        assert client.get("/api/auth/users", **ADMIN_AUTH).status_code == 403
