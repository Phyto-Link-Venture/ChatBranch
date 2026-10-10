using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Settings → My account (<c>/api/auth/me/*</c>): every signed-in person reads
/// and edits THEIR OWN account — display name, email, password, sessions. There
/// is no id in any route: the caller is always the target, so one user can never
/// reach another's account here (admins manage others through /api/v1/users).
///
/// The username never changes (audit rows and assignments carry it). The writes
/// are session-only: an API key acts as its owner but is not the owner at a
/// keyboard, so it can neither change the password nor sign sessions out nor
/// rewrite the profile (403). Not MCP tools (<c>McpExclusions</c>).
/// </summary>
public static class MyAccountEndpoints
{
    public const int MinPasswordLength = 8; // the same floor as POST /api/v1/users

    public static void MapMyAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // one limiter per app instance (a test host = its own counters)
        var limiter = new PasswordAttemptLimiter();

        app.MapGet("/api/auth/me/account", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var profile = await OwnProfileAsync(db, me);
            if (profile?.User is not AppUser user)
                return Results.Json(new { detail = "No account is attached to this sign-in." }, statusCode: 404);
            return Results.Ok(await PayloadAsync(db, me, profile, user));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/auth/me/account", new[] { "PATCH" },
            async (JsonElement body, ClaimsPrincipal me, AppDbContext db) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var profile = await OwnProfileAsync(db, me);
            if (profile?.User is not AppUser user)
                return Results.Json(new { detail = "No account is attached to this sign-in." }, statusCode: 404);
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send a JSON object.", null);

            var changed = new List<string>();
            foreach (var (key, max) in new[] { ("first_name", 150), ("last_name", 150) })
            {
                if (!body.TryGetProperty(key, out var v)) continue;
                if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return Bad($"{key} must be text.", key);
                var text = (v.ValueKind == JsonValueKind.String ? v.GetString() : "")!.Trim();
                if (text.Length > max) return Bad($"{key} can be at most {max} characters.", key);
                var current = key == "first_name" ? user.FirstName : user.LastName;
                if (text == current) continue;
                if (key == "first_name") user.FirstName = text; else user.LastName = text;
                changed.Add(key);
            }
            if (body.TryGetProperty("email", out var ev))
            {
                if (ev.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return Bad("email must be text.", "email");
                var email = (ev.ValueKind == JsonValueKind.String ? ev.GetString() : "")!.Trim();
                if (email.Length > 254) return Bad("That email address is too long.", "email");
                if (email.Length > 0 && !IsEmail(email))
                    return Bad("That doesn't look like an email address.", "email");
                if (!string.Equals(email, user.Email, StringComparison.Ordinal))
                {
                    if (email.Length > 0)
                    {
                        var lowered = email.ToLowerInvariant();
                        var taken = await db.Users.AsNoTracking()
                            .AnyAsync(u => u.Id != user.Id && u.Email.ToLower() == lowered);
                        if (taken)
                            return Results.Json(new { detail = "Another user already has that email address.",
                                field = "email" }, statusCode: 409);
                    }
                    user.Email = email;
                    changed.Add("email");
                }
            }
            if (body.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String
                && un.GetString() != user.Username)
                return Bad("Usernames can't be changed.", "username");

            if (changed.Count > 0)
            {
                AuditTrail.Add(db, "user.profile_updated", user.Username,
                    $"fields: {string.Join(", ", changed)}", userId: user.Id);
                await db.SaveChangesAsync();
            }
            return Results.Ok(await PayloadAsync(db, me, profile, user));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/auth/me/password",
            async (JsonElement body, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var profile = await OwnProfileAsync(db, me);
            if (profile?.User is not AppUser user)
                return Results.Json(new { detail = "No account is attached to this sign-in." }, statusCode: 404);

            var current = Str(body, "current_password");
            var next = Str(body, "new_password");
            var key = user.Id.ToString();
            if (!limiter.Allowed(key, DateTime.UtcNow, out var retry))
            {
                ctx.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(new
                {
                    detail = $"Too many wrong passwords. Try again in {Math.Max(1, (retry + 59) / 60)} min.",
                    retry_after = retry,
                }, statusCode: 429);
            }
            if (current.Length == 0) return Bad("Enter your current password.", "current_password");
            if (!DjangoPasswordHasher.Verify(current, user.Password))
            {
                limiter.Fail(key, DateTime.UtcNow);
                AuditTrail.Write(db, "user.password_change_failed", user.Username, "wrong current password",
                    userId: user.Id);
                return Bad("Your current password is wrong.", "current_password");
            }
            if (next.Length < MinPasswordLength)
                return Bad($"The new password must be at least {MinPasswordLength} characters.", "new_password");
            if (next == current)
                return Bad("The new password must be different from the current one.", "new_password");

            limiter.Reset(key);
            user.Password = DjangoPasswordHasher.Hash(next);
            var signedOut = await RevokeOthersAsync(db, profile, CurrentToken(ctx));
            AuditTrail.Add(db, "user.password_changed", user.Username,
                $"other sessions signed out: {signedOut}", userId: user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, signed_out = signedOut });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/auth/me/sessions/revoke-others",
            async (ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var profile = await OwnProfileAsync(db, me);
            if (profile?.User is not AppUser user)
                return Results.Json(new { detail = "No account is attached to this sign-in." }, statusCode: 404);
            var signedOut = await RevokeOthersAsync(db, profile, CurrentToken(ctx));
            AuditTrail.Add(db, "user.sessions_revoked", user.Username,
                $"other sessions signed out: {signedOut}", userId: user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, signed_out = signedOut, active_sessions = await SessionCountAsync(db, profile) });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>The display name the shell shows: first + last, else the username.</summary>
    public static string DisplayName(AppUser user)
    {
        var full = $"{user.FirstName} {user.LastName}".Trim();
        return full.Length > 0 ? full : user.Username;
    }

    private static async Task<Profile?> OwnProfileAsync(AppDbContext db, ClaimsPrincipal me)
    {
        if (!int.TryParse(me.FindFirstValue("profile_id"), out var pid)) return null;
        return await db.Profiles.Include(p => p.User).FirstOrDefaultAsync(p => p.Id == pid);
    }

    private static async Task<object> PayloadAsync(AppDbContext db, ClaimsPrincipal me, Profile profile, AppUser user)
    {
        List<object> entities;
        try
        {
            entities = (await AdminEndpoints.AccessibleEntitiesAsync(db, me))
                .Select(e => (object)new { id = e.Id, slug = e.Slug, name = e.Name }).ToList();
        }
        catch (Exception) { entities = new(); }
        return new
        {
            username = user.Username,
            first_name = user.FirstName,
            last_name = user.LastName,
            name = DisplayName(user),
            email = user.Email,
            role = profile.Role,
            entities,
            // stored UTC (SQLite hands it back Unspecified) — say so, so browsers convert it
            last_login = user.LastLogin is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null,
            active_sessions = await SessionCountAsync(db, profile),
            auth_source = ApiKeyAuth.IsApiKey(me) ? "api" : "session",
        };
    }

    private static Task<int> SessionCountAsync(AppDbContext db, Profile profile) =>
        db.AuthTokens.CountAsync(t => t.ProfileId == profile.Id);

    /// <summary>Delete every session of <paramref name="profile"/> except the presented one.</summary>
    private static async Task<int> RevokeOthersAsync(AppDbContext db, Profile profile, string current)
    {
        var n = await db.AuthTokens
            .Where(t => t.ProfileId == profile.Id && t.Token != current)
            .ExecuteDeleteAsync();
        // the legacy per-profile token is another way in; drop it unless it IS this session
        if (profile.Token.Length > 0 && profile.Token != current)
        {
            profile.Token = "";
            n++;
        }
        return n;
    }

    private static string CurrentToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ") ? header["Bearer ".Length..].Trim() : "";
    }

    private static bool IsEmail(string s)
    {
        if (s.Contains(' ') || s.Count(c => c == '@') != 1) return false;
        var at = s.IndexOf('@');
        if (at < 1 || at == s.Length - 1 || !s[(at + 1)..].Contains('.')) return false;
        if (s.EndsWith('.')) return false;
        return MailAddress.TryCreate(s, out var addr) && addr.Address == s;
    }

    private static string Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IResult Bad(string detail, string? field) =>
        Results.Json(new { detail, field }, statusCode: 400);

    private static IResult SessionOnly() =>
        Results.Json(new { detail = "Sign in to the dashboard to change your own account — API keys can't." },
            statusCode: 403);
}

/// <summary>
/// Wrong current passwords on <c>POST /api/auth/me/password</c>: at most
/// <see cref="Limit"/> per <see cref="Window"/> per user, then 429 until the
/// oldest one ages out. A correct change clears the count.
/// </summary>
public sealed class PasswordAttemptLimiter
{
    public const int Limit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _fails = new();

    public bool Allowed(string key, DateTime nowUtc, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_fails.TryGetValue(key, out var q)) return true;
        lock (q)
        {
            while (q.Count > 0 && nowUtc - q.Peek() >= Window) q.Dequeue();
            if (q.Count < Limit) return true;
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((q.Peek() + Window - nowUtc).TotalSeconds));
            return false;
        }
    }

    public void Fail(string key, DateTime nowUtc)
    {
        var q = _fails.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q) q.Enqueue(nowUtc);
    }

    public void Reset(string key) => _fails.TryRemove(key, out _);
}
