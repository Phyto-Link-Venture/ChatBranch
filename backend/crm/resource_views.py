"""Resource (service-provider) + binding REST API (under /api/).

  GET/POST   resources                       -> list / create providers
  GET/PATCH/DELETE resources/<id>            -> read / update / remove
  GET/POST   resources/<id>/links            -> list / create teacher↔student bindings
  DELETE     resources/<id>/links/<link_id>  -> remove a binding

Auth mirrors the rest of the app: any authenticated role with `contacts.view`
reads (an agent needs to see which teacher a student has); `contacts.manage`
(admin+) mutates. Rows are tenant-scoped — superadmin sees all, everyone else
sees their own tenant's providers (plus tenant-less legacy rows).
"""
from __future__ import annotations

import json
import logging

from django.http import HttpResponseNotAllowed, JsonResponse
from django.utils.dateparse import parse_date
from django.views.decorators.csrf import csrf_exempt

from . import auth as rbac
from .models import Contact, Resource, ResourceLink
from .permissions_store import has_capability

logger = logging.getLogger(__name__)


def _json_body(request) -> dict:
    try:
        body = json.loads(request.body or b"{}")
    except ValueError:
        return {}
    return body if isinstance(body, dict) else {}


def _auth_required(request) -> JsonResponse | None:
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _can_view(request) -> bool:
    return has_capability(rbac.role_from_request(request), "contacts.view")


def _can_manage(request) -> bool:
    return has_capability(rbac.role_from_request(request), "contacts.manage")


def _scope(qs, request):
    """Tenant-scope a Resource queryset: superadmin sees all; others see their
    tenant's rows plus tenant-less legacy rows."""
    if rbac.role_from_request(request) == "superadmin":
        return qs
    tenant = rbac.tenant_from_request(request)
    if tenant is None:
        return qs
    return qs.filter(tenant__in=[tenant, None])


def _link_payload(link: ResourceLink) -> dict:
    return {
        "id": link.pk,
        "contact_id": link.contact_id,
        "contact_name": link.contact.name if link.contact_id else "",
        "relationship": link.relationship,
        "active": link.active,
        "since": link.since.isoformat() if link.since else None,
        "created_at": link.created_at.isoformat() if link.created_at else None,
    }


def _payload(row: Resource, *, with_links: bool = False) -> dict:
    out = {
        "id": row.pk,
        "name": row.name,
        "external_id": row.external_id,
        "email": row.email,
        "phone": row.phone,
        "active": row.active,
        "config": row.config or {},
        "attributes": row.attributes or {},
        "tenant_id": row.tenant_id,
        "links_count": row.links.count(),
        "created_at": row.created_at.isoformat() if row.created_at else None,
        "updated_at": row.updated_at.isoformat() if row.updated_at else None,
    }
    if with_links:
        out["links"] = [_link_payload(ln) for ln in
                        row.links.select_related("contact").all()]
    return out


@csrf_exempt
def resources(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        if not _can_view(request):
            return JsonResponse({"detail": "Not permitted."}, status=403)
        qs = _scope(Resource.objects.all(), request)
        if str(request.GET.get("active") or "") == "true":
            qs = qs.filter(active=True)
        return JsonResponse({"resources": [_payload(r) for r in qs]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage providers."},
                            status=403)
    body = _json_body(request)
    name = str(body.get("name") or "").strip()
    if not name:
        return JsonResponse({"detail": "name is required."}, status=400)
    row = Resource(name=name[:120], tenant=rbac.tenant_from_request(request))
    _apply(row, body)
    row.save()
    rbac.audit(request, "resource.create", target=row.name)
    return JsonResponse(_payload(row))


def _apply(row: Resource, body: dict) -> None:
    if isinstance(body.get("name"), str) and body["name"].strip():
        row.name = body["name"].strip()[:120]
    if isinstance(body.get("external_id"), str):
        row.external_id = body["external_id"].strip()[:64]
    if isinstance(body.get("email"), str):
        row.email = body["email"].strip()
    if isinstance(body.get("phone"), str):
        row.phone = body["phone"].strip()[:32]
    if isinstance(body.get("active"), bool):
        row.active = body["active"]
    if isinstance(body.get("config"), dict):
        row.config = body["config"]
    if isinstance(body.get("attributes"), dict):
        row.attributes = body["attributes"]


@csrf_exempt
def resource_detail(request, resource_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    row = _scope(Resource.objects.all(), request).filter(pk=resource_id).first()
    if row is None:
        return JsonResponse({"detail": "Provider not found."}, status=404)
    if request.method == "GET":
        if not _can_view(request):
            return JsonResponse({"detail": "Not permitted."}, status=403)
        return JsonResponse(_payload(row, with_links=True))
    if request.method not in ("PATCH", "POST", "DELETE"):
        return HttpResponseNotAllowed(["GET", "PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage providers."},
                            status=403)
    if request.method == "DELETE":
        name = row.name
        row.delete()
        rbac.audit(request, "resource.delete", target=name)
        return JsonResponse({"ok": True})
    _apply(row, _json_body(request))
    row.save()
    rbac.audit(request, "resource.update", target=row.name)
    return JsonResponse(_payload(row, with_links=True))


@csrf_exempt
def resource_links(request, resource_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    row = _scope(Resource.objects.all(), request).filter(pk=resource_id).first()
    if row is None:
        return JsonResponse({"detail": "Provider not found."}, status=404)
    if request.method == "GET":
        if not _can_view(request):
            return JsonResponse({"detail": "Not permitted."}, status=403)
        return JsonResponse({"links": [_link_payload(ln) for ln in
                                       row.links.select_related("contact").all()]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage bindings."},
                            status=403)
    body = _json_body(request)
    contact_id = body.get("contact_id")
    contact = Contact.objects.filter(pk=contact_id).first() if contact_id else None
    if contact is None:
        return JsonResponse({"detail": "contact_id must reference an existing contact."},
                            status=400)
    from . import resources as res_mod

    since = parse_date(str(body.get("since") or "")) if body.get("since") else None
    link = res_mod.link(row, contact,
                        relationship=str(body.get("relationship") or ""),
                        since=since,
                        active=body.get("active", True) is not False)
    rbac.audit(request, "resource.link", target=f"{row.name} - {contact.name}")
    return JsonResponse(_link_payload(link))


@csrf_exempt
def resource_link_delete(request, resource_id: int, link_id: int):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "DELETE":
        return HttpResponseNotAllowed(["DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage bindings."},
                            status=403)
    row = _scope(Resource.objects.all(), request).filter(pk=resource_id).first()
    if row is None:
        return JsonResponse({"detail": "Provider not found."}, status=404)
    link = ResourceLink.objects.filter(pk=link_id, resource=row).first()
    if link is None:
        return JsonResponse({"detail": "Binding not found."}, status=404)
    contact_name = link.contact.name if link.contact_id else "?"
    link.delete()
    rbac.audit(request, "resource.unlink", target=f"{row.name} - {contact_name}")
    return JsonResponse({"ok": True})
