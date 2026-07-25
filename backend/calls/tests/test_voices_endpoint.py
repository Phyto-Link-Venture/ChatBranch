from unittest import mock
from django.test import TestCase


class VoicesEndpointTests(TestCase):
    def test_get_voices_returns_presets(self):
        resp = self.client.get("/api/voices")
        self.assertEqual(resp.status_code, 200)
        data = resp.json()["voices"]
        ids = {v["id"] for v in data}
        self.assertTrue({"marin", "cedar", "alloy", "puck"} <= ids)
        # provider ("Source") and language are filterable fields, must be present
        self.assertIn("provider", data[0])
        self.assertIn("language", data[0])

    def test_post_not_allowed(self):
        self.assertEqual(self.client.post("/api/voices").status_code, 405)
