# backend/calls/tests/test_dial_voice.py
import json
import os
from unittest import mock
from django.test import TestCase


class DialVoiceTests(TestCase):
    def _post(self, body):
        return self.client.post(
            "/api/calls/dial", data=json.dumps(body), content_type="application/json"
        )

    @mock.patch("calls.lk.dial", return_value="room-1")
    def test_default_dial_has_no_voice_metadata(self, m_dial):
        r = self._post({"phone": "+60123456789", "name": "Ah Beng"})
        self.assertEqual(r.status_code, 200)
        extra = m_dial.call_args[0][3]
        self.assertIsNone(extra)  # unchanged behavior: no voice fields sent -> extra stays None

    def test_elevenlabs_no_key_returns_501(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            r = self._post(
                {"phone": "+60123456789", "voiceProvider": "elevenlabs", "voiceId": "vx_x"}
            )
        self.assertEqual(r.status_code, 501)

    def test_elevenlabs_unknown_voice_returns_400(self):
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post(
                {"phone": "+60123456789", "voiceProvider": "elevenlabs", "voiceId": "nope"}
            )
        self.assertEqual(r.status_code, 400)

    @mock.patch("calls.lk.dial", return_value="room-2")
    @mock.patch("calls.voices.eleven_voice_id", return_value="el_real_id")
    def test_elevenlabs_valid_voice_passes_metadata(self, m_voice_id, m_dial):
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post(
                {"phone": "+60123456789", "voiceProvider": "elevenlabs", "voiceId": "vx_real"}
            )
        self.assertEqual(r.status_code, 200)
        extra = m_dial.call_args[0][3]
        self.assertEqual(extra["voice_provider"], "elevenlabs")
        self.assertEqual(extra["voice"], "el_real_id")
        # pipeline calls carry the Settings-page STT + brain picks to the agent
        self.assertIn(extra["stt_provider"], ("openai", "elevenlabs"))
        self.assertIn(extra["llm_provider"], ("openai", "ilmu", "openrouter", "gemini"))
        m_voice_id.assert_called_once_with("vx_real")

    @mock.patch("calls.lk.dial", return_value="room-3")
    def test_openai_voice_passes_through_unresolved(self, m_dial):
        r = self._post(
            {"phone": "+60123456789", "voiceProvider": "openai", "voiceId": "cedar"}
        )
        self.assertEqual(r.status_code, 200)
        extra = m_dial.call_args[0][3]
        self.assertEqual(extra["voice_provider"], "openai")
        self.assertEqual(extra["voice"], "cedar")

    @mock.patch("calls.lk.dial", return_value="room-4")
    @mock.patch("calls.voices.eleven_voice_id", return_value="el_test_voice")
    @mock.patch("calls.contacts.dial_meta", return_value={"contact_name": "Test Contact", "language": "en"})
    @mock.patch(
        "calls.contacts.get_contact",
        return_value={"phone": "+60123456789", "name": "Test Contact", "language": "en"},
    )
    def test_contact_dial_merges_voice_into_existing_extra(
        self, m_get_contact, m_dial_meta, m_voice_id, m_dial
    ):
        # contact_id dials already build an `extra` dict (dial_meta) — voice
        # fields must be MERGED into it, not replace it. Contact lookup is
        # mocked so this test doesn't depend on the gitignored, per-environment
        # backend/data/contacts.json seed data.
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post(
                {"contact_id": 1, "voiceProvider": "elevenlabs", "voiceId": "vx_real"}
            )
        self.assertEqual(r.status_code, 200)
        extra = m_dial.call_args[0][3]
        self.assertEqual(extra["voice_provider"], "elevenlabs")
        self.assertEqual(extra["voice"], "el_test_voice")
        self.assertIn("contact_name", extra)  # dial_meta's fields survive the merge
