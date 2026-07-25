"""Single source of truth for user management (RBAC + brands).

Both the canonical DRF endpoint (`/api/v1/users`, `api/views.py`) and the
backward-compat function views (`/api/auth/users`, `crm/views.py`) call this
service, so the tiered rules live in exactly one place:

- superadmin : manage everyone, any role, any tenant.
- admin      : manage only agents in their own tenant; may assign brands
               within that tenant; may never change roles or touch admins.
- agent      : no access.

Functions raise `UserMgmtError(code, message, status)` on any validation or
permission failure; callers translate that into their own error shape. Audit +
notification are left to the caller (they need the request); the service returns
the affected user + a `changed` list.
"""
from __future__ import annotations

from django.contrib.auth.models import User

from .models import Profile

ROLES = tuple(r for r, _ in Profile.ROLE_CHOICES)  # superadmin / admin / agent

_UNSET = object()  # distinguishes "not provided" from "set to None/empty"


class UserMgmtError(Exception):
    def __init__(self, code: str, message: str, status: int = 400):
        self.code = code
        self.message = message
        self.status = status
        super().__init__(message)


# ── helpers ──────────────────────────────────────────────────────────────────

def role_of(user) -> str:
    try:
        return user.profile.role
    except Profile.DoesNotExist:
        return "superadmin" if user.is_superuser else "admin"


def _apply_role_flags(user, role: str) -> None:
    """Keep Django-admin flags coherent with the app role (mirrors seed_demo)."""
    user.is_superuser = role == "superadmin"
    user.is_staff = role in ("superadmin", "admin")


def managed_user_payload(user) -> dict:
    try:
        profile = user.profile
    except Profile.DoesNotExist:
        profile = None
    tenant = getattr(profile, "tenant", None)
    return {
        "id": user.pk,
        "username": user.username,
        "name": user.first_name or user.username,
        "role": role_of(user),
        "active": user.is_active,
        "created": user.date_joined.isoformat() if user.date_joined else None,
        "last_login": user.last_login.isoformat() if user.last_login else None,
        "tenant": tenant.slug if tenant else None,
        "brands": ([{"id": b.pk, "name": b.name, "slug": b.slug}
                    for b in profile.brands.all()] if profile else []),
    }


def _resolve_brands(brand_ids):
    """Validate a brand_ids list. All ids must exist and share ONE tenant
    (a user works for a single tenant). Returns a list of Brand."""
    from ingest.models import Brand

    ids = []
    for raw in brand_ids or []:
        try:
            ids.append(int(raw))
        except (TypeError, ValueError):
            raise UserMgmtError("bad_request", "brand_ids must be a list of brand ids.")
    brands = list(Brand.objects.filter(pk__in=ids).select_related("tenant"))
    if len(brands) != len(set(ids)):
        raise UserMgmtError("bad_request", "One or more brands do not exist.")
    if len({b.tenant_id for b in brands}) > 1:
        raise UserMgmtError("bad_request", "All brands must belong to the same tenant.")
    return brands


def would_remove_last_superadmin(target, new_role=None, new_active=None) -> bool:
    if role_of(target) != "superadmin" or not target.is_active:
        return False
    demoted = new_role is not None and new_role != "superadmin"
    deactivated = new_active is False
    if not (demoted or deactivated):
        return False
    return not (Profile.objects.filter(role="superadmin", user__is_active=True)
                .exclude(user=target).exists())


def _assert_admin_may_manage(actor_role, actor_profile, target_profile) -> None:
    """Admins may act only on agents in their own tenant."""
    if actor_role != "admin":
        return
    if target_profile.role != "agent":
        raise UserMgmtError("forbidden", "Admins can only manage agent users.", 403)
    actor_tenant_id = actor_profile.tenant_id if actor_profile else None
    if target_profile.tenant_id != actor_tenant_id:
        raise UserMgmtError("forbidden", "That user is outside your tenant.", 403)


# ── operations ───────────────────────────────────────────────────────────────

def list_users(actor_role, actor_tenant_id):
    rows = User.objects.select_related("profile").order_by("username")
    if actor_role == "admin":
        rows = rows.filter(profile__tenant_id=actor_tenant_id)
    return rows


def create_user(actor_role, actor_profile, *, username, name, password, role,
                brand_ids=None, tenant=None) -> User:
    username = (username or "").strip()
    name = (name or "").strip()
    password = password or ""
    role = (role or "").strip()

    if not username:
        raise UserMgmtError("bad_request", "username is required.")
    if User.objects.filter(username__iexact=username).exists():
        raise UserMgmtError("bad_request", f"Username '{username}' is already taken.")
    if len(password) < 8:
        raise UserMgmtError("bad_request", "Password must be at least 8 characters.")
    if role not in ROLES:
        raise UserMgmtError("bad_request", f"role must be one of: {', '.join(ROLES)}.")
    if actor_role == "admin" and role != "agent":
        raise UserMgmtError("forbidden", "Admins can only create agents.", 403)

    brand_ids = brand_ids or []
    if brand_ids and role != "agent":
        raise UserMgmtError("bad_request", "Brand access applies to agent users only.")
    brands = _resolve_brands(brand_ids)
    actor_tenant_id = actor_profile.tenant_id if actor_profile else None
    if (actor_role == "admin" and brands and actor_tenant_id is not None
            and brands[0].tenant_id != actor_tenant_id):
        raise UserMgmtError("forbidden", "Those brands are outside your tenant.", 403)

    # Resolve the new user's tenant: brands pin it; else an admin creates within
    # their own tenant; else a superadmin may name one explicitly (slug).
    if brands:
        target_tenant = brands[0].tenant
    elif actor_role == "admin" and actor_profile:
        target_tenant = actor_profile.tenant
    elif actor_role == "superadmin" and tenant:
        from ingest.models import Tenant
        target_tenant = Tenant.objects.filter(slug=tenant).first()
        if target_tenant is None:
            raise UserMgmtError("bad_request", f"Unknown tenant '{tenant}'.")
    else:
        target_tenant = None

    user = User.objects.create_user(username=username, password=password, first_name=name)
    _apply_role_flags(user, role)
    user.save(update_fields=["is_superuser", "is_staff"])
    profile = Profile.objects.create(user=user, role=role, tenant=target_tenant)
    if brands:
        profile.brands.set(brands)
    return user


def update_user(actor_role, actor_profile, target, *, name=_UNSET, role=_UNSET,
                active=_UNSET, password=_UNSET, brand_ids=_UNSET):
    """Apply a partial update. Returns (user, changed_fields)."""
    profile, _ = Profile.objects.get_or_create(
        user=target, defaults={"role": role_of(target)})
    target.profile = profile
    _assert_admin_may_manage(actor_role, actor_profile, profile)

    new_role = None if role is _UNSET else role
    new_active = None if active is _UNSET else active

    if new_role is not None and new_role not in ROLES:
        raise UserMgmtError("bad_request", f"role must be one of: {', '.join(ROLES)}.")
    if actor_role == "admin" and new_role is not None and new_role != "agent":
        raise UserMgmtError("forbidden", "Admins cannot change user roles.", 403)
    if new_active is not None and not isinstance(new_active, bool):
        raise UserMgmtError("bad_request", "active must be true or false.")
    if password is not _UNSET and password is not None and len(str(password)) < 8:
        raise UserMgmtError("bad_request", "Password must be at least 8 characters.")
    if would_remove_last_superadmin(target, new_role=new_role, new_active=new_active):
        raise UserMgmtError("bad_request",
                            "Cannot demote or deactivate the last active super admin — "
                            "promote another super admin first.")

    new_brands = None
    if brand_ids is not _UNSET:
        if not isinstance(brand_ids, list):
            raise UserMgmtError("bad_request", "brand_ids must be a list of brand ids.")
        effective_role = new_role if new_role is not None else profile.role
        if brand_ids and effective_role != "agent":
            raise UserMgmtError("bad_request", "Brand access applies to agent users only.")
        new_brands = _resolve_brands(brand_ids)
        if (new_brands and profile.tenant_id is not None
                and profile.tenant_id != new_brands[0].tenant_id):
            raise UserMgmtError("bad_request",
                                "Those brands belong to a different tenant than this user.")

    changed = []
    if name is not _UNSET and str(name).strip() != target.first_name:
        target.first_name = str(name).strip()
        changed.append("name")
    if new_role is not None and new_role != profile.role:
        profile.role = new_role
        _apply_role_flags(target, new_role)
        if new_role != "agent":
            profile.brands.clear()
        changed.append("role")
    if brand_ids is not _UNSET:
        profile.brands.set(new_brands)
        if new_brands and profile.tenant_id is None:
            profile.tenant = new_brands[0].tenant
        changed.append("brands")
    if new_active is not None and new_active != target.is_active:
        target.is_active = new_active
        if not new_active:
            profile.token = ""
        changed.append("active")
    if password is not _UNSET and password is not None:
        target.set_password(str(password))
        profile.token = ""  # rotate: old bearer tokens die with the password
        changed.append("password")
    if changed:
        target.save()
        profile.save()
    return target, changed


def deactivate_user(actor_role, actor_profile, target) -> User:
    profile, _ = Profile.objects.get_or_create(
        user=target, defaults={"role": role_of(target)})
    target.profile = profile
    _assert_admin_may_manage(actor_role, actor_profile, profile)
    if would_remove_last_superadmin(target, new_active=False):
        raise UserMgmtError("bad_request",
                            "Cannot deactivate the last active super admin — "
                            "promote another super admin first.")
    target.is_active = False
    target.save(update_fields=["is_active"])
    profile.token = ""
    profile.save(update_fields=["token"])
    return target
