using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Managing website widgets (the operator side of <see cref="WidgetEndpoints"/>).
///
/// A widget IS a <see cref="ChannelAccount"/> with channel="website": its
/// ExternalId is the public token that ships in the embed snippet, and its
/// Config carries the look (title, greeting, accent). Reusing the channel
/// account means routing, entity scoping, health and the inbox all work on
/// widget conversations with no special cases.
/// </summary>
public static class WidgetAdminEndpoints
{
    public static void MapWidgetAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- list -------------------------------------------------------------
        app.MapGet("/api/widgets", async (string? entity, AppDbContext db, ScopingService sc,
            HttpContext ctx) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await sc.ScopeInherited(
                    db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
                        .Where(a => a.Channel == Channels.Website), entityId)
                .OrderBy(a => a.Id).ToListAsync();
            var counts = await db.Threads.AsNoTracking()
                .Where(t => t.Channel == Channels.Website && t.AccountId != null)
                .GroupBy(t => t.AccountId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(new
            {
                widgets = rows.Select(a => Payload(a, ctx, counts.GetValueOrDefault(a.Id))).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        // ---- create -----------------------------------------------------------
        app.MapPost("/api/widgets", async (JsonElement body, AppDbContext db, ClaimsPrincipal me,
            HttpContext ctx) =>
        {
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0)
                return Results.Json(new { detail = "name is required." }, statusCode: 400);
            if (LanguageError(body) is string langErr)
                return Results.Json(new { detail = langErr }, statusCode: 400);
            var row = new ChannelAccount
            {
                Channel = Channels.Website,
                Provider = "widget",
                Name = name,
                // The public token. Rotatable, and never a secret — it ships in
                // a script tag on someone else's page.
                ExternalId = WidgetEndpoints.NewWidgetToken(),
                Owner = me.Identity?.Name ?? "admin",
                EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity")),
                Config = LookConfig(body),
                HealthStatus = Health.Healthy,
                CreatedAt = DateTime.UtcNow,
            };
            if (InboxSettings.Apply(db, row, InboxRaw(body, InboxSettings.TeamKey),
                    InboxRaw(body, InboxSettings.AiDefaultKey),
                    InboxRaw(body, InboxSettings.ReviewModeKey)) is string inboxErr)
                return Results.Json(new { detail = inboxErr }, statusCode: 400);
            db.ChannelAccounts.Add(row);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "widget.create",
                Target = row.Name, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(a => a.Entity).LoadAsync();
            return Results.Json(Payload(row, ctx, 0), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- update (look, name, entity) --------------------------------------
        app.MapMethods("/api/widgets/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await db.ChannelAccounts.FirstOrDefaultAsync(
                a => a.Id == id && a.Channel == Channels.Website);
            if (row == null) return Results.NotFound(new { detail = "Widget not found." });
            if (LanguageError(body) is string langErr)
                return Results.Json(new { detail = langErr }, statusCode: 400);

            if (InboxSettings.Apply(db, row, InboxRaw(body, InboxSettings.TeamKey),
                    InboxRaw(body, InboxSettings.AiDefaultKey),
                    InboxRaw(body, InboxSettings.ReviewModeKey)) is string inboxErr)
                return Results.Json(new { detail = inboxErr }, statusCode: 400);

            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0)
                    return Results.Json(new { detail = "name cannot be empty." }, statusCode: 400);
                row.Name = name;
            }
            if (Has(body, "entity"))
                row.EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity"));

            var look = LookConfig(body);
            if (look.Count > 0)
            {
                var cfg = new Dictionary<string, object?>(row.Config);
                foreach (var kv in look) cfg[kv.Key] = kv.Value;
                row.Config = cfg;
            }
            // Rotating the token invalidates every embed of the old one — that
            // IS the point (a leaked snippet on a site you no longer own).
            var rotated = Bool(body, "rotate_token") == true;
            if (rotated)
                row.ExternalId = WidgetEndpoints.NewWidgetToken();

            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "widget.update", Target = row.Name,
                Detail = rotated ? "token rotated" : "", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            // the response names the entity, so load it (only the FK moved)
            await db.Entry(row).Reference(a => a.Entity).LoadAsync();
            return Results.Ok(Payload(row, ctx, await db.Threads.CountAsync(t => t.AccountId == row.Id)));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- delete -----------------------------------------------------------
        app.MapDelete("/api/widgets/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts.FirstOrDefaultAsync(
                a => a.Id == id && a.Channel == Channels.Website);
            if (row == null) return Results.NotFound(new { detail = "Widget not found." });
            // Conversations stay: they are history, and deleting a widget must
            // not erase what customers said through it.
            var convos = await db.Threads.CountAsync(t => t.AccountId == row.Id);
            await InboxSettings.DetachAccountAsync(db, row.Id);
            db.ChannelAccounts.Remove(row);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "widget.delete", Target = row.Name,
                Detail = convos > 0 ? $"{convos} conversation(s) kept" : "", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, conversations_kept = convos });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ---- helpers ------------------------------------------------------------

    private static object Payload(ChannelAccount a, HttpContext ctx, int conversations)
    {
        var origin = Origin(ctx);
        return new
        {
            id = a.Id,
            name = a.Name,
            token = a.ExternalId,
            entity = a.Entity?.Slug,
            entity_name = a.Entity?.Name ?? "",
            title = Cfg(a, "title"),
            greeting = Cfg(a, "greeting"),
            accent = Cfg(a, "accent"),
            placeholder = Cfg(a, "placeholder"),
            // the widget's own words: auto = the visitor's browser, else en | ms | zh
            language = WidgetLanguage(a),
            conversations,
            created = a.CreatedAt,
            // "Who answers it": owning team (null = none) + AI default ("" = on)
            team_id = InboxSettings.TeamId(a),
            ai_default = InboxSettings.AiDefaultWire(a),
            ai_review_mode = InboxSettings.ReviewModeWire(a),
            // Paste-ready: one tag, no build step, no npm package.
            embed = $"<script src=\"{origin}/api/widget.js\" data-token=\"{a.ExternalId}\" async></script>",
            script_url = $"{origin}/api/widget.js",
        };
    }

    /// <summary>Only the look keys — never the whole config bag from the client.</summary>
    private static Dictionary<string, object?> LookConfig(JsonElement body)
    {
        var outv = new Dictionary<string, object?>();
        foreach (var key in new[] { "title", "greeting", "accent", "placeholder", "ask_identity" })
            if (Has(body, key)) outv[key] = (Str(body, key) ?? "").Trim();
        // validated by LanguageError first; "" (or JSON null) = auto
        if (Has(body, "language")) outv["language"] = NormalizeLanguage(Str(body, "language")) ?? "auto";
        return outv;
    }

    /// <summary>
    /// The website widget's language: <c>auto</c> (the visitor's browser) or
    /// one of the dashboard's locales (<see cref="SettingsEndpoints.SupportedLocales"/>).
    /// The script carries a dictionary for each — keep them in step.
    /// </summary>
    internal static string WidgetLanguage(ChannelAccount a) => NormalizeLanguage(Cfg(a, "language")) ?? "auto";

    /// <summary>"auto" / a supported locale, normalized; "" = auto; null for anything else.</summary>
    private static string? NormalizeLanguage(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0 || v == "auto") return "auto";
        return SettingsEndpoints.SupportedLocales.Contains(v) ? v : null;
    }

    /// <summary>A 400 detail when the body carries an unknown language, else null.</summary>
    private static string? LanguageError(JsonElement body) =>
        Has(body, "language") && NormalizeLanguage(Str(body, "language")) == null
            ? $"language must be auto or one of: {string.Join(", ", SettingsEndpoints.SupportedLocales)}."
            : null;

    private static string Cfg(ChannelAccount a, string key) =>
        a.Config.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string Origin(HttpContext ctx)
    {
        var req = ctx.Request;
        var host = req.Host.HasValue ? req.Host.Value : "localhost:8090";
        var scheme = string.IsNullOrEmpty(req.Scheme) ? "http" : req.Scheme;
        return $"{scheme}://{host}";
    }

    private static bool Has(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    /// <summary>An inbox key's raw value: null when absent, "" when JSON null.</summary>
    private static string? InboxRaw(JsonElement b, string key) => Has(b, key) ? Str(b, key) ?? "" : null;

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
}
