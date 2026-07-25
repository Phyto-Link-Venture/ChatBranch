"""One-time setup: create a LiveKit OUTBOUND SIP trunk pointing at your Twilio
Elastic SIP Trunk. Run once, then copy the printed trunk id into .env as
SIP_OUTBOUND_TRUNK_ID.

Prereqs in .env (from the Twilio console — see README "Live phone calls"):
  CALLER_ID              your Twilio voice number, E.164 (e.g. +14155550123)
  TWILIO_TERMINATION_URI your trunk's Termination SIP URI host (e.g. abc.pstn.twilio.com)
  TWILIO_SIP_USERNAME    a Credential List username on that trunk
  TWILIO_SIP_PASSWORD    that credential's password

Usage:  python setup_trunk.py
"""
import asyncio
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import config  # noqa: E402  (loads .env)

from livekit import api  # noqa: E402


async def main():
    config.require_sip_setup()
    lkapi = api.LiveKitAPI()
    try:
        resp = await lkapi.sip.create_sip_outbound_trunk(
            api.CreateSIPOutboundTrunkRequest(
                trunk=api.SIPOutboundTrunkInfo(
                    name="twilio-outbound",
                    address=config.TWILIO_TERMINATION_URI,
                    numbers=[config.CALLER_ID],
                    auth_username=config.TWILIO_SIP_USERNAME,
                    auth_password=config.TWILIO_SIP_PASSWORD,
                )
            )
        )
    finally:
        await lkapi.aclose()

    # Plain ASCII: this runs on a Windows cp1252 console, where a non-ASCII
    # character raises UnicodeEncodeError *after* the trunk already exists —
    # the run looks failed, the id is never printed, and re-running silently
    # creates a duplicate trunk (2026-07-23).
    print("\nOutbound trunk created. Add this line to your .env:\n")
    print(f"SIP_OUTBOUND_TRUNK_ID={resp.sip_trunk_id}\n")


if __name__ == "__main__":
    asyncio.run(main())
