"""Bridge between registered MCP servers and the chat brain's tool loop.

Turns the tools advertised by every enabled `integrations.McpServer` into
OpenAI-format tool schemas the model can call, and routes those calls back out
through `integrations/mcp.py`. Wired into `aicore/tools.py` so MCP tools sit
next to the built-in ones and share the SAME single-confirmation gate.

Exposed tool names are namespaced `mcp__<server_pk>__<tool>` so a call routes
unambiguously back to its server. Mutating vs read is taken from the MCP tool's
`annotations.readOnlyHint` — anything not explicitly read-only is treated as
mutating (confirmation required), the safe default for a customer-facing bot.

Every entry point is exception-safe: a server error skips that server and the
reply proceeds. Zero enabled servers -> `tool_specs()` returns [], so the reply
path is byte-for-byte the no-tools behaviour (the sandbox contract).
"""
from __future__ import annotations

import logging
import time

logger = logging.getLogger(__name__)

_PREFIX = "mcp__"
_CACHE: dict[int, tuple[float, list]] = {}  # server_pk -> (expires_at, tools)
_CACHE_TTL_S = 300
_MAX_TOOLS = 32          # bound the prompt: cap total MCP tools offered
_RESULT_MAX = 4000       # truncate a tool result before feeding it back


def _enabled_servers() -> list:
    """The usable MCP servers, or [] on any error (never raises)."""
    try:
        from integrations.models import McpServer

        return [s for s in McpServer.objects.all() if s.usable]
    except Exception:
        logger.exception("mcp_tools: server lookup failed")
        return []


def _list_cached(server) -> list:
    """The server's tools, memoised for _CACHE_TTL_S. [] on failure."""
    now = time.monotonic()
    hit = _CACHE.get(server.pk)
    if hit and hit[0] > now:
        return hit[1]
    from integrations import mcp

    res = mcp.list_tools(server)
    tools = res.get("tools") or [] if res.get("ok") else []
    if res.get("ok"):
        _CACHE[server.pk] = (now + _CACHE_TTL_S, tools)
    return tools


def _allowed(server, tool_name: str) -> bool:
    allow = server.tool_allowlist or []
    return (not allow) or (tool_name in allow)


def _exposed_name(server, tool_name: str) -> str:
    return f"{_PREFIX}{server.pk}__{tool_name}"


def _resolve(exposed: str):
    """(server, real_tool_name) for a namespaced name, or (None, '')."""
    if not exposed.startswith(_PREFIX):
        return None, ""
    rest = exposed[len(_PREFIX):]
    pk_str, sep, tool_name = rest.partition("__")
    if not sep or not pk_str.isdigit():
        return None, ""
    try:
        from integrations.models import McpServer

        server = McpServer.objects.filter(pk=int(pk_str)).first()
    except Exception:
        return None, ""
    if server is None or not server.usable:
        return None, ""
    return server, tool_name


def _find_tool(server, tool_name: str) -> dict | None:
    for t in _list_cached(server):
        if isinstance(t, dict) and t.get("name") == tool_name:
            return t
    return None


def is_mcp_tool(name: str) -> bool:
    return name.startswith(_PREFIX)


def is_mutating(name: str) -> bool:
    """True unless the MCP tool is explicitly annotated read-only. Fail-safe:
    an unresolved/unknown tool is treated as mutating (needs confirmation)."""
    server, tool_name = _resolve(name)
    if server is None:
        return True
    tool = _find_tool(server, tool_name)
    ann = (tool or {}).get("annotations") or {}
    return not bool(ann.get("readOnlyHint"))


_CONFIRM_DESC = (
    "Single-confirmation flag. Omit (or false) on the first call — nothing "
    "happens and you will be told to ask the customer. Set true ONLY after the "
    "customer explicitly agreed in a LATER message, keeping the other arguments "
    "identical."
)


def _augment(params: dict, mutating: bool) -> dict:
    """Inject a synthetic `confirmed` boolean into a mutating tool's schema so
    the model can signal the customer's go-ahead (MCP servers don't declare it).
    Stripped again before the call reaches the server (see tools._dispatch_mcp)."""
    if not mutating:
        return params
    out = dict(params or {})
    out["type"] = out.get("type", "object")
    props = dict(out.get("properties") or {})
    props.setdefault("confirmed", {"type": "boolean", "description": _CONFIRM_DESC})
    out["properties"] = props
    return out


def tool_specs() -> list[dict]:
    """OpenAI-format schemas for every enabled server's allowlisted tools."""
    specs: list[dict] = []
    for server in _enabled_servers():
        try:
            for t in _list_cached(server):
                if not isinstance(t, dict) or not t.get("name"):
                    continue
                if not _allowed(server, t["name"]):
                    continue
                params = t.get("inputSchema") or {"type": "object", "properties": {}}
                ann = t.get("annotations") or {}
                params = _augment(params, not bool(ann.get("readOnlyHint")))
                desc = f"[via {server.name}] " + str(t.get("description") or t["name"])
                specs.append({
                    "type": "function",
                    "function": {
                        "name": _exposed_name(server, t["name"]),
                        "description": desc[:1024],
                        "parameters": params,
                    },
                })
                if len(specs) >= _MAX_TOOLS:
                    logger.warning("mcp_tools: hit _MAX_TOOLS (%d) — some tools dropped",
                                   _MAX_TOOLS)
                    return specs
        except Exception:
            logger.exception("mcp_tools: spec build failed for %s", server.name)
    return specs


def dispatch(name: str, args: dict | None = None) -> dict:
    """Invoke a namespaced MCP tool and return a compact result dict.
    Never raises — the confirmation gate is applied by the caller (tools.py)."""
    server, tool_name = _resolve(name)
    if server is None:
        return {"ok": False, "error": f"unknown MCP tool {name!r}"}
    from integrations import mcp

    res = mcp.call_tool(server, tool_name, args or {})
    if not res.get("ok"):
        return {"ok": False,
                "error": res.get("detail") or "the tool reported an error",
                "hint": "tell the customer you could not complete that and offer "
                        "to have the team follow up"}
    text = (res.get("text") or "")[:_RESULT_MAX]
    out: dict = {"ok": True, "result": text}
    if res.get("structured") is not None:
        out["data"] = res["structured"]
    return out
