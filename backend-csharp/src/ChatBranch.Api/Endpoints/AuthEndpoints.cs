using System.Security.Cryptography;
using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Security;
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
    public record LoginRequest(string? Username, string? Password);
    public record SecondFactorRequest(string? Challenge, string? Code);

    /// <summary>A hash nobody's password matches, verified for unknown usernames so a miss takes
    /// as long as a wrong password (the answer never tells whether a username exists).</summary>
    private static readonly Lazy<string> DummyHash =
        new(() => DjangoPasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Sign-in, step 1: the password. Failed attempts are throttled per username AND per
        // client IP (Security/LoginThrottle) — a locked username or IP gets 429 before its
        // password is even checked. Someone with two-step sign-in gets a short-lived signed
        // challenge instead of a token (step 2 below).
        app.MapPost("/api/auth/login", async (LoginRequest body, HttpContext ctx, AppDbContext db,
            LoginThrottle throttle, SecurityOptions security, TwoFactorService twoFactor) =>
        {
            var username = (body.Username ?? "").Trim();
            var password = body.Password ?? "";
            var ip = ClientIp.Of(ctx);
            var user = username.Length == 0 ? null : await db.Users.Include(u => u.Profile)
                .FirstOrDefaultAsync(u => u.Username == username);
            // a locked username or IP is refused before the password is looked at (an IP lock
            // spares a person who has signed in from that address before — LockedForAsync)
            var locked = user == null ? throttle.Locked(username, ip)
                : await throttle.LockedForAsync(db, user.Id, username, ip);
            if (locked != null) return LoginThrottle.Refusal(ctx, locked);
            // always one PBKDF2 run — an unknown or inactive username, or a login still waiting
            // for its invited person to choose a password (an unusable "!…" value), costs the same time
            var usable = user != null && DjangoPasswordHasher.IsUsable(user.Password);
            var passwordOk = DjangoPasswordHasher.Verify(password, usable ? user!.Password : DummyHash.Value) && usable;
            if (user is not { IsActive: true } || !passwordOk)
            {
                // Failed sign-ins belong in the activity log (never the password).
                ChatBranch.Data.Audit.AuditTrail.Write(db, "login.failed",
                    ChatBranch.Data.Audit.AuditTrail.Cap(username, 150),
                    user == null ? "unknown username" : !user.IsActive ? "inactive account"
                        : usable ? "wrong password" : "no password chosen yet (welcome email link not used)",
                    actor: ChatBranch.Data.Audit.AuditTrail.Cap(username, 150), userId: user?.Id);
                var started = throttle.Fail(username, ip);
                if (started.Count > 0)
                    LoginThrottle.Report(db, started, "sign-ins", user?.Profile?.TenantId);
                return Results.Json(new { detail = "Invalid credentials", code = "invalid_credentials" }, statusCode: 401);
            }
            throttle.Succeed(username);
            var mustChange = DefaultCredentials.MustChange(security, user, password);

            if (await twoFactor.RowAsync(user.Id) is { Enabled: true })
            {
                ChatBranch.Data.Audit.AuditTrail.Write(db, "login.password_ok", user.Username,
                    "waiting for the two-step code", actor: user.Username, userId: user.Id);
                return Results.Ok(new
                {
                    two_factor_required = true,
                    challenge = twoFactor.IssueChallenge(user, mustChange),
                    expires_in = (int)TwoFactorService.ChallengeLifetime.TotalSeconds,
                    issuer = security.TotpIssuer,
                    username = user.Username,
                });
            }
            return await IssueSessionAsync(db, user, mustChange, "password");
        }).AllowAnonymous();

        // Sign-in, step 2 (two-step sign-in on): the challenge from step 1 + a code from the
        // authenticator app or a recovery code. Throttled like passwords; a code is never
        // accepted twice; the challenge lives 5 minutes and signs in once.
        app.MapPost("/api/auth/login/2fa", async (SecondFactorRequest body, HttpContext ctx, AppDbContext db,
            LoginThrottle throttle, TwoFactorService twoFactor) =>
        {
            var ip = ClientIp.Of(ctx);
            var expired = Results.Json(new
            {
                detail = "This sign-in has expired — enter your password again.", code = "challenge_expired",
            }, statusCode: 401);
            var challenge = twoFactor.ReadChallenge(body.Challenge);
            if (challenge == null)
                return throttle.Locked(null, ip) is { } ipLock ? LoginThrottle.Refusal(ctx, ipLock) : expired;
            var user = await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == challenge.UserId);
            if (user is not { IsActive: true } || TwoFactorService.PasswordStamp(user.Password) != challenge.PasswordStamp)
                return expired;
            if (await throttle.LockedForAsync(db, user.Id, user.Username, ip) is { } locked)
                return LoginThrottle.Refusal(ctx, locked);

            var row = await twoFactor.RowAsync(user.Id);
            var kind = row == null ? null : twoFactor.Verify(row, body.Code);
            if (row == null || kind == null)
            {
                ChatBranch.Data.Audit.AuditTrail.Write(db, "login.failed", user.Username, "wrong two-step code",
                    actor: user.Username, userId: user.Id);
                var started = throttle.Fail(user.Username, ip);
                if (started.Count > 0) LoginThrottle.Report(db, started, "sign-in codes", user.Profile?.TenantId);
                return Results.Json(new { detail = "That code isn't right.", code = "invalid_code" }, statusCode: 401);
            }
            TwoFactorService.Spend(challenge);
            throttle.Succeed(user.Username);
            if (kind == TwoFactorService.CodeKind.Recovery)
                ChatBranch.Data.Audit.AuditTrail.Add(db, "user.2fa_recovery_used", user.Username,
                    $"recovery codes left: {row.RecoveryCodes.Count}", actor: user.Username, userId: user.Id);
            return await IssueSessionAsync(db, user, challenge.DefaultPassword,
                kind == TwoFactorService.CodeKind.Recovery ? "password + recovery code" : "password + app code",
                recoveryLeft: kind == TwoFactorService.CodeKind.Recovery ? row.RecoveryCodes.Count : null);
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
        app.MapGet("/api/auth/whoami", async (ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var name = me.Identity?.Name ?? "";
            try
            {
                if (int.TryParse(me.FindFirstValue("profile_id"), out var pid)
                    && await db.Profiles.AsNoTracking().Where(p => p.Id == pid)
                        .Select(p => p.User).FirstOrDefaultAsync() is AppUser u)
                    name = MyAccountEndpoints.DisplayName(u);
            }
            catch (Exception) { /* the display name is cosmetic — never fail whoami over it */ }
            return Results.Ok(new
            {
                username = me.Identity?.Name ?? "",
                name,
                role,
                tenant_id = me.FindFirstValue("tenant_id"),
                // (an API key with scopes reports only what its scopes leave)
                capabilities = CompanyAdminScope.Filter(await AdminEndpoints.CapabilitiesForAsync(db, role), CompanyAdminScope.Current)
                    .Where(c => ApiKeyAuth.Scopes(me) is not { } s || s.Contains(c)).ToList(),
                auth_source = ApiKeyAuth.IsApiKey(me) ? "api" : "session",
                // "" | change_password | enroll_2fa — the dashboard shows the account-setup screen until ""
                restriction = SessionGate.RestrictionOf(ctx),
                two_factor = int.TryParse(me.FindFirstValue("user_id"), out var uid) && TwoFactorService.IsEnrolled(db, uid),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Settings → My account: the caller's own name, email, password, sessions
        app.MapMyAccountEndpoints();
        // two-step sign-in (own enrollment, an admin's reset) + the organization's security policy
        app.MapTwoFactorEndpoints();
        // the welcome email: the public set-password link + Settings → System email
        app.MapPasswordLinkEndpoints();
        app.MapSystemEmailEndpoints();
    }

    /// <summary>
    /// The password (and code, when on) checked out: one AuthToken row per session. A
    /// seeded default password on a real install makes it a RESTRICTED session
    /// (<see cref="SessionRestrictions.ChangePassword"/>); the answer also says when the
    /// organization requires two-step sign-in that this person hasn't set up yet.
    /// </summary>
    private static async Task<IResult> IssueSessionAsync(AppDbContext db, AppUser user, bool mustChangePassword,
        string how, int? recoveryLeft = null)
    {
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
            Restriction = mustChangePassword ? SessionRestrictions.ChangePassword : "",
        });
        user.LastLogin = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Actor = user.Username, Action = "login", UserId = user.Id,
            Detail = mustChangePassword ? $"{how}; must change the initial password" : how,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var restriction = mustChangePassword ? SessionRestrictions.ChangePassword
            : TwoFactorService.RequiredFor(TwoFactorService.Policy(db), profile.Role)
              && !TwoFactorService.IsEnrolled(db, user.Id) ? SessionRestrictions.EnrollTwoFactor : "";

        // Django contract: {token, user:{username, name, role, ...}}. The
        // dashboard reads `data.user` (name for the profile chip, role for
        // gates) — a flat body left it showing "Not signed in". Top-level
        // token/role/username kept for back-compat with existing callers.
        // name = first + last name (Settings → My account), else the
        // username — Django's `get_full_name() or username`.
        var userPayload = new
        {
            username = user.Username,
            name = MyAccountEndpoints.DisplayName(user),
            role = profile.Role,
            restriction,
        };
        return Results.Ok(new
        {
            token,
            role = profile.Role,
            username = user.Username,
            user = userPayload,
            restriction,
            must_change_password = mustChangePassword,
            two_factor_setup_required = restriction == SessionRestrictions.EnrollTwoFactor,
            recovery_codes_left = recoveryLeft,
        });
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
}
