# backend/calls/voices.py
"""Voice registry: static presets + file-backed cloned voices.

Presets map to their realtime/TTS provider. Cloned voices are created via
ElevenLabs and persisted to data/voices.json (gitignored — consent-sensitive).
This module is import-cheap; provider SDKs are imported lazily in views.
"""
import json
import os
from pathlib import Path

_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
VOICES_FILE = _DATA_DIR / "voices.json"

# Preset voices. marin/cedar/alloy speak via OpenAI TTS; puck is a Gemini
# realtime voice with no TTS speech endpoint (preview disabled in the UI).
PRESETS = [
    {"id": "marin", "name": "Marin", "gender": "Female", "accent": "Neutral EN",
     "language": "en", "persona": "advisor", "status": "ready", "custom": False, "provider": "openai", "tag": "Default"},
    {"id": "cedar", "name": "Cedar", "gender": "Male", "accent": "Neutral EN",
     "language": "en", "persona": "negotiator", "status": "ready", "custom": False, "provider": "openai"},
    {"id": "alloy", "name": "Alloy", "gender": "Neutral", "accent": "US EN",
     "language": "en", "persona": "officer", "status": "ready", "custom": False, "provider": "openai"},
    {"id": "puck", "name": "Puck", "gender": "Male", "accent": "US EN",
     "language": "en", "persona": "counsellor", "status": "ready", "custom": False, "provider": "gemini"},
]


def _read_clones() -> list[dict]:
    try:
        with open(VOICES_FILE, "r", encoding="utf-8") as f:
            return json.load(f).get("voices", [])
    except (FileNotFoundError, json.JSONDecodeError):
        return []


def load_voices() -> list[dict]:
    return list(PRESETS) + _read_clones()


def _write_clones(clones: list[dict]) -> None:
    VOICES_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = str(VOICES_FILE) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"voices": clones}, f, indent=2)
    os.replace(tmp, VOICES_FILE)


def add_voice(record: dict) -> dict:
    clones = _read_clones()
    clones.append(record)
    _write_clones(clones)
    return record


def update_voice(voice_id: str, patch: dict) -> dict | None:
    """Update a custom (cloned/imported) voice's editable fields. Returns the
    updated record, or None if voice_id isn't a custom voice (presets aren't
    stored here and can't be edited)."""
    clones = _read_clones()
    for v in clones:
        if v["id"] == voice_id:
            for key in ("name", "gender", "accent", "age", "description", "language", "persona"):
                if key in patch:
                    v[key] = patch[key]
            _write_clones(clones)
            return v
    return None


def remove_voice(voice_id: str) -> dict | None:
    """Remove a custom voice from the local store. Returns the removed record,
    or None if voice_id isn't a custom voice."""
    clones = _read_clones()
    for i, v in enumerate(clones):
        if v["id"] == voice_id:
            removed = clones.pop(i)
            _write_clones(clones)
            return removed
    return None


def provider_for(voice_id: str) -> str | None:
    for v in load_voices():
        if v["id"] == voice_id:
            return v.get("provider", "elevenlabs" if v.get("custom") else None)
    return None


def eleven_voice_id(voice_id: str) -> str | None:
    for v in _read_clones():
        if v["id"] == voice_id:
            return v.get("elevenVoiceId")
    return None


def default_elevenlabs_voice() -> str | None:
    """First ready cloned/imported ElevenLabs voice — an automatic fallback
    for features that need LITERAL text-to-speech (e.g. a script call reading
    a fixed script verbatim). Native realtime models have no standalone TTS,
    so asking them to "read this exactly" via generate_reply is unreliable
    (observed producing no audible speech) — pipeline voices are the only
    dependable path, so pick one rather than silently falling through."""
    return next(
        (v["id"] for v in load_voices()
         if v.get("provider") == "elevenlabs" and v.get("status") == "ready"),
        None,
    )


def get_custom_voice(voice_id: str) -> dict | None:
    for v in _read_clones():
        if v["id"] == voice_id:
            return v
    return None


def find_by_eleven_id(eleven_voice_id: str) -> dict | None:
    for v in _read_clones():
        if v.get("elevenVoiceId") == eleven_voice_id:
            return v
    return None
