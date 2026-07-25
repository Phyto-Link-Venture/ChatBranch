# backend/calls/tests/test_pronunciation.py
import json
import os
from pathlib import Path
from unittest import mock
from django.test import TestCase
from calls import pronunciation


class PronunciationTests(TestCase):
    def setUp(self):
        import tempfile
        self.pf = Path(tempfile.mkdtemp()) / "pronunciation.json"

    def _get(self):
        return self.client.get("/api/pronunciation")

    def _post(self, body):
        return self.client.post("/api/pronunciation", data=json.dumps(body), content_type="application/json")

    def test_get_empty_by_default(self):
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf):
            r = self._get()
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.json(), {"terms": [], "elevenlabs_only": True})

    @mock.patch("calls.tts.eleven_sync_pronunciation_dictionary", return_value={"dictionary_id": "d1", "version_id": "v1"})
    def test_post_saves_and_syncs(self, m_sync):
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"terms": [{"term": "Northwind", "alias": "Doo-it Ghee-nee"}]})
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.json()["terms"][0]["term"], "Northwind")
        m_sync.assert_called_once()
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf):
            self.assertEqual(pronunciation.dictionary_locator(), {"pronunciation_dictionary_id": "d1", "version_id": "v1"})

    @mock.patch("calls.tts.eleven_sync_pronunciation_dictionary", return_value={"dictionary_id": "d1", "version_id": "v1"})
    def test_post_strips_whitespace(self, m_sync):
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"terms": [{"term": " Northwind ", "alias": " Doo-it Ghee-nee "}]})
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.json()["terms"][0]["alias"], "Doo-it Ghee-nee")
        m_sync.assert_called_once_with([{"term": "Northwind", "alias": "Doo-it Ghee-nee"}])

    def test_post_no_key_501(self):
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf), \
             mock.patch.dict(os.environ, {}, clear=True):
            r = self._post({"terms": [{"term": "X", "alias": "y"}]})
        self.assertEqual(r.status_code, 501)

    def test_post_rejects_missing_alias(self):
        with mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"terms": [{"term": "X", "alias": ""}]})
        self.assertEqual(r.status_code, 400)

    def test_post_rejects_non_list(self):
        r = self._post({"terms": "nope"})
        self.assertEqual(r.status_code, 400)

    def test_post_empty_list_clears_without_key_check(self):
        with mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"terms": []})
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.json()["terms"], [])

    def test_post_upstream_error_returns_502(self):
        class FakeResponse:
            status_code = 422
            text = "bad rule"

        class FakeHTTPStatusError(Exception):
            def __init__(self):
                self.response = FakeResponse()

        with mock.patch("calls.tts.eleven_sync_pronunciation_dictionary", side_effect=FakeHTTPStatusError()), \
             mock.patch.object(pronunciation, "PRONUNCIATION_FILE", self.pf), \
             mock.patch.dict(os.environ, {"ELEVENLABS_API_KEY": "k"}):
            r = self._post({"terms": [{"term": "X", "alias": "y"}]})
        self.assertEqual(r.status_code, 502)
