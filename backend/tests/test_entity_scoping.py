"""Entity scoping — global default, entity override, entity-owned records.

The operator's rule, which this file exists to pin down:

    an entity-specific binding wins; the global one is the fallback.

Covers the three verbs in crm/scoping.py (`setting_value`, `scope_inherited`,
`scope_owned`), the thread-attribution chain that decides which outlet a
conversation belongs to, and the endpoints an operator actually notices when
switching between two branches of the same business: booking hours, knowledge,
channel health and analytics.

The running example throughout is the real one: Musician Studio Rimbayu and
Musician Studio Bandar Botanic under one organization.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm import scoping
from crm.models import (
    AgentSkill,
    CannedResponse,
    ChannelAccount,
    Contact,
    ContactEntity,
    KnowledgeItem,
    Message,
    Profile,
    Setting,
    Thread,
)
from ingest.models import Entity, Tenant

TOKEN_SUPER = "ent-super-token"
TOKEN_AGENT = "ent-agent-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_SUPER}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def org(db):
    """One tenant, two outlets — the shape every test here reasons about."""
    tenant = Tenant.objects.create(name="Musician Studio", slug="musician-studio")
    rimbayu = Entity.objects.create(tenant=tenant, name="Rimbayu", slug="rimbayu")
    botanic = Entity.objects.create(tenant=tenant, name="Bandar Botanic",
                                    slug="bandar-botanic")
    return tenant, rimbayu, botanic


@pytest.fixture
def superadmin(org):
    u = User.objects.create_user(username="ent_super", password="x")
    Profile.objects.create(user=u, role="superadmin", token=TOKEN_SUPER,
                           tenant=org[0])
    return u


@pytest.fixture
def rimbayu_agent(org):
    """An agent assigned to Rimbayu only — used to prove RBAC still bounds the
    filter, so a hand-crafted ?entity= cannot widen anyone's view."""
    tenant, rimbayu, _ = org
    u = User.objects.create_user(username="ent_agent", password="x")
    p = Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT, tenant=tenant)
    p.entities.add(rimbayu)
    return u


# ---- settings: entity override wins, global is the fallback -------------------

@pytest.mark.django_db
class TestSettingResolution:
    def test_entity_override_wins_and_global_is_the_fallback(self, org):
        _, rimbayu, botanic = org
        scoping.set_setting("booking_hours", '{"mon": [9, 18]}')          # org
        scoping.set_setting("booking_hours", '{"mon": [11, 22]}', rimbayu)  # outlet

        assert scoping.setting_value("booking_hours") == '{"mon": [9, 18]}'
        assert scoping.setting_value("booking_hours", rimbayu) == '{"mon": [11, 22]}'
        # Bandar Botanic never overrode it — it inherits the organization value.
        assert scoping.setting_value("booking_hours", botanic) == '{"mon": [9, 18]}'

    def test_clearing_an_override_reverts_to_inherited(self, org):
        _, rimbayu, _ = org
        scoping.set_setting("booking_hours", '{"mon": [9, 18]}')
        scoping.set_setting("booking_hours", '{"mon": [11, 22]}', rimbayu)
        assert scoping.setting_scope("booking_hours", rimbayu) == "entity"

        assert scoping.clear_setting("booking_hours", rimbayu) is True
        assert scoping.setting_value("booking_hours", rimbayu) == '{"mon": [9, 18]}'
        assert scoping.setting_scope("booking_hours", rimbayu) == "global"

    def test_blank_override_falls_through_rather_than_blanking(self, org):
        """A blank row means "no opinion", exactly as a blank global row means
        "fall back to .env" — it must not shadow the organization value."""
        _, rimbayu, _ = org
        scoping.set_setting("quiet_start", "22:00")
        scoping.set_setting("quiet_start", "", rimbayu)
        assert scoping.setting_value("quiet_start", rimbayu) == "22:00"

    def test_organization_wide_keys_refuse_a_per_entity_override(self, org):
        """Credentials are deliberately NOT overridable — forking an API key
        per outlet is how one branch silently stops sending."""
        _, rimbayu, _ = org
        assert scoping.is_entity_scoped("openai_api_key") is False
        with pytest.raises(ValueError):
            scoping.set_setting("openai_api_key", "sk-nope", rimbayu)

    def test_unknown_slug_resolves_to_global_not_another_outlet(self, org):
        scoping.set_setting("booking_hours", '{"mon": [9, 18]}')
        assert scoping.setting_value("booking_hours", "no-such-outlet") \
            == '{"mon": [9, 18]}'

    def test_global_row_stays_unique_per_key(self, org):
        """The partial unique constraint — SQL treats NULLs as distinct, so
        without it two global rows for one key would be allowed."""
        from django.db import IntegrityError

        Setting.objects.create(key="quiet_start", value="22:00")
        with pytest.raises(IntegrityError):
            Setting.objects.create(key="quiet_start", value="23:00")

    def test_bulk_read_matches_single_read(self, org):
        _, rimbayu, _ = org
        scoping.set_setting("quiet_start", "22:00")
        scoping.set_setting("quiet_end", "08:00")
        scoping.set_setting("quiet_start", "23:00", rimbayu)
        assert scoping.setting_values(["quiet_start", "quiet_end"], rimbayu) == {
            "quiet_start": "23:00", "quiet_end": "08:00"}


# ---- inherited libraries vs entity-owned records ------------------------------

@pytest.mark.django_db
class TestQuerysetScoping:
    def test_inherited_returns_global_plus_own_but_not_a_sibling(self, org):
        _, rimbayu, botanic = org
        CannedResponse.objects.create(short_code="hello", content="Hi!")           # global
        CannedResponse.objects.create(short_code="rim", content="Rimbayu",
                                      entity=rimbayu)
        CannedResponse.objects.create(short_code="bot", content="Botanic",
                                      entity=botanic)
        codes = set(scoping.scope_inherited(CannedResponse.objects.all(), rimbayu)
                    .values_list("short_code", flat=True))
        assert codes == {"hello", "rim"}          # sibling's row must not leak

    def test_owned_returns_only_that_entity(self, org):
        _, rimbayu, botanic = org
        Thread.objects.create(channel="whatsapp", key="1", entity=rimbayu)
        Thread.objects.create(channel="whatsapp", key="2", entity=botanic)
        Thread.objects.create(channel="whatsapp", key="3")   # unassigned
        assert set(scoping.scope_owned(Thread.objects.all(), rimbayu)
                   .values_list("key", flat=True)) == {"1"}
        assert set(scoping.scope_owned(Thread.objects.all(), rimbayu,
                                       include_unassigned=True)
                   .values_list("key", flat=True)) == {"1", "3"}

    def test_none_entity_means_all_entities_and_does_not_narrow(self, org):
        _, rimbayu, botanic = org
        Thread.objects.create(channel="whatsapp", key="1", entity=rimbayu)
        Thread.objects.create(channel="whatsapp", key="2", entity=botanic)
        assert scoping.scope_owned(Thread.objects.all(), None).count() == 2

    def test_override_by_shadows_the_global_row_of_the_same_key(self, org):
        _, rimbayu, _ = org
        AgentSkill.objects.create(skill_id="as-greet", name="Greeting (org)",
                                  position=0)
        AgentSkill.objects.create(skill_id="as-greet", name="Greeting (Rimbayu)",
                                  entity=rimbayu, position=0)
        AgentSkill.objects.create(skill_id="as-close", name="Closing", position=1)
        rows = scoping.override_by(
            scoping.scope_inherited(AgentSkill.objects.all(), rimbayu),
            rimbayu, "skill_id")
        names = [r.name for r in rows]
        assert names == ["Greeting (Rimbayu)", "Closing"]   # one row per skill_id


# ---- thread attribution -------------------------------------------------------

@pytest.mark.django_db
class TestThreadAttribution:
    def test_receiving_account_decides(self, org):
        _, rimbayu, botanic = org
        acct = ChannelAccount.objects.create(channel="whatsapp", name="Rimbayu WA",
                                             entity=rimbayu)
        contact = Contact.objects.create(name="Aminah", phone="+60111222333")
        ContactEntity.objects.create(contact=contact, entity=botanic)
        # The number that took the message wins over the contact's own link.
        assert scoping.thread_entity(account=acct, contact=contact) == rimbayu

    def test_falls_back_to_the_contact_on_a_shared_number(self, org):
        _, rimbayu, _ = org
        shared = ChannelAccount.objects.create(channel="whatsapp", name="Company WA")
        contact = Contact.objects.create(name="Aminah", phone="+60111222333")
        ContactEntity.objects.create(contact=contact, entity=rimbayu)
        assert scoping.thread_entity(account=shared, contact=contact) == rimbayu

    def test_multi_entity_contact_on_a_shared_number_stays_unassigned(self, org):
        """No basis to choose — filing it under a guessed branch is worse than
        leaving it visibly unassigned."""
        _, rimbayu, botanic = org
        shared = ChannelAccount.objects.create(channel="whatsapp", name="Company WA")
        contact = Contact.objects.create(name="Aminah", phone="+60111222333")
        ContactEntity.objects.create(contact=contact, entity=rimbayu)
        ContactEntity.objects.create(contact=contact, entity=botanic)
        assert scoping.thread_entity(account=shared, contact=contact) is None

    def test_unknown_contact_on_a_shared_number_is_unassigned(self, org):
        shared = ChannelAccount.objects.create(channel="whatsapp", name="Company WA")
        assert scoping.thread_entity(account=shared, contact=None) is None

    def test_new_whatsapp_thread_is_attributed_to_the_receiving_outlet(self, org):
        from whatsapp import store as wa_store

        _, rimbayu, _ = org
        acct = ChannelAccount.objects.create(channel="whatsapp", name="Rimbayu WA",
                                             external_id="pnid-1", entity=rimbayu)
        wa_store.get_or_create_thread("60111222333", name="Aminah",
                                      account_id=str(acct.pk))
        thread = Thread.objects.get(channel="whatsapp", key="60111222333")
        assert thread.entity == rimbayu

    def test_rebinding_a_number_moves_its_existing_chats(self, org):
        """Binding a number after the fact must re-file its history, or the
        inbox looks like it lost conversations."""
        from whatsapp import store as wa_store, wa_config

        _, rimbayu, _ = org
        acct = ChannelAccount.objects.create(channel="whatsapp", name="Shared WA",
                                             external_id="pnid-2")
        wa_store.get_or_create_thread("60999888777", account_id=str(acct.pk))
        assert Thread.objects.get(key="60999888777").entity_id is None

        wa_config.update_account(str(acct.pk), {"entity": "rimbayu"})
        assert Thread.objects.get(key="60999888777").entity == rimbayu


# ---- channel accounts + integrations: binding with global fallback ------------

@pytest.mark.django_db
class TestAccountBinding:
    def test_outlet_with_its_own_number_does_not_fall_back_to_the_shared_one(self, org):
        _, rimbayu, _ = org
        ChannelAccount.objects.create(channel="whatsapp", name="Company WA")
        own = ChannelAccount.objects.create(channel="whatsapp", name="Rimbayu WA",
                                            entity=rimbayu)
        picked = scoping.accounts_for_entity(ChannelAccount.objects.all(), rimbayu)
        assert list(picked) == [own]

    def test_outlet_without_its_own_number_uses_the_shared_pool(self, org):
        _, _, botanic = org
        shared = ChannelAccount.objects.create(channel="whatsapp", name="Company WA")
        ChannelAccount.objects.create(channel="whatsapp", name="Rimbayu WA",
                                      entity=Entity.objects.get(slug="rimbayu"))
        picked = scoping.accounts_for_entity(ChannelAccount.objects.all(), botanic)
        assert list(picked) == [shared]


# ---- booking hours: the per-outlet case that started this ---------------------

@pytest.mark.django_db
class TestBookingHoursApi:
    URL = "/api/aicore/booking-hours"

    def test_outlet_hours_override_the_organization_default(self, client, superadmin, org):
        _, rimbayu, botanic = org
        client.put(self.URL, data=json.dumps({"hours": {"mon": [9, 18]}}),
                   content_type="application/json", **SUPER)
        client.put(f"{self.URL}?entity=rimbayu",
                   data=json.dumps({"hours": {"mon": [11, 22]}}),
                   content_type="application/json", **SUPER)

        org_hours = client.get(self.URL, **SUPER).json()
        rim = client.get(f"{self.URL}?entity=rimbayu", **SUPER).json()
        bot = client.get(f"{self.URL}?entity=bandar-botanic", **SUPER).json()

        assert org_hours["hours"]["mon"] == [9, 18]
        assert rim["hours"]["mon"] == [11, 22]
        assert rim["scope"] == "entity"
        # The inherited value travels with the payload so the UI can show what
        # removing the override would restore.
        assert rim["global_hours"]["mon"] == [9, 18]
        assert bot["hours"]["mon"] == [9, 18]
        assert bot["scope"] == "global"

    def test_delete_removes_only_the_override(self, client, superadmin, org):
        client.put(self.URL, data=json.dumps({"hours": {"mon": [9, 18]}}),
                   content_type="application/json", **SUPER)
        client.put(f"{self.URL}?entity=rimbayu",
                   data=json.dumps({"hours": {"mon": [11, 22]}}),
                   content_type="application/json", **SUPER)

        res = client.delete(f"{self.URL}?entity=rimbayu", **SUPER)
        assert res.status_code == 200
        assert res.json()["hours"]["mon"] == [9, 18]
        assert res.json()["scope"] == "global"
        # the organization default is untouched
        assert client.get(self.URL, **SUPER).json()["hours"]["mon"] == [9, 18]

    def test_delete_without_an_entity_is_refused(self, client, superadmin, org):
        """Deleting "the global row" is not a thing — that is an edit."""
        assert client.delete(self.URL, **SUPER).status_code == 400

    def test_the_ai_is_offered_the_contacts_own_outlet_hours(self, org):
        from integrations import actions

        _, rimbayu, _ = org
        scoping.set_setting("booking_hours", json.dumps({"mon": [9, 18]}))
        scoping.set_setting("booking_hours", json.dumps({"mon": [11, 22]}), rimbayu)
        contact = Contact.objects.create(name="Aminah", phone="+60111222333")
        ContactEntity.objects.create(contact=contact, entity=rimbayu)

        assert actions.booking_hours_raw(actions.contact_entity(contact))["mon"] \
            == [11, 22]


# ---- knowledge: inheritance + scope labelling ---------------------------------

@pytest.mark.django_db
class TestKnowledgeScoping:
    URL = "/api/aicore/knowledge"

    def test_outlet_sees_global_plus_its_own_but_not_a_sibling(
            self, client, superadmin, org):
        _, rimbayu, botanic = org
        KnowledgeItem.objects.create(kind="policy", title="Refunds",
                                     content="14 days.")
        KnowledgeItem.objects.create(kind="notice", title="Rimbayu aircon",
                                     content="Studio 2 closed.", entity=rimbayu)
        KnowledgeItem.objects.create(kind="notice", title="Botanic repaint",
                                     content="Lobby closed.", entity=botanic)

        payload = client.get(f"{self.URL}?entity=rimbayu", **SUPER).json()
        titles = {i["title"] for i in payload["items"]}
        assert titles == {"Refunds", "Rimbayu aircon"}
        scopes = {i["title"]: i["scope"] for i in payload["items"]}
        assert scopes == {"Refunds": "global", "Rimbayu aircon": "entity"}
        # counts follow the same scope as the list
        assert payload["counts"]["notice"] == 1

    def test_creating_while_viewing_an_outlet_scopes_it_to_that_outlet(
            self, client, superadmin, org):
        _, rimbayu, _ = org
        res = client.post(f"{self.URL}?entity=rimbayu", data=json.dumps({
            "kind": "notice", "title": "Aircon service", "content": "Studio 2 shut."}),
            content_type="application/json", **SUPER)
        assert res.status_code in (200, 201)
        assert KnowledgeItem.objects.get(title="Aircon service").entity == rimbayu

    def test_explicit_global_scope_beats_the_viewing_context(
            self, client, superadmin, org):
        res = client.post(f"{self.URL}?entity=rimbayu", data=json.dumps({
            "kind": "policy", "title": "Company refunds", "content": "14 days.",
            "entity": None}), content_type="application/json", **SUPER)
        assert res.status_code in (200, 201)
        assert KnowledgeItem.objects.get(title="Company refunds").entity_id is None

    def test_retrieval_gives_the_contact_only_their_outlets_notice(self, org):
        from aicore import knowledge

        _, rimbayu, botanic = org
        KnowledgeItem.objects.create(kind="notice", title="Rimbayu aircon",
                                     content="Studio 2 closed.", entity=rimbayu)
        KnowledgeItem.objects.create(kind="notice", title="Botanic repaint",
                                     content="Lobby closed.", entity=botanic)
        contact = Contact.objects.create(name="Aminah", phone="+60111222333")
        ContactEntity.objects.create(contact=contact, entity=rimbayu)

        block = knowledge.build_knowledge_context(contact=contact)
        assert "Studio 2 closed." in block
        assert "Lobby closed." not in block


# ---- the switcher: inbox, health and analytics actually change ----------------

@pytest.mark.django_db
class TestSwitchingChangesWhatYouSee:
    def _seed(self, org):
        _, rimbayu, botanic = org
        rim_acct = ChannelAccount.objects.create(channel="whatsapp", name="Rimbayu WA",
                                                 entity=rimbayu)
        bot_acct = ChannelAccount.objects.create(channel="whatsapp", name="Botanic WA",
                                                 entity=botanic)
        for key, acct, ent in (("60111", rim_acct, rimbayu), ("60222", bot_acct, botanic)):
            t = Thread.objects.create(channel="whatsapp", key=key, account=acct,
                                      entity=ent)
            Message.objects.create(thread=t, direction="out", source="agent",
                                   text="hi", account=acct)
        return rim_acct, bot_acct

    def test_inbox_shows_only_the_selected_outlet(self, client, superadmin, org):
        self._seed(org)
        rim = client.get("/api/whatsapp/threads?entity=rimbayu", **SUPER).json()
        assert [t["wa_id"] for t in rim["threads"]] == ["60111"]
        both = client.get("/api/whatsapp/threads", **SUPER).json()
        assert {t["wa_id"] for t in both["threads"]} == {"60111", "60222"}

    def test_channel_health_shows_only_that_outlets_numbers(
            self, client, superadmin, org):
        self._seed(org)
        rim = client.get("/api/whatsapp/channels/health?entity=rimbayu", **SUPER).json()
        assert [a["name"] for a in rim["accounts"]] == ["Rimbayu WA"]

    def test_analytics_narrows_and_reports_its_scope(self, client, superadmin, org):
        self._seed(org)
        rim = client.get("/api/analytics/summary?entity=rimbayu", **SUPER).json()
        assert rim["entity"] == "rimbayu"
        assert rim["funnel"]["sent"] == 1
        allv = client.get("/api/analytics/summary", **SUPER).json()
        assert allv["entity"] is None
        assert allv["funnel"]["sent"] == 2

    def test_all_entities_view_breaks_the_company_down_per_outlet(
            self, client, superadmin, org):
        self._seed(org)
        payload = client.get("/api/analytics/summary", **SUPER).json()
        rows = {r["name"]: r for r in payload["by_entity"]}
        assert rows["Rimbayu"]["outbound"] == 1
        assert rows["Bandar Botanic"]["outbound"] == 1
        # a single-entity view is already that row — no breakdown
        assert client.get("/api/analytics/summary?entity=rimbayu",
                          **SUPER).json()["by_entity"] == []

    def test_an_agent_cannot_widen_their_view_with_a_crafted_slug(
            self, client, rimbayu_agent, org):
        """RBAC bounds the filter: asking for a sibling outlet you have no
        access to must not return that outlet's data."""
        self._seed(org)
        res = client.get("/api/whatsapp/threads?entity=bandar-botanic", **AGENT).json()
        assert [t["wa_id"] for t in res["threads"]] == ["60111"]

    def test_an_agent_with_one_outlet_is_pinned_even_without_the_parameter(
            self, client, rimbayu_agent, org):
        self._seed(org)
        res = client.get("/api/whatsapp/threads", **AGENT).json()
        assert [t["wa_id"] for t in res["threads"]] == ["60111"]
