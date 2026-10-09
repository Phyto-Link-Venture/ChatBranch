using System.Security.Claims;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Auth;

/// <summary>
/// The guard on every per-conversation route (<c>/api/whatsapp/threads/{key}/…</c>,
/// <c>/api/messaging/threads/{channel}/{key}/…</c>, the inbox's
/// <c>{channel}/{key}</c> routes): the caller needs <paramref name="capability"/>
/// AND must be able to see the conversation under the inbox's rule — inside their
/// entity scope (<see cref="ShellScope.ThreadEntityIds"/>), unassigned
/// conversations visible to everyone. A conversation outside it answers 404, the
/// same as one that does not exist, so its existence doesn't leak either. A key
/// with no conversation yet passes through (a first send creates it).
/// </summary>
public static class ThreadAccessFilter
{
    public static TBuilder RequireThreadAccess<TBuilder>(this TBuilder builder, string capability)
        where TBuilder : IEndpointConventionBuilder =>
        // the metadata lets the route-table audit (PermissionGatesTests) see the gate
        builder.WithMetadata(new RequiredCapabilities(new[] { capability })).AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var role = http.User.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, capability))
            {
                var need = $"The {capability} capability is required.";
                return Results.Json(new { detail = need, error = need }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (!await VisibleAsync(http, db))
                return Results.Json(new { detail = "Thread not found." }, statusCode: StatusCodes.Status404NotFound);
            return await next(ctx);
        });

    /// <summary>
    /// The same rule for a conversation an endpoint found itself (a body that
    /// names it, a get-or-create): inside the caller's entity scope, or unassigned.
    /// </summary>
    public static bool InScope(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? threadEntityId)
    {
        if (threadEntityId == null) return true;
        var allowed = ShellScope.ThreadEntityIds(db, sc, me, null);
        return allowed == null || allowed.Contains(threadEntityId.Value);
    }

    /// <summary>True unless a conversation the route names lies outside the caller's scope.</summary>
    private static async Task<bool> VisibleAsync(HttpContext http, AppDbContext db)
    {
        var values = http.Request.RouteValues;
        var raw = (values.TryGetValue("key", out var k) ? k?.ToString() : null) ?? "";
        var channel = (values.TryGetValue("channel", out var c) ? c?.ToString() : null)
                      ?? ChatBranch.Domain.Channels.WhatsApp;
        // the lookups the handlers use: a trim (messaging store) and, for WhatsApp, digits only
        var keys = new List<string> { raw, raw.Trim() };
        if (channel == ChatBranch.Domain.Channels.WhatsApp) keys.Add(new string(raw.Where(char.IsDigit).ToArray()));
        keys = keys.Where(x => x.Length > 0).Distinct().ToList();
        if (keys.Count == 0) return true;

        var entities = await db.Threads.AsNoTracking()
            .Where(t => t.Channel == channel && keys.Contains(t.Key))
            .Select(t => t.EntityId).ToListAsync();
        if (entities.Count == 0) return true;
        var sc = http.RequestServices.GetRequiredService<ScopingService>();
        var allowed = ShellScope.ThreadEntityIds(db, sc, http.User, null);
        return allowed == null || entities.All(e => e == null || allowed.Contains(e.Value));
    }
}
