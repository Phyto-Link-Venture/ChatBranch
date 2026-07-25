"""Integrations REST API (under /api/integrations/).

  GET  providers                 -> catalog + per-provider connected state
  GET/POST accounts              -> list / create integration rows (POST admin+)
  PATCH/DELETE accounts/<id>     -> update / remove (admin+)
  POST accounts/<id>/test        -> one live probe
  GET  oauth/<provider>/start    -> {authorize_url} (PKCE + signed state)
  GET  oauth/<provider>/callback -> token exchange, then bounce to the dashboard
  GET/PUT settings               -> OAuth client + webhook secrets (PUT superadmin)
  POST webhook/shopify           -> HMAC-verified order intake
  POST webhook/stripe            -> HMAC-verified payment intake

AUTH mirrors whatsapp/views.py exactly: every endpoint needs a bearer role,
mutations need admin+, global settings need superadmin. The ONLY open routes
are the two webhooks (signed by HMAC) and the OAuth callback — a browser
arriving from Google carries no Authorization header, so its CSRF control is
the signed, time-limited `state` (integrations/oauth.py).

SECRETS NEVER LEAVE THE SERVER. `_payload` masks every credential the same way
whatsapp/views.py `_mask` does, and a value still containing "••" on the way
back in is treated as untouched.
"""
from __future__ import annotations

import json
import logging

from django.http import HttpResponseNotAllowed, HttpResponseRedirect, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac

from . import actions, catalog, health, mapping, oauth, webhooks
from .models import Integration

logger = logging.getLogger(__name__)


# ---- request helpers -------------------------------------------------------------

def _json_body(request) -> dict:
    try:
        body = json.loads(request.body or b"{}")
    except ValueError:
        return {}
    return body if isinstance(body, dict) else {}


def _auth_required(request) -> JsonResponse | None:
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _can_manage(request) -> bool:
    return rbac.role_from_request(request) in ("superadmin", "admin")


def _is_superadmin(request) -> bool:
    return rbac.role_from_request(request) == "superadmin"


def _requester(request) -> str:
    return rbac.requester_name(request)


def _mask(secret: str) -> str:
    if not secret:
        return ""
    return f"••••{secret[-4:]}" if len(secret) >= 8 else "••••"


def _untouched(value) -> bool:
    """A masked placeholder round-tripped from a GET — keep the stored secret."""
    return isinstance(value, str) and "••" in value


# ---- serialization ----------------------------------------------------------------

def _payload(row: Integration) -> dict:
    meta = catalog.provider_meta(row.provider) or {}
    creds = row.credentials or {}
    return {
        "id": row.pk,
        "provider": row.provider,
        "provider_label": meta.get("label", row.provider),
        "kind": meta.get("kind", "oauth"),
        "name": row.name,
        "external_id": row.external_id,
        "config": row.config or {},
        "scopes": row.scopes or [],
        "connected": row.connected,
        # secrets: masked only, never the value
        "api_key_masked": _mask(str(creds.get("api_key") or "")),
        "webhook_secret_masked": _mask(str(creds.get("webhook_secret") or "")),
        "has_refresh_token": bool(creds.get("refresh_token")),
        "status": row.status,
        "status_detail": row.status_detail,
        "last_ok_at": row.last_ok_at.isoformat() if row.last_ok_at else None,
        "status_changed_at": (row.status_changed_at.isoformat()
                              if row.status_changed_at else None),
        "created_by": row.created_by,
        "created_at": row.created_at.isoformat() if row.created_at else None,
    }


def providers(request):
    """The catalog + how many rows each provider has connected. Works with an
    empty database and zero credentials — that is the template's default."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    rows = list(Integration.objects.all())
    out = []
    for pid, meta in catalog.PROVIDERS.items():
        mine = [r for r in rows if r.provider == pid]
        connected = [r for r in mine if r.connected]
        out.append({
            "id": pid,
            "label": meta["label"],
            "kind": meta["kind"],
            "family": meta["family"],
            "blurb": meta["blurb"],
            "scopes": meta.get("scopes", []),
            "config_fields": meta.get("config_fields", []),
            "secret_fields": meta.get("secret_fields", []),
            "actions": meta.get("actions", []),
            "webhook_path": meta.get("webhook_path", ""),
            "accounts": len(mine),
            "connected": len(connected),
            "oauth_configured": (catalog.google_oauth_configured()
                                 if meta["kind"] == "oauth" else True),
        })
    return JsonResponse({
        "providers": out,
        "google_oauth_configured": catalog.google_oauth_configured(),
        "sandbox": not any(r.connected for r in rows),
    })


@csrf_exempt
def accounts(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        qs = Integration.objects.all()
        provider = str(request.GET.get("provider") or "").strip()
        if provider:
            qs = qs.filter(provider=provider)
        return JsonResponse({"accounts": [_payload(r) for r in qs]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage integrations."},
                            status=403)
    body = _json_body(request)
    provider = str(body.get("provider") or "").strip()
    if provider not in catalog.PROVIDERS:
        return JsonResponse(
            {"detail": f"provider must be one of {', '.join(catalog.PROVIDER_IDS)}."},
            status=400)
    meta = catalog.PROVIDERS[provider]
    row = Integration(
        provider=provider,
        name=str(body.get("name") or meta["label"])[:120],
        external_id=str(body.get("external_id") or "")[:190],
        config=body.get("config") if isinstance(body.get("config"), dict) else {},
        created_by=_requester(request),
        tenant=rbac.tenant_from_request(request),
    )
    creds = {}
    for key in ("api_key", "webhook_secret"):
        value = body.get(key)
        if isinstance(value, str) and value.strip() and not _untouched(value):
            creds[key] = value.strip()
    row.credentials = creds
    row.save()
    rbac.audit(request, "integration.create", target=f"{provider}:{row.name}")
    rbac.notify(("superadmin", "admin"), "system",
                f"Integration added: {row.name}",
                f"{meta['label']} — by {_requester(request)}", health.SETTINGS_LINK)
    return JsonResponse(_payload(row))


@csrf_exempt
def account_detail(request, account_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    row = Integration.objects.filter(pk=account_id).first()
    if row is None:
        return JsonResponse({"detail": "Integration not found."}, status=404)
    if request.method == "GET":
        return JsonResponse(_payload(row))
    if request.method not in ("PATCH", "POST", "DELETE"):
        return HttpResponseNotAllowed(["GET", "PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage integrations."},
                            status=403)
    if request.method == "DELETE":
        label = f"{row.provider}:{row.name}"
        row.delete()
        rbac.audit(request, "integration.delete", target=label)
        return JsonResponse({"ok": True})

    body = _json_body(request)
    fields = []
    if isinstance(body.get("name"), str) and body["name"].strip():
        row.name = body["name"].strip()[:120]
        fields.append("name")
    if isinstance(body.get("external_id"), str):
        row.external_id = body["external_id"].strip()[:190]
        fields.append("external_id")
    if isinstance(body.get("config"), dict):
        merged = dict(row.config or {})
        merged.update({k: v for k, v in body["config"].items()})
        row.config = merged
        fields.append("config")
    creds = dict(row.credentials or {})
    changed_secret = False
    for key in ("api_key", "webhook_secret"):
        if key in body:
            value = body.get(key)
            if _untouched(value):
                continue  # masked round-trip: leave the stored value alone
            creds[key] = str(value or "").strip()
            changed_secret = True
    if changed_secret:
        row.credentials = creds
        fields.append("credentials")
    if fields:
        row.save(update_fields=list(dict.fromkeys(fields)) + ["updated_at"])
    if changed_secret:
        # Same rule as whatsapp.health.reset_on_reconfigure: new credentials
        # invalidate the old verdict, silently (the operator's own action).
        health.set_status(row, health.UNKNOWN, "credentials updated")
    rbac.audit(request, "integration.update", target=f"{row.provider}:{row.name}",
               detail=f"fields: {sorted(set(fields))}")
    return JsonResponse(_payload(row))


@csrf_exempt
def account_test(request, account_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    row = Integration.objects.filter(pk=account_id).first()
    if row is None:
        return JsonResponse({"detail": "Integration not found."}, status=404)
    result = actions.test_connection(row)
    status = 200 if result["ok"] else (400 if result["skipped"] else 502)
    return JsonResponse({**result, "account": _payload(row)}, status=status)


@csrf_exempt
def account_disconnect(request, account_id: int):
    """Forget the stored grant but keep the row (its config + name survive, so
    reconnecting is one click)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage integrations."},
                            status=403)
    row = Integration.objects.filter(pk=account_id).first()
    if row is None:
        return JsonResponse({"detail": "Integration not found."}, status=404)
    oauth.revoke_local(row)
    rbac.audit(request, "integration.disconnect", target=f"{row.provider}:{row.name}")
    return JsonResponse(_payload(row))


# ---- OAuth ------------------------------------------------------------------------

def oauth_start(request, provider: str):
    """Returns {authorize_url} for the dashboard to navigate to.

    Sandbox (no Google client configured) -> 400 with the operator-facing
    reason. Nothing is attempted over the network."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to connect an integration."},
                            status=403)
    if provider not in catalog.PROVIDERS:
        return JsonResponse({"detail": "Unknown provider."}, status=404)
    row = None
    raw_id = str(request.GET.get("integration_id") or "").strip()
    if raw_id.isdigit():
        row = Integration.objects.filter(pk=int(raw_id), provider=provider).first()
        if row is None:
            return JsonResponse({"detail": "Integration not found."}, status=404)
    if row is None:
        row = Integration.objects.create(
            provider=provider, name=catalog.PROVIDERS[provider]["label"],
            created_by=_requester(request), tenant=rbac.tenant_from_request(request))
    try:
        url = oauth.authorize_url(row, request)
    except oauth.OAuthError as e:
        return JsonResponse({"detail": str(e), "integration_id": row.pk}, status=400)
    return JsonResponse({"authorize_url": url, "integration_id": row.pk})


@csrf_exempt
def oauth_callback(request, provider: str):
    """Google redirects the BROWSER here — no bearer token can be present, so
    the signed+expiring `state` is the CSRF control. The path's `provider` is
    cosmetic: the state decides which row this grant belongs to."""
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    error = str(request.GET.get("error") or "").strip()
    state = str(request.GET.get("state") or "")
    code = str(request.GET.get("code") or "")
    wants_json = request.GET.get("format") == "json"

    def done(ok: bool, detail: str, row=None):
        if wants_json:
            return JsonResponse({"ok": ok, "detail": detail,
                                 "integration_id": row.pk if row else None},
                                status=200 if ok else 400)
        from urllib.parse import urlencode
        query = urlencode({("connected" if ok else "error"): detail})
        return HttpResponseRedirect(f"{catalog.dashboard_url()}?{query}")

    if error:
        return done(False, f"Google returned: {error}")
    try:
        data = oauth.verify_state(state)
    except oauth.OAuthError as e:
        return done(False, str(e))
    row = Integration.objects.filter(pk=data["i"], provider=data["p"]).first()
    if row is None:
        return done(False, "That integration no longer exists.")
    if not code:
        return done(False, "Google returned no authorization code.", row)
    try:
        oauth.exchange_code(row, code, request)
    except oauth.OAuthError as e:
        health.set_status(row, health.DISCONNECTED, str(e)[:200])
        return done(False, str(e), row)
    rbac.audit(None, "integration.connect", target=f"{row.provider}:{row.name}",
               detail="OAuth grant stored")
    return done(True, row.name, row)


# ---- global settings ---------------------------------------------------------------

def _settings_payload() -> dict:
    effective = catalog.settings()
    stored = catalog._stored()
    out = {}
    for key in catalog.GLOBAL_KEYS:
        if key in catalog.SECRET_KEYS:
            out[f"{key}_masked"] = _mask(effective[key])
        else:
            out[key] = effective[key]
        out.setdefault("source", {})[key] = (
            "settings" if str(stored.get(key) or "").strip()
            else "env" if effective[key] else "")
    out["google_oauth_configured"] = catalog.google_oauth_configured()
    out["redirect_uri"] = catalog.redirect_uri()
    out["webhook_paths"] = {p: m.get("webhook_path", "")
                            for p, m in catalog.PROVIDERS.items()
                            if m.get("webhook_path")}
    return out


@csrf_exempt
def settings_view(request):
    """GET = any authenticated role (secrets masked); PUT = SUPER ADMIN, the
    same rule as every other global app setting."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_settings_payload())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _is_superadmin(request):
        return JsonResponse(
            {"detail": "Super admin role required to change integration settings."},
            status=403)
    body = _json_body(request)
    patch = {}
    for key in catalog.GLOBAL_KEYS:
        value = body.get(key)
        if isinstance(value, str) and not _untouched(value):
            patch[key] = value.strip()
    if not patch:
        return JsonResponse({"detail": "Nothing to update."}, status=400)
    catalog.save_settings(patch)
    rbac.audit(request, "settings.update", target="integrations",
               detail=f"keys: {sorted(patch)}")
    return JsonResponse(_settings_payload())


# ---- webhooks (OPEN routes — HMAC is the authentication) ---------------------------

@csrf_exempt
def shopify_webhook(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    row = webhooks.shopify_integration(request)
    try:
        body = webhooks.verify_shopify(request, row)
    except webhooks.WebhookError as e:
        return JsonResponse({"detail": str(e)}, status=e.status)
    try:
        payload = json.loads(body or b"{}")
    except ValueError:
        return JsonResponse({"detail": "Malformed JSON body."}, status=400)
    if not isinstance(payload, dict):
        return JsonResponse({"detail": "Malformed JSON body."}, status=400)
    topic = str(request.headers.get("X-Shopify-Topic", "")).strip()
    event_id = str(request.headers.get("X-Shopify-Webhook-Id", "")).strip()
    if row is not None:
        health.record_ok(row)
    if not topic.startswith("orders"):
        return JsonResponse({"ok": True, "ignored": topic or "unknown topic"})
    try:
        result = mapping.shopify_order(payload, topic=topic, event_id=event_id)
    except Exception:
        # Never let a mapping bug turn into a retry storm from Shopify.
        logger.exception("integrations: shopify order mapping failed")
        return JsonResponse({"ok": False, "detail": "mapping failed"}, status=200)
    return JsonResponse({"ok": True, **result})


@csrf_exempt
def stripe_webhook(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    row = webhooks.stripe_integration(request)
    try:
        body = webhooks.verify_stripe(request, row)
    except webhooks.WebhookError as e:
        return JsonResponse({"detail": str(e)}, status=e.status)
    try:
        event = json.loads(body or b"{}")
    except ValueError:
        return JsonResponse({"detail": "Malformed JSON body."}, status=400)
    if not isinstance(event, dict):
        return JsonResponse({"detail": "Malformed JSON body."}, status=400)
    if row is not None:
        health.record_ok(row)
    kind = str(event.get("type") or "")
    if kind != "payment_intent.succeeded":
        return JsonResponse({"ok": True, "ignored": kind or "unknown event"})
    try:
        result = mapping.stripe_payment(event)
    except Exception:
        logger.exception("integrations: stripe payment mapping failed")
        return JsonResponse({"ok": False, "detail": "mapping failed"}, status=200)
    return JsonResponse({"ok": True, **result})
