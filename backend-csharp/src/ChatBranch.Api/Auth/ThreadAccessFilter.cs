using System.Security.Claims;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;

namespace ChatBranch.Api.Auth;

/// <summary>
/// The guard on every per-conversation route (<c>/api/whatsapp/threads/{key}/…</c>,
/// <c>/api/messaging/threads/{channel}/{key}/…</c>, the inbox's
/// <c>{channel}/{key}</c> routes): the caller needs <paramref name="capability"/>
/// AND the route must name ONE conversation they may see
/// (<see cref="ThreadResolver"/>: inside their entity scope — unassigned
/// conversations visible to everyone — and, when the customer has a
/// conversation with several companies the caller sees, the <c>thread_id</c> /
/// <c>company</c> query parameter that says which; without it → 409 with the
/// candidates). An unknown <c>thread_id</c> answers 404, the same as a
/// conversation that does not exist, so existence doesn't leak. A key with no
/// conversation of the caller's yet passes through (a first send creates one,
/// in the caller's company); the handler reads the pick with
/// <see cref="ThreadResolver.LoadAsync"/>, never by channel + key again.
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
            var pick = await ThreadResolver.ForRequestAsync(http);
            if (ThreadResolver.Refusal(pick) is { } refused) return refused;
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

    /// <summary>
    /// The full rule for a conversation an endpoint found itself: entity scope AND the
    /// caller's channel allow-list (<see cref="ShellScope.ThreadVisible"/>). Use this
    /// whenever there is a conversation row; the entity-only overload is for placing a
    /// NEW one.
    /// </summary>
    public static bool InScope(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        ChatBranch.Domain.Thread thread) =>
        ShellScope.ThreadVisible(thread.EntityId, thread.AccountId,
            ShellScope.ThreadEntityIds(db, sc, me, null), ShellScope.ChannelAccountIds(db, me));
}
