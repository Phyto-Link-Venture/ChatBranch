using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatBranch.Api.Security;

/// <summary>
/// Request rate limits (ASP.NET Core's rate limiter, one sliding window per minute):
/// <list type="table">
/// <item><term>login</term><description>sign-in steps and the set-password link — per client IP (<c>RATE_LIMIT_LOGIN_PER_MIN</c>, 30)</description></item>
/// <item><term>widget</term><description>the website widget's session starts + messages — per IP (<c>RATE_LIMIT_WIDGET_PER_MIN</c>, 30)</description></item>
/// <item><term>public-link</term><description>public document links + bill PDF links — per IP (<c>RATE_LIMIT_PUBLIC_PER_MIN</c>, 60)</description></item>
/// <item><term>media</term><description>signed media links (the inbox's players, providers fetching an attachment) — per IP (<c>RATE_LIMIT_MEDIA_PER_MIN</c>, 1200)</description></item>
/// <item><term>oauth</term><description>the OAuth callback — per IP (<c>RATE_LIMIT_OAUTH_PER_MIN</c>, 30)</description></item>
/// <item><term>user</term><description>everything a signed-in caller does — per session / API key (<c>RATE_LIMIT_USER_PER_MIN</c>, 1200)</description></item>
/// <item><term>anonymous</term><description>every other anonymous request — per IP (<c>RATE_LIMIT_ANON_PER_MIN</c>, 300)</description></item>
/// </list>
/// Provider webhooks (signed, bursty, from a few provider IPs) and the Baileys gateway's
/// pushes are exempt. A refusal is 429 <c>{detail, code: "rate_limited", retry_after}</c>
/// with <c>Retry-After</c>. Runs after authentication (so a made-up bearer token falls in
/// the anonymous per-IP bucket, never a fresh one). <c>RATE_LIMIT=0</c> turns it off.
/// </summary>
public static class RateLimiting
{
    public const string Login = "login";
    public const string Widget = "widget";
    public const string PublicLink = "public-link";
    public const string Media = "media";
    public const string OAuth = "oauth";
    public const string User = "user";
    public const string Anonymous = "anonymous";
    public const string Exempt = "exempt";

    /// <summary>Which limit a request falls under (path + method + whether it is signed in).</summary>
    public static string Classify(string method, string path, bool signedIn)
    {
        var p = path.ToLowerInvariant();
        var post = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        // signed provider traffic: never throttled here (a dropped webhook = a lost message)
        if (p.StartsWith("/api/whatsapp/webhook", StringComparison.Ordinal)
            || p.StartsWith("/api/messaging/webhook", StringComparison.Ordinal)
            || p.StartsWith("/api/whatsapp/baileys/", StringComparison.Ordinal)
            || p.StartsWith("/api/integrations/webhooks/", StringComparison.Ordinal))
            return Exempt;
        if (post && (p == "/api/auth/login" || p == "/api/auth/login/2fa")) return Login;
        // the welcome email's set-password link (read + submit) counts like a sign-in step
        if (p == "/api/auth/set-password" || p.StartsWith("/api/auth/set-password/", StringComparison.Ordinal)) return Login;
        if (post && p.StartsWith("/api/widget/", StringComparison.Ordinal)
            && (p.EndsWith("/session", StringComparison.Ordinal) || p.EndsWith("/messages", StringComparison.Ordinal)))
            return Widget;
        if (p.StartsWith("/api/documents/public/", StringComparison.Ordinal)
            || p.StartsWith("/api/billing/ai/pdf/s/", StringComparison.Ordinal))
            return PublicLink;
        if (p.StartsWith("/api/media/s/", StringComparison.Ordinal)
            || p.StartsWith("/api/media/public/", StringComparison.Ordinal))
            return Media;
        if (p.StartsWith("/api/integrations/oauth/", StringComparison.Ordinal) && p.EndsWith("/callback", StringComparison.Ordinal))
            return OAuth;
        return signedIn ? User : Anonymous;
    }

    public static int LimitFor(string kind, SecurityOptions o) => kind switch
    {
        Login => o.LoginPerMinute,
        Widget => o.WidgetPerMinute,
        PublicLink => o.PublicLinkPerMinute,
        Media => o.MediaPerMinute,
        OAuth => o.OAuthPerMinute,
        User => o.UserPerMinute,
        _ => o.AnonymousPerMinute,
    };

    /// <summary>The partition a request counts against: kind + the caller (IP, or the session / key).</summary>
    public static string PartitionKey(HttpContext ctx, string kind)
    {
        if (kind == User)
        {
            // a signed-in caller is its credential (sessions + API keys alike), never a shared IP
            var credential = ctx.Request.Headers.Authorization.ToString();
            if (credential.Length == 0) credential = ctx.Request.Headers["X-Api-Key"].ToString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..24];
            return $"{kind}:{hash}";
        }
        var ip = ClientIp.Of(ctx);
        return $"{kind}:{(ip.Length == 0 ? "unknown" : ip)}";
    }

    public static IServiceCollection AddChatBranchRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<SecurityOptions>((options, security) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (!security.RateLimitEnabled) return RateLimitPartition.GetNoLimiter("off");
                var kind = Classify(ctx.Request.Method, ctx.Request.Path.Value ?? "",
                    ctx.User.Identity?.IsAuthenticated == true);
                if (kind == Exempt || HttpMethods.IsOptions(ctx.Request.Method))
                    return RateLimitPartition.GetNoLimiter(Exempt);
                var limit = Math.Max(1, LimitFor(kind, security));
                return RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(ctx, kind), _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            options.OnRejected = async (context, ct) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                    ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 10;
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                await http.Response.WriteAsJsonAsync(new
                {
                    detail = $"Too many requests — slow down and try again in {retry} s.",
                    code = "rate_limited",
                    retry_after = retry,
                }, ct);
            };
        });
        return services;
    }
}
