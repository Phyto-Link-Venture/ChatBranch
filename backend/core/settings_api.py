"""Runtime app-settings REST API (under /api/settings/).

  GET  /api/settings/ai-providers  -> effective AI keys/selectors/tunables,
                                      secrets MASKED, plus a self-describing
                                      `fields` schema the frontend renders from.
  PUT  /api/settings/ai-providers  -> SUPER ADMIN only. Masked round-trip (a
                                      value still containing "••" is treated as
                                      untouched), writes each field to its
                                      owning Setting store, audits
                                      `settings.update`.

AUTH mirrors whatsapp/views.py + integrations/views.py exactly: GET needs any
bearer role (secrets masked); PUT is superadmin, the same rule as every other
GLOBAL app setting. Nothing here reaches the Django-free voice agent — see
core/app_settings for the env boundary.
"""
from __future__ import annotations

import json
import re

from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac

from . import app_settings

# Self-describing field schema — the frontend builds the AI & Providers tab from
# this (label + kind + which are masked + dropdown options), so a new field only
# needs a row here plus a getter in app_settings.
FIELD_META = [
    {"key": "openai_api_key", "label": "OpenAI API key", "group": "providers", "kind": "secret"},
    {"key": "openrouter_api_key", "label": "OpenRouter API key", "group": "providers", "kind": "secret"},
    {"key": "google_api_key", "label": "Google Gemini API key", "group": "providers", "kind": "secret"},
    {"key": "ilmu_api_key", "label": "ILMU API key (chat)", "group": "providers", "kind": "secret",
     "help": "Used for AI chat/summaries. Live voice calls read the key from the agent's .env."},
    {"key": "elevenlabs_api_key", "label": "ElevenLabs API key", "group": "providers", "kind": "secret",
     "help": "Voice listing / cloning / preview / TTS. Live voice calls read the key from the agent's .env."},
    {"key": "chat_provider_pref", "label": "Chat AI provider", "group": "providers", "kind": "select"},
    {"key": "chat_provider_order", "label": "Provider fallback order", "group": "providers", "kind": "order",
     "help": "Only used in Automatic mode — the priority the chat brain tries providers "
             "in, picking the first with a key set."},
    {"key": "chat_model", "label": "Chat model", "group": "providers", "kind": "model",
     "help": "Applies to whichever provider wins. Empty = that provider's default model; "
             "pick Custom… to enter any model id."},
    {"key": "email_subject", "label": "Default email subject", "group": "tunables", "kind": "text"},
    {"key": "flows_tick_seconds", "label": "Flow engine tick (seconds)", "group": "tunables", "kind": "int", "min": 1},
    {"key": "whatsapp_health_interval", "label": "Channel health check interval (seconds)",
     "group": "tunables", "kind": "int", "min": 30},
    {"key": "aicore_summary_interval", "label": "Idle-chat summariser interval (seconds)",
     "group": "tunables", "kind": "int", "min": 5,
     "help": "Read when the summariser thread starts — a change applies on the next restart."},
    {"key": "aicore_summary_idle_min", "label": "Idle-chat threshold (minutes)",
     "group": "tunables", "kind": "int", "min": 1},
]

_INT_MIN = {f["key"]: f["min"] for f in FIELD_META if f["kind"] == "int"}
_SECRET_KEYS = tuple(f["key"] for f in FIELD_META if f["kind"] == "secret")
# Free-text/select/model strings share the masked round-trip handling; the
# `order` field is a list, validated + parsed separately below.
_STR_KEYS = tuple(f["key"] for f in FIELD_META if f["kind"] in ("text", "select", "model"))


# ---- request helpers (identical pattern to integrations/views.py) ------------

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


def _is_superadmin(request) -> bool:
    return rbac.role_from_request(request) == "superadmin"


def _mask(secret: str) -> str:
    if not secret:
        return ""
    return f"••••{secret[-4:]}" if len(secret) >= 8 else "••••"


def _untouched(value) -> bool:
    """A masked placeholder round-tripped from a GET — keep the stored secret."""
    return isinstance(value, str) and "••" in value


def _order_members(raw) -> list[str] | None:
    """Flatten a chat_provider_order body value (a list OR a JSON/CSV string)
    to lowercased tokens, or None when the shape is unusable. Validation of the
    members against the known providers happens in the PUT handler."""
    if isinstance(raw, list):
        items: list = raw
    elif isinstance(raw, str):
        s = raw.strip()
        items = []
        if s.startswith("["):
            try:
                parsed = json.loads(s)
            except ValueError:
                parsed = None
            if isinstance(parsed, list):
                items = parsed
        if not items:
            items = [t for t in re.split(r"[,\s]+", s) if t]
    else:
        return None
    return [str(x).strip().lower() for x in items if str(x).strip()]


# ---- serialization -----------------------------------------------------------

def _payload() -> dict:
    values = app_settings.effective()
    src = app_settings.sources()
    out: dict = {"fields": FIELD_META,
                 "chat_provider_options": app_settings.chat_provider_options(),
                 "chat_model_options": app_settings.chat_model_options(),
                 "source": src}
    for f in FIELD_META:
        key = f["key"]
        if f["kind"] == "secret":
            out[f"{key}_masked"] = _mask(str(values.get(key) or ""))
        else:
            out[key] = values.get(key)
    out["ai_available"] = app_settings.ai_available()
    # Honest boundary note for the UI: dual-use keys drive Django only.
    out["env_only_note"] = (
        "Live voice calls read ILMU_API_KEY / ELEVENLABS_API_KEY / "
        "PIPELINE_LLM_PROVIDER from the voice-agent's .env at call time — the "
        "agent process is Django-free and cannot read these settings. Keys here "
        "power AI chat, summaries and Django-side voice tooling."
    )
    return out


# ---- view --------------------------------------------------------------------

@csrf_exempt
def ai_providers(request):
    """GET = any authenticated role (secrets masked); PUT = SUPER ADMIN."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_payload())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _is_superadmin(request):
        return JsonResponse(
            {"detail": "Super admin role required to change AI & provider settings."},
            status=403)

    body = _json_body(request)
    patch: dict = {}

    # secrets + free-text/select strings: honour the masked round-trip
    for key in _SECRET_KEYS + _STR_KEYS:
        value = body.get(key)
        if isinstance(value, str) and not _untouched(value):
            patch[key] = value.strip()

    # chat provider pin must be a known provider or "" (automatic)
    if "chat_provider_pref" in patch:
        pref = patch["chat_provider_pref"].lower()
        options = app_settings.chat_provider_options()
        if pref and pref not in options:
            return JsonResponse(
                {"detail": "chat_provider_pref must be one of "
                           f"{', '.join(o for o in options if o)} or empty (auto)."},
                status=400)
        patch["chat_provider_pref"] = pref

    # provider fallback order: a list (or JSON/CSV string) of KNOWN providers.
    if "chat_provider_order" in body:
        members = _order_members(body["chat_provider_order"])
        if members is None:
            return JsonResponse(
                {"detail": "chat_provider_order must be a list of provider keys."},
                status=400)
        known = {o for o in app_settings.chat_provider_options() if o}
        invalid = sorted({m for m in members if m not in known})
        if invalid:
            return JsonResponse(
                {"detail": f"chat_provider_order has unknown providers: "
                           f"{', '.join(invalid)}. Allowed: {', '.join(sorted(known))}."},
                status=400)
        deduped: list[str] = []
        for m in members:
            if m not in deduped:
                deduped.append(m)
        patch["chat_provider_order"] = deduped

    # integer tunables: parse + enforce the safe minimum
    for key, minimum in _INT_MIN.items():
        if key in body:
            raw = body.get(key)
            if raw in ("", None):
                continue  # nothing to change
            try:
                num = int(raw)
            except (TypeError, ValueError):
                return JsonResponse({"detail": f"{key} must be an integer."}, status=400)
            if num < minimum:
                return JsonResponse(
                    {"detail": f"{key} must be at least {minimum}."}, status=400)
            patch[key] = num

    if not patch:
        return JsonResponse({"detail": "Nothing to update."}, status=400)

    app_settings.save(patch)
    rbac.audit(request, "settings.update", target="ai-providers",
               detail=f"keys: {sorted(patch)}")
    rbac.notify(("superadmin",), "system", "AI & provider settings updated",
                f"Changed by {rbac.requester_name(request)}: "
                f"{', '.join(sorted(patch)) or 'nothing'}",
                "/settings/ai-providers")
    return JsonResponse(_payload())
