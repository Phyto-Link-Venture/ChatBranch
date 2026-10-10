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
/// Appearance: each user's chosen look (theme, mode, accent, text size / scale,
/// corner style, fonts, line height, density / spacing, page width and margins,
/// card padding, navigation layout, motion, card style — lib/theme.ts
/// <c>Appearance</c>) stored under
/// <c>appearance</c> in their Profile.preferences JSON, plus one organization
/// default (global Setting <c>appearance_default</c>) for users who never chose.
///
/// <code>
/// GET /api/auth/preferences    → {appearance|null, org_default|null, locale|null, org_locale|null, can_set_org_default}
/// PUT /api/auth/preferences    {appearance?: {…}|null, locale?: "en"|"ms"|"zh"|null}   (null = back to the org default)
/// GET /api/settings/appearance → {appearance|null, locale|null, can_manage}
/// PUT /api/settings/appearance {appearance?: {…}|null, locale?: …|null}   (capability settings.appearance.org)
/// </code>
/// The dashboard language rides along: the user's own <c>locale</c> in the same
/// preferences JSON, the organization default in Setting <c>default_locale</c>.
/// Values are validated server-side against the same closed vocabularies the
/// client uses, so a hand-crafted PUT can never store a look the app can't draw.
/// </summary>
public static partial class SettingsEndpoints
{
    internal const string AppearanceOrgCapability = "settings.appearance.org";
    internal const string AppearanceSettingKey = "appearance_default";
    internal const string LocaleSettingKey = "default_locale";

    /// <summary>The dashboard languages (frontend/src/i18n/locales.ts — keep in step).</summary>
    internal static readonly string[] SupportedLocales = { "en", "ms", "zh" };

    /// <summary>The fonts Settings → Appearance offers (lib/theme.ts FONT_IDS — keep in step).</summary>
    private static readonly string[] AppearanceFonts =
    {
        "system", "inter", "schibsted", "geist", "jakarta", "manrope", "dmsans", "plex", "space", "nunito",
        "notosc", "fraunces", "sourceserif", "lora",
    };

    private static readonly Dictionary<string, string[]> AppearanceChoices = new()
    {
        ["theme"] = new[] { "paper", "graphite", "soft", "ocean", "forest", "midnight", "rose", "sand", "nord", "contrast" },
        ["mode"] = new[] { "light", "dark", "system" },
        ["textSize"] = new[] { "sm", "md", "lg", "xl" },
        ["radius"] = new[] { "theme", "square", "sharp", "round" },
        // "serif" / "sans" are the original two choices, still accepted
        ["headingFont"] = new[] { "theme", "serif", "sans" }.Concat(AppearanceFonts).ToArray(),
        ["bodyFont"] = new[] { "theme" }.Concat(AppearanceFonts).ToArray(),
        ["lineHeight"] = new[] { "tight", "normal", "relaxed" },
        ["density"] = new[] { "compact", "comfortable", "spacious" },
        ["contentWidth"] = new[] { "narrow", "normal", "wide", "full" },
        ["gutter"] = new[] { "sm", "md", "lg" },
        ["cardPadding"] = new[] { "sm", "md", "lg" },
        ["navLayout"] = new[] { "side", "top" },
        ["navSide"] = new[] { "left", "right" },
        ["navWidth"] = new[] { "narrow", "normal", "wide" },
        ["navStart"] = new[] { "expanded", "collapsed" },
        ["motion"] = new[] { "system", "reduce", "full" },
        ["surface"] = new[] { "theme", "flat", "bordered", "shadow" },
    };

    /// <summary>The look before anyone chooses (lib/theme.ts DEFAULT_APPEARANCE).</summary>
    private static readonly Dictionary<string, string> AppearanceDefaults = new()
    {
        ["theme"] = "paper",
        ["mode"] = "light",
        ["textSize"] = "md",
        ["radius"] = "theme",
        ["headingFont"] = "theme",
        ["bodyFont"] = "theme",
        ["lineHeight"] = "normal",
        ["density"] = "comfortable",
        ["contentWidth"] = "full",
        ["gutter"] = "md",
        ["cardPadding"] = "md",
        ["navLayout"] = "side",
        ["navSide"] = "left",
        ["navWidth"] = "normal",
        ["navStart"] = "expanded",
        ["motion"] = "system",
        ["surface"] = "theme",
    };

    /// <summary>Free percentage scales (null = the preset): text size and spacing.</summary>
    private static readonly Dictionary<string, (int Min, int Max)> AppearanceScales = new()
    {
        ["fontScale"] = (85, 130),
        ["spacing"] = (70, 140),
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
            var hasAppearance = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("appearance", out _);
            var hasLocale = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("locale", out _);
            if (!hasAppearance && !hasLocale)
                return AppearanceBad("Send {appearance: {…}} and/or {locale: \"en\"|\"ms\"|\"zh\"} (null = the organization default).");
            string? locale = null;
            if (hasLocale && !TryLocale(body.GetProperty("locale"), out locale))
                return AppearanceBad($"locale must be one of: {string.Join(", ", SupportedLocales)} (or null).");

            var userId = await UserIdOf(db, me);
            var profile = userId is int uid
                ? await db.Profiles.FirstOrDefaultAsync(p => p.UserId == uid)
                : null;
            if (profile == null) return AppearanceBad("No profile for this login.");

            var doc = ProfilePreferences.Load(profile.Preferences);
            if (hasAppearance)
            {
                var raw = body.GetProperty("appearance");
                if (raw.ValueKind == JsonValueKind.Null)
                    doc.Remove("appearance");
                else
                {
                    var (value, error) = ValidateAppearance(raw);
                    if (error != null) return AppearanceBad(error);
                    doc["appearance"] = value;
                }
                await AuditSettings(db, ctx, me, "preferences.update", "appearance",
                    raw.ValueKind == JsonValueKind.Null ? "reset to organization default" : "appearance saved");
            }
            if (hasLocale)
            {
                if (locale == null) doc.Remove("locale"); else doc["locale"] = locale;
                await AuditSettings(db, ctx, me, "preferences.update", "locale",
                    locale == null ? "language reset to organization default" : $"language set to {locale}");
            }
            profile.Preferences = ProfilePreferences.Save(doc);
            await db.SaveChangesAsync();
            return Results.Ok(await PreferencesPayload(db, sc, me));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- the organization default ----------------------------------------
        app.MapGet("/api/settings/appearance", async (AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            return Results.Ok(new
            {
                appearance = OrgAppearance(sc),
                locale = OrgLocale(sc),
                can_manage = await AdminEndpoints.HasCapabilityAsync(db, role, AppearanceOrgCapability),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/settings/appearance", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            if (await RequireCapability(db, me, AppearanceOrgCapability) is { } denied) return denied;
            var hasAppearance = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("appearance", out _);
            var hasLocale = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("locale", out _);
            if (!hasAppearance && !hasLocale)
                return AppearanceBad("Send {appearance: {…}} and/or {locale: …} (null clears the organization default).");
            string? locale = null;
            if (hasLocale && !TryLocale(body.GetProperty("locale"), out locale))
                return AppearanceBad($"locale must be one of: {string.Join(", ", SupportedLocales)} (or null).");

            if (hasAppearance)
            {
                var raw = body.GetProperty("appearance");
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
            }
            if (hasLocale)
            {
                sc.SetSetting(LocaleSettingKey, locale ?? "");
                await AuditSettings(db, ctx, me, "settings.update", "locale",
                    locale == null ? "organization language cleared" : $"organization language set to {locale}");
            }
            return Results.Ok(new { appearance = OrgAppearance(sc), locale = OrgLocale(sc), can_manage = true });
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
        foreach (var key in AppearanceScales.Keys) outv[key] = null;

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
            if (AppearanceScales.TryGetValue(prop.Name, out var range))
            {
                if (prop.Value.ValueKind == JsonValueKind.Null) { outv[prop.Name] = null; continue; }
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out var pct)
                    || double.IsNaN(pct) || pct < range.Min || pct > range.Max)
                    return (null, $"{prop.Name} must be a number from {range.Min} to {range.Max} (percent), or null.");
                outv[prop.Name] = (int)Math.Round(pct);
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
        var ownLocale = doc["locale"] is JsonValue lv && lv.TryGetValue<string>(out var l) && SupportedLocales.Contains(l)
            ? l : null;
        return new
        {
            appearance = own,
            org_default = OrgAppearance(sc),
            locale = ownLocale,
            org_locale = OrgLocale(sc),
            can_set_org_default = await AdminEndpoints.HasCapabilityAsync(db, role, AppearanceOrgCapability),
        };
    }

    /// <summary>null (= the default) or one of <see cref="SupportedLocales"/>; false for anything else.</summary>
    private static bool TryLocale(JsonElement v, out string? locale)
    {
        locale = null;
        if (v.ValueKind == JsonValueKind.Null) return true;
        var s = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim().ToLowerInvariant() : "";
        if (!SupportedLocales.Contains(s)) return false;
        locale = s;
        return true;
    }

    private static string? OrgLocale(ScopingService sc)
    {
        var v = (sc.SettingValue(LocaleSettingKey) ?? "").Trim().ToLowerInvariant();
        return SupportedLocales.Contains(v) ? v : null;
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
