using System.Globalization;

namespace ChatBranch.Api.Security;

/// <summary>
/// The API's HTTP security headers, set as a response starts (so they follow what the
/// endpoint actually sent):
/// <list type="bullet">
/// <item>every response: <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy:
/// no-referrer</c> (signed links carry tokens in the URL), <c>X-Robots-Tag: noindex,
/// nofollow</c>, <c>X-Permitted-Cross-Domain-Policies: none</c>, and HSTS when the
/// request came over https (directly or via a trusted proxy's X-Forwarded-Proto);</item>
/// <item>JSON, text and event streams — data, never a page: <c>Content-Security-Policy:
/// default-src 'none'; frame-ancestors 'none'</c> + <c>X-Frame-Options: DENY</c>;</item>
/// <item>anything a browser could RUN as a page (HTML, SVG, XML) — e.g. a customer's
/// uploaded file opened from a media link: a sandboxing CSP, so it gets no script and no
/// access to the dashboard's origin;</item>
/// <item>files that must render or be embedded — PDFs, images, audio, video, downloads,
/// <c>widget.js</c> — get no CSP / frame headers (an endpoint's own headers always win).</item>
/// </list>
/// The <c>Server</c> header is dropped at Kestrel (<c>AddServerHeader = false</c>).
/// <c>SECURITY_HEADERS=0</c> turns this off; <c>HSTS_MAX_AGE=0</c> stops HSTS.
/// </summary>
public static class SecurityHeaders
{
    public const string DataCsp = "default-src 'none'; frame-ancestors 'none'";
    public const string SandboxCsp = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, SecurityOptions options)
    {
        if (!options.SecurityHeaders) return app;
        var hsts = options.HstsMaxAgeSeconds > 0
            ? $"max-age={options.HstsMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}" : null;
        return app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                Apply(ctx, hsts);
                return Task.CompletedTask;
            });
            await next(ctx);
        });
    }

    /// <summary>Set the headers for a response about to start (public so tests can check the rules).</summary>
    public static void Apply(HttpContext ctx, string? hsts)
    {
        var h = ctx.Response.Headers;
        h.Remove("Server");
        h.Remove("X-Powered-By");
        Default(h, "X-Content-Type-Options", "nosniff");
        Default(h, "Referrer-Policy", "no-referrer");
        Default(h, "X-Robots-Tag", "noindex, nofollow");
        Default(h, "X-Permitted-Cross-Domain-Policies", "none");
        if (hsts != null && ctx.Request.IsHttps) Default(h, "Strict-Transport-Security", hsts);

        if (h.ContainsKey("Content-Security-Policy")) return; // the endpoint chose its own
        switch (Kind(ctx.Response.ContentType))
        {
            case ContentKind.Data:
                h["Content-Security-Policy"] = DataCsp;
                Default(h, "X-Frame-Options", "DENY");
                break;
            case ContentKind.Page:
                h["Content-Security-Policy"] = SandboxCsp;
                break;
        }
    }

    private enum ContentKind { Data, Page, File }

    private static ContentKind Kind(string? contentType)
    {
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (ct.Length == 0) return ContentKind.Data; // empty bodies, redirects, 204s
        if (ct is "application/json" or "text/plain" or "text/event-stream" or "text/csv"
            || ct.EndsWith("+json", StringComparison.Ordinal))
            return ContentKind.Data;
        if (ct is "text/html" or "application/xhtml+xml" or "image/svg+xml" or "text/xml" or "application/xml"
            || ct.EndsWith("+xml", StringComparison.Ordinal))
            return ContentKind.Page;
        return ContentKind.File;
    }

    private static void Default(IHeaderDictionary h, string name, string value)
    {
        if (!h.ContainsKey(name)) h[name] = value;
    }
}
