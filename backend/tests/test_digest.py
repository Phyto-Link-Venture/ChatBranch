"""Daily ops digest — build, render, deliver, and the once-a-day ticker gate.

Fully offline. The email path is mocked; the in-app path writes real
crm.Notification rows to every admin/superadmin.
"""
from datetime import timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from crm import digest
from crm.models import Contact, Message, Notification, Profile, Record, Setting, Thread


@pytest.fixture
def admins(db):
    out = []
    for name, role in [("dg_super", "superadmin"), ("dg_admin", "admin"), ("dg_agent", "agent")]:
        u = User.objects.create_user(username=name, password="x", email=f"{name}@studio.test")
        Profile.objects.create(user=u, role=role, token=f"tok-{name}")
        out.append(u)
    return out


_seq = [0]


def _thread():
    _seq[0] += 1
    return Thread.objects.create(channel="whatsapp", key=f"6012{_seq[0]}", name="Aminah")


def _msg(direction, source="customer", when=None):
    m = Message.objects.create(thread=_thread(), direction=direction, source=source,
                               text="hi", status="sent")
    if when is not None:
        Message.objects.filter(pk=m.pk).update(created_at=when)
    return m


@pytest.mark.django_db
class TestBuild:
    def test_counts_messages_records_contacts(self):
        until = timezone.now()
        since = until - timedelta(hours=24)
        mid = until - timedelta(hours=2)
        _msg("in", "customer", mid)
        _msg("out", "ai", mid)
        _msg("out", "agent", mid)
        _msg("in", "customer", until - timedelta(days=3))  # outside window — excluded
        c1 = Contact.objects.create(name="New", phone="+60111", status="active")
        c2 = Contact.objects.create(name="C2", phone="+60222")
        Contact.objects.filter(pk__in=[c1.pk, c2.pk]).update(created_at=mid)
        rec = Record.objects.create(contact=c2, type="booking", status="scheduled")
        Record.objects.filter(pk=rec.pk).update(created_at=mid)

        d = digest.build_digest(since, until)
        assert d["messages"] == {"inbound": 1, "outbound": 2, "ai": 1}
        assert d["new_bookings"] == 1
        assert d["new_contacts"] == 2

    def test_private_notes_excluded(self):
        until = timezone.now()
        since = until - timedelta(hours=24)
        m = Message.objects.create(thread=_thread(), direction="out", source="agent",
                                   text="internal", status="sent", private=True)
        Message.objects.filter(pk=m.pk).update(created_at=until - timedelta(hours=1))
        d = digest.build_digest(since, until)
        assert d["messages"]["outbound"] == 0

    def test_render_text_has_headline(self):
        d = digest.build_digest(timezone.now() - timedelta(hours=24), timezone.now())
        body = digest.render_text(d)
        assert "Daily summary" in body and "Messages:" in body


@pytest.mark.django_db
class TestDeliver:
    def test_notifies_every_admin_and_superadmin(self, admins):
        d = digest.build_digest(timezone.now() - timedelta(hours=24), timezone.now())
        result = digest.deliver(d)
        assert result["notified"] == 2  # admin + superadmin, NOT the agent
        titles = Notification.objects.values_list("title", flat=True)
        assert all("Daily summary" in t for t in titles)
        assert Notification.objects.count() == 2  # agent gets nothing

    def test_email_path_is_used_when_asked(self, admins, monkeypatch):
        sent = []
        import messaging.email_client as ec
        monkeypatch.setattr(ec, "send_email",
                            lambda to, body, subject=None, settings=None: sent.append(to))
        d = digest.build_digest(timezone.now() - timedelta(hours=24), timezone.now())
        result = digest.deliver(d, email=True)
        assert result["emailed"] == 2
        assert set(sent) == {"dg_super@studio.test", "dg_admin@studio.test"}

    def test_email_failure_never_raises(self, admins, monkeypatch):
        import messaging.email_client as ec

        def boom(*a, **k):
            raise RuntimeError("SMTP down")
        monkeypatch.setattr(ec, "send_email", boom)
        d = digest.build_digest(timezone.now() - timedelta(hours=24), timezone.now())
        result = digest.deliver(d, email=True)   # must not raise
        assert result["emailed"] == 0 and result["notified"] == 2


@pytest.mark.django_db
class TestMaybeSend:
    def test_holds_before_the_hour(self, admins):
        Setting.objects.create(key="digest_hour", value="8")
        now = timezone.make_aware(timezone.datetime(2026, 8, 4, 6, 0))  # 06:00 < 08:00
        assert digest.maybe_send(now) is False
        assert Notification.objects.count() == 0

    def test_sends_once_after_the_hour(self, admins):
        Setting.objects.create(key="digest_hour", value="8")
        now = timezone.make_aware(timezone.datetime(2026, 8, 4, 9, 0))
        assert digest.maybe_send(now) is True
        assert Notification.objects.count() == 2
        assert Setting.objects.get(key="digest_last_sent").value == "2026-08-04"
        # second call the same day is a no-op
        assert digest.maybe_send(now) is False
        assert Notification.objects.count() == 2
