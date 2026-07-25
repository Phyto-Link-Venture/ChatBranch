"""Flow-builder persistence + engine REST API (under /api/flows/).

  GET  library   -> the saved workflow library {workflows, agentSkills}
                    (agentSkills are READ-ONLY here — they feed the builder's
                    node picker; Settings → AI Skills owns them)
  PUT  library   -> WORKFLOW sync from the builder (admin+): upserts by builder
                    id, deletes workflow rows the builder no longer has, and
                    preserves each workflow's `active` flag unless the payload
                    sets it explicitly. `agentSkills` in the body is accepted
                    for backward compatibility and IGNORED — see below.
  GET  runs      -> recent FlowRuns (?limit=, ?status=) for monitoring
  GET  queue     -> recent OutboundJobs (?limit=) — the conversation queue
  POST tick      -> run one engine pass now (admin+) — demo/testing seam
  GET  status    -> engine counters + ticker state

Auth mirrors the whatsapp app: every endpoint requires a bearer role; writes
require admin+ (crm.auth). The library payload uses the builder's camelCase
field names so the frontend syncs its state verbatim.
"""
import json

from django.db.models import Count
from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac
from crm.models import AgentSkill, FlowRun, OutboundJob, Workflow

from . import engine


def _json_body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except ValueError:
        return {}


def _auth_required(request) -> JsonResponse | None:
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _can_manage(request) -> bool:
    return rbac.role_from_request(request) in ("superadmin", "admin")


def _wf_payload(w: Workflow) -> dict:
    return {"id": w.flow_id, "name": w.name, "hint": w.hint, "active": w.active,
            "nodes": w.nodes, "edges": w.edges}


def _skill_payload(s: AgentSkill) -> dict:
    return {"id": s.skill_id, "name": s.name, "useWhen": s.use_when,
            "instructions": s.instructions}


def _library_payload() -> dict:
    return {
        "workflows": [_wf_payload(w) for w in Workflow.objects.all()],
        "agentSkills": [_skill_payload(s) for s in AgentSkill.objects.all()],
    }


@csrf_exempt
def library(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_library_payload())
    if request.method != "PUT":
        return HttpResponseNotAllowed(["GET", "PUT"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required."}, status=403)

    body = _json_body(request)
    workflows = body.get("workflows")
    skills = body.get("agentSkills")
    if not isinstance(workflows, list) or not isinstance(skills, list):
        return JsonResponse(
            {"detail": "Body must carry workflows[] and agentSkills[]."}, status=400)

    requester = rbac.requester_name(request)
    kept_wf = set()
    for pos, wf in enumerate(workflows):
        flow_id = str(wf.get("id") or "").strip()
        if not flow_id:
            continue
        kept_wf.add(flow_id)
        defaults = {
            "name": str(wf.get("name") or "Workflow")[:120],
            "hint": str(wf.get("hint") or "")[:200],
            "nodes": wf.get("nodes") or [],
            "edges": wf.get("edges") or [],
            "position": pos,
            "updated_by": requester,
        }
        if isinstance(wf.get("active"), bool):
            defaults["active"] = wf["active"]
        Workflow.objects.update_or_create(flow_id=flow_id, defaults=defaults)
    Workflow.objects.exclude(flow_id__in=kept_wf).delete()
    # agentSkills in the payload are IGNORED, deliberately (Phase 8).
    # crm.AgentSkill is owned exclusively by GET/PUT /api/aicore/skills
    # (Settings → AI Skills). This sync used to upsert AND PRUNE them, so a
    # stale flow-builder tab could silently delete skills an operator had just
    # typed into Settings. The builder still READS them (GET below) for its
    # node picker; it can no longer write them.

    rbac.audit(request, "flows.library_save",
               detail=f"{len(kept_wf)} workflows "
                      f"({len(skills)} skills ignored — owned by /api/aicore/skills)")
    return JsonResponse(_library_payload())


def _run_payload(r: FlowRun) -> dict:
    return {
        "id": r.pk,
        "workflow": {"id": r.workflow.flow_id, "name": r.workflow.name},
        "contact": ({"id": r.contact.pk, "name": r.contact.name,
                      "phone": r.contact.phone} if r.contact else None),
        "status": r.status,
        "currentNode": r.current_node,
        "waitFor": r.wait_for,
        "waitUntil": r.wait_until.isoformat() if r.wait_until else None,
        "startedAt": r.started_at.isoformat() if r.started_at else None,
        "finishedAt": r.finished_at.isoformat() if r.finished_at else None,
        "log": (r.log or [])[-30:],
    }


def runs(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    qs = FlowRun.objects.select_related("workflow", "contact")
    status_filter = request.GET.get("status", "")
    if status_filter:
        qs = qs.filter(status=status_filter)
    try:
        limit = max(1, min(int(request.GET.get("limit", 50)), 200))
    except ValueError:
        limit = 50
    return JsonResponse({"runs": [_run_payload(r) for r in qs[:limit]]})


def _job_payload(j: OutboundJob) -> dict:
    flow = None
    if j.flow_run is not None:
        flow = {"run": j.flow_run_id, "workflow": j.flow_run.workflow.name,
                "node": j.flow_node}
    return {
        "id": j.pk,
        "status": j.status,
        "kind": j.kind,
        "channel": j.channel,
        "contact": j.contact.name if j.contact_id else None,
        "to": j.to_address,
        "account": j.account.name if j.account_id else None,
        "attempts": j.attempts,
        "notBefore": j.not_before.isoformat() if j.not_before else None,
        "sentAt": j.sent_at.isoformat() if j.sent_at else None,
        "lastError": j.last_error,
        "flow": flow,
        "createdAt": j.created_at.isoformat() if j.created_at else None,
    }


def queue_jobs(request):
    """Recent conversation-queue jobs, newest first — the operator's view of
    what the pacing worker (flows/queue.py) is sending, deferring or failing."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    try:
        limit = max(1, min(int(request.GET.get("limit", 100)), 200))
    except ValueError:
        limit = 100
    qs = (OutboundJob.objects
          .select_related("contact", "account", "flow_run__workflow")
          .order_by("-created_at", "-pk"))
    return JsonResponse({"jobs": [_job_payload(j) for j in qs[:limit]]})


@csrf_exempt
def tick_now(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    denied = _auth_required(request)
    if denied:
        return denied
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required."}, status=403)
    result = engine.tick()
    rbac.audit(request, "flows.tick", detail=str(result))
    return JsonResponse(result)


def status(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    by_status = {row["status"]: row["n"] for row in
                 FlowRun.objects.values("status").annotate(n=Count("id"))}
    return JsonResponse({
        "workflows": Workflow.objects.count(),
        "activeWorkflows": Workflow.objects.filter(active=True).count(),
        "agentSkills": AgentSkill.objects.count(),
        "runs": by_status,
        "tickerAlive": engine.ticker_alive(),
    })
