"""AI memory system + assistant persona configuration (aicore).

Covers: memory.build_context composition/ordering/truncation,
summarize_conversation with a mocked LLM (fenced JSON, profile updates, the
no-key sandbox rule, bad JSON), the idle-chat summarizer's selection logic
(idle burst summarized exactly once, active/unchanged threads skipped), the
call-transcript hook in wa_calls, the /api/aicore/persona endpoints
(superadmin gate, reset-to-default, audit), the assistant prompt composition
(hard rules + persona override + CONTACT MEMORY block), and the Baileys
dispatch metadata carrying the memory string into the voice agent's
instructions. Fully offline — no OpenAI, no LiveKit SDK.
"""
import json
import sys
import types
from datetime import date, timedelta
from pathlib import Path

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from aicore import assistant, memory, persona
from crm.models import (
    AuditLog,
    ConversationSummary,
    Contact,
    Message,
    Profile,
    Record,
    Setting,
    Thread,
)
from whatsapp import baileys_calls, wa_calls

TOKEN_SUPER = "memtest-super-token"
TOKEN_ADMIN = "memtest-admin-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_SUPER}"}
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
WA_ID = "60111222333"


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="mem_super", password="x")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN_SUPER)
    return user


@pytest.fixture
def admin(db):
    user = User.objects.create_user(username="mem_admin", password="x")
    Profile.objects.create(user=user, role="admin", token=TOKEN_ADMIN)
    return user


@pytest.fixture
def contact(db):
    c = Contact.objects.create(
        name="Aminah", phone=f"+{WA_ID}", language="ms",
        tags=["vip"], attributes={"tier": "gold"},
        preferred_contact_time="evenings after 8pm",
        communication_style="formal Bahasa Malaysia",
        sentiment_trend="negative",
        ai_profile_summary="Salary arrives on the 25th; responds best on WhatsApp.")
    Record.objects.create(contact=c, type="order", external_id="ORD-77",
                          title="Weekly grocery box", status="packed",
                          amount=1200, currency="MYR", due_date=date(2026, 7, 20))
    return c


def _mk_summary(contact, text, days_ago, source="chat", ref=f"whatsapp:{WA_ID}"):
    s = ConversationSummary.objects.create(
        contact=contact, source=source, channel="whatsapp", ref=ref, summary=text)
    ConversationSummary.objects.filter(pk=s.pk).update(
        created_at=timezone.now() - timedelta(days=days_ago))
    return s


# ---- memory.build_context ------------------------------------------------------

@pytest.mark.django_db
class TestBuildContext:
    def test_composes_profile_prefs_and_records(self, contact):
        ctx = memory.build_context(contact)
        assert "Aminah" in ctx
        assert "ORD-77" in ctx                               # open record reference
        assert "MYR 1200" in ctx                             # record amount
        assert "Bahasa Malaysia" in ctx                      # language
        assert "evenings after 8pm" in ctx                   # preferred_contact_time
        assert "formal Bahasa Malaysia" in ctx               # communication_style
        assert "negative" in ctx                             # sentiment_trend
        assert "Salary arrives on the 25th" in ctx           # ai_profile_summary
        assert "Open records" in ctx
        assert "tier: gold" in ctx                           # attributes

    def test_unset_prefs_omitted(self, db):
        c = Contact.objects.create(name="Bare", phone="+60100000001")
        ctx = memory.build_context(c)
        assert "Preferred contact time" not in ctx
        assert "Communication style" not in ctx
        assert "sentiment trend" not in ctx
        assert "Profile notes" not in ctx
        assert "Recent conversations" not in ctx

    def test_last_n_summaries_newest_first(self, contact):
        for i in range(6):
            _mk_summary(contact, f"Summary number {i}", days_ago=6 - i)
        ctx = memory.build_context(contact, max_summaries=5)
        assert "Summary number 0" not in ctx      # 6th-newest falls outside the window
        assert "Summary number 5" in ctx
        assert "Summary number 1" in ctx
        assert ctx.index("Summary number 5") < ctx.index("Summary number 1")  # newest first

    def test_truncates_oldest_summaries_first(self, contact):
        for i in range(5):
            _mk_summary(contact, f"S{i}marker " + "x" * 400, days_ago=5 - i)
        ctx = memory.build_context(contact)
        assert len(ctx) <= memory.CONTEXT_BUDGET
        assert "S4marker" in ctx      # newest survives
        assert "S0marker" not in ctx  # oldest dropped first

    def test_source_tag_in_summary_line(self, contact):
        _mk_summary(contact, "Promised RM200 by Friday.", days_ago=2, source="call", ref="room-1")
        ctx = memory.build_context(contact)
        assert "call] Promised RM200 by Friday." in ctx

    def test_never_raises(self):
        assert memory.build_context(None) == ""


# ---- memory.summarize_conversation ----------------------------------------------

FENCED_REPLY = """```json
{
  "summary": "Contact promised RM200 by Friday and asked to be contacted after 8pm.",
  "facts": ["Salary arrives on the 25th"],
  "sentiment": "negative",
  "profile_updates": {
    "preferred_contact_time": "after 8pm",
    "communication_style": "short, direct BM",
    "ai_profile_summary": "Under income delay; keeps small promises."
  }
}
```"""


@pytest.mark.django_db
class TestSummarizeConversation:
    @pytest.fixture
    def provider_ok(self, monkeypatch):
        monkeypatch.setattr(memory, "_provider", lambda: ("http://llm", "key-1", "model-x"))

    def test_fenced_json_parsed_and_profile_applied(self, contact, provider_ok, monkeypatch):
        prompts = []
        monkeypatch.setattr(
            memory, "_chat_text",
            lambda base, key, model, prompt, **kw: prompts.append(prompt) or FENCED_REPLY)
        row = memory.summarize_conversation(
            contact, "call", "whatsapp", "room-1",
            "Agent: hello\nCustomer: I get paid on the 25th")
        assert row is not None
        assert row.source == "call" and row.channel == "whatsapp" and row.ref == "room-1"
        assert row.summary.startswith("Contact promised RM200")
        assert row.facts == ["Salary arrives on the 25th"]
        assert row.sentiment == "negative"
        assert "I get paid on the 25th" in prompts[0]  # transcript reached the LLM
        contact.refresh_from_db()
        assert contact.preferred_contact_time == "after 8pm"
        assert contact.communication_style == "short, direct BM"
        assert contact.ai_profile_summary == "Under income delay; keeps small promises."
        assert contact.sentiment_trend == "negative"

    def test_empty_profile_updates_ignored(self, contact, provider_ok, monkeypatch):
        reply = json.dumps({
            "summary": "Chat happened.", "facts": [], "sentiment": "neutral",
            "profile_updates": {"preferred_contact_time": "", "ai_profile_summary": "  "},
        })
        monkeypatch.setattr(memory, "_chat_text", lambda *a, **kw: reply)
        row = memory.summarize_conversation(contact, "chat", "whatsapp",
                                            f"whatsapp:{WA_ID}", "Contact: hi")
        assert row is not None
        contact.refresh_from_db()
        assert contact.preferred_contact_time == "evenings after 8pm"      # unchanged
        assert contact.ai_profile_summary.startswith("Salary arrives")     # unchanged
        assert contact.sentiment_trend == "neutral"  # trend always follows the newest

    def test_no_api_key_returns_none(self, contact, monkeypatch):
        monkeypatch.setattr(memory, "_provider", lambda: ("http://llm", "", "model-x"))
        monkeypatch.setattr(
            memory, "_chat_text",
            lambda *a, **kw: (_ for _ in ()).throw(AssertionError("must not call the LLM")))
        assert memory.summarize_conversation(contact, "chat", "whatsapp",
                                             "r", "Contact: hi") is None
        assert ConversationSummary.objects.count() == 0

    def test_bad_json_returns_none(self, contact, provider_ok, monkeypatch):
        monkeypatch.setattr(memory, "_chat_text", lambda *a, **kw: "sorry, no JSON here { broken")
        assert memory.summarize_conversation(contact, "chat", "whatsapp",
                                             "r", "Contact: hi") is None
        assert ConversationSummary.objects.count() == 0

    def test_llm_exception_swallowed(self, contact, provider_ok, monkeypatch):
        def boom(*a, **kw):
            raise RuntimeError("provider down")
        monkeypatch.setattr(memory, "_chat_text", boom)
        assert memory.summarize_conversation(contact, "chat", "whatsapp",
                                             "r", "Contact: hi") is None

    def test_empty_transcript_skipped(self, contact, provider_ok, monkeypatch):
        monkeypatch.setattr(
            memory, "_chat_text",
            lambda *a, **kw: (_ for _ in ()).throw(AssertionError("must not call the LLM")))
        assert memory.summarize_conversation(contact, "chat", "whatsapp", "r", "   ") is None

    def test_invalid_sentiment_blanked(self, contact, provider_ok, monkeypatch):
        reply = json.dumps({"summary": "ok", "facts": [], "sentiment": "ANGRY!!",
                            "profile_updates": {}})
        monkeypatch.setattr(memory, "_chat_text", lambda *a, **kw: reply)
        row = memory.summarize_conversation(contact, "chat", "whatsapp", "r", "Contact: hi")
        assert row is not None and row.sentiment == ""
        contact.refresh_from_db()
        assert contact.sentiment_trend == "negative"  # untouched


# ---- the idle-chat summarizer -----------------------------------------------------

def _mk_thread(contact, key=WA_ID, channel="whatsapp"):
    return Thread.objects.create(channel=channel, key=key, name="Aminah", contact=contact)


def _mk_message(thread, text, minutes_ago, direction="in", source="customer"):
    m = Message.objects.create(thread=thread, direction=direction, source=source, text=text)
    Message.objects.filter(pk=m.pk).update(
        created_at=timezone.now() - timedelta(minutes=minutes_ago))
    return m


@pytest.mark.django_db
class TestIdleSummarizer:
    @pytest.fixture
    def recorder(self, monkeypatch):
        calls = []

        def fake_summarize(contact, source, channel, ref, transcript_text):
            calls.append({"contact": contact, "source": source, "channel": channel,
                          "ref": ref, "transcript": transcript_text})
            return ConversationSummary.objects.create(
                contact=contact, source=source, channel=channel, ref=ref, summary="stub")

        monkeypatch.setattr(memory, "summarize_conversation", fake_summarize)
        return calls

    def test_idle_thread_summarized_exactly_once(self, contact, recorder):
        t = _mk_thread(contact)
        _mk_message(t, "hello, I need more time", minutes_ago=200)
        _mk_message(t, "sure - how much can you pay?", minutes_ago=190,
                    direction="out", source="ai")
        assert memory.summarize_idle_chats() == 1
        assert len(recorder) == 1
        call = recorder[0]
        assert call["contact"] == contact
        assert call["source"] == "chat" and call["channel"] == "whatsapp"
        assert call["ref"] == f"whatsapp:{WA_ID}"
        assert "Contact: hello, I need more time" in call["transcript"]
        assert "Agent: sure - how much can you pay?" in call["transcript"]
        # unchanged thread -> cheap skip, no second summary
        assert memory.summarize_idle_chats() == 0
        assert len(recorder) == 1

    def test_active_thread_skipped(self, contact, recorder):
        t = _mk_thread(contact)
        _mk_message(t, "recent message", minutes_ago=5)
        assert memory.summarize_idle_chats() == 0
        assert recorder == []

    def test_thread_without_contact_skipped(self, db, recorder):
        t = Thread.objects.create(channel="whatsapp", key="60999888777", name="Stranger")
        _mk_message(t, "who is this", minutes_ago=300)
        assert memory.summarize_idle_chats() == 0
        assert recorder == []

    def test_new_burst_summarizes_only_new_messages(self, contact, recorder):
        t = _mk_thread(contact)
        _mk_message(t, "old burst message", minutes_ago=600)
        assert memory.summarize_idle_chats() == 1
        # age the first summary, then a NEW burst arrives and goes idle
        ConversationSummary.objects.all().update(
            created_at=timezone.now() - timedelta(minutes=500))
        _mk_message(t, "new message after summary", minutes_ago=150)
        assert memory.summarize_idle_chats() == 1
        assert len(recorder) == 2
        assert "new message after summary" in recorder[1]["transcript"]
        assert "old burst message" not in recorder[1]["transcript"]

    def test_system_messages_do_not_retrigger(self, contact, recorder):
        t = _mk_thread(contact)
        _mk_message(t, "real message", minutes_ago=300)
        assert memory.summarize_idle_chats() == 1
        _mk_message(t, "[call ended]", minutes_ago=10, direction="out", source="system")
        assert memory.summarize_idle_chats() == 0  # only system chatter since
        assert len(recorder) == 1

    def test_failed_summary_not_counted(self, contact, monkeypatch):
        monkeypatch.setattr(memory, "summarize_conversation", lambda *a, **kw: None)
        t = _mk_thread(contact)
        _mk_message(t, "hello", minutes_ago=200)
        assert memory.summarize_idle_chats() == 0  # sandbox / LLM failure -> no count


# ---- the call-transcript hook (wa_calls) -------------------------------------------

TURNS = [{"role": "assistant", "text": "Hello, this is Northwind Trading."},
         {"role": "user", "text": "I can pay Friday."}]


@pytest.mark.django_db
class TestCallSummaryHook:
    def test_summarize_call_resolves_contact(self, contact, monkeypatch):
        calls = []

        def fake(cust, source, channel, ref, transcript_text):
            calls.append({"contact": cust, "source": source, "ref": ref,
                          "transcript": transcript_text})

        monkeypatch.setattr(memory, "summarize_conversation", fake)
        wa_calls._summarize_call(f"{WA_ID}-1000", "room-7", TURNS)
        assert len(calls) == 1
        assert calls[0]["contact"] == contact      # matched by phone digits
        assert calls[0]["source"] == "call"
        assert calls[0]["ref"] == "room-7"
        assert "Agent: Hello, this is Northwind Trading." in calls[0]["transcript"]
        assert "Contact: I can pay Friday." in calls[0]["transcript"]

    def test_no_contact_no_summary(self, db, monkeypatch):
        monkeypatch.setattr(memory, "summarize_conversation",
                            lambda *a, **kw: pytest.fail("must not be called"))
        wa_calls._summarize_call("99888777666-1", "room-7", TURNS)  # unknown number

    def test_save_call_transcript_spawns_hook(self, db, monkeypatch, tmp_path):
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)

        class FakeStorage:
            def get_live(self, room):
                return {"turns": TURNS, "done": True}

        monkeypatch.setattr(wa_calls, "_voice_storage", lambda: FakeStorage())
        spawned = []
        monkeypatch.setattr(wa_calls, "_summarize_call_async",
                            lambda base, room, turns: spawned.append((base, room, turns)))
        assert wa_calls.save_call_transcript(f"{WA_ID}-1000.wav", "room-7") is True
        assert spawned == [(f"{WA_ID}-1000", "room-7", TURNS)]

    def test_no_transcript_no_hook(self, monkeypatch, tmp_path):
        monkeypatch.setattr(wa_calls, "RECORDINGS_DIR", tmp_path)
        monkeypatch.setattr(
            wa_calls, "_voice_storage",
            lambda: type("S", (), {"get_live": lambda self, r: {"turns": []}})())
        spawned = []
        monkeypatch.setattr(wa_calls, "_summarize_call_async",
                            lambda *a: spawned.append(a))
        assert wa_calls.save_call_transcript(f"{WA_ID}-1.wav", "room-x") is False
        assert spawned == []


# ---- persona endpoints --------------------------------------------------------------

@pytest.mark.django_db
class TestPersonaEndpoints:
    def test_requires_auth(self, client, superadmin):
        assert client.get("/api/aicore/persona").status_code == 401

    def test_get_default(self, client, admin):
        r = client.get("/api/aicore/persona", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert body["is_default"] is True
        assert body["persona"] == persona.DEFAULT_PERSONA
        assert body["default"] == persona.DEFAULT_PERSONA

    def test_put_is_superadmin_only(self, client, superadmin, admin):
        r = client.put("/api/aicore/persona",
                       data=json.dumps({"persona": "Be a pirate."}),
                       content_type="application/json", **ADMIN)
        assert r.status_code == 403
        assert persona.is_default()

    def test_put_override_and_reset(self, client, superadmin):
        r = client.put("/api/aicore/persona",
                       data=json.dumps({"persona": "Custom persona text."}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert r.json()["is_default"] is False
        assert r.json()["persona"] == "Custom persona text."
        assert Setting.objects.get(key="aicore_persona").value == "Custom persona text."
        assert AuditLog.objects.filter(action="aicore.persona.update").exists()
        assert persona.persona_text() == "Custom persona text."
        assert "Custom persona text." in persona.system_prompt()
        assert persona.HARD_RULES in persona.system_prompt()  # hard rules NOT overridable
        # empty string resets to the default
        r = client.put("/api/aicore/persona", data=json.dumps({"persona": ""}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert r.json()["is_default"] is True
        assert r.json()["persona"] == persona.DEFAULT_PERSONA

    def test_put_non_string_rejected(self, client, superadmin):
        r = client.put("/api/aicore/persona", data=json.dumps({"persona": 42}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400

    def test_other_methods_rejected(self, client, superadmin):
        r = client.post("/api/aicore/persona", data="{}",
                        content_type="application/json", **SUPER)
        assert r.status_code == 405


# ---- assistant prompt composition ----------------------------------------------------

class _FakeOpenAI:
    """Captures chat.completions.create kwargs; returns a canned reply."""
    captured: list = []
    reply = "ok, noted"

    def __init__(self, base_url=None, api_key=None):
        outer = self

        class _Completions:
            def create(self, **kwargs):
                _FakeOpenAI.captured.append(kwargs)
                msg = types.SimpleNamespace(content=_FakeOpenAI.reply)
                choice = types.SimpleNamespace(message=msg)
                return types.SimpleNamespace(choices=[choice])

        self.chat = types.SimpleNamespace(completions=_Completions())
        del outer


@pytest.mark.django_db
class TestAssistantPrompt:
    @pytest.fixture
    def fake_llm(self, monkeypatch):
        import openai
        _FakeOpenAI.captured = []
        _FakeOpenAI.reply = "ok, noted"
        monkeypatch.setattr(openai, "OpenAI", _FakeOpenAI)
        monkeypatch.setattr(assistant, "_provider",
                            lambda: ("http://llm", "key-1", "model-x"))
        return _FakeOpenAI.captured

    def _reply(self):
        return assistant.generate_reply(
            [{"role": "user", "content": "hi"}],
            contact_name="Aminah", phone=f"+{WA_ID}", channel="whatsapp")

    def _systems(self, captured):
        return [m["content"] for m in captured[0]["messages"] if m["role"] == "system"]

    def test_default_prompt_has_hard_rules_and_memory_block(self, contact, fake_llm):
        assert self._reply() == "ok, noted"
        joined = "\n".join(self._systems(fake_llm))
        assert "Hard safety rules" in joined              # fixed layer
        assert "Never threaten, shame, harass" in joined
        assert "Personality: you are a professional" in joined  # default persona
        assert "CONTACT MEMORY" in joined                 # delimited memory block
        assert "Aminah" in joined and "ORD-77" in joined

    def test_memory_block_carries_summaries_and_prefs(self, contact, fake_llm):
        _mk_summary(contact, "Asked for Friday delivery; prefers BM.", days_ago=1)
        self._reply()
        block = next(s for s in self._systems(fake_llm) if "CONTACT MEMORY" in s)
        assert "Asked for Friday delivery; prefers BM." in block
        assert "evenings after 8pm" in block
        assert "Open records" in block

    def test_persona_override_replaces_default_keeps_hard_rules(self, contact, fake_llm):
        Setting.objects.create(key="aicore_persona",
                               value="PERSONA-OVERRIDE-MARKER speak like a kopitiam uncle")
        self._reply()
        joined = "\n".join(self._systems(fake_llm))
        assert "PERSONA-OVERRIDE-MARKER" in joined
        assert "Hard safety rules" in joined                            # still present
        assert "Personality: you are a professional" not in joined     # default replaced

    def test_no_contact_no_memory_block(self, db, fake_llm):
        assistant.generate_reply([{"role": "user", "content": "hi"}],
                                 contact_name="Stranger", phone="+60109998888",
                                 channel="whatsapp")
        assert all("CONTACT MEMORY" not in s for s in self._systems(fake_llm))

    def test_leaked_placeholders_render_from_crm(self, contact, fake_llm):
        # models copy {{link}}-style tokens verbatim out of the persona/skill
        # instructions — the reply must reach the contact with real values,
        # same variable catalog as the flow builder
        record = contact.records.get()
        record.data = {"tracking_url": "https://track.example/aminah"}
        record.save(update_fields=["data"])
        _FakeOpenAI.reply = "Order {{record_id}} ({{amount}}): {{tracking_url}}"
        assert self._reply() == (
            "Order ORD-77 (MYR 1,200.00): https://track.example/aminah")


# ---- voice: dispatch metadata + agent instructions --------------------------------

def _fake_livekit(monkeypatch, captured):
    """Inject a fake `livekit.api` so _dispatch_agent runs without the SDK."""
    class FakeDispatchAPI:
        async def create_dispatch(self, req):
            captured.append(req)

    class FakeLiveKitAPI:
        def __init__(self):
            self.agent_dispatch = FakeDispatchAPI()

        async def aclose(self):
            pass

    class FakeToken:
        def with_identity(self, *_):
            return self

        def with_name(self, *_):
            return self

        def with_grants(self, *_):
            return self

        def to_jwt(self):
            return "jwt-test"

    api = types.ModuleType("livekit.api")
    api.LiveKitAPI = FakeLiveKitAPI
    api.CreateAgentDispatchRequest = (
        lambda agent_name=None, room=None, metadata=None:
        types.SimpleNamespace(agent_name=agent_name, room=room, metadata=metadata))
    api.AccessToken = FakeToken
    api.VideoGrants = lambda **kw: kw
    pkg = types.ModuleType("livekit")
    pkg.api = api
    monkeypatch.setitem(sys.modules, "livekit", pkg)
    monkeypatch.setitem(sys.modules, "livekit.api", api)


@pytest.mark.django_db
class TestDispatchMemoryMetadata:
    def test_dispatch_metadata_carries_memory(self, contact, monkeypatch):
        _mk_summary(contact, "Asked for Friday delivery; prefers BM.", days_ago=1)
        captured = []
        _fake_livekit(monkeypatch, captured)
        room, token = baileys_calls._dispatch_agent(WA_ID, None)
        assert token == "jwt-test" and room.startswith("wa-call-")
        meta = json.loads(captured[0].metadata)
        assert meta["contact_name"] == "Aminah"
        assert meta["record_id"] == "ORD-77"
        assert "Asked for Friday delivery" in meta["memory"]
        assert "evenings after 8pm" in meta["memory"]

    def test_no_contact_no_memory_field(self, db, monkeypatch):
        captured = []
        _fake_livekit(monkeypatch, captured)
        baileys_calls._dispatch_agent("60888777666", "Push Name")
        meta = json.loads(captured[0].metadata)
        assert meta.get("contact_name") == "Push Name"
        assert "memory" not in meta


def _voice_prompts():
    agent_dir = str(Path(__file__).resolve().parent.parent / "voice_agent")
    if agent_dir not in sys.path:
        sys.path.insert(0, agent_dir)
    import prompts  # plain script module — only imports datetime
    return prompts


VOICE_CONTACT = {
    "company_name": "Northwind Trading", "contact_name": "Aminah",
    "language": "ms", "record_type": "order", "record_id": "ORD-77",
    "record_title": "Weekly grocery box", "record_status": "packed",
    "record_amount": "MYR1,200", "record_due_date": "2026-07-20",
}


class TestVoicePromptMemory:
    def test_memory_string_appended_to_instructions(self):
        prompts = _voice_prompts()
        text = prompts.agent_instructions(
            {**VOICE_CONTACT, "memory": "Prefers evenings; missed one delivery."})
        assert "CONTACT MEMORY" in text
        assert "Prefers evenings; missed one delivery." in text

    def test_record_block_rendered(self):
        prompts = _voice_prompts()
        text = prompts.agent_instructions(dict(VOICE_CONTACT))
        assert "ORD-77" in text and "packed" in text
        assert "What this call is about — a order" in text

    def test_no_memory_no_block(self):
        prompts = _voice_prompts()
        assert "CONTACT MEMORY" not in prompts.agent_instructions(dict(VOICE_CONTACT))
