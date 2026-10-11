using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// flows surface (/api/flows/*): the workflow library sync (GET flows.view /
/// PUT flows.manage — PUT prunes within a scope), FlowRun + queue inspection, a
/// manual start for "Manual / API" flows, per-step statistics, and the manual
/// drain seam (POST /tick). Also /api/analytics/flows (the Analytics page's
/// "Flows" card) because it reads the same tables. The library PUT ignores
/// agentSkills (the aicore endpoints are the sole writer of crm.AgentSkill).
///
/// Statistics come from <see cref="FlowStepEvent"/> rows (one per arrival,
/// completion, park, failure) bucketed by org-local day; run totals come from
/// FlowRun itself. Every read is bounded by the caller's contact scope (tenant,
/// assigned entities, ?entity=) so a branch never counts head office's people.
/// </summary>
public static class FlowsEndpoints
{
    public record WorkflowDto(string? FlowId, string? Id, string Name, string? Hint,
        List<object?>? Nodes, List<object?>? Edges, bool Active, int Position);
    public record LibraryWrite(string? Entity, List<WorkflowDto> Workflows);
    public record EnqueueRequest(string Channel, string Kind, string ToAddress,
        Dictionary<string, object?>? Payload, int? ContactId, int? AccountId);
    public record StartRequest(List<int>? ContactIds);

    private const int DefaultPageSize = 50;

    public static void MapFlowsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- workflow library --------------------------------------------------
        app.MapGet("/api/flows/library", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            // the library rule (ShellScope.ScopeLibrary): an entity-bound agent reads
            // their subtrees + ancestors + the global flows, never another outlet's
            var rows = ShellScope.ScopeLibrary(db.Workflows.AsNoTracking(), db, sc, me, entityId)
                .OrderBy(w => w.Position).ThenBy(w => w.CreatedAt).ToList();
            return Results.Ok(new { workflows = rows.Select(WorkflowOut) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.view");

        app.MapPut("/api/flows/library", async (LibraryWrite body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext http) =>
        {
            if (await Denied(db, me, "flows.manage") is IResult no) return no;
            // The builder GETs the library through ScopeLibrary (entity or "All") and
            // PUTs the whole copy back. So: the rows it LOADED are exactly what the
            // same rule returns for the same ?entity — only those may be pruned —
            // and an existing flow is matched by flowId across every scope the
            // caller may write and updated IN PLACE (its EntityId kept), so a save
            // from "All entities" never duplicates an outlet's flow as a global one.
            var bound = ShellScope.BoundEntityIds(db, sc, me);
            var entityId = ShellScope.ClampPick(bound, await InboxEndpoints.EntityId(db, body.Entity));
            if (bound != null && entityId == null && !string.IsNullOrWhiteSpace(body.Entity)
                && body.Entity is not ("all" or "__all__"))
                return Results.Json(new { detail = "No such entity." }, statusCode: 404);
            var writable = await ShellScope.ScopeLibrary(db.Workflows, db, sc, me, null).ToListAsync();
            var loadedIds = (await ShellScope.ScopeLibrary(db.Workflows.AsNoTracking(), db, sc, me, entityId)
                .Select(w => w.Id).ToListAsync()).ToHashSet();
            var existing = writable.Where(w => loadedIds.Contains(w.Id)).ToList();
            var keep = new HashSet<string>();
            var actor = me.Identity?.Name ?? "system";
            var created = new List<string>();
            var edited = new List<string>();
            var wentLive = new List<string>();
            var paused = new List<string>();
            var now = DateTime.UtcNow;
            foreach (var w in body.Workflows)
            {
                // the builder's string id travels as flowId (older builds sent it as id)
                var flowId = (w.FlowId ?? w.Id ?? "").Trim();
                if (flowId.Length == 0 || !keep.Add(flowId)) continue;
                // the row at the level being edited first, then any loaded row, then
                // any row the caller may write (a flow that lives at another level)
                var row = existing.FirstOrDefault(e => e.FlowId == flowId && e.EntityId == entityId)
                          ?? existing.FirstOrDefault(e => e.FlowId == flowId)
                          ?? writable.FirstOrDefault(e => e.FlowId == flowId && e.EntityId == entityId)
                          ?? writable.FirstOrDefault(e => e.FlowId == flowId);
                var isNew = row == null;
                if (row == null)
                {
                    row = new Workflow { FlowId = flowId, EntityId = entityId, CreatedAt = now };
                    db.Workflows.Add(row);
                    created.Add(w.Name);
                }
                var nodes = w.Nodes ?? new();
                var edges = w.Edges ?? new();
                var changed = isNew || row.Name != w.Name || row.Hint != (w.Hint ?? "")
                              || row.Position != w.Position || row.Active != w.Active
                              || Json(row.Nodes) != Json(nodes) || Json(row.Edges) != Json(edges);
                if (!changed) continue;
                if (w.Active && !row.Active) { row.LiveSince = now; wentLive.Add(w.Name); }
                if (!w.Active && row.Active) { row.LiveSince = null; paused.Add(w.Name); }
                if (!isNew && (row.Name != w.Name || Json(row.Nodes) != Json(nodes) || Json(row.Edges) != Json(edges)))
                    edited.Add(w.Name);
                row.Name = w.Name; row.Hint = w.Hint ?? "";
                row.Nodes = nodes; row.Edges = edges;
                row.Active = w.Active; row.Position = w.Position;
                row.UpdatedAt = now; row.UpdatedBy = actor;
            }
            // PRUNE within the loaded scope only, and never a row whose flowId the
            // payload still carries (another level's twin of a kept flow).
            var removed = existing.Where(e => !keep.Contains(e.FlowId)).ToList();
            db.Workflows.RemoveRange(removed);
            await db.SaveChangesAsync();

            var target = body.Entity is { Length: > 0 } slug ? $"entity:{slug}" : "global";
            void Event(string action, IEnumerable<string> names)
            {
                var list = names.ToList();
                if (list.Count > 0) Audit(db, me, http, action, target, string.Join(", ", list));
            }
            Event("flow.create", created);
            Event("flow.delete", removed.Select(r => r.Name));
            Event("flow.live", wentLive);
            Event("flow.pause", paused);
            if (edited.Count > 0) AuditEdit(db, me, http, target, edited);
            return Results.Ok(new { count = keep.Count });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- runs: paged list + one run's path ---------------------------------
        app.MapGet("/api/flows/runs", async (string? flow, string? status, string? contact,
            string? node, int? page, int? page_size, string? days, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var q = db.FlowRuns.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(flow))
            {
                var ids = await db.Workflows.AsNoTracking().Where(w => w.FlowId == flow)
                    .Select(w => w.Id).ToListAsync();
                if (int.TryParse(flow, out var pk)) ids.Add(pk);
                q = q.Where(r => ids.Contains(r.WorkflowId));
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.ToLowerInvariant()).ToList();
                q = q.Where(r => wanted.Contains(r.Status));
            }
            if (!string.IsNullOrWhiteSpace(contact))
            {
                if (int.TryParse(contact, out var cid)) q = q.Where(r => r.ContactId == cid);
                else
                {
                    var like = $"%{contact.Trim()}%";
                    q = q.Where(r => r.Contact != null && (EF.Functions.Like(r.Contact.Name, like)
                                                         || EF.Functions.Like(r.Contact.Phone, like)
                                                         || EF.Functions.Like(r.Contact.Email, like)));
                }
            }
            if (!string.IsNullOrWhiteSpace(node))
                q = q.Where(r => db.FlowStepEvents.Any(e => e.RunId == r.Id && e.NodeId == node
                                                            && e.Kind == FlowStepKind.Entered));
            if (!string.IsNullOrWhiteSpace(days))
            {
                var sinceUtc = OrgClock.ToUtc(DateTime.Parse(OrgClock.LastDays(ParseDays(days), OrgClock.Zone(sc))[0]),
                    OrgClock.Zone(sc));
                q = q.Where(r => r.StartedAt >= sinceUtc || (r.FinishedAt != null && r.FinishedAt >= sinceUtc));
            }
            if (await ContactScopeAsync(db, sc, me, entity) is IQueryable<int> scope)
                q = q.Where(r => r.ContactId != null && scope.Contains(r.ContactId.Value));

            var size = Math.Clamp(page_size ?? DefaultPageSize, 1, 200);
            var count = await q.CountAsync();
            var pages = Math.Max(1, (int)Math.Ceiling(count / (double)size));
            var pg = Math.Clamp(page ?? 1, 1, pages);
            var rows = await q.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
                .Skip((pg - 1) * size).Take(size)
                .Select(r => new
                {
                    r.Id, r.WorkflowId, r.ContactId, r.Status, r.CurrentNode, r.WaitUntil, r.WaitFor,
                    r.StartedAt, r.UpdatedAt, r.FinishedAt, r.Detail,
                    ContactName = r.Contact != null ? r.Contact.Name : "",
                    ContactPhone = r.Contact != null ? r.Contact.Phone : "",
                    FlowId = r.Workflow != null ? r.Workflow.FlowId : "",
                    FlowName = r.Workflow != null ? r.Workflow.Name : "",
                })
                .ToListAsync();
            var labels = await NodeLabelsAsync(db, rows.Select(r => r.WorkflowId));
            return Results.Ok(new
            {
                runs = rows.Select(r => new
                {
                    id = r.Id, workflowId = r.WorkflowId, flowId = r.FlowId, flowName = r.FlowName,
                    contactId = r.ContactId, contactName = r.ContactName, contactPhone = r.ContactPhone,
                    status = r.Status, currentNode = r.CurrentNode,
                    currentLabel = Label(labels, r.WorkflowId, r.CurrentNode),
                    waitUntil = Utc(r.WaitUntil), waitFor = r.WaitFor,
                    startedAt = Utc(r.StartedAt), updatedAt = Utc(r.UpdatedAt),
                    finishedAt = Utc(r.FinishedAt), detail = r.Detail,
                }),
                count, page = pg, pageSize = size, pages,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.view");

        app.MapGet("/api/flows/runs/{id:int}", async (int id, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var q = db.FlowRuns.AsNoTracking().Where(r => r.Id == id);
            if (await ContactScopeAsync(db, sc, me, entity) is IQueryable<int> scope)
                q = q.Where(r => r.ContactId != null && scope.Contains(r.ContactId.Value));
            var run = await q.Include(r => r.Workflow).Include(r => r.Contact).FirstOrDefaultAsync();
            if (run == null) return Results.Json(new { detail = "No such run." }, statusCode: 404);
            var labels = await NodeLabelsAsync(db, new[] { run.WorkflowId });
            var events = await db.FlowStepEvents.AsNoTracking().Where(e => e.RunId == id)
                .OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync();
            var ctx = run.Context;
            string CtxStr(string key) => ctx.TryGetValue(key, out var v) && v != null
                ? (v is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : v.ToString() ?? "")
                : "";
            return Results.Ok(new
            {
                run = new
                {
                    id = run.Id, workflowId = run.WorkflowId, flowId = run.Workflow?.FlowId ?? "",
                    flowName = run.Workflow?.Name ?? "", contactId = run.ContactId,
                    contactName = run.Contact?.Name ?? "", contactPhone = run.Contact?.Phone ?? "",
                    status = run.Status, currentNode = run.CurrentNode,
                    currentLabel = Label(labels, run.WorkflowId, run.CurrentNode),
                    waitUntil = Utc(run.WaitUntil), waitFor = run.WaitFor,
                    startedAt = Utc(run.StartedAt), updatedAt = Utc(run.UpdatedAt),
                    finishedAt = Utc(run.FinishedAt), detail = run.Detail,
                    lastReply = CtxStr("last_reply"), answered = CtxStr("answered") is "true" or "True",
                },
                path = events.Select(e => new
                {
                    at = Utc(e.At), day = e.Day, nodeId = e.NodeId,
                    label = Label(labels, run.WorkflowId, e.NodeId), kind = e.Kind, detail = e.Detail,
                }),
                log = run.Log,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.view");

        // ---- per-step statistics for one flow ---------------------------------
        app.MapGet("/api/flows/{flowId}/stats", async (string flowId, string? days, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var wf = await FindWorkflowAsync(db, sc, me, flowId, entity);
            if (wf == null) return Results.Json(new { detail = "No such flow." }, statusCode: 404);
            return Results.Ok(await StatsAsync(db, sc, me, wf, ParseDays(days), entity));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.view");

        // ---- the Analytics page's "Flows" card ---------------------------------
        app.MapGet("/api/analytics/flows", async (string? days, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            return Results.Ok(await FlowsOverviewAsync(db, sc, me, ParseDays(days), entity));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.view");

        // ---- manual start ("Manual / API" trigger) -----------------------------
        app.MapPost("/api/flows/{flowId}/start", async (string flowId, StartRequest body, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext http, FlowEngine engine) =>
        {
            if (await Denied(db, me, "flows.manage") is IResult no) return no;
            var wf = await FindWorkflowAsync(db, sc, me, flowId, entity);
            if (wf == null) return Results.Json(new { detail = "No such flow." }, statusCode: 404);
            var ids = (body.ContactIds ?? new()).Distinct().Take(500).ToList();
            if (ids.Count == 0) return Results.Json(new { detail = "Pick at least one contact." }, statusCode: 400);
            // only contacts the caller may see
            if (await ContactScopeAsync(db, sc, me, entity) is IQueryable<int> scope)
                ids = await scope.Where(id => ids.Contains(id)).ToListAsync();
            var tracked = await db.Workflows.FirstAsync(w => w.Id == wf.Id);
            var (started, skipped) = engine.StartManual(tracked, ids);
            skipped += (body.ContactIds ?? new()).Distinct().Count() - ids.Count;
            Audit(db, me, http, "flow.start", wf.FlowId, $"{wf.Name}: started {started}, skipped {skipped}");
            return Results.Ok(new { started, skipped });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- queue inspection + manual enqueue ---------------------------------
        app.MapGet("/api/flows/queue", async (AppDbContext db) =>
        {
            var rows = await db.OutboundJobs.AsNoTracking()
                .OrderByDescending(j => j.CreatedAt).Take(200).ToListAsync();
            return Results.Ok(new { jobs = rows.Select(j => new
            {
                id = j.Id, channel = j.Channel, kind = j.Kind, toAddress = j.ToAddress,
                status = j.Status, attempts = j.Attempts, notBefore = j.NotBefore,
                sentAt = j.SentAt, lastError = j.LastError, createdAt = j.CreatedAt,
            }) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage");

        app.MapPost("/api/flows/enqueue", (EnqueueRequest body, QueueService queue) =>
        {
            var job = queue.Enqueue(body.Channel, body.Kind, body.ToAddress,
                body.Payload ?? new(), body.ContactId, body.AccountId);
            return Results.Ok(new { id = job.Id, status = job.Status });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage")
          .OrgWideAction();   // a raw job to any address on any account — not confined to an entity

        // ---- manual engine tick (resume + triggers + drain) --------------------
        app.MapPost("/api/flows/tick", (ChatBranch.Data.Flows.FlowEngine engine) =>
            Results.Ok(engine.Tick())).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage")
            .OrgWideAction();   // the engine pass runs every tenant's journeys
    }

    // ======================= statistics ======================================

    private record EventRow(int RunId, string NodeId, string Kind, string Detail, string Day);
    private record RunRow(int Id, int WorkflowId, string Status, DateTime StartedAt, DateTime? FinishedAt);

    private static async Task<object> StatsAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        Workflow wf, int days, string? entity)
    {
        var zone = OrgClock.Zone(sc);
        var dayKeys = OrgClock.LastDays(days, zone);
        var sinceUtc = OrgClock.ToUtc(DateTime.Parse(dayKeys[0]), zone);
        var scope = await ContactScopeAsync(db, sc, me, entity);

        var evq = db.FlowStepEvents.AsNoTracking()
            .Where(e => e.WorkflowId == wf.FlowId && dayKeys.Contains(e.Day));
        if (scope != null) evq = evq.Where(e => e.ContactId != null && scope.Contains(e.ContactId.Value));
        var events = await evq.Select(e => new EventRow(e.RunId, e.NodeId, e.Kind, e.Detail, e.Day))
            .ToListAsync();

        var runq = db.FlowRuns.AsNoTracking().Where(r => r.WorkflowId == wf.Id);
        if (scope != null) runq = runq.Where(r => r.ContactId != null && scope.Contains(r.ContactId.Value));
        var live = await runq.Where(r => r.Status == FlowRunStatus.Waiting || r.Status == FlowRunStatus.Running)
            .Select(r => new { r.CurrentNode, r.Status }).ToListAsync();
        var windowRuns = await runq.Where(r => r.StartedAt >= sinceUtc
                                               || (r.FinishedAt != null && r.FinishedAt >= sinceUtc))
            .Select(r => new RunRow(r.Id, r.WorkflowId, r.Status, r.StartedAt, r.FinishedAt)).ToListAsync();

        int Distinct(IEnumerable<EventRow> rows, string kind) =>
            rows.Where(e => e.Kind == kind).Select(e => e.RunId).Distinct().Count();

        var nodes = ParseNodes(wf);
        var byNode = events.GroupBy(e => e.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        var waitingNow = live.Where(r => r.Status == FlowRunStatus.Waiting)
            .GroupBy(r => r.CurrentNode).ToDictionary(g => g.Key, g => g.Count());
        var nodeStats = new Dictionary<string, object>();
        var entered = new Dictionary<string, int>();
        var completed = new Dictionary<string, int>();
        foreach (var id in nodes.Select(n => n.Id).Concat(byNode.Keys).Distinct())
        {
            var rows = byNode.GetValueOrDefault(id) ?? new List<EventRow>();
            entered[id] = Distinct(rows, FlowStepKind.Entered);
            completed[id] = Distinct(rows, FlowStepKind.Completed);
            nodeStats[id] = new
            {
                entered = entered[id],
                completed = completed[id],
                waiting = Distinct(rows, FlowStepKind.Waiting),
                waitingNow = waitingNow.GetValueOrDefault(id),
                failed = Distinct(rows, FlowStepKind.Failed),
                skipped = rows.Count(e => e.Kind == FlowStepKind.Skipped),
                followups = rows.Count(e => e.Kind == FlowStepKind.FollowUp),
            };
        }

        // BFS order from the triggers, each step measured against the step
        // that leads into it: drop-off = of the people who FINISHED the step
        // before, the share that did not arrive here (people still waiting
        // there have not dropped off; on a branch it is the other side's share).
        var order = new List<object>();
        var dropoff = new List<object>();
        var edges = ParseEdges(wf);
        var seen = new HashSet<string>();
        var queue = new Queue<(string id, string? parent)>();
        foreach (var t in nodes.Where(n => n.Type == "trigger")) queue.Enqueue((t.Id, null));
        var nodeById = nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        while (queue.Count > 0)
        {
            var (id, parent) = queue.Dequeue();
            if (!seen.Add(id) || !nodeById.TryGetValue(id, out var meta)) continue;
            var from = parent != null ? completed.GetValueOrDefault(parent) : (int?)null;
            var here = entered.GetValueOrDefault(id);
            order.Add(new
            {
                nodeId = id, label = meta.Label, type = meta.Type, parent, entered = here,
                dropPct = from is int f && f > 0 ? Math.Round(100.0 * Math.Max(0, f - here) / f, 1) : (double?)null,
            });
            var done = completed.GetValueOrDefault(id);
            foreach (var e in edges.Where(e => e.Source == id))
            {
                var to = entered.GetValueOrDefault(e.Target);
                dropoff.Add(new
                {
                    from = id, to = e.Target, branch = e.Branch, fromCount = done, toCount = to,
                    dropPct = done > 0 ? Math.Round(100.0 * Math.Max(0, done - to) / done, 1) : (double?)null,
                });
                queue.Enqueue((e.Target, id));
            }
        }

        var perDay = DayCounts(windowRuns, zone);
        var enteredByDay = events.Where(e => e.Kind == FlowStepKind.Entered).GroupBy(e => e.Day)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => e.NodeId)
                .ToDictionary(n => n.Key, n => n.Select(e => e.RunId).Distinct().Count()));
        var series = dayKeys.Select(day =>
        {
            var c = perDay.GetValueOrDefault(day);
            return new
            {
                day, started = c.Started, completed = c.Completed, failed = c.Failed,
                nodes = enteredByDay.GetValueOrDefault(day) ?? new Dictionary<string, int>(),
            };
        }).ToList();

        object TopReasons(string kind) => events.Where(e => e.Kind == kind)
            .GroupBy(e => (e.NodeId, e.Detail))
            .Select(g => new { nodeId = g.Key.NodeId, label = nodeById.GetValueOrDefault(g.Key.NodeId)?.Label ?? "",
                reason = g.Key.Detail == "" ? "(no reason recorded)" : g.Key.Detail, count = g.Count() })
            .OrderByDescending(x => x.count).Take(8).ToList();

        return new
        {
            flowId = wf.FlowId, name = wf.Name, active = wf.Active, days,
            from = dayKeys[0], to = dayKeys[^1],
            totals = new
            {
                started = windowRuns.Count(r => r.StartedAt >= sinceUtc),
                completed = windowRuns.Count(r => r.Status == FlowRunStatus.Done && r.FinishedAt >= sinceUtc),
                failed = windowRuns.Count(r => r.Status == FlowRunStatus.Failed && r.FinishedAt >= sinceUtc),
                waitingNow = live.Count(r => r.Status == FlowRunStatus.Waiting),
                runningNow = live.Count(r => r.Status == FlowRunStatus.Running),
                followupsSent = events.Count(e => e.Kind == FlowStepKind.FollowUp),
                repliedAfterFollowUp = events.Count(e => e.Kind == FlowStepKind.Completed
                                                         && e.Detail.StartsWith("replied after follow-up")),
            },
            nodes = nodeStats,
            order,
            dropoff,
            series,
            failures = TopReasons(FlowStepKind.Failed),
            skips = TopReasons(FlowStepKind.Skipped),
        };
    }

    private static async Task<object> FlowsOverviewAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int days, string? entity)
    {
        var zone = OrgClock.Zone(sc);
        var dayKeys = OrgClock.LastDays(days, zone);
        var sinceUtc = OrgClock.ToUtc(DateTime.Parse(dayKeys[0]), zone);
        var entityId = await InboxEndpoints.EntityId(db, entity);
        // the library rule: a bound agent sees their subtrees + ancestors + global flows only
        var flows = await ShellScope.ScopeLibrary(db.Workflows.AsNoTracking(), db, sc, me, entityId)
            .OrderBy(w => w.Position).ThenBy(w => w.CreatedAt).ToListAsync();
        var flowIds = flows.Select(f => f.Id).ToList();
        var scope = await ContactScopeAsync(db, sc, me, entity);
        var runq = db.FlowRuns.AsNoTracking().Where(r => flowIds.Contains(r.WorkflowId));
        if (scope != null) runq = runq.Where(r => r.ContactId != null && scope.Contains(r.ContactId.Value));
        var runs = await runq.Where(r => r.StartedAt >= sinceUtc || (r.FinishedAt != null && r.FinishedAt >= sinceUtc))
            .Select(r => new RunRow(r.Id, r.WorkflowId, r.Status, r.StartedAt, r.FinishedAt)).ToListAsync();
        var inProgress = (await runq.Where(r => r.Status == FlowRunStatus.Waiting || r.Status == FlowRunStatus.Running)
                .Select(r => r.WorkflowId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        var rows = flows.Select(f =>
        {
            var mine = runs.Where(r => r.WorkflowId == f.Id).ToList();
            var cohort = mine.Where(r => r.StartedAt >= sinceUtc).ToList();
            var perDay = DayCounts(mine, zone);
            return new
            {
                flowId = f.FlowId, name = f.Name, active = f.Active,
                entered = cohort.Count,
                reachedEnd = cohort.Count(r => r.Status == FlowRunStatus.Done),
                completed = mine.Count(r => r.Status == FlowRunStatus.Done && r.FinishedAt >= sinceUtc),
                failed = mine.Count(r => r.Status == FlowRunStatus.Failed && r.FinishedAt >= sinceUtc),
                inProgress = inProgress.GetValueOrDefault(f.Id),
                series = dayKeys.Select(day =>
                {
                    var c = perDay.GetValueOrDefault(day);
                    return new { day, entered = c.Started, completed = c.Completed, failed = c.Failed };
                }).ToList(),
            };
        }).OrderByDescending(r => r.active).ThenByDescending(r => r.entered).ToList();
        return new { days, from = dayKeys[0], to = dayKeys[^1], flows = rows };
    }

    // ======================= helpers =========================================

    private record struct DayCount(int Started, int Completed, int Failed);

    /// <summary>Runs started / finished / failed per org-local day, in one pass.</summary>
    private static Dictionary<string, DayCount> DayCounts(IEnumerable<RunRow> runs, TimeZoneInfo zone)
    {
        var outv = new Dictionary<string, DayCount>();
        void Bump(string day, Func<DayCount, DayCount> f) => outv[day] = f(outv.GetValueOrDefault(day));
        foreach (var r in runs)
        {
            Bump(OrgClock.Day(r.StartedAt, zone), c => c with { Started = c.Started + 1 });
            if (r.FinishedAt is not DateTime fin) continue;
            if (r.Status == FlowRunStatus.Done) Bump(OrgClock.Day(fin, zone), c => c with { Completed = c.Completed + 1 });
            if (r.Status == FlowRunStatus.Failed) Bump(OrgClock.Day(fin, zone), c => c with { Failed = c.Failed + 1 });
        }
        return outv;
    }

    private record NodeMeta(string Id, string Type, string Label);
    private record EdgeMeta(string Source, string Target, string Branch);

    private static List<NodeMeta> ParseNodes(Workflow wf) => wf.Nodes
        .Select(o => JsonSerializer.SerializeToElement(o))
        .Where(e => e.ValueKind == JsonValueKind.Object)
        .Select(e => new NodeMeta(Prop(e, "id"), Prop(e, "type"),
            e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? Prop(d, "label") : ""))
        .Where(n => n.Id != "").ToList();

    private static List<EdgeMeta> ParseEdges(Workflow wf) => wf.Edges
        .Select(o => JsonSerializer.SerializeToElement(o))
        .Where(e => e.ValueKind == JsonValueKind.Object)
        .Select(e => new EdgeMeta(Prop(e, "source"), Prop(e, "target"), Prop(e, "sourceHandle")))
        .Where(e => e.Source != "" && e.Target != "").ToList();

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static async Task<Dictionary<int, Dictionary<string, string>>> NodeLabelsAsync(AppDbContext db,
        IEnumerable<int> workflowIds)
    {
        var ids = workflowIds.Distinct().ToList();
        var flows = await db.Workflows.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync();
        return flows.ToDictionary(w => w.Id, w => ParseNodes(w)
            .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Label));
    }

    private static string Label(Dictionary<int, Dictionary<string, string>> labels, int workflowId, string nodeId) =>
        labels.TryGetValue(workflowId, out var map) && map.TryGetValue(nodeId, out var l) ? l : "";

    private static async Task<Workflow?> FindWorkflowAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string flowId, string? entity)
    {
        var entityId = await InboxEndpoints.EntityId(db, entity);
        var q = ShellScope.ScopeLibrary(db.Workflows.AsNoTracking(), db, sc, me, entityId);
        var hit = await q.Where(w => w.FlowId == flowId)
            .OrderByDescending(w => w.EntityId != null).FirstOrDefaultAsync();
        if (hit == null && int.TryParse(flowId, out var pk)) hit = await q.FirstOrDefaultAsync(w => w.Id == pk);
        return hit;
    }

    /// <summary>
    /// The contacts the caller may count, as a subquery (null = no narrowing):
    /// their tenant (superadmin: all), their assigned entities' subtrees for
    /// agents/custom roles (none assigned = the tenant view, as in contacts),
    /// and ?entity= on top — which can only narrow, never widen.
    /// </summary>
    private static async Task<IQueryable<int>?> ContactScopeAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? entity)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        IQueryable<Contact> qs = db.Contacts.AsNoTracking();
        var narrowed = false;
        if (role != Roles.SuperAdmin && int.TryParse(me.FindFirstValue("tenant_id"), out var tid))
        {
            qs = qs.Where(c => c.TenantId == tid);
            narrowed = true;
        }
        {
            var assigned = ShellScope.ScopeRoots(db, me);   // company admins + agents
            if (assigned.Count > 0)
            {
                var ids = sc.EntitySubtreeIds(assigned);
                qs = ContactsEndpoints.InEntities(qs, ids);
                narrowed = true;
            }
        }
        if (await InboxEndpoints.EntityId(db, entity) is int eid)
        {
            var sub = sc.PickSubtree(eid);
            qs = qs.Where(c => c.EntityLinks.Any(l => sub.Contains(l.EntityId)));
            narrowed = true;
        }
        return narrowed ? qs.Select(c => c.Id) : null;
    }

    private static async Task<IResult?> Denied(AppDbContext db, ClaimsPrincipal me, string capability)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        return await AdminEndpoints.HasCapabilityAsync(db, role, capability)
            ? null
            : Results.Json(new { detail = $"Your role lacks the {capability} permission." }, statusCode: 403);
    }

    private static int ParseDays(string? raw)
    {
        if (string.Equals(raw, "today", StringComparison.OrdinalIgnoreCase)) return 1;
        return int.TryParse(raw, out var d) ? Math.Clamp(d, 1, 365) : 7;
    }

    private static string? Utc(DateTime? dt) =>
        dt is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o") : null;

    private static string Json(List<object?> value) => JsonSerializer.Serialize(value);

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext http, string action,
        string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = action, Target = target,
                Detail = detail.Length > 500 ? detail[..500] : detail,
                Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the save itself */ }
    }

    /// <summary>The builder autosaves every pause in typing, so plain edits by
    /// one person coalesce into one "flow.edit" row per 10 minutes instead of
    /// flooding the activity log.</summary>
    private static void AuditEdit(AppDbContext db, ClaimsPrincipal me, HttpContext http, string target,
        List<string> names)
    {
        try
        {
            var actor = me.Identity?.Name ?? "system";
            var since = DateTime.UtcNow.AddMinutes(-10);
            var recent = db.AuditLogs.Where(a => a.Actor == actor && a.Action == "flow.edit"
                                                && a.Target == target && a.CreatedAt >= since)
                .OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            if (recent == null) { Audit(db, me, http, "flow.edit", target, string.Join(", ", names)); return; }
            var merged = recent.Detail.Split(", ", StringSplitOptions.RemoveEmptyEntries).Concat(names).Distinct();
            var detail = string.Join(", ", merged);
            recent.Detail = detail.Length > 500 ? detail[..500] : detail;
            recent.CreatedAt = DateTime.UtcNow;
            db.SaveChanges();
        }
        catch { }
    }

    private static object WorkflowOut(Workflow w) => new
    {
        id = w.Id, flowId = w.FlowId, name = w.Name, hint = w.Hint,
        nodes = w.Nodes, edges = w.Edges, active = w.Active, position = w.Position,
        entityId = w.EntityId, liveSince = Utc(w.LiveSince), updatedAt = Utc(w.UpdatedAt),
        updatedBy = w.UpdatedBy,
    };
}
