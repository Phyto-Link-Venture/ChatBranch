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
            return Results.Ok(new { rules = rows.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("flows.manage");

        // ---- create ----------------------------------------------------------
        app.MapPost("/api/automations", async (JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
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

            var row = new AutomationRule
            {
                Name = name,
                Description = (Str(body, "description") ?? "").Trim(),
                Event = evt,
                Active = Bool(body, "active") ?? true,
                Position = Int(body, "position") ?? 0,
                Conditions = JsonList(body, "conditions"),
                Actions = JsonList(body, "actions"),
                EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity")),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.AutomationRules.Add(row);
            await Audit(db, me, "automation.create", row.Name);
            await db.SaveChangesAsync();
            return Results.Json(Payload(row), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- update ----------------------------------------------------------
        app.MapMethods("/api/automations/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Rule not found." });
            if (ValidationError(body) is IResult err) return err;

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
            if (Has(body, "entity")) row.EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity"));
            row.UpdatedAt = DateTime.UtcNow;
            await Audit(db, me, "automation.update", row.Name);
            await db.SaveChangesAsync();
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- delete ----------------------------------------------------------
        app.MapDelete("/api/automations/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Rule not found." });
            db.AutomationRules.Remove(row);
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
            return Results.Ok(new { matches = automation.WouldMatch(row, thread, text) });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ---- helpers ------------------------------------------------------------

    private static object Payload(AutomationRule r) => new
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
