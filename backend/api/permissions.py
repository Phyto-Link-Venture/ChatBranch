"""RBAC permission classes for the v1 API.

Role lives on `crm.Profile.role` (superadmin / admin / agent). These map the
existing role gates (crm/views.py) onto DRF permissions.
"""
from __future__ import annotations

from rest_framework.permissions import BasePermission


def _role(request) -> str:
    user = getattr(request, "user", None)
    if user is None or not user.is_authenticated:
        return ""
    try:
        return user.profile.role
    except Exception:
        return ""


class IsAuthenticatedProfile(BasePermission):
    message = "Authentication required."

    def has_permission(self, request, view):
        return bool(_role(request))


class IsAdmin(BasePermission):
    """admin or superadmin."""
    message = "Admin role required."

    def has_permission(self, request, view):
        return _role(request) in ("admin", "superadmin")


class IsSuperadmin(BasePermission):
    message = "Super admin role required."

    def has_permission(self, request, view):
        return _role(request) == "superadmin"


class HasCapability(BasePermission):
    """Data-driven gate: allow when the caller's role holds `capability`
    (crm.permissions_store). Tenancy/ownership scoping still happens in the
    view's queryset — this only gates the action.

    Use as a factory: `permission_classes = [HasCapability.for_("users.manage")]`.
    """
    capability = ""
    message = "You do not have permission to perform this action."

    def has_permission(self, request, view):
        from crm.permissions_store import has_capability

        role = _role(request)
        if not role:
            return False
        return has_capability(role, self.capability or getattr(view, "required_capability", ""))

    @classmethod
    def for_(cls, capability: str):
        return type(f"HasCapability_{capability.replace('.', '_')}",
                    (cls,), {"capability": capability})
