using System.Security.Cryptography;
using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Auth surface ported from <c>/api/auth/*</c>: login issues one AuthToken row
/// per session (concurrent sessions allowed), logout deletes just that row,
/// whoami echoes the caller's identity.
/// </summary>
public static class AuthEndpoints
{
    public record LoginRequest(string Username, string Password);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest body, AppDbContext db) =>
        {
            var user = await db.Users.Include(u => u.Profile)
                .FirstOrDefaultAsync(u => u.Username == body.Username);
            if (user is not { IsActive: true } ||
                !DjangoPasswordHasher.Verify(body.Password, user.Password))
            {
                // Failed sign-ins belong in the activity log (never the password).
                ChatBranch.Data.Audit.AuditTrail.Write(db, "login.failed",
                    ChatBranch.Data.Audit.AuditTrail.Cap(body.Username, 150),
                    user == null ? "unknown username" : user.IsActive ? "wrong password" : "inactive account",
                    actor: ChatBranch.Data.Audit.AuditTrail.Cap(body.Username, 150), userId: user?.Id);
                return Results.Json(new { detail = "Invalid credentials" }, statusCode: 401);
            }

            var profile = user.Profile ??
                new Profile { UserId = user.Id, Role = Roles.Admin };
            if (user.Profile == null) db.Profiles.Add(profile);

            var token = NewToken();
            db.AuthTokens.Add(new AuthToken
            {
                ProfileId = profile.Id,
                Profile = profile,
                Token = token,
                CreatedAt = DateTime.UtcNow,
            });
            user.LastLogin = DateTime.UtcNow;
            db.AuditLogs.Add(new AuditLog
            {
                Actor = user.Username, Action = "login", UserId = user.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            // Django contract: {token, user:{username, name, role, ...}}. The
            // dashboard reads `data.user` (name for the profile chip, role for
            // gates) — a flat body left it showing "Not signed in". Top-level
            // token/role/username kept for back-compat with existing callers.
            // (auth_user has no first_name column, so name falls back to the
            // username, matching Django's `first_name or username`.)
            var userPayload = new
            {
                username = user.Username,
                name = user.Username,
                role = profile.Role,
            };
            return Results.Ok(new
            {
                token,
                role = profile.Role,
                username = user.Username,
                user = userPayload,
            });
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext ctx, AppDbContext db) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer "))
            {
                var token = header["Bearer ".Length..].Trim();
                await db.AuthTokens.Where(t => t.Token == token).ExecuteDeleteAsync();
            }
            ChatBranch.Data.Audit.AuditTrail.Write(db, "logout");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // whoami also carries the caller's `capabilities` — the capability-gated
        // UI reads them here (lib/auth.ts fetchCapabilities falls back to this
        // endpoint when the login response omits them, then hasCap() gates every
        // admin control off the list). Derived from the SAME matrix
        // /api/auth/permissions renders, so a matrix edit moves both together.
        app.MapGet("/api/auth/whoami", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            return Results.Ok(new
            {
                username = me.Identity?.Name ?? "",
                role,
                tenant_id = me.FindFirstValue("tenant_id"),
                // (an API key with scopes reports only what its scopes leave)
                capabilities = CompanyAdminScope.Filter(await AdminEndpoints.CapabilitiesForAsync(db, role), CompanyAdminScope.Current)
                    .Where(c => ApiKeyAuth.Scopes(me) is not { } s || s.Contains(c)).ToList(),
                auth_source = ApiKeyAuth.IsApiKey(me) ? "api" : "session",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
}
