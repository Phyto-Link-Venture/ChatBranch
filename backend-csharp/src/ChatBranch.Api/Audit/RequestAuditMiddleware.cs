using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Audit;

/// <summary>Opt an endpoint out of the generic request row (pure reads that are POSTs).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipRequestAuditAttribute : Attribute { }

/// <summary>
/// The activity log's safety net. For every <c>/api/*</c> (and <c>/mcp</c>) request it
/// opens an <see cref="AuditContext"/> — source, actor, user, method, path, IP,
/// user agent, request id — so any audit row written while the request runs is
/// stamped with that provenance. For MUTATING requests (POST/PUT/PATCH/DELETE)
/// that did not write a domain row of their own, it then records one generic
/// row: who, how (source), what (METHOD + route), the outcome (status).
///
/// Never logged: request bodies, query strings, or secret-looking path segments
/// (a Telegram bot token lives in its webhook path). Never fatal: an audit
/// failure is swallowed — it must not fail the request it describes.
///
/// Source is derived from how the caller got in: a session token → user, an API
/// key → api, the MCP endpoint → mcp, the operator assistant → assistant, and an
/// unauthenticated caller (provider webhooks, gateways) → webhook.
/// </summary>
public sealed class RequestAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestAuditMiddleware> _log;
    private static readonly ConcurrentDictionary<int, int> ProfileUsers = new();

    /// <summary>Paths whose POSTs are reads or high-frequency noise, not actions.</summary>
    private static readonly string[] SkipPrefixes =
    {
        "/api/search",                    // global search box (read)
        "/api/messaging/email/sync",      // /chats nudges IMAP every 60s
        "/api/aicore/knowledge/preview",  // renders the AI's knowledge block (read)
        "/api/auth/notifications",        // mark-as-read on the bell
        "/api/assistant/chat",            // tool calls write their own rows
        "/api/widget/",                   // anonymous visitor traffic on customer sites
        "/mcp",                           // tools/call writes per-tool rows
    };

    private static readonly Regex SecretSegment =
        new(@"^(?=.*\d)(?=.*[A-Za-z])[A-Za-z0-9_\-]{24,}$", RegexOptions.Compiled);

    public RequestAuditMiddleware(RequestDelegate next, ILogger<RequestAuditMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isMcp = path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase);
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (!isApi && !isMcp)
        {
            await _next(ctx);
            return;
        }

        var method = ctx.Request.Method.ToUpperInvariant();
        var mutating = method is "POST" or "PUT" or "PATCH" or "DELETE";
        var user = ctx.User;
        var authed = user.Identity?.IsAuthenticated == true;

        var audit = new AuditContext
        {
            Source = SourceFor(path, isMcp, authed, user),
            Actor = authed ? user.Identity!.Name ?? "unknown" : AnonymousActor(path),
            UserId = authed ? await UserIdAsync(ctx, user) : null,
            Method = method,
            Path = RedactPath(path),
            Ip = ClientIp(ctx),
            UserAgent = AuditTrail.Cap(ctx.Request.Headers.UserAgent.ToString(), 300),
            RequestId = ctx.TraceIdentifier,
        };
        // Lets an API client quote the exact row it caused.
        ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;

        using var scope = AuditContext.Begin(audit);

        // A scoped key holding no write capability is read-only on the REST API.
        if (mutating && !isMcp && authed && ApiKeyAuth.IsReadOnlyKey(user))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                detail = "This API key is read-only — its scopes grant no write capability.",
            });
            await FinishAsync(ctx, audit, true, 403);
            return;
        }

        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await _next(ctx);
            status = ctx.Response.StatusCode;
        }
        finally
        {
            await FinishAsync(ctx, audit, mutating, status);
        }
    }

    private async Task FinishAsync(HttpContext ctx, AuditContext audit, bool mutating, int status)
    {
        try
        {
            var opts = ctx.RequestServices.GetRequiredService<
                Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext>>();
            await using var db = new AppDbContext(opts);

            // Domain rows written during the request learn how it ended.
            var ids = audit.Written.Where(r => r.Id > 0 && r.Status == null).Select(r => r.Id).ToList();
            if (ids.Count > 0)
                await db.AuditLogs.Where(a => ids.Contains(a.Id) && a.Status == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status));

            if (!mutating || audit.SkipRequestRow || audit.DomainRows > 0 || Skipped(ctx)) return;

            var endpoint = ctx.GetEndpoint() as RouteEndpoint;
            var template = endpoint?.RoutePattern.RawText;
            var row = new AuditLog
            {
                Actor = audit.Actor,
                UserId = audit.UserId,
                Source = audit.Source,
                Action = AuditTrail.Cap(ActionFor(audit.Method, template, audit.Path), 120),
                Target = AuditTrail.Cap(TargetFor(ctx), 300),
                Detail = status >= 400 ? $"failed ({status})" : "",
                EntityId = await EntityFromQueryAsync(db, ctx),
                Method = audit.Method,
                Path = audit.Path,
                Status = status,
                Ip = audit.Ip,
                UserAgent = audit.UserAgent,
                RequestId = audit.RequestId,
                CreatedAt = DateTime.UtcNow,
            };
            db.AuditLogs.Add(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "request audit row could not be written");
        }
    }

    private static bool Skipped(HttpContext ctx)
    {
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<SkipRequestAuditAttribute>() != null) return true;
        var path = ctx.Request.Path.Value ?? "";
        foreach (var p in SkipPrefixes)
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        // the inbox badge counter is a read even when it is POSTed
        if (path.StartsWith("/api/inbox/threads", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.Query.ContainsKey("count_only")) return true;
        return false;
    }

    // ---- provenance --------------------------------------------------------

    internal static string SourceFor(string path, bool isMcp, bool authed, ClaimsPrincipal user)
    {
        if (isMcp) return AuditSources.Mcp;
        if (path.StartsWith("/api/assistant", StringComparison.OrdinalIgnoreCase)) return AuditSources.Assistant;
        if (authed) return ApiKeyAuth.IsApiKey(user) ? AuditSources.Api : AuditSources.User;
        // A sign-in attempt is a person at the login form, not a webhook.
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) return AuditSources.User;
        return AuditSources.Webhook;
    }

    /// <summary>Provider callbacks and gateways read as "webhook"; any other
    /// unauthenticated caller (a sign-in attempt, an open legacy endpoint such
    /// as the call controls) as "anonymous".</summary>
    private static string AnonymousActor(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("/webhook") || p.Contains("/baileys/") || p.StartsWith("/api/widget/")
            ? "webhook" : "anonymous";
    }

    private static async Task<int?> UserIdAsync(HttpContext ctx, ClaimsPrincipal user)
    {
        if (int.TryParse(user.FindFirstValue("user_id"), out var direct)) return direct;
        if (!int.TryParse(user.FindFirstValue("profile_id"), out var pid)) return null;
        if (ProfileUsers.TryGetValue(pid, out var cached)) return cached;
        try
        {
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var uid = await db.Profiles.AsNoTracking().Where(p => p.Id == pid)
                .Select(p => (int?)p.UserId).FirstOrDefaultAsync();
            if (uid is int u) ProfileUsers[pid] = u;
            return uid;
        }
        catch { return null; }
    }

    private static string ClientIp(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        // Behind the bundled nginx every request comes from the proxy; trust its
        // X-Forwarded-For only when the direct peer is local/private.
        if (remote != null && (IPAddress.IsLoopback(remote) || IsPrivate(remote)))
        {
            var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
            var first = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(first) && IPAddress.TryParse(first, out _)) return first;
        }
        return remote?.ToString() ?? "";
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>The path with secret-looking segments (bot tokens, long keys) masked.</summary>
    internal static string RedactPath(string path)
    {
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Contains(':') || SecretSegment.IsMatch(parts[i])) parts[i] = "[redacted]";
        return AuditTrail.Cap(string.Join('/', parts), 300);
    }

    /// <summary>"POST /api/contacts/{id}" — the ROUTE, so one action groups every id.</summary>
    internal static string ActionFor(string method, string? template, string path)
    {
        var route = string.IsNullOrEmpty(template) ? path : template;
        route = Regex.Replace(route, @"\{([^}:?=]+)[^}]*\}", "{$1}");
        if (!route.StartsWith('/')) route = "/" + route;
        return $"{method} {route}";
    }

    private static string TargetFor(HttpContext ctx)
    {
        var bits = new List<string>();
        foreach (var (k, v) in ctx.Request.RouteValues)
        {
            var s = v?.ToString() ?? "";
            if (s.Length == 0) continue;
            if (s.Contains(':') || SecretSegment.IsMatch(s)) s = "[redacted]";
            bits.Add($"{k}={s}");
        }
        return string.Join(" ", bits);
    }

    private static async Task<int?> EntityFromQueryAsync(AppDbContext db, HttpContext ctx)
    {
        var slug = ctx.Request.Query["entity"].ToString();
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return null;
        try
        {
            return await db.Entities.AsNoTracking().Where(e => e.Slug == slug)
                .Select(e => (int?)e.Id).FirstOrDefaultAsync();
        }
        catch { return null; }
    }
}
