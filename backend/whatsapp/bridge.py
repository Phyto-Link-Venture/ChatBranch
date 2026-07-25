"""WhatsApp voice call media bridge: Meta WebRTC leg <-> LiveKit room.

Python equivalent of Whatomate's Go call engine (internal/calling/
{outgoing,webrtc,bridge}.go), reshaped for this app: instead of bridging to a
human agent's browser, the far side is a LiveKit room where the AI voice agent
(backend/voice_agent/agent.py) is dispatched — a WhatsApp call becomes just
another caller in a room, exactly like a SIP call.

One process per call, spawned by the Django API (wa_calls.start_call):

    venv\\Scripts\\python -m whatsapp.bridge --wa-id 60123456789 \\
        --account-id wa-xxxx [--name "Ahmad"] [--language ms]

Flow (mirrors Whatomate's InitiateOutgoingCall):
  1. dispatch the voice agent into a fresh LiveKit room, join it ourselves and
     publish a "whatsapp_contact" audio track
  2. aiortc PeerConnection: local track = agent audio pulled from the room;
     create SDP offer (full ICE, no trickle) -> Cloud API action=connect
  3. Django's webhook receives the contact's SDP ANSWER on accept and writes
     it to data/whatsapp/calls/{call_id}.json (file IPC, same pattern as the
     rest of the app); we poll it, set the remote description, audio flows
  4. Meta remote track frames -> resample -> LiveKit AudioSource
  5. teardown on: webhook terminate/reject, dashboard hang-up (thread status
     'ended'), PeerConnection failure, agent/room gone, or MAX_CALL_SECONDS

Liveness bookkeeping: wa_calls.start_call records our pid in
data/whatsapp/calls/pending-{wa_id}.json at spawn time; once Meta assigns a
call_id we merge os.getpid() into {call_id}.json and delete the pending file
(also deleted by cleanup() and the crash handler), so the Django side can
detect a dead bridge and recover stale call state.

ICE/TURN: set WHATSAPP_ICE_SERVERS in backend/.env to a JSON list, e.g.
  [{"urls": "stun:stun.l.google.com:19302"},
   {"urls": "turn:turn.example.com:3478", "username": "u", "credential": "p"}]
Unset -> aiortc's defaults; malformed values log a warning and fall back —
a bad config line never takes a call down.

Run `python -m whatsapp.bridge --selftest` for an offline smoke test (creates
the peer, generates a real SDP offer, exercises the ICE-config parser and the
pending-signal helpers; no Meta/LiveKit needed).
"""
from __future__ import annotations

import argparse
import array
import asyncio
import wave
import json
import logging
import os
import secrets
import sys
import time
from fractions import Fraction

from dotenv import load_dotenv

load_dotenv()  # backend/.env — LIVEKIT_*, AGENT_NAME, MAX_CALL_SECONDS

# The stores are database-backed now — boot Django ORM for this standalone
# process before importing them (cwd is backend/, set by wa_calls.start_call).
# This process runs its whole call inside one asyncio loop and touches the ORM
# only for sparse per-call state writes — lift Django's async-context guard
# (SynchronousOnlyOperation) rather than to_thread every store call.
os.environ.setdefault("DJANGO_ALLOW_ASYNC_UNSAFE", "true")
os.environ.setdefault("DJANGO_SETTINGS_MODULE", "core.settings")
import django  # noqa: E402

django.setup()

from . import client, store, wa_calls, wa_config  # noqa: E402

logger = logging.getLogger("wa-bridge")

SAMPLE_RATE = 48000
CHANNELS = 1
FRAME_SAMPLES = 960  # 20 ms @ 48 kHz — one Opus frame
ANSWER_TIMEOUT = 60  # s to wait for the contact to accept
POLL_INTERVAL = 0.25


# ---- LiveKit room -> Meta: a paced aiortc track fed from the agent's audio ----

class RoomAudioTrack:
    """aiortc MediaStreamTrack producing 20 ms frames from a byte buffer that
    the LiveKit subscriber task fills; silence when the agent isn't talking.
    (Defined lazily inside build() because aiortc imports are heavy.)"""

    @staticmethod
    def build():
        import av
        from aiortc.mediastreams import MediaStreamTrack

        class _Track(MediaStreamTrack):
            kind = "audio"

            def __init__(self):
                super().__init__()
                self.buffer = bytearray()
                self.lock = asyncio.Lock()
                self._ts = 0
                self._start: float | None = None

            async def push(self, pcm: bytes) -> None:
                async with self.lock:
                    self.buffer.extend(pcm)
                    # never let latency build past ~1 s
                    max_bytes = SAMPLE_RATE * 2  # 1 s of s16 mono
                    if len(self.buffer) > max_bytes:
                        del self.buffer[:-max_bytes]

            async def recv(self):
                # pace to real time like aiortc's built-in AudioStreamTrack
                if self._start is None:
                    self._start = time.monotonic()
                wait = self._start + (self._ts / SAMPLE_RATE) - time.monotonic()
                if wait > 0:
                    await asyncio.sleep(wait)

                need = FRAME_SAMPLES * 2  # s16 mono bytes
                async with self.lock:
                    if len(self.buffer) >= need:
                        chunk = bytes(self.buffer[:need])
                        del self.buffer[:need]
                    else:
                        chunk = b"\x00" * need  # silence keeps RTP flowing

                frame = av.AudioFrame(format="s16", layout="mono", samples=FRAME_SAMPLES)
                frame.planes[0].update(chunk)
                frame.sample_rate = SAMPLE_RATE
                frame.pts = self._ts
                frame.time_base = Fraction(1, SAMPLE_RATE)
                self._ts += FRAME_SAMPLES
                return frame

        return _Track()


async def _wait_ice_complete(pc, timeout: float = 15.0) -> None:
    """aiortc gathers during setLocalDescription; poll as a belt-and-braces."""
    deadline = time.monotonic() + timeout
    while pc.iceGatheringState != "complete":
        if time.monotonic() > deadline:
            raise TimeoutError("ICE gathering timed out")
        await asyncio.sleep(0.05)


def _ice_configuration():
    """RTCConfiguration from WHATSAPP_ICE_SERVERS (JSON list of
    {"urls": ..., "username"?, "credential"?} dicts — see module docstring).
    Unset -> None (aiortc defaults). Malformed -> warn + None; never let a
    bad config line crash a call."""
    raw = (os.getenv("WHATSAPP_ICE_SERVERS") or "").strip()
    if not raw:
        return None
    from aiortc import RTCConfiguration, RTCIceServer

    try:
        servers = json.loads(raw)
        if not isinstance(servers, list) or not servers:
            raise ValueError("expected a non-empty JSON list")
        ice_servers = []
        for s in servers:
            if not isinstance(s, dict) or not s.get("urls"):
                raise ValueError("each entry needs an 'urls' key")
            kwargs = {"urls": s["urls"]}
            if s.get("username"):
                kwargs["username"] = s["username"]
            if s.get("credential"):
                kwargs["credential"] = s["credential"]
            ice_servers.append(RTCIceServer(**kwargs))
        return RTCConfiguration(iceServers=ice_servers)
    except (ValueError, TypeError) as e:
        logger.warning("WHATSAPP_ICE_SERVERS invalid (%s) — using aiortc defaults", e)
        return None


def _prefer_opus(pc) -> None:
    """Restrict the audio m-line to opus. Meta's SDP validator (error 138008)
    is far stricter than WebRTC parsers — Whatomate's known-good pion stack
    offers opus only, while aiortc otherwise advertises G722/PCMU/PCMA too."""
    from aiortc import RTCRtpSender

    opus = [c for c in RTCRtpSender.getCapabilities("audio").codecs
            if c.mimeType.lower() == "audio/opus"]
    for t in pc.getTransceivers():
        if t.kind == "audio" and opus:
            t.setCodecPreferences(opus)


def _single_fingerprint(sdp: str) -> str:
    """Drop aiortc's extra sha-384/sha-512 a=fingerprint lines. Every stack
    Meta demonstrably accepts (browsers, pion) sends exactly one sha-256;
    the local description keeps all three — only Meta gets the filtered copy."""
    return "\r\n".join(
        line for line in sdp.split("\r\n")
        if not line.startswith("a=fingerprint:") or line.startswith("a=fingerprint:sha-256")
    )


class _CallRecorder:
    """Stereo call recording for the Cloud bridge (L = contact, R = agent),
    mirroring the Baileys gateway's {wa_id}-{ms}.wav files so /chats shows one
    call entry with recording + transcript. add() is append-only (no I/O on
    the pump hot path); alignment + interleave happen once at save().
    Env WHATSAPP_RECORD_CALLS=0 disables."""

    def __init__(self):
        self.enabled = (os.getenv("WHATSAPP_RECORD_CALLS", "1") or "1") != "0"
        self._sides = {"contact": bytearray(), "agent": bytearray()}
        self._first: dict[str, float] = {}

    def add(self, side: str, pcm: bytes) -> None:
        if not self.enabled:
            return
        if side not in self._first:
            self._first[side] = time.monotonic()
        self._sides[side] += pcm

    def save(self, path) -> bool:
        cust, agent = self._sides["contact"], self._sides["agent"]
        if not (cust or agent):
            return False
        # pad the later-starting side at the front so the tracks line up
        if len(self._first) == 2:
            offset = self._first["agent"] - self._first["contact"]
            pad = int(abs(offset) * SAMPLE_RATE) * 2
            if offset > 0:
                agent = bytearray(pad) + agent
            elif offset < 0:
                cust = bytearray(pad) + cust
        n = max(len(cust), len(agent))
        cust = cust + bytearray(n - len(cust))
        agent = agent + bytearray(n - len(agent))
        left = array.array("h"); left.frombytes(bytes(cust))
        right = array.array("h"); right.frombytes(bytes(agent))
        stereo = array.array("h", bytes(len(left) * 4))
        stereo[0::2] = left
        stereo[1::2] = right
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with wave.open(str(path), "wb") as w:
                w.setnchannels(2)
                w.setsampwidth(2)
                w.setframerate(SAMPLE_RATE)
                w.writeframes(stereo.tobytes())
        except OSError:
            logger.exception("recording save failed")
            return False
        return True


async def run_call(wa_id: str, account_id: str, name: str | None, language: str | None) -> int:
    from aiortc import RTCPeerConnection
    import av
    from livekit import api, rtc

    account = wa_config.get_account(account_id)
    if not wa_config.account_configured(account):
        logger.error("account %s not configured", account_id)
        wa_calls.clear_pending_signal(wa_id)  # nobody will merge/delete it now
        store.update_thread(wa_id, {"call": {"call_id": None, "status": "idle", "room": None}})
        return 2

    room_name = f"wa-call-{secrets.token_urlsafe(12)}"
    agent_name = os.getenv("AGENT_NAME", "repayment-agent")
    max_seconds = int(os.getenv("MAX_CALL_SECONDS", "300") or 0)

    stop = asyncio.Event()
    tasks: list[asyncio.Task] = []
    to_meta = RoomAudioTrack.build()
    recorder = _CallRecorder()
    call_started_ms = int(time.time() * 1000)
    _pumped_sids: set = set()

    def _end(reason: str):
        if not stop.is_set():
            logger.info("ending call: %s", reason)
            stop.set()

    # room -> Meta: pump every subscribed audio track (the agent's voice).
    # Deduped by track sid: called from both the subscribe event and the
    # post-connect sweep.
    def _start_room_pump(track, participant):
        if track.kind != rtc.TrackKind.KIND_AUDIO:
            return
        sid = getattr(track, "sid", None) or id(track)
        if sid in _pumped_sids:
            return
        _pumped_sids.add(sid)
        logger.info("room->meta: pumping %s's audio track",
                    getattr(participant, "identity", "?"))

        async def pump_room_to_meta():
            n = 0
            try:
                stream = rtc.AudioStream(track, sample_rate=SAMPLE_RATE, num_channels=CHANNELS)
                async for event in stream:
                    if stop.is_set():
                        break
                    data = bytes(event.frame.data)
                    await to_meta.push(data)
                    recorder.add("agent", data)
                    n += 1
                    if n % 500 == 1:
                        logger.info("room->meta: %d frames pumped", n)
                await stream.aclose()
            except Exception:
                # a dead pump = one-way audio; it must never die silently
                logger.exception("room->meta pump crashed")
                _end("room->meta pump crashed")

        tasks.append(asyncio.create_task(pump_room_to_meta()))

    # ---- 1. LiveKit: dispatch the agent, join the room, publish our track ----
    try:
        lkapi = api.LiveKitAPI()
        meta = {"phone": f"+{wa_id}", "channel": "whatsapp"}
        if name:
            meta["contact_name"] = name
        if language:
            meta["language"] = language
        # CRM enrichment — the SAME helper the Baileys path uses
        # (whatsapp.store.call_metadata), so the agent gets the contact's real
        # name / record / language / memory instead of its fallback defaults.
        try:
            contact = store.contact_for_thread(wa_id)
        except Exception:
            contact = None
        if contact is not None:
            try:
                meta.update(store.call_metadata(contact, channel="whatsapp"))
            except Exception:
                logger.exception("bridge: CRM context failed (continuing without)")
        await lkapi.agent_dispatch.create_dispatch(
            api.CreateAgentDispatchRequest(
                agent_name=agent_name, room=room_name, metadata=json.dumps(meta)
            )
        )
        token = (
            api.AccessToken()
            .with_identity("whatsapp_contact")
            .with_name(name or f"+{wa_id}")
            .with_grants(api.VideoGrants(room_join=True, room=room_name))
            .to_jwt()
        )

        room = rtc.Room()
        # handlers BEFORE connect: the agent is dispatched first and can
        # publish while we're still connecting — a handler attached after
        # connect misses the subscribe event and the contact hears dead air
        # (race seen live 2026-07-16).
        room.on("track_subscribed",
                lambda track, publication, participant: _start_room_pump(track, participant))
        room.on("disconnected", lambda *_: _end("livekit room disconnected"))
        await room.connect(os.getenv("LIVEKIT_URL", ""), token)
        source = rtc.AudioSource(SAMPLE_RATE, CHANNELS)
        lk_track = rtc.LocalAudioTrack.create_audio_track("wa-audio", source)
        await room.local_participant.publish_track(
            lk_track, rtc.TrackPublishOptions(source=rtc.TrackSource.SOURCE_MICROPHONE)
        )
        # sweep tracks that finished subscribing during connect (event already
        # fired) so they get a pump too
        for p in room.remote_participants.values():
            for pub in p.track_publications.values():
                if pub.track is not None:
                    _start_room_pump(pub.track, p)
        # greeting gate: we join during ringing — tell the agent not to speak
        # until Meta's leg actually connects (wa.callStatus flips to "active"
        # in the connectionstatechange handler below)
        try:
            await room.local_participant.set_attributes({"wa.callStatus": "ringing"})
        except Exception:
            logger.warning("set_attributes unsupported — agent may greet before pickup")
    except Exception as e:
        # pre-call_id failure: main()'s crash handler resets the thread and
        # removes the pending file — just make the log line unambiguous
        logger.error("LiveKit dispatch/connect failed: %s — check LIVEKIT_URL / "
                     "LIVEKIT_API_KEY / LIVEKIT_API_SECRET and that the agent "
                     "worker is running", e)
        raise

    # ---- 2. aiortc peer for Meta's leg ----
    ice = _ice_configuration()
    pc = RTCPeerConnection(configuration=ice) if ice else RTCPeerConnection()
    pc.addTrack(to_meta)
    _prefer_opus(pc)

    # Meta -> room: decode/resample and feed the LiveKit source
    @pc.on("track")
    def on_meta_track(track):
        if track.kind != "audio":
            return
        logger.info("meta->room: remote audio track received")

        def _peak(buf: bytes) -> int:
            # s16le peak level (audioop is gone in 3.13) — 0 means pure silence
            a = array.array("h")
            a.frombytes(buf[: len(buf) - (len(buf) % 2)])
            return max((abs(s) for s in a), default=0)

        async def pump_meta_to_room():
            n_in = n_out = 0
            try:
                resampler = av.AudioResampler(format="s16", layout="mono", rate=SAMPLE_RATE)
                while not stop.is_set():
                    try:
                        frame = await track.recv()
                    except Exception:
                        _end("meta track ended")
                        break
                    if n_in == 0:
                        logger.info("meta->room: first frame rate=%s samples=%s layout=%s",
                                    frame.sample_rate, frame.samples, frame.layout.name)
                    n_in += 1
                    for out in resampler.resample(frame):
                        samples = out.samples
                        # av pads plane buffers past the payload — copy only
                        # samples*2 bytes (s16 mono) or the shapes mismatch
                        pcm = bytes(out.planes[0])[: samples * CHANNELS * 2]
                        lk_frame = rtc.AudioFrame.create(SAMPLE_RATE, CHANNELS, samples)
                        buf = memoryview(lk_frame.data).cast("B")
                        n = min(len(buf), len(pcm))
                        buf[:n] = pcm[:n]
                        await source.capture_frame(lk_frame)
                        recorder.add("contact", pcm)
                        n_out += 1
                        if n_out % 250 == 1:
                            logger.info("meta->room: %d in / %d out, peak=%d",
                                        n_in, n_out, _peak(pcm))
                    if n_in == 50 and n_out == 0:
                        logger.warning("meta->room: 50 frames in, resampler emitted none")
            except Exception:
                # a dead pump = the agent hears nothing; never die silently
                logger.exception("meta->room pump crashed")
                _end("meta->room pump crashed")

        tasks.append(asyncio.create_task(pump_meta_to_room()))

    @pc.on("connectionstatechange")
    async def on_state():
        logger.info("meta pc state: %s", pc.connectionState)
        if pc.connectionState == "connected":
            try:  # callee picked up — release the agent's greeting
                await room.local_participant.set_attributes({"wa.callStatus": "active"})
            except Exception:
                pass
        if pc.connectionState in ("failed", "closed"):
            _end(f"meta pc {pc.connectionState}")

    # ---- 3. SDP offer -> Cloud API connect ----
    offer = await pc.createOffer()
    await pc.setLocalDescription(offer)
    await _wait_ice_complete(pc)

    call_id = client.initiate_call(account, wa_id, _single_fingerprint(pc.localDescription.sdp))
    # merge our pid for wa_calls' liveness checks; the spawn-time pending
    # record has served its purpose now that the call_id exists
    wa_calls.write_call_signal(call_id, {"wa_id": wa_id, "room": room_name, "pid": os.getpid()})
    wa_calls.clear_pending_signal(wa_id)
    store.update_thread(wa_id, {"call": {"call_id": call_id, "status": "ringing", "room": room_name}})
    store.append_message(wa_id, "out", "WhatsApp call placed — ringing.", source="system")
    logger.info("call %s ringing (room %s)", call_id, room_name)

    async def cleanup(final_status: str):
        stop.set()
        for t in tasks:
            t.cancel()
        try:
            client.terminate_call(account, call_id)
        except Exception:
            pass  # already ended on Meta's side
        try:
            await pc.close()
        except Exception:
            pass
        try:
            await room.disconnect()
        except Exception:
            pass
        try:
            await lkapi.room.delete_room(api.DeleteRoomRequest(room=room_name))
        except Exception:
            pass
        await lkapi.aclose()
        # persist the recording + paired transcript so /chats shows this call
        # in the Calls panel (same {wa_id}-{ms} naming as the Baileys gateway);
        # save_call_transcript also folds the call into AI memory
        try:
            rec_name = f"{wa_id}-{call_started_ms}.wav"
            if recorder.save(wa_calls.RECORDINGS_DIR / rec_name):
                wa_calls.save_call_transcript(rec_name, room_name)
        except Exception:
            logger.exception("recording/transcript persist failed")
        wa_calls.clear_call_signal(call_id)
        wa_calls.clear_pending_signal(wa_id)  # defensive — normally gone already
        store.update_thread(wa_id, {"call": {"call_id": None, "status": final_status, "room": None}})
        store.append_message(wa_id, "out", "WhatsApp call ended.", source="system")

    # ---- 4. wait for the contact's SDP answer (webhook -> file IPC) ----
    answer_sdp = None
    deadline = time.monotonic() + ANSWER_TIMEOUT
    while time.monotonic() < deadline and not stop.is_set():
        sig = wa_calls.read_call_signal(call_id)
        if sig.get("hangup"):
            await cleanup("idle")
            return 0
        if sig.get("sdp_answer"):
            answer_sdp = sig["sdp_answer"]
            break
        thread = store.load_thread(wa_id) or {}
        if (thread.get("call") or {}).get("status") == "ended":
            await cleanup("ended")
            return 0
        await asyncio.sleep(POLL_INTERVAL)

    if not answer_sdp:
        logger.error("no SDP answer within %ss — contact didn't pick up", ANSWER_TIMEOUT)
        await cleanup("idle")
        return 0

    from aiortc import RTCSessionDescription

    await pc.setRemoteDescription(RTCSessionDescription(sdp=answer_sdp, type="answer"))
    store.update_thread(wa_id, {"call": {"status": "in_call"}})
    logger.info("call %s connected", call_id)

    # ---- 5. run until someone ends it ----
    started = time.monotonic()
    while not stop.is_set():
        sig = wa_calls.read_call_signal(call_id)
        if sig.get("hangup"):
            _end("hangup signal")
            break
        thread = store.load_thread(wa_id) or {}
        if (thread.get("call") or {}).get("status") == "ended":
            _end("dashboard hangup")
            break
        if max_seconds and time.monotonic() - started > max_seconds:
            _end("max duration failsafe")
            break
        await asyncio.sleep(POLL_INTERVAL)

    await cleanup("ended")
    return 0


async def selftest() -> int:
    """Offline smoke test: build the peer + paced track, produce a real SDP
    offer with gathered ICE candidates, exercise the WHATSAPP_ICE_SERVERS
    parser and the pending-signal helpers. No Meta or LiveKit access needed."""
    from aiortc import RTCPeerConnection

    pc = RTCPeerConnection()
    track = RoomAudioTrack.build()
    await track.push(b"\x01\x02" * FRAME_SAMPLES)
    frame = await track.recv()
    assert frame.samples == FRAME_SAMPLES, "track pacing broken"
    pc.addTrack(track)
    _prefer_opus(pc)
    offer = await pc.createOffer()
    await pc.setLocalDescription(offer)
    await _wait_ice_complete(pc)
    sdp = _single_fingerprint(pc.localDescription.sdp)
    assert "m=audio" in sdp and "opus" in sdp.lower(), "no opus audio m-line in offer"
    assert sdp.count("a=fingerprint:") == 1, "Meta offer must carry exactly one fingerprint"
    mline = next(l for l in sdp.splitlines() if l.startswith("m=audio"))
    assert len(mline.split()) == 4, f"Meta offer must advertise opus only, got: {mline}"
    await pc.close()

    # ICE-config parser: valid JSON -> RTCConfiguration, garbage -> defaults
    saved = os.environ.get("WHATSAPP_ICE_SERVERS")
    try:
        os.environ["WHATSAPP_ICE_SERVERS"] = json.dumps([
            {"urls": "stun:stun.l.google.com:19302"},
            {"urls": "turn:turn.example.com:3478", "username": "u", "credential": "p"},
        ])
        cfg = _ice_configuration()
        assert cfg is not None and len(cfg.iceServers) == 2, "ICE parser dropped servers"
        assert cfg.iceServers[1].username == "u" and cfg.iceServers[1].credential == "p", \
            "ICE parser lost TURN credentials"
        os.environ["WHATSAPP_ICE_SERVERS"] = "{not valid json"
        assert _ice_configuration() is None, "malformed ICE config must fall back to defaults"
    finally:
        if saved is None:
            os.environ.pop("WHATSAPP_ICE_SERVERS", None)
        else:
            os.environ["WHATSAPP_ICE_SERVERS"] = saved

    # pending-signal helpers round-trip + pid liveness probe
    probe = "selftest-probe"
    wa_calls.write_pending_signal(probe, {"pid": os.getpid(), "wa_id": probe})
    sig = wa_calls.read_pending_signal(probe)
    assert sig.get("pid") == os.getpid(), "pending signal round-trip broken"
    assert wa_calls._pid_alive(sig["pid"]), "_pid_alive can't see our own process"
    assert not wa_calls._pid_alive(-1), "_pid_alive must treat bogus pids as dead"
    wa_calls.clear_pending_signal(probe)
    assert wa_calls.read_pending_signal(probe) == {}, "pending signal cleanup broken"

    print(f"selftest OK: SDP offer {len(sdp)} bytes, audio m-line + opus present, "
          f"ICE gathering complete; ICE-config parser + pending-signal helpers OK")
    return 0


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
    parser = argparse.ArgumentParser(description="WhatsApp <-> LiveKit call bridge")
    parser.add_argument("--wa-id")
    parser.add_argument("--account-id")
    parser.add_argument("--name", default=None)
    parser.add_argument("--language", default=None)
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()

    if args.selftest:
        return asyncio.run(selftest())
    if not args.wa_id or not args.account_id:
        parser.error("--wa-id and --account-id are required")
    try:
        return asyncio.run(run_call(args.wa_id, args.account_id, args.name, args.language))
    except Exception:
        logger.exception("bridge crashed")
        try:  # leave the thread in a sane state for the dashboard
            wa_calls.clear_pending_signal(args.wa_id)
            store.update_thread(args.wa_id, {"call": {"call_id": None, "status": "idle", "room": None}})
            store.append_message(args.wa_id, "out", "[call failed — see bridge log]",
                                 source="system", status="failed")
        except Exception:
            pass
        return 1


if __name__ == "__main__":
    sys.exit(main())
