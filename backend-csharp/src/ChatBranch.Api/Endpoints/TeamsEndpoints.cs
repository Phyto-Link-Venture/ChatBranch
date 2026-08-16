using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Teams — a group of agents conversations are routed to, and the round-robin
/// that hands each new one to a member.
///
/// Assignment is TWO-LEVEL, like Chatwoot's: a thread belongs to a team (who
/// owns it) and optionally to one agent inside that team (who is working it).
/// Losing the agent never loses the thread — it falls back to the team's queue.
/// </summary>
public static class TeamsEndpoints
{
    public static void MapTeamsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- list -----------------------------------------------------------
        app.MapGet("/api/teams", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await sc.ScopeInherited(
                    db.Teams.AsNoTracking().Include(t => t.Entity)
                        .Include(t => t.Members).ThenInclude(m => m.User), entityId)
                .OrderBy(t => t.Name).ToListAsync();
            var loads = await ThreadLoadsAsync(db);
            return Results.Ok(new { teams = rows.Select(t => Payload(t, loads)).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- create ---------------------------------------------------------
        app.MapPost("/api/teams", async (JsonElement body, AppDbContext db, ClaimsPrincipal me,
            ScopingService sc) =>
        {
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0)
                return Results.Json(new { detail = "name is required." }, statusCode: 400);
            var entityId = await InboxEndpoints.EntityId(db, Str(body, "entity"));
            var row = new Team
            {
                Name = name,
                Description = (Str(body, "description") ?? "").Trim(),
                AutoAssign = Bool(body, "auto_assign") ?? true,
                EntityId = entityId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Teams.Add(row);
            await db.SaveChangesAsync();
            await SyncMembersAsync(db, row, body);
            await Audit(db, me, "team.create", row.Name);
            return Results.Json(Payload(await ReloadAsync(db, row.Id), await ThreadLoadsAsync(db)),
                                statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- update ---------------------------------------------------------
        app.MapMethods("/api/teams/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.Teams.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Team not found." });

            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0)
                    return Results.Json(new { detail = "name cannot be empty." }, statusCode: 400);
                row.Name = name;
            }
            if (Has(body, "description")) row.Description = (Str(body, "description") ?? "").Trim();
            if (Bool(body, "auto_assign") is bool auto) row.AutoAssign = auto;
            if (Has(body, "entity"))
                row.EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity"));
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SyncMembersAsync(db, row, body);
            await Audit(db, me, "team.update", row.Name);
            return Results.Ok(Payload(await ReloadAsync(db, row.Id), await ThreadLoadsAsync(db)));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- delete ---------------------------------------------------------
        app.MapDelete("/api/teams/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.Teams.FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Team not found." });
            // Threads keep existing; the FK is SetNull, so they return to the
            // unassigned queue rather than disappearing with the team.
            var owned = await db.Threads.CountAsync(t => t.TeamId == id);
            db.TeamMembers.RemoveRange(db.TeamMembers.Where(m => m.TeamId == id));
            db.Teams.Remove(row);
            await db.SaveChangesAsync();
            await Audit(db, me, "team.delete", row.Name,
                        owned > 0 ? $"{owned} conversation(s) returned to unassigned" : "");
            return Results.Ok(new { ok = true, released = owned });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- assign a conversation to a team --------------------------------
        // `team_id` null/0 unassigns. Passing assign=true also picks the next
        // member by round-robin, which is what the inbox's "Assign" does.
        app.MapPost("/api/teams/assign", async (JsonElement body, AppDbContext db,
            ClaimsPrincipal me, ScopingService sc) =>
        {
            var channel = (Str(body, "channel") ?? "").Trim();
            var key = (Str(body, "key") ?? "").Trim();
            var thread = await db.Threads.FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });

            var teamId = Int(body, "team_id");
            if (teamId is null or 0)
            {
                thread.TeamId = null;
            }
            else
            {
                var team = await db.Teams.Include(t => t.Members).ThenInclude(m => m.User)
                    .FirstOrDefaultAsync(t => t.Id == teamId);
                if (team == null) return Results.Json(new { detail = "Team not found." }, statusCode: 400);
                thread.TeamId = team.Id;
                if (Bool(body, "assign") == true)
                    thread.Assignee = await NextAssigneeAsync(db, team) ?? thread.Assignee;
            }
            thread.UpdatedAt = DateTime.UtcNow;   // keeps the SSE stream fresh
            await db.SaveChangesAsync();
            await Audit(db, me, "thread.team", $"{thread.Channel}:{thread.Key}",
                        thread.TeamId == null ? "unassigned" : $"team #{thread.TeamId}");
            return Results.Ok(new { ok = true, team_id = thread.TeamId, assignee = thread.Assignee });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- round robin --------------------------------------------------------

    /// <summary>
    /// The next member in rotation, or null when the team has no members. The
    /// cursor is stored on the team (LastAssignee) so a restart does not always
    /// hand the next conversation to the same person.
    /// </summary>
    public static async Task<string?> NextAssigneeAsync(AppDbContext db, Team team)
    {
        var members = await db.TeamMembers.AsNoTracking().Include(m => m.User)
            .Where(m => m.TeamId == team.Id && m.User != null && m.User.IsActive)
            .OrderBy(m => m.Id)
            .Select(m => m.User!.Username)
            .ToListAsync();
        if (members.Count == 0) return null;

        var last = members.IndexOf(team.LastAssignee);
        var next = members[(last + 1) % members.Count];   // last == -1 -> first member

        var tracked = await db.Teams.FirstOrDefaultAsync(t => t.Id == team.Id);
        if (tracked != null)
        {
            tracked.LastAssignee = next;
            tracked.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return next;
    }

    /// <summary>
    /// Route a conversation that has no team yet: the entity's own auto-assign
    /// team wins, then an organization-wide one. Silent no-op when nothing
    /// matches — auto-assignment is a convenience, never a gate on delivery.
    /// </summary>
    public static async Task AutoAssignAsync(AppDbContext db, Thread thread)
    {
        try
        {
            if (thread.TeamId != null) return;
            var team = await db.Teams.Include(t => t.Members)
                           .FirstOrDefaultAsync(t => t.AutoAssign && t.EntityId == thread.EntityId)
                       ?? await db.Teams.Include(t => t.Members)
                           .FirstOrDefaultAsync(t => t.AutoAssign && t.EntityId == null);
            if (team == null) return;
            thread.TeamId = team.Id;
            if (thread.Assignee.Length == 0)
                thread.Assignee = await NextAssigneeAsync(db, team) ?? "";
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch { /* routing never breaks an inbound webhook */ }
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<Team> ReloadAsync(AppDbContext db, int id) =>
        await db.Teams.AsNoTracking().Include(t => t.Entity)
            .Include(t => t.Members).ThenInclude(m => m.User)
            .FirstAsync(t => t.Id == id);

    /// <summary>Open conversations per team — the "how loaded is this team" number.</summary>
    private static async Task<Dictionary<int, int>> ThreadLoadsAsync(AppDbContext db) =>
        await db.Threads.AsNoTracking()
            .Where(t => t.TeamId != null && t.Status == "open")
            .GroupBy(t => t.TeamId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

    private static object Payload(Team t, Dictionary<int, int> loads) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        auto_assign = t.AutoAssign,
        entity = t.Entity?.Slug,
        entity_name = t.Entity?.Name ?? "",
        members = t.Members.Where(m => m.User != null)
            .Select(m => new { user_id = m.UserId, username = m.User!.Username }).ToList(),
        open_conversations = loads.GetValueOrDefault(t.Id),
        created_at = t.CreatedAt,
    };

    /// <summary>Replace the membership list when the body carries `member_ids`.</summary>
    private static async Task SyncMembersAsync(AppDbContext db, Team team, JsonElement body)
    {
        if (!Has(body, "member_ids")) return;
        var wanted = IntList(body, "member_ids");
        var valid = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .Select(u => u.Id).ToListAsync();
        var current = await db.TeamMembers.Where(m => m.TeamId == team.Id).ToListAsync();
        db.TeamMembers.RemoveRange(current.Where(m => !valid.Contains(m.UserId)));
        foreach (var uid in valid.Where(id => current.All(m => m.UserId != id)))
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id, UserId = uid, CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    private static async Task Audit(AppDbContext db, ClaimsPrincipal me, string action,
        string target, string detail = "")
    {
        db.AuditLogs.Add(new AuditLog
        {
            Actor = me.Identity?.Name ?? "system", Action = action, Target = target,
            Detail = detail, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
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
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
                JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : null,
                _ => null,
            }
            : null;

    private static List<int> IntList(JsonElement b, string key)
    {
        var outv = new List<int>();
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(key, out var v)) return outv;
        if (v.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in v.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) outv.Add(n);
            else if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var m)) outv.Add(m);
        return outv;
    }
}
