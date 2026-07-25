"""Provider catalog + global integration settings (OAuth client, webhook secrets).

Two kinds of provider:

  oauth  — the four Google products. ONE Google OAuth client, incremental
           scopes per product (plan §3), PKCE + a signed `state` on every
           authorize round-trip (integrations/oauth.py).
  apikey — Shopify (shop domain + Admin API access token) and Stripe (secret
           key). These are inbound-first: their value is the webhook that maps
           orders/payments onto crm.Record — see integrations/webhooks.py.

Global settings live in ONE crm.Setting row (`integrations_settings`, JSON)
with per-field .env fallback — the same precedence as
whatsapp.health.voice_settings(). Secrets round-trip masked; a value
containing "••" is treated as untouched by the save path.
"""
from __future__ import annotations

import json
import os

SETTINGS_KEY = "integrations_settings"

GOOGLE_AUTH_URL = "https://accounts.google.com/o/oauth2/v2/auth"
GOOGLE_TOKEN_URL = "https://oauth2.googleapis.com/token"

# Every Google product asks for its own scope only — "incremental scopes per
# product" (plan §3). Connecting Drive never silently grants Gmail.
PROVIDERS: dict[str, dict] = {
    "google_calendar": {
        "label": "Google Calendar",
        "kind": "oauth",
        "family": "google",
        "scopes": ["https://www.googleapis.com/auth/calendar.events",
                   "https://www.googleapis.com/auth/calendar.readonly"],
        "blurb": "Book appointments and find free slots on a shared calendar.",
        "config_fields": [
            {"key": "calendar_id", "label": "Calendar id", "placeholder": "primary"},
        ],
        "actions": ["Create calendar event", "Find free slot"],
    },
    "google_drive": {
        "label": "Google Drive",
        "kind": "oauth",
        "family": "google",
        "scopes": ["https://www.googleapis.com/auth/drive.file"],
        "blurb": "Store documents a conversation produced (quotes, photos, receipts).",
        "config_fields": [
            {"key": "folder_id", "label": "Target folder id", "placeholder": "(My Drive root)"},
        ],
        "actions": ["Upload file to Drive"],
    },
    "gmail": {
        "label": "Gmail",
        "kind": "oauth",
        "family": "google",
        "scopes": ["https://www.googleapis.com/auth/gmail.send"],
        "blurb": "Send from a real Gmail mailbox instead of SMTP.",
        "config_fields": [
            {"key": "from_email", "label": "From address", "placeholder": "ops@example.com"},
        ],
        "actions": ["Send via Gmail"],
    },
    "google_sheets": {
        "label": "Google Sheets",
        "kind": "oauth",
        "family": "google",
        "scopes": ["https://www.googleapis.com/auth/spreadsheets"],
        "blurb": "Append a row per conversation outcome to an ops spreadsheet.",
        "config_fields": [
            {"key": "spreadsheet_id", "label": "Spreadsheet id", "placeholder": "1AbC…"},
            {"key": "sheet_range", "label": "Range", "placeholder": "Sheet1!A:Z"},
        ],
        "actions": ["Append row to Sheet"],
    },
    "shopify": {
        "label": "Shopify",
        "kind": "apikey",
        "family": "shopify",
        "scopes": [],
        "blurb": "Orders flow in by webhook and become Records; steps can tag "
                 "or note an order back.",
        "config_fields": [
            {"key": "shop_domain", "label": "Shop domain",
             "placeholder": "my-store.myshopify.com"},
            {"key": "api_version", "label": "Admin API version", "placeholder": "2024-10"},
        ],
        "secret_fields": [
            {"key": "api_key", "label": "Admin API access token"},
            {"key": "webhook_secret", "label": "Webhook signing secret"},
        ],
        "webhook_path": "/api/integrations/webhook/shopify",
        "actions": ["Update order status"],
    },
    "stripe": {
        "label": "Stripe",
        "kind": "apikey",
        "family": "stripe",
        "scopes": [],
        "blurb": "Payments confirm themselves — payment_intent.succeeded books "
                 "a RecordEvent against the matching Record.",
        "config_fields": [],
        "secret_fields": [
            {"key": "api_key", "label": "Secret key (sk_…)"},
            {"key": "webhook_secret", "label": "Webhook signing secret (whsec_…)"},
        ],
        "webhook_path": "/api/integrations/webhook/stripe",
        "actions": ["Create Stripe payment link"],
    },
}

PROVIDER_IDS = tuple(PROVIDERS)
GOOGLE_PROVIDERS = tuple(p for p, m in PROVIDERS.items() if m["family"] == "google")

# Global setting keys -> the .env variable each falls back to.
GLOBAL_KEYS = {
    "google_client_id": "GOOGLE_CLIENT_ID",
    "google_client_secret": "GOOGLE_CLIENT_SECRET",
    "google_redirect_uri": "GOOGLE_REDIRECT_URI",
    "shopify_webhook_secret": "SHOPIFY_WEBHOOK_SECRET",
    "stripe_webhook_secret": "STRIPE_WEBHOOK_SECRET",
}
SECRET_KEYS = ("google_client_secret", "shopify_webhook_secret", "stripe_webhook_secret")


def _stored() -> dict:
    """The dashboard-saved row WITHOUT env merge — save paths must write only
    what the operator typed, never bake .env into the DB."""
    from crm.models import Setting

    row = Setting.objects.filter(key=SETTINGS_KEY).first()
    if row and row.value.strip():
        try:
            data = json.loads(row.value)
            if isinstance(data, dict):
                return data
        except ValueError:
            pass
    return {}


def settings() -> dict:
    """Effective global integration settings: the Setting row wins per field,
    .env is the fallback, "" means "not configured anywhere"."""
    stored = _stored()
    return {key: (str(stored.get(key) or "").strip() or os.getenv(env, "").strip())
            for key, env in GLOBAL_KEYS.items()}


def save_settings(patch: dict) -> dict:
    """Merge a partial patch into the Setting row. Only keys present in the
    patch change; "" clears one back to the .env fallback."""
    from crm.models import Setting

    stored = _stored()
    for key in GLOBAL_KEYS:
        if key in patch:
            stored[key] = str(patch.get(key) or "").strip()
    Setting.objects.update_or_create(key=SETTINGS_KEY,
                                     defaults={"value": json.dumps(stored)})
    return settings()


def google_oauth_configured() -> bool:
    s = settings()
    return bool(s["google_client_id"] and s["google_client_secret"])


def redirect_uri(request=None) -> str:
    """Where Google sends the browser back. The configured value wins; without
    one we derive it from the request so a fresh clone works on localhost."""
    configured = settings()["google_redirect_uri"]
    if configured:
        return configured
    if request is not None:
        return request.build_absolute_uri("/api/integrations/oauth/google/callback")
    return "http://localhost:8000/api/integrations/oauth/google/callback"


def dashboard_url() -> str:
    """Where the OAuth callback bounces the operator back to."""
    base = os.getenv("FRONTEND_URL", "http://localhost:3000").rstrip("/")
    return f"{base}/settings/integrations"


def provider_meta(provider: str) -> dict | None:
    return PROVIDERS.get(provider)


def webhook_secret(provider: str, integration=None) -> str:
    """The HMAC key for a provider's webhook: the integration row's own
    secret first (multi-store setups), then the global setting/env."""
    if integration is not None:
        own = str((integration.credentials or {}).get("webhook_secret") or "").strip()
        if own:
            return own
    return settings().get(f"{provider}_webhook_secret", "")
