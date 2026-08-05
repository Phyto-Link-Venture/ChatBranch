"""AI & Providers app settings — DATABASE-BACKED (crm.Setting), env fallback.

The single Django-runtime accessor for the runtime-configurable AI keys,
provider/model selectors and background-ticker tunables. It follows the SAME
Setting-over-env pattern already used by whatsapp/wa_config, messaging/
msg_config, whatsapp/health.voice_settings and integrations/catalog:

  * every getter reads the crm.Setting table LIVE (no module-level caching),
    so an edit from the dashboard applies WITHOUT a process restart;
  * a dashboard-saved Setting row wins; backend/.env is the fallback; a blank
    Setting falls back to env; unset everywhere -> the built-in default;
  * every getter is exception-safe — a broken Settings table must degrade to
    the env value, never break a send/webhook/tick path.

Single source of truth. The chat AI keys + chat model + provider pin already
live as Setting rows owned by whatsapp/wa_config (`openai_api_key`, …,
`chat_model`, `chat_provider_pref`), and the default e-mail subject already
lives in messaging/msg_config (`email_subject`). This module DELEGATES to those
homes rather than minting duplicate rows — there is exactly one `openai_api_key`
Setting in the database, read identically by the chat brain and by the
Django-side voice tooling. Only the ElevenLabs Django-side key and the four
ticker tunables are NEW Setting keys owned here.

The Django-free processes are deliberately NOT covered:

  * the LiveKit voice-agent worker (backend/voice_agent/) reads its OWN env at
    boot/per-call — it cannot read a Setting row. Its use of ILMU_API_KEY /
    ELEVENLABS_API_KEY / PIPELINE_LLM_PROVIDER / REALTIME_* stays env-only.
  * the Baileys Node gateway (backend/baileys_service/) reads env at Node boot.

So for the dual-use keys (ILMU / ElevenLabs) this module powers ONLY the
Django-runtime side (chat replies, summaries, voice listing/cloning/preview/
TTS). A live voice call still reads the key from the agent's .env — see the
call-dispatch pre-flights in calls/views.py and whatsapp/baileys_calls.py,
which intentionally keep reading os.environ.
"""
from __future__ import annotations

import os

# ---- field taxonomy (drives the self-describing GET payload) -----------------

# Secret keys — never returned unmasked over the API.
SECRET_KEYS = ("openai_api_key", "openrouter_api_key", "google_api_key",
               "ilmu_api_key", "local_api_key", "elevenlabs_api_key")

# Chat AI keys + model + provider pin — Setting rows OWNED by whatsapp/wa_config
# (Settings -> App Keys / AI Configuration write the SAME rows; save() routes
# writes back through wa_config). We READ the shared Setting key directly with a
# LIVE os.getenv fallback -> {setting key: env var}. Reading the row directly
# (rather than via wa_config.global_settings, which snapshots env at import)
# keeps the Django-side voice tooling honouring live os.environ, exactly as it
# did before this module existed, while still sharing the one dashboard row.
_WA_STR_ENV = {
    "openai_api_key": "OPENAI_API_KEY",
    "openrouter_api_key": "OPENROUTER_API_KEY",
    "google_api_key": "GOOGLE_API_KEY",
    "ilmu_api_key": "ILMU_API_KEY",
    # Local / self-hosted OpenAI-compatible endpoint (Ollama, LM Studio, vLLM).
    # base_url is the "configured" signal; the key is optional (Ollama ignores it).
    "local_base_url": "LOCAL_BASE_URL",
    "local_api_key": "LOCAL_API_KEY",
    "chat_model": "WHATSAPP_CHAT_MODEL",
    "chat_provider_pref": "CHAT_PROVIDER",
}
_WA_KEYS = tuple(_WA_STR_ENV)

# Default e-mail subject — Setting row OWNED by messaging/msg_config. Delegated.
_MSG_KEYS = ("email_subject",)

# NEW string Setting keys owned HERE -> the .env var each falls back to.
_OWN_STR_ENV = {
    "elevenlabs_api_key": "ELEVENLABS_API_KEY",
}

# NEW integer tunables owned HERE: key -> (default, minimum, env var).
_INT_TUNABLES = {
    "flows_tick_seconds": (10, 1, "FLOWS_TICK_SECONDS"),
    "whatsapp_health_interval": (300, 30, "WHATSAPP_HEALTH_INTERVAL"),
    "aicore_summary_interval": (600, 5, "AICORE_SUMMARY_INTERVAL"),
    "aicore_summary_idle_min": (120, 1, "AICORE_SUMMARY_IDLE_MIN"),
}

# Every key the /api/settings/ai-providers endpoint reads/writes.
ALL_KEYS = _WA_KEYS + _MSG_KEYS + tuple(_OWN_STR_ENV) + tuple(_INT_TUNABLES)


# ---- low-level Setting-over-env helpers --------------------------------------

def _setting_value(key: str) -> str:
    """The stored Setting value for `key`, or "" (blank/missing/DB error)."""
    try:
        from crm.models import Setting
        row = Setting.objects.filter(entity__isnull=True, key=key).first()
        if row and row.value.strip():
            return row.value.strip()
    except Exception:
        pass
    return ""


def _str_over_env(key: str, env: str) -> str:
    """Setting row (dashboard) wins; else backend/.env; else ""."""
    return _setting_value(key) or os.getenv(env, "").strip()


def _int_over_env(key: str) -> int:
    """Typed int tunable: Setting -> env -> default, clamped to a safe minimum."""
    default, minimum, env = _INT_TUNABLES[key]
    raw = _setting_value(key) or os.getenv(env, "").strip()
    try:
        return max(minimum, int(raw or default))
    except (TypeError, ValueError):
        return default


# ---- typed getters: AI provider keys -----------------------------------------
# The chat-brain keys read the SAME `*_api_key` Setting row the chat brain uses
# (wa_config), with a LIVE os.getenv fallback. ElevenLabs is owned here.

def openai_key() -> str:
    return _str_over_env("openai_api_key", "OPENAI_API_KEY")


def openrouter_key() -> str:
    return _str_over_env("openrouter_api_key", "OPENROUTER_API_KEY")


def google_key() -> str:
    return _str_over_env("google_api_key", "GOOGLE_API_KEY")


def ilmu_key() -> str:
    """The CHAT-side ILMU key. Live voice calls read ILMU_API_KEY from the
    agent's .env instead (the agent is Django-free)."""
    return _str_over_env("ilmu_api_key", "ILMU_API_KEY")


def elevenlabs_key() -> str:
    """The DJANGO-side ElevenLabs key — voice listing/cloning/preview/TTS.
    Live voice calls read ELEVENLABS_API_KEY from the agent's .env."""
    return _str_over_env("elevenlabs_api_key", "ELEVENLABS_API_KEY")


# ---- typed getters: provider/model selectors ---------------------------------

def chat_model() -> str:
    return _str_over_env("chat_model", "WHATSAPP_CHAT_MODEL")


def chat_provider_pref() -> str:
    return _str_over_env("chat_provider_pref", "CHAT_PROVIDER")


def ai_available() -> bool:
    """Any chat-capable provider key set (Setting or env)."""
    return bool(openrouter_key() or openai_key() or ilmu_key() or google_key())


def email_subject() -> str:
    """Default outbound e-mail subject (Setting `email_subject`, env
    EMAIL_DEFAULT_SUBJECT). Delegated to messaging/msg_config, its owner."""
    try:
        from messaging import msg_config
        return str(msg_config.email_settings().get("email_subject") or "").strip()
    except Exception:
        return os.getenv("EMAIL_DEFAULT_SUBJECT", "").strip()


# ---- typed getters: background-ticker tunables -------------------------------

def flows_tick_seconds() -> int:
    return _int_over_env("flows_tick_seconds")


def whatsapp_health_interval() -> int:
    return _int_over_env("whatsapp_health_interval")


def aicore_summary_interval() -> int:
    return _int_over_env("aicore_summary_interval")


def aicore_summary_idle_min() -> int:
    return _int_over_env("aicore_summary_idle_min")


# ---- effective values + provenance (for the endpoint) ------------------------

def chat_provider_options() -> list[str]:
    """Values the chat-provider dropdown accepts. "" = automatic priority."""
    try:
        from whatsapp import wa_config
        return ["", *wa_config.CHAT_PROVIDERS]
    except Exception:
        return ["", "ilmu", "openrouter", "openai", "google"]


def chat_provider_order() -> list[str]:
    """The configured auto/priority fallback order (Setting-over-env). Delegated
    to whatsapp/wa_config, its owner (chat_provider() walks the same list)."""
    try:
        from whatsapp import wa_config
        return wa_config.chat_provider_order()
    except Exception:
        return ["openrouter", "openai", "ilmu", "google"]


def chat_model_options() -> list[dict]:
    """Known chat models grouped by provider, for the Settings model dropdown.
    `chat_model` stays a FREE-FORM string underneath — the UI also offers a
    Custom… entry, so any model id outside this catalog still works. Empty =
    the winning provider's built-in default."""
    return [
        {"provider": "openai", "label": "OpenAI",
         "models": ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini"]},
        {"provider": "openrouter", "label": "OpenRouter",
         "models": ["openai/gpt-4o-mini", "openai/gpt-4o",
                    "anthropic/claude-3.5-sonnet",
                    "google/gemini-2.0-flash-001",
                    "meta-llama/llama-3.1-70b-instruct"]},
        {"provider": "google", "label": "Google Gemini",
         "models": ["gemini-2.5-flash", "gemini-2.0-flash",
                    "gemini-1.5-pro", "gemini-1.5-flash"]},
        {"provider": "ilmu", "label": "ILMU",
         "models": ["ilmu-v3.1"]},
        {"provider": "local", "label": "Local (Ollama / LM Studio)",
         "models": ["llama3.1", "llama3.2", "qwen2.5", "mistral", "gemma2"]},
    ]


def effective() -> dict:
    """Every editable value resolved (Setting-over-env). Secrets are the FULL
    values here — the view masks them before they leave the server."""
    out: dict = {}
    for k, env in _WA_STR_ENV.items():
        out[k] = _str_over_env(k, env)
    out["email_subject"] = email_subject()
    out["elevenlabs_api_key"] = elevenlabs_key()
    out["chat_provider_order"] = chat_provider_order()
    for k in _INT_TUNABLES:
        out[k] = _int_over_env(k)
    return out


def _source(key: str) -> str:
    """Where a key's effective value comes from: 'settings' (a dashboard row),
    'env' (backend/.env), or '' (unset / built-in default). Powers the "from
    .env" vs "saved" hint the Settings UI shows next to each field."""
    if _setting_value(key):
        return "settings"
    if key == "chat_provider_order":
        return "env" if os.getenv("CHAT_PROVIDER_ORDER", "").strip() else ""
    if key in _INT_TUNABLES:
        return "env" if os.getenv(_INT_TUNABLES[key][2], "").strip() else ""
    if key in _OWN_STR_ENV:
        return "env" if os.getenv(_OWN_STR_ENV[key], "").strip() else ""
    if key in _MSG_KEYS:
        return "env" if os.getenv("EMAIL_DEFAULT_SUBJECT", "").strip() else ""
    # wa_config-owned keys fall back to their own env vars (the canonical map is
    # _WA_STR_ENV — reuse it so new keys like local_base_url are covered too).
    return "env" if os.getenv(_WA_STR_ENV.get(key, ""), "").strip() else ""


def sources() -> dict:
    return {k: _source(k) for k in (*ALL_KEYS, "chat_provider_order")}


# ---- save (routes each field to its owning store) ----------------------------

def save(patch: dict) -> dict:
    """Persist a partial, already-validated patch. Each key is written to its
    OWNING store so there is never a duplicate row:

      * chat keys / model / provider pin -> whatsapp/wa_config
      * email_subject                    -> messaging/msg_config
      * elevenlabs key + int tunables    -> Setting rows owned here

    Only keys present in `patch` change; "" clears one back to its env fallback.
    Returns the fresh effective() dict."""
    from crm.models import Setting

    wa_patch = {k: patch[k] for k in _WA_KEYS if k in patch and isinstance(patch[k], str)}
    if wa_patch:
        from whatsapp import wa_config
        wa_config.save_global_settings(wa_patch)

    if "chat_provider_order" in patch:
        from whatsapp import wa_config
        wa_config.save_chat_provider_order(patch["chat_provider_order"])

    if "email_subject" in patch and isinstance(patch["email_subject"], str):
        from messaging import msg_config
        msg_config.save_email_settings({"email_subject": patch["email_subject"]})

    for key, env in _OWN_STR_ENV.items():
        if key in patch and isinstance(patch[key], str):
            Setting.objects.update_or_create(entity=None,
                key=key, defaults={"value": patch[key].strip()})

    for key in _INT_TUNABLES:
        if key in patch:
            Setting.objects.update_or_create(entity=None,
                key=key, defaults={"value": str(patch[key]).strip()})

    return effective()
