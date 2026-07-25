# backend/calls/tests/test_memory.py
from pathlib import Path
from unittest import mock
from django.test import TestCase
from calls import memory


class MemoryTests(TestCase):
    def setUp(self):
        import tempfile
        self.dir = Path(tempfile.mkdtemp())

    def test_load_missing_returns_empty_defaults(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            self.assertEqual(
                memory.load_memory(1),
                {
                    "language": None,
                    "best_contact": None,
                    "preferred_voice": None,
                    "preferred_voice_provider": None,
                    "updated": None,
                    "notes": [],
                },
            )

    def test_save_then_load_roundtrips(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            saved = memory.save_memory(
                1, language="ms", best_contact="weekdays 12:30-14:00",
                new_notes=["prefers WhatsApp over calls"], updated="2026-07-16",
            )
            self.assertEqual(saved["language"], "ms")
            loaded = memory.load_memory(1)
            self.assertEqual(loaded["language"], "ms")
            self.assertEqual(loaded["best_contact"], "weekdays 12:30-14:00")
            self.assertEqual(loaded["notes"], ["prefers WhatsApp over calls"])

    def test_save_merges_and_dedupes_notes(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            memory.save_memory(1, new_notes=["job loss claimed"])
            memory.save_memory(1, new_notes=["job loss claimed", "prefers WhatsApp"])
            loaded = memory.load_memory(1)
            self.assertEqual(loaded["notes"], ["job loss claimed", "prefers WhatsApp"])

    def test_save_caps_notes_at_max(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            for i in range(memory.MAX_NOTES + 5):
                memory.save_memory(1, new_notes=[f"note {i}"])
            loaded = memory.load_memory(1)
            self.assertEqual(len(loaded["notes"]), memory.MAX_NOTES)
            self.assertEqual(loaded["notes"][-1], f"note {memory.MAX_NOTES + 4}")

    def test_language_overwrite_only_when_given(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            memory.save_memory(1, language="ms")
            memory.save_memory(1, new_notes=["some note"])
            loaded = memory.load_memory(1)
            self.assertEqual(loaded["language"], "ms")

    def test_render_parse_roundtrip(self):
        original = {"language": "zh", "best_contact": "after 6pm",
                    "preferred_voice": None, "preferred_voice_provider": None,
                    "updated": "2026-07-16", "notes": ["note one", "note two"]}
        self.assertEqual(memory.parse(memory.render(original)), original)

    def test_preferred_voice_roundtrips(self):
        with mock.patch.object(memory, "MEMORY_DIR", self.dir):
            memory.save_memory(1, preferred_voice="marin", preferred_voice_provider="elevenlabs")
            loaded = memory.load_memory(1)
            self.assertEqual(loaded["preferred_voice"], "marin")
            self.assertEqual(loaded["preferred_voice_provider"], "elevenlabs")
