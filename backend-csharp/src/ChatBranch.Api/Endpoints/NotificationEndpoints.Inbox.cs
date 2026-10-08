using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The full notification inbox behind the /notifications page, plus each
/// user's own notification preferences. Every route works on the CALLER's rows
/// only — another user's notification id answers 404, never 403, so ids are not
/// an oracle. The bell keeps using <c>/api/auth/notifications</c>.
///
/// <code>
/// GET    /api/notifications?page=&amp;page_size=&amp;category=&amp;unread=&amp;q=&amp;from=&amp;to=
/// POST   /api/notifications/{id}/read | /{id}/unread
/// POST   /api/notifications/read-all            {category?}
/// POST   /api/notifications/mark                {ids:[…], read:bool}
/// DELETE /api/notifications/{id}
/// POST   /api/notifications/delete              {ids:[…]} | {read:true}
/// GET    /api/notifications/preferences
/// PUT    /api/notifications/preferences         {user?:{category:bool}, only_assigned?:bool}
/// </code>
/// </summary>
public static partial class NotificationEndpoints
{
    /// <summary>Capability behind Settings → Organization → Notification defaults.</summary>
    internal const string OrgDefaultsCapability = "notifications.manage";

    private const int DefaultPageSize = 30;
    private const int MaxPageSize = 100;

    /// <summary>Labels + descriptions for both settings pages (one source).</summary>
    private static readonly Dictionary<string, (string Label, string Description)> CategoryMeta = new()
    {
        [NotificationCategories.Messages] = ("Customer messages",
            "A customer wrote in, on any chat channel"),
        [NotificationCategories.AccountHealth] = ("Channel health",
            "An account went down or degraded, routing failover and recovery prompts"),
        [NotificationCategories.Flows] = ("Flows & automations",
            "Flow pushes, human handovers and automation alerts"),
        [NotificationCategories.Admin] = ("Admin & configuration",
            "Users, app keys, integrations and channel account changes"),
        [NotificationCategories.System] = ("System",
            "Everything else the platform needs you to know"),
    };

    private static void MapNotificationInbox(IEndpointRouteBuilder app)
    {
        // ---- list --------------------------------------------------------------
        app.MapGet("/api/notifications", async (HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            var uid = await CurrentUserId(db, me);
            var q = ctx.Request.Query;
            var page = Math.Max(1, ParseInt(q["page"], 1));
            var size = Math.Clamp(ParseInt(q["page_size"], DefaultPageSize), 1, MaxPageSize);
            if (uid is not int userId)
                return Results.Ok(EmptyPage(page, size));

            var rows = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);

            var cats = ((string?)q["category"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(NotificationCategories.IsKnown).Distinct().ToList();
            if (cats.Count > 0) rows = rows.Where(InCategories(cats));

            var unreadFlag = ((string?)q["unread"] ?? "").Trim().ToLowerInvariant();
            if (unreadFlag is "true" or "1" or "yes") rows = rows.Where(n => !n.Read);
            else if (unreadFlag is "false" or "0" or "no") rows = rows.Where(n => n.Read);

            var text = ((string?)q["q"] ?? "").Trim();
            if (text.Length > 0)
            {
                var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                rows = rows.Where(n => EF.Functions.Like(n.Title, pattern, "\\")
                                       || EF.Functions.Like(n.Body, pattern, "\\"));
            }

            if (ParseDate(q["from"], endOfDay: false) is DateTime from)
                rows = rows.Where(n => n.CreatedAt >= from);
            if (ParseDate(q["to"], endOfDay: true) is DateTime to)
                rows = rows.Where(n => n.CreatedAt < to);

            var total = await rows.CountAsync();
            var items = await rows
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                .Skip((page - 1) * size).Take(size)
                .ToListAsync();

            return Results.Ok(new
            {
                items = items.Select(Dto),
                page,
                page_size = size,
                total,
                has_more = page * size < total,
                unread = await UnreadCount(db, userId),
                unread_by_category = await UnreadByCategory(db, userId),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- read state --------------------------------------------------------
        app.MapPost("/api/notifications/{id:int}/read", (int id, AppDbContext db, ClaimsPrincipal me) =>
            SetReadOne(db, me, id, true)).RequireAuthorization(AuthPolicies.Authenticated);
        app.MapPost("/api/notifications/{id:int}/unread", (int id, AppDbContext db, ClaimsPrincipal me) =>
            SetReadOne(db, me, id, false)).RequireAuthorization(AuthPolicies.Authenticated);

        // {category?} narrows "all" to one category (the inbox's active filter).
        app.MapPost("/api/notifications/read-all", async (HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await CurrentUserId(db, me) is not int uid)
                return Results.Ok(new { ok = true, marked = 0, unread = 0 });
            var body = await ReadJson(ctx);
            var rows = db.Notifications.Where(n => n.UserId == uid && !n.Read);
            var cat = Str(body, "category");
            if (NotificationCategories.IsKnown(cat)) rows = rows.Where(InCategories(new List<string> { cat! }));
            var marked = await rows.ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, true));
            if (marked > 0)
                await Audit(db, ctx, me, "notifications.read_all", cat ?? "all", $"{marked} marked read");
            return Results.Ok(new { ok = true, marked, unread = await UnreadCount(db, uid) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Bulk read/unread for the inbox's selection bar.
        app.MapPost("/api/notifications/mark", async (HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await CurrentUserId(db, me) is not int uid)
                return Results.Ok(new { ok = true, updated = 0, unread = 0 });
            var body = await ReadJson(ctx);
            var ids = Ids(body);
            var read = Bool(body, "read");
            if (ids.Count == 0 || read is not bool flag)
                return Results.Json(new { detail = "Send {ids:[…], read:true|false}." }, statusCode: 400);
            var updated = await db.Notifications
                .Where(n => n.UserId == uid && ids.Contains(n.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, flag));
            return Results.Ok(new { ok = true, updated, unread = await UnreadCount(db, uid) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- delete ------------------------------------------------------------
        app.MapDelete("/api/notifications/{id:int}", async (int id, HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (await CurrentUserId(db, me) is not int uid) return NotFound();
            var deleted = await db.Notifications.Where(n => n.Id == id && n.UserId == uid).ExecuteDeleteAsync();
            if (deleted == 0) return NotFound();
            await Audit(db, ctx, me, "notifications.delete", $"notification:{id}");
            return Results.Ok(new { ok = true, deleted, unread = await UnreadCount(db, uid) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // {ids:[…]} deletes those; {read:true} clears every READ row.
        app.MapPost("/api/notifications/delete", async (HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await CurrentUserId(db, me) is not int uid)
                return Results.Ok(new { ok = true, deleted = 0, unread = 0 });
            var body = await ReadJson(ctx);
            var ids = Ids(body);
            int deleted;
            string target;
            if (ids.Count > 0)
            {
                deleted = await db.Notifications.Where(n => n.UserId == uid && ids.Contains(n.Id))
                    .ExecuteDeleteAsync();
                target = $"{ids.Count} selected";
            }
            else if (Bool(body, "read") == true)
            {
                deleted = await db.Notifications.Where(n => n.UserId == uid && n.Read).ExecuteDeleteAsync();
                target = "read";
            }
            else
                return Results.Json(new { detail = "Send {ids:[…]} or {read:true}." }, statusCode: 400);

            if (deleted > 0)
                await Audit(db, ctx, me, "notifications.delete", target, $"{deleted} deleted");
            return Results.Ok(new { ok = true, deleted, unread = await UnreadCount(db, uid) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- the caller's own preferences --------------------------------------
        app.MapGet("/api/notifications/preferences", async (AppDbContext db, ScopingService scoping,
            ClaimsPrincipal me) =>
            Results.Ok(await PreferencesPayload(db, scoping, me)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/notifications/preferences", async (HttpContext ctx, AppDbContext db,
            ScopingService scoping, ClaimsPrincipal me) =>
        {
            var profile = await CurrentProfile(db, me);
            if (profile == null)
                return Results.Json(new { detail = "No profile for this login." }, statusCode: 400);

            var body = await ReadJson(ctx);
            if (body is not JsonElement root || root.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "Send a JSON object." }, statusCode: 400);

            var doc = ProfilePreferences.Load(profile.Preferences);
            var prefs = UserNotificationPrefs.From(doc["notifications"] as JsonObject);
            var changed = new List<string>();

            if (root.TryGetProperty("user", out var user))
            {
                if (user.ValueKind != JsonValueKind.Object)
                    return Results.Json(new { detail = "user must be an object of {category: bool}." },
                        statusCode: 400);
                foreach (var prop in user.EnumerateObject())
                {
                    if (!NotificationCategories.IsKnown(prop.Name))
                        return Results.Json(new { detail = $"Unknown category '{prop.Name}'." }, statusCode: 400);
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Results.Json(new { detail = $"{prop.Name} must be true or false." }, statusCode: 400);
                    prefs.InApp[prop.Name] = prop.Value.GetBoolean();
                    changed.Add(prop.Name);
                }
            }
            if (root.TryGetProperty("only_assigned", out var only))
            {
                if (only.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Results.Json(new { detail = "only_assigned must be true or false." }, statusCode: 400);
                prefs.OnlyAssigned = only.GetBoolean();
                changed.Add("only_assigned");
            }

            if (changed.Count > 0)
            {
                doc["notifications"] = prefs.ToJson();
                profile.Preferences = ProfilePreferences.Save(doc);
                await db.SaveChangesAsync();
                await Audit(db, ctx, me, "preferences.update", "notifications",
                    "keys: " + string.Join(", ", changed.OrderBy(k => k, StringComparer.Ordinal)));
            }
            return Results.Ok(await PreferencesPayload(db, scoping, me));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- shapes ----------------------------------------------------------------

    private static object Dto(Notification n) => new
    {
        id = n.Id,
        kind = n.Kind,
        category = NotificationCategories.Normalize(n.Category, n.Kind),
        title = n.Title,
        body = n.Body,
        link = n.Link,
        read = n.Read,
        created_at = n.CreatedAt,
    };

    private static object EmptyPage(int page, int size) => new
    {
        items = Array.Empty<object>(),
        page,
        page_size = size,
        total = 0,
        has_more = false,
        unread = 0,
        unread_by_category = NotificationCategories.All.ToDictionary(c => c, _ => 0),
    };

    private static async Task<object> PreferencesPayload(AppDbContext db, ScopingService scoping,
        ClaimsPrincipal me)
    {
        var profile = await CurrentProfile(db, me, track: false);
        var prefs = UserNotificationPrefs.FromProfileJson(profile?.Preferences);
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        return new
        {
            categories = NotificationCategories.All.Select(c => new
            {
                key = c,
                label = CategoryMeta[c].Label,
                description = CategoryMeta[c].Description,
            }),
            user = prefs.InApp,
            only_assigned = prefs.OnlyAssigned,
            org = ReadPrefs(scoping),
            can_manage_org = await AdminEndpoints.HasCapabilityAsync(db, role, OrgDefaultsCapability),
        };
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Category match that also places pre-column rows (Category "")
    /// where <see cref="NotificationCategories.Normalize"/> would.</summary>
    private static System.Linq.Expressions.Expression<Func<Notification, bool>> InCategories(List<string> cats)
    {
        var wantMessages = cats.Contains(NotificationCategories.Messages);
        var wantAdmin = cats.Contains(NotificationCategories.Admin);
        return n => cats.Contains(n.Category)
                    || (n.Category == "" && ((n.Kind == "message" && wantMessages)
                                             || (n.Kind != "message" && wantAdmin)));
    }

    private static async Task<IResult> SetReadOne(AppDbContext db, ClaimsPrincipal me, int id, bool read)
    {
        if (await CurrentUserId(db, me) is not int uid) return NotFound();
        var updated = await db.Notifications.Where(n => n.Id == id && n.UserId == uid)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, read));
        if (updated == 0) return NotFound();
        return Results.Ok(new { ok = true, id, read, unread = await UnreadCount(db, uid) });
    }

    private static Task<int> UnreadCount(AppDbContext db, int uid) =>
        db.Notifications.CountAsync(n => n.UserId == uid && !n.Read);

    private static async Task<Dictionary<string, int>> UnreadByCategory(AppDbContext db, int uid)
    {
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == uid && !n.Read)
            .GroupBy(n => new { n.Category, IsMessage = n.Kind == "message" })
            .Select(g => new { g.Key.Category, g.Key.IsMessage, N = g.Count() })
            .ToListAsync();
        var counts = NotificationCategories.All.ToDictionary(c => c, _ => 0);
        foreach (var r in rows)
            counts[NotificationCategories.Normalize(r.Category, r.IsMessage ? "message" : "system")] += r.N;
        return counts;
    }

    private static async Task<Profile?> CurrentProfile(AppDbContext db, ClaimsPrincipal me, bool track = true)
    {
        if (await CurrentUserId(db, me) is not int uid) return null;
        var q = track ? db.Profiles : db.Profiles.AsNoTracking();
        return await q.FirstOrDefaultAsync(p => p.UserId == uid);
    }

    private static IResult NotFound() =>
        Results.Json(new { detail = "Notification not found." }, statusCode: 404);

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var n) ? n : fallback;

    /// <summary>ISO date or datetime. A bare date for <c>to</c> means "through the
    /// end of that day" (exclusive next midnight); a full timestamp is taken as-is.</summary>
    private static DateTime? ParseDate(string? raw, bool endOfDay)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return null;
        if (raw.Length == 10 && DateTime.TryParseExact(raw, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var day))
            return endOfDay ? day.AddDays(1) : day;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var ts))
            return ts;
        return null;
    }

    private static string? Str(JsonElement? body, string key) =>
        body is JsonElement b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement? body, string key) =>
        body is JsonElement b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static List<int> Ids(JsonElement? body)
    {
        var ids = new List<int>();
        if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object
            || !b.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) ids.Add(n);
            else if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) ids.Add(s);
        }
        return ids.Distinct().Take(1000).ToList();
    }
}
