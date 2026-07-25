"""Conversation-brain selector (/api/calls voice/llm-settings) — the pipeline
LLM provider store read fresh at dial time by both call transports."""
import pytest

from calls import voice_settings


@pytest.fixture(autouse=True)
def isolated_store(tmp_path, monkeypatch):
    monkeypatch.setattr(voice_settings, "VOICE_SETTINGS_FILE",
                        tmp_path / "voice_settings.json")


class TestLlmProviderStore:
    def test_default_is_env_or_openai(self, monkeypatch):
        assert voice_settings.get_llm_provider() in voice_settings.LLM_PROVIDERS

    def test_save_and_read_back(self):
        for p in voice_settings.LLM_PROVIDERS:
            assert voice_settings.save_llm_provider(p) == p
            assert voice_settings.get_llm_provider() == p

    def test_invalid_provider_rejected(self):
        with pytest.raises(ValueError):
            voice_settings.save_llm_provider("claude")

    def test_llm_and_stt_coexist(self):
        voice_settings.save_stt_provider("openai")
        voice_settings.save_llm_provider("gemini")
        assert voice_settings.get_stt_provider() == "openai"
        assert voice_settings.get_llm_provider() == "gemini"


@pytest.mark.django_db
class TestLlmSettingsEndpoint:
    def test_get_returns_provider(self, client):
        r = client.get("/api/voice/llm-settings")
        assert r.status_code == 200
        assert r.json()["llm_provider"] in voice_settings.LLM_PROVIDERS

    def test_post_saves(self, client):
        r = client.post("/api/voice/llm-settings", data='{"llm_provider": "openrouter"}',
                        content_type="application/json")
        assert r.status_code == 200
        assert r.json()["llm_provider"] == "openrouter"
        assert voice_settings.get_llm_provider() == "openrouter"

    def test_post_invalid_400(self, client):
        r = client.post("/api/voice/llm-settings", data='{"llm_provider": "nope"}',
                        content_type="application/json")
        assert r.status_code == 400
