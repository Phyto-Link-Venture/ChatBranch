using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Appearance: each user's chosen look (theme, mode, accent, text size, corner
/// style, heading font — lib/theme.ts <c>Appearance</c>) stored under
/// <c>appearance</c> in their Profile.preferences JSON, plus one organization
/// default (global Setting <c>appearance_default</c>) for users who never chose.
///
/// <code>
/// GET /api/auth/preferences    → {appearance|null, org_default|null, can_set_org_default}
/// PUT /api/auth/preferences    {appearance: {…}|null}   (null = back to the org default)
/// GET /api/settings/appearance → {appearance|null}
/// PUT /api/settings/appearance {appearance: {…}|null}   (capability settings.appearance.org)
/// </code>
/// Values are validated server-side against the same closed vocabularies the
/// client uses, so a hand-crafted PUT can never store a look the app can't draw.
/// </summary>
public static partial class SettingsEndpoints
{
    internal const string AppearanceOrgCapability = "settings.appearance.org";
    internal const string AppearanceSettingKey = "appearance_default";

    private static readonly Dictionary<string, string[]> AppearanceChoices = new()
    {
        ["theme"] = new[] { "paper", "graphite", "soft" },
        ["mode"] = new[] { "light", "dark", "system" },
        ["textSize"] = new[] { "sm", "md", "lg", "xl" },
        ["radius"] = new[] { "theme", "sharp", "round" },
        ["headingFont"] = new[] { "theme", "serif", "sans" },
    };

    private static readonly Dictionary<string, string> AppearanceDefaults = new()
    {
        ["theme"] = "paper",
        ["mode"] = "light",
        ["textSize"] = "md",
        ["radius"] = "theme",
        ["headingFont"] = "theme",
    };

    private static readonly Regex HexColour = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private static void MapAppearance(IEndpointRouteBuilder app)
    {
        // ---- the caller's own preferences ------------------------------------
        app.MapGet("/api/auth/preferences", async (AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
            Results.Ok(await PreferencesPayload(db, sc, me)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/auth/preferences", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("appearance", out var raw))
                return AppearanceBad("Send {appearance: {…}} (or null to use the organization default).");

            var userId = await UserIdOf(db, me);
            var profile = userId is int uid
                ? await db.Profiles.FirstOrDefaultAsync(p => p.UserId == uid)
                : null;
            if (profile == null) return AppearanceBad("No profile for this login.");

            var doc = ProfilePreferences.Load(profile.Preferences);
            if (raw.ValueKind == JsonValueKind.Null)
                doc.Remove("appearance");
            else
            {
                var (value, error) = ValidateAppearance(raw);
                if (error != null) return AppearanceBad(error);
                doc["appearance"] = value;
            }
            profile.Preferences = ProfilePreferences.Save(doc);
            await db.SaveChangesAsync();
            await AuditSettings(db, ctx, me, "preferences.update", "appearance",
                raw.ValueKind == JsonValueKind.Null ? "reset to organization default" : "appearance saved");
            return Results.Ok(await PreferencesPayload(db, sc, me));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- the organization default ----------------------------------------
        app.MapGet("/api/settings/appearance", async (AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            return Results.Ok(new
            {
                appearance = OrgAppearance(sc),
                can_manage = await AdminEndpoints.HasCapabilityAsync(db, role, AppearanceOrgCapability),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/settings/appearance", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            if (await RequireCapability(db, me, AppearanceOrgCapability) is { } denied) return denied;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("appearance", out var raw))
                return AppearanceBad("Send {appearance: {…}} (or null to clear the organization default).");

            if (raw.ValueKind == JsonValueKind.Null)
                sc.SetSetting(AppearanceSettingKey, "");   // "" = no organization default
            else
            {
                var (value, error) = ValidateAppearance(raw);
                if (error != null) return AppearanceBad(error);
                sc.SetSetting(AppearanceSettingKey, value!.ToJsonString());
            }
            await AuditSettings(db, ctx, me, "settings.update", "appearance",
                raw.ValueKind == JsonValueKind.Null ? "organization default cleared" : "organization default set");
            return Results.Ok(new { appearance = OrgAppearance(sc), can_manage = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>
    /// Normalize + validate one appearance object. Unknown keys and values
    /// outside the vocabulary are refused (400) rather than silently dropped, so
    /// a client bug surfaces; omitted keys take the defaults.
    /// </summary>
    internal static (JsonObject? Value, string? Error) ValidateAppearance(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return (null, "appearance must be an object.");
        var outv = new JsonObject();
        foreach (var (key, fallback) in AppearanceDefaults) outv[key] = fallback;
        outv["accent"] = null;

        foreach (var prop in raw.EnumerateObject())
        {
            if (prop.Name == "accent")
            {
                if (prop.Value.ValueKind == JsonValueKind.Null) { outv["accent"] = null; continue; }
                var hex = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "").Trim() : "";
                if (!HexColour.IsMatch(hex))
                    return (null, "accent must be a #rrggbb colour or null.");
                outv["accent"] = hex.ToUpperInvariant();
                continue;
            }
            if (!AppearanceChoices.TryGetValue(prop.Name, out var allowed))
                return (null, $"Unknown appearance key '{prop.Name}'.");
            var v = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
            if (!allowed.Contains(v))
                return (null, $"{prop.Name} must be one of: {string.Join(", ", allowed)}.");
            outv[prop.Name] = v;
        }
        return (outv, null);
    }

    private static JsonNode? OrgAppearance(ScopingService sc)
    {
        try
        {
            var raw = sc.SettingValue(AppearanceSettingKey);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var parsed = JsonDocument.Parse(raw);
            var (value, error) = ValidateAppearance(parsed.RootElement);
            return error == null ? value : null;   // a corrupt row reads as "no default"
        }
        catch { return null; }
    }

    private static async Task<object> PreferencesPayload(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var userId = await UserIdOf(db, me);
        var prefsJson = userId is int uid
            ? await db.Profiles.AsNoTracking().Where(p => p.UserId == uid)
                .Select(p => p.Preferences).FirstOrDefaultAsync()
            : null;
        var doc = ProfilePreferences.Load(prefsJson);
        JsonNode? own = null;
        if (doc["appearance"] is JsonObject stored)
        {
            using var parsed = JsonDocument.Parse(stored.ToJsonString());
            var (value, error) = ValidateAppearance(parsed.RootElement);
            own = error == null ? value : null;
        }
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        return new
        {
            appearance = own,
            org_default = OrgAppearance(sc),
            can_set_org_default = await AdminEndpoints.HasCapabilityAsync(db, role, AppearanceOrgCapability),
        };
    }

    private static IResult AppearanceBad(string detail) =>
        Results.Json(new { detail, error = detail }, statusCode: 400);

    private static async Task<int?> UserIdOf(AppDbContext db, ClaimsPrincipal me)
    {
        var username = me.Identity?.Name ?? "";
        if (username.Length == 0) return null;
        return await db.Users.AsNoTracking().Where(u => u.Username == username)
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
    }

    /// <summary>One audit row; the trail never breaks the write it records.</summary>
    private static async Task AuditSettings(AppDbContext db, HttpContext ctx, ClaimsPrincipal me,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId = await UserIdOf(db, me),
                Actor = Cap(me.Identity?.Name ?? "anonymous", 150),
                Action = Cap(action, 64),
                Target = Cap(target, 190),
                Detail = Cap(detail, 1000),
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}
