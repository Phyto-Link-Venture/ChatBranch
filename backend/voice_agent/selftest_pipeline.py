# backend/voice_agent/selftest_pipeline.py
"""Offline sanity check for the ElevenLabs pipeline mode (STT+LLM+TTS+VAD) --
verifies imports + construction succeed WITHOUT touching LiveKit/SIP or making
any network call. Run this before ever using voice_provider=elevenlabs on a
real phone call, to catch dependency version-skew or missing-key mistakes early.

Run: python selftest_pipeline.py <a-ready-elevenlabs-voice-id>
"""
import sys

import config
from agent import _build_session_kwargs


def main() -> int:
    if not config.OPENAI_API_KEY:
        print("FAIL: OPENAI_API_KEY not set (needed for pipeline STT+LLM).")
        return 1
    if not config.ELEVENLABS_API_KEY:
        print("FAIL: ELEVENLABS_API_KEY not set.")
        return 1

    voice_id = sys.argv[1] if len(sys.argv) > 1 else "selftest-placeholder-id"
    try:
        kwargs = _build_session_kwargs({"voice_provider": "elevenlabs", "voice": voice_id})
    except Exception as e:
        print(f"FAIL: pipeline construction raised: {e!r}")
        return 1

    missing = [k for k in ("stt", "llm", "tts", "vad") if k not in kwargs]
    if missing:
        print(f"FAIL: pipeline kwargs missing: {missing}")
        return 1

    print("OK: ElevenLabs pipeline (stt/llm/tts/vad) constructed without error.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
