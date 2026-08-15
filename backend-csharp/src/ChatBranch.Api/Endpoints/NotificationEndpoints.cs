using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Notification + audit surface ported from <c>crm/views.py</c>
/// (<c>notifications</c>, <c>notification_prefs</c>, <c>audit_log</c>) and
/// <c>crm/auth.py</c> (<c>NOTIF_CATEGORIES</c>, <c>notification_prefs()</c>).
///
/// Three consumers, three shapes, all snake_case exactly as Django emits them:
///   * Settings → Notifications reads/writes the GLOBAL mute switches — one
///     Setting row per category, key <c>notif_&lt;category&gt;</c>, value
///     "false" = muted. Missing rows mean enabled and every read FAILS OPEN: a
///     broken Settings table must never decide that nobody gets paged.
///   * The header bell polls <c>GET /api/auth/notifications</c> (newest 20 +
///     unread count) and marks read with a POST on the SAME path, body
///     <c>{id: &lt;pk&gt; | "all"}</c>.
///   * The Activity page reads <c>GET /api/auth/audit</c> — superadmin sees
///     every actor, admin only their own trail (agents are 403'd by policy).
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>Mirrors <c>crm.auth.NOTIF_CATEGORIES</c> — the mute groups the
    /// settings page renders. Order is the page's display order.</summary>
    private static readonly string[] NotifCategories =
        { "messages", "account_health", "flows", "admin" };

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- global mute switches ---------------------------------------------

        // GET /api/auth/notification-prefs → {messages: true, account_health: …}
        app.MapGet("/api/auth/notification-prefs", (ScopingService scoping) =>
            Results.Ok(ReadPrefs(scoping)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // PUT /api/auth/notification-prefs {<category>: bool, …} (admin+)
        // Partial patch: only the categories present in the body are written.
        app.MapPut("/api/auth/notification-prefs", async (HttpContext ctx,
            AppDbContext db, ScopingService scoping, ClaimsPrincipal me) =>
        {
            var root = await ReadJson(ctx);
            var patch = new Dictionary<string, bool>();
            if (root is JsonElement body && body.ValueKind == JsonValueKind.Object)
                foreach (var cat in NotifCategories)
                {
                    if (!body.TryGetProperty(cat, out var val)) continue;
                    if (val.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Results.Json(new { detail = $"{cat} must be true or false." },
                            statusCode: 400);
                    patch[cat] = val.GetBoolean();
                }

            foreach (var (cat, enabled) in patch)
                try { scoping.SetSetting($"notif_{cat}", enabled ? "true" : "false"); }
                catch { /* never 500 a settings save */ }

            if (patch.Count > 0)
                await Audit(db, ctx, me, "settings.update", "notifications",
                    // Django writes f"keys: {sorted(patch)}" — keep the repr shape.
                    "keys: [" + string.Join(", ",
                        patch.Keys.OrderBy(k => k, StringComparer.Ordinal)
                            .Select(k => $"'{k}'")) + "]");

            return Results.Ok(ReadPrefs(scoping));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- per-user bell -----------------------------------------------------

        // GET /api/auth/notifications → {unread, notifications:[…20 newest]}
        app.MapGet("/api/auth/notifications", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            var userId = await CurrentUserId(db, me);
            if (userId is not int uid)
                return Results.Ok(new { unread = 0, notifications = Array.Empty<object>() });

            var rows = await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == uid)
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                .Take(20).ToListAsync();
            var unread = await db.Notifications.CountAsync(n => n.UserId == uid && !n.Read);

            return Results.Ok(new
            {
                unread,
                notifications = rows.Select(n => new
                {
                    id = n.Id,
                    kind = n.Kind,
                    title = n.Title,
                    body = n.Body,
                    link = n.Link,
                    read = n.Read,
                    created_at = n.CreatedAt,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST /api/auth/notifications {id: <pk> | "all"} → mark read.
        // The bell posts to the bare path (Django's method-dispatched view);
        // /read is an alias so either spelling works.
        app.MapPost("/api/auth/notifications", MarkRead)
            .RequireAuthorization(AuthPolicies.Authenticated);
        app.MapPost("/api/auth/notifications/read", MarkRead)
            .RequireAuthorization(AuthPolicies.Authenticated);

        // ---- audit trail -------------------------------------------------------

        // GET /api/auth/audit → {entries:[…50 newest]} (admin+; admins see only
        // their own actions, superadmin sees every actor).
        app.MapGet("/api/auth/audit", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var q = db.AuditLogs.AsNoTracking().AsQueryable();

            if (role != Roles.SuperAdmin)
            {
                var userId = await CurrentUserId(db, me);
                if (userId is not int uid)
                    return Results.Ok(new { entries = Array.Empty<object>() });
                q = q.Where(e => e.UserId == uid);
            }

            var rows = await q
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Take(50).ToListAsync();

            return Results.Ok(new
            {
                entries = rows.Select(e => new
                {
                    id = e.Id,
                    actor = e.Actor,
                    action = e.Action,
                    target = e.Target,
                    detail = e.Detail,
                    ip = e.Ip,
                    created_at = e.CreatedAt,
                }),
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ---- handlers shared by more than one route --------------------------------

    private static async Task<IResult> MarkRead(HttpContext ctx, AppDbContext db, ClaimsPrincipal me)
    {
        var userId = await CurrentUserId(db, me);
        if (userId is not int uid) return Results.Ok(new { ok = true, marked = 0 });

        var root = await ReadJson(ctx);
        int? onlyId = null;
        if (root is JsonElement body && body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("id", out var idVal))
        {
            if (idVal.ValueKind == JsonValueKind.Number && idVal.TryGetInt32(out var n))
                onlyId = n;
            else if (idVal.ValueKind == JsonValueKind.String &&
                     idVal.GetString() is string s && s != "all" && int.TryParse(s, out var parsed))
                onlyId = parsed;
            // null / "all" / anything else → mark every unread row, like Django.
        }

        var q = db.Notifications.Where(n => n.UserId == uid && !n.Read);
        if (onlyId is int pk) q = q.Where(n => n.Id == pk);

        var marked = 0;
        try { marked = await q.ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, true)); }
        catch { /* sandbox-safe: report 0 rather than 500 the bell */ }
        return Results.Ok(new { ok = true, marked });
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>
    /// The four mute switches, resolved from global Setting rows. Only the exact
    /// string "false" mutes; a missing row, a blank value or a read failure means
    /// ENABLED (fail-open, matching <c>crm.auth.notification_prefs</c>).
    /// </summary>
    private static Dictionary<string, bool> ReadPrefs(ScopingService scoping)
    {
        var prefs = NotifCategories.ToDictionary(c => c, _ => true);
        try
        {
            var values = scoping.SettingValues(NotifCategories.Select(c => $"notif_{c}"));
            foreach (var cat in NotifCategories)
                if (values.TryGetValue($"notif_{cat}", out var raw))
                    prefs[cat] = !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* fail open — a broken Settings table never mutes the bell */ }
        return prefs;
    }

    /// <summary>The caller's auth_user row id, or null when it can't be resolved
    /// (pre-seed X-Role bootstrap, deleted user). Callers answer empty, not 500.</summary>
    private static async Task<int?> CurrentUserId(AppDbContext db, ClaimsPrincipal me)
    {
        var username = me.Identity?.Name ?? "";
        if (string.IsNullOrEmpty(username)) return null;
        try
        {
            return await db.Users.AsNoTracking()
                .Where(u => u.Username == username)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
        }
        catch { return null; }
    }

    /// <summary>One audit row, with Django's column caps applied.</summary>
    private static async Task Audit(AppDbContext db, HttpContext ctx, ClaimsPrincipal me,
        string action, string target = "", string detail = "")
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId = await CurrentUserId(db, me),
                Actor = Cap(me.Identity?.Name ?? "anonymous", 64),
                Action = Cap(action, 64),
                Target = Cap(target, 190),
                Detail = Cap(detail, 1000),
                Ip = Cap(ctx.Connection.RemoteIpAddress?.ToString() ?? "", 64),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { /* the audit trail must never break the write it records */ }
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Parse the request body, or null when it is empty/garbage — every
    /// caller treats that as "no fields supplied" rather than a 400.</summary>
    private static async Task<JsonElement?> ReadJson(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch { return null; }
    }
}
