"""WhatsApp voice-call orchestration (signaling + bridge process control).

Port of the control flow in Whatomate's internal/calling/{outgoing,session}.go:
  1. permission  — interactive call_permission_request, contact taps Allow
  2. connect     — POST /calls action=connect with an SDP offer -> call_id
  3. media       — WebRTC audio session (Meta <-> us <-> LiveKit)
  4. terminate   — POST /calls action=terminate

THE MEDIA PLANE lives in whatsapp/bridge.py — one spawned process per call
that terminates Meta's WebRTC leg with aiortc and joins a LiveKit room where
the voice agent (backend/voice_agent/agent.py) is dispatched, so a WhatsApp
call behaves exactly like a SIP call. This module spawns/ends that process
and relays webhook signaling to it through call-signal files
(data/whatsapp/calls/{call_id}.json — same file IPC as the rest of the app).

Diagnosability plumbing (all under data/whatsapp/calls/):
  - pending-{wa_id}.json — written at spawn time with the bridge pid (the
    call_id isn't known yet); the bridge merges its pid into {call_id}.json
    and deletes the pending file once Meta assigns a call_id.
  - logs/{wa_id}-{timestamp}.log — each bridge process's stdout+stderr
    (14-day retention, swept best-effort on the next start_call).
  - stale-state recovery — if a thread claims an active call but the recorded
    bridge pid is dead (or the signal files are ancient and pid-less), the
    state is reset and a new call proceeds instead of 409ing forever.

In sandbox mode (thread has no configured account) the full call lifecycle is
simulated so the dashboard flow can be demoed end to end.
"""
from __future__ import annotations

import json
import logging
import os
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

from . import client, store, wa_config

logger = logging.getLogger(__name__)

PERMISSION_TEXT = (
    "We'd like to call you on WhatsApp about your account. "
    "Tap Allow so we can reach you — it only takes a moment."
)

CALLS_DIR = Path(__file__).resolve().parent.parent / "data" / "whatsapp" / "calls"
BACKEND_DIR = Path(__file__).resolve().parent.parent

STALE_AFTER_SECONDS = 600   # pid-less signal files older than this = dead call
LOG_RETENTION_DAYS = 14

_signal_lock = threading.Lock()


# ---- call-signal files: webhook (Django) <-> bridge process IPC ---------------

def _read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def _merge_json(path: Path, patch: dict) -> None:
    with _signal_lock:
        current = _read_json(path)
        current.update(patch)
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(current), encoding="utf-8")
        tmp.replace(path)


def _signal_path(call_id: str) -> Path:
    safe = "".join(c for c in call_id if c.isalnum() or c in "._-")
    return CALLS_DIR / f"{safe}.json"


def read_call_signal(call_id: str) -> dict:
    return _read_json(_signal_path(call_id))


def write_call_signal(call_id: str, patch: dict) -> None:
    _merge_json(_signal_path(call_id), patch)


def clear_call_signal(call_id: str) -> None:
    try:
        _signal_path(call_id).unlink()
    except OSError:
        pass


# ---- pending signal: spawn-time pid record, before a call_id exists -----------

def _pending_path(wa_id: str) -> Path:
    safe = "".join(c for c in str(wa_id) if c.isalnum() or c in "._-")
    return CALLS_DIR / f"pending-{safe}.json"


def read_pending_signal(wa_id: str) -> dict:
    return _read_json(_pending_path(wa_id))


def write_pending_signal(wa_id: str, patch: dict) -> None:
    _merge_json(_pending_path(wa_id), patch)


def clear_pending_signal(wa_id: str) -> None:
    try:
        _pending_path(wa_id).unlink()
    except OSError:
        pass


# ---- bridge process liveness ---------------------------------------------------

def _pid_alive(pid) -> bool:
    """Best-effort "is this bridge process still running?".

    Windows first (OpenProcess + exit-code check — os.kill(pid, 0) can't
    probe on Windows), POSIX signal-0 otherwise. Anything we can't prove
    alive is treated as dead — except EPERM, which proves existence."""
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    try:
        import ctypes

        kernel32 = ctypes.windll.kernel32  # AttributeError on non-Windows
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        STILL_ACTIVE = 259
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            ok = kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
            return bool(ok) and code.value == STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    except AttributeError:
        pass  # not Windows — fall through to the POSIX probe
    except OSError:
        return False
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True  # exists, just owned by someone else
    except OSError:
        return False


def _file_age(path: Path) -> float | None:
    try:
        return max(0.0, time.time() - path.stat().st_mtime)
    except OSError:
        return None


def _recover_stale_call(wa_id: str, call: dict) -> bool:
    """Called when the double-start guard trips: decide whether the "active"
    call is actually a corpse (bridge crashed, machine rebooted) and, if so,
    reset the thread + signal files so a new call can proceed.

    Returns True when state was recovered (caller proceeds with the new call),
    False when the bridge looks genuinely alive (caller keeps the 409)."""
    call_id = call.get("call_id")
    real_call_id = call_id if call_id and call_id != "wacid.sandbox" else None
    signal = read_call_signal(real_call_id) if real_call_id else {}
    pending = read_pending_signal(wa_id)

    pid = signal.get("pid") or pending.get("pid")
    if pid is not None:
        if _pid_alive(pid):
            return False  # genuinely in a call
    else:
        # No pid on record (sandbox, or files from an older build): only call
        # it stale when signal files exist AND are all past the grace window.
        ages = [a for a in (
            _file_age(_pending_path(wa_id)),
            _file_age(_signal_path(real_call_id)) if real_call_id else None,
        ) if a is not None]
        if not ages or min(ages) < STALE_AFTER_SECONDS:
            return False

    if real_call_id:
        clear_call_signal(real_call_id)
    clear_pending_signal(wa_id)
    store.update_thread(wa_id, {"call": {"call_id": None, "status": "idle", "room": None}})
    store.append_message(wa_id, "out", "[stale call state recovered]", source="system")
    return True


# ---- per-call bridge logs ------------------------------------------------------

def _logs_dir() -> Path:
    return CALLS_DIR / "logs"


def _sweep_old_logs() -> None:
    """Best-effort retention: drop bridge logs older than LOG_RETENTION_DAYS."""
    cutoff = time.time() - LOG_RETENTION_DAYS * 86400
    try:
        for f in _logs_dir().glob("*.log"):
            try:
                if f.stat().st_mtime < cutoff:
                    f.unlink()
            except OSError:
                pass
    except OSError:
        pass


# ---- orchestration -------------------------------------------------------------

def _account_for(wa_id: str) -> dict | None:
    """The thread's owning WhatsApp account (or the default), if configured."""
    thread = store.load_thread(wa_id) or {}
    account = wa_config.get_account(thread.get("account_id"))
    return account if wa_config.account_configured(account) else None


def request_permission(wa_id: str) -> dict:
    """Send the call-permission request message and track it on the thread."""
    account = _account_for(wa_id)
    if account and account.get("provider") == "baileys":
        # personal WhatsApp accounts can dial anyone — Meta's permission flow
        # is a Cloud API concept, so grant immediately and let Start proceed
        store.update_thread(wa_id, {"call": {"permission": "granted"}})
        store.append_message(
            wa_id, "out", "Baileys account — no call permission needed, calls can start directly.",
            source="system",
        )
        return {"permission": "granted", "provider": "baileys"}
    if account:
        msg_id = client.send_call_permission_request(account, wa_id, PERMISSION_TEXT)
    else:  # sandbox: pretend the contact granted it so the demo can continue
        store.update_thread(wa_id, {"call": {"permission": "granted"}})
        store.append_message(
            wa_id, "out", "[sandbox] Call permission request sent — auto-granted.",
            source="system", status="sandbox",
        )
        return {"permission": "granted", "sandbox": True}

    store.update_thread(wa_id, {"call": {"permission": "requested"}})
    store.append_message(
        wa_id, "out", "Call permission requested.", source="system", msg_id=msg_id,
    )
    return {"permission": "requested", "message_id": msg_id}


def start_call(wa_id: str, voice_provider: str = "", voice_id: str = "",
               force: bool = False) -> dict:
    """Start a business-initiated WhatsApp voice call.

    Real mode spawns the media bridge process (whatsapp/bridge.py), which does
    the SDP offer -> connect -> answer dance and pipes audio to the LiveKit
    voice agent. This returns as soon as the bridge is launched; the thread's
    call.status advances (connecting -> ringing -> in_call) as it progresses.

    voice_provider/voice_id (optional) pick the agent's voice: "elevenlabs" +
    a dashboard voice id runs the ElevenLabs pipeline; empty uses the native
    realtime voice. Honoured on the Baileys transport (the Cloud bridge builds
    its own dispatch metadata and ignores these for now).
    """
    thread = store.load_thread(wa_id) or {}
    call = thread.get("call") or {}
    account = _account_for(wa_id)
    if account and account.get("provider") == "baileys":
        # different transport entirely: the long-lived gateway places the call
        # (no per-call bridge process, no signal files, no Meta permission)
        from . import baileys_calls
        return baileys_calls.start_call(wa_id, thread, account, voice_provider, voice_id,
                                        force=force)

    if call.get("status") in ("connecting", "ringing", "in_call"):
        # double-start guard (applies to sandbox too): a second bridge process
        # for the same contact would fight over the call-signal files. But a
        # crashed bridge must not wedge the contact — try stale recovery first.
        if not _recover_stale_call(wa_id, call):
            return {"error": "A call is already in progress for this contact."}

    if not account:
        # Sandbox: simulate the lifecycle so the UI can be exercised.
        store.update_thread(wa_id, {"call": {"call_id": "wacid.sandbox", "status": "in_call"}})
        store.append_message(
            wa_id, "out", "[sandbox] WhatsApp call started — voice agent connected.",
            source="system", status="sandbox",
        )
        return {"call_id": "wacid.sandbox", "status": "in_call", "sandbox": True}

    permission = client.get_call_permission(account, wa_id)
    if permission == "no_permission":
        return {"error": "No call permission — send a permission request first.", "permission": permission}

    cmd = [sys.executable, "-m", "whatsapp.bridge", "--wa-id", wa_id, "--account-id", account["id"]]
    if thread.get("name") and not str(thread["name"]).startswith("+"):
        cmd += ["--name", thread["name"]]

    _sweep_old_logs()
    log_name = f"{wa_id}-{time.strftime('%Y%m%d-%H%M%S')}.log"
    log_path = _logs_dir() / log_name
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_file = open(log_path, "a", buffering=1, encoding="utf-8")  # line-buffered text
    try:
        proc = subprocess.Popen(  # detached per-call process; logs to its own file
            cmd,
            cwd=str(BACKEND_DIR),
            stdout=log_file,
            stderr=subprocess.STDOUT,
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
        )
    finally:
        log_file.close()  # the child inherited the handle; ours is done

    # spawn-time liveness record — the bridge merges its pid into
    # {call_id}.json and deletes this once Meta assigns a call_id (bridge.py)
    write_pending_signal(wa_id, {
        "pid": proc.pid,
        "wa_id": wa_id,
        "started_at": datetime.now(timezone.utc).isoformat(),
        "log": log_name,
    })
    store.update_thread(wa_id, {"call": {"status": "connecting"}})
    store.append_message(wa_id, "out", f"Starting WhatsApp call… (log: {log_name})",
                         source="system")
    return {"status": "connecting", "log": log_name}


def end_call(wa_id: str) -> dict:
    """Hang up: signal the bridge (real) or reset the state (sandbox).

    If the bridge process is already dead there's nobody left to read a
    hangup signal — reset the thread and clean the files directly."""
    thread = store.load_thread(wa_id) or {}
    call = thread.get("call") or {}
    call_id = call.get("call_id")
    account = _account_for(wa_id)
    if account and account.get("provider") == "baileys":
        from . import baileys_calls
        return baileys_calls.end_call(wa_id, account)

    if account and call_id and call_id != "wacid.sandbox":
        pid = read_call_signal(call_id).get("pid") or read_pending_signal(wa_id).get("pid")
        if pid is not None and not _pid_alive(pid):
            clear_call_signal(call_id)
            clear_pending_signal(wa_id)
            store.update_thread(wa_id, {"call": {"call_id": None, "status": "ended", "room": None}})
            store.append_message(wa_id, "out",
                                 "WhatsApp call ended (bridge process was gone — state reset).",
                                 source="system")
            return {"status": "ended", "recovered": True}
        # the bridge watches this file and does the full teardown
        write_call_signal(call_id, {"hangup": True})
        store.update_thread(wa_id, {"call": {"status": "ended"}})
        return {"status": "ending"}

    if account and call.get("status") == "connecting":
        # bridge not signaled yet (no call_id) — it also watches thread status
        pid = read_pending_signal(wa_id).get("pid")
        if pid is not None and not _pid_alive(pid):
            clear_pending_signal(wa_id)
            store.update_thread(wa_id, {"call": {"call_id": None, "status": "ended", "room": None}})
            store.append_message(wa_id, "out",
                                 "WhatsApp call ended (bridge process was gone — state reset).",
                                 source="system")
            return {"status": "ended", "recovered": True}
        store.update_thread(wa_id, {"call": {"status": "ended"}})
        return {"status": "ending"}

    store.update_thread(wa_id, {"call": {"call_id": None, "status": "ended"}})
    store.append_message(wa_id, "out", "WhatsApp call ended.", source="system",
                         status="sent" if account else "sandbox")
    return {"status": "ended"}


def handle_call_event(wa_id: str, event: str, call_id: str = "", sdp: str = "") -> None:
    """Apply a webhook call event and relay signaling to the bridge process.

    Event names per Whatomate's HandleOutgoingCallWebhook: ringing;
    accepted/in_call/connect (carry the contact's SDP ANSWER for
    business-initiated calls); rejected; ended/terminated/terminate; failed.
    """
    if call_id and sdp:
        write_call_signal(call_id, {"sdp_answer": sdp})
    if call_id and event in ("rejected", "ended", "terminated", "terminate", "failed"):
        write_call_signal(call_id, {"hangup": True, "event": event})

    status_map = {
        "ringing": "ringing",
        "accepted": "in_call",
        "in_call": "in_call",
        "connect": "in_call",
        "connected": "in_call",
        "rejected": "idle",
        "ended": "ended",
        "terminated": "ended",
        "terminate": "ended",
        "failed": "idle",
    }
    status = status_map.get(event, event)
    patch: dict = {"call": {"status": status}}
    if event in ("rejected", "ended", "terminated", "terminate", "failed"):
        patch["call"]["call_id"] = None
    if call_id and event == "ringing":
        patch["call"]["call_id"] = call_id
    store.update_thread(wa_id, patch)
    store.append_message(wa_id, "in", f"Call event: {event}.", source="system")


# ---- live monitoring: the same seams the /live-calls page uses --------------
# A WhatsApp call runs the same voice agent as a SIP call, in a LiveKit room
# stored on the thread as call.room. The agent writes its transcript to the
# voice-agent file store (data/live/{room}.json) and reads operator
# instructions from data/inbox/{room}.json — regardless of channel. So the
# dashboard can watch/steer a WhatsApp call by reading/writing that same store,
# keyed by the thread's room (exactly what calls/lk.py does for SIP).

ESCALATE_TEXT = (
    "Politely tell the caller that a human agent will follow up with them "
    "shortly, then wrap up."
)


def _voice_storage():
    """Import the voice-agent file store WITHOUT pulling the LiveKit SDK.
    storage.py depends only on voice_agent/config.py (env + paths), so this
    stays cheap enough for the frequent live-transcript poll — unlike
    calls.lk, which imports caller (and the whole LiveKit SDK) at module top."""
    agent_dir = str(BACKEND_DIR / "voice_agent")
    if agent_dir not in sys.path:
        sys.path.insert(0, agent_dir)
    import storage  # noqa: E402  (voice_agent is a script dir, not a package)
    return storage


def live_transcript(wa_id: str) -> dict:
    """Live transcript for this contact's in-progress call. Same shape the
    live-calls page consumes: {turns:[{role,text}], done}. Empty (but valid)
    until the agent logs its first turn, or when there's no active room
    (sandbox calls, or a call that hasn't reached the room yet)."""
    call = (store.load_thread(wa_id) or {}).get("call") or {}
    room = call.get("room")
    out = {
        "status": call.get("status") or "idle",
        "call_id": call.get("call_id"),
        "room": room,
        "turns": [],
        "done": False,
    }
    if room:
        live = _voice_storage().get_live(room) or {}
        out["turns"] = live.get("turns", [])
        out["done"] = bool(live.get("done", False))
    return out


def push_say(wa_id: str, mode: str, text: str) -> dict:
    """Steer (guide) or speak-for (takeover) the live agent — queues an
    instruction the running agent polls from its inbox each second."""
    room = ((store.load_thread(wa_id) or {}).get("call") or {}).get("room")
    if not room:
        return {"error": "No live call is in progress for this contact."}
    _voice_storage().push_instruction(room, {"mode": mode, "text": text})
    return {"ok": True}


# ---- call audio recordings + paired transcripts ------------------------------
# The gateway writes one stereo WAV per call ({wa_id}-{ms}.wav); Django saves the
# matching transcript next to it ({wa_id}-{ms}.json) so /chats shows the
# recording and its transcript as ONE call entry (not two separate things).

RECORDINGS_DIR = CALLS_DIR / "recordings"


def _call_party_path(kind: str, wa_id: str) -> Path:
    return CALLS_DIR / f"{kind}-{wa_id}.json"


def set_call_party(kind: str, wa_id: str, by: str) -> None:
    """Record who initiated ("initiator") or ended ("ender") the current call
    for this contact — consumed once by save_call_transcript into the
    persisted transcript's meta. One call at a time per contact, so a
    wa_id-keyed sidecar is unambiguous."""
    try:
        CALLS_DIR.mkdir(parents=True, exist_ok=True)
        _call_party_path(kind, wa_id).write_text(
            json.dumps({"by": by, "at": datetime.now(tz=timezone.utc).isoformat()}),
            encoding="utf-8")
    except OSError:
        logger.warning("could not record call %s for %s", kind, wa_id)


def _pop_call_party(kind: str, wa_id: str) -> str:
    path = _call_party_path(kind, wa_id)
    try:
        by = json.loads(path.read_text(encoding="utf-8")).get("by") or ""
    except (OSError, json.JSONDecodeError):
        by = ""
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass
    return by


def save_call_transcript(recording_name: str, room: str | None,
                         rec_started_at: int | None = None) -> bool:
    """Persist a finished call's transcript beside its recording (same base
    name, .json), with meta: who placed the call and who/what ended it.
    rec_started_at (epoch ms, from the gateway) anchors the WAV's t=0 for
    transcript<->audio sync — the filename ms is call construction, which
    leads the recording by the whole ring duration.
    Returns True if a transcript was written."""
    if not recording_name or not room:
        return False
    live = _voice_storage().get_live(room) or {}
    turns = live.get("turns") or []
    if not turns:
        return False  # call never produced a transcript (didn't connect / silent)
    base = Path(str(recording_name)).stem
    wa_id = str(base).rsplit("-", 1)[0]
    # end-cause priority: the operator End button > what the agent recorded
    # (end_call tool / silence watchdog / max duration / caller disconnect)
    ended_by = _pop_call_party("ender", wa_id) or live.get("ended_by") or ""
    meta = {"initiated_by": _pop_call_party("initiator", wa_id) or "",
            "ended_by": ended_by}
    if rec_started_at:
        meta["rec_started_at"] = int(rec_started_at)
    try:
        thread = store.load_thread(wa_id, limit=1) or {}
        account = wa_config.get_account(thread.get("account_id"))
        if account:
            meta["account"] = account.get("name") or ""
    except Exception:
        pass
    try:
        RECORDINGS_DIR.mkdir(parents=True, exist_ok=True)
        (RECORDINGS_DIR / f"{base}.json").write_text(
            json.dumps({"turns": turns, "meta": meta}, ensure_ascii=False),
            encoding="utf-8")
    except OSError:
        return False
    if not ended_by:
        # Race: the gateway's `ended` push lands here BEFORE the agent's
        # shutdown writes ended_by into the live store — re-read shortly and
        # backfill the persisted meta so "who ended it" isn't lost.
        def _late_fill():
            # the agent writes ended_by only AFTER its call summary (an LLM
            # round-trip) — poll patiently instead of a single fixed wait
            for _ in range(15):
                time.sleep(3)
                try:
                    late = (_voice_storage().get_live(room) or {}).get("ended_by") or ""
                    if not late:
                        continue
                    path = RECORDINGS_DIR / f"{base}.json"
                    data = json.loads(path.read_text(encoding="utf-8"))
                    data.setdefault("meta", {})["ended_by"] = late
                    path.write_text(json.dumps(data, ensure_ascii=False),
                                    encoding="utf-8")
                    return
                except Exception:
                    logger.exception("ended_by backfill failed for %s", base)
                    return

        threading.Thread(target=_late_fill, daemon=True, name="wa-endedby").start()
    # AI memory: fold the finished call into the contact's long-term memory
    # (aicore) — off-thread, the gateway's `ended` push must never wait on it.
    _summarize_call_async(base, room, turns)
    return True


def _summarize_call(recording_base: str, room: str, turns: list) -> None:
    """Resolve the contact and store one ConversationSummary for the finished
    call (aicore/memory.py). Runs on a worker thread — see the async wrapper."""
    from aicore import memory

    wa_id = str(recording_base).rsplit("-", 1)[0]  # recordings are {wa_id}-{ms}
    contact = store.contact_for_thread(wa_id)
    if contact is None:
        return  # no CRM identity — nothing to remember against
    transcript = "\n".join(
        f"{'Agent' if t.get('role') == 'assistant' else 'Contact'}: {t.get('text', '')}"
        for t in turns if isinstance(t, dict)
    )
    memory.summarize_conversation(contact, source="call", channel="whatsapp",
                                  ref=str(room or "")[:190], transcript_text=transcript)


def _summarize_call_async(recording_base: str, room: str, turns: list) -> None:
    """Fire-and-forget memory summarization — same daemon-thread pattern as
    the webhook AI replies: the caller never blocks on (or fails because of)
    an LLM call."""
    def runner():
        try:
            _summarize_call(recording_base, room, turns)
        except Exception:
            logger.exception("whatsapp: call memory summarization failed")
        finally:
            from django.db import close_old_connections
            close_old_connections()

    try:
        threading.Thread(target=runner, daemon=True, name="wa-call-summary").start()
    except Exception:
        logger.exception("whatsapp: could not start call-summary thread")


def list_recordings(wa_id: str) -> list[dict]:
    """Recordings (+ their transcripts) for this contact — the Baileys gateway
    writes one stereo WAV per call named {wa_id}-{ms}.wav (left = contact,
    right = agent); the paired {wa_id}-{ms}.json holds the transcript. Newest
    first."""
    try:
        files = sorted(RECORDINGS_DIR.glob(f"{wa_id}-*.wav"),
                       key=lambda f: f.stat().st_mtime, reverse=True)
    except OSError:
        return []
    out = []
    for f in files:
        try:
            st = f.stat()
        except OSError:
            continue
        transcript, meta = [], {}
        tj = f.with_suffix(".json")
        if tj.exists():
            try:
                data = json.loads(tj.read_text(encoding="utf-8"))
                transcript = data.get("turns", [])
                meta = data.get("meta") or {}
            except (OSError, json.JSONDecodeError):
                transcript = []
        out.append({"name": f.name, "size": st.st_size, "transcript": transcript,
                    "meta": meta,
                    "created": datetime.fromtimestamp(st.st_mtime, tz=timezone.utc).isoformat()})
    return out


def recording_path(name: str):
    """Resolve a recording filename to a path, rejecting directory traversal."""
    safe = Path(str(name)).name  # drop any path components
    if not safe.endswith(".wav"):
        return None
    p = RECORDINGS_DIR / safe
    return p if p.exists() else None
