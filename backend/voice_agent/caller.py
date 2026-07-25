"""Shared outbound-call logic: dispatch the agent + dial a number into a LiveKit
room. Used by both make_call.py (CLI) and token_server.py (HTTP test page)."""
import json
import secrets

from livekit import api

import config


async def place_call(
    phone: str,
    name: str | None = None,
    language: str | None = None,
    wait_until_answered: bool = True,
    extra_meta: dict | None = None,
) -> str:
    """Dispatch the agent and dial `phone` into a fresh room. Returns the room name.

    `language` is a demographic hint ("en" / "ms" / "zh") the agent opens in.
    `extra_meta` adds contact context (record_amount, record_due_date,
    record_status, company_name) passed to the agent via dispatch metadata.
    wait_until_answered=True blocks until the callee picks up (good for the CLI);
    False returns as soon as the call is initiated (good for an HTTP request).
    Raises on failure — caller decides how to report it.
    """
    config.require_sip_dial()

    # Full-entropy, unguessable room id — it doubles as the lookup key for the
    # (unauthenticated) /calls/{id} endpoint, so it must not be brute-forceable.
    room = f"call-{secrets.token_urlsafe(16)}"
    meta = {"phone": phone}
    if name:
        meta["contact_name"] = name
    if language:
        meta["language"] = language
    if extra_meta:
        meta.update({k: v for k, v in extra_meta.items() if v is not None})

    lkapi = api.LiveKitAPI()
    try:
        await lkapi.agent_dispatch.create_dispatch(
            api.CreateAgentDispatchRequest(
                agent_name=config.AGENT_NAME,
                room=room,
                metadata=json.dumps(meta),
            )
        )
        await lkapi.sip.create_sip_participant(
            api.CreateSIPParticipantRequest(
                sip_trunk_id=config.SIP_OUTBOUND_TRUNK_ID,
                sip_call_to=phone,
                room_name=room,
                participant_identity="phone_contact",
                participant_name=name or "contact",
                wait_until_answered=wait_until_answered,
            )
        )
        return room
    finally:
        await lkapi.aclose()


async def hangup_room(room: str) -> None:
    """Delete the room, disconnecting the phone participant (hangs up the call)."""
    lkapi = api.LiveKitAPI()
    try:
        await lkapi.room.delete_room(api.DeleteRoomRequest(room=room))
    finally:
        await lkapi.aclose()
