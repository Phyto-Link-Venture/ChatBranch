"""Voice calls on Baileys accounts: permission auto-grant, start/end routing
through the gateway (with LiveKit dispatch mocked), stale-state recovery, and
the gateway's call-event push endpoint. Fully offline — no gateway process,
no LiveKit SDK import.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm.models import ChannelAccount, Profile, Thread
from whatsapp import baileys_calls
from whatsapp import client as wa_client
from whatsapp import store as wa_store
from whatsapp import wa_config

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
SECRET_HDR = {"HTTP_X_BAILEYS_SECRET": wa_config.BAILEYS_SECRET}
WA_ID = "60111222333"


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def no_env_account(monkeypatch):
    monkeypatch.setattr(wa_config, "_env_account", lambda: None)


@pytest.fixture
def baileys_account(db):
    return ChannelAccount.objects.create(channel="whatsapp", provider="baileys",
                                         name="QR Number")


@pytest.fixture
def thread(client, superadmin, no_env_account, baileys_account):
    client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                content_type="application/json", **AUTH)
    # One inbound so the thread is WARM: the cold-call guard blocks dialling a
    # number that has never messaged us, and these tests are about call
    # mechanics (voice resolution, LiveKit config, gateway failures), not that
    # policy — TestColdCallGuard in test_health_routing.py covers the guard.
    wa_store.append_message(WA_ID, "in", "hi", source="customer")
    return Thread.objects.get(channel="whatsapp", key=WA_ID)


@pytest.fixture
def livekit_ok(monkeypatch):
    """Pretend LiveKit is configured and stub the SDK-touching helpers."""
    calls = {"dispatched": [], "deleted": []}
    monkeypatch.setattr(baileys_calls, "_livekit_ready", lambda: True)
    monkeypatch.setattr(baileys_calls, "_dispatch_agent",
                        lambda wa_id, name, voice_meta=None, script="": (calls["dispatched"].append((wa_id, name))
                                                             or ("room-test", "jwt-test")))
    monkeypatch.setattr(baileys_calls, "_delete_room",
                        lambda room: calls["deleted"].append(room))
    return calls


@pytest.fixture
def gateway(monkeypatch):
    """Record gateway requests; programmable responses per path suffix."""
    state = {"requests": [], "call_response": {"call_id": "00CAFE", "status": "connecting"},
             "status_response": {"status": "idle", "call_id": None}}

    def fake_request(path, payload=None, method="POST"):
        state["requests"].append((method, path, payload))
        if path.endswith("/call-status"):
            return state["status_response"]
        if path.endswith("/call-end"):
            return {"status": "ended"}
        if path.endswith("/call"):
            resp = state["call_response"]
            if isinstance(resp, Exception):
                raise resp
            return resp
        return {}

    monkeypatch.setattr(wa_client, "baileys_request", fake_request)
    return state


def _call_state():
    return (wa_store.load_thread(WA_ID) or {}).get("call") or {}


def _system_texts():
    t = Thread.objects.get(channel="whatsapp", key=WA_ID)
    return [m.text for m in t.messages.filter(source="system")]


@pytest.mark.django_db
class TestPermission:
    def test_baileys_auto_grants(self, client, thread, gateway):
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/permission", **AUTH)
        assert r.status_code == 200
        assert r.json()["permission"] == "granted"
        assert _call_state()["permission"] == "granted"
        assert gateway["requests"] == []  # no gateway/cloud traffic involved


@pytest.mark.django_db
class TestStartCall:
    def test_happy_path(self, client, thread, livekit_ok, gateway):
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 200, r.json()
        body = r.json()
        assert body["provider"] == "baileys"
        assert body["call_id"] == "00CAFE"
        method, path, payload = gateway["requests"][-1]
        assert path == f"/accounts/{thread.account_id}/call"
        assert payload["to"] == WA_ID
        assert payload["room"] == "room-test"
        assert payload["token"] == "jwt-test"
        state = _call_state()
        assert state["call_id"] == "00CAFE"
        assert state["status"] == "connecting"
        assert state["room"] == "room-test"

    def test_requires_livekit_config(self, client, thread, gateway, monkeypatch):
        monkeypatch.setattr(baileys_calls, "_livekit_ready", lambda: False)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 409
        assert "LiveKit" in r.json()["detail"]

    def test_gateway_failure_reports_and_frees_room(self, client, thread, livekit_ok, gateway):
        gateway["call_response"] = wa_client.WhatsAppError("account 1 is not connected")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 409
        assert "not connected" in r.json()["detail"]
        assert livekit_ok["deleted"] == ["room-test"]  # agent isn't left waiting
        # the early "connecting" write (dial feedback) must roll back on failure
        assert _call_state().get("status") == "idle"

    def test_dispatch_failure_resets_status(self, client, thread, livekit_ok, gateway, monkeypatch):
        def boom(wa_id, name, voice_meta=None, script=""):
            raise RuntimeError("dispatch down")
        monkeypatch.setattr(baileys_calls, "_dispatch_agent", boom)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 409
        assert "dispatch failed" in r.json()["detail"]
        assert _call_state().get("status") == "idle"

    def test_double_start_blocked_while_gateway_active(self, client, thread, livekit_ok, gateway):
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00CAFE"}})
        gateway["status_response"] = {"status": "in_call", "call_id": "00CAFE"}
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 409
        assert "already in progress" in r.json()["detail"]

    def test_stale_state_recovers_when_gateway_idle(self, client, thread, livekit_ok, gateway):
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00DEAD"}})
        gateway["status_response"] = {"status": "idle", "call_id": None}
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 200, r.json()
        assert r.json()["call_id"] == "00CAFE"
        assert "[stale call state recovered]" in _system_texts()

    def test_elevenlabs_voice_resolved_into_dispatch(self, client, thread, livekit_ok, gateway, monkeypatch):
        from whatsapp import baileys_calls
        captured = {}

        def fake_dispatch(wa_id, name, voice_meta=None, script=""):
            captured["voice_meta"] = voice_meta
            return "room-test", "jwt-test"

        monkeypatch.setattr(baileys_calls, "_dispatch_agent", fake_dispatch)
        monkeypatch.setenv("ELEVENLABS_API_KEY", "sk-test")
        from calls import voices as voices_mod
        from calls import voice_settings as voice_settings_mod
        monkeypatch.setattr(voices_mod, "eleven_voice_id", lambda vid: "EL_REAL_ID" if vid == "vx_demo" else None)
        monkeypatch.setattr(voice_settings_mod, "get_stt_provider", lambda: "elevenlabs")
        monkeypatch.setattr(voice_settings_mod, "get_llm_provider", lambda: "ilmu")

        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "elevenlabs", "voice_id": "vx_demo"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        # brain + STT selections ride along so Settings changes reach WhatsApp calls
        assert captured["voice_meta"] == {"voice_provider": "elevenlabs", "voice": "EL_REAL_ID",
                                          "stt_provider": "elevenlabs", "llm_provider": "ilmu"}

    def test_unknown_elevenlabs_voice_rejected(self, client, thread, livekit_ok, gateway, monkeypatch):
        from whatsapp import baileys_calls
        monkeypatch.setenv("ELEVENLABS_API_KEY", "sk-test")
        from calls import voices as voices_mod
        monkeypatch.setattr(voices_mod, "eleven_voice_id", lambda vid: None)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "elevenlabs", "voice_id": "nope"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        assert "ElevenLabs" in r.json()["detail"]

    def test_ilmu_voice_resolved_into_dispatch(self, client, thread, livekit_ok, gateway, monkeypatch):
        from whatsapp import baileys_calls
        captured = {}

        def fake_dispatch(wa_id, name, voice_meta=None, script=""):
            captured["voice_meta"] = voice_meta
            return "room-test", "jwt-test"

        monkeypatch.setattr(baileys_calls, "_dispatch_agent", fake_dispatch)
        monkeypatch.setenv("ILMU_API_KEY", "sk-ilmu-test")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "ilmu", "voice_id": "voice_2"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert captured["voice_meta"] == {"voice_provider": "ilmu", "voice": "voice_2"}

    def test_ilmu_defaults_to_voice_1(self, client, thread, livekit_ok, gateway, monkeypatch):
        from whatsapp import baileys_calls
        captured = {}
        monkeypatch.setattr(baileys_calls, "_dispatch_agent",
                            lambda wa_id, name, voice_meta=None, script="": (captured.update(voice_meta=voice_meta), ("room-test", "jwt-test"))[1])
        monkeypatch.setenv("ILMU_API_KEY", "sk-ilmu-test")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "ilmu"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200, r.json()
        assert captured["voice_meta"] == {"voice_provider": "ilmu", "voice": "voice_1"}

    def test_ilmu_unknown_voice_rejected(self, client, thread, livekit_ok, gateway, monkeypatch):
        monkeypatch.setenv("ILMU_API_KEY", "sk-ilmu-test")
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "ilmu", "voice_id": "voice_9"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        assert "ILMU voice" in r.json()["detail"]

    def test_ilmu_requires_key(self, client, thread, livekit_ok, gateway, monkeypatch):
        monkeypatch.delenv("ILMU_API_KEY", raising=False)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start",
                        data=json.dumps({"voice_provider": "ilmu"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409
        assert "ILMU_API_KEY" in r.json()["detail"]

    def test_no_voice_means_native_default(self, client, thread, livekit_ok, gateway, monkeypatch):
        from whatsapp import baileys_calls
        captured = {}
        monkeypatch.setattr(baileys_calls, "_dispatch_agent",
                            lambda wa_id, name, voice_meta=None, script="": (captured.update(voice_meta=voice_meta), ("room-test", "jwt-test"))[1])
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/start", **AUTH)
        assert r.status_code == 200, r.json()
        assert captured["voice_meta"] is None  # native realtime voice


@pytest.mark.django_db
class TestScriptCall:
    """One-way "read it, then hang up" calls (the flow builder's Script call
    channel over WhatsApp). The script rides the SAME agent dispatch key the
    SIP path uses, so neither the gateway nor the agent needs a per-transport
    branch — and compliance still runs, because it lives in start_call."""

    SCRIPT = "Hello Ahmad Faiz, your order is ready for collection."

    def _dispatch_spy(self, monkeypatch):
        captured = {}
        monkeypatch.setattr(baileys_calls, "_livekit_ready", lambda: True)
        monkeypatch.setattr(baileys_calls, "_delete_room", lambda room: None)

        def fake_dispatch(wa_id, name, voice_meta=None, script=""):
            captured["script"] = script
            return "room-test", "jwt-test"

        monkeypatch.setattr(baileys_calls, "_dispatch_agent", fake_dispatch)
        return captured

    def _start(self, baileys_account, **kwargs):
        return baileys_calls.start_call(
            WA_ID, wa_store.load_thread(WA_ID),
            wa_config.get_account(str(baileys_account.pk)), **kwargs)

    def test_script_forwarded_into_dispatch(self, thread, baileys_account, gateway, monkeypatch):
        captured = self._dispatch_spy(monkeypatch)
        result = self._start(baileys_account, script=self.SCRIPT)
        assert result["status"] == "connecting"
        assert captured["script"] == self.SCRIPT
        assert "Starting WhatsApp script call (Baileys)…" in _system_texts()

    def test_plain_call_carries_no_script(self, thread, baileys_account, gateway, monkeypatch):
        captured = self._dispatch_spy(monkeypatch)
        self._start(baileys_account)
        assert captured["script"] == ""
        assert "Starting WhatsApp call (Baileys)…" in _system_texts()

    def test_opt_out_blocks_a_script_call(self, thread, baileys_account, gateway, monkeypatch):
        captured = self._dispatch_spy(monkeypatch)
        wa_store.update_thread(WA_ID, {"opted_out": True})
        result = self._start(baileys_account, script=self.SCRIPT)
        assert "opted out" in result["error"]
        assert captured == {}  # never reached dispatch


@pytest.mark.django_db
class TestEndCall:
    def test_end_signals_gateway_and_resets(self, client, thread, livekit_ok, gateway):
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00CAFE",
                                                "room": "room-test"}})
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/end", **AUTH)
        assert r.status_code == 200
        assert ("POST", f"/accounts/{thread.account_id}/call-end", None) in gateway["requests"]
        assert livekit_ok["deleted"] == ["room-test"]
        assert _call_state()["status"] == "ended"
        assert _call_state()["call_id"] is None

    def test_end_survives_gateway_down(self, client, thread, livekit_ok, monkeypatch):
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00CAFE"}})

        def boom(path, payload=None, method="POST"):
            raise wa_client.WhatsAppError("gateway unreachable")

        monkeypatch.setattr(wa_client, "baileys_request", boom)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/end", **AUTH)
        assert r.status_code == 200
        assert _call_state()["status"] == "ended"


@pytest.mark.django_db
class TestCallEventPush:
    def _push(self, client, event, **extra):
        return client.post("/api/whatsapp/baileys/call-event",
                           data=json.dumps({"wa_id": WA_ID, "event": event,
                                            "call_id": "00CAFE", **extra}),
                           content_type="application/json", **SECRET_HDR)

    def test_bad_secret_403(self, client, db):
        r = client.post("/api/whatsapp/baileys/call-event", data="{}",
                        content_type="application/json", HTTP_X_BAILEYS_SECRET="wrong")
        assert r.status_code == 403

    def test_lifecycle_progression(self, client, thread, livekit_ok):
        wa_store.update_thread(WA_ID, {"call": {"status": "connecting", "call_id": "00CAFE",
                                                "room": "room-test"}})
        assert self._push(client, "ringing").status_code == 200
        assert _call_state()["status"] == "ringing"
        assert self._push(client, "connected").status_code == 200
        assert _call_state()["status"] == "in_call"
        assert self._push(client, "ended", reason="remote_end").status_code == 200
        assert _call_state()["status"] == "ended"
        assert _call_state()["call_id"] is None
        assert livekit_ok["deleted"] == ["room-test"]
        texts = _system_texts()
        assert any("ringing" in t for t in texts)
        assert any("connected" in t for t in texts)
        assert any("ended" in t for t in texts)

    def test_duplicate_ended_is_noop(self, client, thread, livekit_ok):
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00CAFE"}})
        self._push(client, "ended")
        first = _system_texts()
        r = self._push(client, "ended")
        assert r.json().get("duplicate") is True
        assert _system_texts() == first  # no second "ended" breadcrumb

    def test_unknown_thread_ignored(self, client, db):
        r = client.post("/api/whatsapp/baileys/call-event",
                        data=json.dumps({"wa_id": "999000111", "event": "ringing"}),
                        content_type="application/json", **SECRET_HDR)
        assert r.json().get("ignored") is True


@pytest.mark.django_db
class TestLiveMonitor:
    """The transcript + steer seams the /chats live monitor reuses. The voice
    agent's file store is stubbed so no voice_agent import is needed."""

    @pytest.fixture
    def store_stub(self, monkeypatch):
        from whatsapp import wa_calls
        state = {"live": {"turns": [{"role": "assistant", "text": "Hi"}], "done": False},
                 "instructions": []}

        class FakeStorage:
            def get_live(self, room):
                return state["live"]

            def push_instruction(self, room, instruction):
                state["instructions"].append((room, instruction))

        monkeypatch.setattr(wa_calls, "_voice_storage", lambda: FakeStorage())
        return state

    def test_live_returns_transcript_for_active_room(self, client, superadmin, no_env_account,
                                                     baileys_account, store_stub):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "call_id": "00CAFE",
                                                "room": "room-test"}})
        r = client.get(f"/api/whatsapp/threads/{WA_ID}/call/live", **AUTH)
        assert r.status_code == 200
        body = r.json()
        assert body["room"] == "room-test"
        assert body["turns"] == [{"role": "assistant", "text": "Hi"}]
        assert body["status"] == "in_call"

    def test_live_empty_without_room(self, client, superadmin, no_env_account,
                                     baileys_account, store_stub):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        r = client.get(f"/api/whatsapp/threads/{WA_ID}/call/live", **AUTH)
        assert r.status_code == 200
        assert r.json()["turns"] == []

    def test_live_requires_auth(self, client, superadmin, no_env_account, baileys_account):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        assert client.get(f"/api/whatsapp/threads/{WA_ID}/call/live").status_code == 401

    def test_say_pushes_instruction(self, client, superadmin, no_env_account,
                                    baileys_account, store_stub):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "room": "room-test"}})
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/say",
                        data=json.dumps({"mode": "takeover", "text": "Please pay by Friday."}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 200
        assert store_stub["instructions"] == [
            ("room-test", {"mode": "takeover", "text": "Please pay by Friday."})
        ]

    def test_say_rejects_bad_mode(self, client, superadmin, no_env_account,
                                  baileys_account, store_stub):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "room": "room-test"}})
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/say",
                        data=json.dumps({"mode": "shout", "text": "hi"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 400

    def test_say_409_without_room(self, client, superadmin, no_env_account,
                                  baileys_account, store_stub):
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/say",
                        data=json.dumps({"mode": "guide", "text": "hi"}),
                        content_type="application/json", **AUTH)
        assert r.status_code == 409

    def test_escalate_pushes_guide(self, client, superadmin, no_env_account,
                                   baileys_account, store_stub):
        from whatsapp import wa_calls
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        wa_store.update_thread(WA_ID, {"call": {"status": "in_call", "room": "room-test"}})
        r = client.post(f"/api/whatsapp/threads/{WA_ID}/call/escalate", **AUTH)
        assert r.status_code == 200
        room, instr = store_stub["instructions"][-1]
        assert room == "room-test"
        assert instr["mode"] == "guide"
        assert instr["text"] == wa_calls.ESCALATE_TEXT

    def test_transcript_paired_with_recording(self, monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)

        class FakeStorage:
            def get_live(self, room):
                return {"turns": [{"role": "assistant", "text": "Hi, Northwind Trading here."},
                                  {"role": "user", "text": "Yes, speaking."}], "done": True}

        monkeypatch.setattr(wa_calls, "_voice_storage", lambda: FakeStorage())
        ok = wa_calls.save_call_transcript(f"{WA_ID}-1000.wav", "room-x")
        assert ok is True
        data = json.loads((tmp_path / f"{WA_ID}-1000.json").read_text(encoding="utf-8"))
        assert data["turns"][0]["text"] == "Hi, Northwind Trading here."
        assert data["turns"][1]["role"] == "user"

    def test_transcript_skipped_when_empty(self, monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        monkeypatch.setattr(wa_calls, "_voice_storage",
                            lambda: type("S", (), {"get_live": lambda self, r: {"turns": []}})())
        assert wa_calls.save_call_transcript(f"{WA_ID}-1.wav", "room-x") is False
        assert not (tmp_path / f"{WA_ID}-1.json").exists()


@pytest.mark.django_db
class TestRecordings:
    def test_list_and_serve(self, client, superadmin, no_env_account, baileys_account,
                            monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        (tmp_path / f"{WA_ID}-1000.wav").write_bytes(b"RIFF....WAVEdata....")
        # paired transcript file → returned inline with the recording
        (tmp_path / f"{WA_ID}-1000.json").write_text(
            json.dumps({"turns": [{"role": "assistant", "text": "Hi"}]}), encoding="utf-8")
        client.post("/api/whatsapp/threads", data=json.dumps({"phone": f"+{WA_ID}"}),
                    content_type="application/json", **AUTH)
        r = client.get(f"/api/whatsapp/threads/{WA_ID}/call/recordings", **AUTH)
        assert r.status_code == 200
        recs = r.json()["recordings"]
        assert len(recs) == 1 and recs[0]["name"] == f"{WA_ID}-1000.wav"
        assert recs[0]["url"] == f"/api/whatsapp/calls/recording/{WA_ID}-1000.wav"
        assert recs[0]["transcript"] == [{"role": "assistant", "text": "Hi"}]
        served = client.get(f"/api/whatsapp/calls/recording/{WA_ID}-1000.wav", **AUTH)
        assert served.status_code == 200
        assert served["Content-Type"] == "audio/wav"

    def test_recording_requires_auth(self, client, superadmin, monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        (tmp_path / f"{WA_ID}-1.wav").write_bytes(b"x")  # a Profile exists (superadmin) → auth enforced
        assert client.get(f"/api/whatsapp/calls/recording/{WA_ID}-1.wav").status_code == 401

    def test_missing_recording_404(self, client, superadmin, monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        assert client.get(f"/api/whatsapp/calls/recording/{WA_ID}-nope.wav", **AUTH).status_code == 404

    def test_path_traversal_rejected(self, monkeypatch, tmp_path):
        from whatsapp import wa_calls
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        assert wa_calls.recording_path("../../secret.wav") is None  # .name strips dirs
        assert wa_calls.recording_path("notaudio.txt") is None  # wrong extension
