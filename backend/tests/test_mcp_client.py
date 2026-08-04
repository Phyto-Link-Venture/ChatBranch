"""MCP client — the AI can call tools on a registered external MCP server.

Fully offline. The Streamable-HTTP transport is exercised against a fake
httpx.Client; the bridge + dispatch tests mock `integrations.mcp` directly.
Covers:

* integrations/mcp.py — initialize→tools/list→tools/call over JSON and SSE,
  Mcp-Session-Id echo, auth header, JSON-RPC + HTTP error envelopes;
* aicore/mcp_tools.py — namespacing, `confirmed` injection for mutating tools,
  allowlist, read-vs-mutating from annotations, dispatch result mapping, cap;
* aicore/tools.py wiring — MCP tools merge into tool_specs, the SINGLE
  confirmation gate applies to mutating MCP tools, read-only tools run directly,
  the synthetic `confirmed` flag is stripped before it reaches the server, and
  the zero-server sandbox is unchanged.
"""
import json
import types

import pytest

from aicore import mcp_tools
from aicore import tools as tools_mod
from crm.models import Contact
from integrations import mcp as mcp_mod
from integrations.models import McpServer

READ_TOOL = {
    "name": "read_sheet",
    "description": "Read a range from the availability sheet",
    "inputSchema": {"type": "object", "properties": {"range": {"type": "string"}}},
    "annotations": {"readOnlyHint": True},
}
WRITE_TOOL = {
    "name": "book_slot",
    "description": "Book a lesson slot",
    "inputSchema": {"type": "object", "properties": {"start": {"type": "string"}}},
}


@pytest.fixture(autouse=True)
def _clear_cache():
    mcp_mod  # keep import
    mcp_tools._CACHE.clear()
    yield
    mcp_tools._CACHE.clear()


@pytest.fixture
def server(db):
    return McpServer.objects.create(
        name="Google Workspace", url="https://mcp.example/rpc",
        credentials={"auth_header": "Authorization", "auth_token": "Bearer sekret"},
        enabled=True)


@pytest.fixture
def contact(db):
    return Contact.objects.create(name="Aminah", phone="+60123456789", status="active")


# ---- transport: a fake httpx.Client -----------------------------------------

class _FakeResp:
    def __init__(self, *, status_code=200, headers=None, json_body=None, sse=None):
        self.status_code = status_code
        self.headers = headers or {"content-type": "application/json"}
        self._json = json_body
        self.text = sse or ""

    def json(self):
        return self._json


def _install_fake_httpx(monkeypatch, responder):
    """Route httpx.Client(...).post through `responder(method, body, headers)`."""
    import httpx

    posts = []

    class _FakeClient:
        def __init__(self, timeout=None):
            pass

        def post(self, url, headers=None, content=None):
            body = json.loads(content)
            posts.append({"method": body.get("method"), "headers": headers, "body": body})
            return responder(body.get("method"), body, headers)

        def close(self):
            pass

    monkeypatch.setattr(httpx, "Client", _FakeClient)
    return posts


def _ok(body, *, sse=False, session=None):
    """A canned JSON-RPC success response for the given request body."""
    result_by_method = _ok.registry
    result = result_by_method.get(body.get("method"), {})
    envelope = {"jsonrpc": "2.0", "id": body.get("id"), "result": result}
    headers = {"content-type": "text/event-stream" if sse else "application/json"}
    if session:
        headers["mcp-session-id"] = session
    if sse:
        return _FakeResp(headers=headers,
                         sse=f"event: message\ndata: {json.dumps(envelope)}\n\n")
    return _FakeResp(headers=headers, json_body=envelope)


@pytest.mark.django_db
class TestTransport:
    def _responder(self, *, sse=False, session=None):
        _ok.registry = {
            "initialize": {"protocolVersion": "2025-06-18", "serverInfo": {"name": "x"}},
            "tools/list": {"tools": [READ_TOOL, WRITE_TOOL]},
            "tools/call": {"content": [{"type": "text", "text": "A1=free"}], "isError": False},
        }

        def responder(method, body, headers):
            if method == "notifications/initialized":
                return _FakeResp(json_body={})
            return _ok(body, sse=sse, session=session)

        return responder

    def test_list_tools_json(self, server, monkeypatch):
        _install_fake_httpx(monkeypatch, self._responder())
        res = mcp_mod.list_tools(server)
        assert res["ok"] is True
        assert [t["name"] for t in res["tools"]] == ["read_sheet", "book_slot"]

    def test_call_tool_flattens_text(self, server, monkeypatch):
        _install_fake_httpx(monkeypatch, self._responder())
        res = mcp_mod.call_tool(server, "read_sheet", {"range": "A1:B3"})
        assert res["ok"] is True and res["text"] == "A1=free" and res["is_error"] is False

    def test_sse_response_is_parsed(self, server, monkeypatch):
        _install_fake_httpx(monkeypatch, self._responder(sse=True))
        res = mcp_mod.list_tools(server)
        assert res["ok"] is True and len(res["tools"]) == 2

    def test_session_id_is_echoed(self, server, monkeypatch):
        posts = _install_fake_httpx(monkeypatch, self._responder(session="sess-42"))
        mcp_mod.list_tools(server)
        # initialize has no session id yet; the later tools/list carries it back.
        list_post = next(p for p in posts if p["body"].get("method") == "tools/list")
        assert list_post["headers"].get("Mcp-Session-Id") == "sess-42"

    def test_auth_header_applied(self, server, monkeypatch):
        posts = _install_fake_httpx(monkeypatch, self._responder())
        mcp_mod.list_tools(server)
        assert posts[0]["headers"].get("Authorization") == "Bearer sekret"

    def test_jsonrpc_error_is_envelope(self, server, monkeypatch):
        def responder(method, body, headers):
            if method == "notifications/initialized":
                return _FakeResp(json_body={})
            if method == "tools/call":
                return _FakeResp(json_body={"jsonrpc": "2.0", "id": body["id"],
                                            "error": {"code": -32000, "message": "denied"}})
            return _ok(body)
        _ok.registry = {"initialize": {}, "tools/list": {"tools": []}}
        _install_fake_httpx(monkeypatch, responder)
        res = mcp_mod.call_tool(server, "book_slot", {})
        assert res["ok"] is False and "denied" in res["detail"]

    def test_http_error_is_envelope(self, server, monkeypatch):
        def responder(method, body, headers):
            return _FakeResp(status_code=500, json_body={})
        _install_fake_httpx(monkeypatch, responder)
        res = mcp_mod.list_tools(server)
        assert res["ok"] is False and res["tools"] == []


# ---- bridge: aicore/mcp_tools.py --------------------------------------------

def _mock_list(monkeypatch, tools):
    monkeypatch.setattr(mcp_mod, "list_tools",
                        lambda s: {"ok": True, "tools": tools, "detail": ""})


@pytest.mark.django_db
class TestBridge:
    def test_namespacing_and_confirmed_injection(self, server, monkeypatch):
        _mock_list(monkeypatch, [READ_TOOL, WRITE_TOOL])
        specs = {s["function"]["name"]: s for s in mcp_tools.tool_specs()}
        assert set(specs) == {f"mcp__{server.pk}__read_sheet",
                              f"mcp__{server.pk}__book_slot"}
        read_props = specs[f"mcp__{server.pk}__read_sheet"]["function"]["parameters"]["properties"]
        write_props = specs[f"mcp__{server.pk}__book_slot"]["function"]["parameters"]["properties"]
        assert "confirmed" not in read_props           # read-only: no gate flag
        assert "confirmed" in write_props              # mutating: model can confirm

    def test_allowlist_filters(self, server, monkeypatch):
        server.tool_allowlist = ["read_sheet"]
        server.save()
        _mock_list(monkeypatch, [READ_TOOL, WRITE_TOOL])
        names = {s["function"]["name"] for s in mcp_tools.tool_specs()}
        assert names == {f"mcp__{server.pk}__read_sheet"}

    def test_is_mutating_from_annotations(self, server, monkeypatch):
        _mock_list(monkeypatch, [READ_TOOL, WRITE_TOOL])
        assert mcp_tools.is_mutating(f"mcp__{server.pk}__read_sheet") is False
        assert mcp_tools.is_mutating(f"mcp__{server.pk}__book_slot") is True
        assert mcp_tools.is_mutating(f"mcp__{server.pk}__ghost") is True   # unknown → safe
        assert mcp_tools.is_mutating("mcp__9999__x") is True               # no server → safe

    def test_dispatch_maps_result(self, server, monkeypatch):
        _mock_list(monkeypatch, [READ_TOOL])
        monkeypatch.setattr(mcp_mod, "call_tool",
                            lambda s, n, a: {"ok": True, "text": "A1=free", "content": [],
                                             "is_error": False, "detail": "",
                                             "structured": {"rows": 1}})
        out = mcp_tools.dispatch(f"mcp__{server.pk}__read_sheet", {"range": "A1"})
        assert out["ok"] is True and out["result"] == "A1=free" and out["data"] == {"rows": 1}

    def test_dispatch_error(self, server, monkeypatch):
        monkeypatch.setattr(mcp_mod, "call_tool",
                            lambda s, n, a: {"ok": False, "detail": "boom", "text": "",
                                             "content": [], "is_error": True})
        out = mcp_tools.dispatch(f"mcp__{server.pk}__book_slot", {})
        assert out["ok"] is False and "boom" in out["error"]

    def test_disabled_server_offers_nothing(self, server, monkeypatch):
        server.enabled = False
        server.save()
        _mock_list(monkeypatch, [READ_TOOL, WRITE_TOOL])
        assert mcp_tools.tool_specs() == []

    def test_cap_is_enforced(self, server, monkeypatch):
        many = [{"name": f"t{i}", "inputSchema": {"type": "object", "properties": {}},
                 "annotations": {"readOnlyHint": True}} for i in range(mcp_tools._MAX_TOOLS + 5)]
        _mock_list(monkeypatch, many)
        assert len(mcp_tools.tool_specs()) == mcp_tools._MAX_TOOLS


# ---- wiring: aicore/tools.py dispatch + gate --------------------------------

@pytest.mark.django_db
class TestDispatchWiring:
    def _spy_call(self, monkeypatch):
        calls = []

        def call_tool(s, name, args):
            calls.append({"name": name, "args": args})
            return {"ok": True, "text": "done", "content": [], "is_error": False,
                    "detail": "", "structured": None}
        monkeypatch.setattr(mcp_mod, "call_tool", call_tool)
        return calls

    def test_read_tool_runs_without_confirmation(self, server, contact, monkeypatch):
        _mock_list(monkeypatch, [READ_TOOL])
        calls = self._spy_call(monkeypatch)
        out = tools_mod.dispatch(f"mcp__{server.pk}__read_sheet", {"range": "A1"},
                                 contact=contact, channel="whatsapp")
        assert out["ok"] is True and out["result"] == "done"
        assert len(calls) == 1                                  # ran directly

    def test_mutating_tool_needs_one_confirmation(self, server, contact, monkeypatch):
        _mock_list(monkeypatch, [WRITE_TOOL])
        calls = self._spy_call(monkeypatch)
        name = f"mcp__{server.pk}__book_slot"
        # Round 1: no confirmed → gate asks, server is NOT called.
        first = tools_mod.dispatch(name, {"start": "2026-08-10T09:00:00+00:00"},
                                   contact=contact, channel="whatsapp", session="r1")
        assert first["ok"] is False and first["needs_confirmation"] is True
        assert calls == []
        # Round 2 (later session) confirmed → executes, `confirmed` stripped.
        second = tools_mod.dispatch(name,
                                    {"start": "2026-08-10T09:00:00+00:00", "confirmed": True},
                                    contact=contact, channel="whatsapp", session="r2")
        assert second["ok"] is True
        assert len(calls) == 1
        assert calls[0]["args"] == {"start": "2026-08-10T09:00:00+00:00"}   # no confirmed

    def test_same_session_confirm_is_blocked(self, server, contact, monkeypatch):
        _mock_list(monkeypatch, [WRITE_TOOL])
        calls = self._spy_call(monkeypatch)
        name = f"mcp__{server.pk}__book_slot"
        tools_mod.dispatch(name, {"start": "x"}, contact=contact, channel="whatsapp",
                           session="r1")
        out = tools_mod.dispatch(name, {"start": "x", "confirmed": True},
                                 contact=contact, channel="whatsapp", session="r1")
        assert out["needs_confirmation"] is True and calls == []


# ---- sandbox: no server → tool_specs unchanged ------------------------------

@pytest.mark.django_db
class TestSandbox:
    def test_no_server_offers_no_mcp_tools(self, db, monkeypatch):
        monkeypatch.setattr(mcp_mod, "list_tools",
                            lambda s: pytest.fail("must not contact a server in sandbox"))
        assert tools_mod.tool_specs() == []
