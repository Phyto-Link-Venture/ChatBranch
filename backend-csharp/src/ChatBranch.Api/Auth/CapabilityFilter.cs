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
        where TBuilder : IEndpointConventionBuilder => builder.RequireAnyCapability(capability);

    /// <summary>Passes when the caller holds ANY of <paramref name="capabilities"/>.</summary>
    public static TBuilder RequireAnyCapability<TBuilder>(this TBuilder builder, params string[] capabilities)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var role = http.User.FindFirstValue(ClaimTypes.Role) ?? "";
            foreach (var capability in capabilities)
                if (await AdminEndpoints.HasCapabilityAsync(db, role, capability)) return await next(ctx);
            var need = capabilities.Length == 1
                ? $"The {capabilities[0]} capability is required."
                : $"One of these capabilities is required: {string.Join(", ", capabilities)}.";
            return Results.Json(new { detail = need, error = need }, statusCode: StatusCodes.Status403Forbidden);
        });
}
