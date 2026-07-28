"""RBAC helpers: token resolution, role checks, and notification fan-out.

Auth model: POST /api/auth/login returns a bearer token (rotated per login,
stored on crm.Profile). API callers send `Authorization: Bearer <token>`.

Bootstrap fallback: while NO Profile rows exist yet (fresh DB before
seed_demo), the old X-Role header is honoured so the app isn't bricked.
Once users exist, only real tokens carry a role.
"""
from __future__ import annotations

from .models import AuditLog, Notification, Profile, Setting


def user_from_request(request):
    header = request.headers.get("Authorization", "")
    if not header.startswith("Bearer "):
        return None
    token = header[len("Bearer "):].strip()
    if not token:
        return None
    profile = Profile.objects.filter(token=token).select_related("user").first()
    return profile.user if profile and profile.user.is_active else None


def role_from_request(request) -> str:
    """'superadmin' | 'admin' | 'agent' | '' (unauthenticated)."""
    user = user_from_request(request)
    if user:
        return user.profile.role
    if not Profile.objects.exists():  # pre-seed bootstrap only
        raw = request.headers.get("X-Role", "").replace("-", "").replace(" ", "").lower()
        return raw if raw in ("superadmin", "admin", "agent") else "admin"
    return ""


def requester_name(request) -> str:
    user = user_from_request(request)
    if user:
        return user.username
    return request.headers.get("X-User", "") or "admin"


def tenant_from_request(request):
    """The tenant that owns the caller's data, or None.

    Resolved from the authenticated user's Profile. Function-based views use this
    to scope querysets; DRF views read `request.user.profile.tenant` directly.
    Returns None for unauthenticated / tenant-less callers — callers decide
    whether that means "empty" or "all" (during migration we treat None as
    "no scoping" for backward-compatibility, logged as a follow-up).
    """
    user = user_from_request(request)
    if user is None:
        return None
    profile = getattr(user, "profile", None)
    return getattr(profile, "tenant", None)


NOTIF_CATEGORIES = ("messages", "account_health", "flows", "admin")


def notification_prefs() -> dict[str, bool]:
    """Global notification toggles (Settings → Notifications): one crm.Setting
    row per category, key `notif_<category>`, value "false" = muted. Missing
    rows mean enabled, and read errors fail open — a broken Settings table must
    never decide that nobody gets paged."""
    prefs = {c: True for c in NOTIF_CATEGORIES}
    try:
        keys = [f"notif_{c}" for c in NOTIF_CATEGORIES]
        for row in Setting.objects.filter(entity__isnull=True, key__in=keys):
            prefs[row.key[len("notif_"):]] = row.value.strip().lower() != "false"
    except Exception:
        pass
    return prefs


def notify(roles: tuple[str, ...], kind: str, title: str, body: str = "",
           link: str = "", category: str = "") -> None:
    """Create a notification for every active user holding one of `roles`.

    `category` is the Settings → Notifications mute group; unset, it derives
    from kind (message → "messages", everything else → "admin")."""
    group = category or ("messages" if kind == "message" else "admin")
    if not notification_prefs().get(group, True):
        return
    profiles = Profile.objects.filter(role__in=roles, user__is_active=True).select_related("user")
    Notification.objects.bulk_create([
        Notification(user=p.user, kind=kind, title=title[:160], body=body[:500], link=link[:200])
        for p in profiles
    ])


def audit(request, action: str, target: str = "", detail: str = "") -> None:
    """Write one audit-trail entry. Pass request=None for system/webhook events."""
    user = user_from_request(request) if request is not None else None
    ip = ""
    actor = "system"
    if request is not None:
        ip = request.META.get("REMOTE_ADDR", "")
        actor = user.username if user else "anonymous"
    AuditLog.objects.create(
        user=user, actor=actor, action=action[:64],
        target=str(target)[:190], detail=str(detail)[:1000], ip=ip,
    )
