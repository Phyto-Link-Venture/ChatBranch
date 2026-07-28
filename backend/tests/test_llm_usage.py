"""LLM usage ledger — every AI call records tokens into crm.LlmUsage so
admins can meter and bill consumption (aicore/usage.py + the wiring in
assistant/memory/wa_calls + GET /api/analytics/usage).

Fully offline: OpenAI clients are scripted fakes, the voice path feeds a fake
live store. Covers the recorder contract (usage objects, zero-skip, contact
resolution), reply + tool-loop accumulation, memory summaries, the voice-call
fold-in, price estimation (text + audio + Setting override) and the usage
endpoint (auth, totals, per-model priced flags)."""
import json
import types

import pytest
from django.contrib.auth.models import User

from aicore import assistant
from aicore import usage as usage_ledger
from crm.models import Contact, LlmUsage, Profile, Setting

AUTH = {"HTTP_AUTHORIZATION": "Bearer usage-admin-token"}


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="usage_admin", password="x")
    Profile.objects.create(user=u, role="admin", token="usage-admin-token")
    return u


@pytest.fixture
def contact(db):
    return Contact.objects.create(name="Aminah", phone="+60123456789",
                                  status="active")


def _usage(pt, ct):
    return types.SimpleNamespace(prompt_tokens=pt, completion_tokens=ct)


# ---- recorder contract ------------------------------------------------------

@pytest.mark.django_db
class TestRecord:
    def test_records_from_usage_object(self, contact):
        usage_ledger.record("chat_reply", provider="openai", model="gpt-4o-mini",
                            usage=_usage(120, 40), contact=contact,
                            channel="whatsapp", ref="60123456789")
        row = LlmUsage.objects.get()
        assert (row.prompt_tokens, row.completion_tokens) == (120, 40)
        assert row.contact_id == contact.pk
        assert row.feature == "chat_reply" and row.model == "gpt-4o-mini"

    def test_zero_usage_records_nothing(self, db):
        usage_ledger.record("chat_reply", model="gpt-4o-mini",
                            prompt_tokens=0, completion_tokens=0)
        assert LlmUsage.objects.count() == 0

    def test_input_output_token_shape(self, db):
        # realtime-style usage objects name the fields input/output
        u = types.SimpleNamespace(input_tokens=10, output_tokens=5)
        usage_ledger.record("voice_call", model="gpt-realtime", usage=u)
        row = LlmUsage.objects.get()
        assert (row.prompt_tokens, row.completion_tokens) == (10, 5)

    def test_contact_id_resolution(self, contact):
        usage_ledger.record("summary", model="gpt-4o-mini",
                            prompt_tokens=9, completion_tokens=1,
                            contact_id=contact.pk)
        assert LlmUsage.objects.get().contact_id == contact.pk

    def test_never_raises(self, db, monkeypatch):
        # a broken ledger must not break the reply path
        monkeypatch.setattr("crm.models.LlmUsage.objects", None)
        usage_ledger.record("chat_reply", model="m", prompt_tokens=1,
                            completion_tokens=1)  # must not raise


# ---- pricing ----------------------------------------------------------------

@pytest.mark.django_db
class TestPricing:
    def test_default_price_math(self, db):
        # gpt-4o-mini: 0.15 in / 0.60 out per 1M
        cost = usage_ledger.estimate_cost("gpt-4o-mini", 1_000_000, 1_000_000)
        assert cost == pytest.approx(0.75)

    def test_audio_tokens_priced_separately(self, db):
        cost = usage_ledger.estimate_cost("gpt-realtime", 0, 0,
                                          audio_prompt_tokens=1_000_000,
                                          audio_completion_tokens=0)
        assert cost == pytest.approx(32.0)

    def test_unknown_model_costs_zero(self, db):
        assert usage_ledger.estimate_cost("mystery-9000", 5_000_000, 5_000_000) == 0

    def test_setting_overrides_price(self, db):
        Setting.objects.create(key="llm_prices",
                               value=json.dumps({"gpt-4o-mini": [1.0, 2.0]}))
        cost = usage_ledger.estimate_cost("gpt-4o-mini", 1_000_000, 1_000_000)
        assert cost == pytest.approx(3.0)

    def test_malformed_price_rows_ignored(self, db):
        Setting.objects.create(key="llm_prices",
                               value=json.dumps({"gpt-4o-mini": "cheap"}))
        assert usage_ledger.price_table()["gpt-4o-mini"] == (0.15, 0.60)


# ---- generate_reply wiring --------------------------------------------------

class _UsageOpenAI:
    """Scripted client whose responses carry usage payloads."""

    scripts: list = []

    def __init__(self, base_url=None, api_key=None):
        class _Completions:
            def create(self, **kwargs):
                msg, usage = _UsageOpenAI.scripts.pop(0)
                return types.SimpleNamespace(
                    choices=[types.SimpleNamespace(message=msg)], usage=usage)

        self.chat = types.SimpleNamespace(completions=_Completions())


def _prose(content):
    return types.SimpleNamespace(content=content, tool_calls=None)


def _tool_call(call_id, name, arguments="{}"):
    return types.SimpleNamespace(
        id=call_id, function=types.SimpleNamespace(name=name, arguments=arguments))


@pytest.fixture
def fake_openai(monkeypatch):
    import openai

    _UsageOpenAI.scripts = []
    monkeypatch.setattr(openai, "OpenAI", _UsageOpenAI)
    monkeypatch.setattr(assistant, "_provider",
                        lambda: ("https://openrouter.ai/api/v1", "k", "openai/gpt-4o-mini"))
    return _UsageOpenAI


@pytest.mark.django_db
class TestReplyWiring:
    def test_plain_reply_records_one_row(self, contact, fake_openai):
        fake_openai.scripts = [(_prose("Hello!"), _usage(200, 30))]
        out = assistant.generate_reply(
            [{"role": "user", "content": "hi"}], contact_name="Aminah",
            phone="+60123456789", channel="whatsapp", contact_id=contact.pk)
        assert out == "Hello!"
        row = LlmUsage.objects.get()
        assert row.feature == "chat_reply" and row.provider == "openrouter"
        assert (row.prompt_tokens, row.completion_tokens) == (200, 30)
        assert row.contact_id == contact.pk and row.channel == "whatsapp"

    def test_tool_loop_rounds_are_summed(self, contact, fake_openai, monkeypatch):
        # calendar connected -> tools offered -> 2 completions in one reply
        from datetime import timedelta

        from django.utils import timezone

        from integrations import actions
        from integrations.models import Integration

        Integration.objects.create(
            provider="google_calendar", name="Cal", status="healthy",
            credentials={"access_token": "t",
                         "expires_at": (timezone.now() + timedelta(hours=1)).isoformat()},
            config={})
        monkeypatch.setattr(actions, "_request",
                            lambda *a, **k: (True, {"calendars": {"primary": {"busy": []}}}))
        fake_openai.scripts = [
            (types.SimpleNamespace(content=None,
                                   tool_calls=[_tool_call("c1", "check_calendar_availability")]),
             _usage(500, 20)),
            (_prose("I have Monday 9am free."), _usage(700, 40)),
        ]
        assistant.generate_reply(
            [{"role": "user", "content": "book me"}], contact_name="Aminah",
            phone="+60123456789", channel="whatsapp", contact_id=contact.pk)
        row = LlmUsage.objects.get(feature="chat_reply")
        assert (row.prompt_tokens, row.completion_tokens) == (1200, 60)
        assert row.detail["rounds"] == 2

    def test_no_usage_payload_records_nothing(self, contact, fake_openai):
        fake_openai.scripts = [(_prose("ok"), None)]
        assistant.generate_reply(
            [{"role": "user", "content": "hi"}], contact_name="A",
            phone="+60123456789", channel="whatsapp", contact_id=contact.pk)
        assert LlmUsage.objects.count() == 0


# ---- memory summaries -------------------------------------------------------

@pytest.mark.django_db
class TestMemoryWiring:
    def test_chat_text_records_summary_usage(self, contact, monkeypatch):
        import openai

        from aicore import memory

        _UsageOpenAI.scripts = [(_prose('{"summary": "s"}'), _usage(300, 50))]
        monkeypatch.setattr(openai, "OpenAI", _UsageOpenAI)
        memory._chat_text("https://api.openai.com/v1", "k", "gpt-4o-mini",
                          "prompt", contact=contact, channel="whatsapp",
                          ref="chat")
        row = LlmUsage.objects.get()
        assert row.feature == "summary" and row.provider == "openai"
        assert (row.prompt_tokens, row.completion_tokens) == (300, 50)
        assert row.contact_id == contact.pk


# ---- voice calls (live store -> ledger) -------------------------------------

@pytest.mark.django_db
class TestVoiceWiring:
    def test_save_call_transcript_folds_usage(self, contact, monkeypatch, tmp_path):
        from crm.models import Thread
        from whatsapp import store as wa_store
        from whatsapp import wa_calls

        wa_store.get_or_create_thread("60123456789")
        Thread.objects.filter(channel="whatsapp", key="60123456789").update(contact=contact)
        live = {"turns": [{"role": "user", "text": "hello", "ts": 1.0}],
                "ended_by": "agent",
                "llm_usage": {"prompt": 100, "completion": 60,
                              "audio_prompt": 4000, "audio_completion": 2500,
                              "model": "gpt-realtime", "provider": "openai",
                              "summary": {"model": "openai/gpt-4o-mini",
                                          "prompt": 800, "completion": 90}}}
        monkeypatch.setattr(wa_calls, "_voice_storage",
                            lambda: types.SimpleNamespace(get_live=lambda room: live))
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        # keep the memory fold-in synchronous-free: its daemon thread would
        # race the test DB (sqlite lock)
        monkeypatch.setattr(wa_calls, "_summarize_call_async", lambda *a, **k: None)
        ok = wa_calls.save_call_transcript("60123456789-1785000000000.wav", "room-1")
        assert ok is True
        call_row = LlmUsage.objects.get(feature="voice_call")
        assert call_row.model == "gpt-realtime"
        assert (call_row.prompt_tokens, call_row.completion_tokens) == (100, 60)
        assert (call_row.audio_prompt_tokens, call_row.audio_completion_tokens) == (4000, 2500)
        assert call_row.contact_id == contact.pk and call_row.ref == "room-1"
        sum_row = LlmUsage.objects.get(feature="summary")
        assert (sum_row.prompt_tokens, sum_row.completion_tokens) == (800, 90)
        # the persisted transcript carries the same usage for transparency
        saved = json.loads((tmp_path / "60123456789-1785000000000.json").read_text())
        assert saved["meta"]["llm_usage"]["model"] == "gpt-realtime"


# ---- the usage endpoint -----------------------------------------------------

@pytest.mark.django_db
class TestUsageEndpoint:
    URL = "/api/analytics/usage"

    def _seed(self, contact):
        LlmUsage.objects.create(feature="chat_reply", provider="openrouter",
                                model="openai/gpt-4o-mini", channel="whatsapp",
                                contact=contact, prompt_tokens=1_000_000,
                                completion_tokens=1_000_000)
        LlmUsage.objects.create(feature="voice_call", provider="openai",
                                model="gpt-realtime", channel="whatsapp",
                                contact=contact, prompt_tokens=0,
                                completion_tokens=0,
                                audio_prompt_tokens=1_000_000,
                                audio_completion_tokens=0)
        LlmUsage.objects.create(feature="summary", model="mystery-9000",
                                prompt_tokens=50, completion_tokens=10)

    def test_requires_auth(self, client, admin):
        assert client.get(self.URL).status_code == 401

    def test_totals_and_costs(self, client, admin, contact):
        self._seed(contact)
        d = client.get(self.URL, **AUTH).json()
        t = d["totals"]
        assert t["prompt_tokens"] == 1_000_050
        assert t["completion_tokens"] == 1_000_010
        assert t["audio_prompt_tokens"] == 1_000_000
        assert t["requests"] == 3
        # 0.75 (gpt-4o-mini text) + 32.0 (realtime audio) + 0 (unpriced)
        assert t["est_cost_usd"] == pytest.approx(32.75, abs=0.01)
        models = {r["model"]: r for r in d["by_model"]}
        assert models["gpt-realtime"]["est_cost_usd"] == pytest.approx(32.0)
        assert models["mystery-9000"]["priced"] is False
        features = {r["feature"] for r in d["by_feature"]}
        assert features == {"chat_reply", "voice_call", "summary"}
        top = d["by_contact"][0]
        assert top["contact_id"] == contact.pk and top["name"] == "Aminah"
        assert top["est_cost_usd"] == pytest.approx(32.75, abs=0.01)
        assert len(d["daily"]) == 30
        assert sum(x["tokens"] for x in d["daily"]) == 3_000_060

    def test_price_setting_applies(self, client, admin, contact):
        LlmUsage.objects.create(feature="chat_reply", model="custom-llm",
                                prompt_tokens=1_000_000, completion_tokens=0,
                                contact=contact)
        Setting.objects.create(key="llm_prices",
                               value=json.dumps({"custom-llm": [5.0, 10.0]}))
        d = client.get(self.URL, **AUTH).json()
        assert d["totals"]["est_cost_usd"] == pytest.approx(5.0)
