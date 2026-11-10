using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Stores;

/// <summary>
/// The unified-inbox store — the C# equivalent of whatsapp/messaging
/// <c>store.py</c>. Views/AI/flows call THIS, never the models directly, so the
/// storage rules (thread-entity sync, updated_at bumps that drive the SSE
/// stream, wamid dedup) live in one place. All channels share it because every
/// conversation is a <see cref="Thread"/> row regardless of provider.
/// </summary>
public class ThreadStore
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public ThreadStore(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

    /// <summary>List threads for a channel (or all), newest-active first, scoped to an entity.</summary>
    public List<Thread> ListThreads(string? channel = null, int? entityId = null,
        string? status = null, int limit = 100)
    {
        IQueryable<Thread> q = _db.Threads
            .Include(t => t.Contact)
            .Include(t => t.Account)
            .AsNoTracking();
        if (!string.IsNullOrEmpty(channel)) q = q.Where(t => t.Channel == channel);
        if (!string.IsNullOrEmpty(status)) q = q.Where(t => t.Status == status);
        // Inbox shows unassigned conversations too, so include the null bucket.
        q = _scoping.ScopeOwned(q, entityId, includeUnassigned: true);
        return q.OrderByDescending(t => t.UpdatedAt).Take(limit).ToList();
    }

    private IQueryable<Thread> Tracked() => _db.Threads.Include(t => t.Contact).Include(t => t.Account);

    /// <summary>A conversation by its id (account + contact loaded), else null.</summary>
    public Thread? ById(int id) => Tracked().FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Every conversation with this (channel, key): one per company that talks to
    /// the customer, plus at most one unattributed. A caller holding a person's
    /// scope, an account or a company picks ONE of them — never "the first".
    /// </summary>
    public List<Thread> AllFor(string channel, string key) =>
        Tracked().Where(t => t.Channel == channel && t.Key == key).OrderBy(t => t.Id).ToList();

    /// <summary>
    /// The conversation a message on (channel, key) belongs to when it arrives on
    /// (or leaves through) <paramref name="accountId"/>:
    /// <list type="bullet">
    ///   <item>a number of company C → C's own conversation (an unattributed one —
    ///         no company, no entity — is nobody's yet, so C takes it);</item>
    ///   <item>an organization-wide number → the conversation already on that number,
    ///         else the unattributed one;</item>
    ///   <item>no account (the env default number, the sandbox) → the unattributed
    ///         conversation, else the customer's only one.</item>
    /// </list>
    /// Never another company's conversation. Null = none yet.
    /// </summary>
    public Thread? FindForAccount(string channel, string key, int? accountId)
    {
        var q = Tracked().Where(t => t.Channel == channel && t.Key == key);
        if (_scoping.AccountCompany(accountId) is int company)
            return q.FirstOrDefault(t => t.CompanyEntityId == company)
                   ?? q.FirstOrDefault(t => t.CompanyEntityId == null && t.EntityId == null);
        if (accountId is int aid)
            return q.Where(t => t.AccountId == aid).OrderByDescending(t => t.UpdatedAt).FirstOrDefault()
                   ?? q.FirstOrDefault(t => t.CompanyEntityId == null);
        var unattributed = q.FirstOrDefault(t => t.CompanyEntityId == null);
        if (unattributed != null) return unattributed;
        var all = q.Take(2).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>
    /// The conversation of (channel, key) in a company: that company's own, else an
    /// unattributed one with no entity (which the company may adopt). Null company =
    /// the unattributed conversation.
    /// </summary>
    public Thread? FindInCompany(string channel, string key, int? companyId)
    {
        var q = Tracked().Where(t => t.Channel == channel && t.Key == key);
        return companyId is int c
            ? q.FirstOrDefault(t => t.CompanyEntityId == c)
              ?? q.FirstOrDefault(t => t.CompanyEntityId == null && t.EntityId == null)
            : q.FirstOrDefault(t => t.CompanyEntityId == null);
    }

    /// <summary>
    /// Get or create the conversation for a message on (channel, key) arriving on
    /// (or sent from) <paramref name="accountId"/> — FOR THAT NUMBER'S COMPANY
    /// (<see cref="FindForAccount"/>): a customer writing to a second company's
    /// number gets that company's own, new conversation. Syncs entity attribution.
    /// </summary>
    public Thread GetOrCreateThread(string channel, string key, string? name = null,
        int? accountId = null, int? contactId = null)
    {
        var thread = FindForAccount(channel, key, accountId);
        var company = _scoping.AccountCompany(accountId);
        return Settle(thread, channel, key, company, name, accountId, contactId, entityHint: null);
    }

    /// <summary>
    /// Get or create the conversation of (channel, key) in a KNOWN company (an
    /// operator starting a chat, a flow, a queued job): that company's own, else a new
    /// one there. <paramref name="entityHint"/> files a brand-new conversation under
    /// that entity when nothing else (account, contact) decides it.
    /// </summary>
    public Thread GetOrCreateInCompany(string channel, string key, int? companyId, string? name = null,
        int? accountId = null, int? contactId = null, int? entityHint = null)
    {
        companyId ??= _scoping.CompanyOf(entityHint);
        var thread = FindInCompany(channel, key, companyId);
        return Settle(thread, channel, key, companyId, name, accountId, contactId, entityHint);
    }

    // ---- queued jobs ------------------------------------------------------------

    /// <summary>The payload key that pins a queued job to its conversation.</summary>
    public const string JobThreadKey = "thread_id";

    /// <summary>The conversation id a job's payload pins, else null.</summary>
    public static int? JobThreadId(OutboundJob job) =>
        Ai.AiGate.Num(job.Payload, JobThreadKey) is double d ? (int)d : null;

    /// <summary>The conversation a queued job belongs to — never creates one (dry runs, cancellations).</summary>
    public Thread? FindForJob(OutboundJob job) => ResolveJob(job, create: false);

    /// <summary>The conversation a queued job is delivered into, created in the job's company when it has none yet.</summary>
    public Thread GetOrCreateForJob(OutboundJob job) => ResolveJob(job, create: true)!;

    /// <summary>
    /// A WhatsApp job may be addressed to the contact's "+60…" phone while the
    /// conversation is keyed by the digits-only wa_id: an existing thread under the
    /// literal address wins, else the digits key.
    /// </summary>
    public string JobKey(OutboundJob job)
    {
        if (job.Channel != Domain.Channels.WhatsApp) return job.ToAddress;
        var digits = PhoneMatch.Digits(job.ToAddress);
        if (digits.Length == 0 || digits == job.ToAddress) return job.ToAddress;
        var literal = _db.Threads.AsNoTracking().Any(t => t.Channel == job.Channel && t.Key == job.ToAddress);
        return literal ? job.ToAddress : digits;
    }

    /// <summary>
    /// Which conversation a queued job goes into — resolved within ONE company,
    /// never "the first thread with that address":
    /// <list type="number">
    ///   <item>the conversation its payload pins (<c>thread_id</c> — every job made from a conversation carries it);</item>
    ///   <item>the company it was queued for: payload <c>company_id</c>, else its journey's (the
    ///         journey's entity, else the conversation that started / last answered the run);</item>
    ///   <item>the number it is bound to (its account's company);</item>
    ///   <item>the contact's own conversation on that address, else the contact's one company;</item>
    ///   <item>the customer's only conversation, else the unattributed one.</item>
    /// </list>
    /// </summary>
    private Thread? ResolveJob(OutboundJob job, bool create)
    {
        if (JobThreadId(job) is int tid && ById(tid) is { } pinned && pinned.Channel == job.Channel) return pinned;
        var key = JobKey(job);
        var (known, company) = JobCompany(job);
        if (known)
            return create
                ? GetOrCreateInCompany(job.Channel, key, company, accountId: job.AccountId, contactId: job.ContactId)
                : FindInCompany(job.Channel, key, company);
        if (job.AccountId is int aid)
            return create
                ? GetOrCreateThread(job.Channel, key, accountId: aid, contactId: job.ContactId)
                : FindForAccount(job.Channel, key, aid);
        var all = AllFor(job.Channel, key);
        if (job.ContactId is int cid)
        {
            var linked = all.Where(t => t.ContactId == cid).ToList();
            if (linked.Count == 1) return linked[0];
            var companies = ContactCompanies.LinkCompanies(_db, _scoping, cid);
            if (companies.Count == 1)
                return create
                    ? GetOrCreateInCompany(job.Channel, key, companies.First(), contactId: cid)
                    : FindInCompany(job.Channel, key, companies.First());
        }
        if (all.Count == 1) return all[0];
        var unattributed = all.FirstOrDefault(t => t.CompanyEntityId == null);
        if (unattributed != null || !create) return unattributed;
        return GetOrCreateInCompany(job.Channel, key, null, contactId: job.ContactId);
    }

    /// <summary>The company a job was queued for, when it says (payload / its journey).</summary>
    public (bool Known, int? Company) JobCompany(OutboundJob job)
    {
        if (job.Payload.ContainsKey("company_id"))
            return (true, Ai.AiGate.Num(job.Payload, "company_id") is double c ? (int)c : null);
        if (job.FlowRunId is not int rid) return (false, null);
        var run = _db.FlowRuns.AsNoTracking().FirstOrDefault(r => r.Id == rid);
        if (run == null) return (false, null);
        // the journey's companies ("Runs for": its entities + their branches; none = organization-wide)
        var wf = _db.Workflows.AsNoTracking().FirstOrDefault(w => w.Id == run.WorkflowId);
        var companies = wf == null ? new List<int>()
            : LibraryEntities.Ids(_db, LibraryKinds.Workflow, wf.Id, wf.EntityId)
                .Select(e => _scoping.CompanyOf(e)).OfType<int>().Distinct().ToList();
        if (companies.Count == 1) return (true, companies[0]);
        // the conversation that started / last answered the run — one of the journey's companies
        foreach (var k in new[] { "last_reply_thread_id", "trigger_thread_id" })
            if (Ai.AiGate.Num(run.Context, k) is double d
                && _db.Threads.AsNoTracking().FirstOrDefault(t => t.Id == (int)d) is { } t && t.Channel == job.Channel
                && (companies.Count == 0 || _scoping.ThreadCompany(t) is int tc && companies.Contains(tc)))
                return (true, _scoping.ThreadCompany(t));
        if (companies.Count > 1)
        {
            // a journey of several companies: the contact's own one among them, else its first
            var mine = job.ContactId is int cid ? ContactCompanies.LinkCompanies(_db, _scoping, cid) : new HashSet<int>();
            foreach (var c in companies)
                if (mine.Contains(c)) return (true, c);
            return (true, companies[0]);
        }
        return (false, null);
    }

    /// <summary>Does this queued job belong to <paramref name="thread"/> (cancellations, throttles, debounce)?</summary>
    public bool JobBelongsTo(OutboundJob job, Thread thread)
    {
        if (JobThreadId(job) is int tid) return tid == thread.Id;
        try { return FindForJob(job)?.Id == thread.Id; }
        catch { return false; }
    }

    private Thread Settle(Thread? thread, string channel, string key, int? company, string? name,
        int? accountId, int? contactId, int? entityHint)
    {
        // nothing crosses companies: a number or a contact of another company is never adopted
        if (accountId is int a && !ScopingService.SameCompanyOrShared(company, _scoping.AccountCompany(a)))
            accountId = null;
        if (contactId is int cid && !ContactCompanies.MayLink(_db, _scoping, cid, company))
            contactId = null;
        // a group conversation never belongs to one contact (Channels/GroupChats.cs)
        if (ChatBranch.Data.Channels.GroupChats.IsGroup(channel, key)) contactId = null;
        if (thread == null)
        {
            thread = new Thread
            {
                Channel = channel,
                Key = key,
                Address = key,
                Name = name ?? key,
                Status = ThreadStatus.Open,
                AccountId = accountId,
                ContactId = contactId,
                CompanyEntityId = company,
                EntityId = entityHint is int h && (company == null || _scoping.CompanyOf(h) == company) ? h : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.Threads.Add(thread);
            _db.SaveChanges();
            _db.Entry(thread).Reference(t => t.Account).Load();
            _db.Entry(thread).Reference(t => t.Contact).Load();
        }
        else
        {
            // An existing thread adopts the receiving account / matched contact
            // when it has none yet — "the number that receives a message decides
            // attribution" (crm/scoping). Never overrides an existing binding.
            var dirty = false;
            if (thread.CompanyEntityId == null && thread.EntityId == null && company != null)
            {
                // an unattributed conversation becomes the receiving company's
                thread.CompanyEntityId = company;
                dirty = true;
            }
            var mine = _scoping.ThreadCompany(thread);
            if (thread.AccountId == null && accountId is int acc
                && ScopingService.SameCompanyOrShared(mine, _scoping.AccountCompany(acc)))
            { thread.AccountId = acc; dirty = true; }
            if (thread.ContactId == null && contactId is int c && ContactCompanies.MayLink(_db, _scoping, c, mine))
            { thread.ContactId = c; dirty = true; }
            if (dirty)
            {
                thread.UpdatedAt = DateTime.UtcNow;
                _db.SaveChanges();
                _db.Entry(thread).Reference(t => t.Account).Load();
                _db.Entry(thread).Reference(t => t.Contact).Load();
            }
        }
        _scoping.SyncThreadEntity(thread);
        return thread;
    }

    /// <summary>
    /// Append a message and bump the thread's updated_at (the SSE stream watches
    /// it). Honours wamid dedup: a non-empty external_id already present on the
    /// thread collapses onto the existing row.
    /// </summary>
    // ---- idempotency claim (POST /api/messaging/notify) ---------------------

    private string _pendingIdempotencyKey = "";

    /// <summary>
    /// Stamp the NEXT outbound message this store writes with <paramref name="key"/>, so a
    /// retried transactional send reads that message back instead of sending a second one
    /// (<c>Endpoints/MessagingNotifyEndpoints</c>).
    ///
    /// It is a claim rather than a parameter because the row is written by whichever send
    /// path the channel takes — <see cref="ChatBranch.Data.Channels.OutboundSender"/> and the
    /// per-channel senders under it, thirty-odd call sites — and every one of them ends
    /// here. The store is scoped to the request, so the claim cannot leak to another caller;
    /// it covers ONE row and clears itself either way, and disposing the scope clears it
    /// even when the send threw. The key lands in the SAME save as the message, so there is
    /// never a key that resolves to nothing.
    ///
    /// <code>
    /// using (store.ClaimIdempotency(key))
    ///     msg = sender.Send(thread, text, MessageSource.System, account?.Id);
    /// </code>
    /// </summary>
    public IDisposable ClaimIdempotency(string key)
    {
        _pendingIdempotencyKey = (key ?? "").Trim();
        return new IdempotencyClaim(this);
    }

    private string TakeIdempotencyKey()
    {
        var key = _pendingIdempotencyKey;
        _pendingIdempotencyKey = "";   // one row only — a follow-up note is not the message
        return key;
    }

    private sealed class IdempotencyClaim : IDisposable
    {
        private readonly ThreadStore _store;
        public IdempotencyClaim(ThreadStore store) => _store = store;
        public void Dispose() => _store._pendingIdempotencyKey = "";
    }

    public Message AppendMessage(Thread thread, string direction, string source, string text,
        string status = "sent", string externalId = "", int? accountId = null,
        bool priv = false, DateTime? providerTs = null, string replyToExternalId = "",
        ChatBranch.Data.Channels.InboundSender? sender = null)
    {
        if (!string.IsNullOrEmpty(externalId))
        {
            var existing = _db.Messages.FirstOrDefault(
                m => m.ThreadId == thread.Id && m.ExternalId == externalId);
            if (existing != null) return existing; // dedup redelivery
        }

        var msg = new Message
        {
            ThreadId = thread.Id,
            AccountId = accountId ?? thread.AccountId,
            ExternalId = externalId,
            Direction = direction,
            Source = source,
            Text = text,
            Status = status,
            Private = priv,
            CreatedAt = providerTs ?? DateTime.UtcNow,
            // a transactional send's key, when one is claimed around this write
            IdempotencyKey = direction == MessageDirection.Out ? TakeIdempotencyKey() : "",
        };
        // a group message's author (Channels/GroupChats.cs) — clipped to what the columns keep
        if (ChatBranch.Data.Channels.GroupChats.Clean(sender) is { } who)
        {
            msg.SenderKey = who.Key;
            msg.SenderName = who.Name;
            msg.SenderPhone = who.Phone;
        }
        // a quoted reply: keep the provider's id, and link our own row when we hold it
        if (!string.IsNullOrWhiteSpace(replyToExternalId))
        {
            msg.ReplyToExternalId = replyToExternalId.Trim();
            msg.ReplyToId = ResolveExternal(thread.Id, msg.ReplyToExternalId);
        }
        _db.Messages.Add(msg);

        // Inbound re-opens a resolved/snoozed thread.
        if (direction == MessageDirection.In && thread.Status is ThreadStatus.Resolved or ThreadStatus.Snoozed)
            thread.Status = ThreadStatus.Open;
        thread.UpdatedAt = DateTime.UtcNow;
        _db.Threads.Update(thread);
        _db.SaveChanges();
        return msg;
    }

    /// <summary>The local message of <paramref name="threadId"/> carrying a provider id (null = not held).</summary>
    public int? ResolveExternal(int threadId, string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        return _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == threadId && m.ExternalId == externalId)
            .Select(m => (int?)m.Id).FirstOrDefault();
    }

    /// <summary>Paginated messages, oldest→newest, with a `before` id cursor.</summary>
    public List<Message> GetMessages(Thread thread, int limit = 50, int? before = null)
    {
        IQueryable<Message> q = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id);
        if (before is int b) q = q.Where(m => m.Id < b);
        // take the newest `limit`, then return in chronological order
        var page = q.OrderByDescending(m => m.Id).Take(limit).ToList();
        page.Reverse();
        return page;
    }

    /// <summary>
    /// The change token the SSE stream compares on: max(thread.updated_at) plus
    /// the total message count. Any store write that bumps updated_at (or adds a
    /// message) moves the token, which is how the dashboard knows to re-fetch.
    /// </summary>
    public string ChangeToken()
    {
        var latest = _db.Threads.Max(t => (DateTime?)t.UpdatedAt);
        var count = _db.Messages.Count();
        var ticks = latest?.Ticks ?? 0;
        return $"{ticks}:{count}";
    }
}
