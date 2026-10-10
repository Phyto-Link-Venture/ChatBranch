using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Automation rules: when something happens to a conversation, check conditions
/// and apply actions. The catalog endpoint hands the UI the exact vocabulary the
/// engine understands, so a rule can never be built out of options the engine
/// would silently ignore.
/// </summary>
public static class AutomationsEndpoints
{
    public static void MapAutomationsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- catalog: events, fields, operators, actions ---------------------
        app.MapGet("/api/automations/catalog", async (AppDbContext db) =>
        {
            var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name)
                .Select(t => new { id = t.Id, name = t.Name }).ToListAsync();
            var agents = await db.Profiles.AsNoTracking().Include(p => p.User)
                .Where(p => p.User != null && p.User.IsActive)
                .Select(p => new { username = p.User!.Username, role = p.Role }).ToListAsync();
            return Results.Ok(new
            {
                events = AutomationService.Events.Select(e => new { id = e.Id, label = e.Label }),
                fields = AutomationService.Fields.Select(f => new { id = f.Id, label = f.Label }),
                operators = AutomationService.Operators.Select(o => new { id = o.Id, label = o.Label }),
                actions = AutomationService.ActionTypes.Select(a => new { id = a.Id, label = a.Label }),
                statuses = new[] { "open", "pending", "snoozed", "resolved" },
                teams,
                agents,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage");

        // ---- list ------------------------------------------------------------
        app.MapGet("/api/automations", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await sc.ScopeInherited(
                    db.AutomationRules.AsNoTracking().Include(r => r.Entity), entityId)
                .OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync();
            var scopes = LibraryEntities.Load(db, rows);
            return Results.Ok(new { rules = rows.Select(r => Payload(r, scopes)).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage");

        // ---- create ----------------------------------------------------------
        app.MapPost("/api/automations", async (JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0)
                return Results.Json(new { detail = "name is required." }, statusCode: 400);
            var evt = (Str(body, "event") ?? "message_created").Trim();
            if (!AutomationService.Events.Any(e => e.Id == evt))
                return Results.Json(new
                {
                    detail = $"event must be one of {string.Join(", ", AutomationService.Events.Select(e => e.Id))}.",
                }, statusCode: 400);
            if (ValidationError(body) is IResult err) return err;
            // "Available to": entities [slug|id…] (or the older single `entity`); none = everyone
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (await LibraryScope.Check(db, sc, me, null, pick.Ids) is { } denied) return denied;

            var row = new AutomationRule
            {
                Name = name,
                Description = (Str(body, "description") ?? "").Trim(),
                Event = evt,
                Active = Bool(body, "active") ?? true,
                // a new rule runs LAST unless told otherwise (rules run in Position order)
                Position = Int(body, "position") ?? ((await db.AutomationRules.MaxAsync(r => (int?)r.Position) ?? -1) + 1),
                Conditions = JsonList(body, "conditions"),
                Actions = JsonList(body, "actions"),
                EntityId = pick.Ids.Count > 0 ? pick.Ids[0] : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.AutomationRules.Add(row);
            await Audit(db, me, "automation.create", row.Name);
            await db.SaveChangesAsync();
            LibraryEntities.Set(db, row, pick.Ids);
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(r => r.Entity).LoadAsync();
            return Results.Json(Payload(row, LibraryEntities.Load(db, new[] { row })), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- update ----------------------------------------------------------
        app.MapMethods("/api/automations/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var row = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Rule not found." });
            if (ValidationError(body) is IResult err) return err;
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (pick.Given)
            {
                var before = LibraryEntities.Ids(db, LibraryKinds.Automation, row.Id, row.EntityId);
                if (await LibraryScope.Check(db, sc, me, before, pick.Ids) is { } denied) return denied;
            }

            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0)
                    return Results.Json(new { detail = "name cannot be empty." }, statusCode: 400);
                row.Name = name;
            }
            if (Has(body, "description")) row.Description = (Str(body, "description") ?? "").Trim();
            if (Has(body, "event"))
            {
                var evt = (Str(body, "event") ?? "").Trim();
                if (!AutomationService.Events.Any(e => e.Id == evt))
                    return Results.Json(new { detail = "Unknown event." }, statusCode: 400);
                row.Event = evt;
            }
            if (Bool(body, "active") is bool active) row.Active = active;
            if (Int(body, "position") is int pos) row.Position = pos;
            if (Has(body, "conditions")) row.Conditions = JsonList(body, "conditions");
            if (Has(body, "actions")) row.Actions = JsonList(body, "actions");
            // entities [] / entity "" = everyone (organization-wide)
            if (pick.Given) LibraryEntities.Set(db, row, pick.Ids);
            row.UpdatedAt = DateTime.UtcNow;
            await Audit(db, me, "automation.update", row.Name);
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(r => r.Entity).LoadAsync();
            return Results.Ok(Payload(row, LibraryEntities.Load(db, new[] { row })));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- delete ----------------------------------------------------------
        app.MapDelete("/api/automations/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Rule not found." });
            db.AutomationRules.Remove(row);
            LibraryEntities.Clear(db, LibraryKinds.Automation, row.Id);
            await Audit(db, me, "automation.delete", row.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- dry run ----------------------------------------------------------
        // "Would this rule have fired on that conversation?" — answered without
        // touching anything, so an operator can check a rule before enabling it.
        app.MapPost("/api/automations/{id:int}/test", async (int id, JsonElement body,
            AppDbContext db, AutomationService automation) =>
        {
            var row = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Rule not found." });
            var channel = (Str(body, "channel") ?? "").Trim();
            var key = (Str(body, "key") ?? "").Trim();
            var thread = await db.Threads.AsNoTracking().Include(t => t.Contact)
                .FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key);
            if (thread == null)
                return Results.Json(new { detail = "Pick a conversation to test against." }, statusCode: 400);
            var text = Str(body, "message") ?? "";
            var checks = automation.Explain(row, thread, text);
            return Results.Ok(new
            {
                matches = automation.WouldMatch(row, thread, text),
                // condition by condition, so the Test button can say WHY
                conditions = checks.Select(c => new { field = c.Field, op = c.Op, value = c.Wanted, actual = c.Actual, ok = c.Ok }),
                @event = row.Event,
                active = row.Active,
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- reorder ----------------------------------------------------------
        // Rules run in this order (all matching rules run, top first): {ids:[…]}
        // puts the listed rules in that order WITHIN the places they already
        // hold, so rules the caller didn't list (another entity's, a filtered
        // view) keep their place; then everything is renumbered 0…n. Unknown ids
        // are ignored.
        app.MapPost("/api/automations/reorder", async (JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("ids", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Results.Json(new { detail = "ids must be a list of rule ids." }, statusCode: 400);
            var ids = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                .Select(x => x.GetInt32()).Distinct().ToList();
            var rows = await db.AutomationRules.ToListAsync();
            var listed = ids.Select(id => rows.FirstOrDefault(r => r.Id == id)).Where(r => r != null).Select(r => r!).ToList();
            var current = rows.OrderBy(r => r.Position).ThenBy(r => r.Id).ToList();
            var queue = new Queue<AutomationRule>(listed);
            var next = current.Select(r => listed.Contains(r) ? queue.Dequeue() : r).ToList();
            var pos = 0;
            foreach (var r in next)
            {
                if (r.Position != pos) { r.Position = pos; r.UpdatedAt = DateTime.UtcNow; }
                pos++;
            }
            await Audit(db, me, "automation.reorder", string.Join(", ", listed.Select(r => r.Name)));
            await db.SaveChangesAsync();
            var scopes = LibraryEntities.Load(db, rows);
            return Results.Ok(new { rules = rows.OrderBy(r => r.Position).Select(r => Payload(r, scopes)) });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ---- helpers ------------------------------------------------------------

    private static object Payload(AutomationRule r, Dictionary<int, List<EntityRef>> scopes) => new
    {
        id = r.Id,
        name = r.Name,
        description = r.Description,
        @event = r.Event,
        active = r.Active,
        position = r.Position,
        conditions = r.Conditions,
        actions = r.Actions,
        times_fired = r.TimesFired,
        last_fired_at = r.LastFiredAt,
        entity = r.Entity?.Slug,
        entity_name = r.Entity?.Name ?? "",
        // "Available to": every entity it was ticked for (primary first); global = everyone
        entities = LibraryEntities.Payload(scopes, r.Id),
        global = r.EntityId == null,
    };


    /// <summary>
    /// A rule with no actions can never do anything, and an unknown action type
    /// would be silently skipped at run time — reject both at write time so the
    /// operator finds out now rather than wondering why nothing happens.
    /// </summary>
    private static IResult? ValidationError(JsonElement body)
    {
        if (Has(body, "actions"))
        {
            var actions = JsonList(body, "actions");
            if (actions.Count == 0)
                return Results.Json(new { detail = "Add at least one action — a rule with none does nothing." },
                                    statusCode: 400);
            foreach (var raw in actions)
            {
                var type = raw is JsonElement el && el.ValueKind == JsonValueKind.Object
                           && el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type.Length > 0 && !AutomationService.ActionTypes.Any(a => a.Id == type))
                    return Results.Json(new { detail = $"Unknown action '{type}'." }, statusCode: 400);
            }
        }
        if (Has(body, "conditions"))
            foreach (var raw in JsonList(body, "conditions"))
            {
                if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Object) continue;
                var op = el.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
                if (op.Length > 0 && !AutomationService.Operators.Any(x => x.Id == op))
                    return Results.Json(new { detail = $"Unknown operator '{op}'." }, statusCode: 400);
            }
        return null;
    }

    private static List<object?> JsonList(JsonElement body, string key)
    {
        var outv = new List<object?>();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return outv;
        if (v.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in v.EnumerateArray()) outv.Add(el.Clone());
        return outv;
    }

    private static async Task Audit(AppDbContext db, ClaimsPrincipal me, string action, string target)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Actor = me.Identity?.Name ?? "system", Action = action, Target = target,
            CreatedAt = DateTime.UtcNow,
        });
        await Task.CompletedTask;
    }

    private static bool Has(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString(),
            }
            : null;

    private static bool? Bool(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static int? Int(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
