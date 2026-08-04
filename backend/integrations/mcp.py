"""MCP (Model Context Protocol) client — the *outbound* side.

ChatBranch connects to a remote MCP server and calls its tools on behalf of the
chat brain. This module speaks the **Streamable HTTP** transport (JSON-RPC 2.0
over HTTP POST) synchronously with httpx — the same lazy-import, hard-timeout
style as `integrations/actions.py`. We deliberately do NOT pull in the async
`mcp` SDK: it is built around persistent connections and asyncio, which is
awkward inside a synchronous Django reply worker.

Handshake per operation (no persistent connection is kept across web requests):

    initialize  ->  notifications/initialized  ->  tools/list | tools/call

The server may answer either `application/json` (one JSON-RPC object) or
`text/event-stream` (SSE: one `data:` line carrying the object). Both are
handled. A returned `Mcp-Session-Id` header is echoed on later requests.

Everything degrades to an envelope, never an exception into the reply path:

    list_tools(server) -> {"ok": bool, "tools": [...], "detail": str}
    call_tool(server, name, args) -> {"ok": bool, "content": [...], "text": str,
                                      "is_error": bool, "detail": str}
"""
from __future__ import annotations

import json
import logging

logger = logging.getLogger(__name__)

HTTP_TIMEOUT = 20
PROTOCOL_VERSION = "2025-06-18"
_CLIENT_INFO = {"name": "chatbranch", "version": "1.0"}


class _Session:
    """One short-lived MCP conversation with a server over a single httpx client."""

    def __init__(self, server):
        self.server = server
        self.session_id = ""
        self._client = None
        self._id = 0

    # -- transport ---------------------------------------------------------
    def _headers(self) -> dict:
        creds = self.server.credentials or {}
        hdrs = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": PROTOCOL_VERSION,
        }
        hdrs.update({str(k): str(v) for k, v in (self.server.headers or {}).items()})
        auth_header = creds.get("auth_header")
        auth_token = creds.get("auth_token")
        if auth_header and auth_token:
            hdrs[str(auth_header)] = str(auth_token)
        if self.session_id:
            hdrs["Mcp-Session-Id"] = self.session_id
        return hdrs

    def _parse_response(self, resp) -> dict:
        """Return the JSON-RPC response object from a JSON or SSE body."""
        ctype = (resp.headers.get("content-type") or "").lower()
        if "text/event-stream" in ctype:
            obj = None
            for block in resp.text.split("\n\n"):
                data = "".join(
                    line[5:].lstrip() for line in block.splitlines()
                    if line.startswith("data:"))
                if not data:
                    continue
                try:
                    candidate = json.loads(data)
                except ValueError:
                    continue
                # Keep the last object that looks like a response (has id).
                if isinstance(candidate, dict) and "id" in candidate:
                    obj = candidate
            if obj is None:
                raise ValueError("no JSON-RPC message in SSE stream")
            return obj
        return resp.json()

    def _rpc(self, method: str, params: dict | None = None):
        """Send one JSON-RPC request and return its `result`. Raises on error."""
        self._id += 1
        payload = {"jsonrpc": "2.0", "id": self._id, "method": method}
        if params is not None:
            payload["params"] = params
        resp = self._client.post(self.server.url, headers=self._headers(),
                                 content=json.dumps(payload))
        sid = resp.headers.get("mcp-session-id") or resp.headers.get("Mcp-Session-Id")
        if sid:
            self.session_id = sid
        if resp.status_code >= 400:
            raise ValueError(f"HTTP {resp.status_code}")
        obj = self._parse_response(resp)
        if isinstance(obj, dict) and obj.get("error"):
            err = obj["error"] or {}
            raise ValueError(str(err.get("message") or err.get("code") or "MCP error")[:160])
        return (obj or {}).get("result", {})

    def _notify(self, method: str, params: dict | None = None) -> None:
        """Fire-and-forget JSON-RPC notification (no id, no response expected)."""
        payload = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        try:
            self._client.post(self.server.url, headers=self._headers(),
                              content=json.dumps(payload))
        except Exception:
            pass  # notifications are best-effort by protocol

    # -- lifecycle ---------------------------------------------------------
    def open(self):
        import httpx  # lazy — keep the flows/reply import light

        self._client = httpx.Client(timeout=HTTP_TIMEOUT)
        self._rpc("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": _CLIENT_INFO,
        })
        self._notify("notifications/initialized")
        return self

    def close(self):
        if self._client is not None:
            try:
                self._client.close()
            except Exception:
                pass
            self._client = None

    def __enter__(self):
        return self.open()

    def __exit__(self, *exc):
        self.close()
        return False


def list_tools(server) -> dict:
    """Discover the server's tools. Envelope, never raises."""
    try:
        with _Session(server) as s:
            result = s._rpc("tools/list")
        tools = result.get("tools") if isinstance(result, dict) else None
        return {"ok": True, "tools": tools or [], "detail": ""}
    except Exception as e:
        logger.warning("mcp.list_tools failed for %s: %s", getattr(server, "name", "?"),
                       type(e).__name__)
        return {"ok": False, "tools": [], "detail": _reason(e)}


def call_tool(server, name: str, arguments: dict | None = None) -> dict:
    """Invoke one tool. Envelope, never raises."""
    try:
        with _Session(server) as s:
            result = s._rpc("tools/call", {"name": name, "arguments": arguments or {}})
    except Exception as e:
        logger.warning("mcp.call_tool %s@%s failed: %s", name,
                       getattr(server, "name", "?"), type(e).__name__)
        return {"ok": False, "content": [], "text": "", "is_error": True,
                "detail": _reason(e)}
    content = result.get("content") if isinstance(result, dict) else None
    is_error = bool(result.get("isError")) if isinstance(result, dict) else False
    return {"ok": not is_error, "content": content or [], "text": _flatten(content),
            "is_error": is_error, "detail": "",
            "structured": (result or {}).get("structuredContent") if isinstance(result, dict) else None}


def _flatten(content) -> str:
    """Collapse an MCP content array into plain text the model can read back."""
    if not isinstance(content, list):
        return ""
    parts: list[str] = []
    for block in content:
        if not isinstance(block, dict):
            continue
        if block.get("type") == "text" and block.get("text"):
            parts.append(str(block["text"]))
        elif block.get("type") == "resource":
            res = block.get("resource") or {}
            if res.get("text"):
                parts.append(str(res["text"]))
        elif block.get("type") in ("image", "audio"):
            parts.append(f"[{block.get('type')} content omitted]")
    return "\n".join(parts).strip()


def _reason(exc: Exception) -> str:
    msg = str(exc).strip()
    return msg[:160] if msg else type(exc).__name__
