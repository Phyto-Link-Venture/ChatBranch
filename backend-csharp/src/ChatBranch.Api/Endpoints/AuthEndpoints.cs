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
                return Results.Json(new { detail = "Invalid credentials" }, statusCode: 401);

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
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/auth/whoami", (ClaimsPrincipal me) =>
            Results.Ok(new
            {
                username = me.Identity?.Name ?? "",
                role = me.FindFirstValue(ClaimTypes.Role) ?? "",
                tenant_id = me.FindFirstValue("tenant_id"),
            })).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
}
