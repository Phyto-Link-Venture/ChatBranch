"""Voice calls for provider="baileys" accounts (unofficial WhatsApp Web).

The Cloud API path (wa_calls.py + bridge.py) spawns one aiortc process per
call; the Baileys path needs none of that — the long-lived Node gateway
(backend/baileys_service, calls.js) terminates WhatsApp's VoIP leg in-process
via baileys-caller's WASM stack and joins the LiveKit room itself. Django's
part is only:

  1. dispatch the voice agent into a fresh room + mint the gateway's join
     token (mirrors bridge.py's LiveKit leg, lazy livekit import — same rule
     as calls.lk: keep the SDK out of cheap request paths),
  2. relay start/stop over the gateway HTTP API (client.baileys_request),
  3. mirror the gateway's pushed call events onto the thread — the /chats SSE
     stream picks the change up from there, no polling.

No Meta permission dance here: a personal WhatsApp account can ring anyone.
That convenience is exactly why this transport violates WhatsApp's ToS —
keep volumes low and use a dedicated number (see baileys_service/index.js).
"""
from __future__ import annotations

import json
import os
import secrets

from . import client, store, wa_config


def _livekit_ready() -> bool:
    return bool(
        os.getenv("LIVEKIT_URL") and os.getenv("LIVEKIT_API_KEY") and os.getenv("LIVEKIT_API_SECRET")
    )


def _dispatch_agent(wa_id: str, name: str | None, voice_meta: dict | None = None,
                    script: str = "") -> tuple[str, str]:
    """Create a room, dispatch the voice agent into it, and mint the token the
    gateway joins with. Identity mirrors bridge.py ("whatsapp_contact") so
    the agent treats Baileys calls exactly like Cloud API bridge calls.

    `script` (already rendered — this layer does no {{variable}} substitution)
    makes it a one-way "read it, then hang up" call. It is the same dispatch
    key the SIP path sets, so the agent needs no per-transport branch."""
    import asyncio

    from livekit import api

    room = f"wa-call-{secrets.token_urlsafe(12)}"
    meta = {"phone": f"+{wa_id}", "channel": "whatsapp"}
    # Prefer the CRM contact over the WhatsApp push-name so the agent uses the
    # real name + record + language. contact_for_thread checks the thread's
    # contact FK FIRST (operators can link a contact to a number that differs
    # from the CRM phone), then falls back to the digits match.
    contact = store.contact_for_thread(wa_id)
    if contact:
        meta.update(store.call_metadata(contact, channel="whatsapp"))
    elif name:
        meta["contact_name"] = name  # no CRM match — fall back to the push name
    if voice_meta:
        # voice_provider (+ resolved voice) — the agent reads these to pick the
        # ElevenLabs STT->LLM->TTS pipeline instead of the native realtime voice
        meta.update(voice_meta)
    if script:
        # Set LAST so a script call can never be clobbered by the blocks above.
        meta["script"] = script[:2000]
    display_name = (contact.name if contact else name) or f"+{wa_id}"

    async def _go():
        lkapi = api.LiveKitAPI()
        try:
            await lkapi.agent_dispatch.create_dispatch(
                api.CreateAgentDispatchRequest(
                    agent_name=os.getenv("AGENT_NAME", "chatwood-agent"),
                    room=room,
                    metadata=json.dumps(meta),
                )
            )
        finally:
            close = getattr(lkapi, "aclose", None)
            if close:
                await close()

    asyncio.run(_go())
    token = (
        api.AccessToken()
        .with_identity("whatsapp_contact")
        .with_name(display_name)
        .with_grants(api.VideoGrants(room_join=True, room=room))
        .to_jwt()
    )
    return room, token


def _delete_room(room: str | None) -> None:
    """Hang up the agent side. The agent also self-terminates (participant
    disconnect handler + max-duration failsafe), so failures are non-fatal."""
    if not room or not _livekit_ready():
        return
    import asyncio

    from livekit import api

    async def _go():
        lkapi = api.LiveKitAPI()
        try:
            await lkapi.room.delete_room(api.DeleteRoomRequest(room=room))
        finally:
            close = getattr(lkapi, "aclose", None)
            if close:
                await close()

    try:
        asyncio.run(_go())
    except Exception:
        pass


LIVE_CALL_STATUSES = ("connecting", "ringing", "in_call")


def _gateway_call_active(account: dict) -> bool:
    """The gateway is the ground truth for liveness (no bridge pid to check)."""
    try:
        data = client.baileys_request(f"/accounts/{account['id']}/call-status", method="GET")
    except Exception:
        return False  # gateway down — nothing can be live
    return data.get("status") in LIVE_CALL_STATUSES


def account_call_busy(account: dict) -> bool:
    """True when this WhatsApp number already has a call up.

    The gateway terminates WhatsApp's VoIP leg on the account's own socket and
    allows exactly ONE call per number — a second dial comes back 502. The call
    queue (flows/queue.py) serialises on this so two flow runs reaching their
    call step in the same tick wait their turn instead of one failing outright.

    The GATEWAY is the source of truth, not the thread rows: a call is placed
    against the account's socket, and nothing guarantees the contact's thread is
    attributed to that account (routing sets thread.account on send paths, and
    a call-only thread may carry none at all) — so a DB-first check silently
    reports "free" while a call is up. Thread state is only reconciled here:
    rows left reading busy by a crash or restart are cleared once the gateway
    disagrees, otherwise one dead call would wedge the number for good.
    """
    from crm.models import Thread

    if _gateway_call_active(account):
        return True
    account_pk = str(account.get("id") or "")
    if account_pk.isdigit():
        stale = (Thread.objects
                 .filter(channel="whatsapp", account_id=int(account_pk),
                         call_status__in=LIVE_CALL_STATUSES)
                 .values_list("key", flat=True))
        for key in list(stale):
            store.update_thread(key, {"call": {"call_id": None, "status": "idle",
                                               "room": None}})
    return False


def _resolve_voice(voice_provider: str, voice_id: str) -> tuple[dict | None, str | None]:
    """Turn a (provider, dashboard voice id) request into agent dispatch
    metadata. Returns (voice_meta, error). ElevenLabs voice ids are resolved to
    the real ElevenLabs voice_id the same way the SIP dial path does. An empty
    provider means "use the agent's default (native realtime voice)"."""
    if not voice_provider:
        return None, None
    if voice_provider == "elevenlabs":
        if not os.getenv("ELEVENLABS_API_KEY"):
            return None, "ElevenLabs is not configured (ELEVENLABS_API_KEY unset)."
        from calls import voices as voices_mod  # calls app owns the voice store
        from calls import voice_settings as voice_settings_mod
        resolved = voices_mod.eleven_voice_id(voice_id) if voice_id else None
        if not resolved:
            return None, "Unknown or not-ready ElevenLabs voice."
        # brain + STT read fresh here (mirrors the SIP dial path) so the
        # Settings-page selectors apply to WhatsApp calls too, no restart
        return {"voice_provider": "elevenlabs", "voice": resolved,
                "stt_provider": voice_settings_mod.get_stt_provider(),
                "llm_provider": voice_settings_mod.get_llm_provider()}, None
    if voice_provider == "ilmu":
        # env check mirrors elevenlabs: the agent worker only reads .env, so a
        # key saved solely through Settings → App Keys can't carry a call.
        if not os.getenv("ILMU_API_KEY"):
            return None, "ILMU is not configured (ILMU_API_KEY unset in backend/.env)."
        voice = voice_id or "voice_1"
        if voice not in ("voice_1", "voice_2", "voice_3"):
            return None, "Unknown ILMU voice (voice_1, voice_2 or voice_3)."
        return {"voice_provider": "ilmu", "voice": voice}, None
    # openai/gemini native realtime — pass a specific voice name through if given
    meta = {"voice_provider": voice_provider}
    if voice_id:
        meta["voice"] = voice_id
    return meta, None


def _compliance_error(wa_id: str, thread: dict, account: dict, force: bool) -> str | None:
    """Refuse calls we must not place. Enforced HERE, not at the callers, so
    the flow engine, the /chats button and the API all inherit one rule.

    Opt-out is absolute: a contact who sent STOP is already blocked from AI
    replies and templates, and ringing their phone anyway is the loudest
    possible violation of the same consent — `force` does NOT override it.

    Cold calls are the ban lever. RESEARCH.md puts inbound-first bots at <2%
    ban over 12 months against 15-30% for cold outreach, and an unsolicited
    voice call from an unknown personal number reads worse to WhatsApp's
    anti-abuse than a cold text — hence its own Setting rather than reusing
    cold_text_guard, so texts can run on "warn" while calls stay blocked.
    "Cold" is measured per ACCOUNT: the contact may know one of our numbers and
    have never heard from this one, which is exactly the risky case.
    """
    from crm.models import Message, Thread

    if thread.get("opted_out"):
        return ("This contact opted out (STOP) — calling them is not allowed. "
                "Opt-out cannot be overridden.")

    guard = wa_config.cold_call_guard()
    if guard == "allow" or force:
        return None
    account_pk = str(account.get("id") or "")
    if not account_pk.isdigit():
        return None  # sandbox / env-default — no account to attribute against
    row = Thread.objects.filter(channel="whatsapp",
                                key=store.normalize_wa_id(wa_id)).first()
    # No thread at all is not "unmeasurable", it is the COLDEST case there is:
    # this contact has never exchanged a WhatsApp message with us on any number.
    # Treating it as unknown-so-allow let exactly the riskiest call straight
    # through (caught on real data 2026-07-23 — a day-10 contact with no thread).
    if row is not None and Message.objects.filter(
            thread=row, account_id=int(account_pk), direction="in").exists():
        return None  # they have messaged THIS number before — warm

    warning = (f"[Cold call: {account.get('name') or 'this number'} has never "
               "received a message from this contact. An unsolicited call from "
               "an unknown personal number is a ban risk — safest is to have "
               "them message this number first.]")
    if guard == "warn":
        store.append_message(wa_id, "out", warning, source="system")
        return None
    return (f"Cold call blocked: this contact has never messaged "
            f"{account.get('name') or 'this number'}. Unsolicited calls from an "
            "unknown personal number risk a WhatsApp ban — have them message "
            "this number first, or set cold_call_guard to warn/allow.")


def start_call(wa_id: str, thread: dict, account: dict,
               voice_provider: str = "", voice_id: str = "",
               force: bool = False, script: str = "") -> dict:
    """Place a WhatsApp voice call. A non-empty `script` (already rendered)
    makes it one-way: the agent reads it, then hangs up. Compliance (opt-out,
    cold-call guard) is checked here, so script calls inherit it unchanged."""
    err = _compliance_error(wa_id, thread, account, force)
    if err:
        return {"error": err}

    call = thread.get("call") or {}
    if call.get("status") in LIVE_CALL_STATUSES:
        if _gateway_call_active(account):
            return {"error": "A call is already in progress for this contact."}
        # thread says in-call but the gateway disagrees (restart/crash) — reset
        store.update_thread(wa_id, {"call": {"call_id": None, "status": "idle", "room": None}})
        store.append_message(wa_id, "out", "[stale call state recovered]", source="system")

    if not _livekit_ready():
        return {"error": "LiveKit is not configured (LIVEKIT_URL / LIVEKIT_API_KEY / "
                         "LIVEKIT_API_SECRET) — the voice agent has no room to join."}

    voice_meta, verr = _resolve_voice(voice_provider, voice_id)
    if verr:
        return {"error": verr}

    name = thread.get("name")
    if not name or str(name).startswith("+"):
        name = None

    # Flip the thread to "connecting" BEFORE the slow part: agent dispatch plus
    # the gateway's signaling handshake take several seconds, and the dashboard
    # only learns about the call from this row (SSE watches thread.updated_at) —
    # written late, the whole dial phase is invisible to the operator.
    store.update_thread(wa_id, {"call": {"status": "connecting"}})
    try:
        room, token = _dispatch_agent(wa_id, name, voice_meta, script)
    except Exception as e:
        store.update_thread(wa_id, {"call": {"status": "idle"}})
        return {"error": f"LiveKit dispatch failed: {e} — check LIVEKIT_* keys and that "
                         f"the agent worker is running."}

    try:
        data = client.baileys_request(f"/accounts/{account['id']}/call", {
            "to": wa_id,
            "room": room,
            "livekit_url": os.getenv("LIVEKIT_URL", ""),
            "token": token,
            "max_seconds": int(os.getenv("MAX_CALL_SECONDS", "300") or 300),
        })
    except Exception as e:
        _delete_room(room)  # nobody is coming to talk to the agent
        store.update_thread(wa_id, {"call": {"status": "idle"}})
        return {"error": str(e)}

    call_id = data.get("call_id") or ""
    store.update_thread(wa_id, {"call": {"call_id": call_id, "status": "connecting", "room": room}})
    store.append_message(wa_id, "out",
                         "Starting WhatsApp script call (Baileys)…" if script
                         else "Starting WhatsApp call (Baileys)…", source="system")
    return {"call_id": call_id, "status": "connecting", "room": room, "provider": "baileys"}


def end_call(wa_id: str, account: dict) -> dict:
    thread = store.load_thread(wa_id) or {}
    room = (thread.get("call") or {}).get("room")
    try:
        client.baileys_request(f"/accounts/{account['id']}/call-end")
    except Exception:
        pass  # gateway down — reset our side anyway
    _delete_room(room)
    store.update_thread(wa_id, {"call": {"call_id": None, "status": "ended", "room": None}})
    store.append_message(wa_id, "out", "WhatsApp call ended.", source="system")
    # the transcript is paired with its recording when the gateway's 'ended'
    # push arrives (it carries the recording filename) — see handle_event
    return {"status": "ended", "provider": "baileys"}


def handle_event(body: dict) -> dict:
    """Apply a gateway call-event push (ringing | connected | ended) to the
    thread. Idempotent: repeated/late events for a state we're already in are
    acknowledged but change nothing (end_call sets "ended" before the
    gateway's own ended push lands)."""
    wa_id = store.normalize_wa_id(str(body.get("wa_id") or ""))
    event = str(body.get("event") or "")
    if not wa_id or not event:
        return {"ok": True, "ignored": True}
    thread = store.load_thread(wa_id)
    if thread is None:
        return {"ok": True, "ignored": True}
    call = thread.get("call") or {}

    # Pair the transcript with its recording as soon as the gateway reports one.
    # Done BEFORE the status dedup, because an operator-initiated end_call may
    # have already set status="ended" before this gateway push arrives.
    if event == "ended" and body.get("recording"):
        from . import wa_calls
        wa_calls.save_call_transcript(body["recording"], call.get("room") or body.get("room"),
                                      rec_started_at=body.get("rec_started_at"))

    status = {"ringing": "ringing", "connected": "in_call", "ended": "ended"}.get(event)
    if status is None or call.get("status") == status:
        return {"ok": True, "duplicate": True}

    patch: dict = {"call": {"status": status}}
    if event == "ringing":
        note = "WhatsApp call placed — ringing."
    elif event == "connected":
        note = "WhatsApp call connected — voice agent live."
    else:  # ended
        patch["call"].update({"call_id": None, "room": None})
        reason = str(body.get("reason") or "")
        suffix = f" ({reason})" if reason and reason not in ("hangup", "remote_end") else ""
        note = f"WhatsApp call ended{suffix}."
        _delete_room(call.get("room") or body.get("room"))

    store.update_thread(wa_id, patch)
    store.append_message(wa_id, "out", note, source="system")
    return {"ok": True}
