using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Analytics;

namespace ChatBranch.Api.Endpoints;

/// <summary>Analytics surface (/api/analytics/*): the message/call summary and LLM usage/billing.</summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/summary", async (int? days, string? entity,
            AppDbContext db, AnalyticsService analytics) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(analytics.Summary(days ?? 30, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/analytics/usage", async (int? days, string? entity,
            AppDbContext db, AnalyticsService analytics) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(analytics.Usage(days ?? 30, entityId));
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }
}
