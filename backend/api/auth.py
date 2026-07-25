"""DRF authentication that reuses the existing bearer-token scheme.

The app already issues per-login tokens stored on `crm.Profile.token`
(see crm/auth.py). This wraps that as a DRF authentication class so the new
`/api/v1` DRF endpoints accept the same `Authorization: Bearer <token>` the
dashboard already sends — no second auth system.
"""
from __future__ import annotations

from rest_framework import authentication, exceptions

from crm.models import Profile


class ProfileTokenAuthentication(authentication.BaseAuthentication):
    keyword = "Bearer"

    def authenticate(self, request):
        header = authentication.get_authorization_header(request).decode("latin1")
        if not header:
            return None
        parts = header.split()
        if parts[0] != self.keyword:
            return None
        if len(parts) == 1:
            raise exceptions.AuthenticationFailed("Invalid token header: no credentials.")
        if len(parts) > 2:
            raise exceptions.AuthenticationFailed("Invalid token header: token has spaces.")

        token = parts[1].strip()
        profile = (
            Profile.objects.filter(token=token)
            .select_related("user")
            .first()
        )
        if profile is None or not profile.user.is_active:
            raise exceptions.AuthenticationFailed("Invalid or expired token.")
        # DRF sets request.user / request.auth from this tuple.
        return (profile.user, token)

    def authenticate_header(self, request):
        return self.keyword
