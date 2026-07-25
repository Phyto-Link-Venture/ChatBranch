# backend/calls/voice_settings.py
"""Global STT provider preference for the ElevenLabs voice pipeline branch --
"openai" (gpt-4o-transcribe) or "elevenlabs" (Scribe v2 Realtime, ~150ms
first-partial but shares ELEVENLABS_API_KEY/quota with TTS). File-backed like
pronunciation.py (data/voice_settings.json, gitignored); read fresh at
dial-time and threaded into dispatch metadata so a Settings-page change takes
effect on the next call with no voice_agent worker restart needed.
"""
import json
import os
from pathlib import Path

_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
VOICE_SETTINGS_FILE = _DATA_DIR / "voice_settings.json"

# "ilmu" = YTL's ilmu-asr-v4.2 (Malaysian-trained; en/ms/zh hints) — strongest
# candidate for Malay/Manglish contacts; A/B against Scribe per deployment.
STT_PROVIDERS = ("openai", "elevenlabs", "ilmu")
DEFAULT_STT_PROVIDER = "elevenlabs"

# Conversation brain for pipeline (ElevenLabs-voice) calls. All four speak the
# OpenAI chat-completions shape, so the agent swaps only base_url/key/model.
# Native realtime calls (OpenAI voice) ignore this — their brain IS the
# realtime model. Default comes from env so .env deployments keep working.
LLM_PROVIDERS = ("openai", "ilmu", "openrouter", "gemini")
_env_llm = os.getenv("PIPELINE_LLM_PROVIDER", "").lower()
DEFAULT_LLM_PROVIDER = _env_llm if _env_llm in LLM_PROVIDERS else "openai"


def _read() -> dict:
    try:
        with open(VOICE_SETTINGS_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def _write(state: dict) -> None:
    VOICE_SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = str(VOICE_SETTINGS_FILE) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
    os.replace(tmp, VOICE_SETTINGS_FILE)


def get_stt_provider() -> str:
    return _read().get("stt_provider", DEFAULT_STT_PROVIDER)


def save_stt_provider(provider: str) -> str:
    if provider not in STT_PROVIDERS:
        raise ValueError(f"stt_provider must be one of {STT_PROVIDERS}")
    state = _read()
    state["stt_provider"] = provider
    _write(state)
    return provider


def get_llm_provider() -> str:
    return _read().get("llm_provider", DEFAULT_LLM_PROVIDER)


def save_llm_provider(provider: str) -> str:
    if provider not in LLM_PROVIDERS:
        raise ValueError(f"llm_provider must be one of {LLM_PROVIDERS}")
    state = _read()
    state["llm_provider"] = provider
    _write(state)
    return provider
