using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// "The same person" is decided PER COMPANY (a top-level entity and its
/// branches): one human can be a contact of Lumina and of SwiftHaul, each company
/// keeping its own record, history and tags. Every path that asks "who is this
/// number / address?" goes through here:
///
/// * <see cref="FindClash"/> — the duplicate guard for create / edit / import:
///   a phone (last-8-digit rule, <see cref="PhoneMatch"/>) or email already used
///   by a contact of one of the SAME companies the new one will belong to; a
///   contact with no entity (organization-wide) clashes only with other
///   organization-wide contacts of the same tenant.
/// * <see cref="Pick"/> — inbound matching when several contacts share a phone or
///   email: the one in the receiving number's entity subtree, else in its
///   company, else an organization-wide one — never another company's. With no
///   entity on the conversation it keeps the oldest match (the old behaviour).
/// * <see cref="LinkToEntity"/> — a contact made from a conversation is linked to
///   that conversation's entity, so it shows under its company.
/// </summary>
public static class ContactCompanies
{
    /// <summary>The top-level entity (company) an entity belongs to.</summary>
    public static int CompanyOf(ScopingService sc, int entityId) => sc.CompanyOf(entityId) ?? entityId;

    public static HashSet<int> CompaniesOf(ScopingService sc, IEnumerable<int> entityIds) =>
        entityIds.Distinct().Select(e => CompanyOf(sc, e)).ToHashSet();

    /// <summary>The companies a contact is LINKED to (its entity links' top-level entities).</summary>
    public static HashSet<int> LinkCompanies(AppDbContext db, ScopingService sc, int contactId) =>
        CompaniesOf(sc, db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contactId)
            .Select(l => l.EntityId).ToList());

    /// <summary>
    /// May a conversation of <paramref name="company"/> be linked to this contact: a
    /// contact of that company (a link inside it), or an organization-wide contact (no
    /// links). An unattributed conversation (null) may take any contact — it then
    /// becomes that contact's company's. A contact of ANOTHER company never.
    /// </summary>
    public static bool MayLink(AppDbContext db, ScopingService sc, int contactId, int? company)
    {
        if (company is not int c) return true;
        var mine = LinkCompanies(db, sc, contactId);
        return mine.Count == 0 || mine.Contains(c);
    }

    /// <summary>
    /// Every company a contact's data may have come from: its entity links' companies
    /// plus the companies of the conversations linked to it.
    /// </summary>
    public static HashSet<int> DataCompanies(AppDbContext db, ScopingService sc, int contactId)
    {
        var set = LinkCompanies(db, sc, contactId);
        foreach (var t in db.Threads.AsNoTracking().Where(t => t.ContactId == contactId)
                     .Select(t => new { t.CompanyEntityId, t.EntityId }).ToList())
            if ((t.CompanyEntityId ?? sc.CompanyOf(t.EntityId)) is int c) set.Add(c);
        return set;
    }

    /// <summary>
    /// May contact-LEVEL memory (the profile summary, preferences, timeline / flow
    /// notes — anything not tied to one conversation) be used when <paramref name="company"/>
    /// answers this contact: only when every company the contact's data may have come
    /// from IS that company. A contact shared by several companies keeps it out, and an
    /// unattributed conversation (null) only uses memory no company wrote.
    /// </summary>
    public static bool MemoryAllowed(AppDbContext db, ScopingService sc, int contactId, int? company)
    {
        try
        {
            var from = DataCompanies(db, sc, contactId);
            return company is int c ? from.All(x => x == c) : from.Count == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// The contact's tag names <paramref name="company"/> may see: every tag when the
    /// contact's data is all that company's (<paramref name="allOwn"/>), else only the
    /// organization-wide tags and the company's own — another company's tags never
    /// reach its AI.
    /// </summary>
    public static List<string> VisibleTags(AppDbContext db, ScopingService sc, Contact c, int? company, bool allOwn)
    {
        if (allOwn || c.Tags.Count == 0) return c.Tags;
        try
        {
            var names = c.Tags.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
            var ok = db.Tags.AsNoTracking().Where(t => t.AppliesTo == "contact").ToList()
                .Where(t => names.Contains(t.Name.Trim().ToLowerInvariant()))
                .Where(t => t.EntityId == null || (company != null && sc.CompanyOf(t.EntityId) == company))
                .Select(t => t.Name.Trim().ToLowerInvariant()).ToHashSet();
            return c.Tags.Where(t => ok.Contains(t.Trim().ToLowerInvariant())).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>A duplicate found by <see cref="FindClash"/>.</summary>
    public sealed record Clash(int ContactId, string Name, string Field);

    /// <summary>
    /// The contact that already uses this phone or email in one of the companies
    /// of <paramref name="entityIds"/> (none = the organization-wide contacts of
    /// the tenant), or null. Contacts of OTHER companies never clash.
    /// </summary>
    public static Clash? FindClash(AppDbContext db, ScopingService sc, int? tenantId, string? phone,
        string? email, IReadOnlyCollection<int> entityIds, int? excludeId = null)
    {
        var tail = PhoneMatch.Tail(phone);
        var mail = (email ?? "").Trim().ToLowerInvariant();
        if (tail == null && mail.Length == 0) return null;
        var q = db.Contacts.AsNoTracking().Where(c => tenantId == null || c.TenantId == tenantId);
        if (excludeId is int ex) q = q.Where(c => c.Id != ex);
        var rows = q.Where(c => (tail != null && c.Phone != "") || (mail != "" && c.Email.ToLower() == mail))
            .Select(c => new { c.Id, c.Name, c.Phone, c.Email }).ToList();
        var hits = rows.Where(r => (tail != null && PhoneMatch.Tail(r.Phone) == tail)
                                   || (mail.Length > 0 && r.Email.Trim().ToLowerInvariant() == mail))
            .OrderBy(r => r.Id).ToList();
        if (hits.Count == 0) return null;

        var mine = CompaniesOf(sc, entityIds);
        var ids = hits.Select(h => h.Id).ToList();
        var links = db.ContactEntities.AsNoTracking().Where(l => ids.Contains(l.ContactId))
            .Select(l => new { l.ContactId, l.EntityId }).ToList();
        foreach (var h in hits)
        {
            var theirs = CompaniesOf(sc, links.Where(l => l.ContactId == h.Id).Select(l => l.EntityId));
            var clash = mine.Count == 0 ? theirs.Count == 0 : theirs.Overlaps(mine);
            if (!clash) continue;
            var field = tail != null && PhoneMatch.Tail(h.Phone) == tail ? "phone" : "email";
            return new Clash(h.Id, h.Name, field);
        }
        return null;
    }

    /// <summary>
    /// Among the contacts matching an inbound identity, the one that belongs to
    /// the conversation's entity: linked inside its subtree, else inside its
    /// company, else an organization-wide contact (no entity links). Never a
    /// contact that only belongs to another company. No entity ⇒ the oldest match.
    /// </summary>
    public static Contact? Pick(AppDbContext db, ScopingService sc, IReadOnlyCollection<Contact> candidates,
        int? entityId)
    {
        var ordered = candidates.OrderBy(c => c.Id).ToList();
        if (ordered.Count == 0) return null;
        if (entityId is not int eid) return ordered[0];
        var ids = ordered.Select(c => c.Id).ToList();
        var links = db.ContactEntities.AsNoTracking().Where(l => ids.Contains(l.ContactId))
            .Select(l => new { l.ContactId, l.EntityId }).ToList();
        var subtree = sc.EntitySubtreeIds(eid).ToHashSet();
        var company = CompanyOf(sc, eid);
        var companyTree = sc.EntitySubtreeIds(company).ToHashSet();
        Contact? inCompany = null, orgWide = null;
        foreach (var c in ordered)
        {
            var mine = links.Where(l => l.ContactId == c.Id).Select(l => l.EntityId).ToList();
            if (mine.Any(subtree.Contains)) return c;
            if (inCompany == null && mine.Any(companyTree.Contains)) inCompany = c;
            if (orgWide == null && mine.Count == 0) orgWide = c;
        }
        return inCompany ?? orgWide;
    }

    /// <summary>The contact a phone-keyed inbound belongs to (see <see cref="Pick"/>).</summary>
    public static Contact? MatchPhone(AppDbContext db, ScopingService sc, string? phone, int? entityId)
    {
        var tail = PhoneMatch.Tail(phone);
        if (tail == null) return null;
        var hits = db.Contacts.Where(c => c.Phone != "").AsEnumerable()
            .Where(c => PhoneMatch.Tail(c.Phone) == tail).ToList();
        return Pick(db, sc, hits, entityId);
    }

    /// <summary>The contact an email inbound belongs to (see <see cref="Pick"/>).</summary>
    public static Contact? MatchEmail(AppDbContext db, ScopingService sc, string? email, int? entityId)
    {
        var mail = (email ?? "").Trim().ToLowerInvariant();
        if (mail.Length == 0) return null;
        var hits = db.Contacts.Where(c => c.Email.ToLower() == mail).ToList();
        return Pick(db, sc, hits, entityId);
    }

    /// <summary>Link a contact to an entity (idempotent; no-op without one). Does not save.</summary>
    public static void LinkToEntity(AppDbContext db, int contactId, int? entityId)
    {
        if (entityId is not int eid || contactId <= 0) return;
        if (db.ContactEntities.Any(l => l.ContactId == contactId && l.EntityId == eid)) return;
        if (db.ContactEntities.Local.Any(l => l.ContactId == contactId && l.EntityId == eid)) return;
        db.ContactEntities.Add(new ContactEntity { ContactId = contactId, EntityId = eid, CreatedAt = DateTime.UtcNow });
    }

    /// <summary>
    /// One-time backfill (idempotent): every contact with NO entity links that has
    /// conversations filed under an entity is linked to those entities — contacts
    /// made from chats before contacts were linked on creation. Returns the links added.
    /// </summary>
    public static int Backfill(AppDbContext db)
    {
        var pairs = db.Threads.AsNoTracking()
            .Where(t => t.ContactId != null && t.EntityId != null)
            .Where(t => !db.ContactEntities.Any(l => l.ContactId == t.ContactId))
            .Select(t => new { ContactId = t.ContactId!.Value, EntityId = t.EntityId!.Value })
            .Distinct().ToList();
        foreach (var p in pairs)
            db.ContactEntities.Add(new ContactEntity { ContactId = p.ContactId, EntityId = p.EntityId, CreatedAt = DateTime.UtcNow });
        if (pairs.Count > 0) db.SaveChanges();
        return pairs.Count;
    }
}
