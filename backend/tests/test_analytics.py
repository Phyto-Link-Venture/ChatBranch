"""Analytics endpoint tests — GET /api/analytics/summary.

Covers: the bearer-auth gate, the message funnel (statuses + the thread-level
replied approximation with its 48h window), engagement + records-by-status
aggregation, per-account attribution incl. the null "unattributed" bucket,
call-outcome grouping via the ISO-string window, health-event history, the
daily series, days-param clamping, and empty-DB zeros.

Same fixture pattern as tests/test_contact_channels.py.
"""
from datetime import timedelta

import pytest
from django.contrib.auth.models import User
from django.utils import timezone

from crm.models import (AuditLog, CallRecord, ChannelAccount, Contact,
                        Message, Profile, Record, RecordEvent, Thread)

TOKEN = "test-analytics-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
URL = "/api/analytics/summary"


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="agent", password="collect123")
    Profile.objects.create(user=user, role="agent", token=TOKEN)
    return user


@pytest.fixture
def contact(db):
    return Contact.objects.create(
        name="Ahmad Faiz", phone="+60123456789")


def _get(client, **params):
    return client.get(URL, params, **AUTH)


def _thread(contact=None, key="60123456789", account=None):
    return Thread.objects.create(
        channel="whatsapp", key=key, address=f"+{key}",
        contact=contact, account=account)


def _msg(thread, direction, ts=None, status="sent", private=False, account=None):
    m = Message.objects.create(
        thread=thread, direction=direction,
        source="customer" if direction == "in" else "agent",
        text="x", status=status, private=private, account=account)
    if ts is not None:  # auto_now_add can't be set at create time
        Message.objects.filter(pk=m.pk).update(created_at=ts)
    return m


def _call_iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S")


# ---- auth gate -------------------------------------------------------------------

@pytest.mark.django_db
class TestAuth:
    def test_requires_bearer(self, client, agent_user):
        assert client.get(URL).status_code == 401

    def test_bad_token_rejected(self, client, agent_user):
        r = client.get(URL, HTTP_AUTHORIZATION="Bearer wrong-token")
        assert r.status_code == 401

    def test_any_role_allowed(self, client, agent_user):
        assert _get(client).status_code == 200  # fixture role is plain agent

    def test_get_only(self, client, agent_user):
        assert client.post(URL, **AUTH).status_code == 405


# ---- days param clamping ----------------------------------------------------------

@pytest.mark.django_db
class TestDaysParam:
    def test_default_30(self, client, agent_user):
        assert _get(client).json()["days"] == 30

    def test_capped_at_365(self, client, agent_user):
        assert _get(client, days=9999).json()["days"] == 365

    def test_floor_at_1(self, client, agent_user):
        assert _get(client, days=-5).json()["days"] == 1

    def test_non_numeric_falls_back(self, client, agent_user):
        assert _get(client, days="abc").json()["days"] == 30

    def test_daily_series_spans_window(self, client, agent_user):
        daily = _get(client, days=7).json()["daily"]
        assert len(daily) == 8  # since-day .. today inclusive
        assert daily == sorted(daily, key=lambda d: d["date"])


# ---- message funnel ----------------------------------------------------------------

@pytest.mark.django_db
class TestFunnel:
    def test_status_counts_and_rates(self, client, agent_user, contact):
        t = _thread(contact)
        now = timezone.now()
        _msg(t, "out", now - timedelta(hours=5), status="sent")
        _msg(t, "out", now - timedelta(hours=4), status="delivered")
        _msg(t, "out", now - timedelta(hours=3), status="read")
        _msg(t, "out", now - timedelta(days=45), status="read")  # outside window
        _msg(t, "out", now - timedelta(hours=2), private=True)   # private note
        f = _get(client, days=30).json()["funnel"]
        assert f["sent"] == 3
        assert f["delivered"] == 2  # delivered + read
        assert f["read"] == 1
        assert f["delivered_rate"] == pytest.approx(2 / 3, abs=1e-3)
        assert f["read_rate"] == pytest.approx(1 / 3, abs=1e-3)

    def test_replied_pair_within_48h(self, client, agent_user, contact):
        t = _thread(contact)
        now = timezone.now()
        _msg(t, "out", now - timedelta(hours=30))
        _msg(t, "in", now - timedelta(hours=20))  # reply 10h later
        f = _get(client).json()["funnel"]
        assert f["replied"] == 1
        assert f["reply_rate"] == 1.0

    def test_inbound_after_48h_not_replied(self, client, agent_user, contact):
        t = _thread(contact)
        now = timezone.now()
        _msg(t, "out", now - timedelta(days=5))
        _msg(t, "in", now - timedelta(days=1))  # 4 days later — outside 48h
        assert _get(client).json()["funnel"]["replied"] == 0

    def test_inbound_before_outbound_only_not_replied(self, client, agent_user, contact):
        t = _thread(contact)
        now = timezone.now()
        _msg(t, "in", now - timedelta(hours=6))
        _msg(t, "out", now - timedelta(hours=5))
        assert _get(client).json()["funnel"]["replied"] == 0

    def test_two_way_thread_counts_once(self, client, agent_user, contact):
        # inbound before AND after the first outbound -> replied, 48h waived
        t = _thread(contact)
        now = timezone.now()
        _msg(t, "in", now - timedelta(days=10))
        _msg(t, "out", now - timedelta(days=9))
        _msg(t, "out", now - timedelta(days=8))
        _msg(t, "in", now - timedelta(days=2))
        assert _get(client).json()["funnel"]["replied"] == 1


# ---- engagement + records ----------------------------------------------------------

@pytest.mark.django_db
class TestRecordsByStatus:
    def test_grouped_with_amounts(self, client, agent_user, contact):
        other = Contact.objects.create(name="Mei Lin", phone="+60198765432")
        Record.objects.create(contact=contact, type="order", external_id="O-1",
                              status="open", amount=100)
        Record.objects.create(contact=contact, type="order", external_id="O-2",
                              status="open", amount=200)
        Record.objects.create(contact=other, type="invoice", external_id="I-1",
                              status="paid", amount=300)
        old = Record.objects.create(contact=other, type="order", external_id="O-3",
                                    status="cancelled", amount=50)
        Record.objects.filter(pk=old.pk).update(
            updated_at=timezone.now() - timedelta(days=60))  # outside window

        rows = _get(client, days=30).json()["records_by_status"]
        as_map = {r["status"]: (r["count"], r["amount"]) for r in rows}
        assert as_map == {"open": (2, 300.0), "paid": (1, 300.0)}


@pytest.mark.django_db
class TestEngagement:
    def test_contacted_via_message_and_call(self, client, agent_user, contact):
        msg_contact = contact
        call_contact = Contact.objects.create(name="Raj", phone="+60171112222")
        uncontacted = Contact.objects.create(name="Idle", phone="+60173334444")
        _msg(_thread(msg_contact), "out")
        CallRecord.objects.create(
            call_id="a1", contact=call_contact, phone=call_contact.phone,
            ended_at=_call_iso(timezone.now() - timedelta(days=1)), outcome="no_answer")
        c = _get(client).json()["engagement"]
        assert c["contacted"] == 2
        assert uncontacted.pk  # exists but not counted

    def test_resolved_counts_contacted_resolved_contacts(self, client, agent_user):
        done = Contact.objects.create(name="Done", phone="+60175556666",
                                       status="resolved")
        # resolved but never contacted in-window -> not counted
        Contact.objects.create(name="Old win", phone="+60177778888",
                                status="resolved")
        _msg(_thread(done, key="60175556666"), "out")
        assert _get(client).json()["engagement"]["resolved"] == 1


# ---- per-account volumes -----------------------------------------------------------

@pytest.mark.django_db
class TestAccounts:
    def test_attribution_and_null_bucket(self, client, agent_user, contact):
        main = ChannelAccount.objects.create(
            channel="whatsapp", provider="cloud", name="Main line",
            health_status="healthy")
        spare = ChannelAccount.objects.create(
            channel="whatsapp", provider="baileys", name="Spare QR",
            health_status="degraded")
        t1 = _thread(contact, account=main)
        t2 = _thread(None, key="60755512345", account=spare)
        _msg(t1, "out", account=main)
        _msg(t1, "out", account=main)
        _msg(t1, "in", account=main)
        _msg(t2, "out", account=spare)
        _msg(t2, "in", account=None)  # pre-attribution row -> null bucket
        _msg(t2, "out", account=main, private=True)  # private: excluded from out

        accounts = _get(client).json()["accounts"]
        by_name = {a["name"]: a for a in accounts}
        assert by_name["Main line"] == {
            "account_id": str(main.pk), "name": "Main line", "channel": "whatsapp",
            "provider": "cloud", "health_status": "healthy",
            "outbound": 2, "inbound": 1,
        }
        assert by_name["Spare QR"]["provider"] == "baileys"
        assert by_name["Spare QR"]["health_status"] == "degraded"
        assert by_name["Spare QR"]["outbound"] == 1
        assert by_name["Spare QR"]["inbound"] == 0
        null_bucket = by_name["Unattributed"]
        assert null_bucket["account_id"] is None
        assert null_bucket["provider"] is None
        assert null_bucket["inbound"] == 1
        # sorted by outbound desc
        assert accounts[0]["name"] == "Main line"

    def test_out_of_window_messages_excluded(self, client, agent_user, contact):
        acct = ChannelAccount.objects.create(channel="whatsapp", name="Main")
        t = _thread(contact, account=acct)
        _msg(t, "out", timezone.now() - timedelta(days=60), account=acct)
        assert _get(client, days=30).json()["accounts"] == []


# ---- call outcomes -----------------------------------------------------------------

@pytest.mark.django_db
class TestCallOutcomes:
    def test_grouping_window_and_needs_human(self, client, agent_user):
        now = timezone.now()
        CallRecord.objects.create(call_id="c1", phone="+601", outcome="scheduled",
                                  ended_at=_call_iso(now - timedelta(days=1)))
        CallRecord.objects.create(call_id="c2", phone="+602", outcome="scheduled",
                                  ended_at=_call_iso(now - timedelta(days=2)))
        CallRecord.objects.create(call_id="c3", phone="+603", outcome="dispute",
                                  needs_human=True,
                                  ended_at=_call_iso(now - timedelta(days=3)))
        # no ended_at yet -> windowed on started_at
        CallRecord.objects.create(call_id="c4", phone="+604", outcome="no_answer",
                                  started_at=_call_iso(now - timedelta(hours=2)))
        # outside the window
        CallRecord.objects.create(call_id="c5", phone="+605", outcome="paid",
                                  needs_human=True,
                                  ended_at=_call_iso(now - timedelta(days=90)))

        co = _get(client, days=30).json()["call_outcomes"]
        assert co["total"] == 4
        assert co["needs_human"] == 1
        assert co["outcomes"][0] == {"outcome": "scheduled", "count": 2}
        as_map = {o["outcome"]: o["count"] for o in co["outcomes"]}
        assert as_map == {"scheduled": 2, "dispute": 1, "no_answer": 1}


# ---- account health history --------------------------------------------------------

@pytest.mark.django_db
class TestHealthEvents:
    def test_filtered_windowed_newest_first(self, client, agent_user):
        AuditLog.objects.create(actor="system", action="account.health",
                                target="whatsapp:Main", detail="healthy → degraded — 429")
        AuditLog.objects.create(actor="system", action="account.health",
                                target="whatsapp:Main", detail="degraded → healthy")
        AuditLog.objects.create(actor="admin", action="login")  # other action
        old = AuditLog.objects.create(actor="system", action="account.health",
                                      target="whatsapp:Old", detail="healthy → banned")
        AuditLog.objects.filter(pk=old.pk).update(
            created_at=timezone.now() - timedelta(days=60))

        events = _get(client, days=30).json()["health_events"]
        assert len(events) == 2
        assert events[0]["detail"] == "degraded → healthy"  # newest first
        assert events[1]["target"] == "whatsapp:Main"
        assert {"actor", "target", "detail", "created_at"} == set(events[0])


# ---- daily series ------------------------------------------------------------------

@pytest.mark.django_db
class TestDaily:
    def test_buckets_by_day(self, client, agent_user, contact):
        t = _thread(contact)
        now = timezone.now()
        two_days_ago = now - timedelta(days=2)
        _msg(t, "out", two_days_ago)
        _msg(t, "out", two_days_ago)
        _msg(t, "in", two_days_ago)
        record = Record.objects.create(contact=contact, type="order",
                                       external_id="D-1", status="open")
        RecordEvent.objects.create(record=record, kind="status", at=two_days_ago)
        CallRecord.objects.create(call_id="d1", phone=contact.phone,
                                  outcome="no_answer", ended_at=_call_iso(two_days_ago))

        daily = _get(client, days=7).json()["daily"]
        key = two_days_ago.date().isoformat()
        bucket = next(d for d in daily if d["date"] == key)
        assert bucket == {"date": key, "outbound": 2, "inbound": 1,
                          "calls": 1, "record_events": 1}
        # every other day is zero-filled
        for d in daily:
            if d["date"] != key:
                assert (d["outbound"], d["inbound"], d["calls"],
                        d["record_events"]) == (0, 0, 0, 0)


# ---- empty DB ---------------------------------------------------------------------

@pytest.mark.django_db
class TestEmpty:
    def test_all_zero_shapes(self, client, agent_user):
        data = _get(client).json()
        assert data["funnel"] == {
            "sent": 0, "delivered": 0, "read": 0, "replied": 0,
            "delivered_rate": 0.0, "read_rate": 0.0, "reply_rate": 0.0,
        }
        assert data["engagement"] == {"contacted": 0, "resolved": 0}
        assert data["records_by_status"] == []
        assert data["accounts"] == []
        assert data["call_outcomes"] == {"total": 0, "needs_human": 0, "outcomes": []}
        assert data["health_events"] == []
        assert len(data["daily"]) == 31
        assert all(d["outbound"] == 0 and d["record_events"] == 0
                   for d in data["daily"])
        assert data["sessions"] == {
            "avg_call_seconds": 0, "calls_counted": 0, "chat_sessions": 0,
            "avg_chat_messages": 0, "avg_chat_session_seconds": 0,
        }


# ---- session averages -------------------------------------------------------------

@pytest.mark.django_db
class TestSessions:
    def test_avg_call_length(self, client, agent_user):
        start = timezone.now() - timedelta(hours=3)
        CallRecord.objects.create(
            call_id="c-done", started_at=_call_iso(start),
            ended_at=_call_iso(start + timedelta(seconds=120)))
        # never completed — no ended_at, must be excluded not averaged as zero
        CallRecord.objects.create(call_id="c-half", started_at=_call_iso(start))
        s = _get(client).json()["sessions"]
        assert s["avg_call_seconds"] == 120
        assert s["calls_counted"] == 1

    def test_chat_sessionization_30min_gap(self, client, agent_user, contact):
        t = _thread(contact)
        t0 = timezone.now() - timedelta(hours=6)
        # session 1: three messages inside 10 minutes (duration 600s)
        _msg(t, "out", ts=t0)
        _msg(t, "in", ts=t0 + timedelta(minutes=5))
        _msg(t, "out", ts=t0 + timedelta(minutes=10))
        # >30 min silence -> session 2: a single message (counts, no duration)
        _msg(t, "in", ts=t0 + timedelta(hours=2))
        # a private note in the gap must NOT merge the sessions
        _msg(t, "out", ts=t0 + timedelta(hours=1), private=True)
        s = _get(client).json()["sessions"]
        assert s["chat_sessions"] == 2
        assert s["avg_chat_messages"] == 2.0          # (3 + 1) / 2
        assert s["avg_chat_session_seconds"] == 600   # only the 3-msg session

    def test_sessions_are_per_thread(self, client, agent_user, contact):
        t0 = timezone.now() - timedelta(hours=1)
        a, b = _thread(contact), _thread(None, key="60199998888")
        # interleaved in time but different threads -> two sessions
        _msg(a, "in", ts=t0)
        _msg(b, "in", ts=t0 + timedelta(minutes=1))
        _msg(a, "out", ts=t0 + timedelta(minutes=2))
        s = _get(client).json()["sessions"]
        assert s["chat_sessions"] == 2
