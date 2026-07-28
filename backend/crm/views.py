"""Auth + RBAC API (under /api/auth/):

  POST login {username,password}   -> {token, user:{username,name,role}}
  GET  me                          -> current user (valid token check)
  POST logout                      -> invalidates the token
  GET  notifications               -> caller's latest notifications + unread count
  POST notifications/read {id|all} -> mark read
  GET  notification-prefs          -> global bell category toggles (any role)
  PUT  notification-prefs          -> change toggles (admin+)
  GET  audit                       -> audit trail (superadmin: all; admin: own)
  GET  users                       -> all users (SUPER ADMIN only)
  POST users {username,name,password,role}          -> create (SUPER ADMIN only)
  POST users/<id> {name?,role?,active?,password?}   -> update (SUPER ADMIN only)
  DELETE users/<id>                -> soft-deactivate (SUPER ADMIN only)

Tokens rotate on every login and live on crm.Profile.
"""
import json
import secrets

from django.contrib.auth import authenticate
from django.contrib.auth.models import User
from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from . import auth as rbac
from .models import AuditLog, Notification, Profile, Setting

ROLES = tuple(r for r, _ in Profile.ROLE_CHOICES)  # superadmin / admin / agent


def _json_body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except ValueError:
        return {}


def _user_payload(user) -> dict:
    from .permissions_store import capabilities_for

    return {
        "username": user.username,
        "name": user.first_name or user.username,
        "role": user.profile.role,
        # The frontend gates on capabilities (not role); populated at login +
        # whoami so an older session with only `role` can still fall back.
        "capabilities": capabilities_for(user.profile.role),
    }


@csrf_exempt
def login_view(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    user = authenticate(username=body.get("username", ""), password=body.get("password", ""))
    if user is None or not user.is_active:
        rbac.audit(request, "auth.login_failed", target=body.get("username", ""))
        return JsonResponse({"detail": "Invalid username or password."}, status=401)
    profile, _ = Profile.objects.get_or_create(
        user=user, defaults={"role": "superadmin" if user.is_superuser else "admin"}
    )
    profile.token = secrets.token_hex(24)  # rotate per login
    profile.save(update_fields=["token"])
    rbac.audit(request, "auth.login", target=user.username)
    return JsonResponse({"token": profile.token, "user": _user_payload(user)})


def me(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    user = rbac.user_from_request(request)
    if user is None:
        return JsonResponse({"detail": "Not authenticated."}, status=401)
    return JsonResponse({"user": _user_payload(user)})


@csrf_exempt
def logout_view(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    user = rbac.user_from_request(request)
    if user:
        Profile.objects.filter(user=user).update(token="")
        rbac.audit(request, "auth.logout", target=user.username)
    return JsonResponse({"ok": True})


@csrf_exempt
def notifications(request):
    user = rbac.user_from_request(request)
    if user is None:
        return JsonResponse({"detail": "Not authenticated."}, status=401)
    if request.method == "GET":
        rows = list(user.notifications.all()[:20])
        return JsonResponse({
            "unread": user.notifications.filter(read=False).count(),
            "notifications": [
                {"id": n.pk, "kind": n.kind, "title": n.title, "body": n.body,
                 "link": n.link, "read": n.read, "created_at": n.created_at.isoformat()}
                for n in rows
            ],
        })
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    body = _json_body(request)
    qs = user.notifications.filter(read=False)
    if body.get("id") is not None and body.get("id") != "all":
        qs = qs.filter(pk=body["id"])
    updated = qs.update(read=True)
    return JsonResponse({"ok": True, "marked": updated})


@csrf_exempt
def notification_prefs(request):
    """GET/PUT /api/auth/notification-prefs — the global mute switches for the
    bell fan-out categories (rbac.notify). They apply to every user's bell.
    GET: any authenticated role. PUT: admin+ (matches routing-rules gating)."""
    role = rbac.role_from_request(request)
    if not role:
        return JsonResponse({"detail": "Not authenticated."}, status=401)
    if request.method == "GET":
        return JsonResponse(rbac.notification_prefs())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    from .permissions_store import has_capability
    if not has_capability(role, "notifications.manage"):
        return JsonResponse({"detail": "Admin role required to change notification settings."},
                            status=403)
    body = _json_body(request)
    patch: dict[str, bool] = {}
    for cat in rbac.NOTIF_CATEGORIES:
        if cat in body:
            if not isinstance(body[cat], bool):
                return JsonResponse({"detail": f"{cat} must be true or false."}, status=400)
            patch[cat] = body[cat]
    for cat, enabled in patch.items():
        Setting.objects.update_or_create(key=f"notif_{cat}",
                                         defaults={"value": "true" if enabled else "false"})
    if patch:
        rbac.audit(request, "settings.update", target="notifications",
                   detail=f"keys: {sorted(patch)}")
    return JsonResponse(rbac.notification_prefs())


def audit_log(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    user = rbac.user_from_request(request)
    role = rbac.role_from_request(request)
    if not role or role == "agent":
        return JsonResponse({"detail": "Admin role required."}, status=403)
    qs = AuditLog.objects.all()
    if role != "superadmin" and user is not None:
        qs = qs.filter(user=user)  # admins see their own trail
    return JsonResponse({
        "entries": [
            {"id": e.pk, "actor": e.actor, "action": e.action, "target": e.target,
             "detail": e.detail, "ip": e.ip, "created_at": e.created_at.isoformat()}
            for e in qs[:50]
        ],
    })


@csrf_exempt
def permissions(request):
    """GET/PUT /api/auth/permissions — the data-driven RBAC matrix.

    GET (admin+): the capability catalog, the role list and the effective
    matrix (stored rows overlaid on code defaults).
    PUT (requires the `rbac.manage` capability — superadmin by default):
    validates a `{matrix: {role: {capability: bool}}}` body against the
    catalog, refuses to unset superadmin's rbac.manage, applies it, audits
    `rbac.update` and notifies.
    """
    from .permissions_store import (
        apply_permission_updates, catalog_payload, effective_matrix,
        has_capability, ordered_role_slugs, PermissionValidationError,
    )

    role = rbac.role_from_request(request)
    if not role:
        return JsonResponse({"detail": "Authentication required."}, status=401)

    def _snapshot() -> dict:
        # roles list is dynamic (all Role.slugs, system first) so the matrix UI
        # renders custom roles alongside the built-ins.
        return {"catalog": catalog_payload(), "roles": ordered_role_slugs(),
                "matrix": effective_matrix()}

    if request.method == "GET":
        if role not in ("admin", "superadmin"):
            return JsonResponse({"detail": "Admin role required."}, status=403)
        return JsonResponse(_snapshot())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not has_capability(role, "rbac.manage"):
        return JsonResponse(
            {"detail": "The rbac.manage capability is required to edit permissions."},
            status=403)
    body = _json_body(request)
    matrix = body.get("matrix") if isinstance(body.get("matrix"), dict) else body
    try:
        changes = apply_permission_updates(matrix)
    except PermissionValidationError as e:
        return JsonResponse({"detail": str(e)}, status=400)
    if changes:
        rbac.audit(request, "rbac.update", target="permissions",
                   detail=f"{len(changes)} capability toggle(s)")
        rbac.notify(("superadmin",), "system", "Permissions updated",
                    f"{len(changes)} capability change(s) by {rbac.requester_name(request)}",
                    "/permissions", category="admin")
    return JsonResponse(_snapshot())


@csrf_exempt
def roles(request):
    """GET/POST /api/auth/roles — custom roles / auth groups.

    GET (admin+): every role with its user count, system built-ins first:
    `{roles: [{slug, label, is_system, user_count}]}`.
    POST (requires `rbac.manage`): `{label}` → slugifies a new CUSTOM role,
    seeds all-false RolePermission rows (no auto-pass), audits `role.create`.
    Rejects a blank label, a duplicate slug, or a built-in collision.
    """
    from .permissions_store import (
        RoleError, create_role, has_capability, roles_payload,
    )

    role = rbac.role_from_request(request)
    if not role:
        return JsonResponse({"detail": "Authentication required."}, status=401)

    if request.method == "GET":
        if role not in ("admin", "superadmin"):
            return JsonResponse({"detail": "Admin role required."}, status=403)
        return JsonResponse({"roles": roles_payload()})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not has_capability(role, "rbac.manage"):
        return JsonResponse(
            {"detail": "The rbac.manage capability is required to manage roles."},
            status=403)
    body = _json_body(request)
    try:
        new_role = create_role(body.get("label"))
    except RoleError as e:
        return JsonResponse({"detail": e.message, **e.extra}, status=e.status)
    rbac.audit(request, "role.create", target=new_role.slug,
               detail=f"label: {new_role.label}")
    rbac.notify(("superadmin",), "system", f"Role created: {new_role.label}",
                f"By {rbac.requester_name(request)}", "/permissions", category="admin")
    return JsonResponse({"role": {"slug": new_role.slug, "label": new_role.label,
                                  "is_system": new_role.is_system, "user_count": 0}},
                        status=201)


@csrf_exempt
def role_detail(request, slug: str):
    """PATCH/DELETE /api/auth/roles/<slug> — rename or delete a CUSTOM role
    (both require `rbac.manage`).

    PATCH `{label}` renames the role's display label (the slug stays fixed —
    it's the identity Profile.role/RolePermission point at). 404 unknown, 400 a
    built-in or blank label, 409 a duplicate label/slug.
    DELETE removes it: 404 unknown, 400 built-in, 409 (+ user_count) while any
    user still holds it. Both audit + notify.
    """
    from .permissions_store import (
        RoleError, delete_role, has_capability, rename_role,
    )

    role = rbac.role_from_request(request)
    if not role:
        return JsonResponse({"detail": "Authentication required."}, status=401)
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])
    if not has_capability(role, "rbac.manage"):
        return JsonResponse(
            {"detail": "The rbac.manage capability is required to manage roles."},
            status=403)

    if request.method == "PATCH":
        body = _json_body(request)
        try:
            renamed = rename_role(slug, body.get("label"))
        except RoleError as e:
            return JsonResponse({"detail": e.message, **e.extra}, status=e.status)
        rbac.audit(request, "role.update", target=renamed.slug,
                   detail=f"label: {renamed.label}")
        rbac.notify(("superadmin",), "system", f"Role renamed: {renamed.label}",
                    f"By {rbac.requester_name(request)}", "/permissions", category="admin")
        user_count = Profile.objects.filter(role=renamed.slug).count()
        return JsonResponse({"role": {"slug": renamed.slug, "label": renamed.label,
                                      "is_system": renamed.is_system,
                                      "user_count": user_count}})

    try:
        delete_role(slug)
    except RoleError as e:
        return JsonResponse({"detail": e.message, **e.extra}, status=e.status)
    rbac.audit(request, "role.delete", target=slug)
    rbac.notify(("superadmin",), "system", f"Role deleted: {slug}",
                f"By {rbac.requester_name(request)}", "/permissions", category="admin")
    return JsonResponse({"ok": True})


# ---- user management -------------------------------------------------------
# Thin backward-compat adapters over crm.user_service (the single source of
# truth, shared with the canonical DRF endpoint /api/v1/users). These keep the
# legacy {detail} error shape + managed_user_payload; new callers should use
# /api/v1/users.

from . import user_service as usvc  # noqa: E402

_UNSET = usvc._UNSET


def _manager_gate(request):
    """Returns (actor_role, actor_profile, error_response). 401 without a role,
    403 without the `users.manage` capability.

    Data-driven now (capability, not a hardcoded role list) — but the admin ->
    agent tenant/role TIERING is unchanged: it lives in crm.user_service and
    still applies on top. Capabilities gate the action; tenancy gates the rows.
    """
    role = rbac.role_from_request(request)
    if not role:
        return "", None, JsonResponse({"detail": "Authentication required."}, status=401)
    from .permissions_store import has_capability
    if not has_capability(role, "users.manage"):
        return "", None, JsonResponse(
            {"detail": "Admin role required to manage users."}, status=403)
    user = rbac.user_from_request(request)
    try:
        profile = user.profile if user else None
    except Profile.DoesNotExist:
        profile = None
    return role, profile, None


@csrf_exempt
def users(request):
    actor_role, actor_profile, denied = _manager_gate(request)
    if denied:
        return denied
    actor_tenant_id = actor_profile.tenant_id if actor_profile else None
    if request.method == "GET":
        rows = usvc.list_users(actor_role, actor_tenant_id)
        return JsonResponse({"users": [usvc.managed_user_payload(u) for u in rows]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    body = _json_body(request)
    try:
        user = usvc.create_user(
            actor_role, actor_profile,
            username=body.get("username"), name=body.get("name"),
            password=body.get("password"), role=body.get("role"),
            entity_ids=body.get("entity_ids"), tenant=body.get("tenant"),
        )
    except usvc.UserMgmtError as e:
        return JsonResponse({"detail": e.message}, status=e.status)
    rbac.audit(request, "user.create", target=user.username, detail=f"role: {usvc.role_of(user)}")
    rbac.notify(("superadmin",), "system", f"User created: {user.username}",
                f"Role {usvc.role_of(user)} — by {rbac.requester_name(request)}",
                "/settings?tab=users")
    return JsonResponse(usvc.managed_user_payload(user))


@csrf_exempt
def user_detail(request, user_id: int):
    actor_role, actor_profile, denied = _manager_gate(request)
    if denied:
        return denied
    if request.method not in ("POST", "DELETE"):
        return HttpResponseNotAllowed(["POST", "DELETE"])
    target = User.objects.select_related("profile").filter(pk=user_id).first()
    if target is None:
        return JsonResponse({"detail": "User not found."}, status=404)

    try:
        if request.method == "DELETE":
            usvc.deactivate_user(actor_role, actor_profile, target)
            rbac.audit(request, "user.deactivate", target=target.username)
            rbac.notify(("superadmin",), "system", f"User deactivated: {target.username}",
                        f"By {rbac.requester_name(request)}", "/settings?tab=users")
            return JsonResponse(usvc.managed_user_payload(target))

        body = _json_body(request)
        target, changed = usvc.update_user(
            actor_role, actor_profile, target,
            name=body.get("name", _UNSET),
            role=body.get("role", _UNSET),
            active=body.get("active", _UNSET),
            password=body.get("password", _UNSET),
            entity_ids=body.get("entity_ids", _UNSET),
        )
    except usvc.UserMgmtError as e:
        return JsonResponse({"detail": e.message}, status=e.status)

    if changed:
        rbac.audit(request, "user.update", target=target.username,
                   detail=f"fields: {', '.join(changed)}")
        rbac.notify(("superadmin",), "system", f"User updated: {target.username}",
                    f"Changed by {rbac.requester_name(request)}: {', '.join(changed)}",
                    "/settings?tab=users")
    return JsonResponse(usvc.managed_user_payload(target))
