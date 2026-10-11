using System.Text.Json;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The public half of the welcome email (<c>/set-password?token=…</c> in the dashboard):
/// <list type="bullet">
/// <item><c>GET /api/auth/set-password/{token}</c> → <c>{username, expires_at, purpose,
/// password_min_length, password_policy}</c> for a live link.</item>
/// <item><c>POST /api/auth/set-password {token, password}</c> → the password policy, then the
/// password is set, the link used, the person's other links revoked, every session of theirs
/// signed out (so nothing restricted to "change your initial password" survives) and
/// <c>user.password_set</c> audited. They then sign in normally (two-step sign-in, when
/// required, is handled by the sign-in).</item>
/// </list>
/// Both are anonymous (the 256-bit token is the credential, stored only as a hash), limited
/// per client IP like the sign-in (<see cref="RateLimiting"/>), and answer ONE generic 404 for
/// an unknown, used, revoked or expired link — never which.
/// </summary>
public static class PasswordLinkEndpoints
{
    public const string InvalidCode = "invalid_link";

    public static void MapPasswordLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/set-password/{token}", async (string token, AppDbContext db, SecurityOptions security) =>
        {
            if (await PasswordLinks.FindLiveAsync(db, token) is not { } live) return Invalid();
            var (row, user) = live;
            return Results.Ok(new
            {
                username = user.Username,
                purpose = row.Purpose,
                expires_at = PasswordLinks.Utc(row.ExpiresAt),
                password_min_length = security.RealInstall ? security.PasswordMinLength : PasswordPolicy.DemoMinLength,
                password_policy = security.RealInstall ? "strict" : "demo",
            });
        }).AllowAnonymous();

        app.MapPost("/api/auth/set-password", async (JsonElement body, AppDbContext db, SecurityOptions security,
            LoginThrottle throttle) =>
        {
            var token = Str(body, "token");
            var password = Str(body, "password");
            if (await PasswordLinks.FindLiveAsync(db, token) is not { } live) return Invalid();
            var (row, user) = live;
            if (PasswordPolicy.Check(password, user.Username, security) is { } weak)
                return PasswordPolicy.Refusal(weak, "password");
            if (!await PasswordLinks.ClaimAsync(db, row)) return Invalid();

            user.Password = DjangoPasswordHasher.Hash(password);
            await PasswordLinks.RevokeAllAsync(db, user.Id);
            // every session dies with the old password — a session held to "change your
            // initial password first" included, so must_change_password can't linger
            var signedOut = 0;
            if (user.Profile is Profile profile)
            {
                if (profile.Token.Length > 0) { profile.Token = ""; signedOut++; }
                signedOut += await db.AuthTokens.Where(t => t.ProfileId == profile.Id).ExecuteDeleteAsync();
            }
            // they proved they hold the link: a lock from earlier wrong guesses on this username goes
            throttle.Succeed(user.Username);
            AuditTrail.Add(db, "user.password_set", user.Username,
                $"chose a password with {(row.Purpose == PasswordTokenPurposes.Reset ? "a password reset link" : "the welcome email link")} "
                + $"(sent by {row.CreatedBy}); sessions signed out: {signedOut}",
                actor: user.Username, userId: user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, username = user.Username });
        }).AllowAnonymous();
    }

    private static IResult Invalid() => Results.Json(new
    {
        detail = "This link doesn't work any more — it may have been used already, replaced by a newer one, or expired. Ask your administrator to send a new one.",
        code = InvalidCode,
    }, statusCode: 404);

    private static string Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
