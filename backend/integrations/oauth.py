"""Google OAuth2 with PKCE + a signed `state`, and lazy token refresh.

Security rules this module exists to enforce (plan §3):

* **`state` is signed and time-limited.** It carries the integration pk and a
  nonce through `django.core.signing.dumps` (HMAC over SECRET_KEY, salted);
  the callback rejects a tampered, foreign or >10-minute-old state. That is
  the CSRF control for the callback, which cannot be bearer-authenticated —
  the browser arrives from accounts.google.com with no Authorization header.
* **PKCE (S256).** The verifier is generated at `start`, stashed on the
  integration row (never serialized out of the API) and sent once at
  exchange; the challenge is what travels through the browser.
* **Lazy refresh at call time.** `access_token()` refreshes only when the
  stored token is expired/near-expiry. A FAILED refresh transitions the row to
  "disconnected" through integrations/health.py and returns "" — it never
  raises into a send/flow path.
* **Nothing secret is ever logged.** Only provider ids, row pks and HTTP
  status codes appear in log lines; token/refresh-token/client-secret values
  never do, and neither do raw provider bodies (they carry tokens).
"""
from __future__ import annotations

import base64
import hashlib
import logging
import secrets
from datetime import timedelta

from django.core import signing
from django.utils import timezone

from . import catalog, health

logger = logging.getLogger(__name__)

STATE_SALT = "chatbranch.integrations.oauth"
STATE_MAX_AGE = 600      # seconds — an authorize round-trip is fast
REFRESH_SKEW = 60        # refresh this many seconds before real expiry
HTTP_TIMEOUT = 20


class OAuthError(RuntimeError):
    """Anything that went wrong in the authorize/exchange round-trip. The
    message is operator-facing and must never quote a token."""


# ---- PKCE ---------------------------------------------------------------------

def make_verifier() -> str:
    """RFC 7636 code_verifier: 43-128 chars of unreserved alphabet."""
    return secrets.token_urlsafe(64)[:128]


def challenge_for(verifier: str) -> str:
    digest = hashlib.sha256(verifier.encode("ascii")).digest()
    return base64.urlsafe_b64encode(digest).decode("ascii").rstrip("=")


# ---- signed state -------------------------------------------------------------

def sign_state(integration_id: int, provider: str) -> str:
    return signing.dumps({"i": int(integration_id), "p": provider,
                          "n": secrets.token_urlsafe(12)}, salt=STATE_SALT)


def verify_state(state: str) -> dict:
    """-> {"i", "p", "n"}. Raises OAuthError on a tampered/expired state."""
    try:
        data = signing.loads(str(state or ""), salt=STATE_SALT, max_age=STATE_MAX_AGE)
    except signing.SignatureExpired:
        raise OAuthError("This connect link expired — start the connection again.")
    except signing.BadSignature:
        raise OAuthError("Invalid OAuth state — the request did not originate here.")
    if not isinstance(data, dict) or not data.get("i") or not data.get("p"):
        raise OAuthError("Invalid OAuth state payload.")
    return data


# ---- authorize ----------------------------------------------------------------

def authorize_url(integration, request=None) -> str:
    """Build the Google consent URL for this integration (PKCE + signed state).

    Raises OAuthError when no Google client is configured — the sandbox case.
    The caller turns that into a 400 with the operator-facing message; nothing
    crashes and no network call is attempted.
    """
    from urllib.parse import urlencode

    meta = catalog.provider_meta(integration.provider) or {}
    if meta.get("kind") != "oauth":
        raise OAuthError(f"{meta.get('label', integration.provider)} does not use OAuth — "
                         "paste its API key on the account instead.")
    s = catalog.settings()
    if not (s["google_client_id"] and s["google_client_secret"]):
        raise OAuthError(
            "Google OAuth is not configured. Add a client id + secret in "
            "Settings → Integrations (or GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET "
            "in backend/.env) before connecting a Google account.")

    verifier = make_verifier()
    creds = dict(integration.credentials or {})
    creds["code_verifier"] = verifier
    integration.credentials = creds
    integration.save(update_fields=["credentials", "updated_at"])

    params = {
        "client_id": s["google_client_id"],
        "redirect_uri": catalog.redirect_uri(request),
        "response_type": "code",
        "scope": " ".join(meta.get("scopes") or []),
        "access_type": "offline",       # we want a refresh_token
        "prompt": "consent",
        "include_granted_scopes": "true",  # incremental authorization
        "code_challenge": challenge_for(verifier),
        "code_challenge_method": "S256",
        "state": sign_state(integration.pk, integration.provider),
    }
    return f"{catalog.GOOGLE_AUTH_URL}?{urlencode(params)}"


# ---- token exchange + refresh --------------------------------------------------

def _post_token(payload: dict) -> dict:
    """One POST to Google's token endpoint. Returns the parsed JSON. Raises
    OAuthError with a SAFE message (status + provider error code only — the
    body carries tokens and is never logged or echoed)."""
    import httpx  # lazy: keep cheap request paths free of the HTTP client

    try:
        resp = httpx.post(catalog.GOOGLE_TOKEN_URL, data=payload, timeout=HTTP_TIMEOUT)
    except Exception as e:
        raise OAuthError(f"Could not reach Google's token endpoint: {type(e).__name__}")
    if resp.status_code != 200:
        code = ""
        try:
            code = str((resp.json() or {}).get("error") or "")
        except ValueError:
            pass
        logger.warning("integrations oauth: token endpoint returned %s (%s)",
                       resp.status_code, code or "no error code")
        raise OAuthError(f"Google rejected the token request (HTTP {resp.status_code}"
                         + (f", {code}" if code else "") + ").")
    try:
        data = resp.json()
    except ValueError:
        raise OAuthError("Google returned a token response we could not parse.")
    return data if isinstance(data, dict) else {}


def _store_tokens(integration, data: dict, keep_refresh: bool = True) -> None:
    creds = dict(integration.credentials or {})
    creds.pop("code_verifier", None)  # single-use — never keep it around
    if data.get("access_token"):
        creds["access_token"] = data["access_token"]
    if data.get("refresh_token"):
        creds["refresh_token"] = data["refresh_token"]
    elif not keep_refresh:
        creds.pop("refresh_token", None)
    creds["token_type"] = data.get("token_type") or "Bearer"
    try:
        expires_in = int(data.get("expires_in") or 0)
    except (TypeError, ValueError):
        expires_in = 0
    creds["expires_at"] = ((timezone.now() + timedelta(seconds=expires_in)).isoformat()
                           if expires_in else "")
    integration.credentials = creds
    if data.get("scope"):
        integration.scopes = str(data["scope"]).split()
    integration.save(update_fields=["credentials", "scopes", "updated_at"])


def exchange_code(integration, code: str, request=None) -> None:
    """Swap an authorization code for tokens (PKCE verifier included) and store
    them. Marks the row healthy on success."""
    s = catalog.settings()
    verifier = str((integration.credentials or {}).get("code_verifier") or "")
    if not verifier:
        raise OAuthError("This connection was not started here (missing PKCE verifier).")
    data = _post_token({
        "client_id": s["google_client_id"],
        "client_secret": s["google_client_secret"],
        "code": code,
        "code_verifier": verifier,
        "grant_type": "authorization_code",
        "redirect_uri": catalog.redirect_uri(request),
    })
    if not data.get("access_token"):
        raise OAuthError("Google's response carried no access token.")
    _store_tokens(integration, data)
    health.set_status(integration, health.HEALTHY, "")


def _expired(integration) -> bool:
    from django.utils.dateparse import parse_datetime

    raw = str((integration.credentials or {}).get("expires_at") or "")
    if not raw:
        return False  # no expiry recorded — assume usable, a 401 will correct us
    when = parse_datetime(raw)
    if when is None:
        return False
    if timezone.is_naive(when):
        when = timezone.make_aware(when)
    return timezone.now() >= when - timedelta(seconds=REFRESH_SKEW)


def refresh(integration) -> bool:
    """Exchange the stored refresh token for a new access token.

    Returns True on success. On failure the row transitions to "disconnected"
    (through health.set_status, so the operator is notified exactly once) and
    False comes back — callers treat that as "not configured" and skip the
    step rather than blowing up a flow run or a webhook ack.
    """
    creds = integration.credentials or {}
    token = str(creds.get("refresh_token") or "")
    s = catalog.settings()
    if not token or not (s["google_client_id"] and s["google_client_secret"]):
        health.set_status(integration, health.DISCONNECTED,
                          "no refresh token stored — reconnect this account")
        return False
    try:
        data = _post_token({
            "client_id": s["google_client_id"],
            "client_secret": s["google_client_secret"],
            "refresh_token": token,
            "grant_type": "refresh_token",
        })
    except OAuthError as e:
        health.set_status(integration, health.DISCONNECTED, str(e)[:200])
        return False
    if not data.get("access_token"):
        health.set_status(integration, health.DISCONNECTED,
                          "refresh returned no access token — reconnect this account")
        return False
    _store_tokens(integration, data)
    return True


def access_token(integration) -> str:
    """The usable bearer token for this integration, refreshing lazily.

    Returns "" when the row has no credentials, when the refresh failed, or on
    any unexpected error — NEVER raises into a call site. A "" result always
    means "treat this integration as not configured".
    """
    try:
        if integration is None:
            return ""
        creds = integration.credentials or {}
        token = str(creds.get("access_token") or "")
        if not token:
            return ""
        if _expired(integration):
            if not refresh(integration):
                return ""
            token = str((integration.credentials or {}).get("access_token") or "")
        return token
    except Exception:
        logger.exception("integrations oauth: access_token failed for %s",
                         getattr(integration, "pk", integration))
        return ""


def revoke_local(integration) -> None:
    """Forget the stored grant (the operator disconnected). We do not call
    Google's revoke endpoint — a shared client may hold other grants."""
    integration.credentials = {}
    integration.scopes = []
    integration.save(update_fields=["credentials", "scopes", "updated_at"])
    health.set_status(integration, health.REVOKED, "disconnected by an operator")
