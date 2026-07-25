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
