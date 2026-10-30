using ChatBranch.Data.Audit;
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

    /// <summary>The old wording, capped so one long message cannot bloat the log.</summary>
    private static string Short(string text)
    {
        var one = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 400 ? one : one[..400] + "…";
    }
}
