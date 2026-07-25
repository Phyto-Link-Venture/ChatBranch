"""Signature verification for the Shopify + Stripe webhooks.

`docs/channel-adapter.md` records that the Messenger/Instagram webhook shipped
WITHOUT a signature check — a known gap. These two do not repeat it:

* **Shopify** — base64(HMAC-SHA256(raw body, shared secret)) compared against
  `X-Shopify-Hmac-Sha256` with `hmac.compare_digest`.
* **Stripe** — the `Stripe-Signature` scheme: `t=<unix>,v1=<hex>`, signed
  payload `"{t}.{raw body}"`, HMAC-SHA256 with the endpoint's `whsec_…`, plus
  a 5-minute replay window.

No secret configured = **reject** (503), never "accept unsigned". A template
that shipped with an open webhook would be a vulnerability, not a sandbox
convenience — the endpoint exists but declines to process anything until the
operator pastes the signing secret.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import logging
import time

from . import catalog

logger = logging.getLogger(__name__)

STRIPE_TOLERANCE = 300  # seconds — replay window, matches Stripe's own default


class WebhookError(Exception):
    """Verification failed. `.status` is the HTTP status to return."""

    def __init__(self, message: str, status: int = 401):
        super().__init__(message)
        self.status = status


def _candidate_secrets(provider: str, integration=None) -> list[str]:
    """Per-integration secret first (multi-store), then the global one. Both
    are tried because the shop-domain header that picks the row is itself
    unverified — the HMAC is what actually decides."""
    out: list[str] = []
    if integration is not None:
        own = str((integration.credentials or {}).get("webhook_secret") or "").strip()
        if own:
            out.append(own)
    shared = catalog.settings().get(f"{provider}_webhook_secret", "")
    if shared and shared not in out:
        out.append(shared)
    return out


# ---- Shopify ---------------------------------------------------------------------

def shopify_integration(request):
    """The Integration row this delivery belongs to, matched on the (unverified)
    shop-domain header. None is fine — the global secret still verifies."""
    from .models import Integration

    domain = str(request.headers.get("X-Shopify-Shop-Domain", "")).strip().lower()
    qs = Integration.objects.filter(provider="shopify")
    if domain:
        for row in qs:
            if str((row.config or {}).get("shop_domain") or "").strip().lower() == domain:
                return row
    return qs.first()


def verify_shopify(request, integration=None) -> bytes:
    """Returns the raw body once the HMAC checks out. Raises WebhookError."""
    provided = str(request.headers.get("X-Shopify-Hmac-Sha256", "")).strip()
    if not provided:
        raise WebhookError("Missing X-Shopify-Hmac-Sha256 header.")
    secrets_ = _candidate_secrets("shopify", integration)
    if not secrets_:
        raise WebhookError(
            "Shopify webhook secret is not configured — set it in "
            "Settings → Integrations before pointing Shopify here.", status=503)
    body = request.body or b""
    for secret in secrets_:
        digest = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).digest()
        if hmac.compare_digest(base64.b64encode(digest).decode("ascii"), provided):
            return body
    logger.warning("integrations: rejected a Shopify webhook with a bad signature")
    raise WebhookError("Shopify signature verification failed.")


# ---- Stripe ----------------------------------------------------------------------

def stripe_integration(request):
    from .models import Integration

    return Integration.objects.filter(provider="stripe").first()


def _parse_stripe_signature(header: str) -> tuple[str, list[str]]:
    timestamp, signatures = "", []
    for part in str(header or "").split(","):
        key, _, value = part.strip().partition("=")
        if key == "t":
            timestamp = value.strip()
        elif key == "v1":
            signatures.append(value.strip())
    return timestamp, signatures


def verify_stripe(request, integration=None, now: float | None = None) -> bytes:
    """Returns the raw body once the `Stripe-Signature` checks out (including
    the replay window). Raises WebhookError."""
    header = request.headers.get("Stripe-Signature", "")
    timestamp, signatures = _parse_stripe_signature(header)
    if not timestamp or not signatures:
        raise WebhookError("Missing or malformed Stripe-Signature header.")
    try:
        sent_at = int(timestamp)
    except ValueError:
        raise WebhookError("Malformed Stripe-Signature timestamp.")
    if abs((now if now is not None else time.time()) - sent_at) > STRIPE_TOLERANCE:
        raise WebhookError("Stripe signature timestamp outside the tolerance window.")
    secrets_ = _candidate_secrets("stripe", integration)
    if not secrets_:
        raise WebhookError(
            "Stripe webhook secret is not configured — paste the whsec_… value in "
            "Settings → Integrations before pointing Stripe here.", status=503)
    body = request.body or b""
    payload = timestamp.encode("ascii") + b"." + body
    for secret in secrets_:
        expected = hmac.new(secret.encode("utf-8"), payload, hashlib.sha256).hexdigest()
        if any(hmac.compare_digest(expected, s) for s in signatures):
            return body
    logger.warning("integrations: rejected a Stripe webhook with a bad signature")
    raise WebhookError("Stripe signature verification failed.")


def stripe_signature_header(secret: str, body: bytes, timestamp: int | None = None) -> str:
    """Build a valid `Stripe-Signature` for a body — used by the tests (and by
    anyone replaying a payload against a local server)."""
    ts = int(timestamp if timestamp is not None else time.time())
    payload = str(ts).encode("ascii") + b"." + body
    sig = hmac.new(secret.encode("utf-8"), payload, hashlib.sha256).hexdigest()
    return f"t={ts},v1={sig}"


def shopify_signature_header(secret: str, body: bytes) -> str:
    digest = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).digest()
    return base64.b64encode(digest).decode("ascii")
