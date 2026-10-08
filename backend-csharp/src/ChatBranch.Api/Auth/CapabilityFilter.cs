using System.Security.Claims;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Gate an endpoint on one capability from the dynamic matrix (Permissions
/// page), instead of a role literal — so the server agrees with the sidebar,
/// which shows a page to anyone holding its capability. Superadmin always
/// passes (HasCapabilityAsync hard-codes it). Pair with
/// RequireAuthorization(Authenticated) so anonymous callers still get a 401.
/// </summary>
public static class CapabilityFilter
{
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, string capability)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var role = http.User.FindFirstValue(ClaimTypes.Role) ?? "";
            if (await AdminEndpoints.HasCapabilityAsync(db, role, capability)) return await next(ctx);
            return Results.Json(new
            {
                detail = $"The {capability} capability is required.",
                error = $"The {capability} capability is required.",
            }, statusCode: StatusCodes.Status403Forbidden);
        });
}
