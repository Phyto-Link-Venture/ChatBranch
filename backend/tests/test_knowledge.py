"""Phase 8 — Knowledge Base + Skills (`docs/KNOWLEDGE-BASE-PLAN.md`).

Covers the three things the spec says matter most:

1. **The AgentSkill write-ownership fix** — `PUT /api/flows/library` no longer
   upserts OR prunes `crm.AgentSkill`; `GET/PUT /api/aicore/skills` is the sole
   writer. The regression this prevents (a stale flow-builder tab silently
   deleting skills an operator just typed into Settings) has its own test.
2. **Retrieval is keyword + recency + priority, not embeddings** — tier order,
   keyword scoring, date-window expiry, channel + brand scoping.
3. **Bounded and exception-safe** — the hard character budget, per-item
   labelled truncation, and the rule that a failure degrades to "answer
   without knowledge" instead of breaking a send.

Plus the API surface (auth, admin+ mutations, validation, preview, audit) and
the two injection seams: the chat system prompt and the voice dispatch
metadata (both WhatsApp transports + SIP), and the Django-free
voice_agent/prompts.py rendering.
"""
import json
import sys
import types
from datetime import timedelta
from pathlib import Path

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from aicore import assistant, knowledge
from crm.models import (
    AgentSkill,
    AuditLog,
    Contact,
    KnowledgeItem,
    Record,
    Setting,
    Workflow,
)
from ingest.models import Brand, Tenant

TOKEN_SUPER = "kb-super-token"
TOKEN_ADMIN = "kb-admin-token"
TOKEN_AGENT = "kb-agent-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_SUPER}"}
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}
WA_ID = "60111222444"


@pytest.fixture
def superadmin(db):
    u = User.objects.create_user(username="kb_super", password="x")
    from crm.models import Profile
    Profile.objects.create(user=u, role="superadmin", token=TOKEN_SUPER)
    return u


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="kb_admin", password="x")
    from crm.models import Profile
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="kb_agent", password="x")
    from crm.models import Profile
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


@pytest.fixture
def contact(db):
    c = Contact.objects.create(name="Aminah", phone=f"+{WA_ID}", language="en")
    Record.objects.create(contact=c, type="order", external_id="ORD-91",
                          title="Gift set", status="packed")
    return c


def _item(**kw):
    kw.setdefault("title", "Item")
    kw.setdefault("content", "Body text.")
    return KnowledgeItem.objects.create(**kw)


# ---- retrieval: tiering -------------------------------------------------------------

@pytest.mark.django_db
class TestTiering:
    def test_notices_come_before_events_before_facts(self):
        _item(kind="fact", title="Opening hours", content="9am to 6pm daily.")
        _item(kind="event", title="Raya promo", content="15% off until 15 Aug.")
        _item(kind="notice", title="Flood delay", content="Deliveries delayed 2 days.")
        block = knowledge.build_knowledge_context()
        assert block.index("Flood delay") < block.index("Raya promo")
        assert block.index("Raya promo") < block.index("Opening hours")
        assert "[NOTICE]" in block and "[NOW ON]" in block and "[FACT]" in block

    def test_faq_and_policy_need_a_keyword_hit(self):
        _item(kind="faq", title="Refunds", question="How do I get a refund?",
              content="Refunds take 5 working days.")
        _item(kind="policy", title="Warranty", content="One year warranty.")
        assert knowledge.build_knowledge_context(text="what are your hours") == ""
        hit = knowledge.build_knowledge_context(text="I want a refund please")
        assert "Refunds take 5 working days." in hit
        assert "One year warranty." not in hit   # no keyword overlap, no padding

    def test_faq_renders_the_question_form(self):
        _item(kind="faq", title="Refunds", question="How do I get a refund?",
              content="Five working days.")
        block = knowledge.build_knowledge_context(text="refund")
        assert "How do I get a refund?" in block

    def test_priority_orders_within_a_tier(self):
        _item(kind="fact", title="Low fact", content="x", priority=0)
        _item(kind="fact", title="High fact", content="y", priority=10)
        block = knowledge.build_knowledge_context()
        assert block.index("High fact") < block.index("Low fact")

    def test_matched_tier_is_capped(self):
        for i in range(10):
            _item(kind="policy", title=f"Delivery policy {i}",
                  content="delivery delivery delivery")
        block = knowledge.build_knowledge_context(text="delivery")
        assert block.count("Delivery policy") == knowledge.MATCH_LIMIT

    def test_higher_score_wins_the_match_slots(self):
        _item(kind="faq", title="Shipping cost", question="How much is shipping?",
              content="Shipping is free above RM100.", tags=["shipping"])
        _item(kind="policy", title="Returns", content="mentions shipping once")
        block = knowledge.build_knowledge_context(text="how much is shipping")
        assert block.index("How much is shipping?") < block.index("Returns")

    def test_empty_knowledge_base_returns_empty_string(self):
        assert knowledge.build_knowledge_context() == ""
        assert knowledge.prompt_block() == ""

    def test_stopwords_do_not_match(self):
        _item(kind="policy", title="Returns policy", content="Return within 14 days.")
        # "what can you do for me" is all stopwords/short tokens
        assert knowledge.build_knowledge_context(text="what can you do for me") == ""


# ---- retrieval: filters -------------------------------------------------------------

@pytest.mark.django_db
class TestFilters:
    def test_inactive_items_are_excluded(self):
        _item(kind="fact", title="Hidden", content="x", active=False)
        assert knowledge.build_knowledge_context() == ""

    def test_future_item_is_not_live_yet(self):
        _item(kind="notice", title="Next week outage", content="x",
              starts_at=timezone.now() + timedelta(days=3))
        assert knowledge.build_knowledge_context() == ""

    def test_expired_item_self_expires(self):
        _item(kind="notice", title="Old outage", content="x",
              starts_at=timezone.now() - timedelta(days=5),
              ends_at=timezone.now() - timedelta(hours=1))
        assert knowledge.build_knowledge_context() == ""

    def test_item_inside_its_window_is_live(self):
        _item(kind="notice", title="Warehouse flooded", content="Delays of 2 days.",
              starts_at=timezone.now() - timedelta(hours=1),
              ends_at=timezone.now() + timedelta(days=2))
        assert "Warehouse flooded" in knowledge.build_knowledge_context()

    def test_channel_scope(self):
        _item(kind="fact", title="WhatsApp only", content="x", channels=["whatsapp"])
        _item(kind="fact", title="Everywhere", content="y")
        wa = knowledge.build_knowledge_context(channel="whatsapp")
        email = knowledge.build_knowledge_context(channel="email")
        both = knowledge.build_knowledge_context()
        assert "WhatsApp only" in wa and "Everywhere" in wa
        assert "WhatsApp only" not in email and "Everywhere" in email
        assert "WhatsApp only" in both  # no channel asked = no channel filter

    def test_brand_scope(self, db):
        tenant = Tenant.objects.create(name="T", slug="t")
        brand_a = Brand.objects.create(tenant=tenant, name="A", slug="a")
        brand_b = Brand.objects.create(tenant=tenant, name="B", slug="b")
        _item(kind="fact", title="Shared", content="x")
        _item(kind="fact", title="Brand A only", content="y", brand=brand_a)
        _item(kind="fact", title="Brand B only", content="z", brand=brand_b)
        contact = Contact.objects.create(name="C", phone="+60100000009", tenant=tenant)
        from crm.models import ContactBrand
        ContactBrand.objects.create(contact=contact, brand=brand_a)
        block = knowledge.build_knowledge_context(contact)
        assert "Shared" in block and "Brand A only" in block
        assert "Brand B only" not in block
        # no contact at all -> only brand-agnostic rows, never another brand's
        anon = knowledge.build_knowledge_context()
        assert "Shared" in anon and "Brand A only" not in anon


# ---- bounded + exception-safe --------------------------------------------------------

@pytest.mark.django_db
class TestBounds:
    def test_block_never_exceeds_the_budget(self):
        for i in range(40):
            _item(kind="fact", title=f"Fact {i}", content="x" * 300)
        block = knowledge.build_knowledge_context()
        assert 0 < len(block) <= knowledge.BUDGET

    def test_custom_budget_is_honoured(self):
        for i in range(10):
            _item(kind="fact", title=f"Fact {i}", content="y" * 200)
        assert len(knowledge.build_knowledge_context(budget=300)) <= 300

    def test_long_item_is_truncated_with_a_label(self):
        _item(kind="notice", title="Long one", content="z" * 2000)
        block = knowledge.build_knowledge_context()
        assert block.endswith("…[truncated]")
        assert len(block.splitlines()[0]) <= knowledge.ITEM_MAX

    def test_urgent_tiers_win_the_budget(self):
        _item(kind="notice", title="URGENT NOTICE", content="short")
        for i in range(30):
            _item(kind="fact", title=f"Filler {i}", content="w" * 300)
        block = knowledge.build_knowledge_context()
        assert "URGENT NOTICE" in block   # tier 1 was assembled first

    def test_retrieval_failure_degrades_to_empty(self, monkeypatch):
        def boom(*a, **kw):
            raise RuntimeError("db on fire")

        monkeypatch.setattr(knowledge, "live_items", boom)
        assert knowledge.build_knowledge_context() == ""   # no exception escapes
        assert knowledge.prompt_block() == ""

    def test_prompt_block_carries_the_heading(self):
        _item(kind="fact", title="Hours", content="9-6")
        block = knowledge.prompt_block()
        assert block.startswith(knowledge.HEADING)
        assert "Hours" in block


# ---- API ------------------------------------------------------------------------------

@pytest.mark.django_db
class TestKnowledgeApi:
    def test_requires_auth(self, client, admin):
        assert client.get("/api/aicore/knowledge").status_code == 401
        assert client.post("/api/aicore/knowledge/preview", data="{}",
                           content_type="application/json").status_code == 401

    def test_agent_can_read_but_not_write(self, client, agent):
        assert client.get("/api/aicore/knowledge", **AGENT).status_code == 200
        r = client.post("/api/aicore/knowledge",
                        data=json.dumps({"kind": "fact", "title": "T", "content": "C"}),
                        content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_create_update_delete_round_trip(self, client, admin):
        r = client.post("/api/aicore/knowledge", data=json.dumps({
            "kind": "notice", "title": "Warehouse flooded",
            "content": "Deliveries delayed by 2 days.",
            "tags": ["delivery", "delay"], "channels": ["whatsapp"], "priority": 5,
        }), content_type="application/json", **ADMIN)
        assert r.status_code == 200, r.content
        body = r.json()
        assert body["kind"] == "notice" and body["live"] is True
        assert body["tags"] == ["delivery", "delay"]
        assert body["created_by"] == "kb_admin"
        assert AuditLog.objects.filter(action="knowledge.update").count() == 1

        pk = body["id"]
        r = client.patch(f"/api/aicore/knowledge/{pk}",
                         data=json.dumps({"content": "Back to normal.", "priority": 1}),
                         content_type="application/json", **ADMIN)
        assert r.status_code == 200
        assert r.json()["content"] == "Back to normal."
        assert r.json()["tags"] == ["delivery", "delay"]  # untouched by the patch

        assert client.delete(f"/api/aicore/knowledge/{pk}", **ADMIN).status_code == 200
        assert KnowledgeItem.objects.count() == 0

    def test_validation(self, client, admin):
        def post(body):
            return client.post("/api/aicore/knowledge", data=json.dumps(body),
                               content_type="application/json", **ADMIN)

        assert post({"title": "T", "content": "C", "kind": "gossip"}).status_code == 400
        assert post({"title": "", "content": "C"}).status_code == 400
        assert post({"title": "T", "content": ""}).status_code == 400
        assert post({"title": "T", "content": "C", "tags": "vip"}).status_code == 400
        assert post({"title": "T", "content": "C",
                     "starts_at": "not-a-date"}).status_code == 400
        bad_window = post({"title": "T", "content": "C",
                           "starts_at": "2026-08-10T00:00:00Z",
                           "ends_at": "2026-08-01T00:00:00Z"})
        assert bad_window.status_code == 400
        assert "after" in bad_window.json()["detail"]

    def test_list_filters_and_counts(self, client, admin):
        _item(kind="fact", title="Hours", content="9-6")
        _item(kind="faq", title="Refunds", content="5 days", active=False)
        r = client.get("/api/aicore/knowledge?kind=fact", **ADMIN).json()
        assert [i["title"] for i in r["items"]] == ["Hours"]
        assert r["counts"]["fact"] == 1 and r["counts"]["faq"] == 1
        assert r["budget"] == knowledge.BUDGET
        inactive = client.get("/api/aicore/knowledge?active=false", **ADMIN).json()
        assert [i["title"] for i in inactive["items"]] == ["Refunds"]
        search = client.get("/api/aicore/knowledge?q=refund", **ADMIN).json()
        assert [i["title"] for i in search["items"]] == ["Refunds"]

    def test_expired_items_stay_listed_but_flagged(self, client, admin):
        _item(kind="notice", title="Old", content="x",
              ends_at=timezone.now() - timedelta(days=1))
        item = client.get("/api/aicore/knowledge", **ADMIN).json()["items"][0]
        assert item["expired"] is True and item["live"] is False

    def test_preview_returns_the_exact_block(self, client, admin, contact):
        _item(kind="fact", title="Opening hours", content="9am to 6pm.")
        _item(kind="faq", title="Refunds", question="How do I get a refund?",
              content="Five working days.")
        r = client.post("/api/aicore/knowledge/preview", data=json.dumps(
            {"contact_id": contact.pk, "channel": "whatsapp",
             "text": "hello, how about a refund"}),
            content_type="application/json", **ADMIN)
        assert r.status_code == 200
        body = r.json()
        assert "Opening hours" in body["block"] and "How do I get a refund?" in body["block"]
        assert body["chars"] == len(body["block"])
        assert body["budget"] == knowledge.BUDGET
        assert body["items"] == 2
        assert body["contact"]["name"] == "Aminah"
        assert body["full_prompt_block"].startswith(knowledge.HEADING)

    def test_preview_needs_no_write_role(self, client, agent):
        _item(kind="fact", title="Hours", content="9-6")
        r = client.post("/api/aicore/knowledge/preview", data=json.dumps({}),
                        content_type="application/json", **AGENT)
        assert r.status_code == 200 and "Hours" in r.json()["block"]

    def test_missing_item_404(self, client, admin):
        assert client.get("/api/aicore/knowledge/9999", **ADMIN).status_code == 404

    def test_method_not_allowed(self, client, admin):
        assert client.put("/api/aicore/knowledge", data="{}",
                          content_type="application/json", **ADMIN).status_code == 405


# ---- skills: the write-ownership fix ---------------------------------------------------

def _skill(skill_id="as-1", name="Greeting", **kw):
    return AgentSkill.objects.create(skill_id=skill_id, name=name, **kw)


@pytest.mark.django_db
class TestSkillOwnership:
    def test_flow_library_put_no_longer_prunes_skills(self, client, admin):
        """The hazard KNOWLEDGE-BASE-PLAN §3 exists to close: a STALE
        flow-builder tab syncing its old copy must not delete a skill an
        operator just wrote in Settings."""
        _skill("as-typed-in-settings", "Bulk pricing",
               instructions="Collect quantity and company name.")
        r = client.put("/api/flows/library",
                       data=json.dumps({"workflows": [], "agentSkills": []}),
                       content_type="application/json", **ADMIN)
        assert r.status_code == 200
        assert AgentSkill.objects.filter(skill_id="as-typed-in-settings").exists()

    def test_flow_library_put_does_not_create_skills_either(self, client, admin):
        client.put("/api/flows/library", data=json.dumps({
            "workflows": [],
            "agentSkills": [{"id": "as-from-builder", "name": "Nope"}],
        }), content_type="application/json", **ADMIN)
        assert AgentSkill.objects.count() == 0

    def test_flow_library_put_still_owns_workflows(self, client, admin):
        Workflow.objects.create(flow_id="wf-old", name="Old")
        client.put("/api/flows/library", data=json.dumps({
            "workflows": [{"id": "wf-new", "name": "New", "nodes": [], "edges": []}],
            "agentSkills": [],
        }), content_type="application/json", **ADMIN)
        assert list(Workflow.objects.values_list("flow_id", flat=True)) == ["wf-new"]

    def test_flow_library_get_still_exposes_skills_read_only(self, client, admin):
        _skill("as-1", "Greeting", use_when="at the start", instructions="Say hi.")
        body = client.get("/api/flows/library", **ADMIN).json()
        assert body["agentSkills"] == [
            {"id": "as-1", "name": "Greeting", "useWhen": "at the start",
             "instructions": "Say hi."}]


@pytest.mark.django_db
class TestSkillsApi:
    def test_requires_auth(self, client, admin):
        assert client.get("/api/aicore/skills").status_code == 401

    def test_agent_can_read_not_write(self, client, agent):
        assert client.get("/api/aicore/skills", **AGENT).status_code == 200
        r = client.put("/api/aicore/skills", data=json.dumps({"skills": []}),
                       content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_put_is_the_sole_writer(self, client, admin):
        _skill("as-old", "Old skill")
        r = client.put("/api/aicore/skills", data=json.dumps({"skills": [
            {"id": "as-a", "name": "Bulk pricing", "useWhen": "asked about bulk",
             "instructions": "Collect quantity, then hand to sales."},
            {"id": "as-b", "name": "Closing"},
        ]}), content_type="application/json", **ADMIN)
        assert r.status_code == 200
        skills = r.json()["skills"]
        assert [s["id"] for s in skills] == ["as-a", "as-b"]
        assert skills[0]["useWhen"] == "asked about bulk"
        assert not AgentSkill.objects.filter(skill_id="as-old").exists()
        assert AuditLog.objects.filter(action="aicore.skills.update").exists()

    def test_put_rejects_a_bad_body(self, client, admin):
        r = client.put("/api/aicore/skills", data=json.dumps({"skills": "nope"}),
                       content_type="application/json", **ADMIN)
        assert r.status_code == 400

    def test_put_skips_id_less_entries(self, client, admin):
        client.put("/api/aicore/skills", data=json.dumps({"skills": [
            {"name": "no id"}, {"id": "as-ok", "name": "fine"}, "garbage",
        ]}), content_type="application/json", **ADMIN)
        assert list(AgentSkill.objects.values_list("skill_id", flat=True)) == ["as-ok"]


@pytest.mark.django_db
class TestSkillsBlock:
    def test_block_is_a_skills_section(self):
        _skill("as-1", "Bulk pricing", use_when="asked about bulk",
               instructions="Collect quantity and company name.")
        block = knowledge.skills_block()
        assert block.startswith("SKILLS")
        assert "Bulk pricing" in block
        assert "[when: asked about bulk]" in block
        assert "Collect quantity and company name." in block

    def test_no_skills_no_block(self):
        assert knowledge.skills_block() == ""


# ---- injection seam 1: the chat system prompt -------------------------------------------

class _FakeOpenAI:
    captured: list = []
    reply = "ok"

    def __init__(self, base_url=None, api_key=None):
        class _Completions:
            def create(self, **kwargs):
                _FakeOpenAI.captured.append(kwargs)
                msg = types.SimpleNamespace(content=_FakeOpenAI.reply)
                return types.SimpleNamespace(choices=[types.SimpleNamespace(message=msg)])

        self.chat = types.SimpleNamespace(completions=_Completions())


@pytest.mark.django_db
class TestChatInjection:
    @pytest.fixture
    def fake_llm(self, monkeypatch):
        import openai
        _FakeOpenAI.captured = []
        _FakeOpenAI.reply = "ok"
        monkeypatch.setattr(openai, "OpenAI", _FakeOpenAI)
        monkeypatch.setattr(assistant, "_provider",
                            lambda: ("http://llm", "key-1", "model-x"))
        return _FakeOpenAI.captured

    def _reply(self, text="hi"):
        return assistant.generate_reply(
            [{"role": "user", "content": text}],
            contact_name="Aminah", phone=f"+{WA_ID}", channel="whatsapp")

    def _systems(self, captured):
        return [m["content"] for m in captured[0]["messages"] if m["role"] == "system"]

    def test_knowledge_block_reaches_the_prompt(self, contact, fake_llm):
        _item(kind="fact", title="Opening hours", content="9am to 6pm daily.")
        self._reply()
        joined = "\n".join(self._systems(fake_llm))
        assert knowledge.HEADING in joined
        assert "9am to 6pm daily." in joined

    def test_knowledge_matches_the_latest_user_message(self, contact, fake_llm):
        _item(kind="faq", title="Refunds", question="How do I get a refund?",
              content="Five working days.")
        self._reply("can I get a refund")
        joined = "\n".join(self._systems(fake_llm))
        assert "Five working days." in joined

    def test_no_knowledge_no_block(self, contact, fake_llm):
        self._reply()
        assert all(knowledge.HEADING not in s for s in self._systems(fake_llm))

    def test_knowledge_reaches_unknown_contacts_too(self, db, fake_llm):
        """A policy is not personal — an unlinked chat still gets the block."""
        _item(kind="policy", title="Returns", content="14 days to return.")
        assistant.generate_reply([{"role": "user", "content": "return policy?"}],
                                 contact_name="Stranger", phone="+60109990000",
                                 channel="whatsapp")
        joined = "\n".join(self._systems(fake_llm))
        assert "14 days to return." in joined

    def test_channel_scoped_item_respects_the_channel(self, contact, fake_llm):
        _item(kind="fact", title="Telegram only", content="secret", channels=["telegram"])
        self._reply()
        assert all("Telegram only" not in s for s in self._systems(fake_llm))

    def test_skills_section_is_present(self, contact, fake_llm):
        _skill("as-1", "Bulk pricing", use_when="asked about bulk",
               instructions="Collect quantity.")
        self._reply()
        joined = "\n".join(self._systems(fake_llm))
        assert "SKILLS" in joined and "Bulk pricing" in joined

    def test_knowledge_failure_does_not_break_the_reply(self, contact, fake_llm,
                                                        monkeypatch):
        _item(kind="fact", title="Hours", content="9-6")
        monkeypatch.setattr(knowledge, "live_items",
                            lambda *a, **kw: (_ for _ in ()).throw(RuntimeError("boom")))
        assert self._reply() == "ok"   # the send still happens


# ---- injection seam 2: the voice dispatch metadata ---------------------------------------

@pytest.mark.django_db
class TestVoiceMetadata:
    def test_whatsapp_call_metadata_carries_knowledge(self, contact):
        from whatsapp import store as wa_store

        _item(kind="notice", title="Warehouse flooded", content="Delays of 2 days.")
        meta = wa_store.call_metadata(contact, channel="whatsapp")
        assert "Warehouse flooded" in meta["knowledge"]

    def test_no_knowledge_no_key(self, contact):
        from whatsapp import store as wa_store

        assert "knowledge" not in wa_store.call_metadata(contact, channel="whatsapp")

    def test_sip_dial_meta_carries_knowledge(self, contact):
        from calls import contacts as calls_contacts

        _item(kind="fact", title="Support line", content="Call 1-300-88.")
        meta = calls_contacts.dial_meta({"id": contact.pk, "name": contact.name,
                                         "language": "en"})
        assert "Call 1-300-88." in meta["knowledge"]

    def test_dispatch_metadata_end_to_end(self, contact, monkeypatch):
        from whatsapp import baileys_calls

        _item(kind="event", title="Raya promo", content="15% off until 15 Aug.")
        captured = []

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

        baileys_calls._dispatch_agent(WA_ID, None)
        meta = json.loads(captured[0].metadata)
        assert "Raya promo" in meta["knowledge"]


def _voice_prompts():
    agent_dir = str(Path(__file__).resolve().parent.parent / "voice_agent")
    if agent_dir not in sys.path:
        sys.path.insert(0, agent_dir)
    import prompts  # plain script module — the agent process is Django-free
    return prompts


VOICE_CONTACT = {
    "company_name": "Northwind Trading", "contact_name": "Aminah",
    "language": "en", "record_type": "order", "record_id": "ORD-91",
}


class TestVoicePromptRendering:
    def test_knowledge_block_rendered(self):
        prompts = _voice_prompts()
        text = prompts.agent_instructions(
            {**VOICE_CONTACT, "knowledge": "- [NOTICE] Flood: delays of 2 days."})
        assert "COMPANY KNOWLEDGE" in text
        assert "Flood: delays of 2 days." in text

    def test_no_knowledge_no_block(self):
        prompts = _voice_prompts()
        assert "COMPANY KNOWLEDGE" not in prompts.agent_instructions(dict(VOICE_CONTACT))

    def test_memory_and_knowledge_coexist(self):
        prompts = _voice_prompts()
        text = prompts.agent_instructions({**VOICE_CONTACT,
                                           "memory": "Prefers evenings.",
                                           "knowledge": "- [FACT] Open 9-6."})
        assert "CONTACT MEMORY" in text and "COMPANY KNOWLEDGE" in text
        assert text.index("CONTACT MEMORY") < text.index("COMPANY KNOWLEDGE")


# ---- persona still works alongside ---------------------------------------------------

@pytest.mark.django_db
class TestPersonaUnaffected:
    def test_persona_endpoint_intact(self, client, superadmin):
        r = client.put("/api/aicore/persona",
                       data=json.dumps({"persona": "Be brief."}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200 and r.json()["is_default"] is False
        assert Setting.objects.filter(key="aicore_persona").exists()
