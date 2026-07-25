"""Bridge from Django to the LiveKit voice-agent code (backend/voice_agent/*).

The voice_agent package holds the tested dial / hangup / storage logic. We reuse
it rather than duplicate the LiveKit calls. `voice_agent/` is a plain script dir
(no package), so we add it to sys.path and import its modules directly.
"""
import asyncio
import os
import sys

_AGENT_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "voice_agent")
if _AGENT_DIR not in sys.path:
    sys.path.insert(0, _AGENT_DIR)

import caller  # noqa: E402
import storage  # noqa: E402


def _run(coro):
    """Run an async coroutine from a synchronous Django view."""
    return asyncio.run(coro)


def dial(phone: str, name: str | None, language: str | None, extra_meta: dict | None) -> str:
    """Place an outbound call; returns the room id. Does not block on pickup."""
    return _run(
        caller.place_call(
            phone,
            name=name,
            language=language,
            wait_until_answered=False,
            extra_meta=extra_meta,
        )
    )


def hangup(room: str) -> None:
    _run(caller.hangup_room(room))


def live_transcript(room: str) -> dict:
    live = storage.get_live(room)
    if live is None:
        # No live file: the call is over IFF a final record was written;
        # otherwise it's still spinning up (agent connecting) — report not-done.
        return {"turns": [], "done": storage.get_call(room) is not None}
    # Belt-and-suspenders: a persisted record means the call ended even if the
    # final done-write was missed (agent crash mid-shutdown), so the website's
    # live poll never sticks on "Ringing…" forever.
    if not live.get("done") and storage.get_call(room) is not None:
        return {**live, "done": True}
    return live


def call_record(room: str):
    return storage.get_call(room)


def send_instruction(room: str, mode: str, text: str) -> None:
    """Queue a guide / take-over / escalate instruction for the running agent."""
    storage.push_instruction(room, {"mode": mode, "text": text})
