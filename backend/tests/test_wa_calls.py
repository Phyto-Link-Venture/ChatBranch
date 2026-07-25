"""wa_calls process-control tests: pid liveness, stale-state recovery,
per-call log files, and the sandbox lifecycle.

Everything runs offline: subprocess.Popen and _pid_alive are monkeypatched —
no bridge process is ever spawned and aiortc/livekit are never imported.
"""
import os
import time

import pytest

from whatsapp import store, wa_calls

WA_ID = "60123456789"
FAKE_ACCOUNT = {"id": "1", "name": "Test", "access_token": "tok", "phone_number_id": "111"}


class StubPopen:
    """Stands in for subprocess.Popen — records each launch, returns a pid."""

    def __init__(self, pid=4242):
        self.pid = pid
        self.launches = []

    def __call__(self, cmd, **kwargs):
        self.launches.append((cmd, kwargs))
        return self


@pytest.fixture
def calls_dir(tmp_path, monkeypatch):
    """Redirect all signal/log files away from the real data/ directory."""
    monkeypatch.setattr(wa_calls, "CALLS_DIR", tmp_path)
    return tmp_path


@pytest.fixture
def thread(db):
    return store.get_or_create_thread(f"+{WA_ID}", name="Tester")


@pytest.fixture
def sandbox(monkeypatch):
    monkeypatch.setattr(wa_calls, "_account_for", lambda wa_id: None)


@pytest.fixture
def real_account(monkeypatch):
    monkeypatch.setattr(wa_calls, "_account_for", lambda wa_id: dict(FAKE_ACCOUNT))
    monkeypatch.setattr(wa_calls.client, "get_call_permission",
                        lambda account, wa_id: "temporary")


@pytest.fixture
def popen(monkeypatch):
    stub = StubPopen()
    monkeypatch.setattr(wa_calls.subprocess, "Popen", stub)
    return stub


def _alive(monkeypatch, value):
    monkeypatch.setattr(wa_calls, "_pid_alive", lambda pid: value)


def _messages(wa_id=WA_ID):
    return [m["text"] for m in (store.load_thread(wa_id) or {}).get("messages", [])]


class TestPidAlive:
    """The real helper, probed only with safe values (our own pid, garbage)."""

    def test_own_pid_is_alive(self):
        assert wa_calls._pid_alive(os.getpid()) is True

    def test_bogus_pids_are_dead(self):
        assert wa_calls._pid_alive(None) is False
        assert wa_calls._pid_alive("nope") is False
        assert wa_calls._pid_alive(-7) is False
        assert wa_calls._pid_alive(0) is False


@pytest.mark.django_db
class TestSandboxLifecycle:
    def test_lifecycle_unchanged(self, thread, sandbox, calls_dir):
        r = wa_calls.start_call(WA_ID)
        assert r == {"call_id": "wacid.sandbox", "status": "in_call", "sandbox": True}
        # double start still refuses — no signal files / pid to prove staleness
        assert "error" in wa_calls.start_call(WA_ID)
        assert wa_calls.end_call(WA_ID)["status"] == "ended"
        assert wa_calls.start_call(WA_ID)["status"] == "in_call"


@pytest.mark.django_db
class TestDoubleStart:
    def test_live_pid_conflicts(self, thread, real_account, popen, calls_dir, monkeypatch):
        _alive(monkeypatch, True)
        first = wa_calls.start_call(WA_ID)
        assert first["status"] == "connecting"
        assert wa_calls.read_pending_signal(WA_ID)["pid"] == popen.pid
        second = wa_calls.start_call(WA_ID)
        assert second == {"error": "A call is already in progress for this contact."}
        assert len(popen.launches) == 1  # no second bridge spawned

    def test_dead_pid_recovers_and_proceeds(self, thread, real_account, popen,
                                            calls_dir, monkeypatch):
        _alive(monkeypatch, True)
        assert wa_calls.start_call(WA_ID)["status"] == "connecting"
        _alive(monkeypatch, False)  # bridge crashed / machine rebooted
        second = wa_calls.start_call(WA_ID)
        assert second["status"] == "connecting"  # proceeded instead of 409
        assert len(popen.launches) == 2
        assert "[stale call state recovered]" in _messages()
        assert wa_calls.read_pending_signal(WA_ID)["pid"] == popen.pid  # fresh record

    def test_dead_pid_recovery_cleans_call_signal(self, thread, real_account, popen,
                                                  calls_dir, monkeypatch):
        _alive(monkeypatch, False)
        # a ringing call whose bridge died: thread state + signal file linger
        store.update_thread(WA_ID, {"call": {"call_id": "wacid.dead1", "status": "ringing"}})
        wa_calls.write_call_signal("wacid.dead1", {"pid": 987654, "wa_id": WA_ID})
        r = wa_calls.start_call(WA_ID)
        assert r["status"] == "connecting"
        assert wa_calls.read_call_signal("wacid.dead1") == {}  # stale file removed
        assert not wa_calls._signal_path("wacid.dead1").exists()

    def test_pidless_recent_files_stay_conservative(self, thread, real_account, popen,
                                                    calls_dir, monkeypatch):
        _alive(monkeypatch, False)
        store.update_thread(WA_ID, {"call": {"status": "connecting"}})
        wa_calls.write_pending_signal(WA_ID, {"wa_id": WA_ID})  # no pid recorded
        assert "error" in wa_calls.start_call(WA_ID)  # young file -> keep the 409

    def test_pidless_ancient_files_recover(self, thread, real_account, popen,
                                           calls_dir, monkeypatch):
        _alive(monkeypatch, False)
        store.update_thread(WA_ID, {"call": {"status": "connecting"}})
        wa_calls.write_pending_signal(WA_ID, {"wa_id": WA_ID})
        old = time.time() - wa_calls.STALE_AFTER_SECONDS - 60
        os.utime(wa_calls._pending_path(WA_ID), (old, old))
        assert wa_calls.start_call(WA_ID)["status"] == "connecting"
        assert "[stale call state recovered]" in _messages()


@pytest.mark.django_db
class TestEndCall:
    def test_dead_pid_resets_state_directly(self, thread, real_account, calls_dir,
                                            monkeypatch):
        _alive(monkeypatch, False)
        store.update_thread(WA_ID, {"call": {"call_id": "wacid.dead2", "status": "in_call"}})
        wa_calls.write_call_signal("wacid.dead2", {"pid": 424242, "wa_id": WA_ID})
        r = wa_calls.end_call(WA_ID)
        assert r == {"status": "ended", "recovered": True}
        call = (store.load_thread(WA_ID) or {}).get("call") or {}
        assert call.get("call_id") is None
        assert call.get("status") == "ended"
        # no hangup signal written for a bridge nobody can hear
        assert not wa_calls._signal_path("wacid.dead2").exists()

    def test_live_pid_writes_hangup_signal(self, thread, real_account, calls_dir,
                                           monkeypatch):
        _alive(monkeypatch, True)
        store.update_thread(WA_ID, {"call": {"call_id": "wacid.live1", "status": "in_call"}})
        wa_calls.write_call_signal("wacid.live1", {"pid": 424242, "wa_id": WA_ID})
        r = wa_calls.end_call(WA_ID)
        assert r == {"status": "ending"}
        assert wa_calls.read_call_signal("wacid.live1").get("hangup") is True

    def test_connecting_dead_pid_resets(self, thread, real_account, calls_dir,
                                        monkeypatch):
        _alive(monkeypatch, False)
        store.update_thread(WA_ID, {"call": {"status": "connecting"}})
        wa_calls.write_pending_signal(WA_ID, {"pid": 424242, "wa_id": WA_ID})
        r = wa_calls.end_call(WA_ID)
        assert r == {"status": "ended", "recovered": True}
        assert wa_calls.read_pending_signal(WA_ID) == {}


@pytest.mark.django_db
class TestCallLogs:
    def test_start_call_creates_log_file_and_records_it(self, thread, real_account,
                                                        popen, calls_dir, monkeypatch):
        _alive(monkeypatch, True)
        r = wa_calls.start_call(WA_ID)
        assert r["status"] == "connecting"
        logs = list((calls_dir / "logs").glob("*.log"))
        assert len(logs) == 1
        assert logs[0].name.startswith(WA_ID)
        assert r["log"] == logs[0].name
        # Popen was handed the log file as stdout, stderr folded in
        cmd, kwargs = popen.launches[0]
        assert kwargs["stdout"].name.endswith(logs[0].name)
        assert kwargs["stderr"] == wa_calls.subprocess.STDOUT
        # the pending record + thread message point operators at the log
        assert wa_calls.read_pending_signal(WA_ID)["log"] == logs[0].name
        assert any(logs[0].name in m for m in _messages())

    def test_retention_sweep_deletes_old_logs(self, thread, real_account, popen,
                                              calls_dir, monkeypatch):
        _alive(monkeypatch, True)
        logs_dir = calls_dir / "logs"
        logs_dir.mkdir(parents=True, exist_ok=True)
        ancient = logs_dir / "999-old.log"
        ancient.write_text("old")
        old = time.time() - (wa_calls.LOG_RETENTION_DAYS + 1) * 86400
        os.utime(ancient, (old, old))
        fresh = logs_dir / "999-fresh.log"
        fresh.write_text("fresh")
        wa_calls.start_call(WA_ID)
        assert not ancient.exists()
        assert fresh.exists()
