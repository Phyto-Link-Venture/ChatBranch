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

    public Thread? GetThread(string channel, string key) =>
        _db.Threads.Include(t => t.Contact).Include(t => t.Account)
            .FirstOrDefault(t => t.Channel == channel && t.Key == key);

    /// <summary>Get or create the thread for a (channel, key). Syncs entity attribution.</summary>
    public Thread GetOrCreateThread(string channel, string key, string? name = null,
        int? accountId = null, int? contactId = null)
    {
        var thread = _db.Threads
            .Include(t => t.Account).Include(t => t.Contact)
            .FirstOrDefault(t => t.Channel == channel && t.Key == key);
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
            if (thread.AccountId == null && accountId != null) { thread.AccountId = accountId; dirty = true; }
            if (thread.ContactId == null && contactId != null) { thread.ContactId = contactId; dirty = true; }
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
    public Message AppendMessage(Thread thread, string direction, string source, string text,
        string status = "sent", string externalId = "", int? accountId = null,
        bool priv = false, DateTime? providerTs = null, string replyToExternalId = "")
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
        };
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
