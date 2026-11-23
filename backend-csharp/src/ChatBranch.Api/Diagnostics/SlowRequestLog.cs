using System.Diagnostics;

namespace ChatBranch.Api.Diagnostics;

/// <summary>
/// One warning line per request slower than <c>SLOW_REQUEST_MS</c> (default 2000;
/// 0 turns it off): method, route template, status and milliseconds — never the
/// query string, which can carry signed links. Long-lived streams (the SSE change
/// feed) are skipped: they are slow on purpose. In-process only — the
/// container's own log is where it lands, no collector involved.
/// </summary>
public static class SlowRequestLog
{
    public static int ThresholdMs(IConfiguration config) =>
        int.TryParse(config["SLOW_REQUEST_MS"], out var ms) && ms >= 0 ? ms : 2000;

    public static IApplicationBuilder UseSlowRequestLog(this IApplicationBuilder app, int thresholdMs)
    {
        if (thresholdMs <= 0) return app;
        var log = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("ChatBranch.SlowRequest");
        return app.Use(async (ctx, next) =>
        {
            var start = Stopwatch.GetTimestamp();
            try { await next(); }
            finally
            {
                var ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (ms >= thresholdMs
                    && !(ctx.Response.ContentType ?? "").StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    var route = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ctx.Request.Path.Value ?? "";
                    log.LogWarning("slow request {Method} {Route} -> {Status} in {Ms} ms",
                        ctx.Request.Method, route, ctx.Response.StatusCode, ms);
                }
            }
        });
    }
}
