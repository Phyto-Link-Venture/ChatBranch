using System.Globalization;
using ChatBranch.Data;

namespace ChatBranch.Api.Security;

/// <summary>
/// Every security knob in one place, read once from configuration (environment
/// variables included) when the host starts. Registered as a singleton so a test
/// host can swap in its own (<c>ConfigureTestServices</c>) without touching the
/// process environment. docs/security.md lists every knob with its default.
/// </summary>
public sealed record SecurityOptions
{
    // ---- rate limiting (Security/RateLimiting) ----------------------------------
    /// <summary><c>RATE_LIMIT</c> — "0" / "false" / "off" turns every limit off (the test host does).</summary>
    public bool RateLimitEnabled { get; init; } = true;
    /// <summary><c>RATE_LIMIT_ANON_PER_MIN</c> — anonymous requests per client IP per minute.</summary>
    public int AnonymousPerMinute { get; init; } = 300;
    /// <summary><c>RATE_LIMIT_USER_PER_MIN</c> — requests per signed-in session / API key per minute.</summary>
    public int UserPerMinute { get; init; } = 1200;
    /// <summary><c>RATE_LIMIT_LOGIN_PER_MIN</c> — sign-in attempts (password + code steps) per IP per minute.</summary>
    public int LoginPerMinute { get; init; } = 30;
    /// <summary><c>RATE_LIMIT_WIDGET_PER_MIN</c> — website-widget session starts + messages per IP per minute.</summary>
    public int WidgetPerMinute { get; init; } = 30;
    /// <summary><c>RATE_LIMIT_PUBLIC_PER_MIN</c> — public document links + bill PDF links per IP per minute.</summary>
    public int PublicLinkPerMinute { get; init; } = 60;
    /// <summary><c>RATE_LIMIT_MEDIA_PER_MIN</c> — signed media links (inbox players, provider fetches) per IP per minute.</summary>
    public int MediaPerMinute { get; init; } = 1200;
    /// <summary><c>RATE_LIMIT_OAUTH_PER_MIN</c> — OAuth callbacks per IP per minute.</summary>
    public int OAuthPerMinute { get; init; } = 30;

    // ---- sign-in protection (Security/LoginThrottle) ------------------------------
    /// <summary><c>LOGIN_MAX_FAILURES</c> — wrong passwords / codes for one username before it is locked.</summary>
    public int MaxFailuresPerUser { get; init; } = 5;
    /// <summary><c>LOGIN_MAX_FAILURES_PER_IP</c> — failures from one IP (any usernames) before the IP is locked.</summary>
    public int MaxFailuresPerIp { get; init; } = 30;
    /// <summary><c>LOGIN_LOCKOUT_MINUTES</c> — the counting window AND how long a lock lasts.</summary>
    public int LockoutMinutes { get; init; } = 15;
    /// <summary><c>API_KEY_MAX_FAILURES_PER_IP</c> — bad API keys from one IP before it is locked.</summary>
    public int MaxApiKeyFailuresPerIp { get; init; } = 20;

    // ---- proxies + headers ------------------------------------------------------------
    /// <summary><c>TRUSTED_PROXIES</c> — comma list of IPs / CIDRs whose X-Forwarded-For / -Proto are
    /// believed; "" = loopback + the private ranges Docker uses; "none" = nobody.</summary>
    public string TrustedProxies { get; init; } = "";
    /// <summary><c>SECURITY_HEADERS</c> — "0" turns the API's security headers off.</summary>
    public bool SecurityHeaders { get; init; } = true;
    /// <summary><c>HSTS_MAX_AGE</c> — seconds for Strict-Transport-Security on https requests; 0 = never sent.</summary>
    public int HstsMaxAgeSeconds { get; init; } = 31_536_000;

    // ---- passwords, default credentials, two-step sign-in --------------------------------
    /// <summary><c>SEED_DEMO</c> truthy = a demo environment: the seeded POC logins keep working
    /// unchanged and the old 8-character password rule applies.</summary>
    public bool IsDemo { get; init; }
    /// <summary><c>SUPERADMIN_PASSWORD</c> — the password the operator chose for the seeded superadmin.</summary>
    public string SuperadminPassword { get; init; } = "";
    /// <summary><c>PASSWORD_MIN_LENGTH</c> — the floor for new passwords on a real install (never below 8).</summary>
    public int PasswordMinLength { get; init; } = 10;
    /// <summary><c>TOTP_ISSUER</c> — the name authenticator apps show next to the account.</summary>
    public string TotpIssuer { get; init; } = "ChatBranch";

    // ---- welcome email + set-password links (Security/PasswordLinks) -------------------
    /// <summary><c>FRONTEND_URL</c>, else <c>PUBLIC_BASE_URL</c> — the dashboard's address, which the
    /// welcome email's login address and set-password link are built on ("" = not configured:
    /// invites are refused rather than mailing a link that can't work).</summary>
    public string LoginBaseUrl { get; init; } = "";
    /// <summary><c>INVITE_LINK_HOURS</c> — how long a set-password link works (72).</summary>
    public int InviteLinkHours { get; init; } = 72;
    /// <summary><c>INVITE_MAX_PER_HOUR</c> — links sent to ONE person per hour (5).</summary>
    public int InvitesPerUserPerHour { get; init; } = 5;

    /// <summary>A real install (not <c>SEED_DEMO</c>): stricter passwords, seeded passwords must change.</summary>
    public bool RealInstall => !IsDemo;

    public TimeSpan LockoutWindow => TimeSpan.FromMinutes(Math.Max(1, LockoutMinutes));

    public static SecurityOptions From(IConfiguration cfg)
    {
        string S(string key) => (cfg[key] ?? "").Trim();
        int I(string key, int fallback, int min = 0)
        {
            var raw = S(key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min
                ? n : fallback;
        }
        bool On(string key, bool fallback)
        {
            var raw = S(key).ToLowerInvariant();
            if (raw.Length == 0) return fallback;
            return raw is not ("0" or "false" or "off" or "no");
        }
        var d = new SecurityOptions();
        return new SecurityOptions
        {
            RateLimitEnabled = On("RATE_LIMIT", d.RateLimitEnabled),
            AnonymousPerMinute = I("RATE_LIMIT_ANON_PER_MIN", d.AnonymousPerMinute, 1),
            UserPerMinute = I("RATE_LIMIT_USER_PER_MIN", d.UserPerMinute, 1),
            LoginPerMinute = I("RATE_LIMIT_LOGIN_PER_MIN", d.LoginPerMinute, 1),
            WidgetPerMinute = I("RATE_LIMIT_WIDGET_PER_MIN", d.WidgetPerMinute, 1),
            PublicLinkPerMinute = I("RATE_LIMIT_PUBLIC_PER_MIN", d.PublicLinkPerMinute, 1),
            MediaPerMinute = I("RATE_LIMIT_MEDIA_PER_MIN", d.MediaPerMinute, 1),
            OAuthPerMinute = I("RATE_LIMIT_OAUTH_PER_MIN", d.OAuthPerMinute, 1),
            MaxFailuresPerUser = I("LOGIN_MAX_FAILURES", d.MaxFailuresPerUser, 1),
            MaxFailuresPerIp = I("LOGIN_MAX_FAILURES_PER_IP", d.MaxFailuresPerIp, 1),
            LockoutMinutes = I("LOGIN_LOCKOUT_MINUTES", d.LockoutMinutes, 1),
            MaxApiKeyFailuresPerIp = I("API_KEY_MAX_FAILURES_PER_IP", d.MaxApiKeyFailuresPerIp, 1),
            TrustedProxies = S("TRUSTED_PROXIES"),
            SecurityHeaders = On("SECURITY_HEADERS", d.SecurityHeaders),
            HstsMaxAgeSeconds = I("HSTS_MAX_AGE", d.HstsMaxAgeSeconds, 0),
            IsDemo = Seed.IsDemo(cfg["SEED_DEMO"]),
            SuperadminPassword = cfg["SUPERADMIN_PASSWORD"] ?? "",
            PasswordMinLength = Math.Max(8, I("PASSWORD_MIN_LENGTH", d.PasswordMinLength, 8)),
            TotpIssuer = S("TOTP_ISSUER") is { Length: > 0 } issuer ? issuer : d.TotpIssuer,
            LoginBaseUrl = S("FRONTEND_URL") is { Length: > 0 } frontend ? frontend : S("PUBLIC_BASE_URL"),
            InviteLinkHours = Math.Clamp(I("INVITE_LINK_HOURS", d.InviteLinkHours, 1), 1, 24 * 14),
            InvitesPerUserPerHour = I("INVITE_MAX_PER_HOUR", d.InvitesPerUserPerHour, 1),
        };
    }
}
