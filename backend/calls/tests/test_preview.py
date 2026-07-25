import json
import os
from pathlib import Path
from unittest import mock
from django.test import TestCase
from calls import tts


class PreviewTests(TestCase):
    def _post(self, body):
        return self.client.post("/api/voice/preview", data=json.dumps(body),
                                content_type="application/json")

    def test_cache_path_is_deterministic(self):
        a = tts.cache_path("openai", "marin", "gpt-4o-mini-tts", "hello")
        b = tts.cache_path("openai", "marin", "gpt-4o-mini-tts", "hello")
        c = tts.cache_path("openai", "marin", "gpt-4o-mini-tts", "world")
        self.assertEqual(a, b)
        self.assertNotEqual(a, c)
        self.assertTrue(str(a).endswith(".mp3"))

    @mock.patch("calls.tts.openai_speech", return_value=b"FAKEMP3")
    def test_preview_generates_then_caches(self, m_speech):
        with mock.patch.object(tts, "_CACHE_DIR", Path(self._tmp)):
            r1 = self._post({"voiceId": "marin", "text": "Good afternoon."})
            self.assertEqual(r1.status_code, 200)
            self.assertEqual(r1["Content-Type"], "audio/mpeg")
            self.assertEqual(r1.content, b"FAKEMP3")
            r2 = self._post({"voiceId": "marin", "text": "Good afternoon."})
            self.assertEqual(r2.content, b"FAKEMP3")
        # generated once, second served from cache
        self.assertEqual(m_speech.call_count, 1)

    def test_puck_preview_rejected(self):
        self.assertEqual(self._post({"voiceId": "puck", "text": "hi"}).status_code, 400)

    def test_unknown_voice_rejected(self):
        self.assertEqual(self._post({"voiceId": "nope", "text": "hi"}).status_code, 400)

    def test_missing_text_rejected(self):
        self.assertEqual(self._post({"voiceId": "marin"}).status_code, 400)

    def test_overlong_text_rejected(self):
        self.assertEqual(self._post({"voiceId": "marin", "text": "x" * 401}).status_code, 400)

    @mock.patch("calls.tts.openai_speech", side_effect=Exception("boom"))
    def test_provider_failure_returns_502(self, m_speech):
        with mock.patch.object(tts, "_CACHE_DIR", Path(self._tmp)):
            with mock.patch.dict(os.environ, {"OPENAI_API_KEY": "k"}):
                r = self._post({"voiceId": "marin", "text": "hi"})
        self.assertEqual(r.status_code, 502)

    def test_missing_key_rejected_before_provider_call(self):
        import tempfile
        empty_dir = tempfile.mkdtemp()
        with mock.patch.object(tts, "_CACHE_DIR", Path(empty_dir)):
            with mock.patch.dict(os.environ, {}, clear=True):
                r = self._post({"voiceId": "marin", "text": "unique-501-text"})
        self.assertEqual(r.status_code, 501)

    def setUp(self):
        import tempfile
        self._tmp = tempfile.mkdtemp()

    @mock.patch("calls.pronunciation.dictionary_locator", return_value=None)
    @mock.patch("calls.tts.eleven_speech", return_value=b"ELMP3")
    @mock.patch("calls.voices.provider_for", return_value="elevenlabs")
    @mock.patch("calls.voices.eleven_voice_id", return_value="el_abc")
    def test_elevenlabs_preview(self, m_id, m_prov, m_speech, m_locator):
        import os
        with mock.patch.object(tts, "_CACHE_DIR", Path(self._tmp)), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"voiceId": "vx_test", "text": "Hello there."})
            self.assertEqual(r.status_code, 200)
            self.assertEqual(r.content, b"ELMP3")
        m_speech.assert_called_once_with("el_abc", "Hello there.", None)

    @mock.patch("calls.voices.provider_for", return_value="elevenlabs")
    def test_elevenlabs_no_key_501(self, m_prov):
        import os
        with mock.patch.dict(os.environ, {}, clear=True), \
             mock.patch.object(tts, "_CACHE_DIR", Path(self._tmp)):
            self.assertEqual(self._post({"voiceId": "vx", "text": "hi"}).status_code, 501)
