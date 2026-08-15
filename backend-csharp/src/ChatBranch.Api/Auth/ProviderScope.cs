using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Auth;

/// <summary>
/// "Their own world" for a provider login. A user whose Profile is bound to a
/// Service Provider (Profile.ResourceId → the <c>resource_id</c> claim) must
/// only ever see the customers bound to THEM (crm.ResourceLink), those
/// customers' conversations, and their own bookings. This is the ONE place
/// that rule lives — endpoints call these helpers instead of re-deriving it,
/// so a provider can never widen their view by hitting a different endpoint.
///
/// For operator logins (no resource_id claim) every helper is a no-op passthrough.
/// </summary>
public static class ProviderScope
{
    /// <summary>The caller's Service Provider id, or null for a normal operator.</summary>
    public static int? ResourceId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("resource_id"), out var id) ? id : null;

    public static bool IsProvider(ClaimsPrincipal me) => ResourceId(me) != null;

    /// <summary>Contacts → only those bound to the caller's provider (any relationship, active links).</summary>
    public static IQueryable<Contact> Contacts(IQueryable<Contact> qs, ClaimsPrincipal me, AppDbContext db)
    {
        if (ResourceId(me) is not int rid) return qs;
        var bound = db.ResourceLinks.Where(l => l.ResourceId == rid && l.Active).Select(l => l.ContactId);
        return qs.Where(c => bound.Contains(c.Id));
    }

    /// <summary>Threads → only conversations with the caller's bound customers.</summary>
    public static IQueryable<Thread> Threads(IQueryable<Thread> qs, ClaimsPrincipal me, AppDbContext db)
    {
        if (ResourceId(me) is not int rid) return qs;
        var bound = db.ResourceLinks.Where(l => l.ResourceId == rid && l.Active).Select(l => l.ContactId);
        return qs.Where(t => t.ContactId != null && bound.Contains(t.ContactId.Value));
    }

    /// <summary>Records (bookings/orders) → only the caller's own, or their bound customers'.</summary>
    public static IQueryable<Record> Records(IQueryable<Record> qs, ClaimsPrincipal me, AppDbContext db)
    {
        if (ResourceId(me) is not int rid) return qs;
        var bound = db.ResourceLinks.Where(l => l.ResourceId == rid && l.Active).Select(l => l.ContactId);
        return qs.Where(r => r.ResourceId == rid || bound.Contains(r.ContactId));
    }

    /// <summary>Resources → a provider sees only themselves.</summary>
    public static IQueryable<Resource> Resources(IQueryable<Resource> qs, ClaimsPrincipal me)
    {
        if (ResourceId(me) is not int rid) return qs;
        return qs.Where(r => r.Id == rid);
    }

    /// <summary>
    /// May the caller act on this provider's bindings? Operators: yes. A provider:
    /// only their own (they may bind/unbind THEIR customers, nobody else's).
    /// </summary>
    public static bool CanManageResource(ClaimsPrincipal me, int resourceId) =>
        ResourceId(me) is not int rid || rid == resourceId;

    /// <summary>Is this contact inside the caller's world? (operators: always)</summary>
    public static async Task<bool> CanSeeContactAsync(ClaimsPrincipal me, AppDbContext db, int contactId)
    {
        if (ResourceId(me) is not int rid) return true;
        return await db.ResourceLinks.AnyAsync(l => l.ResourceId == rid && l.Active && l.ContactId == contactId);
    }
}
