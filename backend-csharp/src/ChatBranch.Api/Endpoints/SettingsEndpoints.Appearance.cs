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
/// GET /api/auth/preferences    → {appearance|null, org_default|null, locale|null, org_locale|null, can_set_org_default,
///                                 guides: {auto, seen}, chats: {sort}}
/// PUT /api/auth/preferences    {appearance?: {…}|null, locale?: "en"|"ms"|"zh"|null}   (null = back to the org default)
///                              {guides?: {auto?, seen?, reset?}} · {chats?: {sort: "newest"|"customer_newest"}|null}
/// GET /api/settings/appearance → {appearance|null, locale|null, can_manage}
/// PUT /api/settings/appearance {appearance?: {…}|null, locale?: …|null}   (capability settings.appearance.org)
/// </code>
/// The dashboard language rides along: the user's own <c>locale</c> in the same
/// preferences JSON, the organization default in Setting <c>default_locale</c>.
/// So do two per-person conveniences: the page-guide memory (<c>guides</c>) and
/// the chat list's order (<c>chats.sort</c>: newest message, or newest from the
/// customer — the switch above the /chats list).
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
            var hasGuides = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("guides", out _);
            var hasChats = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("chats", out _);
            if (!hasAppearance && !hasLocale && !hasGuides && !hasChats)
                return AppearanceBad("Send {appearance: {…}}, {locale: \"en\"|\"ms\"|\"zh\"} (null = the organization default), {guides: {auto?, seen?}} and/or {chats: {sort}}.");
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
            if (hasGuides && MergeGuides(doc, body.GetProperty("guides")) is string guidesError)
                return AppearanceBad(guidesError);
            if (hasChats && MergeChats(doc, body.GetProperty("chats")) is string chatsError)
                return AppearanceBad(chatsError);
            profile.Preferences = ProfilePreferences.Save(doc);
            await db.SaveChangesAsync();
            return Results.Ok(await PreferencesPayload(db, sc, me));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // closing a page guide writes here — a convenience, not an audited change
          // (appearance / language saves write their own audit rows)
          .WithMetadata(new ChatBranch.Api.Audit.SkipRequestAuditAttribute());

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
            guides = GuidesOf(doc),
            chats = ChatsOf(doc),
        };
    }

    /// <summary>The chat-list orders a person can keep (frontend inboxFilters.ts HEADER_SORTS — keep in step).</summary>
    private static readonly string[] ChatListSorts = { "newest", "customer_newest" };

    /// <summary>The person's chat-list order, <c>{sort}</c> — "newest" until they pick another.</summary>
    private static object ChatsOf(JsonObject doc)
    {
        var sort = (doc["chats"] as JsonObject)?["sort"] is JsonValue v && v.TryGetValue<string>(out var x)
                   && ChatListSorts.Contains(x) ? x : "newest";
        return new { sort };
    }

    /// <summary>Store <c>{sort}</c> (null forgets it). Unknown keys or orders are refused.</summary>
    private static string? MergeChats(JsonObject doc, JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Null)
        {
            doc.Remove("chats");
            return null;
        }
        if (raw.ValueKind != JsonValueKind.Object) return "chats must be an object {sort} (or null).";
        var c = doc["chats"] as JsonObject ?? new JsonObject();
        foreach (var prop in raw.EnumerateObject())
        {
            if (prop.Name != "sort") return $"Unknown chats key '{prop.Name}'.";
            var v = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "").Trim() : "";
            if (!ChatListSorts.Contains(v)) return $"chats.sort must be one of: {string.Join(", ", ChatListSorts)}.";
            c["sort"] = v;
        }
        doc["chats"] = c;
        return null;
    }

    private static readonly Regex GuideId = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);
    private const int MaxSeenGuides = 200;

    /// <summary>The page-guide memory that follows a person across browsers and
    /// devices: <c>auto</c> (guides start by themselves on a first visit) and the
    /// guides already <c>seen</c>.</summary>
    private static object GuidesOf(JsonObject doc)
    {
        var g = doc["guides"] as JsonObject;
        var auto = g?["auto"] is JsonValue a && a.TryGetValue<bool>(out var b) ? b : true;
        var seen = (g?["seen"] as JsonArray)?.Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var x) ? x : null).OfType<string>().ToArray()
                   ?? Array.Empty<string>();
        return new { auto, seen };
    }

    /// <summary>Merge {auto?, seen?} into the stored guides: <c>seen</c> ADDS ids
    /// (a guide closed on any device stays seen), <c>reset: true</c> clears them.</summary>
    private static string? MergeGuides(JsonObject doc, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return "guides must be an object {auto?, seen?, reset?}.";
        var g = doc["guides"] as JsonObject ?? new JsonObject();
        if (raw.TryGetProperty("auto", out var auto))
        {
            if (auto.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "guides.auto must be true or false.";
            g["auto"] = auto.GetBoolean();
        }
        var seen = new List<string>();
        if (!(raw.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True)
            && g["seen"] is JsonArray old)
            seen.AddRange(old.Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var x) ? x : null).OfType<string>());
        if (raw.TryGetProperty("seen", out var add))
        {
            if (add.ValueKind != JsonValueKind.Array) return "guides.seen must be a list of guide ids.";
            foreach (var v in add.EnumerateArray())
            {
                var id = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (!GuideId.IsMatch(id)) return $"'{id}' is not a guide id.";
                if (!seen.Contains(id)) seen.Add(id);
            }
        }
        g["seen"] = new JsonArray(seen.TakeLast(MaxSeenGuides).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        doc["guides"] = g;
        return null;
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
