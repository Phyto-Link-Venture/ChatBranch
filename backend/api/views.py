"""v1 API views: identity, RBAC-scoped resources, and user/brand management.

Access rules (see api/rbac.py):
- superadmin : everything, all tenants.
- admin      : all brands within their own tenant; creates agents.
- agent      : only their assigned brands; read-only work queues.
"""
from __future__ import annotations

from django.contrib.auth.models import User
from drf_spectacular.utils import extend_schema
from rest_framework import generics, status
from rest_framework.filters import OrderingFilter, SearchFilter
from rest_framework.permissions import AllowAny
from rest_framework.response import Response
from rest_framework.views import APIView

from crm import user_service as usvc
from crm.auth import audit
from crm.models import Contact
from ingest.models import Brand, Tenant
from . import rbac
from .permissions import HasCapability, IsAdmin, IsAuthenticatedProfile
from .serializers import BrandSerializer, ContactSerializer
from .tenancy import current_tenant


def _svc_err(e: usvc.UserMgmtError) -> Response:
    return Response({"error": {"code": e.code, "message": e.message}}, status=e.status)


class PingView(APIView):
    """Liveness probe for the v1 API. No auth."""
    authentication_classes: list = []
    permission_classes = [AllowAny]

    @extend_schema(summary="Ping")
    def get(self, request):
        return Response({"status": "ok", "version": "v1"})


class WhoAmIView(APIView):
    """Authenticated user: role, tenant, and accessible brand slugs."""
    permission_classes = [IsAuthenticatedProfile]

    @extend_schema(summary="Current user")
    def get(self, request):
        from crm.permissions_store import capabilities_for

        user = request.user
        tenant = current_tenant(request)
        return Response({
            "username": user.username,
            "name": user.first_name or user.username,
            "role": rbac.role(user),
            "tenant": tenant.slug if tenant else None,
            "brands": list(rbac.accessible_brands(user).values_list("slug", flat=True)),
            # Frontend gates on capabilities, not role (falls back to role for
            # older sessions that predate this field).
            "capabilities": capabilities_for(rbac.role(user)),
        })


@extend_schema(tags=["contacts"], summary="List contacts (brand-scoped)")
class ContactListView(generics.ListAPIView):
    """Contacts the caller may see. superadmin/admin -> their tenant; agent ->
    only contacts active on their assigned brands (via ContactBrand)."""
    serializer_class = ContactSerializer
    filter_backends = [SearchFilter, OrderingFilter]
    search_fields = ["name", "phone", "email", "external_id"]
    ordering_fields = ["updated_at", "name", "status"]

    def get_queryset(self):
        user = self.request.user
        r = rbac.role(user)
        if r == "superadmin":
            qs = Contact.objects.all()
        elif r == "admin":
            tenant = rbac.user_tenant(user)
            qs = Contact.objects.filter(tenant=tenant) if tenant else Contact.objects.none()
        elif r == "agent":
            qs = Contact.objects.filter(
                brand_links__brand__in=rbac.accessible_brands(user)).distinct()
        else:
            qs = Contact.objects.none()
        return qs.order_by("-updated_at")

    @extend_schema(summary="Create a contact (admin+)")
    def post(self, request):
        """Create a contact by hand (the dashboard's Add-contact form). Tenant =
        the caller's (superadmin may pass a "tenant" slug). Brand membership:
        the CURRENT brand (`brand` slug) by default, all tenant brands when
        `all_brands` is set, or an explicit `brand_ids` list. 409 when the phone
        already exists in-tenant (phone is the identity key everywhere)."""
        from crm.models import ContactBrand
        from crm.permissions_store import has_capability

        if not has_capability(rbac.role(request.user), "contacts.manage"):
            return Response({"error": {"code": "forbidden",
                                       "message": "Admin role required to create contacts."}},
                            status=403)
        if rbac.role(request.user) == "superadmin" and request.data.get("tenant"):
            tenant = Tenant.objects.filter(slug=request.data["tenant"]).first()
        else:
            tenant = rbac.user_tenant(request.user)
        if tenant is None:
            return Response({"error": {"code": "bad_request",
                                       "message": "No tenant to create the contact under."}},
                            status=400)
        name = str(request.data.get("name") or "").strip()
        phone = str(request.data.get("phone") or "").strip()
        if not name or not phone:
            return Response({"error": {"code": "bad_request",
                                       "message": "name and phone are required."}}, status=400)
        if Contact.objects.filter(tenant=tenant, phone=phone).exists():
            return Response({"error": {"code": "conflict",
                                       "message": "A contact with this phone already exists."}},
                            status=409)

        def pick(field, choices, default):
            value = str(request.data.get(field) or "").strip().lower()
            return value if value in {c for c, _ in choices} else default

        tags = request.data.get("tags")
        attributes = request.data.get("attributes")
        contact = Contact.objects.create(
            tenant=tenant, name=name, phone=phone,
            email=str(request.data.get("email") or "").strip(),
            language=str(request.data.get("language") or "").strip().lower()
            if str(request.data.get("language") or "").strip().lower() in Contact.SUPPORTED_LANGUAGES
            else "en",
            status=pick("status", Contact.STATUS_CHOICES, "pending"),
            method=pick("method", Contact.METHOD_CHOICES, "ai-call"),
            company_name=str(request.data.get("company") or tenant.name).strip(),
            external_id=str(request.data.get("external_id") or "").strip()[:64],
            tags=list(tags) if isinstance(tags, list) else [],
            attributes=dict(attributes) if isinstance(attributes, dict) else {},
            notes=str(request.data.get("notes") or "").strip(),
        )
        # Brand membership on create. Precedence: explicit `brand_ids` →
        # `all_brands` toggle (cross-brand) → the caller's CURRENT brand
        # (default; the create form passes the topbar-selected brand slug) →
        # legacy fallback of every tenant brand when no brand context is sent.
        brand_ids = request.data.get("brand_ids") or []
        all_brands = str(request.data.get("all_brands") or "").strip().lower() \
            in ("1", "true", "yes", "on")
        current = str(request.data.get("brand") or "").strip()
        brands = Brand.objects.filter(tenant=tenant)
        if isinstance(brand_ids, list) and brand_ids:
            brands = brands.filter(id__in=brand_ids)
        elif all_brands:
            pass  # link to every brand in the tenant
        elif current:
            brands = brands.filter(slug=current)  # default: current brand only
        for b in brands:
            ContactBrand.objects.get_or_create(contact=contact, brand=b)
        audit(request, "contact.create", target=phone,
              detail=f"name={name} tenant={tenant.slug}")
        return Response(ContactSerializer(contact).data, status=status.HTTP_201_CREATED)


@extend_schema(tags=["contacts"])
class ContactDetailView(APIView):
    """Single-contact updates. PATCH accepts `language` only for now (the
    contact-details page's inline editor) — the AI conducts calls and hints
    STT in this language, so it's operator-tunable per contact. Scoping
    mirrors ContactListView: superadmin all, admin their tenant, agent
    their assigned brands."""

    def _scoped(self, request):
        r = rbac.role(request.user)
        if r == "superadmin":
            return Contact.objects.all()
        if r == "admin":
            tenant = rbac.user_tenant(request.user)
            return Contact.objects.filter(tenant=tenant) if tenant else Contact.objects.none()
        if r == "agent":
            return Contact.objects.filter(
                brand_links__brand__in=rbac.accessible_brands(request.user)).distinct()
        return Contact.objects.none()

    @extend_schema(summary="Update a contact (language)")
    def patch(self, request, pk: int):
        contact = self._scoped(request).filter(pk=pk).first()
        if contact is None:
            return Response({"error": {"code": "not_found",
                                       "message": "No such contact (or not visible to you)."}},
                            status=404)
        changed = []
        if "language" in request.data:
            language = str(request.data.get("language") or "").strip().lower()
            if language not in Contact.SUPPORTED_LANGUAGES:
                return Response({"error": {"code": "bad_request",
                                           "message": "language must be one of "
                                                      f"{', '.join(Contact.SUPPORTED_LANGUAGES)}."}},
                                status=400)
            contact.language = language
            changed.append("language")
        if changed:
            contact.save(update_fields=changed + ["updated_at"])
            audit(request, "contact.update", target=contact.phone,
                  detail=f"{', '.join(changed)} -> " +
                         ", ".join(str(getattr(contact, f)) for f in changed))
        return Response(ContactSerializer(contact).data)

    @extend_schema(summary="Delete a contact (contacts.manage)")
    def delete(self, request, pk: int):
        """Hard-delete a contact. Requires `contacts.manage`, scoped exactly
        like PATCH/GET (a contact outside the caller's tenant/brands is a 404,
        never a leak). The model's FKs make this clean: owned rows (records,
        brand links, assignments, queued jobs, summaries) cascade away, while
        conversation history (threads, call records, flow runs) is SET_NULL —
        preserved but unlinked — so nothing is left dangling."""
        from crm.permissions_store import has_capability

        if not has_capability(rbac.role(request.user), "contacts.manage"):
            return Response({"error": {"code": "forbidden",
                                       "message": "Admin role required to delete contacts."}},
                            status=403)
        contact = self._scoped(request).filter(pk=pk).first()
        if contact is None:
            return Response({"error": {"code": "not_found",
                                       "message": "No such contact (or not visible to you)."}},
                            status=404)
        phone = contact.phone
        contact.delete()
        audit(request, "contact.delete", target=phone)
        return Response({"ok": True})


@extend_schema(tags=["contacts"])
class ContactBrandsView(APIView):
    """Manage a contact's brand memberships WITHOUT deleting the shared profile.
    POST {brand} links the contact to a brand; DELETE {brand} unlinks it from
    ONE brand (the profile and every other brand link stay). This is the
    'remove from this brand only' seam — plain contact DELETE removes the whole
    tenant-shared profile from every brand. Scoped + admin+ like the rest."""
    permission_classes = [IsAuthenticatedProfile]

    def _resolve(self, request, pk):
        """(contact, brand, error_response). Contact scoped to the caller; brand
        must be in the contact's tenant AND accessible to the caller."""
        from crm.permissions_store import has_capability

        if not has_capability(rbac.role(request.user), "contacts.manage"):
            return None, None, Response(
                {"error": {"code": "forbidden",
                           "message": "Admin role required to manage brand links."}},
                status=403)
        contact = ContactDetailView()._scoped(request).filter(pk=pk).first()
        if contact is None:
            return None, None, Response(
                {"error": {"code": "not_found",
                           "message": "No such contact (or not visible to you)."}},
                status=404)
        slug = str(request.data.get("brand") or "").strip()
        brand = Brand.objects.filter(tenant=contact.tenant, slug=slug).first()
        if brand is None:
            return None, None, Response(
                {"error": {"code": "bad_request",
                           "message": "Unknown brand for this contact's tenant."}},
                status=400)
        if rbac.role(request.user) == "agent" \
                and brand not in rbac.accessible_brands(request.user):
            return None, None, Response(
                {"error": {"code": "forbidden",
                           "message": "That brand is not assigned to you."}},
                status=403)
        return contact, brand, None

    @extend_schema(summary="Link a contact to a brand (contacts.manage)")
    def post(self, request, pk: int):
        from crm.models import ContactBrand

        contact, brand, err = self._resolve(request, pk)
        if err is not None:
            return err
        _link, created = ContactBrand.objects.get_or_create(contact=contact, brand=brand)
        audit(request, "contact.brand_link", target=contact.phone,
              detail=f"+brand {brand.slug}")
        return Response({"ok": True, "created": created,
                         "brands": [bl.brand.slug for bl in contact.brand_links.all()]})

    @extend_schema(summary="Unlink a contact from one brand (contacts.manage)")
    def delete(self, request, pk: int):
        from crm.models import ContactBrand

        contact, brand, err = self._resolve(request, pk)
        if err is not None:
            return err
        ContactBrand.objects.filter(contact=contact, brand=brand).delete()
        audit(request, "contact.brand_unlink", target=contact.phone,
              detail=f"-brand {brand.slug}")
        return Response({"ok": True,
                         "brands": [bl.brand.slug for bl in contact.brand_links.all()]})


@extend_schema(tags=["brands"])
class BrandsView(APIView):
    """List brands the caller can access; create a brand (superadmin any tenant,
    admin in their own tenant)."""
    permission_classes = [IsAuthenticatedProfile]

    def get(self, request):
        brands = rbac.accessible_brands(request.user).order_by("tenant__slug", "slug")
        return Response({"brands": BrandSerializer(brands, many=True).data})

    def post(self, request):
        from crm.permissions_store import has_capability

        if not has_capability(rbac.role(request.user), "brands.manage"):
            return Response({"error": {"code": "forbidden",
                                       "message": "Admin role required."}}, status=403)
        name = (request.data.get("name") or "").strip()
        slug = (request.data.get("slug") or "").strip().lower()
        if not name or not slug:
            return Response({"error": {"code": "bad_request",
                                       "message": "name and slug are required."}}, status=400)
        # superadmin may target any tenant; admin is pinned to their own.
        if rbac.role(request.user) == "superadmin":
            tslug = request.data.get("tenant")
            tenant = Tenant.objects.filter(slug=tslug).first() if tslug else None
            if tenant is None:
                return Response({"error": {"code": "bad_request",
                                           "message": "tenant slug required."}}, status=400)
        else:
            tenant = rbac.user_tenant(request.user)
        if Brand.objects.filter(tenant=tenant, slug=slug).exists():
            return Response({"error": {"code": "conflict",
                                       "message": "Brand slug already exists in this tenant."}}, status=409)
        brand = Brand.objects.create(tenant=tenant, name=name, slug=slug)
        audit(request, "brand.create", target=f"{tenant.slug}/{slug}")
        return Response(BrandSerializer(brand).data, status=201)


@extend_schema(tags=["brands"])
class BrandDetailView(APIView):
    """Update (PATCH) or delete (DELETE) a single brand. Requires
    `brands.manage`; scoped like BrandsView — superadmin any tenant, admin only
    brands in their own tenant (a brand outside that scope is a 404). agent has
    no manage capability, so it 403s before scoping."""
    permission_classes = [IsAuthenticatedProfile]

    def _manageable(self, request):
        """Brands this caller may edit/delete (NOT the agent read scope)."""
        r = rbac.role(request.user)
        if r == "superadmin":
            return Brand.objects.all()
        if r == "admin":
            tenant = rbac.user_tenant(request.user)
            return Brand.objects.filter(tenant=tenant) if tenant else Brand.objects.none()
        return Brand.objects.none()

    def _gate(self, request):
        from crm.permissions_store import has_capability

        if not has_capability(rbac.role(request.user), "brands.manage"):
            return Response({"error": {"code": "forbidden",
                                       "message": "Admin role required to manage brands."}},
                            status=403)
        return None

    @extend_schema(summary="Update a brand (brands.manage)")
    def patch(self, request, brand_id: int):
        denied = self._gate(request)
        if denied:
            return denied
        brand = self._manageable(request).filter(pk=brand_id).first()
        if brand is None:
            return Response({"error": {"code": "not_found",
                                       "message": "No such brand (or not visible to you)."}},
                            status=404)
        changed = []
        if "name" in request.data:
            name = str(request.data.get("name") or "").strip()
            if not name:
                return Response({"error": {"code": "bad_request",
                                           "message": "name cannot be empty."}}, status=400)
            brand.name = name
            changed.append("name")
        if "slug" in request.data:
            slug = str(request.data.get("slug") or "").strip().lower()
            if not slug:
                return Response({"error": {"code": "bad_request",
                                           "message": "slug cannot be empty."}}, status=400)
            if (Brand.objects.filter(tenant=brand.tenant, slug=slug)
                    .exclude(pk=brand.pk).exists()):
                return Response({"error": {"code": "conflict",
                                           "message": "Brand slug already exists in this tenant."}},
                                status=409)
            brand.slug = slug
            changed.append("slug")
        if "is_active" in request.data:
            brand.is_active = bool(request.data.get("is_active"))
            changed.append("is_active")
        if not changed:
            return Response({"error": {"code": "bad_request",
                                       "message": "Nothing to update — send name, slug and/or is_active."}},
                            status=400)
        brand.save(update_fields=changed + ["updated_at"])
        audit(request, "brand.update", target=f"{brand.tenant.slug}/{brand.slug}",
              detail=f"fields: {', '.join(changed)}")
        return Response(BrandSerializer(brand).data)

    @extend_schema(summary="Delete a brand (brands.manage)")
    def delete(self, request, brand_id: int):
        """Hard-delete a brand, but only when nothing depends on it. A brand
        with contacts (ContactBrand), assigned agents (Profile.brands) or
        knowledge items (KnowledgeItem, which CASCADE — real data loss) returns
        409 with the counts, mirroring the roles delete-when-assigned block —
        reassign/clear first. To retire a brand without deleting, PATCH
        is_active=false."""
        from crm.models import ContactBrand, KnowledgeItem

        denied = self._gate(request)
        if denied:
            return denied
        brand = self._manageable(request).filter(pk=brand_id).first()
        if brand is None:
            return Response({"error": {"code": "not_found",
                                       "message": "No such brand (or not visible to you)."}},
                            status=404)
        contacts = ContactBrand.objects.filter(brand=brand).count()
        agents = brand.agents.count()
        knowledge = KnowledgeItem.objects.filter(brand=brand).count()
        if contacts or agents or knowledge:
            parts = []
            if contacts:
                parts.append(f"{contacts} contact(s)")
            if agents:
                parts.append(f"{agents} user(s)")
            if knowledge:
                parts.append(f"{knowledge} knowledge item(s)")
            return Response({"error": {"code": "conflict",
                                       "message": f"This brand still has {', '.join(parts)} — "
                                                  "reassign or remove them first."},
                             "contacts": contacts, "agents": agents,
                             "knowledge": knowledge}, status=409)
        label = f"{brand.tenant.slug}/{brand.slug}"
        brand.delete()
        audit(request, "brand.delete", target=label)
        return Response({"ok": True})


@extend_schema(tags=["users"])
class UsersView(APIView):
    """Canonical user management (single source of truth: crm.user_service).
    GET list / POST create. superadmin: everyone; admin: only agents in
    their own tenant (+ brand assignment); agent: 403.

    Gated on the `users.manage` capability (data-driven); the admin->agent
    tenant tiering still lives in crm.user_service and applies on top."""
    permission_classes = [HasCapability.for_("users.manage")]

    def get(self, request):
        tenant = rbac.user_tenant(request.user)
        rows = usvc.list_users(rbac.role(request.user), tenant.id if tenant else None)
        return Response({"users": [usvc.managed_user_payload(u) for u in rows]})

    def post(self, request):
        d = request.data
        try:
            user = usvc.create_user(
                rbac.role(request.user), getattr(request.user, "profile", None),
                username=d.get("username"), name=d.get("name"),
                password=d.get("password"), role=d.get("role"),
                brand_ids=d.get("brand_ids"), tenant=d.get("tenant"),
            )
        except usvc.UserMgmtError as e:
            return _svc_err(e)
        audit(request, "user.create", target=user.username, detail=f"role: {usvc.role_of(user)}")
        return Response(usvc.managed_user_payload(user), status=201)


@extend_schema(tags=["users"])
class UserDetailView(APIView):
    """Update (POST) / deactivate (DELETE) a user, same rules as create."""
    permission_classes = [HasCapability.for_("users.manage")]

    @staticmethod
    def _target(user_id):
        return User.objects.select_related("profile").filter(pk=user_id).first()

    def post(self, request, user_id):
        target = self._target(user_id)
        if target is None:
            return Response({"error": {"code": "not_found", "message": "User not found."}}, status=404)
        d = request.data
        try:
            target, changed = usvc.update_user(
                rbac.role(request.user), getattr(request.user, "profile", None), target,
                name=d.get("name", usvc._UNSET), role=d.get("role", usvc._UNSET),
                active=d.get("active", usvc._UNSET), password=d.get("password", usvc._UNSET),
                brand_ids=d.get("brand_ids", usvc._UNSET),
            )
        except usvc.UserMgmtError as e:
            return _svc_err(e)
        if changed:
            audit(request, "user.update", target=target.username, detail=f"fields: {', '.join(changed)}")
        return Response(usvc.managed_user_payload(target))

    def delete(self, request, user_id):
        target = self._target(user_id)
        if target is None:
            return Response({"error": {"code": "not_found", "message": "User not found."}}, status=404)
        try:
            usvc.deactivate_user(rbac.role(request.user), getattr(request.user, "profile", None), target)
        except usvc.UserMgmtError as e:
            return _svc_err(e)
        audit(request, "user.deactivate", target=target.username)
        return Response(usvc.managed_user_payload(target))
