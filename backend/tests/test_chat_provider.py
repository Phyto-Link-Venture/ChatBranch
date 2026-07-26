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


@pytest.mark.django_db
class TestChatProviderOrder:
    """The configurable auto/priority fallback order (`chat_provider_order`)."""

    @pytest.fixture(autouse=True)
    def clean_env(self, monkeypatch):
        for k in ("openai_api_key", "openrouter_api_key", "google_api_key",
                  "ilmu_api_key", "chat_provider_pref", "chat_model"):
            monkeypatch.setitem(wa_config._GLOBAL_ENV, k, "")
        monkeypatch.delenv("CHAT_PROVIDER_ORDER", raising=False)

    def _set(self, **rows):
        from crm.models import Setting
        for key, value in rows.items():
            Setting.objects.update_or_create(key=key, defaults={"value": value})

    def test_default_order_when_unset(self):
        assert wa_config.chat_provider_order() == list(
            wa_config.DEFAULT_CHAT_PROVIDER_ORDER)

    def test_json_setting_round_trip(self):
        self._set(chat_provider_order='["openai", "google"]')
        assert wa_config.chat_provider_order() == ["openai", "google"]

    def test_csv_setting_parsed(self):
        self._set(chat_provider_order="ilmu, google , openai")
        assert wa_config.chat_provider_order() == ["ilmu", "google", "openai"]

    def test_unknown_members_dropped_and_deduped(self):
        self._set(chat_provider_order="acme,openai,openai,google")
        assert wa_config.chat_provider_order() == ["openai", "google"]

    def test_bad_value_falls_back_to_default(self):
        self._set(chat_provider_order="   ")
        assert wa_config.chat_provider_order() == list(
            wa_config.DEFAULT_CHAT_PROVIDER_ORDER)
        self._set(chat_provider_order="acme, widgets")  # all unknown -> empty
        assert wa_config.chat_provider_order() == list(
            wa_config.DEFAULT_CHAT_PROVIDER_ORDER)

    def test_env_used_when_setting_unset(self, monkeypatch):
        monkeypatch.setenv("CHAT_PROVIDER_ORDER", "openai,ilmu")
        assert wa_config.chat_provider_order() == ["openai", "ilmu"]

    def test_setting_beats_env(self, monkeypatch):
        monkeypatch.setenv("CHAT_PROVIDER_ORDER", "openai")
        self._set(chat_provider_order='["google"]')
        assert wa_config.chat_provider_order() == ["google"]

    def test_chat_provider_honours_configured_order(self):
        # Put openai ahead of openrouter — with both keys present, openai wins.
        self._set(openrouter_api_key="sk-or", openai_api_key="sk-oa",
                  chat_provider_order="openai,openrouter")
        base, key, _ = wa_config.chat_provider()
        assert "api.openai.com" in base and key == "sk-oa"

    def test_order_skips_keyless_provider(self):
        # ilmu first but no key -> falls to next in the configured order.
        self._set(openai_api_key="sk-oa", chat_provider_order="ilmu,openai,google")
        base, key, _ = wa_config.chat_provider()
        assert "api.openai.com" in base and key == "sk-oa"

    def test_ultimate_fallback_is_last_in_order_when_no_keys(self):
        # No keys anywhere -> last provider in the configured order is returned
        # (key empty), so a caller still gets a well-formed target.
        self._set(chat_provider_order="openai,ilmu")
        base, key, _ = wa_config.chat_provider()
        assert base == wa_config.ILMU_BASE_URL and key == ""

    def test_save_chat_provider_order_cleans_and_persists(self):
        cleaned = wa_config.save_chat_provider_order(
            ["Google", "acme", "openai", "google"])
        assert cleaned == ["google", "openai"]
        assert wa_config.chat_provider_order() == ["google", "openai"]

    def test_save_empty_clears_back_to_default(self):
        wa_config.save_chat_provider_order(["openai"])
        assert wa_config.chat_provider_order() == ["openai"]
        wa_config.save_chat_provider_order([])  # nothing valid -> cleared row
        assert wa_config.chat_provider_order() == list(
            wa_config.DEFAULT_CHAT_PROVIDER_ORDER)

    def test_pin_still_overrides_order(self):
        self._set(openrouter_api_key="sk-or", ilmu_api_key="sk-il",
                  chat_provider_pref="ilmu", chat_provider_order="openrouter,openai")
        base, key, _ = wa_config.chat_provider()
        assert base == wa_config.ILMU_BASE_URL and key == "sk-il"
