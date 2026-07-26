"""Data-driven RBAC: the capability catalog, default matrix, and the single
`has_capability` gate everything server-side consults.

Design rules (UPGRADE-SPEC §3):

* **Superadmin is always allowed, in code** — never derivable from a DB row, so
  the matrix can never lock the platform out. `rbac.manage` for superadmin is
  additionally pinned on at seed time and refused by the PUT validator.
* **Missing row → the code default matrix** (which is itself deny-by-default for
  capabilities a role isn't granted). This keeps a freshly-migrated but
  unseeded DB behaving exactly like the seeded one, so the enforcement gates
  never accidentally open or close because a seed step was skipped.
* **No module-level cache** of DB state — every check reads the current rows
  (an operator edit must take effect immediately, no restart). Callers that
  need many capabilities at once (whoami, the matrix endpoint) do ONE query.
* **Capabilities gate the ACTION; tenancy/ownership scoping gates the ROWS.**
  Refactoring a gate to `has_capability` never removes the queryset scoping
  that sits alongside it.
"""
from __future__ import annotations

# The 3 built-in roles, in canonical display order. Custom roles created by an
# operator are additive; the authoritative list of *all* roles is the crm.Role
# table (see `ordered_role_slugs`). These three are always present (seeded) and
# always ordered first.
ROLES = ("superadmin", "admin", "agent")

# Labels for the seeded built-in Role rows.
SYSTEM_ROLE_LABELS = {"superadmin": "Super admin", "admin": "Admin", "agent": "Agent"}

# The authoritative capability catalog. The frontend renders the permission
# matrix from exactly this list (capability, area, label). Keep it in sync with
# UPGRADE-SPEC §3.
CAPABILITIES: list[tuple[str, str, str]] = [
    ("contacts.view", "Contacts", "View contacts"),
    ("contacts.manage", "Contacts", "Create & edit contacts"),
    ("chats.view", "Chats", "View chats"),
    ("chats.send", "Chats", "Send messages"),
    ("chats.manage", "Chats", "Manage chats"),
    ("flows.view", "Flows", "View flows"),
    ("flows.manage", "Flows", "Build & edit flows"),
    ("channels.view", "Channels", "View channels"),
    ("channels.manage", "Channels", "Manage channel accounts"),
    ("integrations.view", "Integrations", "View integrations"),
    ("integrations.manage", "Integrations", "Manage integrations"),
    ("users.view", "Users & Roles", "View users"),
    ("users.manage", "Users & Roles", "Create & manage users"),
    ("brands.view", "Brands & Tenants", "View brands & tenants"),
    ("brands.manage", "Brands & Tenants", "Manage brands & tenants"),
    ("templates.manage", "Library", "Manage message templates"),
    ("script_templates.manage", "Library", "Manage script templates"),
    ("notifications.manage", "Notifications", "Manage notification settings"),
    ("analytics.view", "Review", "View analytics"),
    ("call_logs.view", "Review", "View call logs"),
    ("activity.view", "Review", "View activity log"),
    ("settings.ai.manage", "Settings", "Manage AI configuration"),
    ("account_assignment.manage", "Settings", "Manage account assignment"),
    ("app_keys.manage", "Settings", "Manage app keys"),
    ("pronunciation.manage", "Settings", "Manage pronunciation"),
    ("rbac.manage", "Admin", "Edit the permission matrix"),
]

CAPABILITY_IDS: tuple[str, ...] = tuple(c for c, _, _ in CAPABILITIES)

# --- default seed --------------------------------------------------------------
# admin gets everything EXCEPT these (kept superadmin-only).
_ADMIN_EXCLUDE = {"rbac.manage", "app_keys.manage"}
# agent's read-mostly work surface.
_AGENT_ALLOW = {
    "contacts.view", "chats.view", "chats.send", "flows.view",
    "analytics.view", "call_logs.view", "activity.view",
}

DEFAULT_MATRIX: dict[str, dict[str, bool]] = {
    "superadmin": {c: True for c in CAPABILITY_IDS},
    "admin": {c: (c not in _ADMIN_EXCLUDE) for c in CAPABILITY_IDS},
    "agent": {c: (c in _AGENT_ALLOW) for c in CAPABILITY_IDS},
}

# rbac.manage for superadmin is never toggleable off (anti-lockout).
LOCKED = {("superadmin", "rbac.manage")}


# --- role resolution -----------------------------------------------------------

def role_of(role_or_user) -> str:
    """Accept a role string OR a Django User/Profile-bearing object -> role str.

    Returns "" for anonymous/unresolvable callers."""
    if role_or_user is None:
        return ""
    if isinstance(role_or_user, str):
        return role_or_user.strip()
    user = role_or_user
    # An unauthenticated DRF/AnonymousUser has is_authenticated == False.
    if getattr(user, "is_authenticated", True) is False:
        return ""
    profile = getattr(user, "profile", None)
    role = getattr(profile, "role", "") or ""
    if role:
        return role
    # A Django superuser with no Profile row still counts as superadmin.
    if getattr(user, "is_superuser", False):
        return "superadmin"
    return ""


# --- the gate ------------------------------------------------------------------

def has_capability(role_or_user, capability: str) -> bool:
    """Is `capability` granted to this role/user? The one place gates consult.

    superadmin -> always True. Unknown capability -> False. A stored
    RolePermission row wins; without one, the code default applies.
    """
    role = role_of(role_or_user)
    if not role:
        return False
    if role == "superadmin":
        return True
    if capability not in CAPABILITY_IDS:
        return False
    from .models import RolePermission

    row = RolePermission.objects.filter(role=role, capability=capability).first()
    if row is not None:
        return bool(row.allowed)
    return DEFAULT_MATRIX.get(role, {}).get(capability, False)


def capabilities_for(role_or_user) -> list[str]:
    """Every capability this role/user holds — for whoami/login so the frontend
    gates on capabilities, not role. superadmin holds all of them."""
    role = role_of(role_or_user)
    if not role:
        return []
    if role == "superadmin":
        return list(CAPABILITY_IDS)
    from .models import RolePermission

    stored = {r.capability: r.allowed
              for r in RolePermission.objects.filter(role=role)}
    defaults = DEFAULT_MATRIX.get(role, {})
    return [cap for cap in CAPABILITY_IDS
            if stored.get(cap, defaults.get(cap, False))]


def ordered_role_slugs() -> list[str]:
    """Every role slug, system built-ins first (canonical superadmin/admin/agent
    order), then custom roles by creation order. The built-ins are always
    included even if a Role row is somehow missing, so the matrix never loses a
    system role."""
    from .models import Role

    rows = list(Role.objects.all())
    ordered = list(ROLES)  # built-ins first, canonical order, always present
    custom = sorted(
        (r for r in rows if not r.is_system and r.slug not in ROLES),
        key=lambda r: (r.created_at, r.pk),
    )
    ordered += [r.slug for r in custom]
    return ordered


def effective_matrix() -> dict[str, dict[str, bool]]:
    """The current matrix for every role (system first, then custom): stored row
    if present, else the code default (custom roles have no code default, so
    every capability is False until granted). superadmin's rbac.manage is always
    reported on (locked)."""
    from .models import RolePermission

    stored: dict[str, dict[str, bool]] = {}
    for r in RolePermission.objects.all():
        stored.setdefault(r.role, {})[r.capability] = r.allowed
    matrix: dict[str, dict[str, bool]] = {}
    for role in ordered_role_slugs():
        defaults = DEFAULT_MATRIX.get(role, {})
        matrix[role] = {
            cap: stored.get(role, {}).get(cap, defaults.get(cap, False))
            for cap in CAPABILITY_IDS
        }
    if "superadmin" in matrix:
        matrix["superadmin"]["rbac.manage"] = True
    return matrix


def roles_payload() -> list[dict]:
    """`GET /api/auth/roles`: each role with its user count, system first."""
    from django.db.models import Count

    from .models import Profile, Role

    rows = {r.slug: r for r in Role.objects.all()}
    counts = {row["role"]: row["n"]
              for row in Profile.objects.values("role").annotate(n=Count("id"))}
    out: list[dict] = []
    for slug in ordered_role_slugs():
        row = rows.get(slug)
        out.append({
            "slug": slug,
            "label": row.label if row else SYSTEM_ROLE_LABELS.get(slug, slug),
            "is_system": row.is_system if row else (slug in ROLES),
            "user_count": counts.get(slug, 0),
        })
    return out


def catalog_payload() -> list[dict]:
    return [{"capability": c, "area": area, "label": label}
            for c, area, label in CAPABILITIES]


# --- seeding + updates ---------------------------------------------------------

def ensure_default_permissions() -> None:
    """Idempotently seed the matrix AND the built-in Role rows. Safe on a fresh
    DB AND an existing one: get_or_create never clobbers an operator's edits.
    Call from seed_demo and from post_migrate."""
    from .models import Role, RolePermission

    # Built-in Role rows (protected). Kept in sync so a bare `migrate` leaves the
    # 3 system roles present for the roles endpoint + validation.
    for role in ROLES:
        Role.objects.get_or_create(
            slug=role,
            defaults={"label": SYSTEM_ROLE_LABELS.get(role, role),
                      "is_system": True},
        )
    # Built-ins can never drift to is_system=False (would let them be deleted).
    Role.objects.filter(slug__in=ROLES).update(is_system=True)

    for role in ROLES:
        defaults = DEFAULT_MATRIX[role]
        for cap in CAPABILITY_IDS:
            RolePermission.objects.get_or_create(
                role=role, capability=cap,
                defaults={"allowed": defaults[cap]},
            )
    # Anti-lockout, even if a prior bad edit slipped through.
    RolePermission.objects.filter(
        role="superadmin", capability="rbac.manage").update(allowed=True)


# --- custom role management ----------------------------------------------------

class RoleError(Exception):
    """A rejected role create/delete — the message is operator-facing; `status`
    is the HTTP code the view should return and `extra` merges into the body."""

    def __init__(self, message: str, status: int = 400, extra: dict | None = None):
        self.message = message
        self.status = status
        self.extra = extra or {}
        super().__init__(message)


def create_role(label: str):
    """Create a custom role from an operator-supplied label. Slugifies the
    label, rejects a blank/unslug-able label, a duplicate slug, and any slug
    that collides with a built-in. Seeds a full set of all-false RolePermission
    rows (no auto-pass). Returns the new Role."""
    from django.utils.text import slugify

    from .models import Role, RolePermission

    label = (label or "").strip()
    if not label:
        raise RoleError("A role name is required.")
    slug = slugify(label)[:32].strip("-")
    if not slug:
        raise RoleError("Could not derive a role slug from that name.")
    if slug in ROLES:
        raise RoleError(f"'{slug}' collides with a built-in role.", status=409)
    if Role.objects.filter(slug=slug).exists():
        raise RoleError(f"A role '{slug}' already exists.", status=409)

    role = Role.objects.create(slug=slug, label=label, is_system=False)
    RolePermission.objects.bulk_create([
        RolePermission(role=slug, capability=cap, allowed=False)
        for cap in CAPABILITY_IDS
    ])
    return role


def rename_role(slug: str, label: str):
    """Rename a CUSTOM role's display label. The slug — the role's identity,
    referenced by Profile.role and RolePermission.role — is IMMUTABLE; only the
    human-facing label changes. 404 unknown, 400 a built-in (system roles are
    protected) or a blank label, 409 when another role already uses that label
    (case-insensitive) or the label would slugify onto a different role's slug.
    Returns the updated Role."""
    from django.utils.text import slugify

    from .models import Role

    role = Role.objects.filter(slug=slug).first()
    if role is None:
        raise RoleError("No such role.", status=404)
    if role.is_system:
        raise RoleError("Built-in roles cannot be renamed.", status=400)
    label = (label or "").strip()
    if not label:
        raise RoleError("A role name is required.")
    if Role.objects.filter(label__iexact=label).exclude(pk=role.pk).exists():
        raise RoleError(f"A role named '{label}' already exists.", status=409)
    new_slug = slugify(label)[:32].strip("-")
    if (new_slug and new_slug != role.slug
            and Role.objects.filter(slug=new_slug).exclude(pk=role.pk).exists()):
        raise RoleError(f"A role '{new_slug}' already exists.", status=409)
    role.label = label
    role.save(update_fields=["label"])
    return role


def delete_role(slug: str) -> None:
    """Delete a custom role: 404 unknown, refuse a built-in, and block (409 with
    the count) while any user still holds it. Removes its RolePermission rows."""
    from .models import Profile, Role, RolePermission

    role = Role.objects.filter(slug=slug).first()
    if role is None:
        raise RoleError("No such role.", status=404)
    if role.is_system:
        raise RoleError("Built-in roles cannot be deleted.", status=400)
    in_use = Profile.objects.filter(role=slug).count()
    if in_use:
        raise RoleError(
            f"{in_use} user(s) still hold this role — reassign them first.",
            status=409, extra={"user_count": in_use})
    RolePermission.objects.filter(role=slug).delete()
    role.delete()


class PermissionValidationError(ValueError):
    """A rejected PUT body — the message is operator-facing."""


def apply_permission_updates(matrix) -> list[tuple[str, str, bool]]:
    """Validate `{role: {capability: bool}}` against the catalog and apply it.

    Raises PermissionValidationError on any unknown role/capability, a
    non-boolean value, or an attempt to unset superadmin's rbac.manage
    (anti-lockout). Returns the list of applied (role, capability, allowed).
    """
    if not isinstance(matrix, dict):
        raise PermissionValidationError(
            "matrix must be an object of {role: {capability: bool}}.")

    from .models import Role, RolePermission

    # Any existing role (built-in or custom) is a valid target — the matrix is
    # dynamic now. Built-ins are always valid even if a Role row is missing.
    valid_roles = set(Role.objects.values_list("slug", flat=True)) | set(ROLES)
    changes: list[tuple[str, str, bool]] = []
    for role, caps in matrix.items():
        if role not in valid_roles:
            raise PermissionValidationError(f"Unknown role '{role}'.")
        if not isinstance(caps, dict):
            raise PermissionValidationError(f"matrix['{role}'] must be an object.")
        for cap, allowed in caps.items():
            if cap not in CAPABILITY_IDS:
                raise PermissionValidationError(f"Unknown capability '{cap}'.")
            if not isinstance(allowed, bool):
                raise PermissionValidationError(
                    f"{role}.{cap} must be true or false.")
            if (role, cap) in LOCKED and not allowed:
                raise PermissionValidationError(
                    "Superadmin's rbac.manage cannot be turned off.")
            changes.append((role, cap, allowed))

    for role, cap, allowed in changes:
        RolePermission.objects.update_or_create(
            role=role, capability=cap, defaults={"allowed": allowed})
    return changes
