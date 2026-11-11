using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Data.Ai;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The user side of the AI usage limits: an interactive AI feature a signed-in
/// person runs themselves — Ask ChatBranch, the AI playground, skill drafting,
/// on-demand summaries — refuses once
/// <list type="bullet">
/// <item>that person's monthly AI quota is used up (<see cref="AiQuota"/>), or</item>
/// <item>that person's own per-login AI credit is used up (an ENABLED per-login
/// account only — <see cref="AiCreditGuard.UserBlock"/>), or</item>
/// <item>EVERY company the person is bound to is on prepaid AI credit and out of it
/// (<see cref="AiCreditGuard.EntityBlock"/>). A login bound to no entity — a
/// superadmin or platform user — is never refused for a company's credit, so
/// nobody can lock themselves out of the dashboard.</item>
/// </list>
/// A company with no credit account, or with credit switched off, never refuses.
/// </summary>
public static class QuotaGuard
{
    /// <summary>The refusal sentence, or null when the caller may go on.</summary>
    public static string? UserBlocked(AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        Blocked(db, sc, me)?.Detail;

    /// <summary>429 {detail, quota: true} (plus credit: true for a credit refusal) when the caller
    /// may not run AI; else null.</summary>
    public static IResult? Refusal(AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        Blocked(db, sc, me) is { } b
            ? Results.Json(b.Credit ? new { detail = b.Detail, quota = true, credit = true } as object
                    : new { detail = b.Detail, quota = true },
                statusCode: StatusCodes.Status429TooManyRequests)
            : null;

    private sealed record Block(string Detail, bool Credit);

    private static Block? Blocked(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var username = me.Identity?.Name;
        if (AiQuota.UserBlock(db, sc, username) is { } s) return new Block(AiQuota.UserRefusal(s), false);
        if (AiCreditGuard.UserBlock(db, sc, username) is { } own) return new Block(AiCreditGuard.UserRefusal(own), true);
        return CompanyCredit(db, sc, username) is { } co ? new Block(AiCreditGuard.CompanyRefusal(co), true) : null;
    }

    /// <summary>An exhausted company balance when EVERY entity the caller is bound to draws on one; else null.</summary>
    private static AiCreditStatus? CompanyCredit(AppDbContext db, ScopingService sc, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            var assigned = ContactsEndpoints.AssignedEntityIds(db, username);
            if (assigned.Count == 0) return null;   // unbound (superadmin / platform): never locked out
            AiCreditStatus? first = null;
            foreach (var id in assigned)
            {
                if (AiCreditGuard.EntityBlock(db, sc, id) is not { } s) return null;   // one company can still pay
                first ??= s;
            }
            return first;
        }
        catch { return null; }   // fails open, like every other credit read
    }
}
