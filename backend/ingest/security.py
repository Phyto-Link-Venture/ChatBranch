"""Source-system API-key handling for the sync intake.

The source sends its key in the `X-API-Key` header. We never store the raw
key: `DataSource.api_key_hash` holds SHA-256(key). Lookup hashes the presented
key and matches the (indexed) column; the final compare is constant-time.
"""
from __future__ import annotations

import hashlib
import hmac

from .models import DataSource


def hash_api_key(raw: str) -> str:
    return hashlib.sha256((raw or "").encode("utf-8")).hexdigest()


def source_from_request(request) -> DataSource | None:
    """Resolve the active DataSource for a presented X-API-Key, or None."""
    presented = request.headers.get("X-API-Key", "")
    if not presented:
        return None
    digest = hash_api_key(presented)
    source = (
        DataSource.objects.filter(is_active=True, api_key_hash=digest)
        .select_related("tenant")
        .first()
    )
    if source is None:
        return None
    # Defense in depth: constant-time compare even though the lookup matched.
    if not hmac.compare_digest(source.api_key_hash, digest):
        return None
    return source
