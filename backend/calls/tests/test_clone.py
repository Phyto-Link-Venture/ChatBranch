# backend/calls/tests/test_clone.py
from unittest import mock
from pathlib import Path
from django.core.files.uploadedfile import SimpleUploadedFile
from django.test import TestCase
from calls import voices, tts


class CloneTests(TestCase):
    def setUp(self):
        import tempfile, os
        self.vf = Path(tempfile.mkdtemp()) / "voices.json"

    def _post(self, **kw):
        return self.client.post("/api/voice/clone", data=kw)

    @mock.patch("calls.tts.eleven_clone", return_value="el_new")
    def test_clone_creates_voice(self, m_clone):
        import os
        audio = SimpleUploadedFile("s.mp3", b"AUDIODATA", content_type="audio/mpeg")
        with mock.patch.object(voices, "VOICES_FILE", self.vf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post(name="Azri", audio=audio, language="ms")
            self.assertEqual(r.status_code, 200)
            v = r.json()["voice"]
            self.assertEqual(v["name"], "Azri")
            self.assertEqual(v["language"], "ms")
            self.assertTrue(v["custom"])
            self.assertEqual(v["status"], "ready")
            self.assertNotIn("elevenVoiceId", v)  # internal, not leaked
        m_clone.assert_called_once()

    def test_clone_no_key_501(self):
        import os
        audio = SimpleUploadedFile("s.mp3", b"A", content_type="audio/mpeg")
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(self._post(name="X", audio=audio, language="en").status_code, 501)

    def test_clone_requires_name_and_audio(self):
        import os
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            self.assertEqual(self._post(name="").status_code, 400)

    def test_clone_requires_valid_language(self):
        import os
        audio = SimpleUploadedFile("s.mp3", b"A", content_type="audio/mpeg")
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            self.assertEqual(self._post(name="X", audio=audio, language="fr").status_code, 400)

    def test_clone_rejects_non_audio(self):
        import os
        bad = SimpleUploadedFile("s.txt", b"nope", content_type="text/plain")
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            self.assertEqual(self._post(name="X", audio=bad, language="en").status_code, 400)
