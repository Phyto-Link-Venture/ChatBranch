"""chat_provider() selection: auto priority chain + the chat_provider_pref
pin (Settings → App Keys → "Chat AI provider") + ILMU availability."""
import pytest

from whatsapp import wa_config


@pytest.mark.django_db
class TestChatProviderChain:
    @pytest.fixture(autouse=True)
    def clean_env(self, monkeypatch):
        # neutralize whatever keys the developer's real .env injected at import
        for k in ("openai_api_key", "openrouter_api_key", "google_api_key",
                  "ilmu_api_key", "chat_provider_pref", "chat_model"):
            monkeypatch.setitem(wa_config._GLOBAL_ENV, k, "")

    def _set(self, **rows):
        from crm.models import Setting
        for key, value in rows.items():
            Setting.objects.update_or_create(key=key, defaults={"value": value})

    def test_auto_prefers_openrouter(self):
        self._set(openrouter_api_key="sk-or", openai_api_key="sk-oa", ilmu_api_key="sk-il")
        base, key, model = wa_config.chat_provider()
        assert "openrouter.ai" in base and key == "sk-or"

    def test_auto_falls_to_ilmu_when_no_openrouter_openai(self):
        self._set(ilmu_api_key="sk-il", google_api_key="g-key")
        base, key, model = wa_config.chat_provider()
        assert base == wa_config.ILMU_BASE_URL
        assert key == "sk-il"
        assert model == "ilmu-v3.1"

    def test_pref_pins_ilmu_over_auto_priority(self):
        self._set(openrouter_api_key="sk-or", ilmu_api_key="sk-il",
                  chat_provider_pref="ilmu")
        base, key, model = wa_config.chat_provider()
        assert base == wa_config.ILMU_BASE_URL and key == "sk-il"

    def test_pref_without_key_falls_back_to_auto(self):
        self._set(openai_api_key="sk-oa", chat_provider_pref="ilmu")
        base, key, model = wa_config.chat_provider()
        assert "api.openai.com" in base and key == "sk-oa"

    def test_chat_model_overrides_ilmu_default(self):
        self._set(ilmu_api_key="sk-il", chat_provider_pref="ilmu",
                  chat_model="ilmu-mini-v3.3")
        _, _, model = wa_config.chat_provider()
        assert model == "ilmu-mini-v3.3"

    def test_ai_available_counts_ilmu_key(self):
        assert not wa_config.ai_available()
        self._set(ilmu_api_key="sk-il")
        assert wa_config.ai_available()
