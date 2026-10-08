using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Per-user saved list views — the named filter tabs above the inbox
/// ("All · Mine · Unassigned · VIP drivers"). Every route works on the CALLER's
/// own rows only: another login's view is a 404, never a 403 that would confirm
/// it exists. The owner is resolved from the session, never taken from the body.
///
///   GET    /api/views?scope=chats           → {views:[…]} in tab order
///   POST   /api/views {scope,name,filters}  → the new view (appended last)
///   PATCH  /api/views/{id} {name?,filters?,position?}
///   DELETE /api/views/{id}
///   POST   /api/views/reorder {scope, ids:[…]} → positions follow the list
/// </summary>
public static class SavedViewsEndpoints
{
    private static readonly string[] Scopes = { "chats", "contacts" };
    private const int MaxName = 60;
    private const int MaxFilters = 8192;
    private const int MaxViewsPerScope = 50;

    public static void MapSavedViewsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/views", async (string? scope, AppDbContext db, ClaimsPrincipal me) =>
        {
            var uid = await UserIdAsync(db, me);
            if (uid is not int userId) return NoUser();
            var sc = NormScope(scope);
            if (sc == null) return BadScope();
            var rows = await db.SavedViews.AsNoTracking()
                .Where(v => v.UserId == userId && v.Scope == sc)
                .OrderBy(v => v.Position).ThenBy(v => v.Id).ToListAsync();
            return Results.Ok(new { views = rows.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/views", async (JsonElement body, AppDbContext db, ClaimsPrincipal me,
            HttpContext ctx) =>
        {
            var uid = await UserIdAsync(db, me);
            if (uid is not int userId) return NoUser();
            var sc = NormScope(Str(body, "scope") ?? "chats");
            if (sc == null) return BadScope();
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");
            if (name.Length > MaxName) return Bad($"name must be at most {MaxName} characters.");
            var filters = FiltersJson(body);
            if (filters == null) return Bad("filters must be a JSON object.");
            if (filters.Length > MaxFilters) return Bad("filters are too large.");

            var mine = db.SavedViews.Where(v => v.UserId == userId && v.Scope == sc);
            if (await mine.CountAsync() >= MaxViewsPerScope)
                return Bad($"At most {MaxViewsPerScope} saved views per list.");
            if (await mine.AnyAsync(v => v.Name.ToLower() == name.ToLower()))
                return Results.Json(new { detail = $"You already have a view called \"{name}\"." },
                    statusCode: 409);

            var row = new SavedView
            {
                UserId = userId,
                Scope = sc,
                Name = name,
                Filters = filters,
                Position = (await mine.MaxAsync(v => (int?)v.Position) ?? -1) + 1,
                CreatedAt = DateTime.UtcNow,
            };
            db.SavedViews.Add(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "view.create", $"{sc}:{row.Id}", name, userId);
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/views/{id:int}", new[] { "PATCH" }, async (int id, JsonElement body,
            AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var uid = await UserIdAsync(db, me);
            if (uid is not int userId) return NoUser();
            var row = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == id && v.UserId == userId);
            if (row == null) return Results.NotFound(new { detail = "View not found." });

            if (Str(body, "name") is string rawName)
            {
                var name = rawName.Trim();
                if (name.Length == 0) return Bad("name is required.");
                if (name.Length > MaxName) return Bad($"name must be at most {MaxName} characters.");
                if (await db.SavedViews.AnyAsync(v => v.UserId == userId && v.Scope == row.Scope
                        && v.Id != row.Id && v.Name.ToLower() == name.ToLower()))
                    return Results.Json(new { detail = $"You already have a view called \"{name}\"." },
                        statusCode: 409);
                row.Name = name;
            }
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("filters", out _))
            {
                var filters = FiltersJson(body);
                if (filters == null) return Bad("filters must be a JSON object.");
                if (filters.Length > MaxFilters) return Bad("filters are too large.");
                row.Filters = filters;
            }
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("position", out var p)
                && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pos))
                row.Position = Math.Max(0, pos);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "view.update", $"{row.Scope}:{row.Id}", row.Name, userId);
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/views/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me,
            HttpContext ctx) =>
        {
            var uid = await UserIdAsync(db, me);
            if (uid is not int userId) return NoUser();
            var row = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == id && v.UserId == userId);
            if (row == null) return Results.NotFound(new { detail = "View not found." });
            db.SavedViews.Remove(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "view.delete", $"{row.Scope}:{row.Id}", row.Name, userId);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/views/reorder", async (JsonElement body, AppDbContext db, ClaimsPrincipal me,
            HttpContext ctx) =>
        {
            var uid = await UserIdAsync(db, me);
            if (uid is not int userId) return NoUser();
            var sc = NormScope(Str(body, "scope") ?? "chats");
            if (sc == null) return BadScope();
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("ids", out var raw)
                || raw.ValueKind != JsonValueKind.Array)
                return Bad("ids must be a list of view ids.");
            var order = raw.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n
                    : int.TryParse(e.ToString(), out var s) ? s : -1)
                .Where(n => n > 0).Distinct().ToList();

            var rows = await db.SavedViews.Where(v => v.UserId == userId && v.Scope == sc)
                .OrderBy(v => v.Position).ThenBy(v => v.Id).ToListAsync();
            // Listed ids first in the given order; anything not listed (or not
            // the caller's) keeps its relative order after them.
            var ordered = order.Select(id => rows.FirstOrDefault(r => r.Id == id))
                .Where(r => r != null).Select(r => r!)
                .Concat(rows.Where(r => !order.Contains(r.Id))).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].Position = i;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "view.reorder", sc, string.Join(",", ordered.Select(r => r.Id)), userId);
            return Results.Ok(new { views = ordered.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- helpers ------------------------------------------------------------

    private static object Payload(SavedView v)
    {
        JsonElement filters;
        try { filters = JsonDocument.Parse(string.IsNullOrWhiteSpace(v.Filters) ? "{}" : v.Filters).RootElement.Clone(); }
        catch { filters = JsonDocument.Parse("{}").RootElement.Clone(); }
        return new
        {
            id = v.Id,
            scope = v.Scope,
            name = v.Name,
            filters,
            position = v.Position,
            created_at = DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc).ToString("o"),
        };
    }

    /// <summary>The caller's auth_user id: via the session's profile, else by username.</summary>
    private static async Task<int?> UserIdAsync(AppDbContext db, ClaimsPrincipal me)
    {
        if (int.TryParse(me.FindFirstValue("profile_id"), out var pid))
        {
            var uid = await db.Profiles.AsNoTracking().Where(p => p.Id == pid)
                .Select(p => (int?)p.UserId).FirstOrDefaultAsync();
            if (uid != null) return uid;
        }
        var name = me.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Username == name)
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
    }

    private static string? NormScope(string? scope)
    {
        var s = (scope ?? "chats").Trim().ToLowerInvariant();
        if (s.Length == 0) s = "chats";
        return Scopes.Contains(s) ? s : null;
    }

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;

    /// <summary>The body's `filters` as compact JSON text; missing = {}; non-object = null.</summary>
    private static string? FiltersJson(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("filters", out var f)
            || f.ValueKind == JsonValueKind.Null)
            return "{}";
        return f.ValueKind == JsonValueKind.Object ? f.GetRawText() : null;
    }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult BadScope() => Bad($"scope must be one of {string.Join(", ", Scopes)}.");
    private static IResult NoUser() =>
        Results.Json(new { detail = "Saved views need a signed-in user." }, statusCode: 403);

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail, int? userId = null)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail.Length > 200 ? detail[..200] : detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }
}
