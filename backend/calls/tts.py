# backend/calls/tts.py
"""Text-to-speech generation + on-disk cache.

Provider SDKs (openai, httpx) are imported lazily so importing this module
stays cheap. Each (provider, voice, model, text) tuple caches to one mp3.
"""
import hashlib
import os
from pathlib import Path

from core import app_settings

_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
_CACHE_DIR = _DATA_DIR / "voice_cache"

OPENAI_TTS_MODEL = "gpt-4o-mini-tts"
# OpenAI speech voices supported by this account. marin is confirmed (live
# REALTIME_VOICE). If a preset name is rejected by the API, fall back to alloy.
_OPENAI_FALLBACK = "alloy"


def cache_path(provider: str, voice_key: str, model: str, text: str) -> Path:
    key = f"{provider}:{voice_key}:{model}:{text}"
    digest = hashlib.sha256(key.encode("utf-8")).hexdigest()[:16]
    return _CACHE_DIR / f"{digest}.mp3"


def read_cache(path: Path) -> bytes | None:
    try:
        return path.read_bytes()
    except FileNotFoundError:
        return None


def write_cache(path: Path, data: bytes) -> None:
    try:
        _CACHE_DIR.mkdir(parents=True, exist_ok=True)
        tmp = str(path) + ".tmp"
        with open(tmp, "wb") as f:
            f.write(data)
        os.replace(tmp, path)
    except OSError:
        pass  # cache is best-effort; still return the bytes to the caller


def openai_speech(voice: str, text: str) -> bytes:
    from openai import OpenAI  # lazy
    client = OpenAI(api_key=app_settings.openai_key())
    try:
        resp = client.audio.speech.create(model=OPENAI_TTS_MODEL, voice=voice, input=text)
    except Exception:
        resp = client.audio.speech.create(model=OPENAI_TTS_MODEL, voice=_OPENAI_FALLBACK, input=text)
    return resp.read()


ELEVEN_MODEL = "eleven_multilingual_v2"


def eleven_speech(eleven_voice_id: str, text: str, dictionary_locator: dict | None = None) -> bytes:
    import httpx  # lazy (bundled with openai)
    key = app_settings.elevenlabs_key()
    url = f"https://api.elevenlabs.io/v1/text-to-speech/{eleven_voice_id}"
    body = {"text": text, "model_id": ELEVEN_MODEL, "output_format": "mp3_44100_128"}
    if dictionary_locator:
        body["pronunciation_dictionary_locators"] = [dictionary_locator]
    resp = httpx.post(
        url,
        headers={"xi-api-key": key, "accept": "audio/mpeg"},
        json=body,
        timeout=30.0,
    )
    resp.raise_for_status()
    return resp.content


def eleven_sync_pronunciation_dictionary(terms: list[dict]) -> dict:
    """Create a fresh ElevenLabs pronunciation dictionary from the given
    [{"term", "alias"}] rules. Returns {"dictionary_id", "version_id"}.

    Uses "alias" (respelling) rules, not "phoneme"/IPA rules -- ElevenLabs'
    eleven_multilingual_v2 (ELEVEN_MODEL above, needed for ms/zh) silently
    ignores phoneme-type rules and speaks the literal term instead. Phoneme+IPA
    only works on eleven_flash_v2 / eleven_v3, which this app doesn't use.

    ElevenLabs dictionaries are immutable per version, so on every settings
    save we create a new dictionary rather than mutate an existing one --
    simplest correct approach; old dictionaries are just orphaned on their
    side (harmless, no cleanup API needed for a POC).
    """
    import httpx  # lazy
    key = app_settings.elevenlabs_key()
    rules = [
        {"string_to_replace": t["term"], "type": "alias", "alias": t["alias"]}
        for t in terms
    ]
    resp = httpx.post(
        "https://api.elevenlabs.io/v1/pronunciation-dictionaries/add-from-rules",
        headers={"xi-api-key": key},
        json={"name": "chatbranch-terms", "rules": rules},
        timeout=15.0,
    )
    resp.raise_for_status()
    data = resp.json()
    return {"dictionary_id": data["id"], "version_id": data["version_id"]}


def eleven_clone(name: str, audio_bytes: bytes, filename: str) -> str:
    import httpx  # lazy
    key = app_settings.elevenlabs_key()
    resp = httpx.post(
        "https://api.elevenlabs.io/v1/voices/add",
        headers={"xi-api-key": key},
        data={"name": name},
        files={"files": (filename, audio_bytes, "application/octet-stream")},
        timeout=60.0,
    )
    resp.raise_for_status()
    return resp.json()["voice_id"]


def eleven_get_voice(eleven_voice_id: str) -> dict:
    import httpx  # lazy
    key = app_settings.elevenlabs_key()
    resp = httpx.get(
        f"https://api.elevenlabs.io/v1/voices/{eleven_voice_id}",
        headers={"xi-api-key": key},
        timeout=15.0,
    )
    resp.raise_for_status()
    return resp.json()


def eleven_delete_voice(eleven_voice_id: str) -> None:
    import httpx  # lazy
    key = app_settings.elevenlabs_key()
    resp = httpx.delete(
        f"https://api.elevenlabs.io/v1/voices/{eleven_voice_id}",
        headers={"xi-api-key": key},
        timeout=15.0,
    )
    resp.raise_for_status()
