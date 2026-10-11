using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Stores;

/// <summary>
/// The schema side of "one conversation per customer PER COMPANY" (incident
/// 2026-10-11, a chat that crossed from one company into another): an existing
/// database is brought to the new identity on every boot, idempotently, on
/// SQLite and PostgreSQL alike —
///
/// <list type="number">
///   <item>backfill <c>crm_thread.company_entity_id</c>: the company (top-level
///         entity) of the thread's account, else of its own entity; a thread with
///         neither stays unattributed (null). At upgrade time (channel, key) is still
///         unique, so every slot is free; later runs only fill rows that can take one;</item>
///   <item>attribute the case-summary cache to its thread (<c>thread:{id}</c> instead
///         of the shared <c>channel:key</c> a second company's chat would also read);</item>
///   <item>create the two new unique indexes, THEN drop the old (channel, key) one —
///         a failed create leaves the old guarantee in place.</item>
/// </list>
/// </summary>
public static class ThreadCompanies
{
    /// <summary>
    /// The /chats deep link to ONE conversation: <c>thread=channel:key</c> (what older links
    /// and people read) plus <c>thread_id</c> — the customer may have one per company, and the
    /// id is what picks this one.
    /// </summary>
    public static string ChatHref(string channel, string key, int? threadId) =>
        $"/chats?thread={Uri.EscapeDataString(channel)}:{Uri.EscapeDataString(key)}"
        + (threadId is int id && id > 0 ? $"&thread_id={id}" : "");

    public static string ChatHref(Domain.Thread t) => ChatHref(t.Channel, t.Key, t.Id);

    /// <summary>The older <c>/chats?channel=…&amp;key=…</c> link form (what notifications, search
    /// and the assistant have always handed out), plus <c>thread_id</c>.</summary>
    public static string LegacyChatHref(string channel, string key, int? threadId) =>
        $"/chats?channel={Uri.EscapeDataString(channel)}&key={Uri.EscapeDataString(key)}"
        + (threadId is int id && id > 0 ? $"&thread_id={id}" : "");

    public static string LegacyChatHref(Domain.Thread t) => LegacyChatHref(t.Channel, t.Key, t.Id);

    /// <summary>The Ref a thread's stored case summary carries (crm_conversationsummary.ref).</summary>
    public static string SummaryRef(int threadId) => $"thread:{threadId}";

    /// <summary>The thread id of a <see cref="SummaryRef"/>, else null (a flow note, a legacy row…).</summary>
    public static int? ThreadOfSummaryRef(string? reference) =>
        reference != null && reference.StartsWith("thread:", StringComparison.Ordinal)
        && int.TryParse(reference.AsSpan(7), out var id) ? id : null;

    /// <summary>
    /// A number was re-bound to another entity: its conversations of the SAME company (or
    /// unattributed ones) follow it; a conversation of another company keeps its company and
    /// entity and lets go of the number (routing finds it one of its own) — re-binding a
    /// number never carries a conversation, or its history, into another company. Does not save.
    /// </summary>
    public static void AfterAccountRebound(AppDbContext db, ScopingService sc, int accountId)
    {
        var account = db.ChannelAccounts.Find(accountId);
        var accountCompany = sc.AccountCompany(account);
        foreach (var t in db.Threads.Include(x => x.Contact).Include(x => x.Account)
                     .Where(x => x.AccountId == accountId).ToList())
        {
            if (!ScopingService.SameCompanyOrShared(sc.ThreadCompany(t), accountCompany))
            {
                t.AccountId = null;
                t.Account = null;
                t.UpdatedAt = DateTime.UtcNow;
            }
            sc.SyncThreadEntity(t, save: false);
        }
    }

    public static void Upgrade(AppDbContext db)
    {
        Backfill(db);
        AttributeSummaries(db);
        db.Database.ExecuteSqlRaw(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{AppDbContext.ThreadCompanyIndex}\" ON \"crm_thread\" "
            + "(\"channel\", \"key\", \"company_entity_id\") WHERE \"company_entity_id\" IS NOT NULL");
        db.Database.ExecuteSqlRaw(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{AppDbContext.ThreadUnattributedIndex}\" ON \"crm_thread\" "
            + "(\"channel\", \"key\") WHERE \"company_entity_id\" IS NULL");
        db.Database.ExecuteSqlRaw($"DROP INDEX IF EXISTS \"{AppDbContext.LegacyThreadIndex}\"");
    }

    /// <summary>Fill the company of every thread that has an account or entity to take it from. Returns the rows set.</summary>
    public static int Backfill(AppDbContext db)
    {
        var rows = db.Threads.AsNoTracking()
            .Where(t => t.CompanyEntityId == null && (t.EntityId != null || t.AccountId != null))
            .Select(t => new { t.Id, t.Channel, t.Key, t.EntityId, AccountEntity = t.Account != null ? t.Account.EntityId : null })
            .ToList();
        if (rows.Count == 0) return 0;
        var sc = new ScopingService(db);
        var companies = sc.CompanyMap();
        int? Company(int? entity) => entity is int e && companies.TryGetValue(e, out var c) ? c : null;
        // the slots already taken, so two rows never claim the same (channel, key, company)
        var taken = db.Threads.AsNoTracking().Where(t => t.CompanyEntityId != null)
            .Select(t => new { t.Channel, t.Key, t.CompanyEntityId }).AsEnumerable()
            .Select(t => (t.Channel, t.Key, t.CompanyEntityId!.Value)).ToHashSet();
        var set = 0;
        foreach (var r in rows.OrderBy(r => r.Id))
        {
            var company = Company(r.AccountEntity) ?? Company(r.EntityId);
            if (company is not int c || !taken.Add((r.Channel, r.Key, c))) continue;
            // the entity always sits inside the company: a drifted one follows the account
            var entity = Company(r.EntityId) == c ? r.EntityId : r.AccountEntity ?? c;
            set += db.Threads.Where(t => t.Id == r.Id && t.CompanyEntityId == null)
                .ExecuteUpdate(u => u.SetProperty(t => t.CompanyEntityId, (int?)c)
                    .SetProperty(t => t.EntityId, entity));
        }
        return set;
    }

    /// <summary>
    /// Case summaries were cached under "channel:key" — the key every company's
    /// conversation with that customer shares. Each legacy row is moved to the ONE
    /// thread that held that key when it was written ("thread:{id}"); a key that
    /// already has several threads keeps its legacy row, which nothing reads any more.
    /// </summary>
    public static int AttributeSummaries(AppDbContext db)
    {
        var legacy = db.ConversationSummaries.AsNoTracking()
            .Where(s => s.Source == "chat" && !s.Ref.StartsWith("thread:"))
            .Select(s => new { s.Id, s.Ref }).ToList();
        var moved = 0;
        foreach (var s in legacy)
        {
            var at = s.Ref.IndexOf(':');
            if (at <= 0) continue;
            var channel = s.Ref[..at];
            var key = s.Ref[(at + 1)..];
            var ids = db.Threads.AsNoTracking().Where(t => t.Channel == channel && t.Key == key)
                .Select(t => t.Id).Take(2).ToList();
            if (ids.Count != 1) continue;
            var newRef = SummaryRef(ids[0]);
            moved += db.ConversationSummaries.Where(x => x.Id == s.Id)
                .ExecuteUpdate(u => u.SetProperty(x => x.Ref, newRef));
        }
        return moved;
    }
}
