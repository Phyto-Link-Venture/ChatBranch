# backend/calls/tests/test_playground.py
import json
from unittest import mock
from django.test import TestCase
from calls import playground


class PlaygroundViewTests(TestCase):
    def _post(self, body):
        return self.client.post("/api/voice/playground", data=json.dumps(body),
                                 content_type="application/json")

    def test_missing_contact_id_rejected(self):
        r = self._post({"text": "hi"})
        self.assertEqual(r.status_code, 400)

    @mock.patch("calls.contacts.get_contact", return_value=None)
    def test_unknown_contact_returns_404(self, m_get):
        r = self._post({"contactId": 999})
        self.assertEqual(r.status_code, 404)

    @mock.patch("calls.playground.generate_reply", return_value=("Hi, this is Northwind...", False))
    @mock.patch("calls.memory.load_memory", return_value={"language": None, "best_contact": None, "updated": None, "notes": []})
    @mock.patch("calls.contacts.dial_meta", return_value={"contact_name": "Ah Beng", "language": "en"})
    @mock.patch("calls.contacts.get_contact", return_value={"name": "Ah Beng", "language": "en"})
    def test_happy_path_returns_reply(self, m_get, m_dial_meta, m_load_memory, m_reply):
        r = self._post({"contactId": 1, "history": [], "text": None})
        self.assertEqual(r.status_code, 200)
        data = r.json()
        self.assertEqual(data["reply"], "Hi, this is Northwind...")
        self.assertFalse(data["ended"])
        # fa9e260 added the contact context to the payload (Playground upgrades)
        self.assertEqual(data["contact"]["contact_name"], "Ah Beng")
        m_reply.assert_called_once_with(
            {"contact_name": "Ah Beng", "language": "en"}, [], None,
            memory={"language": None, "best_contact": None, "updated": None, "notes": []},
        )

    @mock.patch("calls.playground.generate_reply", side_effect=RuntimeError("No chat key set"))
    @mock.patch("calls.contacts.dial_meta", return_value={"contact_name": "Ah Beng"})
    @mock.patch("calls.contacts.get_contact", return_value={"name": "Ah Beng"})
    def test_no_key_returns_501(self, m_get, m_dial_meta, m_reply):
        r = self._post({"contactId": 1})
        self.assertEqual(r.status_code, 501)

    @mock.patch("calls.playground.generate_reply", side_effect=Exception("boom"))
    @mock.patch("calls.contacts.dial_meta", return_value={"contact_name": "Ah Beng"})
    @mock.patch("calls.contacts.get_contact", return_value={"name": "Ah Beng"})
    def test_provider_error_returns_502(self, m_get, m_dial_meta, m_reply):
        r = self._post({"contactId": 1})
        self.assertEqual(r.status_code, 502)


class GenerateReplyTests(TestCase):
    @mock.patch("openai.OpenAI")
    @mock.patch.object(playground.agent_config, "summary_config", return_value=({"api_key": "k"}, "gpt-4o-mini"))
    def test_opening_line_uses_directive_not_history(self, m_summary_config, m_openai_cls):
        client = m_openai_cls.return_value
        msg = mock.Mock(content="Good afternoon, this is Northwind.", tool_calls=None)
        client.chat.completions.create.return_value = mock.Mock(choices=[mock.Mock(message=msg)])

        contact = {"contact_name": "Ah Beng", "company_name": "Northwind", "language": "en",
                   "record_type": "order", "record_id": "ORD-1", "record_status": "packed"}
        reply, ended = playground.generate_reply(contact, [], None)

        self.assertEqual(reply, "Good afternoon, this is Northwind.")
        self.assertFalse(ended)
        kwargs = client.chat.completions.create.call_args.kwargs
        messages = kwargs["messages"]
        self.assertEqual(messages[0]["role"], "system")
        self.assertIn("Ah Beng", messages[0]["content"])
        self.assertEqual(messages[1]["role"], "system")
        self.assertIn("Greet", messages[1]["content"])

    @mock.patch("openai.OpenAI")
    @mock.patch.object(playground.agent_config, "summary_config", return_value=({"api_key": "k"}, "gpt-4o-mini"))
    def test_contact_reply_appends_history_and_detects_end_call(self, m_summary_config, m_openai_cls):
        client = m_openai_cls.return_value
        msg = mock.Mock(content="Great, see you then.", tool_calls=[mock.Mock()])
        client.chat.completions.create.return_value = mock.Mock(choices=[mock.Mock(message=msg)])

        contact = {"contact_name": "Ah Beng", "company_name": "Northwind", "language": "en",
                   "record_type": "order", "record_id": "ORD-1", "record_status": "packed"}
        history = [{"role": "assistant", "content": "Hi, can you pay today?"},
                   {"role": "user", "content": "I can pay next Friday."}]
        reply, ended = playground.generate_reply(contact, history, "Yes, next Friday works.")

        self.assertEqual(reply, "Great, see you then.")
        self.assertTrue(ended)
        messages = client.chat.completions.create.call_args.kwargs["messages"]
        self.assertEqual(messages[1:3], history)
        self.assertEqual(messages[-1], {"role": "user", "content": "Yes, next Friday works."})

    @mock.patch.object(playground.agent_config, "summary_config", return_value=(None, None))
    def test_no_key_raises_runtime_error(self, m_summary_config):
        with self.assertRaises(RuntimeError):
            playground.generate_reply({"contact_name": "Ah Beng"}, [], "hi")
