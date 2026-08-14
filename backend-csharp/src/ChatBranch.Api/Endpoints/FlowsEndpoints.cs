using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// flows surface (/api/flows/*): the workflow library sync (GET/PUT, admin+ —
/// PUT prunes within a scope), FlowRun + queue inspection, a manual enqueue, and
/// the manual drain seam (POST /tick). The library PUT ignores agentSkills (the
/// aicore endpoints are the sole writer of crm.AgentSkill).
/// </summary>
public static class FlowsEndpoints
{
    public record WorkflowDto(string FlowId, string Name, string? Hint,
        List<object?>? Nodes, List<object?>? Edges, bool Active, int Position);
    public record LibraryWrite(string? Entity, List<WorkflowDto> Workflows);
    public record EnqueueRequest(string Channel, string Kind, string ToAddress,
        Dictionary<string, object?>? Payload, int? ContactId, int? AccountId);

    public static void MapFlowsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- workflow library --------------------------------------------------
        app.MapGet("/api/flows/library", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = sc.ScopeInherited(db.Workflows.AsNoTracking(), entityId)
                .OrderBy(w => w.Position).ThenBy(w => w.CreatedAt).ToList();
            return Results.Ok(new { workflows = rows.Select(WorkflowOut) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/flows/library", async (LibraryWrite body, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            var existing = await db.Workflows.Where(w => w.EntityId == entityId).ToListAsync();
            var keep = new HashSet<string>();
            foreach (var w in body.Workflows)
            {
                keep.Add(w.FlowId);
                var row = existing.FirstOrDefault(e => e.FlowId == w.FlowId);
                if (row == null)
                {
                    row = new Workflow { FlowId = w.FlowId, EntityId = entityId, CreatedAt = DateTime.UtcNow };
                    db.Workflows.Add(row);
                }
                row.Name = w.Name; row.Hint = w.Hint ?? "";
                row.Nodes = w.Nodes ?? new(); row.Edges = w.Edges ?? new();
                row.Active = w.Active; row.Position = w.Position; row.UpdatedAt = DateTime.UtcNow;
            }
            // PRUNE within this scope only (mirrors the builder's library sync).
            db.Workflows.RemoveRange(existing.Where(e => !keep.Contains(e.FlowId)));
            await db.SaveChangesAsync();
            return Results.Ok(new { count = body.Workflows.Count });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- runs + queue inspection ------------------------------------------
        app.MapGet("/api/flows/runs", async (AppDbContext db) =>
        {
            var rows = await db.FlowRuns.AsNoTracking()
                .OrderByDescending(r => r.StartedAt).Take(200).ToListAsync();
            return Results.Ok(new { runs = rows.Select(r => new
            {
                id = r.Id, workflowId = r.WorkflowId, contactId = r.ContactId,
                status = r.Status, currentNode = r.CurrentNode, waitUntil = r.WaitUntil,
                waitFor = r.WaitFor, startedAt = r.StartedAt,
            }) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/flows/enqueue", (EnqueueRequest body, QueueService queue) =>
        {
            var job = queue.Enqueue(body.Channel, body.Kind, body.ToAddress,
                body.Payload ?? new(), body.ContactId, body.AccountId);
            return Results.Ok(new { id = job.Id, status = job.Status });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- manual engine tick (resume + triggers + drain) --------------------
        app.MapPost("/api/flows/tick", (ChatBranch.Data.Flows.FlowEngine engine) =>
            Results.Ok(engine.Tick())).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    private static object WorkflowOut(Workflow w) => new
    {
        id = w.Id, flowId = w.FlowId, name = w.Name, hint = w.Hint,
        nodes = w.Nodes, edges = w.Edges, active = w.Active, position = w.Position,
        entityId = w.EntityId,
    };
}
