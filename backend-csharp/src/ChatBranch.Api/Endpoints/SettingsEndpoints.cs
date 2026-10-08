using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Entity-scoped settings surface. Reads resolve through
/// <see cref="ScopingService"/> (entity override → global → ""), writes are
/// superadmin-gated and refuse to override an organization-wide key. This is
/// the first endpoint that proves the scoping rule end-to-end over HTTP.
/// </summary>
public static partial class SettingsEndpoints
{
    public record SettingWrite(string Key, string? Value, string? Entity);

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Appearance (per-user + organization default).
        MapAppearance(app);

        // GET /api/settings?entity=<slug>&keys=a,b,c
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
                values = wanted.ToDictionary(k => k, k => values.GetValueOrDefault(k, "")),
                scopes,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.SuperAdmin);
    }

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
