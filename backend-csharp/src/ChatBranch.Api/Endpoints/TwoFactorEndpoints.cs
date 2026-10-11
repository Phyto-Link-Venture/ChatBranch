using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Two-step sign-in, the caller's OWN (Settings → My account → Two-step sign-in,
/// <c>/api/auth/me/2fa*</c>): status, set up (QR + manual key), confirm with a code
/// (→ 10 recovery codes, shown once), new recovery codes, turn off (password + code).
/// Session-only — an API key acts as its owner but is not the owner holding the phone
/// (403). Plus the organization's policy (Settings → Security,
/// <c>/api/settings/security</c>, <c>security.manage</c>). An admin's reset of
/// someone else's is <c>POST /api/v1/users/{id}/2fa/reset</c> (ApiV1Endpoints).
/// None are MCP tools (<c>McpExclusions</c>).
/// </summary>
public static class TwoFactorEndpoints
{
    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/me/2fa", async (ClaimsPrincipal me, AppDbContext db, TwoFactorService tf) =>
        {
            var user = await OwnUserAsync(db, me);
            if (user == null) return NoAccount();
            return Results.Ok(await StatusAsync(db, tf, me, user));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Start (or restart) setup. Already on = setting up a new phone: the password first.
        app.MapPost("/api/auth/me/2fa/setup", async (JsonElement body, ClaimsPrincipal me, HttpContext ctx,
            AppDbContext db, TwoFactorService tf, LoginThrottle throttle, SecurityOptions o) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var user = await OwnUserAsync(db, me);
            if (user == null) return NoAccount();
            var row = await tf.RowAsync(user.Id);
            if (row is { Enabled: true }
                && await CheckPasswordAsync(db, ctx, throttle, user, Str(body, "password"), "two-step setup") is { } refused)
                return refused;
            var setup = await tf.BeginAsync(user);
            return Results.Ok(new
            {
                manual_key = Base32.Grouped(setup.ManualKey),
                otpauth_uri = setup.Uri,
                qr_svg = setup.QrSvg,
                expires_at = DateTime.SpecifyKind(setup.ExpiresAt, DateTimeKind.Utc),
                issuer = o.TotpIssuer,
                account = user.Username,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/auth/me/2fa/confirm", async (JsonElement body, ClaimsPrincipal me, HttpContext ctx,
            AppDbContext db, TwoFactorService tf, LoginThrottle throttle) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var user = await OwnUserAsync(db, me);
            if (user == null) return NoAccount();
            var ip = ClientIp.Of(ctx);
            if (await throttle.LockedForAsync(db, user.Id, user.Username, ip) is { } locked)
                return LoginThrottle.Refusal(ctx, locked);
            var wasOn = (await tf.RowAsync(user.Id))?.Enabled == true;
            var codes = await tf.ConfirmAsync(user, Str(body, "code"));
            if (codes == null)
            {
                var started = throttle.Fail(user.Username, ip);
                if (started.Count > 0) LoginThrottle.Report(db, started, "two-step codes", user.Profile?.TenantId);
                return Results.Json(new
                {
                    detail = "That code isn't right (or the setup expired — start again).", code = "invalid_code", field = "code",
                }, statusCode: 400);
            }
            throttle.Succeed(user.Username);
            AuditTrail.Write(db, wasOn ? "user.2fa_device_changed" : "user.2fa_enabled", user.Username,
                wasOn ? "two-step sign-in moved to a new authenticator" : "two-step sign-in turned on", userId: user.Id);
            return Results.Ok(new { ok = true, recovery_codes = codes, status = await StatusAsync(db, tf, me, user) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/auth/me/2fa/recovery-codes", async (JsonElement body, ClaimsPrincipal me, HttpContext ctx,
            AppDbContext db, TwoFactorService tf, LoginThrottle throttle) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var user = await OwnUserAsync(db, me);
            if (user == null) return NoAccount();
            var row = await tf.RowAsync(user.Id);
            if (row is not { Enabled: true })
                return Results.Json(new { detail = "Two-step sign-in is off.", code = "two_factor_off" }, statusCode: 409);
            if (await CheckPasswordAndCodeAsync(db, ctx, throttle, tf, user, row, body) is { } refused) return refused;
            var codes = TwoFactorService.NewRecoveryCodes(row);
            row.UpdatedAt = DateTime.UtcNow;
            AuditTrail.Add(db, "user.2fa_recovery_codes", user.Username, "new recovery codes made (the old ones stop working)",
                userId: user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, recovery_codes = codes, status = await StatusAsync(db, tf, me, user) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Turn it off: password + a code (or a recovery code). Not while the organization requires it.
        app.MapPost("/api/auth/me/2fa/disable", async (JsonElement body, ClaimsPrincipal me, HttpContext ctx,
            AppDbContext db, TwoFactorService tf, LoginThrottle throttle) =>
        {
            if (ApiKeyAuth.IsApiKey(me)) return SessionOnly();
            var user = await OwnUserAsync(db, me);
            if (user == null) return NoAccount();
            var row = await tf.RowAsync(user.Id);
            if (row is not { Enabled: true })
            {
                // an unconfirmed setup is simply dropped
                if (row != null) await tf.RemoveAsync(user.Id);
                return Results.Ok(new { ok = true, status = await StatusAsync(db, tf, me, user) });
            }
            if (TwoFactorService.RequiredFor(TwoFactorService.Policy(db), Caller.Role(me)))
                return Results.Json(new
                {
                    detail = "Your organization requires two-step sign-in, so it can't be turned off. "
                             + "To move it to a new phone, set it up again.",
                    code = "two_factor_required",
                }, statusCode: 409);
            if (await CheckPasswordAndCodeAsync(db, ctx, throttle, tf, user, row, body) is { } refused) return refused;
            await tf.RemoveAsync(user.Id);
            AuditTrail.Write(db, "user.2fa_disabled", user.Username, "two-step sign-in turned off", userId: user.Id);
            return Results.Ok(new { ok = true, status = await StatusAsync(db, tf, me, user) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- Settings → Security: the organization policy ---------------------------------

        app.MapGet("/api/settings/security", async (AppDbContext db, SecurityOptions o) =>
            Results.Ok(await SecurityPayloadAsync(db, o)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("security.manage").OrgWideAction();

        app.MapPut("/api/settings/security", async (JsonElement body, ClaimsPrincipal me, AppDbContext db,
            ScopingService sc, SecurityOptions o) =>
        {
            var policy = Str(body, "require_2fa").Trim().ToLowerInvariant();
            if (!TwoFactorService.Policies.Contains(policy))
                return Results.Json(new
                {
                    detail = $"require_2fa must be one of: {string.Join(", ", TwoFactorService.Policies)}.",
                    field = "require_2fa",
                }, statusCode: 400);
            var before = TwoFactorService.Policy(db);
            if (before != policy)
            {
                sc.SetSetting(TwoFactorService.PolicyKey, policy == TwoFactorService.PolicyOff ? "" : policy, null);
                TwoFactorService.Invalidate();
                AuditTrail.Write(db, "security.policy", "require_2fa", $"{before} → {policy}");
            }
            return Results.Ok(await SecurityPayloadAsync(db, o));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("security.manage").OrgWideAction();
    }

    private static async Task<object> StatusAsync(AppDbContext db, TwoFactorService tf, ClaimsPrincipal me, AppUser user)
    {
        var row = await tf.RowAsync(user.Id);
        var policy = TwoFactorService.Policy(db);
        return new
        {
            enabled = row?.Enabled == true,
            pending = row is { PendingSecret.Length: > 0 },
            confirmed_at = row?.ConfirmedAt is DateTime c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : (DateTime?)null,
            recovery_codes_left = row?.Enabled == true ? row.RecoveryCodes.Count : 0,
            required = TwoFactorService.RequiredFor(policy, Caller.Role(me)),
            policy,
            auth_source = ApiKeyAuth.IsApiKey(me) ? "api" : "session",
        };
    }

    private static async Task<object> SecurityPayloadAsync(AppDbContext db, SecurityOptions o)
    {
        var active = await db.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new { u.Id, Role = u.Profile != null ? u.Profile.Role : "" }).ToListAsync();
        var enrolled = (await db.UserTwoFactors.AsNoTracking().Where(t => t.Enabled).Select(t => t.UserId)
            .ToListAsync()).ToHashSet();
        var admins = active.Where(u => u.Role is Roles.Admin or Roles.SuperAdmin).ToList();
        return new
        {
            require_2fa = TwoFactorService.Policy(db),
            policies = TwoFactorService.Policies,
            users = active.Count,
            users_enrolled = active.Count(u => enrolled.Contains(u.Id)),
            admins = admins.Count,
            admins_enrolled = admins.Count(u => enrolled.Contains(u.Id)),
            // what the server enforces from its environment (docs/security.md) — read-only here
            protections = new
            {
                login_max_failures = o.MaxFailuresPerUser,
                login_max_failures_per_ip = o.MaxFailuresPerIp,
                lockout_minutes = o.LockoutMinutes,
                rate_limit = o.RateLimitEnabled,
                password_min_length = o.RealInstall ? o.PasswordMinLength : PasswordPolicy.DemoMinLength,
                real_install = o.RealInstall,
            },
        };
    }

    // ---- helpers ------------------------------------------------------------------------

    private static async Task<AppUser?> OwnUserAsync(AppDbContext db, ClaimsPrincipal me)
    {
        if (!int.TryParse(me.FindFirstValue("profile_id"), out var pid)) return null;
        return await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Profile != null && u.Profile.Id == pid);
    }

    /// <summary>The current password, throttled like a sign-in. Null = correct.</summary>
    private static async Task<IResult?> CheckPasswordAsync(AppDbContext db, HttpContext ctx, LoginThrottle throttle,
        AppUser user, string password, string what)
    {
        var ip = ClientIp.Of(ctx);
        if (await throttle.LockedForAsync(db, user.Id, user.Username, ip) is { } locked)
            return LoginThrottle.Refusal(ctx, locked);
        if (password.Length > 0 && DjangoPasswordHasher.Verify(password, user.Password)) return null;
        AuditTrail.Write(db, "user.password_check_failed", user.Username, $"wrong password ({what})", userId: user.Id);
        var started = throttle.Fail(user.Username, ip);
        if (started.Count > 0) LoginThrottle.Report(db, started, "password checks", user.Profile?.TenantId);
        await Task.CompletedTask;
        return Results.Json(new
        {
            detail = password.Length == 0 ? "Enter your password." : "Your password is wrong.",
            code = "wrong_password", field = "password",
        }, statusCode: 400);
    }

    /// <summary>Password + a code from the app (or a recovery code) — both throttled. Null = both right
    /// (the caller saves: the code's step / the used recovery code are recorded on the row).</summary>
    private static async Task<IResult?> CheckPasswordAndCodeAsync(AppDbContext db, HttpContext ctx,
        LoginThrottle throttle, TwoFactorService tf, AppUser user, UserTwoFactor row, JsonElement body)
    {
        if (await CheckPasswordAsync(db, ctx, throttle, user, Str(body, "password"), "two-step change") is { } refused)
            return refused;
        if (tf.Verify(row, Str(body, "code")) != null)
        {
            throttle.Succeed(user.Username);
            await db.SaveChangesAsync();
            return null;
        }
        var ip = ClientIp.Of(ctx);
        AuditTrail.Write(db, "user.password_check_failed", user.Username, "wrong two-step code", userId: user.Id);
        var started = throttle.Fail(user.Username, ip);
        if (started.Count > 0) LoginThrottle.Report(db, started, "two-step codes", user.Profile?.TenantId);
        return Results.Json(new { detail = "That code isn't right.", code = "invalid_code", field = "code" }, statusCode: 400);
    }

    private static string Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IResult NoAccount() =>
        Results.Json(new { detail = "No account is attached to this sign-in." }, statusCode: 404);

    private static IResult SessionOnly() =>
        Results.Json(new
        {
            detail = "Sign in to the dashboard to change your own two-step sign-in — API keys can't.",
        }, statusCode: 403);
}
