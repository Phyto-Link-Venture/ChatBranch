using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Entity-scoped settings surface. Reads resolve through
/// <see cref="ScopingService"/> (entity override → global → ""), writes need
/// settings.raw.manage (superadmin by default) and refuse to override an
/// organization-wide key. This is
/// the first endpoint that proves the scoping rule end-to-end over HTTP.
/// </summary>
public static partial class SettingsEndpoints
{
    public record SettingWrite(string Key, string? Value, string? Entity);

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Appearance (per-user + organization default).
        MapAppearance(app);

        // GET /api/settings?entity=<slug>&keys=a,b,c — the raw key/value store
        // (any key: persona, routing, notification defaults…): settings.raw.view
        // (admins by default) while its PUT is settings.raw.manage (superadmin by
        // default); no page reads it, each settings page has its own route. It
        // reads any level of the tree, so someone bound to entities beyond a
        // company admin's old reach is refused (OrgWideAction).
        app.MapGet("/api/settings", async (string? keys, string? entity,
            AppDbContext db, ScopingService scoping) =>
        {
            var entityId = await ResolveEntityId(db, entity);
            var wanted = (keys ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (wanted.Count == 0)
                return Results.Ok(new { values = new Dictionary<string, string>() });

            var values = scoping.SettingValues(wanted, entityId);
            var scopes = wanted.ToDictionary(k => k, k => scoping.SettingScope(k, entityId));
            return Results.Ok(new
            {
                entity,
                // Secrets never leave through this generic read — an admin (or a
                // read-only API key of one) can call it. Their own settings
                // pages round-trip them masked.
                values = wanted.ToDictionary(k => k, k => IsSecretKey(k)
                    ? MaskSecret(values.GetValueOrDefault(k, ""))
                    : values.GetValueOrDefault(k, "")),
                scopes,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.raw.view").OrgWideAction();

        // PUT /api/settings  {key, value, entity?}
        app.MapPut("/api/settings", async (SettingWrite body,
            AppDbContext db, ScopingService scoping) =>
        {
            var entityId = await ResolveEntityId(db, body.Entity);
            try
            {
                scoping.SetSetting(body.Key, body.Value, entityId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { detail = ex.Message }, statusCode: 400);
            }
            return Results.Ok(new
            {
                key = body.Key,
                value = scoping.SettingValue(body.Key, entityId),
                scope = scoping.SettingScope(body.Key, entityId),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // ANY key — credentials, billing terms, the safety rules: superadmin-only by
          // default and organization-level (never a bound person's)
          .RequireCapability("settings.raw.manage");
    }

    /// <summary>Setting keys that hold credentials (or JSON blobs carrying them).</summary>
    internal static bool IsSecretKey(string key)
    {
        var k = key.ToLowerInvariant();
        string[] blobs = { "integrations_settings", "voice_twilio_settings", "mcp_servers",
            "whatsapp_custom_templates_secret", "oauth_state_secret", "baileys_secret" };
        return blobs.Contains(k)
               || k.Contains("secret") || k.Contains("password") || k.Contains("token")
               || k.EndsWith("_key") || k.Contains("api_key") || k.StartsWith("smtp_pass");
    }

    /// <summary>"" stays ""; anything else becomes "••••" + its last 4 characters.</summary>
    internal static string MaskSecret(string value) =>
        string.IsNullOrEmpty(value) ? "" : "••••" + (value.Length > 8 ? value[^4..] : "");

    /// <summary>
    /// The capability gate every Settings sub-page's write endpoint uses:
    /// null = allowed (superadmin always is, in code), otherwise a ready 403.
    /// Replaces the old role-literal policies so a custom role granted one
    /// settings capability can manage exactly that page.
    /// </summary>
    internal static async Task<IResult?> RequireCapability(AppDbContext db, ClaimsPrincipal me,
        string capability)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        if (await AdminEndpoints.HasCapabilityAsync(db, role, capability)) return null;
        return Results.Json(new
        {
            detail = $"The {capability} capability is required for this setting.",
            error = $"The {capability} capability is required for this setting.",
        }, statusCode: 403);
    }

    private static async Task<int?> ResolveEntityId(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return null;
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
        return ent?.Id; // unknown slug → null (global), the safe direction
    }
}
