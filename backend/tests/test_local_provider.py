"""Local / self-hosted model support (#3) — Ollama / LM Studio / vLLM.

A `local` chat provider whose base_url is operator-configurable, so the
OpenAI-compatible client can point at http://localhost:11434/v1 (Ollama) etc.
It is opt-in (not in the auto fallback order) — used only when pinned.
"""
import pytest

from core import app_settings
from crm.models import Setting
from whatsapp import wa_config


@pytest.mark.django_db
class TestLocalProviderSlot:
    def test_slot_uses_configured_base_url(self):
        Setting.objects.create(entity=None, key="local_base_url",
                               value="http://localhost:11434/v1")
        base, key, model = wa_config._provider_slot("local", "")
        assert base == "http://localhost:11434/v1"
        assert key == "ollama"          # dummy key so the OpenAI client is happy
        assert model == "llama3.1"      # built-in default

    def test_slot_honours_chat_model_and_api_key(self):
        Setting.objects.create(entity=None, key="local_base_url",
                               value="http://localhost:1234/v1")
        Setting.objects.create(entity=None, key="local_api_key", value="lm-studio")
        base, key, model = wa_config._provider_slot("local", "qwen2.5")
        assert base == "http://localhost:1234/v1" and key == "lm-studio" and model == "qwen2.5"

    def test_entry_none_until_base_url_set(self):
        # No base_url → not configured → not eligible (even though key defaults).
        assert wa_config._provider_entry("local", "") is None
        Setting.objects.create(entity=None, key="local_base_url",
                               value="http://localhost:11434/v1")
        assert wa_config._provider_entry("local", "") is not None


@pytest.mark.django_db
class TestLocalProviderSelection:
    def test_pinned_local_is_used(self):
        Setting.objects.create(entity=None, key="local_base_url",
                               value="http://localhost:11434/v1")
        Setting.objects.create(entity=None, key="chat_provider_pref", value="local")
        Setting.objects.create(entity=None, key="chat_model", value="llama3.2")
        base, key, model = wa_config.chat_provider()
        assert base == "http://localhost:11434/v1" and model == "llama3.2"

    def test_local_is_opt_in_not_in_default_order(self):
        # Without a pin, the default fallback order must NOT include local, so a
        # stray localhost endpoint is never auto-tried.
        assert "local" not in wa_config.DEFAULT_CHAT_PROVIDER_ORDER
        assert "local" in wa_config.CHAT_PROVIDERS


@pytest.mark.django_db
class TestLocalProviderSettingsApi:
    def test_option_and_model_group_exposed(self):
        assert "local" in app_settings.chat_provider_options()
        groups = {g["provider"] for g in app_settings.chat_model_options()}
        assert "local" in groups

    def test_effective_and_save_roundtrip(self):
        app_settings.save({"local_base_url": "http://localhost:11434/v1",
                           "local_api_key": "ollama", "chat_provider_pref": "local"})
        eff = app_settings.effective()
        assert eff["local_base_url"] == "http://localhost:11434/v1"
        assert eff["chat_provider_pref"] == "local"
        # and it actually drives provider selection
        base, _k, _m = wa_config.chat_provider()
        assert base == "http://localhost:11434/v1"

    def test_local_api_key_is_masked_secret(self):
        assert "local_api_key" in app_settings.SECRET_KEYS
