"""Entity-level access resolution for the v1 API.

Access rules (locked 2026-07-17):
- superadmin : everything, all tenants.
- admin      : all entities within their own tenant.
- agent      : only the entities explicitly assigned to them (Profile.entities).
"""
from __future__ import annotations

from ingest.models import Entity


def role(user) -> str:
    if user is None or not getattr(user, "is_authenticated", False):
        return ""
    return getattr(getattr(user, "profile", None), "role", "")


def user_tenant(user):
    return getattr(getattr(user, "profile", None), "tenant", None)


def accessible_entities(user):
    """Entities this user may see, as a queryset."""
    r = role(user)
    if r == "superadmin":
        return Entity.objects.all()
    tenant = user_tenant(user)
    if r == "admin":
        return Entity.objects.filter(tenant=tenant) if tenant else Entity.objects.none()
    if r == "agent":
        # agent's assigned entities, constrained to their tenant as defense
        return user.profile.entities.filter(tenant=tenant) if tenant else Entity.objects.none()
    return Entity.objects.none()


def accessible_entity_ids(user) -> list[int]:
    return list(accessible_entities(user).values_list("id", flat=True))


def can_manage_tenant(user, tenant) -> bool:
    """May this user create/manage resources in `tenant`?"""
    r = role(user)
    if r == "superadmin":
        return True
    if r == "admin":
        return tenant is not None and user_tenant(user) == tenant
    return False
