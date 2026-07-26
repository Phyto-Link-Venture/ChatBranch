"""core/app_settings + the /api/settings/ai-providers endpoint.

Covers what actually protects the settings migration:

* accessor precedence — a crm.Setting row beats backend/.env; env is the
  fallback when the Setting is unset OR blank; the built-in default when neither
  is set (the tunable getters), with the safe-minimum clamp;
* the masked round-trip — a "••" value on the way back in leaves the stored
  secret UNCHANGED, while a real new value replaces it (both an own key,
  elevenlabs, and a wa_config-delegated key, openai);
* the superadmin gate — admin/agent get 403 on PUT, any role reads, no role 401s;
* movable getters still behave (return today's defaults) with nothing
  configured, so unconfigured deployments are byte-for-byte unchanged.

Fully offline — no network, no OpenAI/ElevenLabs.
"""
import json

import pytest
from django.contrib.auth.models import User

from core import app_settings
from crm.models import AuditLog, Profile, Setting
from whatsapp import wa_config

URL = "/api/settings/ai-providers"

TOKEN_SUPER = "aip-super-token"
TOKEN_ADMIN = "aip-admin-token"
TOKEN_AGENT = "aip-agent-token"
SUPER = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_SUPER}"}
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def superadmin(db):
    u = User.objects.create_user(username="aip_super", password="x")
    Profile.objects.create(user=u, role="superadmin", token=TOKEN_SUPER)
    return u


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="aip_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="aip_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


@pytest.fixture
def clean_env(monkeypatch):
    """Neutralise whatever the developer's real backend/.env injected, both the
    live os.environ vars our own getters read AND the module-level snapshot
    wa_config took at import time — so precedence tests are deterministic."""
    for env in ("ELEVENLABS_API_KEY", "OPENAI_API_KEY", "OPENROUTER_API_KEY",
                "GOOGLE_API_KEY", "ILMU_API_KEY", "EMAIL_DEFAULT_SUBJECT",
                "FLOWS_TICK_SECONDS", "WHATSAPP_HEALTH_INTERVAL",
                "AICORE_SUMMARY_INTERVAL", "AICORE_SUMMARY_IDLE_MIN"):
        monkeypatch.delenv(env, raising=False)
    for k in ("openai_api_key", "openrouter_api_key", "google_api_key",
              "ilmu_api_key", "chat_provider_pref", "chat_model"):
        monkeypatch.setitem(wa_config._GLOBAL_ENV, k, "")


def _set(**rows):
    for key, value in rows.items():
        Setting.objects.update_or_create(key=key, defaults={"value": value})


# ---- accessor precedence -----------------------------------------------------

@pytest.mark.django_db
class TestPrecedence:
    def test_defaults_when_nothing_configured(self, clean_env):
        assert app_settings.flows_tick_seconds() == 10
        assert app_settings.whatsapp_health_interval() == 300
        assert app_settings.aicore_summary_interval() == 600
        assert app_settings.aicore_summary_idle_min() == 120
        assert app_settings.elevenlabs_key() == ""
        assert app_settings.openai_key() == ""

    def test_env_used_when_setting_unset(self, clean_env, monkeypatch):
        monkeypatch.setenv("ELEVENLABS_API_KEY", "env-el-key")
        monkeypatch.setenv("FLOWS_TICK_SECONDS", "45")
        assert app_settings.elevenlabs_key() == "env-el-key"
        assert app_settings.flows_tick_seconds() == 45

    def test_setting_beats_env_secret(self, clean_env, monkeypatch):
        monkeypatch.setenv("ELEVENLABS_API_KEY", "env-el-key")
        _set(elevenlabs_api_key="db-el-key")
        assert app_settings.elevenlabs_key() == "db-el-key"

    def test_setting_beats_env_int(self, clean_env, monkeypatch):
        monkeypatch.setenv("FLOWS_TICK_SECONDS", "45")
        _set(flows_tick_seconds="12")
        assert app_settings.flows_tick_seconds() == 12

    def test_blank_setting_falls_back_to_env(self, clean_env, monkeypatch):
        monkeypatch.setenv("ELEVENLABS_API_KEY", "env-el-key")
        _set(elevenlabs_api_key="   ")  # whitespace-only counts as unset
        assert app_settings.elevenlabs_key() == "env-el-key"

    def test_int_below_minimum_is_clamped(self, clean_env):
        _set(whatsapp_health_interval="5")   # min is 30
        assert app_settings.whatsapp_health_interval() == 30
        _set(flows_tick_seconds="0")         # min is 1
        assert app_settings.flows_tick_seconds() == 1

    def test_invalid_int_setting_falls_to_default(self, clean_env):
        _set(aicore_summary_idle_min="not-a-number")
        assert app_settings.aicore_summary_idle_min() == 120

    def test_openai_key_delegates_to_wa_config_setting(self, clean_env):
        _set(openai_api_key="sk-oa-db")
        assert app_settings.openai_key() == "sk-oa-db"

    def test_email_subject_delegates_to_msg_config(self, clean_env):
        _set(email_subject="Payment reminder")
        assert app_settings.email_subject() == "Payment reminder"


# ---- movable getters' existing callers still behave --------------------------

@pytest.mark.django_db
class TestCallersUnchanged:
    def test_memory_idle_minutes_uses_accessor(self, clean_env):
        from aicore import memory
        assert memory._idle_minutes() == 120
        _set(aicore_summary_idle_min="30")
        assert memory._idle_minutes() == 30

    def test_health_check_interval_uses_accessor(self, clean_env):
        from whatsapp import health
        assert health.check_interval() == 300
        _set(whatsapp_health_interval="600")
        assert health.check_interval() == 600


# ---- auth gating -------------------------------------------------------------

@pytest.mark.django_db
class TestAuth:
    def test_get_401_without_role(self, client, admin):
        # `admin` fixture ensures a Profile row exists, so the pre-seed X-Role
        # bootstrap is OFF and an unauthenticated request is a real 401.
        assert client.get(URL).status_code == 401

    def test_get_ok_for_any_role(self, client, agent):
        r = client.get(URL, **AGENT)
        assert r.status_code == 200
        body = r.json()
        assert "fields" in body and body["chat_provider_options"][0] == ""
        # secrets are masked, never present in the clear
        assert "openai_api_key" not in body
        assert "openai_api_key_masked" in body

    def test_put_requires_superadmin_admin(self, client, admin):
        r = client.put(URL, data=json.dumps({"chat_model": "gpt-4o"}),
                       content_type="application/json", **ADMIN)
        assert r.status_code == 403

    def test_put_requires_superadmin_agent(self, client, agent):
        r = client.put(URL, data=json.dumps({"chat_model": "gpt-4o"}),
                       content_type="application/json", **AGENT)
        assert r.status_code == 403

    def test_put_empty_patch_is_400(self, client, superadmin):
        r = client.put(URL, data=json.dumps({}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400


# ---- write path: masking, round-trip, validation, audit ----------------------

@pytest.mark.django_db
class TestWrite:
    def test_superadmin_saves_secret_masked_and_audits(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"elevenlabs_api_key": "el-secret-1234"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert r.json()["elevenlabs_api_key_masked"] == "••••1234"
        assert app_settings.elevenlabs_key() == "el-secret-1234"
        assert AuditLog.objects.filter(action="settings.update",
                                       target="ai-providers").exists()

    def test_masked_round_trip_keeps_secret_changes_other(self, client, superadmin, clean_env):
        _set(elevenlabs_api_key="el-secret-1234")
        r = client.put(URL, data=json.dumps({"elevenlabs_api_key": "••••1234",
                                             "openai_api_key": "sk-oa-new-5678"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert app_settings.elevenlabs_key() == "el-secret-1234"   # untouched
        assert app_settings.openai_key() == "sk-oa-new-5678"       # replaced

    def test_int_tunable_saved(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"flows_tick_seconds": 25}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert app_settings.flows_tick_seconds() == 25

    def test_int_below_minimum_rejected(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"whatsapp_health_interval": 5}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400
        assert "at least 30" in r.json()["detail"]

    def test_non_integer_tunable_rejected(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"flows_tick_seconds": "soon"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400

    def test_invalid_chat_provider_rejected(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"chat_provider_pref": "acme"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400

    def test_valid_chat_provider_saved(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"chat_provider_pref": "ilmu"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert app_settings.chat_provider_pref() == "ilmu"


# ---- provider fallback order + model catalog ---------------------------------

@pytest.mark.django_db
class TestProviderOrderAndModels:
    def test_payload_exposes_order_and_model_options(self, client, agent, clean_env):
        body = client.get(URL, **AGENT).json()
        assert body["chat_provider_order"] == ["openrouter", "openai", "ilmu", "google"]
        cat = body["chat_model_options"]
        assert isinstance(cat, list) and any(g["provider"] == "openai" for g in cat)
        providers = {g["provider"] for g in cat}
        assert {"openai", "openrouter", "google", "ilmu"} <= providers
        assert "gpt-4o" in next(g for g in cat if g["provider"] == "openai")["models"]
        # order field + model field are in the self-describing schema
        kinds = {f["key"]: f["kind"] for f in body["fields"]}
        assert kinds["chat_provider_order"] == "order"
        assert kinds["chat_model"] == "model"

    def test_order_saved_as_list(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"chat_provider_order": ["openai", "google"]}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert r.json()["chat_provider_order"] == ["openai", "google"]
        assert app_settings.chat_provider_order() == ["openai", "google"]

    def test_order_saved_as_json_string(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"chat_provider_order": '["ilmu","openai"]'}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert app_settings.chat_provider_order() == ["ilmu", "openai"]

    def test_order_unknown_member_rejected(self, client, superadmin, clean_env):
        r = client.put(URL, data=json.dumps({"chat_provider_order": ["openai", "acme"]}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 400
        assert "acme" in r.json()["detail"]

    def test_custom_model_free_form_saved(self, client, superadmin, clean_env):
        # A model id OUTSIDE the catalog still persists (free-form underneath).
        r = client.put(URL, data=json.dumps({"chat_model": "some/custom-model-xyz"}),
                       content_type="application/json", **SUPER)
        assert r.status_code == 200
        assert app_settings.chat_model() == "some/custom-model-xyz"
