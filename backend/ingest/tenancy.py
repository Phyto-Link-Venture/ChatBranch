"""Current-tenant context.

Multi-tenancy on SQLite is enforced in the application layer: code that reads
tenant-scoped data must filter by the active tenant. During partner sync the
tenant is resolved from the data source's api-key (see views/normalizer), not from a
web session, so this threadlocal is mainly for request-scoped reads once the
dashboard endpoints are tenant-aware (follow-up).

When the DB moves to Postgres, set `app.tenant_id` from here in middleware and
add RLS policies as defense in depth.
"""
from __future__ import annotations

import threading
from contextlib import contextmanager

_state = threading.local()


def set_current_tenant(tenant):
    _state.tenant = tenant


def get_current_tenant():
    return getattr(_state, "tenant", None)


def clear_current_tenant():
    _state.tenant = None


@contextmanager
def tenant_scope(tenant):
    """Run a block with `tenant` as the active tenant, restoring the previous
    value afterwards. Used by the normalizer and management commands."""
    previous = get_current_tenant()
    set_current_tenant(tenant)
    try:
        yield tenant
    finally:
        set_current_tenant(previous)
