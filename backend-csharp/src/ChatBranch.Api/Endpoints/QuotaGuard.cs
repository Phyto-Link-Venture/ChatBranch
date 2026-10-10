using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Data.Ai;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The user side of the AI quotas (<see cref="AiQuota"/>): an interactive AI
/// feature a signed-in person runs themselves — Ask ChatBranch, the AI
/// playground, skill drafting, on-demand summaries — refuses once that
/// person's monthly cap is used up.
/// </summary>
public static class QuotaGuard
{
    /// <summary>The refusal sentence, or null when the caller may go on.</summary>
    public static string? UserBlocked(AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        AiQuota.UserBlock(db, sc, me.Identity?.Name) is { } s ? AiQuota.UserRefusal(s) : null;

    /// <summary>429 {detail, quota: true} when the caller's quota is used up; else null.</summary>
    public static IResult? Refusal(AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        UserBlocked(db, sc, me) is string detail
            ? Results.Json(new { detail, quota = true }, statusCode: StatusCodes.Status429TooManyRequests)
            : null;
}
