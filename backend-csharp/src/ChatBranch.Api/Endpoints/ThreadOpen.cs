using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Finding or opening the caller's conversation with an address — the one copy of that
/// logic, shared by <c>POST /api/messaging/threads</c> (an operator starting a chat) and
/// <c>POST /api/messaging/notify</c> (an app sending a transactional message). Both name
/// the conversation in the BODY rather than the path, so both resolve it the same way and
/// must stay identical: a second copy is how one of them quietly stops linking contacts
/// or starts reaching into another outlet's contact book.
/// </summary>
internal static class ThreadOpen
{
    /// <summary>
    /// The conversation, or the refusal to answer with. <see cref="Started"/> is true when
    /// this call created it — the caller may then stamp things that only apply to a brand-new
    /// conversation (the AI tick), never to one that already existed.
    /// </summary>
    public record Result(Thread? Thread, IResult? Error, bool Started = false,
        ThreadResolver.Outcome Pick = ThreadResolver.Outcome.Found,
        IReadOnlyList<ThreadResolver.Candidate>? Candidates = null,
        ThreadStart.Why Placement = ThreadStart.Why.None)
    {
        public bool Ok => Thread != null && Error == null;
    }

    /// <summary>
    /// Resolve the caller's own conversation with <paramref name="to"/> on
    /// <paramref name="channel"/>, opening one in their company when there is none.
    /// <paramref name="to"/> must already be normalized for the channel (digits for a
    /// phone-keyed one) — the callers do that, because each has its own idea of a bad
    /// address and its own status code for it.
    ///
    /// Order, which is the contract:
    /// <list type="number">
    /// <item>the caller's ONE conversation with that address (<see cref="ThreadResolver"/>:
    /// several visible ones need <c>thread_id</c> / <c>company</c>, which is a 409 with the
    /// candidates, never a silent pick);</item>
    /// <item>a contact to link — email exact, phone by last-8 — but only one the caller may
    /// see and only in the conversation's own company, so starting a chat never reaches into
    /// another outlet's contact book;</item>
    /// <item>no conversation yet → place it in the caller's company
    /// (<see cref="ThreadStart"/>) and create it there.</item>
    /// </list>
    /// </summary>
    public static async Task<Result> FindOrOpenAsync(AppDbContext db, ThreadStore store, ScopingService sc,
        ClaimsPrincipal me, string channel, string to, int? threadId = null, string? company = null,
        string? entity = null, string? name = null)
    {
        // the caller's OWN conversation with this address: one per company, so several
        // the caller can see need thread_id / company (409 with the candidates); one in
        // a branch of their company they may not open is the same 404 as the thread routes
        var pick = await ThreadResolver.ResolveAsync(db, sc, me, channel, new[] { to }, threadId, company);
        // `Error` is the answer POST /api/messaging/threads has always given; `Pick` and
        // `Placement` let a caller that must not answer 404 map the same refusal itself.
        if (ThreadResolver.Refusal(pick) is { } refused)
            return new Result(null, refused, Pick: pick.Outcome, Candidates: pick.Candidates);
        var existing = pick.ThreadId is int tid
            ? await db.Threads.Include(t => t.Contact).Include(t => t.Account).FirstAsync(t => t.Id == tid)
            : null;

        // store._match_contact: email keys match Contact.email. Social ids
        // (PSID/IGSID/TG chat id) have no CRM counterpart — those threads
        // are linked by hand from the chat's Contact panel. Only a contact the
        // caller may see is linked.
        var visible = ContactsEndpoints.ScopeContacts(db.Contacts, me, db, sc);
        // several contacts may share the address (one per company): the one
        // of the conversation's entity wins (ContactCompanies.Pick)
        var forEntity = existing?.EntityId ?? await InboxEndpoints.EntityId(db, entity);
        var matched = channel == Channels.Email
            ? ContactCompanies.Pick(db, sc,
                await visible.Where(c => c.Email.ToLower() == to.ToLower()).ToListAsync(), forEntity)
            // a group's jid is no phone: a group is never linked to a contact (GroupChats)
            : PhoneKeyed(channel) && !ChatBranch.Data.Channels.GroupChats.IsGroup(channel, to) && PhoneMatch.Tail(to) is string tail
                ? ContactCompanies.Pick(db, sc,
                    visible.Where(c => c.Phone != "").AsEnumerable()
                        .Where(c => PhoneMatch.Tail(c.Phone) == tail).ToList(), forEntity)
            : null;

        if (existing != null)
        {
            if (existing.ContactId == null && matched != null
                && ContactCompanies.MayLink(db, sc, matched.Id, sc.ThreadCompany(existing)))
            {
                existing.ContactId = matched.Id; // backfill a pre-link thread
                existing.Contact = matched;
                // The link decided the outlet — re-derive the denormalized entity.
                sc.SyncThreadEntity(existing, save: false);
                await db.SaveChangesAsync();
            }
            await db.Entry(existing).Reference(t => t.Contact).LoadAsync();
            return new Result(existing, null);
        }

        // a NEW conversation of the caller's company (ThreadStart)
        var place = await ThreadStart.PlaceAsync(db, sc, me, entity, company, matched?.Id);
        if (place.Error != null) return new Result(null, place.Error, Placement: place.Failure);

        Thread thread;
        try
        {
            thread = store.GetOrCreateInCompany(channel, to, place.Company, name,
                contactId: matched?.Id, entityHint: place.Entity);
        }
        catch (DbUpdateException)
        {
            // Two first sends to the same new address at once: one of them created the
            // conversation a moment ago and the other's insert hit the (channel, key,
            // company) uniqueness. The loser wants that same conversation, so look it up
            // and carry on — this must not be a 500, and above all must not become a
            // SECOND conversation with the same customer.
            Detach(db);
            var again = await ThreadResolver.ResolveAsync(db, sc, me, channel, new[] { to }, null, company);
            if (again.ThreadId is not int raced) throw;
            thread = await db.Threads.Include(t => t.Contact).Include(t => t.Account)
                .FirstAsync(t => t.Id == raced);
            return new Result(thread, null);
        }

        if (channel == Channels.Email && thread.AiEnabled)
        {
            thread.AiEnabled = false; // email has no auto-reply
            await db.SaveChangesAsync();
        }
        await db.Entry(thread).Reference(t => t.Contact).LoadAsync();
        return new Result(thread, null, Started: true);
    }

    /// <summary>
    /// Leave nothing half-added behind after a rejected insert, so the next read of the
    /// context is clean.
    /// </summary>
    private static void Detach(AppDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            try { entry.State = EntityState.Detached; } catch { /* already gone */ }
    }

    /// <summary>
    /// Channels whose thread key is a phone number, so a contact is matched by its
    /// last 8 digits (<see cref="PhoneMatch"/>) rather than exactly.
    /// </summary>
    public static bool PhoneKeyed(string channel) =>
        channel is Channels.WhatsApp or Channels.Sms;
}
