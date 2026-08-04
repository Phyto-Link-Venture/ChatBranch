"""Resource (service-provider) resolution — the teacher↔student binding layer.

Thin helpers over `crm.Resource` / `crm.ResourceLink` so callers (the reschedule
flow, the AI context builder, the API) never touch the join table directly:

    resources_for_contact(contact)  -> the providers bound to a customer
    contacts_for_resource(resource) -> the customers bound to a provider
    link(resource, contact)         -> create/refresh a binding (idempotent)
    unlink(resource, contact)       -> drop a binding
    availability_ref(resource)      -> the {sheet_id, range, calendar_id} bag

`resource_label()` is the per-deployment display word ("teacher", "coach") so
the UI and prompts read naturally without hard-coding a vertical.
"""
from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def resource_label(default: str = "provider") -> str:
    """The vertical's display word for a Resource — Setting `resource_label`
    (e.g. "teacher"), falling back to a neutral default. Never raises."""
    try:
        from .models import Setting

        row = Setting.objects.filter(key="resource_label").first()
        val = (row.value if row else "").strip() if row else ""
        return val or default
    except Exception:
        return default


def resources_for_contact(contact, *, active_only: bool = True) -> list:
    """The Resources (providers) bound to a customer Contact, newest link first."""
    if contact is None:
        return []
    try:
        from .models import ResourceLink

        qs = ResourceLink.objects.filter(contact=contact).select_related("resource")
        if active_only:
            qs = qs.filter(active=True, resource__active=True)
        return [ln.resource for ln in qs]
    except Exception:
        logger.exception("crm.resources: resources_for_contact failed")
        return []


def contacts_for_resource(resource, *, active_only: bool = True) -> list:
    """The customer Contacts bound to a Resource (provider)."""
    if resource is None:
        return []
    try:
        from .models import ResourceLink

        qs = ResourceLink.objects.filter(resource=resource).select_related("contact")
        if active_only:
            qs = qs.filter(active=True)
        return [ln.contact for ln in qs]
    except Exception:
        logger.exception("crm.resources: contacts_for_resource failed")
        return []


def link(resource, contact, *, relationship: str = "", since=None, active: bool = True):
    """Create or refresh a binding (idempotent on the (resource, contact) pair)."""
    from .models import ResourceLink

    defaults = {"active": active}
    if relationship:
        defaults["relationship"] = relationship[:32]
    if since is not None:
        defaults["since"] = since
    row, _created = ResourceLink.objects.update_or_create(
        resource=resource, contact=contact, defaults=defaults)
    return row


def unlink(resource, contact) -> int:
    """Remove a binding; returns the number of rows deleted (0 or 1)."""
    from .models import ResourceLink

    deleted, _ = ResourceLink.objects.filter(resource=resource, contact=contact).delete()
    return deleted


def availability_ref(resource) -> dict:
    """The provider's availability/scheduling references from `config`
    ({availability_sheet_id, availability_range, calendar_id, …})."""
    return (getattr(resource, "config", None) or {})
