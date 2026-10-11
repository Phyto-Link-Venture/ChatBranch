using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;

namespace ChatBranch.Api.Security;

/// <summary>
/// Sessions that must finish setting up their account before they may use anything:
/// <list type="bullet">
/// <item><b>change_password</b> — signed in on a real install with a seeded default
/// password (<see cref="DefaultCredentials"/>); stamped on the session row.</item>
/// <item><b>enroll_2fa</b> — the organization's <c>require_2fa</c> policy covers this
/// person and they haven't set up two-step sign-in; derived per request, so turning the
/// policy on applies to sessions already open.</item>
/// </list>
/// Such a session reaches only the account-setup routes below; everything else is a
/// 403 <c>{code: "account_setup_required", restriction}</c> (the dashboard shows the
/// setup screen instead of the app). API keys, MCP, webhooks and the widget are never
/// affected.
/// </summary>
public static class SessionGate
{
    public const string Code = "account_setup_required";

    /// <summary>What a restricted session may still call (method + exact path).</summary>
    private static readonly HashSet<(string Method, string Path)> Allowed = new()
    {
        ("GET", "/api/auth/whoami"),
        ("POST", "/api/auth/logout"),
        ("GET", "/api/auth/me/account"),
        ("POST", "/api/auth/me/password"),
        ("GET", "/api/auth/me/2fa"),
        ("POST", "/api/auth/me/2fa/setup"),
        ("POST", "/api/auth/me/2fa/confirm"),
        // the shell paints the person's own look + language around the setup screen
        ("GET", "/api/auth/preferences"),
        ("GET", "/api/settings/appearance"),
        ("POST", "/api/client-errors"),
    };

    public static bool IsAllowed(string method, string path) =>
        Allowed.Contains((method.ToUpperInvariant(), path.TrimEnd('/').ToLowerInvariant()))
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The restriction on the caller's session ("" = none). API keys never have one.</summary>
    public static string RestrictionOf(HttpContext ctx)
    {
        var me = ctx.User;
        if (me.Identity?.IsAuthenticated != true || ApiKeyAuth.IsApiKey(me)) return "";
        if (me.FindFirstValue(TokenAuthenticationHandler.RestrictionClaim) is { Length: > 0 } stamped) return stamped;
        if (!int.TryParse(me.FindFirstValue("user_id"), out var userId)) return "";
        try
        {
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var policy = TwoFactorService.Policy(db);
            if (!TwoFactorService.RequiredFor(policy, Caller.Role(me))) return "";
            return TwoFactorService.IsEnrolled(db, userId) ? "" : SessionRestrictions.EnrollTwoFactor;
        }
        catch { return ""; }
    }

    /// <summary>After UseAuthentication: hold restricted sessions to the account-setup routes.</summary>
    public static IApplicationBuilder UseSessionGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var restriction = RestrictionOf(ctx);
            if (restriction.Length > 0 && !IsAllowed(ctx.Request.Method, ctx.Request.Path.Value ?? ""))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    detail = restriction == SessionRestrictions.ChangePassword
                        ? "Change your password before using ChatBranch — this account still has its initial password."
                        : "Your organization requires two-step sign-in. Set it up before using ChatBranch.",
                    code = Code,
                    restriction,
                });
                return;
            }
            await next(ctx);
        });

    /// <summary>Before UseAuthentication: an IP locked out for bad API keys gets 429 before any lookup.</summary>
    public static IApplicationBuilder UseApiKeyGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (ApiKeyAuth.Presented(ctx.Request) != null
                && ctx.RequestServices.GetRequiredService<LoginThrottle>().ApiKeyLocked(ClientIp.Of(ctx)) is { } l)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.Response.Headers.RetryAfter = l.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ctx.Response.WriteAsJsonAsync(new
                {
                    detail = $"Too many invalid API keys from this address. Try again in {Math.Max(1, (l.RetryAfterSeconds + 59) / 60)} min.",
                    code = "too_many_attempts",
                    retry_after = l.RetryAfterSeconds,
                });
                return;
            }
            await next(ctx);
        });
}
