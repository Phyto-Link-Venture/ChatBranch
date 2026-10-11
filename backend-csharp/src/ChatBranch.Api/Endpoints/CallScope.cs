using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Which AI voice calls (<see cref="CallRecord"/>) a person may see — the contact
/// page's calls, the channel hub's voice row, the timeline, call logs and the
/// recordings. Superadmins, organization-wide admins and unassigned agents (the
/// tenant view) see every call. Someone bound to entities sees a call placed FOR
/// one of their entities (<see cref="CallRecord.EntityId"/>, stamped from the
/// dispatch); an older, unstamped call only when its contact belongs to their
/// company alone (linked inside their scope and nowhere else) — a call another
/// company placed never shows, even on a contact both companies can open.
/// </summary>
internal static class CallScope
{
    internal static List<CallRecord> Visible(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        List<CallRecord> rows)
    {
        var bound = ShellScope.BoundEntityIds(db, sc, me);
        if (bound == null || rows.Count == 0) return rows;
        var set = bound.ToHashSet();
        var companies = ShellScope.ScopeRoots(db, me).Select(r => sc.CompanyOf(r)).OfType<int>().ToHashSet();
        var contactIds = rows.Where(r => r.EntityId == null && r.ContactId != null)
            .Select(r => r.ContactId!.Value).Distinct().ToList();
        var links = db.ContactEntities.AsNoTracking().Where(l => contactIds.Contains(l.ContactId))
            .Select(l => new { l.ContactId, l.EntityId }).ToList();
        var byContact = links.GroupBy(l => l.ContactId).ToDictionary(g => g.Key, g => g.Select(x => x.EntityId).ToList());
        return rows.Where(r => r.EntityId is int e
                ? set.Contains(e)
                : r.ContactId is int c && byContact.TryGetValue(c, out var ents) && ents.Any(set.Contains)
                  && ents.All(x => sc.CompanyOf(x) is int co && companies.Contains(co)))
            .ToList();
    }

    internal static Task<List<CallRecord>> VisibleAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        List<CallRecord> rows) => Task.FromResult(Visible(db, sc, me, rows));

    /// <summary>One call by id: visible to the caller (no record yet = organization-level, unbound only).</summary>
    internal static async Task<bool> CallVisibleAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string callId)
    {
        var record = await db.CallRecords.AsNoTracking().FirstOrDefaultAsync(r => r.CallId == callId);
        if (record == null) return ShellScope.BoundEntityIds(db, sc, me) == null;
        return Visible(db, sc, me, new List<CallRecord> { record }).Count == 1;
    }
}
