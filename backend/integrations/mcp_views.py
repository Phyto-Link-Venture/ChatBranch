"""MCP server registry REST API (under /api/integrations/mcp).

  GET/POST   mcp             -> list / create servers (POST admin+)
  GET/PATCH/DELETE mcp/<id>  -> read / update / remove (mutations admin+)
  POST       mcp/<id>/test   -> live tools/list probe, returns the tool names

Same rules as the Integration endpoints (integrations/views.py): every route
needs a bearer role, mutations need `integrations.manage`, and the auth TOKEN
(`credentials.auth_token`) NEVER leaves the server unmasked — a value still
carrying "••" on the way back in is treated as untouched. The header NAME
(`auth_header`) is not a secret and round-trips in the clear.
"""
from __future__ import annotations

import logging

from django.http import HttpResponseNotAllowed, JsonResponse
from django.utils import timezone
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac

from .models import McpServer
from .views import (_auth_required, _can_manage, _json_body, _mask, _requester,
                    _untouched)

logger = logging.getLogger(__name__)


def _payload(row: McpServer) -> dict:
    creds = row.credentials or {}
    return {
        "id": row.pk,
        "name": row.name,
        "url": row.url,
        "transport": row.transport,
        "enabled": row.enabled,
        "usable": row.usable,
        "tool_allowlist": row.tool_allowlist or [],
        "headers": row.headers or {},
        "auth_header": str(creds.get("auth_header") or ""),
        "auth_token_masked": _mask(str(creds.get("auth_token") or "")),
        "has_auth_token": bool(creds.get("auth_token")),
        "status": row.status,
        "status_detail": row.status_detail,
        "last_ok_at": row.last_ok_at.isoformat() if row.last_ok_at else None,
        "created_by": row.created_by,
        "created_at": row.created_at.isoformat() if row.created_at else None,
    }


def _invalidate_cache(pk: int) -> None:
    """Drop the tool-list cache for a server so a config change takes effect."""
    try:
        from aicore import mcp_tools

        mcp_tools._CACHE.pop(pk, None)
    except Exception:
        pass


def _apply_common(row: McpServer, body: dict) -> list[str]:
    """Set name/url/transport/enabled/allowlist/headers from a body. Returns the
    changed field names. Credentials are handled by the caller."""
    fields: list[str] = []
    if isinstance(body.get("name"), str) and body["name"].strip():
        row.name = body["name"].strip()[:120]
        fields.append("name")
    if isinstance(body.get("url"), str) and body["url"].strip():
        row.url = body["url"].strip()[:500]
        fields.append("url")
    if isinstance(body.get("transport"), str) and body["transport"] in dict(
            McpServer.TRANSPORT_CHOICES):
        row.transport = body["transport"]
        fields.append("transport")
    if isinstance(body.get("enabled"), bool):
        row.enabled = body["enabled"]
        fields.append("enabled")
    if isinstance(body.get("tool_allowlist"), list):
        row.tool_allowlist = [str(t) for t in body["tool_allowlist"] if str(t).strip()]
        fields.append("tool_allowlist")
    if isinstance(body.get("headers"), dict):
        row.headers = {str(k): str(v) for k, v in body["headers"].items()}
        fields.append("headers")
    return fields


@csrf_exempt
def servers(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"servers": [_payload(r) for r in McpServer.objects.all()]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage MCP servers."},
                            status=403)
    body = _json_body(request)
    if not str(body.get("url") or "").strip():
        return JsonResponse({"detail": "url is required."}, status=400)
    row = McpServer(created_by=_requester(request),
                    tenant=rbac.tenant_from_request(request))
    _apply_common(row, {**body, "name": body.get("name") or "MCP server"})
    creds = {}
    if isinstance(body.get("auth_header"), str) and body["auth_header"].strip():
        creds["auth_header"] = body["auth_header"].strip()
    token = body.get("auth_token")
    if isinstance(token, str) and token.strip() and not _untouched(token):
        creds["auth_token"] = token.strip()
    row.credentials = creds
    row.save()
    rbac.audit(request, "mcp.create", target=row.name)
    rbac.notify(("superadmin", "admin"), "system", f"MCP server added: {row.name}",
                f"{row.url} — by {_requester(request)}", "/settings/integrations")
    return JsonResponse(_payload(row))


@csrf_exempt
def server_detail(request, server_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    row = McpServer.objects.filter(pk=server_id).first()
    if row is None:
        return JsonResponse({"detail": "MCP server not found."}, status=404)
    if request.method == "GET":
        return JsonResponse(_payload(row))
    if request.method not in ("PATCH", "POST", "DELETE"):
        return HttpResponseNotAllowed(["GET", "PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage MCP servers."},
                            status=403)
    if request.method == "DELETE":
        name = row.name
        row.delete()
        _invalidate_cache(server_id)
        rbac.audit(request, "mcp.delete", target=name)
        return JsonResponse({"ok": True})

    body = _json_body(request)
    fields = _apply_common(row, body)
    creds = dict(row.credentials or {})
    if isinstance(body.get("auth_header"), str):
        creds["auth_header"] = body["auth_header"].strip()
        fields.append("credentials")
    if "auth_token" in body:
        token = body.get("auth_token")
        if not _untouched(token):
            creds["auth_token"] = str(token or "").strip()
            fields.append("credentials")
    row.credentials = creds
    if fields:
        row.status = "unknown"          # config changed — re-probe to re-verify
        row.save()
        _invalidate_cache(server_id)
    rbac.audit(request, "mcp.update", target=row.name,
               detail=f"fields: {sorted(set(fields))}")
    return JsonResponse(_payload(row))


@csrf_exempt
def server_test(request, server_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    row = McpServer.objects.filter(pk=server_id).first()
    if row is None:
        return JsonResponse({"detail": "MCP server not found."}, status=404)
    if not row.usable:
        return JsonResponse({"ok": False, "detail": "Server is disabled or has no URL.",
                             "server": _payload(row)}, status=400)
    from . import mcp

    _invalidate_cache(server_id)
    res = mcp.list_tools(row)
    now = timezone.now()
    if res["ok"]:
        tools = [str(t.get("name")) for t in res["tools"] if isinstance(t, dict)]
        row.status, row.status_detail, row.last_ok_at = "healthy", f"{len(tools)} tools", now
        row.status_changed_at = now
        row.save(update_fields=["status", "status_detail", "last_ok_at",
                                "status_changed_at", "updated_at"])
        return JsonResponse({"ok": True, "tools": tools, "server": _payload(row)})
    row.status, row.status_detail = "disconnected", (res["detail"] or "unreachable")[:200]
    row.status_changed_at = now
    row.save(update_fields=["status", "status_detail", "status_changed_at", "updated_at"])
    return JsonResponse({"ok": False, "detail": res["detail"], "server": _payload(row)},
                        status=502)
