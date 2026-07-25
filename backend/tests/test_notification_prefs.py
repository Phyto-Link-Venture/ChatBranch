"""Settings → Notifications: the global bell mute toggles.

Covers the GET/PUT /api/auth/notification-prefs endpoint (role gates,
persistence into crm.Setting rows) and the rbac.notify category gate
(kind-derived defaults, explicit category override, fail-open defaults).
Same fixture pattern as tests/test_health_routing.py.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm import auth as rbac
from crm.models import Notification, Profile, Setting

TOKEN = "test-super-token"
AUTH = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN}"}
ADMIN_TOKEN = "test-admin-token"
ADMIN_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {ADMIN_TOKEN}"}
AGENT_TOKEN = "test-agent-token"
AGENT_AUTH = {"HTTP_AUTHORIZATION": f"Bearer {AGENT_TOKEN}"}

URL = "/api/auth/notification-prefs"


@pytest.fixture
def superadmin(db):
    user = User.objects.create_user(username="super", password="super123")
    Profile.objects.create(user=user, role="superadmin", token=TOKEN)
    return user


@pytest.fixture
def admin_user(db):
    user = User.objects.create_user(username="boss", password="admin123")
    Profile.objects.create(user=user, role="admin", token=ADMIN_TOKEN)
    return user


@pytest.fixture
def agent_user(db):
    user = User.objects.create_user(username="col", password="collect123")
    Profile.objects.create(user=user, role="agent", token=AGENT_TOKEN)
    return user


def _put(client, body, auth):
    return client.put(URL, data=json.dumps(body), content_type="application/json", **auth)


class TestPrefsEndpoint:
    def test_defaults_all_on(self, client, superadmin):
        res = client.get(URL, **AUTH)
        assert res.status_code == 200
        assert res.json() == {c: True for c in rbac.NOTIF_CATEGORIES}

    def test_requires_auth(self, client, superadmin):
        assert client.get(URL).status_code == 401

    def test_put_persists_setting_rows(self, client, admin_user):
        res = _put(client, {"messages": False}, ADMIN_AUTH)
        assert res.status_code == 200
        assert res.json()["messages"] is False
        assert Setting.objects.get(key="notif_messages").value == "false"
        assert client.get(URL, **ADMIN_AUTH).json()["messages"] is False
        # toggling back updates the same row, no duplicates
        assert _put(client, {"messages": True}, ADMIN_AUTH).json()["messages"] is True
        assert Setting.objects.filter(key="notif_messages").count() == 1

    def test_agent_reads_but_cannot_change(self, client, agent_user):
        assert client.get(URL, **AGENT_AUTH).status_code == 200
        assert _put(client, {"messages": False}, AGENT_AUTH).status_code == 403
        assert not Setting.objects.filter(key="notif_messages").exists()

    def test_rejects_non_bool(self, client, admin_user):
        assert _put(client, {"messages": "nope"}, ADMIN_AUTH).status_code == 400

    def test_unknown_keys_ignored(self, client, admin_user):
        assert _put(client, {"bogus": False}, ADMIN_AUTH).status_code == 200
        assert not Setting.objects.filter(key="notif_bogus").exists()


class TestNotifyGating:
    def test_default_enabled(self, superadmin):
        rbac.notify(("superadmin",), "message", "hi")
        assert Notification.objects.count() == 1

    def test_muted_messages_still_allows_admin(self, superadmin):
        Setting.objects.create(key="notif_messages", value="false")
        rbac.notify(("superadmin",), "message", "hi")
        assert Notification.objects.count() == 0
        # kind=system derives category "admin" — unaffected by the messages mute
        rbac.notify(("superadmin",), "system", "cfg changed")
        assert Notification.objects.count() == 1

    def test_explicit_category_overrides_kind_default(self, superadmin):
        Setting.objects.create(key="notif_account_health", value="false")
        rbac.notify(("superadmin",), "system", "account down", category="account_health")
        assert Notification.objects.count() == 0
        # same kind, no category → admin group → still delivered
        rbac.notify(("superadmin",), "system", "user created")
        assert Notification.objects.count() == 1

    def test_unmute_restores_delivery(self, superadmin):
        row = Setting.objects.create(key="notif_flows", value="false")
        rbac.notify(("superadmin",), "system", "push", category="flows")
        assert Notification.objects.count() == 0
        row.value = "true"
        row.save()
        rbac.notify(("superadmin",), "system", "push", category="flows")
        assert Notification.objects.count() == 1
