using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Where a conversation a PERSON starts belongs (a new chat, a first send on a key
/// the caller has no conversation with yet): one conversation per customer PER
/// COMPANY, so a new one needs its company —
/// <list type="number">
///   <item>the sending number's company, when one is named;</item>
///   <item>the <c>entity</c> / <c>company</c> the request names (inside the caller's scope);</item>
///   <item>the caller's own company when they are bound to entities of exactly one;</item>
///   <item>the matched contact's one company;</item>
///   <item>else none — the unattributed conversation (an organization-wide view's).</item>
/// </list>
/// </summary>
internal static class ThreadStart
{
    /// <summary>
    /// Why a placement was refused, so a caller can answer in its own words and status.
    /// <c>POST /api/messaging/threads</c> uses <see cref="Place.Error"/> as it always has;
    /// <c>POST /api/messaging/notify</c> must never answer 404 (a client reads that as
    /// "no such route"), so it maps this instead.
    /// </summary>
    internal enum Why { None, UnknownEntity, OutOfScope, CompanyRequired }

    internal sealed record Place(IResult? Error, int? Company, int? Entity, Why Failure = Why.None);

    internal static async Task<Place> PlaceAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string? entityHint = null, string? companyHint = null, int? contactId = null, int? accountId = null)
    {
        var roots = ShellScope.ScopeRoots(db, me);
        var mine = roots.Select(r => sc.CompanyOf(r)).OfType<int>().Distinct().ToList();
        int? RootIn(int company) => roots.FirstOrDefault(x => sc.CompanyOf(x) == company) is var hit && hit != 0 ? hit : company;

        // 1. the number the conversation goes out on decides
        if (!ShellScope.AccountAllowed(db, me, accountId))
            return new Place(Forbidden("That channel isn't one of yours — ask a manager for access."), null, null,
                Why.OutOfScope);
        if (sc.AccountCompany(accountId) is int byAccount)
        {
            if (roots.Count > 0 && !mine.Contains(byAccount))
                return new Place(Forbidden("That number belongs to a company outside your scope."), null, null,
                    Why.OutOfScope);
            var accountEntity = await db.ChannelAccounts.AsNoTracking().Where(a => a.Id == accountId)
                .Select(a => a.EntityId).FirstOrDefaultAsync();
            return new Place(null, byAccount, accountEntity);
        }
        // 2. what the request names
        if (!string.IsNullOrWhiteSpace(entityHint))
        {
            var entity = await InboxEndpoints.EntityId(db, entityHint.Trim());
            if (entity is not int e) return new Place(NotFound("Entity not found."), null, null, Why.UnknownEntity);
            var bound = ShellScope.BoundEntityIds(db, sc, me);
            if (bound != null && !bound.Contains(e))
                return new Place(Forbidden("That entity is outside your scope."), null, null, Why.OutOfScope);
            return new Place(null, sc.CompanyOf(e), e);
        }
        if (!string.IsNullOrWhiteSpace(companyHint))
        {
            var (unknown, company) = await Auth.ThreadResolver.CompanyIdAsync(db, sc, companyHint);
            if (unknown) return new Place(NotFound("Company not found."), null, null, Why.UnknownEntity);
            if (company is int c)
            {
                if (roots.Count > 0 && !mine.Contains(c))
                    return new Place(Forbidden("That company is outside your scope."), null, null, Why.OutOfScope);
                return new Place(null, c, roots.Count > 0 ? RootIn(c) : c);
            }
            return roots.Count > 0
                ? new Place(Forbidden("Your account is limited to its companies — pick one of them."), null, null,
                    Why.OutOfScope)
                : new Place(null, null, null);
        }
        var contactCompanies = contactId is int cid ? ContactCompanies.LinkCompanies(db, sc, cid) : new HashSet<int>();
        // 3. the caller's own company
        if (mine.Count == 1) return new Place(null, mine[0], RootIn(mine[0]));
        if (mine.Count > 1)
        {
            var shared = contactCompanies.Where(mine.Contains).ToList();
            if (shared.Count == 1) return new Place(null, shared[0], RootIn(shared[0]));
            return new Place(Results.Json(new
            {
                detail = "You work for more than one company — say which one this conversation is for "
                         + "(company or entity).",
                code = "company_required",
            }, statusCode: StatusCodes.Status400BadRequest), null, null, Why.CompanyRequired);
        }
        // 4. the contact's one company, 5. else unattributed
        return contactCompanies.Count == 1 ? new Place(null, contactCompanies.First(), null) : new Place(null, null, null);
    }

    private static IResult Forbidden(string detail) => Results.Json(new { detail }, statusCode: StatusCodes.Status403Forbidden);
    private static IResult NotFound(string detail) => Results.Json(new { detail }, statusCode: StatusCodes.Status404NotFound);
}
