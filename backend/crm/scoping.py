"""Global ↔ entity resolution — the ONE place the binding rule lives.

The operator's rule, applied uniformly across the platform:

    an entity-specific binding wins; the global one is the fallback.

Nothing in this module caches: every call reads live, so an override saved from
the dashboard applies without a restart (same contract as wa_config/msg_config).
Every read is exception-safe — a scoping failure must degrade to the global
value, never break a send/webhook/tick path.

Three verbs, and picking the wrong one is the bug this module exists to prevent:

  * `setting_value(key, entity)` — config. Entity row -> global row -> "".
    Callers then apply their own .env/built-in fallback as before.

  * `scope_inherited(qs, entity)` — libraries an entity INHERITS: knowledge,
    canned/script templates, skills, workflows, channel accounts, integrations.
    Returns the entity's own rows PLUS the global ones.

  * `scope_owned(qs, entity)` — records that BELONG to exactly one entity:
    contacts, threads, call records, outbound jobs, analytics rows. Returns
    only that entity's own. Global/unassigned rows are deliberately excluded
    from a single-entity view and surface in the All-entities view instead.

`entity=None` means "All entities" for the scope_* helpers (no narrowing) and
"the global value" for the setting helpers. The two never collide because a
caller either has an entity in hand or is explicitly asking for the org default.
"""
from __future__ import annotations

import logging

logger = logging.getLogger(__name__)

# ---- which Setting keys may be overridden per entity -------------------------

# Config an outlet legitimately differs on. Anything NOT listed here is
# organization-wide by design — provider API keys, webhook/verify secrets, LLM
# prices, notification prefs, health bookkeeping. Keeping this an explicit
# allowlist (rather than "anything may be overridden") is what stops an operator
# from accidentally forking a credential per outlet and then wondering why one
# branch stopped sending.
ENTITY_SCOPED_SETTINGS = frozenset({
    # opening/appointment schedule — the per-outlet case that started this
    "booking_hours",
    # how the assistant presents itself for this outlet
    "aicore_persona",
    # contact-time compliance
    "quiet_start", "quiet_end",
    # outbound pacing + caps
    "queue_min_gap_s", "queue_max_gap_s", "queue_daily_cap",
    "queue_active_cap", "queue_active_window_h",
    "queue_call_gap_s", "queue_call_daily_cap",
    # conversation operating rules
    "ai_reply_mode", "reminder_ai_default", "ai_tool_use",
    "business_redirect", "business_redirect_target", "business_redirect_template",
    "recovery_mode", "recovery_template",
    "cold_text_guard", "cold_call_guard",
    # per-outlet contact details / voice
    "support_contact", "flow_call_voice", "email_subject",
})


def is_entity_scoped(key: str) -> bool:
    """True when `key` may carry a per-entity override."""
    return str(key or "") in ENTITY_SCOPED_SETTINGS


# ---- entity coercion ---------------------------------------------------------

def as_entity(value):
    """Coerce an Entity / pk / slug / None into an Entity instance or None.
    Never raises — an unknown slug resolves to None (i.e. the global scope),
    which is the safe direction: you get the org default, not another
    entity's data."""
    if value is None or value == "":
        return None
    try:
        from ingest.models import Entity

        if isinstance(value, Entity):
            return value
        if isinstance(value, int) or (isinstance(value, str) and value.isdigit()):
            return Entity.objects.filter(pk=int(value)).first()
        return Entity.objects.filter(slug=str(value).strip()).first()
    except Exception:
        logger.exception("scoping: as_entity failed for %r", value)
        return None


def entity_id_of(value) -> int | None:
    ent = as_entity(value)
    return ent.pk if ent is not None else None


# ---- settings: entity row -> global row --------------------------------------

def setting_row(key: str, entity=None):
    """The Setting row that governs `key` for `entity`: the entity's own
    override if it has one, else the global row, else None."""
    from .models import Setting

    eid = entity_id_of(entity)
    if eid is not None and is_entity_scoped(key):
        row = Setting.objects.filter(key=key, entity_id=eid).first()
        if row is not None:
            return row
    return Setting.objects.filter(key=key, entity__isnull=True).first()


def setting_value(key: str, entity=None) -> str:
    """The stored value governing `key` for `entity`, or "" when neither an
    override nor a global row has a non-blank value.

    A BLANK entity override falls through to the global row on purpose: the UI
    clears an override by deleting the row, so a blank one means "no opinion",
    exactly as a blank global row means "fall back to .env"."""
    try:
        from .models import Setting

        eid = entity_id_of(entity)
        if eid is not None and is_entity_scoped(key):
            row = Setting.objects.filter(key=key, entity_id=eid).first()
            if row and row.value.strip():
                return row.value.strip()
        row = Setting.objects.filter(key=key, entity__isnull=True).first()
        if row and row.value.strip():
            return row.value.strip()
    except Exception:
        logger.exception("scoping: setting_value(%s) failed", key)
    return ""


def setting_values(keys, entity=None) -> dict[str, str]:
    """Bulk `setting_value` — two queries instead of 2N. Same resolution."""
    out: dict[str, str] = {}
    try:
        from .models import Setting

        wanted = [str(k) for k in keys]
        for row in Setting.objects.filter(key__in=wanted, entity__isnull=True):
            if row.value.strip():
                out[row.key] = row.value.strip()
        eid = entity_id_of(entity)
        if eid is not None:
            overridable = [k for k in wanted if is_entity_scoped(k)]
            for row in Setting.objects.filter(key__in=overridable, entity_id=eid):
                if row.value.strip():
                    out[row.key] = row.value.strip()
    except Exception:
        logger.exception("scoping: setting_values failed")
    return out


def setting_scope(key: str, entity=None) -> str:
    """Provenance for the settings UI: "entity" when this entity overrides the
    key, "global" when it inherits a stored org value, "" when neither exists
    (the caller's .env/built-in default is in play)."""
    try:
        from .models import Setting

        eid = entity_id_of(entity)
        if eid is not None and is_entity_scoped(key) and \
                Setting.objects.filter(key=key, entity_id=eid).exists():
            return "entity"
        if Setting.objects.filter(key=key, entity__isnull=True).exists():
            return "global"
    except Exception:
        logger.exception("scoping: setting_scope(%s) failed", key)
    return ""


def set_setting(key: str, value: str, entity=None) -> None:
    """Write the global row (entity=None) or an entity override. Writing an
    override for a key that is not entity-scoped is refused loudly — that is a
    programming error, not operator input."""
    from .models import Setting

    ent = as_entity(entity)
    if ent is not None and not is_entity_scoped(key):
        raise ValueError(f"Setting '{key}' is organization-wide and cannot be "
                         f"overridden per entity.")
    Setting.objects.update_or_create(
        key=key, entity=ent, defaults={"value": str(value or "").strip()})


def clear_setting(key: str, entity) -> bool:
    """Drop an entity's override so it inherits the global value again.
    Returns True when a row was actually removed. Refuses to delete the global
    row — clearing THAT is `set_setting(key, "")`, which is how every existing
    accessor already means "fall back to .env"."""
    ent = as_entity(entity)
    if ent is None:
        return False
    from .models import Setting

    deleted, _ = Setting.objects.filter(key=key, entity=ent).delete()
    return bool(deleted)


# ---- querysets: inherited libraries vs entity-owned records ------------------

def scope_inherited(qs, entity, *, field: str = "entity"):
    """Libraries an entity inherits: its OWN rows plus the global ones.
    `entity=None` (All entities) does not narrow — the owner sees everything."""
    ent = as_entity(entity)
    if ent is None:
        return qs
    from django.db.models import Q

    return qs.filter(Q(**{f"{field}__isnull": True}) | Q(**{field: ent}))


def scope_owned(qs, entity, *, field: str = "entity", include_unassigned: bool = False):
    """Records that belong to exactly one entity. `entity=None` (All entities)
    does not narrow. `include_unassigned` also returns rows with no entity —
    used by inbox views that offer an "unassigned" bucket rather than hiding
    conversations a global number received."""
    ent = as_entity(entity)
    if ent is None:
        return qs
    if include_unassigned:
        from django.db.models import Q

        return qs.filter(Q(**{f"{field}__isnull": True}) | Q(**{field: ent}))
    return qs.filter(**{field: ent})


def override_by(rows, entity, key_attr: str):
    """Collapse an inherited library to what the entity ACTUALLY sees: one row
    per `key_attr`, the entity's own version shadowing the global one of the
    same key. Takes an iterable, returns a list in the input's order (the
    entity row keeps the position of the global row it replaces, so an
    overridden template does not jump around in the list).

    Pure Python on purpose — these libraries are small (tens of rows), and
    expressing "override by key" in SQL costs a window function SQLite only
    recently learned."""
    ent_id = entity_id_of(entity)
    rows = list(rows)
    if ent_id is None:
        return rows
    own = {getattr(r, key_attr) for r in rows if getattr(r, "entity_id", None) == ent_id}
    out = []
    seen: set = set()
    for r in rows:
        key = getattr(r, key_attr)
        is_own = getattr(r, "entity_id", None) == ent_id
        if not is_own and key in own:
            continue                      # shadowed by this entity's own row
        if key in seen:
            continue                      # another entity's row — not ours
        if is_own or getattr(r, "entity_id", None) is None:
            seen.add(key)
            out.append(r)
    return out


# ---- channel accounts + integrations: entity binding, global fallback --------

def accounts_for_entity(qs, entity):
    """Channel accounts usable BY `entity`: its own if it has any, otherwise
    the global (unbound) ones.

    Note this is a preference, not a union: once an outlet has its own WhatsApp
    number, sends for that outlet must go out on it — silently falling back to
    the company number would be exactly the leak the entity binding exists to
    stop. With no account of its own the outlet uses the global pool."""
    ent = as_entity(entity)
    if ent is None:
        return qs
    own = qs.filter(entity=ent)
    return own if own.exists() else qs.filter(entity__isnull=True)


def integration_for_entity(provider: str, entity=None, *, connected_only: bool = True):
    """The Integration `provider` connection governing `entity`: the entity's
    own if present, else the organization-wide one, else None. Same preference
    rule as channel accounts — each studio's bookings land in its own calendar,
    and outlets that never connected one use the company account."""
    try:
        from integrations.models import Integration

        qs = Integration.objects.filter(provider=provider)
        if connected_only:
            qs = qs.exclude(status__in=("disconnected", "revoked"))
        ent = as_entity(entity)
        if ent is not None:
            own = qs.filter(entity=ent).first()
            if own is not None:
                return own
        return qs.filter(entity__isnull=True).first()
    except Exception:
        logger.exception("scoping: integration_for_entity(%s) failed", provider)
        return None


# ---- thread attribution ------------------------------------------------------

def thread_entity(account=None, contact=None):
    """Which entity a conversation belongs to, in priority order:

      1. the RECEIVING ACCOUNT's entity — the number that took the message
         decides, so attribution needs no manual tagging;
      2. the linked CONTACT's entity — covers global/shared numbers, where the
         account cannot tell us which outlet this is;
      3. None — "unassigned": a brand-new contact on a shared number. It shows
         in the All-entities view and can be tagged from the chat. Guessing an
         outlet here would put a conversation under the wrong branch, which is
         worse than leaving it visibly unassigned.
    """
    try:
        ent = getattr(account, "entity", None)
        if ent is not None:
            return ent
        if contact is not None:
            links = list(contact.entity_links.select_related("entity")[:2])
            if len(links) == 1:
                return links[0].entity
            if links:
                # Multi-entity contact on a shared number: no basis to choose.
                return None
    except Exception:
        logger.exception("scoping: thread_entity failed")
    return None


def sync_thread_entity(thread, *, save: bool = True):
    """Recompute and store `thread.entity`. Called wherever a thread's account
    or contact link changes (creation, inbound re-attribution, manual account
    switch, contact linking) so the denormalized column cannot drift from the
    rule above. Returns the resolved entity."""
    try:
        ent = thread_entity(account=getattr(thread, "account", None),
                            contact=getattr(thread, "contact", None))
        if getattr(thread, "entity_id", None) != (ent.pk if ent else None):
            thread.entity = ent
            if save:
                thread.save(update_fields=["entity", "updated_at"])
        return ent
    except Exception:
        logger.exception("scoping: sync_thread_entity failed")
        return None


# ---- request helpers ---------------------------------------------------------

def entity_from_request(request, *, param: str = "entity"):
    """The entity a request is scoped to, or None for "All entities".

    Reads `?entity=<slug>` and BOUNDS it by what the caller may see, so a
    hand-crafted slug cannot read another tenant's outlet. An agent assigned to
    exactly one entity is pinned to it even when the parameter is absent —
    their "All entities" is that one entity anyway, and pinning means a missing
    parameter can never widen their view.

    A slug the caller may NOT see falls through to that pinning rule rather
    than returning None. This is the important direction: None means "all
    entities", so returning it for a rejected slug would WIDEN the response
    instead of narrowing it — an agent asking for a sibling outlet would have
    been handed every outlet."""
    try:
        raw = ""
        if request is not None and hasattr(request, "GET"):
            raw = (request.GET.get(param) or "").strip()
        allowed = visible_entities(request)
        if raw and raw not in ("__all__", "all"):
            match = next((e for e in allowed if e.slug == raw), None)
            if match is not None:
                return match
            # not permitted (or unknown) — fall through, never widen
        if len(allowed) == 1:
            return allowed[0]
    except Exception:
        logger.exception("scoping: entity_from_request failed")
    return None


def visible_entities(request) -> list:
    """Entities the caller may see, as a list. Works for BOTH auth surfaces:
    the v1 DRF views (request.user) and the legacy bearer-token endpoints
    (crm.auth resolves the Profile from the header)."""
    try:
        from api import rbac

        user = getattr(request, "user", None)
        if user is None or not getattr(user, "is_authenticated", False):
            from crm import auth as crm_auth

            user = crm_auth.user_from_request(request)
        if user is None:
            return []
        return list(rbac.accessible_entities(user))
    except Exception:
        logger.exception("scoping: visible_entities failed")
        return []
