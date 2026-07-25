"""Tiny local persistence: append call records to data/calls.json (a JSON array).

Also keeps a per-call LIVE transcript in data/live/<call_id>.json so the website
can poll turns while the call is still in progress (the agent and the HTTP server
run in separate processes, so they share state through these files)."""
import json
from typing import Any

from config import CALLS_FILE, DATA_DIR

LIVE_DIR = DATA_DIR / "live"


def _live_path(call_id: str):
    # call_id is a server-generated token; keep only safe chars for a filename.
    safe = "".join(c for c in call_id if c.isalnum() or c in "-_")
    return LIVE_DIR / f"{safe}.json"


def save_live(call_id: str, turns: list[dict[str, str]], done: bool = False,
              ended_by: str | None = None) -> None:
    """Overwrite the live transcript for an in-progress call. ended_by (set
    on the final done=True write) records WHO ended it: "agent" (end_call
    tool), "contact" (hung up), "system:silence" / "system:max_duration"."""
    LIVE_DIR.mkdir(parents=True, exist_ok=True)
    payload: dict = {"turns": turns, "done": done}
    if ended_by:
        payload["ended_by"] = ended_by
    with open(_live_path(call_id), "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False)


def get_live(call_id: str) -> dict[str, Any] | None:
    path = _live_path(call_id)
    if not path.exists():
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def _load() -> list[dict[str, Any]]:
    if not CALLS_FILE.exists():
        return []
    try:
        with open(CALLS_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data if isinstance(data, list) else []
    except (json.JSONDecodeError, OSError):
        # Corrupt/empty file — start fresh rather than crash the call.
        return []


def save_call(record: dict[str, Any]) -> None:
    """Append one call record and write the whole array back to disk."""
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    calls = _load()
    calls.append(record)
    with open(CALLS_FILE, "w", encoding="utf-8") as f:
        json.dump(calls, f, indent=2, ensure_ascii=False)


def get_call(call_id: str) -> dict[str, Any] | None:
    for c in _load():
        if c.get("call_id") == call_id:
            return c
    return None


# ── Inbox: instructions from the HTTP API (Django) to the running agent ───────
# The agent and the web server are separate processes; the agent polls this file
# for guide/take-over/escalate instructions the operator sends mid-call.
INBOX_DIR = DATA_DIR / "inbox"


def _inbox_path(call_id: str):
    safe = "".join(c for c in call_id if c.isalnum() or c in "-_")
    return INBOX_DIR / f"{safe}.json"


def push_instruction(call_id: str, instruction: dict[str, Any]) -> None:
    """Append one instruction (e.g. {mode:'guide'|'takeover', text:...})."""
    INBOX_DIR.mkdir(parents=True, exist_ok=True)
    path = _inbox_path(call_id)
    items: list[dict[str, Any]] = []
    if path.exists():
        try:
            with open(path, "r", encoding="utf-8") as f:
                items = json.load(f)
        except (json.JSONDecodeError, OSError):
            items = []
    items.append(instruction)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(items, f, ensure_ascii=False)


def pop_instructions(call_id: str) -> list[dict[str, Any]]:
    """Return all pending instructions and clear the inbox (consume-once)."""
    path = _inbox_path(call_id)
    if not path.exists():
        return []
    try:
        with open(path, "r", encoding="utf-8") as f:
            items = json.load(f)
    except (json.JSONDecodeError, OSError):
        return []
    try:
        path.unlink()
    except OSError:
        pass
    return items if isinstance(items, list) else []
