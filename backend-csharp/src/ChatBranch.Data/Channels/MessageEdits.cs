using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The ONE writer of <see cref="Message.EditedAt"/> — WhatsApp's edit of a
/// message we already sent.
///
/// Two rules, both about honesty rather than mechanics:
///
///  - **the chat shows what the customer now sees.** <see cref="Message.Text"/>
///    is replaced, because a chat that still showed the old wording would be
///    lying about the conversation;
///  - **the trail keeps what it used to say.** The previous wording goes into
///    the audit row, so "what did we actually tell them at 3pm" is still
///    answerable after an edit.
///
/// The AI reads the current text like any other: an edited message is not
/// special to it, which is the point — the customer sees one message, so the AI
/// should reason about one message.
/// </summary>
public static class MessageEdits
{
    public const string AuditAction = "message.edited";

    /// <summary>
    /// WhatsApp's own edit window. It is not published as an API limit, so this
    /// is the observed one (about 15 minutes) kept in a single named place: past
    /// it WhatsApp refuses the edit, and refusing here first gives the operator a
    /// reason instead of a provider error.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// WhatsApp's "delete for everyone" window — longer than the edit one (about
    /// two days). Same reasoning: named once, checked before the call.
    /// </summary>
    public static readonly TimeSpan RecallWindow = TimeSpan.FromDays(2);

    /// <summary>How long is left to edit (never negative), for the UI and the guard.</summary>
    public static TimeSpan Remaining(Message message, DateTime? nowUtc = null)
    {
        var sent = DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc);
        var left = sent + Window - (nowUtc ?? DateTime.UtcNow);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public static bool WithinWindow(Message message, DateTime? nowUtc = null) =>
        Remaining(message, nowUtc) > TimeSpan.Zero;

    public static bool WithinRecallWindow(Message message, DateTime? nowUtc = null)
    {
        var sent = DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc);
        return (nowUtc ?? DateTime.UtcNow) < sent + RecallWindow;
    }

    /// <summary>
    /// Record an edit the provider has already accepted: the row takes the new
    /// wording, the conversation is bumped so the open chat redraws (the SSE
    /// change token), and the audit row keeps the old wording.
    /// </summary>
    public static void Record(AppDbContext db, Thread thread, Message message, string text, string username,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var before = message.Text;
        message.Text = text;
        message.EditedAt = now;
        thread.UpdatedAt = now;
        AuditTrail.Add(db, AuditAction, $"{thread.Channel}:{thread.Key}",
            $"Edited message #{message.Id} — was: {Short(before)}",
            actor: $"agent:{username}", source: "user", entityId: thread.EntityId);
        db.SaveChanges();
    }

    /// <summary>Who re-worded it, for the actor and the note.</summary>
    public const string ByBusiness = "business";
    public const string ByCustomer = "customer";

    /// <summary>
    /// An edit WhatsApp has ALREADY applied, reported by the gateway: made on our
    /// own number's phone (or WhatsApp Web), or by the customer on their own
    /// message. Same two rules as an edit we asked for — the row takes the new
    /// wording, the audit row keeps the old — so a chat read here says what the
    /// two of them actually see now.
    ///
    /// It never adds a bubble (an edit is the same message) and never reopens,
    /// notifies, or wakes the AI: nothing new was said. What it DOES do is expire
    /// a held AI reply that answered only this message — that draft was written
    /// against wording the customer has replaced, and sending it would answer a
    /// question nobody asked.
    ///
    /// Rows are found the way a recall finds them (the account's own rows first,
    /// then the conversation's), and never throws into a webhook.
    /// </summary>
    public static List<Message> RecordExternal(AppDbContext db, ThreadStore threads, ChannelAccount account,
        string key, string externalId, string text, string by, AiGate? gate = null, DateTime? nowUtc = null)
    {
        try
        {
            var id = (externalId ?? "").Trim();
            var wording = (text ?? "").Trim();
            if (id.Length == 0 || wording.Length == 0) return new();
            var prefix = id + "#";
            var rows = db.Messages.Where(m => m.AccountId == account.Id
                    && (m.ExternalId == id || m.ExternalId.StartsWith(prefix))).ToList();
            if (rows.Count == 0 && threads.FindForAccount(account.Channel, key, account.Id) is { } found)
                rows = db.Messages.Where(m => m.ThreadId == found.Id
                    && (m.ExternalId == id || m.ExternalId.StartsWith(prefix))).ToList();
            // a message already taken back has nothing to re-word, and a private
            // note is ours alone — neither is on the customer's phone
            rows = rows.Where(m => m.RecalledAt == null && !m.Private && m.Text != wording).ToList();
            if (rows.Count == 0) return rows;
            // A recall takes back every part of a split message; an edit does NOT
            // repeat the new wording across them — one provider message carries one
            // wording, and on a split message that is the first part (its caption).
            // WhatsApp ids never carry a part suffix, so this only matters if another
            // channel's edit ever arrives here (Messenger splits its attachments).
            if (rows.Count > 1)
                rows = new List<Message> { rows.FirstOrDefault(m => m.ExternalId == id) ?? rows.MinBy(m => m.Id)! };

            var now = nowUtc ?? DateTime.UtcNow;
            var who = by == ByBusiness ? ByBusiness : ByCustomer;
            var thread = db.Threads.FirstOrDefault(t => t.Id == rows[0].ThreadId);
            foreach (var m in rows)
            {
                var before = m.Text;
                m.Text = wording;
                m.EditedAt = now;
                AuditTrail.Add(db, AuditAction, thread != null ? $"{thread.Channel}:{thread.Key}" : "",
                    $"Edited message #{m.Id} — was: {Short(before)}",
                    actor: who == ByBusiness ? "agent:phone" : "contact", source: "webhook",
                    entityId: thread?.EntityId);
            }
            if (thread != null) thread.UpdatedAt = now;
            db.SaveChanges();
            // only the customer's own re-wording can strand a held reply; ours cannot
            if (who == ByCustomer && gate != null && thread != null)
            {
                try { gate.ExpireForEdit(thread, rows.Select(m => m.Id).ToList()); }
                catch { /* the edit is recorded; a draft left for a person is not a failure */ }
            }
            return rows;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>The old wording, capped so one long message cannot bloat the log.</summary>
    private static string Short(string text)
    {
        var one = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 400 ? one : one[..400] + "…";
    }
}
