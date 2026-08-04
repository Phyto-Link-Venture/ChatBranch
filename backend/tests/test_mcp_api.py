"""MCP server registry REST API (/api/integrations/mcp).

Mirrors test_integrations.py: bearer-role gating, mutations admin+, and the
auth token never returning unmasked. The live-probe endpoint is exercised with
`integrations.mcp.list_tools` mocked — no network.
"""
import json

import pytest
from django.contrib.auth.models import User

from crm.models import Profile
from integrations import mcp as mcp_mod
from integrations.models import McpServer

TOKEN_ADMIN = "mcp-admin-token"
TOKEN_AGENT = "mcp-agent-token"
ADMIN = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_ADMIN}"}
AGENT = {"HTTP_AUTHORIZATION": f"Bearer {TOKEN_AGENT}"}


@pytest.fixture
def admin(db):
    u = User.objects.create_user(username="mcp_admin", password="x")
    Profile.objects.create(user=u, role="admin", token=TOKEN_ADMIN)
    return u


@pytest.fixture
def agent(db):
    u = User.objects.create_user(username="mcp_agent", password="x")
    Profile.objects.create(user=u, role="agent", token=TOKEN_AGENT)
    return u


def _create(client, **over):
    body = {"name": "Workspace", "url": "https://mcp.example/rpc",
            "auth_header": "Authorization", "auth_token": "Bearer sekret-123"}
    body.update(over)
    return client.post("/api/integrations/mcp", data=json.dumps(body),
                       content_type="application/json", **ADMIN)


@pytest.mark.django_db
class TestMcpApi:
    def test_requires_auth(self, client, admin):
        # `admin` exists so we are past pre-seed bootstrap (X-Role no longer works);
        # a request with no bearer token must be rejected.
        assert client.get("/api/integrations/mcp").status_code == 401

    def test_agent_cannot_create(self, client, agent):
        r = client.post("/api/integrations/mcp",
                        data=json.dumps({"url": "https://x/rpc"}),
                        content_type="application/json", **AGENT)
        assert r.status_code == 403
        assert McpServer.objects.count() == 0

    def test_create_requires_url(self, client, admin):
        r = client.post("/api/integrations/mcp", data=json.dumps({"name": "x"}),
                        content_type="application/json", **ADMIN)
        assert r.status_code == 400

    def test_create_and_masking(self, client, admin):
        r = _create(client)
        assert r.status_code == 200
        data = r.json()
        assert data["url"] == "https://mcp.example/rpc"
        assert data["auth_header"] == "Authorization"      # header name is not secret
        assert data["has_auth_token"] is True
        assert "••" in data["auth_token_masked"]
        assert "sekret" not in json.dumps(data)            # token never leaks
        # stored for real
        row = McpServer.objects.get(pk=data["id"])
        assert row.credentials["auth_token"] == "Bearer sekret-123"

    def test_masked_roundtrip_keeps_secret(self, client, admin):
        row_id = _create(client).json()["id"]
        masked = McpServer.objects.get(pk=row_id)
        r = client.patch(f"/api/integrations/mcp/{row_id}",
                         data=json.dumps({"name": "Renamed",
                                          "auth_token": "••••-123"}),
                         content_type="application/json", **ADMIN)
        assert r.status_code == 200 and r.json()["name"] == "Renamed"
        assert McpServer.objects.get(pk=row_id).credentials["auth_token"] == "Bearer sekret-123"

    def test_patch_enabled_and_allowlist(self, client, admin):
        row_id = _create(client).json()["id"]
        r = client.patch(f"/api/integrations/mcp/{row_id}",
                         data=json.dumps({"enabled": False,
                                          "tool_allowlist": ["read_sheet"]}),
                         content_type="application/json", **ADMIN)
        assert r.status_code == 200
        row = McpServer.objects.get(pk=row_id)
        assert row.enabled is False and row.tool_allowlist == ["read_sheet"]

    def test_delete(self, client, admin):
        row_id = _create(client).json()["id"]
        r = client.delete(f"/api/integrations/mcp/{row_id}", **ADMIN)
        assert r.status_code == 200 and McpServer.objects.count() == 0

    def test_probe_success(self, client, admin, monkeypatch):
        row_id = _create(client).json()["id"]
        monkeypatch.setattr(mcp_mod, "list_tools",
                            lambda s: {"ok": True, "detail": "",
                                       "tools": [{"name": "read_sheet"}, {"name": "book"}]})
        r = client.post(f"/api/integrations/mcp/{row_id}/test", **ADMIN)
        assert r.status_code == 200
        assert r.json()["tools"] == ["read_sheet", "book"]
        assert McpServer.objects.get(pk=row_id).status == "healthy"

    def test_probe_failure_marks_disconnected(self, client, admin, monkeypatch):
        row_id = _create(client).json()["id"]
        monkeypatch.setattr(mcp_mod, "list_tools",
                            lambda s: {"ok": False, "detail": "timeout", "tools": []})
        r = client.post(f"/api/integrations/mcp/{row_id}/test", **ADMIN)
        assert r.status_code == 502
        assert McpServer.objects.get(pk=row_id).status == "disconnected"
