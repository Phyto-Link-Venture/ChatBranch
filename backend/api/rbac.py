"""Brand-level access resolution for the v1 API.

Access rules (locked 2026-07-17):
- superadmin : everything, all tenants.
- admin      : all brands within their own tenant.
- agent      : only the brands explicitly assigned to them (Profile.brands).
"""
from __future__ import annotations

from ingest.models import Brand


def role(user) -> str:
    if user is None or not getattr(user, "is_authenticated", False):
        return ""
    return getattr(getattr(user, "profile", None), "role", "")


def user_tenant(user):
    return getattr(getattr(user, "profile", None), "tenant", None)


def accessible_brands(user):
    """Brands this user may see, as a queryset."""
    r = role(user)
    if r == "superadmin":
        return Brand.objects.all()
    tenant = user_tenant(user)
    if r == "admin":
        return Brand.objects.filter(tenant=tenant) if tenant else Brand.objects.none()
    if r == "agent":
        # agent's assigned brands, constrained to their tenant as defense
        return user.profile.brands.filter(tenant=tenant) if tenant else Brand.objects.none()
    return Brand.objects.none()


def accessible_brand_ids(user) -> list[int]:
    return list(accessible_brands(user).values_list("id", flat=True))


def can_manage_tenant(user, tenant) -> bool:
    """May this user create/manage resources in `tenant`?"""
    r = role(user)
    if r == "superadmin":
        return True
    if r == "admin":
        return tenant is not None and user_tenant(user) == tenant
    return False
