"""Unified call-logs endpoint (SIP + WhatsApp) behind the standard role gate."""
import pytest
from django.contrib.auth.models import User
from django.test import Client

from crm.models import Profile


@pytest.mark.django_db
def test_call_logs_requires_auth_once_profiles_exist():
    # with zero Profile rows the pre-seed bootstrap grants admin (documented),
    # so create one to make the gate real
    u = User.objects.create_user(username="a1", password="x")
    Profile.objects.create(user=u, role="admin", token="tok_logs")
    assert Client().get("/api/call-logs").status_code == 401
    assert Client().get("/api/call-logs",
                        HTTP_AUTHORIZATION="Bearer tok_logs").status_code == 200


@pytest.mark.django_db
def test_call_logs_lists_and_shapes():
    r = Client().get("/api/call-logs", HTTP_X_ROLE="admin")  # pre-seed bootstrap header
    assert r.status_code == 200
    logs = r.json()["logs"]
    assert isinstance(logs, list)
    for row in logs[:10]:  # shape check on whatever local data exists
        assert row["type"] in ("ai-call", "wa-call")
        assert set(row) >= {"id", "contact", "phone", "ts", "outcome", "transcript"}
