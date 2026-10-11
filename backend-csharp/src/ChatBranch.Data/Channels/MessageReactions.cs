using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Emoji reactions on messages (<c>crm_messagereaction</c>) — the customer's
/// (inbound: WhatsApp Cloud <c>type: reaction</c>, Baileys <c>reactionMessage</c>,
/// Telegram <c>message_reaction</c>) and ours (the inbox's reaction bar,
/// <c>POST …/messages/{pk}/react</c>).
///
/// A reaction is NOT a message: it never makes a bubble, wakes the AI, runs a
/// flow or automation, pages anyone or reopens a resolved chat. It only bumps
/// <see cref="Thread.UpdatedAt"/> so the inbox's SSE stream re-renders.
/// One reaction per message × actor: reacting again replaces it, an empty emoji
/// removes it. On our side the business is ONE sender to the provider (WhatsApp
/// and Telegram keep one reaction per sender), so a person's reaction replaces
/// any colleague's — or the AI's (actor <c>ai</c>) — on the same message.
/// </summary>
public static class MessageReactions
{
    public const string ContactActor = "contact";
    public const string UserPrefix = "user:";
    public const int MaxEmojiLength = 32;

    public static string UserActor(string username) => UserPrefix + (username ?? "").Trim();

    /// <summary>The AI's own reactions (react_to_message, delivered by the AI gate) — ours, like a person's.</summary>
    public const string AiActor = "ai";

    /// <summary>A reaction the BUSINESS made (a person or the AI): one sender at the provider.</summary>
    public static bool IsOurs(string actor) =>
        actor == AiActor || actor.StartsWith(UserPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The emoji Telegram's Bot API accepts as a reaction (<c>ReactionTypeEmoji</c>) —
    /// anything else is refused with REACTION_INVALID. Kept without the U+FE0F
    /// variation selector, the way Telegram spells them.
    /// </summary>
    public static readonly IReadOnlySet<string> TelegramEmoji = new HashSet<string>
    {
        "👍", "👎", "❤", "🔥", "🥰", "👏", "😁", "🤔", "🤯", "😱", "🤬", "😢", "🎉", "🤩", "🤮", "💩", "🙏",
        "👌", "🕊", "🤡", "🥱", "🥴", "😍", "🐳", "❤‍🔥", "🌚", "🌭", "💯", "🤣", "⚡", "🍌", "🏆", "💔",
        "🤨", "😐", "🍓", "🍾", "💋", "🖕", "😈", "😴", "😭", "🤓", "👻", "👨‍💻", "👀", "🎃", "🙈", "😇",
        "😨", "🤝", "✍", "🤗", "🫡", "🎅", "🎄", "☃", "💅", "🤪", "🗿", "🆒", "💘", "🙉", "🦄", "😘", "💊",
        "🙊", "😎", "👾", "🤷‍♂", "🤷", "🤷‍♀", "😡",
    };

    /// <summary>Reactions the AI never makes, whatever the channel allows (rude, crude or cold).</summary>
    private static readonly HashSet<string> NotForAi = new()
    {
        "🖕", "💩", "🤮", "🤬", "😈", "💋", "🍆", "🍑", "💊", "🤡", "😡", "👎", "🥱", "🌭", "🍌",
    };

    private static string Bare(string e) => e.Replace("️", "");

    /// <summary>
    /// The emoji the AI may react with on <paramref name="channel"/>, spelled the way
    /// that channel takes it (Telegram: its own set, no variation selector) — null
    /// when it isn't ONE emoji, the channel has no reactions, the channel's set
    /// doesn't hold it, or it is one the AI never uses.
    /// </summary>
    public static string? ForAi(string channel, string? emoji)
    {
        if (!Supports(channel)) return null;
        var e = Normalize(emoji);
        if (string.IsNullOrEmpty(e)) return null;
        if (new System.Globalization.StringInfo(e).LengthInTextElements != 1) return null;
        if (NotForAi.Contains(Bare(e))) return null;
        if (channel == ChatBranch.Domain.Channels.Telegram)
            return TelegramEmoji.Contains(Bare(e)) ? Bare(e) : null;
        return e;
    }

    /// <summary>Channels whose provider carries reactions both ways (sandbox threads included).</summary>
    public static bool Supports(string channel) =>
        channel is ChatBranch.Domain.Channels.WhatsApp or ChatBranch.Domain.Channels.Telegram;

    /// <summary>
    /// A reaction as typed: "" = remove; null = not an emoji (letters, digits,
    /// markup, control characters, or too long).
    /// </summary>
    public static string? Normalize(string? emoji)
    {
        var e = (emoji ?? "").Trim();
        if (e.Length == 0) return "";
        if (e.Length > MaxEmojiLength) return null;
        foreach (var c in e)
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c < 0x80 && c != '#' && c != '*' && !char.IsDigit(c))
                return null;
        // a keycap (#️⃣ 1️⃣) is the only emoji that starts with an ASCII character
        if (e[0] < 0x80 && (e.Length < 2 || e.All(c => c < 0x80))) return null;
        return e;
    }

    /// <summary>
    /// The customer's reaction to a message we hold in the conversation
    /// (<paramref name="channel"/>, <paramref name="threadKey"/>). A reaction to
    /// something we never stored is dropped. Returns true when something changed. Saves.
    /// </summary>
    public static bool ApplyInbound(AppDbContext db, string channel, string threadKey, string targetExternalId,
        string? emoji, string actor = ContactActor, int? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(targetExternalId)) return false;
        var e = Normalize(emoji);
        if (e == null) return false;
        // the conversation of the RECEIVING account's company (another company's chat with
        // the same customer is never touched); without an account, the one holding the message
        Thread? thread = null;
        if (accountId is int aid)
            thread = new Stores.ThreadStore(db, new ScopingService(db)).FindForAccount(channel, threadKey, aid);
        thread ??= db.Messages.Where(m => m.ExternalId == targetExternalId && m.Thread!.Channel == channel
                                          && m.Thread.Key == threadKey)
            .OrderByDescending(m => m.Id).Select(m => m.Thread).FirstOrDefault();
        if (thread == null) return false;
        var msg = db.Messages.FirstOrDefault(m => m.ThreadId == thread.Id && m.ExternalId == targetExternalId);
        if (msg == null) return false;
        return Set(db, thread, msg, actor, e);
    }

    /// <summary>
    /// Put <paramref name="actor"/>'s reaction on <paramref name="msg"/> ("" removes it).
    /// One of OURS replaces every other person's (one business sender). Bumps the
    /// thread's updated_at — never its status. Returns true when something changed. Saves.
    /// </summary>
    public static bool Set(AppDbContext db, Thread thread, Message msg, string actor, string emoji)
    {
        var rows = db.MessageReactions.Where(r => r.MessageId == msg.Id).ToList();
        var ours = IsOurs(actor);
        var changed = false;
        foreach (var r in rows.Where(r => r.Actor != actor && ours && IsOurs(r.Actor)))
        {
            db.MessageReactions.Remove(r);
            changed = true;
        }
        var mine = rows.FirstOrDefault(r => r.Actor == actor);
        if (emoji.Length == 0)
        {
            if (mine != null) { db.MessageReactions.Remove(mine); changed = true; }
        }
        else if (mine == null)
        {
            db.MessageReactions.Add(new MessageReaction
            {
                MessageId = msg.Id, ThreadId = thread.Id, Actor = actor, Emoji = emoji, CreatedAt = DateTime.UtcNow,
            });
            changed = true;
        }
        else if (mine.Emoji != emoji)
        {
            mine.Emoji = emoji;
            mine.CreatedAt = DateTime.UtcNow;
            changed = true;
        }
        if (!changed) return false;
        thread.UpdatedAt = DateTime.UtcNow; // the SSE change token — never the status
        db.SaveChanges();
        return true;
    }

    /// <summary>Every reaction on these messages, by message id (oldest first).</summary>
    public static Dictionary<int, List<MessageReaction>> For(AppDbContext db, IEnumerable<int> messageIds)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        return db.MessageReactions.AsNoTracking()
            .Where(r => ids.Contains(r.MessageId))
            .OrderBy(r => r.Id)
            .AsEnumerable()
            .GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}

/// <summary>
/// The text shapes the inbox renders as cards instead of plain bubbles:
/// <c>[location] lat,lng label</c> (a map pin) and <c>[contact] Name · +phone</c>
/// (a shared contact card). Every channel writes them the same way.
/// </summary>
public static class MessageCards
{
    public static string Contact(string? name, string? phone)
    {
        var n = (name ?? "").Trim().Replace("\n", " ");
        var p = (phone ?? "").Trim();
        if (n.Length == 0 && p.Length == 0) return "[contact card]";
        return p.Length == 0 ? $"[contact] {n}" : $"[contact] {(n.Length > 0 ? n : p)} · {p}";
    }

    /// <summary>The first TEL out of a vCard (WhatsApp Web shares contacts as vCards).</summary>
    public static (string Name, string Phone) FromVCard(string? vcard)
    {
        string name = "", phone = "";
        foreach (var raw in (vcard ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].ToUpperInvariant();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0 && (key == "FN" || key.StartsWith("FN;"))) name = value;
            if (phone.Length == 0 && (key == "TEL" || key.StartsWith("TEL;") || key.Contains(".TEL"))) phone = value;
        }
        return (name, phone);
    }
}
