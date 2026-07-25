"""Tenant scoping for the v1 DRF API.

The current tenant is resolved from the authenticated user's Profile
(`request.user.profile.tenant`). `TenantScopedMixin` filters a viewset's
queryset to that tenant; with no tenant it returns nothing (fail-closed) —
stricter than the legacy function endpoints, which stay backward-compatible.
"""
from __future__ import annotations


def current_tenant(request):
    user = getattr(request, "user", None)
    if user is None or not user.is_authenticated:
        return None
    return getattr(getattr(user, "profile", None), "tenant", None)


class TenantScopedMixin:
    """Filter `get_queryset()` to the caller's tenant. Fail-closed."""

    tenant_field = "tenant"

    def get_queryset(self):
        qs = super().get_queryset()
        tenant = current_tenant(self.request)
        if tenant is None:
            return qs.none()
        return qs.filter(**{self.tenant_field: tenant})
