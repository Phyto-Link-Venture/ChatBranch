# backend/calls/tests/test_voice_detail.py
import json
import os
from pathlib import Path
from unittest import mock
from django.test import TestCase
from calls import voices


class VoiceDetailTests(TestCase):
    def setUp(self):
        import tempfile
        self.vf = Path(tempfile.mkdtemp()) / "voices.json"

    def _seed(self, record):
        with mock.patch.object(voices, "VOICES_FILE", self.vf):
            voices.add_voice(record)

    def _patch(self, voice_id, body):
        return self.client.generic(
            "PATCH", f"/api/voice/{voice_id}", data=json.dumps(body), content_type="application/json"
        )

    def _delete(self, voice_id):
        return self.client.generic("DELETE", f"/api/voice/{voice_id}")

    def test_patch_updates_fields(self):
        self._seed({"id": "vx_1", "name": "Old", "gender": "Custom", "accent": "Custom",
                     "language": "en", "status": "ready", "custom": True, "provider": "elevenlabs",
                     "elevenVoiceId": "el_1"})
        with mock.patch.object(voices, "VOICES_FILE", self.vf):
            r = self._patch("vx_1", {"name": "New", "language": "ms"})
        self.assertEqual(r.status_code, 200)
        v = r.json()["voice"]
        self.assertEqual(v["name"], "New")
        self.assertEqual(v["language"], "ms")
        self.assertNotIn("elevenVoiceId", v)  # internal, not leaked

    def test_patch_rejects_invalid_language(self):
        self._seed({"id": "vx_1", "name": "Old", "gender": "Custom", "accent": "Custom",
                     "language": "en", "status": "ready", "custom": True, "provider": "elevenlabs",
                     "elevenVoiceId": "el_1"})
        with mock.patch.object(voices, "VOICES_FILE", self.vf):
            r = self._patch("vx_1", {"language": "fr"})
        self.assertEqual(r.status_code, 400)

    def test_patch_preset_returns_404(self):
        r = self._patch("marin", {"name": "New"})
        self.assertEqual(r.status_code, 404)

    def test_patch_unknown_voice_returns_404(self):
        with mock.patch.object(voices, "VOICES_FILE", self.vf):
            r = self._patch("vx_missing", {"name": "New"})
        self.assertEqual(r.status_code, 404)

    @mock.patch("calls.tts.eleven_delete_voice")
    def test_delete_removes_local_and_calls_elevenlabs(self, m_delete):
        self._seed({"id": "vx_1", "name": "Old", "gender": "Custom", "accent": "Custom",
                     "language": "en", "status": "ready", "custom": True, "provider": "elevenlabs",
                     "elevenVoiceId": "el_1"})
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._delete("vx_1")
            self.assertEqual(r.status_code, 200)
            self.assertEqual(voices.get_custom_voice("vx_1"), None)
        m_delete.assert_called_once_with("el_1")

    def test_delete_preset_returns_404(self):
        r = self._delete("cedar")
        self.assertEqual(r.status_code, 404)

    def test_delete_no_key_returns_501(self):
        self._seed({"id": "vx_1", "name": "Old", "gender": "Custom", "accent": "Custom",
                     "language": "en", "status": "ready", "custom": True, "provider": "elevenlabs",
                     "elevenVoiceId": "el_1"})
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {}, clear=True):
            r = self._delete("vx_1")
        self.assertEqual(r.status_code, 501)
        with mock.patch.object(voices, "VOICES_FILE", self.vf):
            self.assertIsNotNone(voices.get_custom_voice("vx_1"))  # not removed on failure

    def test_delete_upstream_error_returns_502_and_keeps_local(self):
        class FakeResponse:
            status_code = 500
            text = "boom"

        class FakeHTTPStatusError(Exception):
            def __init__(self):
                self.response = FakeResponse()

        self._seed({"id": "vx_1", "name": "Old", "gender": "Custom", "accent": "Custom",
                     "language": "en", "status": "ready", "custom": True, "provider": "elevenlabs",
                     "elevenVoiceId": "el_1"})
        with mock.patch("calls.tts.eleven_delete_voice", side_effect=FakeHTTPStatusError()), \
             mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._delete("vx_1")
            self.assertEqual(r.status_code, 502)
            self.assertIsNotNone(voices.get_custom_voice("vx_1"))  # not removed on upstream failure
