"""In-process stereo recorder for SIP calls.

LiveKit Cloud egress writes only to cloud object storage (S3/GCS/Azure), so to
keep SIP recordings LOCAL and consistent with the WhatsApp bridge (which taps
media in-process too), we read the room's two audio tracks directly and write a
stereo WAV: L = contact (contact), R = agent. calls/views.py serves it next to
the call's transcript in /chats.

ponytail: 16 kHz, arrival-order append with a leading-silence offset so the two
channels line up at their shared start. Good enough for QA playback, NOT
sample-accurate lip-sync (no drift correction if a channel's clock wanders on a
long call). Upgrade to LiveKit Egress + object storage if broadcast sync ever
matters.
"""
import asyncio
import logging
import time
import wave
from pathlib import Path

from livekit import rtc

logger = logging.getLogger("recorder")

RATE = 16000
_SILENCE = b"\x00\x00"  # one int16 mono sample

try:
    import numpy as _np
except ImportError:  # numpy is a livekit transitive dep, but degrade anyway
    _np = None


async def _wait_audio_track(participant, timeout: float = 25.0):
    """Return a participant's audio track once it's published/subscribed."""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        for pub in list(participant.track_publications.values()):
            if pub.kind == rtc.TrackKind.KIND_AUDIO and pub.track is not None:
                return pub.track
        await asyncio.sleep(0.2)
    return None


class CallRecorder:
    """Records contact (left) + agent (right) audio of one call to a stereo
    16 kHz WAV. Best-effort: any failure yields no/partial audio, never raises
    into the call path."""

    def __init__(self, out_path):
        self.out = Path(out_path)
        self._left = bytearray()   # contact
        self._right = bytearray()  # agent
        self._t0 = None
        self._tasks: list[asyncio.Task] = []
        self._running = False

    def start(self, room, contact_participant) -> None:
        self._running = True
        self._t0 = time.monotonic()
        self._tasks = [
            asyncio.create_task(self._capture(contact_participant, self._left, "contact")),
            asyncio.create_task(self._capture(room.local_participant, self._right, "agent")),
        ]

    async def _capture(self, participant, buf: bytearray, tag: str) -> None:
        stream = None
        try:
            track = await _wait_audio_track(participant)
            if track is None:
                logger.warning("recorder: no audio track for %s", tag)
                return
            # pad leading silence so this channel starts at the shared t0
            lead = int(max(0.0, time.monotonic() - self._t0) * RATE)
            buf.extend(_SILENCE * lead)
            stream = rtc.AudioStream(track, sample_rate=RATE, num_channels=1)
            async for ev in stream:
                if not self._running:
                    break
                buf.extend(bytes(ev.frame.data))
        except asyncio.CancelledError:
            pass
        except Exception as e:  # never let recording break the call
            logger.warning("recorder capture (%s) failed: %s", tag, e)
        finally:
            if stream is not None:
                try:
                    await stream.aclose()
                except Exception:
                    pass

    async def stop(self):
        """Stop capture and write the WAV. Returns the filename, or None."""
        if not self._running:
            return None
        self._running = False
        for t in self._tasks:
            t.cancel()
        for t in self._tasks:
            try:
                await t
            except asyncio.CancelledError:
                pass
        if not self._left and not self._right:
            return None
        # pad the shorter channel so both end together
        n = max(len(self._left), len(self._right))
        self._left.extend(b"\x00" * (n - len(self._left)))
        self._right.extend(b"\x00" * (n - len(self._right)))
        try:
            self.out.parent.mkdir(parents=True, exist_ok=True)
            stereo = self._interleave()
            with wave.open(str(self.out), "wb") as w:
                w.setnchannels(2)
                w.setsampwidth(2)
                w.setframerate(RATE)
                w.writeframes(stereo)
            return self.out.name
        except Exception as e:
            logger.warning("recorder write failed: %s", e)
            return None

    def _interleave(self) -> bytes:
        if _np is not None:
            left = _np.frombuffer(bytes(self._left), dtype="<i2")
            right = _np.frombuffer(bytes(self._right), dtype="<i2")
            m = min(len(left), len(right))
            out = _np.empty(m * 2, dtype="<i2")
            out[0::2] = left[:m]
            out[1::2] = right[:m]
            return out.tobytes()
        # numpy-less fallback: interleave via the array module
        import array
        left = array.array("h"); left.frombytes(bytes(self._left))
        right = array.array("h"); right.frombytes(bytes(self._right))
        m = min(len(left), len(right))
        out = array.array("h", bytes(4 * m))
        out[0::2] = left[:m]
        out[1::2] = right[:m]
        return out.tobytes()


if __name__ == "__main__":  # self-check: interleave + WAV round-trip, no room needed
    import tempfile, os
    r = CallRecorder(Path(tempfile.gettempdir()) / "rec_selftest.wav")
    r._running = True
    r._left = bytearray(b"\x01\x00" * 100)   # 100 samples L=1
    r._right = bytearray(b"\x02\x00" * 60)    # 60 samples R=2 (shorter)
    r._tasks = []
    import asyncio as _a
    name = _a.run(r.stop())
    with wave.open(str(r.out), "rb") as w:
        assert w.getnchannels() == 2, w.getnchannels()
        assert w.getframerate() == RATE
        frames = w.getnframes()
    assert frames == 100, frames  # padded to the longer channel
    os.unlink(r.out)
    print("recorder self-check OK:", name, frames, "frames")
