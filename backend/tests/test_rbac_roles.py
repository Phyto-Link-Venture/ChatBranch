"""Custom roles / auth groups (UPGRADE-SPEC-2 §A).

Extends the dynamic RBAC (test_rbac_permissions.py) with operator-created roles:
the crm.Role table, /api/auth/roles GET/POST/DELETE, a custom role flowing
through has_capability with NO auto-pass, the permissions matrix + roles list
going dynamic, and the admin->agent tiering treating custom roles as
agent-level.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm import permissions_store as ps
from crm.models import AuditLog, Profile, Role, RolePermission

SUPER = "roles-super-token"
ADMIN = "roles-admin-token"
AGENT = "roles-agent-token"
SUPER_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {SUPER}"}
ADMIN_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {ADMIN}"}
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT}"}


@pytest.fixture
def tenant(db):
    from ingest.models import Tenant
    return Tenant.objects.create(name="Roles Tenant", slug="roles-tenant")


@pytest.fixture
def superadmin(db):
    u = User.objects.create_user(username="r_super", password="pw-123456")
    Profile.objects.create(user=u, role="superadmin", token=SUPER)
    return u


@pytest.fixture
def admin(db, tenant):
    u = User.objects.create_user(username="r_admin", password="pw-123456")
    Profile.objects.create(user=u, role="admin", token=ADMIN, tenant=tenant)
    return u


@pytest.fixture
def agent(db, tenant):
    u = User.objects.create_user(username="r_agent", password="pw-123456")
    Profile.objects.create(user=u, role="agent", token=AGENT, tenant=tenant)
    return u


def _post_role(client, label, auth):
    return client.post("/api/auth/roles", data=json.dumps({"label": label}),
                       content_type="application/json", **auth)


def _put_perms(client, matrix, auth):
    return client.put("/api/auth/permissions", data=json.dumps({"matrix": matrix}),
                      content_type="application/json", **auth)


# ── seed: built-in Role rows ─────────────────────────────────────────────────

@pytest.mark.django_db
class TestBuiltinRolesSeeded:
    def test_ensure_seeds_role_rows(self):
        Role.objects.all().delete()
        ps.ensure_default_permissions()
        rows = {r.slug: r for r in Role.objects.all()}
        assert set(rows) == set(ps.ROLES)
        assert all(rows[s].is_system for s in ps.ROLES)

    def test_ensure_is_idempotent_for_roles(self):
        ps.ensure_default_permissions()
        ps.ensure_default_permissions()
        assert Role.objects.filter(slug__in=ps.ROLES).count() == len(ps.ROLES)

    def test_builtins_forced_system(self):
        ps.ensure_default_permissions()
        Role.objects.filter(slug="agent").update(is_system=False)
        ps.ensure_default_permissions()  # heals it
        assert Role.objects.get(slug="agent").is_system is True


# ── create_role / delete_role service ────────────────────────────────────────

@pytest.mark.django_db
class TestRoleService:
    def test_create_slugifies_and_seeds_all_false(self):
        role = ps.create_role("Team Lead")
        assert role.slug == "team-lead"
        assert role.is_system is False
        caps = RolePermission.objects.filter(role="team-lead")
        assert caps.count() == len(ps.CAPABILITY_IDS)
        assert not caps.filter(allowed=True).exists()  # zero caps until granted

    def test_create_rejects_blank(self):
        with pytest.raises(ps.RoleError):
            ps.create_role("   ")

    def test_create_rejects_duplicate(self):
        ps.create_role("Team Lead")
        with pytest.raises(ps.RoleError) as e:
            ps.create_role("team lead")  # same slug
        assert e.value.status == 409

    def test_create_rejects_system_collision(self):
        with pytest.raises(ps.RoleError) as e:
            ps.create_role("Admin")  # slug "admin" collides with a built-in
        assert e.value.status == 409

    def test_delete_removes_role_and_permissions(self):
        ps.create_role("Team Lead")
        ps.delete_role("team-lead")
        assert not Role.objects.filter(slug="team-lead").exists()
        assert not RolePermission.objects.filter(role="team-lead").exists()

    def test_delete_unknown_is_404(self):
        with pytest.raises(ps.RoleError) as e:
            ps.delete_role("nope")
        assert e.value.status == 404

    def test_delete_system_refused(self):
        ps.ensure_default_permissions()
        with pytest.raises(ps.RoleError) as e:
            ps.delete_role("agent")
        assert e.value.status == 400
        assert Role.objects.filter(slug="agent").exists()

    def test_delete_blocked_when_assigned(self, db):
        ps.create_role("Team Lead")
        u = User.objects.create_user(username="holder", password="pw-123456")
        Profile.objects.create(user=u, role="team-lead")
        with pytest.raises(ps.RoleError) as e:
            ps.delete_role("team-lead")
        assert e.value.status == 409
        assert e.value.extra["user_count"] == 1


# ── has_capability for a custom role (NO auto-pass) ──────────────────────────

@pytest.mark.django_db
class TestCustomRoleCapabilities:
    def test_new_role_has_zero_caps(self):
        ps.create_role("Team Lead")
        assert ps.capabilities_for("team-lead") == []
        for cap in ps.CAPABILITY_IDS:
            assert ps.has_capability("team-lead", cap) is False

    def test_no_auto_pass_even_for_unknown_capability(self):
        ps.create_role("Team Lead")
        # unlike superadmin, a custom role never auto-passes
        assert ps.has_capability("team-lead", "does.not.exist") is False

    def test_grant_flips_the_gate(self):
        ps.create_role("Team Lead")
        assert ps.has_capability("team-lead", "contacts.view") is False
        RolePermission.objects.filter(
            role="team-lead", capability="contacts.view").update(allowed=True)
        assert ps.has_capability("team-lead", "contacts.view") is True
        assert ps.capabilities_for("team-lead") == ["contacts.view"]


# ── /api/auth/roles endpoints ────────────────────────────────────────────────

@pytest.mark.django_db
class TestRolesEndpoint:
    def test_get_requires_auth(self, client, superadmin):
        # superadmin fixture ensures a Profile row exists, so the pre-seed
        # X-Role bootstrap fallback is off and a token is genuinely required.
        assert client.get("/api/auth/roles").status_code == 401

    def test_get_agent_forbidden(self, client, agent):
        assert client.get("/api/auth/roles", **AGENT_AUTH).status_code == 403

    def test_get_lists_system_first_with_counts(self, client, superadmin, admin, agent):
        ps.create_role("Team Lead")
        body = client.get("/api/auth/roles", **ADMIN_AUTH).json()
        slugs = [r["slug"] for r in body["roles"]]
        assert slugs[:3] == ["superadmin", "admin", "agent"]  # system first
        assert "team-lead" in slugs
        by_slug = {r["slug"]: r for r in body["roles"]}
        assert by_slug["superadmin"]["is_system"] is True
        assert by_slug["team-lead"]["is_system"] is False
        assert by_slug["superadmin"]["user_count"] == 1
        assert by_slug["admin"]["user_count"] == 1
        assert by_slug["team-lead"]["user_count"] == 0

    def test_post_requires_rbac_manage_admin_forbidden(self, client, admin):
        r = _post_role(client, "Team Lead", ADMIN_AUTH)
        assert r.status_code == 403
        assert not Role.objects.filter(slug="team-lead").exists()

    def test_post_creates_and_audits(self, client, superadmin):
        r = _post_role(client, "Team Lead", SUPER_AUTH)
        assert r.status_code == 201
        assert r.json()["role"]["slug"] == "team-lead"
        assert Role.objects.filter(slug="team-lead", is_system=False).exists()
        assert AuditLog.objects.filter(action="role.create", target="team-lead").exists()

    def test_post_rejects_duplicate(self, client, superadmin):
        _post_role(client, "Team Lead", SUPER_AUTH)
        assert _post_role(client, "team lead", SUPER_AUTH).status_code == 409

    def test_post_rejects_system_collision(self, client, superadmin):
        assert _post_role(client, "Agent", SUPER_AUTH).status_code == 409

    def test_post_rejects_blank(self, client, superadmin):
        assert _post_role(client, "   ", SUPER_AUTH).status_code == 400

    def test_delete_requires_rbac_manage_admin_forbidden(self, client, superadmin, admin):
        ps.create_role("Team Lead")
        assert client.delete("/api/auth/roles/team-lead", **ADMIN_AUTH).status_code == 403
        assert Role.objects.filter(slug="team-lead").exists()

    def test_delete_custom_ok(self, client, superadmin):
        ps.create_role("Team Lead")
        r = client.delete("/api/auth/roles/team-lead", **SUPER_AUTH)
        assert r.status_code == 200
        assert not Role.objects.filter(slug="team-lead").exists()
        assert AuditLog.objects.filter(action="role.delete", target="team-lead").exists()

    def test_delete_system_refused(self, client, superadmin):
        assert client.delete("/api/auth/roles/agent", **SUPER_AUTH).status_code == 400

    def test_delete_blocked_when_assigned(self, client, superadmin):
        ps.create_role("Team Lead")
        u = User.objects.create_user(username="holder", password="pw-123456")
        Profile.objects.create(user=u, role="team-lead")
        r = client.delete("/api/auth/roles/team-lead", **SUPER_AUTH)
        assert r.status_code == 409
        assert r.json()["user_count"] == 1
        assert Role.objects.filter(slug="team-lead").exists()  # not deleted


# ── PATCH /api/auth/roles/<slug> — rename (custom-only) ──────────────────────

def _patch_role(client, slug, label, auth):
    return client.patch(f"/api/auth/roles/{slug}", data=json.dumps({"label": label}),
                        content_type="application/json", **auth)


@pytest.mark.django_db
class TestRoleRenameService:
    def test_rename_updates_label_keeps_slug(self):
        ps.create_role("Team Lead")
        role = ps.rename_role("team-lead", "Squad Lead")
        assert role.slug == "team-lead"  # identity immutable
        assert role.label == "Squad Lead"
        assert Role.objects.get(slug="team-lead").label == "Squad Lead"

    def test_rename_unknown_is_404(self):
        with pytest.raises(ps.RoleError) as e:
            ps.rename_role("nope", "X")
        assert e.value.status == 404

    def test_rename_system_refused_400(self):
        ps.ensure_default_permissions()
        with pytest.raises(ps.RoleError) as e:
            ps.rename_role("agent", "Collector")
        assert e.value.status == 400

    def test_rename_blank_400(self):
        ps.create_role("Team Lead")
        with pytest.raises(ps.RoleError) as e:
            ps.rename_role("team-lead", "   ")
        assert e.value.status == 400

    def test_rename_duplicate_label_409(self):
        ps.create_role("Team Lead")
        ps.create_role("Floor Manager")
        with pytest.raises(ps.RoleError) as e:
            ps.rename_role("floor-manager", "team lead")  # collides with Team Lead
        assert e.value.status == 409


@pytest.mark.django_db
class TestRoleRenameEndpoint:
    def test_patch_renames_and_audits(self, client, superadmin):
        ps.create_role("Team Lead")
        r = _patch_role(client, "team-lead", "Squad Lead", SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["role"]["label"] == "Squad Lead"
        assert r.json()["role"]["slug"] == "team-lead"
        assert Role.objects.get(slug="team-lead").label == "Squad Lead"
        assert AuditLog.objects.filter(action="role.update", target="team-lead").exists()

    def test_patch_requires_rbac_manage_admin_forbidden(self, client, superadmin, admin):
        ps.create_role("Team Lead")
        r = _patch_role(client, "team-lead", "Squad Lead", ADMIN_AUTH)
        assert r.status_code == 403
        assert Role.objects.get(slug="team-lead").label == "Team Lead"  # unchanged

    def test_patch_requires_auth(self, client, superadmin):
        ps.create_role("Team Lead")
        r = client.patch("/api/auth/roles/team-lead", data=json.dumps({"label": "X"}),
                         content_type="application/json")
        assert r.status_code == 401

    def test_patch_system_role_400(self, client, superadmin):
        assert _patch_role(client, "agent", "Collector", SUPER_AUTH).status_code == 400

    def test_patch_unknown_404(self, client, superadmin):
        assert _patch_role(client, "ghost", "X", SUPER_AUTH).status_code == 404

    def test_patch_blank_label_400(self, client, superadmin):
        ps.create_role("Team Lead")
        assert _patch_role(client, "team-lead", "   ", SUPER_AUTH).status_code == 400

    def test_patch_duplicate_label_409(self, client, superadmin):
        ps.create_role("Team Lead")
        ps.create_role("Floor Manager")
        r = _patch_role(client, "floor-manager", "Team Lead", SUPER_AUTH)
        assert r.status_code == 409
        assert Role.objects.get(slug="floor-manager").label == "Floor Manager"


# ── permissions matrix + whoami go dynamic ───────────────────────────────────

@pytest.mark.django_db
class TestPermissionsAndWhoamiDynamic:
    def test_matrix_includes_custom_role(self, client, superadmin):
        ps.create_role("Team Lead")
        body = client.get("/api/auth/permissions", **SUPER_AUTH).json()
        assert "team-lead" in body["roles"]
        assert body["roles"][-1] == "team-lead"  # custom after system
        assert set(body["matrix"]["team-lead"].values()) == {False}  # all deny

    def test_put_accepts_custom_role_and_whoami_reflects_it(self, client, superadmin):
        _post_role(client, "Team Lead", SUPER_AUTH)
        r = _put_perms(client, {"team-lead": {"contacts.view": True, "chats.view": True}},
                       SUPER_AUTH)
        assert r.status_code == 200
        assert r.json()["matrix"]["team-lead"]["contacts.view"] is True
        # a user holding the custom role sees the granted caps in whoami
        u = User.objects.create_user(username="tl_user", password="pw-123456")
        Profile.objects.create(user=u, role="team-lead", token="tl-tok")
        caps = client.get("/api/auth/me", HTTP_AUTHORIZATION="Bearer tl-tok").json()["user"]["capabilities"]
        assert "contacts.view" in caps
        assert "chats.view" in caps
        assert "users.manage" not in caps

    def test_put_rejects_unknown_role_still(self, client, superadmin):
        r = _put_perms(client, {"ghost": {"contacts.view": True}}, SUPER_AUTH)
        assert r.status_code == 400


# ── admin->agent tiering treats custom roles as agent-level ──────────────────

@pytest.mark.django_db
class TestTiering:
    def _mk_custom(self):
        ps.create_role("Team Lead")

    def test_admin_can_assign_custom_role(self, client, superadmin, admin):
        self._mk_custom()
        r = client.post("/api/auth/users", data=json.dumps({
            "username": "new_tl", "name": "TL", "password": "pw-123456",
            "role": "team-lead"}), content_type="application/json", **ADMIN_AUTH)
        assert r.status_code == 200
        created = User.objects.get(username="new_tl")
        assert created.profile.role == "team-lead"
        # a custom-role user created by an admin inherits the admin's tenant
        assert created.profile.tenant == admin.profile.tenant

    def test_admin_cannot_assign_admin_role(self, client, superadmin, admin):
        r = client.post("/api/auth/users", data=json.dumps({
            "username": "new_admin", "name": "A", "password": "pw-123456",
            "role": "admin"}), content_type="application/json", **ADMIN_AUTH)
        assert r.status_code == 403
        assert not User.objects.filter(username="new_admin").exists()

    def test_admin_cannot_assign_superadmin_role(self, client, superadmin, admin):
        r = client.post("/api/auth/users", data=json.dumps({
            "username": "new_su", "name": "S", "password": "pw-123456",
            "role": "superadmin"}), content_type="application/json", **ADMIN_AUTH)
        assert r.status_code == 403

    def test_superadmin_can_assign_anything(self, client, superadmin):
        self._mk_custom()
        for i, role in enumerate(("team-lead", "admin", "superadmin")):
            r = client.post("/api/auth/users", data=json.dumps({
                "username": f"su_made_{i}", "name": role, "password": "pw-123456",
                "role": role}), content_type="application/json", **SUPER_AUTH)
            assert r.status_code == 200, (role, r.content)
            assert User.objects.get(username=f"su_made_{i}").profile.role == role

    def test_admin_can_manage_existing_custom_role_user(self, client, superadmin, admin):
        """Admins may edit an agent-level (custom-role) user in their tenant."""
        self._mk_custom()
        u = User.objects.create_user(username="tl_member", password="pw-123456")
        Profile.objects.create(user=u, role="team-lead", tenant=admin.profile.tenant)
        r = client.post(f"/api/auth/users/{u.pk}", data=json.dumps({"name": "Renamed"}),
                        content_type="application/json", **ADMIN_AUTH)
        assert r.status_code == 200
        u.refresh_from_db()
        assert u.first_name == "Renamed"

    def test_admin_still_cannot_manage_admin_user(self, client, superadmin, admin, tenant):
        other = User.objects.create_user(username="other_admin", password="pw-123456")
        Profile.objects.create(user=other, role="admin", tenant=tenant)
        r = client.post(f"/api/auth/users/{other.pk}", data=json.dumps({"name": "X"}),
                        content_type="application/json", **ADMIN_AUTH)
        assert r.status_code == 403

    def test_reject_unknown_role_on_create(self, client, superadmin):
        r = client.post("/api/auth/users", data=json.dumps({
            "username": "bad", "name": "B", "password": "pw-123456",
            "role": "wizard"}), content_type="application/json", **SUPER_AUTH)
        assert r.status_code == 400
