# backend/calls/tests/test_voices_store.py
import json
from pathlib import Path
from unittest import mock

from django.test import SimpleTestCase

from calls import voices


class VoiceStoreTests(SimpleTestCase):
    def test_presets_include_openai_and_gemini(self):
        ids = {p["id"]: p for p in voices.PRESETS}
        self.assertEqual(ids["marin"]["provider"], "openai")
        self.assertEqual(ids["cedar"]["provider"], "openai")
        self.assertEqual(ids["alloy"]["provider"], "openai")
        self.assertEqual(ids["puck"]["provider"], "gemini")
        # frontend Voice shape
        for p in voices.PRESETS:
            self.assertEqual(set(p) >= {"id", "name", "gender", "accent", "status", "custom", "provider"}, True)
            self.assertEqual(p["status"], "ready")
            self.assertFalse(p["custom"])

    def test_provider_for_preset_and_unknown(self):
        self.assertEqual(voices.provider_for("marin"), "openai")
        self.assertEqual(voices.provider_for("puck"), "gemini")
        self.assertIsNone(voices.provider_for("does-not-exist"))

    def test_add_and_load_clone(self, ):
        with mock.patch.object(voices, "VOICES_FILE", Path(self.tmp)) as _:
            rec = voices.add_voice({
                "id": "vx_test", "name": "Cloned", "gender": "Male",
                "accent": "Custom", "status": "ready", "custom": True,
                "provider": "elevenlabs", "elevenVoiceId": "el_123",
            })
            self.assertEqual(rec["id"], "vx_test")
            loaded = voices.load_voices()
            self.assertIn("vx_test", {v["id"] for v in loaded})
            self.assertEqual(voices.provider_for("vx_test"), "elevenlabs")
            self.assertEqual(voices.eleven_voice_id("vx_test"), "el_123")

    def setUp(self):
        import tempfile, os
        self.tmp = os.path.join(tempfile.mkdtemp(), "voices.json")
