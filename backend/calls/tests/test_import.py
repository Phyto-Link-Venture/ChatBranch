# backend/calls/tests/test_import.py
import json
import os
from pathlib import Path
from unittest import mock
from django.test import TestCase
from calls import voices, tts


class ImportTests(TestCase):
    def setUp(self):
        import tempfile
        self.vf = Path(tempfile.mkdtemp()) / "voices.json"

    def _post(self, body):
        return self.client.post("/api/voice/import", data=json.dumps(body),
                                 content_type="application/json")

    @mock.patch("calls.tts.eleven_get_voice", return_value={"name": "Rina"})
    def test_import_creates_voice(self, m_get):
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"voiceId": "el_abc"})
            self.assertEqual(r.status_code, 200)
            v = r.json()["voice"]
            self.assertEqual(v["name"], "Rina")
            self.assertEqual(v["language"], "unknown")  # ElevenLabs returned no labels.language
            self.assertTrue(v["custom"])
            self.assertEqual(v["status"], "ready")
            self.assertNotIn("elevenVoiceId", v)  # internal, not leaked
        m_get.assert_called_once()

    @mock.patch("calls.tts.eleven_get_voice", return_value={"name": "Wei", "labels": {"language": "zh"}})
    def test_import_uses_elevenlabs_language_label(self, m_get):
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"voiceId": "el_zh"})
            self.assertEqual(r.json()["voice"]["language"], "zh")

    def test_import_no_key_501(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(self._post({"voiceId": "el_abc"}).status_code, 501)

    def test_import_requires_voice_id(self):
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            self.assertEqual(self._post({}).status_code, 400)

    @mock.patch("calls.tts.eleven_get_voice", return_value={"name": "Rina"})
    def test_import_is_idempotent(self, m_get):
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r1 = self._post({"voiceId": "el_dup"})
            r2 = self._post({"voiceId": "el_dup"})
            self.assertEqual(r1.json()["voice"]["id"], r2.json()["voice"]["id"])
        m_get.assert_called_once()

    def test_import_upstream_error_returns_502(self):
        class FakeResponse:
            status_code = 404
            text = "voice_not_found"

        class FakeHTTPStatusError(Exception):
            def __init__(self):
                self.response = FakeResponse()

        with mock.patch("calls.tts.eleven_get_voice", side_effect=FakeHTTPStatusError()), \
             mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"voiceId": "el_missing"})
            self.assertEqual(r.status_code, 502)
            self.assertIn("404", r.json()["detail"])
