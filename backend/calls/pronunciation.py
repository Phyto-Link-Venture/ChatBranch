# backend/calls/pronunciation.py
"""Pronunciation dictionary: term -> respelling (alias) overrides applied to
ElevenLabs TTS. Not real IPA -- eleven_multilingual_v2 (this app's model,
needed for ms/zh) only supports "alias" rules, not "phoneme"/IPA rules.

File-backed like voices.py (data/pronunciation.json, gitignored). ElevenLabs
voices only -- OpenAI's TTS API has no pronunciation-control endpoint.
"""
import json
import os
from pathlib import Path

_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
PRONUNCIATION_FILE = _DATA_DIR / "pronunciation.json"


def _read() -> dict:
    try:
        with open(PRONUNCIATION_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {"terms": [], "dictionary_id": None, "version_id": None}


def _write(state: dict) -> None:
    PRONUNCIATION_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = str(PRONUNCIATION_FILE) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
    os.replace(tmp, PRONUNCIATION_FILE)


def load_terms() -> list[dict]:
    return _read().get("terms", [])


def save_terms(terms: list[dict], dictionary_id: str | None, version_id: str | None) -> dict:
    state = {"terms": terms, "dictionary_id": dictionary_id, "version_id": version_id}
    _write(state)
    return state


def dictionary_locator() -> dict | None:
    state = _read()
    if state.get("dictionary_id") and state.get("version_id"):
        return {"pronunciation_dictionary_id": state["dictionary_id"], "version_id": state["version_id"]}
    return None
