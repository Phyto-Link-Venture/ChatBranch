using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Developers: personal API keys (the credential for the REST API and for AI
/// apps over MCP) and the "connect an AI app" information the /developers page
/// shows.
///
///   GET    /api/developers/keys[?scope=tenant]   own keys; admins may list their tenant's
///   POST   /api/developers/keys                  {name, scopes?, expires_in_days?} → token ONCE
///   DELETE /api/developers/keys/{id}             revoke (own key; admins: their tenant's)
///   GET    /api/developers/mcp                   endpoint facts + the tools this user would get
///   POST   /api/developers/mcp/test              in-process self-check of the MCP server
///
/// Creating keys needs <c>api_keys.manage</c> (admin+ by default). A key can never
/// mint another key, and its scopes can only narrow its owner's capabilities.
/// </summary>
public static class DevelopersEndpoints
{
    public const string Capability = "api_keys.manage";
    private const int MaxActiveKeysPerUser = 25;

    public static void MapDevelopersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/developers/keys", async (string? scope, AppDbContext db, ClaimsPrincipal me) =>
        {
            var uid = await Caller.UserIdAsync(db, me);
            var caps = await ApiKeyAuth.EffectiveCapabilitiesAsync(db, me);
            var canManageOthers = caps.Contains(Capability) && ShellScope.IsOrgWideAdmin(db, me);   // never a company admin
            var q = db.ApiKeys.AsNoTracking().Include(k => k.User).AsQueryable();
            if (scope == "tenant" && canManageOthers)
            {
                if (Caller.Role(me) != Roles.SuperAdmin)
                {
                    var tid = Caller.TenantId(me);
                    var tenantUsers = db.Profiles.Where(p => p.TenantId == tid).Select(p => p.UserId);
                    q = q.Where(k => tenantUsers.Contains(k.UserId));
                }
            }
            else q = q.Where(k => k.UserId == uid);
            var rows = await q.OrderByDescending(k => k.CreatedAt).ToListAsync();
            return Results.Ok(new
            {
                keys = rows.Select(k => KeyPayload(k, uid)),
                can_create = caps.Contains(Capability) && !ApiKeyAuth.IsApiKey(me),
                can_manage_others = canManageOthers,
                capabilities = caps.OrderBy(c => c),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/developers/keys", async (JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (ApiKeyAuth.IsApiKey(me))
                return Results.Json(new { detail = "An API key cannot create other keys — sign in to the dashboard." },
                    statusCode: 403);
            var caps = await ApiKeyAuth.EffectiveCapabilitiesAsync(db, me);
            if (!caps.Contains(Capability))
                return Results.Json(new { detail = "The api_keys.manage capability is required to create API keys." },
                    statusCode: 403);
            if (await Caller.UserIdAsync(db, me) is not int uid)
                return Results.Json(new { detail = "No user behind this session." }, statusCode: 400);

            var name = Str(body, "name")?.Trim() ?? "";
            if (name.Length is 0 or > 80)
                return Results.Json(new { detail = "Give the key a name (up to 80 characters)." }, statusCode: 400);

            var scopes = new List<string>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("scopes", out var s)
                && s.ValueKind == JsonValueKind.Array)
                scopes = s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            var beyond = scopes.Where(x => !caps.Contains(x)).ToList();
            if (beyond.Count > 0)
                return Results.Json(new
                {
                    detail = $"A key can only carry capabilities you hold yourself: {string.Join(", ", beyond)}.",
                }, statusCode: 400);

            DateTime? expires = null;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("expires_in_days", out var e)
                && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var days) && days > 0)
                expires = DateTime.UtcNow.AddDays(Math.Min(days, 3650));

            var active = await db.ApiKeys.CountAsync(k => k.UserId == uid && k.RevokedAt == null
                                                          && (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow));
            if (active >= MaxActiveKeysPerUser)
                return Results.Json(new { detail = $"You already have {active} active keys — revoke one first." },
                    statusCode: 409);

            var (token, row) = ApiKeyAuth.Mint(uid, name, scopes, expires);
            db.ApiKeys.Add(row);
            AuditTrail.Add(db, "api_key.create", name,
                $"cbk_{row.Prefix}_…; scopes={(scopes.Count == 0 ? "all of the owner's" : string.Join(",", scopes))}"
                + (expires is DateTime x ? $"; expires {x:yyyy-MM-dd}" : ""));
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(k => k.User).LoadAsync();
            return Results.Json(new { key = KeyPayload(row, uid), token }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/developers/keys/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var uid = await Caller.UserIdAsync(db, me);
            var key = await db.ApiKeys.Include(k => k.User!).ThenInclude(u => u.Profile)
                .FirstOrDefaultAsync(k => k.Id == id);
            if (key == null) return Results.NotFound(new { detail = "No such key." });
            if (key.UserId != uid)
            {
                var caps = await ApiKeyAuth.EffectiveCapabilitiesAsync(db, me);
                var allowed = caps.Contains(Capability) && (Caller.Role(me) == Roles.SuperAdmin
                    || (Caller.Role(me) == Roles.Admin && ShellScope.IsOrgWideAdmin(db, me)
                        && key.User?.Profile?.TenantId == Caller.TenantId(me)
                        && Caller.TenantId(me) != null));
                // Out of reach reads as missing — never confirm another tenant's key exists.
                if (!allowed) return Results.NotFound(new { detail = "No such key." });
            }
            if (key.RevokedAt == null)
            {
                key.RevokedAt = DateTime.UtcNow;
                AuditTrail.Add(db, "api_key.revoke", key.Name,
                    $"cbk_{key.Prefix}_…" + (key.UserId != uid ? $" (owner {key.User?.Username})" : ""));
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true, key = KeyPayload(key, uid) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/developers/mcp", async (HttpContext ctx, ClaimsPrincipal me) =>
        {
            var tools = await ToolContext.CreateAsync(ctx, me, AuditSources.Mcp);
            return Results.Ok(new
            {
                path = McpServerEndpoints.Paths[0],
                transport = "streamable-http",
                server = new { name = "ChatBranch", title = "ChatBranch", version = McpServerEndpoints.ServerVersion },
                protocol_versions = McpServerEndpoints.SupportedVersions,
                tools = ToolRegistry.All.Select(t => new
                {
                    name = t.Name,
                    title = t.Title,
                    description = t.Description,
                    write = t.IsWrite,
                    capability = t.Capability,
                    allowed = ToolRegistry.Allowed(tools, t),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        // A self-check from inside the API: the MCP surface is mapped, the tool
        // registry loads, and these are the tools a key of yours would expose.
        // (Reaching it from outside is what the page's "Test with a key" does.)
        app.MapPost("/api/developers/mcp/test", async (HttpContext ctx, ClaimsPrincipal me,
            EndpointDataSource endpoints) =>
        {
            if (AuditContext.Current is { } audit) audit.SkipRequestRow = true; // a probe, not an action
            var mapped = endpoints.Endpoints.OfType<RouteEndpoint>()
                .Any(e => McpServerEndpoints.Paths.Contains(e.RoutePattern.RawText ?? "", StringComparer.OrdinalIgnoreCase));
            var tools = await ToolContext.CreateAsync(ctx, me, AuditSources.Mcp);
            var allowed = ToolRegistry.AllowedFor(tools).Select(t => t.Name).ToList();
            return Results.Ok(new
            {
                ok = mapped,
                server = new { name = "ChatBranch", version = McpServerEndpoints.ServerVersion },
                protocol_version = McpServerEndpoints.SupportedVersions[0],
                tools = allowed,
                detail = mapped
                    ? $"MCP server is up · {allowed.Count} of {ToolRegistry.All.Count} tools available to you."
                    : "The /mcp endpoint is not mapped in this build.",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private static object KeyPayload(ApiKey k, int? me) => new
    {
        id = k.Id,
        name = k.Name,
        prefix = k.Prefix,
        display = $"cbk_{k.Prefix}_••••••••",
        scopes = k.Scopes,
        created_at = Utc(k.CreatedAt),
        last_used_at = k.LastUsedAt is DateTime l ? Utc(l) : null,
        expires_at = k.ExpiresAt is DateTime e ? Utc(e) : null,
        revoked_at = k.RevokedAt is DateTime r ? Utc(r) : null,
        status = k.RevokedAt != null ? "revoked"
            : k.ExpiresAt is DateTime x && x <= DateTime.UtcNow ? "expired" : "active",
        owner = k.User?.Username ?? "",
        owner_id = k.UserId,
        mine = k.UserId == me,
    };

    private static string Utc(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
