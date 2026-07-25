"""WhatsApp + AI configuration — DATABASE-BACKED (crm.Setting /
crm.ChannelAccount), same function surface as before.

Two layers, like Whatomate's account settings: backend/.env values are the
DEFAULTS; rows saved from the dashboard override them. Clearing a dashboard
value falls back to env. Accounts are Whatomate's multi-account model — any
number of WhatsApp Business numbers, each a ChannelAccount row; `WHATSAPP_*`
env keys surface as a read-only 'env-default' pseudo-account for back-compat.

SANDBOX MODE: no configured account -> messages/calls stored locally only.

NOTE for non-Django processes (whatsapp/bridge.py): call django.setup()
BEFORE importing this module.
"""
import json
import os
import secrets as _secrets

GRAPH_BASE_URL = "https://graph.facebook.com"

# The value shipped in the repo/docs — anyone can read it, so it must never be
# the token a live webhook actually verifies against (see verify_token()).
DEFAULT_VERIFY_TOKEN = "chatbranch-verify"

_GLOBAL_ENV = {
    # Any string you choose; must match what you type into the Meta webhook form.
    "verify_token": os.getenv("WHATSAPP_VERIFY_TOKEN", DEFAULT_VERIFY_TOKEN),
    "chat_model": os.getenv("WHATSAPP_CHAT_MODEL", ""),
    # AI provider keys for text auto-replies. NOTE: the voice agent worker
    # reads its own keys from .env at process start — these do not affect it.
    "openai_api_key": os.getenv("OPENAI_API_KEY", ""),
    "openrouter_api_key": os.getenv("OPENROUTER_API_KEY", ""),
    # Google Gemini via its OpenAI-compatible endpoint. Shares the .env key
    # with the voice agent's Gemini branch but is otherwise independent.
    "google_api_key": os.getenv("GOOGLE_API_KEY", ""),
    # YTL ILMU (Malaysian sovereign AI, api.ilmu.ai — OpenAI-compatible).
    # Shares the .env key with the voice agent's ilmu pipeline branch.
    "ilmu_api_key": os.getenv("ILMU_API_KEY", ""),
    # Explicit chat-provider pick for A/B testing ("ilmu"/"openrouter"/
    # "openai"/"google"); empty = automatic priority (see chat_provider()).
    "chat_provider_pref": os.getenv("CHAT_PROVIDER", ""),
    # Collections quiet hours, "HH:MM" 24h strings in SERVER LOCAL time
    # (deployment assumption: Malaysia). Empty = disabled. During quiet hours
    # AI auto-replies are skipped and template sends are rejected; a human
    # operator may still send manually.
    "quiet_start": os.getenv("WHATSAPP_QUIET_START", ""),
    "quiet_end": os.getenv("WHATSAPP_QUIET_END", ""),
}

GLOBAL_KEYS = tuple(_GLOBAL_ENV)
SECRET_GLOBAL_KEYS = ("openai_api_key", "openrouter_api_key", "google_api_key",
                      "ilmu_api_key")

ILMU_BASE_URL = os.getenv("ILMU_BASE_URL", "https://api.ilmu.ai/v1")
CHAT_PROVIDERS = ("ilmu", "openrouter", "openai", "google")

ACCOUNT_KEYS = ("name", "phone_number_id", "waba_id", "access_token", "app_secret",
                "api_version", "owner", "provider")

# Operating role (client model): the Cloud business number is the compliant
# "reminder cannon" (templates with CTA buttons pushing contacts to chat), the
# Baileys personal numbers are the "conversation" layer where AI negotiation
# happens — always contact-initiated. Stored in ChannelAccount.config["role"];
# unset defaults by provider (cloud → reminder, baileys → conversation).
ROLE_REMINDER = "reminder"
ROLE_CONVERSATION = "conversation"
ACCOUNT_ROLES = (ROLE_REMINDER, ROLE_CONVERSATION)

# Baileys gateway (backend/baileys_service, `npm run dev:baileys`) — a local
# Node sidecar speaking WhatsApp Web. Django is its only client.
BAILEYS_URL = os.getenv("BAILEYS_URL", "http://127.0.0.1:3020")
BAILEYS_SECRET = os.getenv("BAILEYS_SECRET", "baileys-local-dev")
SECRET_ACCOUNT_KEYS = ("access_token", "app_secret")

ENV_ACCOUNT_ID = "env-default"


# ---- global settings (Setting rows override env) --------------------------------

def global_settings() -> dict:
    from crm.models import Setting

    merged = dict(_GLOBAL_ENV)
    for row in Setting.objects.filter(key__in=GLOBAL_KEYS):
        if row.value.strip():
            merged[row.key] = row.value.strip()
    return merged


def save_global_settings(patch: dict) -> dict:
    from crm.models import Setting

    for k in GLOBAL_KEYS:
        if k in patch and isinstance(patch[k], str):
            Setting.objects.update_or_create(key=k, defaults={"value": patch[k].strip()})
    return global_settings()


# ---- sandbox custom templates ------------------------------------------------------
# Templates created from Settings → Message Templates while NO live WABA is
# linked. They live in one Setting row so the whole template flow (list, send
# with {{n}} rendering) works offline; a live account uses Meta's registry.

CUSTOM_TEMPLATES_KEY = "whatsapp_custom_templates"


def custom_templates() -> list[dict]:
    from crm.models import Setting

    row = Setting.objects.filter(key=CUSTOM_TEMPLATES_KEY).first()
    if not row or not row.value.strip():
        return []
    try:
        data = json.loads(row.value)
    except ValueError:
        return []
    return data if isinstance(data, list) else []


def save_custom_templates(templates: list[dict]) -> None:
    from crm.models import Setting

    Setting.objects.update_or_create(
        key=CUSTOM_TEMPLATES_KEY, defaults={"value": json.dumps(templates)})


# Per-template variable bindings: {scope: {template_name: [crm_key_or_empty,
# …]}} — one entry per {{n}} slot, keyed to the flows-engine variable catalog.
# scope = the owning account's id ("sandbox" when no live account), because
# template names are per-WABA and the app supports multiple accounts. Meta has
# no place to store this, so it lives locally.

TEMPLATE_BINDINGS_KEY = "whatsapp_template_bindings"


def _all_template_bindings() -> dict:
    from crm.models import Setting

    row = Setting.objects.filter(key=TEMPLATE_BINDINGS_KEY).first()
    if not row or not row.value.strip():
        return {}
    try:
        data = json.loads(row.value)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def template_bindings(scope: str) -> dict:
    """{template_name: bindings} for one account scope."""
    data = _all_template_bindings().get(str(scope or "sandbox"))
    return data if isinstance(data, dict) else {}


def save_template_bindings(scope: str, mapping: dict) -> None:
    from crm.models import Setting

    data = _all_template_bindings()
    data[str(scope or "sandbox")] = mapping
    Setting.objects.update_or_create(
        key=TEMPLATE_BINDINGS_KEY, defaults={"value": json.dumps(data)})


# ---- WhatsApp account registry ----------------------------------------------------

def _env_account() -> dict | None:
    token = os.getenv("WHATSAPP_ACCESS_TOKEN", "")
    pnid = os.getenv("WHATSAPP_PHONE_NUMBER_ID", "")
    if not (token and pnid):
        return None
    return {
        "id": ENV_ACCOUNT_ID,
        "name": "From backend/.env",
        "provider": "cloud",
        "role": ROLE_REMINDER,
        "phone_number_id": pnid,
        "waba_id": os.getenv("WHATSAPP_WABA_ID", ""),
        "access_token": token,
        "app_secret": os.getenv("WHATSAPP_APP_SECRET", ""),
        "api_version": os.getenv("WHATSAPP_API_VERSION", "v23.0"),
        "owner": "superadmin",
        "created": "",
    }


def account_role(account) -> str:
    """The account's operating role — "reminder" | "conversation". Accepts a
    crm.ChannelAccount row OR a serialized account dict. Unset/invalid values
    default by provider: cloud → reminder, baileys → conversation."""
    if account is None:
        return ROLE_REMINDER
    if isinstance(account, dict):
        role = str(account.get("role") or "").strip().lower()
        provider = str(account.get("provider") or "cloud")
    else:
        role = str((getattr(account, "config", None) or {}).get("role") or "").strip().lower()
        provider = str(getattr(account, "provider", "") or "cloud")
    if role in ACCOUNT_ROLES:
        return role
    return ROLE_CONVERSATION if provider == "baileys" else ROLE_REMINDER


def _serialize(a) -> dict:
    return {
        "id": str(a.pk),
        "provider": a.provider or "cloud",
        "role": account_role(a),
        "name": a.name,
        "phone_number_id": a.external_id,
        "waba_id": a.waba_id,
        "access_token": a.access_token,
        "app_secret": a.app_secret,
        "api_version": a.api_version or "v23.0",
        "owner": a.owner,
        "created": a.created_at.isoformat() if a.created_at else "",
    }


def list_accounts() -> list[dict]:
    from crm.models import ChannelAccount

    accounts = [_serialize(a) for a in ChannelAccount.objects.filter(channel="whatsapp")]
    env_acct = _env_account()
    if env_acct:
        accounts.append(env_acct)
    return accounts


def get_account(account_id: str | None = None) -> dict | None:
    """Look up an account by id. Without an id (or when the id matches
    nothing — e.g. a deleted account), fall back to the sole CONFIGURED
    account if exactly one exists, else None. Never guess between multiple
    accounts: a wrong pick would send messages from the wrong business
    number."""
    accounts = list_accounts()
    if account_id:
        for a in accounts:
            if a["id"] == str(account_id):
                return a
    configured = [a for a in accounts if account_configured(a)]
    return configured[0] if len(configured) == 1 else None


def find_account_by_phone_number_id(pnid: str) -> dict | None:
    for a in list_accounts():
        if a.get("phone_number_id") == str(pnid):
            return a
    return None


def create_account(data: dict) -> dict:
    from crm.models import ChannelAccount

    provider = str(data.get("provider") or "cloud").strip().lower()
    role = str(data.get("role") or "").strip().lower()
    a = ChannelAccount.objects.create(
        channel="whatsapp",
        provider=provider if provider in ("cloud", "baileys") else "cloud",
        name=str(data.get("name") or "WhatsApp account").strip(),
        external_id=str(data.get("phone_number_id") or "").strip(),
        waba_id=str(data.get("waba_id") or "").strip(),
        access_token=str(data.get("access_token") or "").strip(),
        app_secret=str(data.get("app_secret") or "").strip(),
        api_version=str(data.get("api_version") or "v23.0").strip(),
        owner=str(data.get("owner") or "admin").strip(),
        config={"role": role} if role in ACCOUNT_ROLES else {},
    )
    return _serialize(a)


def update_account(account_id: str, patch: dict) -> dict | None:
    from crm.models import ChannelAccount

    if account_id == ENV_ACCOUNT_ID or not str(account_id).isdigit():
        return None  # env-managed — edit backend/.env or add a dashboard account
    a = ChannelAccount.objects.filter(channel="whatsapp", pk=int(account_id)).first()
    if a is None:
        return None
    field_map = {"phone_number_id": "external_id"}
    creds_changed = False
    for k in ACCOUNT_KEYS:
        if k in patch and isinstance(patch[k], str):
            attr = field_map.get(k, k)
            if getattr(a, attr, "") != patch[k].strip() and \
                    k in ("access_token", "phone_number_id", "app_secret",
                          "api_version", "provider", "waba_id"):
                creds_changed = True
            setattr(a, attr, patch[k].strip())
    role = str(patch.get("role") or "").strip().lower() if "role" in patch else ""
    if role in ACCOUNT_ROLES:
        # role lives in the config JSON; changing it never needs a health reset
        a.config = {**(a.config or {}), "role": role}
    a.save()
    if creds_changed:
        # A reconnected account must become routable NOW — otherwise a stale
        # "disconnected" verdict keeps failing sends over to other accounts.
        from . import health
        health.reset_on_reconfigure(str(a.pk))
    return _serialize(a)


def delete_account(account_id: str) -> bool:
    from crm.models import ChannelAccount

    if account_id == ENV_ACCOUNT_ID or not str(account_id).isdigit():
        return False
    deleted, _ = ChannelAccount.objects.filter(channel="whatsapp", pk=int(account_id)).delete()
    return bool(deleted)


def account_configured(account: dict | None) -> bool:
    if not account:
        return False
    if account.get("provider") == "baileys":
        return True  # live-ness is enforced by the gateway at send time
    return bool(account.get("access_token") and account.get("phone_number_id"))


# ---- convenience accessors ----------------------------------------------------------

def verify_token() -> str:
    """The effective webhook verify token — never the shipped default.

    If the merged value is still DEFAULT_VERIFY_TOKEN (i.e. neither env nor a
    Setting row overrides it), generate a random secret ONCE and persist it as
    the Setting row. Operators read the live value from Settings → App Keys
    and paste it into Meta's webhook form (see SETUP.md)."""
    token = global_settings()["verify_token"]
    if token == DEFAULT_VERIFY_TOKEN:
        token = _ensure_generated_verify_token()
    return token


def _ensure_generated_verify_token() -> str:
    """Create (or reuse) the auto-generated verify-token Setting row."""
    from crm.models import Setting

    row, _ = Setting.objects.get_or_create(
        key="verify_token", defaults={"value": _secrets.token_urlsafe(24)}
    )
    if not row.value.strip():  # blank row would fall back to the default — fill it
        row.value = _secrets.token_urlsafe(24)
        row.save(update_fields=["value"])
    return row.value.strip()


def chat_model() -> str:
    return global_settings()["chat_model"]


def openai_key() -> str:
    return global_settings()["openai_api_key"]


def openrouter_key() -> str:
    return global_settings()["openrouter_api_key"]


def google_key() -> str:
    return global_settings()["google_api_key"]


def ilmu_key() -> str:
    return global_settings()["ilmu_api_key"]


def _provider_entry(name: str, model: str) -> tuple[str, str, str] | None:
    """(base_url, key, model) for one named provider — None when its key is unset."""
    if name == "ilmu" and ilmu_key():
        return (ILMU_BASE_URL, ilmu_key(), model or "ilmu-v3.1")
    if name == "openrouter" and openrouter_key():
        return ("https://openrouter.ai/api/v1", openrouter_key(),
                model or "openai/gpt-4o-mini")
    if name == "openai" and openai_key():
        return ("https://api.openai.com/v1", openai_key(), model or "gpt-4o-mini")
    if name == "google" and google_key():
        return ("https://generativelanguage.googleapis.com/v1beta/openai/",
                google_key(), model or "gemini-2.5-flash")
    return None


def chat_provider() -> tuple[str, str, str]:
    """(base_url, api_key, model) for text-AI calls — the ONE place provider
    priority lives. The `chat_provider_pref` setting pins one provider for
    A/B testing (ignored when that provider has no key); otherwise automatic
    priority OpenRouter > OpenAI > ILMU > Google Gemini (all spoken through
    their OpenAI-compatible endpoints). `chat_model` overrides the
    per-provider default model — note it applies to WHICHEVER provider wins,
    so clear it when switching providers unless the model exists on both."""
    model = chat_model()
    pref = global_settings()["chat_provider_pref"].strip().lower()
    if pref in CHAT_PROVIDERS:
        pinned = _provider_entry(pref, model)
        if pinned:
            return pinned
    for name in ("openrouter", "openai", "ilmu"):
        entry = _provider_entry(name, model)
        if entry:
            return entry
    return ("https://generativelanguage.googleapis.com/v1beta/openai/",
            google_key(), model or "gemini-2.5-flash")


def is_configured() -> bool:
    """At least one usable WhatsApp account? Otherwise sandbox mode."""
    return any(account_configured(a) for a in list_accounts())


# ---- quiet hours (collections compliance) -------------------------------------

def parse_hhmm(value: str) -> int | None:
    """'21:30' -> minutes since midnight (1290), or None when invalid/empty."""
    try:
        hours, minutes = str(value).strip().split(":")
        h, m = int(hours), int(minutes)
    except (ValueError, AttributeError):
        return None
    if 0 <= h < 24 and 0 <= m < 60:
        return h * 60 + m
    return None


def in_quiet_hours(now=None) -> bool:
    """Are we inside the configured quiet-hours window right now?

    quiet_start/quiet_end are "HH:MM" in SERVER LOCAL time (the POC assumes
    the server runs on Malaysia time). Either value empty/invalid, or both
    equal, disables the window. Overnight windows work (21:00 -> 08:00).
    """
    s = global_settings()
    start = parse_hhmm(s["quiet_start"])
    end = parse_hhmm(s["quiet_end"])
    if start is None or end is None or start == end:
        return False
    from datetime import datetime

    now = now or datetime.now()  # local server time on purpose (see docstring)
    cur = now.hour * 60 + now.minute
    if start < end:
        return start <= cur < end
    return cur >= start or cur < end  # window crosses midnight


def ai_available() -> bool:
    return bool(openrouter_key() or openai_key() or ilmu_key() or google_key())


# ---- routing rules (client operating model) -------------------------------------
# Setting rows override the shipped defaults (GET/PUT /api/whatsapp/routing-rules).
#   ai_reply_mode     queued (default) | instant — queued = "one human per
#                     account": AI replies on conversation-role accounts go
#                     through the outbound queue (pacing + working hours).
#   business_redirect true (default) — inbound on a reminder-role number with
#                     AI off answers ONCE per 24h with a wa.me pointer to the
#                     contact's conversation number.
#   business_redirect_target conversation (default) | support | template —
#                     what that auto-answer is: a wa.me link to the contact's
#                     assigned conversation number, a human-contact-support
#                     pointer (the reminder number says the line is automated
#                     and names the support contact below), or the operator's
#                     own approved template (business_redirect_template).
#   support_contact   free text included in the support redirect — phone,
#                     email or link ("" = generic wording).
#   business_redirect_template the template name the "template" target sends
#                     ("" = not set → falls back to the conversation wa.me
#                     handoff so the contact still gets an answer).
#   recovery_mode     one_click (default) | auto — what happens when a
#                     conversation account dies: notify + a manual button, or
#                     auto-queue the recovery template to affected contacts.
#   recovery_template the template name used for recovery ("" = not set).
#   cold_text_guard   block (default) | warn | allow — free-text into a thread
#                     with ZERO inbound ever on a conversation-role account.
#   cold_call_guard   block (default) | warn | allow — the same rule for VOICE
#                     calls, kept separate on purpose: an unsolicited call from
#                     an unknown personal number is a stronger abuse signal
#                     than a text, so an operator may run texts on "warn" while
#                     calls stay hard-blocked.

RULES_DEFAULTS = {
    "ai_reply_mode": "queued",
    "business_redirect": "true",
    "business_redirect_target": "conversation",
    "support_contact": "",
    "business_redirect_template": "",
    "recovery_mode": "one_click",
    "recovery_template": "",
    "cold_text_guard": "block",
    "cold_call_guard": "block",
}

RULE_CHOICES = {
    "ai_reply_mode": ("queued", "instant"),
    "business_redirect_target": ("conversation", "support", "template"),
    "recovery_mode": ("one_click", "auto"),
    "cold_text_guard": ("block", "warn", "allow"),
    "cold_call_guard": ("block", "warn", "allow"),
}


def routing_rules() -> dict:
    """Effective routing-rule settings: crm.Setting rows override the shipped
    defaults; invalid stored enum values fall back to the default."""
    from crm.models import Setting

    merged = dict(RULES_DEFAULTS)
    for row in Setting.objects.filter(key__in=RULES_DEFAULTS):
        if row.value.strip():
            merged[row.key] = row.value.strip()
    for key, choices in RULE_CHOICES.items():
        if merged[key].lower() not in choices:
            merged[key] = RULES_DEFAULTS[key]
        else:
            merged[key] = merged[key].lower()
    return merged


def save_routing_rules(patch: dict) -> dict:
    from crm.models import Setting

    for k in RULES_DEFAULTS:
        if k in patch and isinstance(patch[k], str):
            Setting.objects.update_or_create(key=k, defaults={"value": patch[k].strip()})
    return routing_rules()


def ai_reply_mode() -> str:
    return routing_rules()["ai_reply_mode"]


def business_redirect_enabled() -> bool:
    return str(routing_rules()["business_redirect"]).strip().lower() \
        not in ("false", "0", "no", "off", "")


def business_redirect_target() -> str:
    return routing_rules()["business_redirect_target"]


def support_contact() -> str:
    return routing_rules()["support_contact"]


def business_redirect_template() -> str:
    return routing_rules()["business_redirect_template"]


def recovery_mode() -> str:
    return routing_rules()["recovery_mode"]


def recovery_template() -> str:
    return routing_rules()["recovery_template"]


def cold_text_guard() -> str:
    return routing_rules()["cold_text_guard"]


def cold_call_guard() -> str:
    return routing_rules()["cold_call_guard"]
