"""AI configuration API (under /api/aicore/).

  GET  persona            -> {"persona", "is_default", "default"}  (any authed role)
  PUT  persona            -> {"persona": str}; superadmin only; "" resets to the
                             built-in default. Audited.

  GET  knowledge          -> ?kind=&active=&q= filtered list + counts
  POST knowledge          -> create (admin+)
  PATCH/DELETE knowledge/<id>  -> update / remove (admin+)
  POST knowledge/preview  -> {contact_id?, channel, text} -> the EXACT block the
                             AI receives, plus the budget consumed

  GET  skills             -> the crm.AgentSkill library
  PUT  skills             -> replace it (admin+). **Sole writer.** The
                             flow-builder library sync no longer touches these
                             rows; it used to upsert AND PRUNE them, so a stale
                             builder tab could silently delete skills an
                             operator had just written here.

Auth mirrors whatsapp/views.py: bearer role via crm.auth (the X-Role header
only works pre-seed, while no Profile rows exist). Mutations are admin+ except
the persona, which stays superadmin.
"""
import json

from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac

from . import knowledge as knowledge_mod
from . import persona


def _json_body(request) -> dict:
    try:
        body = json.loads(request.body or b"{}")
    except ValueError:  # covers JSONDecodeError AND non-UTF-8 bodies
        return {}
    return body if isinstance(body, dict) else {}


def _auth_required(request) -> JsonResponse | None:
    """401 unless the requester carries a role — same pattern as whatsapp."""
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _can_manage(request) -> bool:
    return rbac.role_from_request(request) in ("superadmin", "admin")


# ---- persona ---------------------------------------------------------------------

def _persona_payload() -> dict:
    return {
        "persona": persona.persona_text(),
        "is_default": persona.is_default(),
        "default": persona.DEFAULT_PERSONA,
    }


@csrf_exempt
def persona_view(request):
    if request.method not in ("GET", "PUT"):
        return HttpResponseNotAllowed(["GET", "PUT"])
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_persona_payload())

    if rbac.role_from_request(request) != "superadmin":
        return JsonResponse({"detail": "Super admin role required."}, status=403)
    value = _json_body(request).get("persona")
    if not isinstance(value, str):
        return JsonResponse({"detail": "persona must be a string."}, status=400)
    persona.save_persona(value)
    rbac.audit(request, "aicore.persona.update", target=persona.PERSONA_SETTING_KEY,
               detail="reset to default" if not value.strip() else value.strip()[:200])
    return JsonResponse(_persona_payload())


# ---- knowledge base ----------------------------------------------------------------

def _item_payload(item) -> dict:
    from django.utils import timezone as dj_tz

    now = dj_tz.now()
    started = item.starts_at is None or item.starts_at <= now
    ended = item.ends_at is not None and item.ends_at <= now
    return {
        "id": item.pk,
        "kind": item.kind,
        "title": item.title,
        "question": item.question,
        "content": item.content,
        "tags": item.tags or [],
        "channels": item.channels or [],
        "priority": item.priority,
        "active": item.active,
        "entity_id": item.entity_id,
        "starts_at": item.starts_at.isoformat() if item.starts_at else None,
        "ends_at": item.ends_at.isoformat() if item.ends_at else None,
        # what the retrieval layer will actually do with it, precomputed for
        # the UI's live / scheduled / expired chip
        "live": bool(item.active and started and not ended),
        "expired": ended,
        "scheduled": not started,
        "created_by": item.created_by,
        "updated_at": item.updated_at.isoformat() if item.updated_at else None,
    }


def _parse_dt(value):
    """ISO string -> aware datetime, or None. Raises ValueError on garbage."""
    from django.utils import timezone as dj_tz
    from django.utils.dateparse import parse_datetime

    raw = str(value or "").strip()
    if not raw:
        return None
    parsed = parse_datetime(raw)
    if parsed is None:
        raise ValueError(f"{raw!r} is not an ISO datetime")
    return dj_tz.make_aware(parsed) if dj_tz.is_naive(parsed) else parsed


def _apply(item, body: dict) -> None:
    """Partial patch from a request body onto a KnowledgeItem (unsaved)."""
    from crm.models import KnowledgeItem

    kinds = [k for k, _ in KnowledgeItem.KIND_CHOICES]
    if "kind" in body:
        kind = str(body.get("kind") or "").strip()
        if kind not in kinds:
            raise ValueError(f"kind must be one of {', '.join(kinds)}")
        item.kind = kind
    for field, limit in (("title", 200), ("question", 300), ("created_by", 64)):
        if field in body:
            setattr(item, field, str(body.get(field) or "")[:limit])
    if "content" in body:
        item.content = str(body.get("content") or "")
    for field in ("tags", "channels"):
        if field in body:
            raw = body.get(field)
            if not isinstance(raw, list):
                raise ValueError(f"{field} must be a list")
            setattr(item, field, [str(x).strip()[:48] for x in raw if str(x).strip()])
    if "priority" in body:
        try:
            item.priority = int(body.get("priority") or 0)
        except (TypeError, ValueError):
            raise ValueError("priority must be an integer")
    if "active" in body:
        item.active = bool(body.get("active"))
    if "entity_id" in body:
        raw = body.get("entity_id")
        item.entity_id = int(raw) if str(raw or "").strip().isdigit() else None
    for field in ("starts_at", "ends_at"):
        if field in body:
            setattr(item, field, _parse_dt(body.get(field)))
    if (item.starts_at and item.ends_at and item.ends_at <= item.starts_at):
        raise ValueError("ends_at must be after starts_at")


@csrf_exempt
def knowledge_list(request):
    from crm.models import KnowledgeItem

    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        qs = KnowledgeItem.objects.all()
        kind = str(request.GET.get("kind") or "").strip()
        if kind:
            qs = qs.filter(kind=kind)
        active = str(request.GET.get("active") or "").strip().lower()
        if active in ("true", "1"):
            qs = qs.filter(active=True)
        elif active in ("false", "0"):
            qs = qs.filter(active=False)
        q = str(request.GET.get("q") or "").strip()
        if q:
            from django.db.models import Q
            qs = qs.filter(Q(title__icontains=q) | Q(question__icontains=q)
                           | Q(content__icontains=q))
        counts = {k: 0 for k, _ in KnowledgeItem.KIND_CHOICES}
        for row in KnowledgeItem.objects.values_list("kind", flat=True):
            counts[row] = counts.get(row, 0) + 1
        return JsonResponse({
            "items": [_item_payload(i) for i in qs],
            "counts": counts,
            "kinds": [{"id": k, "label": lbl} for k, lbl in KnowledgeItem.KIND_CHOICES],
            "budget": knowledge_mod.BUDGET,
        })
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to edit knowledge."},
                            status=403)
    body = _json_body(request)
    item = KnowledgeItem(created_by=rbac.requester_name(request))
    try:
        _apply(item, body)
    except ValueError as e:
        return JsonResponse({"detail": str(e)}, status=400)
    if not item.title.strip():
        return JsonResponse({"detail": "title is required."}, status=400)
    if not item.content.strip():
        return JsonResponse({"detail": "content is required."}, status=400)
    item.tenant = rbac.tenant_from_request(request)
    item.save()
    rbac.audit(request, "knowledge.update", target=f"{item.kind}:{item.title}",
               detail="created")
    return JsonResponse(_item_payload(item))


@csrf_exempt
def knowledge_detail(request, item_id: int):
    from crm.models import KnowledgeItem

    denied = _auth_required(request)
    if denied:
        return denied
    item = KnowledgeItem.objects.filter(pk=item_id).first()
    if item is None:
        return JsonResponse({"detail": "Knowledge item not found."}, status=404)
    if request.method == "GET":
        return JsonResponse(_item_payload(item))
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["GET", "PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to edit knowledge."},
                            status=403)
    if request.method == "DELETE":
        label = f"{item.kind}:{item.title}"
        item.delete()
        rbac.audit(request, "knowledge.update", target=label, detail="deleted")
        return JsonResponse({"ok": True})
    try:
        _apply(item, _json_body(request))
    except ValueError as e:
        return JsonResponse({"detail": str(e)}, status=400)
    if not item.title.strip() or not item.content.strip():
        return JsonResponse({"detail": "title and content are required."}, status=400)
    item.save()
    rbac.audit(request, "knowledge.update", target=f"{item.kind}:{item.title}",
               detail="updated")
    return JsonResponse(_item_payload(item))


@csrf_exempt
def knowledge_preview(request):
    """The exact block the AI is handed, for a given channel + message.

    This matters more than it looks: it turns "why did the bot say that?" from
    a code investigation into a button."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    contact = None
    raw_id = body.get("contact_id")
    if str(raw_id or "").strip().isdigit():
        from crm.models import Contact
        contact = Contact.objects.filter(pk=int(raw_id)).first()
    channel = str(body.get("channel") or "").strip()
    text = str(body.get("text") or "")
    block = knowledge_mod.build_knowledge_context(contact, channel=channel, text=text)
    return JsonResponse({
        "block": block,
        "full_prompt_block": knowledge_mod.prompt_block(contact, channel=channel,
                                                        text=text),
        "chars": len(block),
        "budget": knowledge_mod.BUDGET,
        "items": len([ln for ln in block.split("\n") if ln.strip()]),
        "contact": ({"id": contact.pk, "name": contact.name} if contact else None),
    })


# ---- skills (crm.AgentSkill) — the SOLE writer -------------------------------------

def _skill_payload(s) -> dict:
    return {"id": s.skill_id, "name": s.name, "useWhen": s.use_when,
            "instructions": s.instructions, "position": s.position}


@csrf_exempt
def skills_view(request):
    """GET: the library (any authed role — the flow builder reads it too).
    PUT: replace it wholesale (admin+). This endpoint is the ONLY writer of
    crm.AgentSkill; `PUT /api/flows/library` explicitly ignores the
    `agentSkills` it still receives from older builder tabs."""
    from crm.models import AgentSkill

    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"skills": [_skill_payload(s) for s in AgentSkill.objects.all()]})
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to edit skills."}, status=403)
    body = _json_body(request)
    skills = body.get("skills")
    if not isinstance(skills, list):
        return JsonResponse({"detail": "Body must carry skills[]."}, status=400)
    kept = set()
    for pos, sk in enumerate(skills):
        if not isinstance(sk, dict):
            continue
        skill_id = str(sk.get("id") or "").strip()[:64]
        if not skill_id:
            continue
        kept.add(skill_id)
        AgentSkill.objects.update_or_create(skill_id=skill_id, defaults={
            "name": str(sk.get("name") or "Agent skill")[:120],
            "use_when": str(sk.get("useWhen") or ""),
            "instructions": str(sk.get("instructions") or ""),
            "position": pos,
        })
    AgentSkill.objects.exclude(skill_id__in=kept).delete()
    rbac.audit(request, "aicore.skills.update", target="agent-skills",
               detail=f"{len(kept)} skill(s)")
    return JsonResponse({"skills": [_skill_payload(s) for s in AgentSkill.objects.all()]})


# ---- booking hours (per-day appointment schedule) ---------------------------

_BOOKING_DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


@csrf_exempt
def booking_hours_view(request):
    """GET/PUT the per-day appointment booking schedule (Setting `booking_hours`).

    GET  -> {"hours": {"mon":[9,21], ... , "sun":null}}  (any authed role)
    PUT  -> {"hours": {...}} ; admin+. A day is [open, close] whole hours (0–24,
    open<close) or null/[] for CLOSED. This drives BOTH the slots the AI offers
    and the booking guard, so what's offered and what's bookable stay in step."""
    from integrations import actions

    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"hours": actions.booking_hours_raw(),
                             "default": dict(actions._DEFAULT_HOURS)})
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to edit booking hours."},
                            status=403)
    hours = _json_body(request).get("hours")
    if not isinstance(hours, dict):
        return JsonResponse({"detail": "hours must be an object keyed by day."}, status=400)
    clean: dict = {}
    for day in _BOOKING_DAYS:
        val = hours.get(day)
        if val in (None, [], ""):
            clean[day] = None  # closed
            continue
        if not isinstance(val, (list, tuple)) or len(val) < 2:
            return JsonResponse({"detail": f"{day} must be [open, close] or null."},
                                status=400)
        try:
            o, c = int(val[0]), int(val[1])
        except (TypeError, ValueError):
            return JsonResponse({"detail": f"{day} hours must be whole numbers."}, status=400)
        if not (0 <= o < c <= 24):
            return JsonResponse({"detail": f"{day} needs 0 ≤ open < close ≤ 24."},
                                status=400)
        clean[day] = [o, c]
    from crm.models import Setting

    Setting.objects.update_or_create(key="booking_hours",
                                     defaults={"value": json.dumps(clean)})
    rbac.audit(request, "aicore.booking_hours.update", target="booking_hours",
               detail=json.dumps(clean)[:200])
    return JsonResponse({"hours": clean, "default": dict(actions._DEFAULT_HOURS)})
