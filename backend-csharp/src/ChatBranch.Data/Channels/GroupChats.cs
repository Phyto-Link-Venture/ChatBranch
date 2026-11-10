using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Who wrote one inbound message of a GROUP conversation: the participant's own id
/// (WhatsApp's full participant jid — <c>…@s.whatsapp.net</c> or <c>…@lid</c> —, a
/// Telegram user id, a LINE userId), the name the channel showed, and the phone
/// digits when the channel told us ("" otherwise).
/// </summary>
public sealed record InboundSender(string Key, string Name, string Phone = "");

/// <summary>
/// Group conversations (a WhatsApp group on a QR number, a Telegram group /
/// supergroup the bot is in, a LINE group or multi-person room). A group is a
/// <see cref="Thread"/> keyed by the GROUP's own id — WhatsApp's full
/// <c>…@g.us</c> jid, Telegram's negative chat id, LINE's groupId (<c>C…</c>) or
/// roomId (<c>R…</c>) — so group-ness is derived from (channel, key) and needs no
/// column. Each inbound message carries its author on
/// <c>crm_message.sender_key / sender_name / sender_phone</c>.
///
/// The rules every seam reads from here: a group NEVER links a contact (a group
/// jid's digits would otherwise tail-match a random customer's phone), never runs
/// flows, automations, STOP/START, the "who are you?" ask or background AI tasks,
/// and the AI answers only when an operator switched it on for that group AND the
/// message is addressed to us (a mention, a reply to us, a bot command).
/// </summary>
public static class GroupChats
{
    /// <summary>The longest group name we keep (WhatsApp allows 100, Telegram 128).</summary>
    public const int MaxNameChars = 120;
    public const int MaxSenderKeyChars = 200;
    public const int MaxSenderNameChars = 120;

    public static bool IsGroup(string? channel, string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return channel switch
        {
            ChatBranch.Domain.Channels.WhatsApp => key.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
            // Telegram: private chats have positive ids, groups / supergroups / channels negative
            ChatBranch.Domain.Channels.Telegram => key.StartsWith('-'),
            // LINE: user ids start with U, group ids with C, room ids with R
            ChatBranch.Domain.Channels.Line => key.Length > 1 && (key[0] == 'C' || key[0] == 'R'),
            _ => false,
        };
    }

    public static bool IsGroup(Thread? t) => t != null && IsGroup(t.Channel, t.Key);

    /// <summary>
    /// The thread key of a WhatsApp id as a gateway sent it: a group jid is kept
    /// whole (trimmed, lower-cased — "1203…@g.us"), anything else is the number's
    /// digits, exactly as before groups existed.
    /// </summary>
    public static string WhatsAppKey(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
        {
            var local = new string(s[..^5].Where(c => char.IsAsciiDigit(c) || c == '-').ToArray());
            return local.Length > 0 ? local + "@g.us" : "";
        }
        return new string(s.Where(char.IsDigit).ToArray());
    }

    /// <summary>A group's name as we keep it: trimmed, one line, at most <see cref="MaxNameChars"/>.</summary>
    public static string CleanName(string? subject)
    {
        var s = string.Join(' ', (subject ?? "").Split(new[] { '\r', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length > MaxNameChars ? s[..MaxNameChars] : s;
    }

    /// <summary>
    /// Keep the conversation's name in step with the group's current subject (a
    /// group can be renamed at any time). A blank subject leaves the name alone.
    /// The caller's context is saved.
    /// </summary>
    public static bool Rename(AppDbContext db, Thread t, string? subject)
    {
        var name = CleanName(subject);
        if (name.Length == 0 || t.Name == name) return false;
        t.Name = name;
        t.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
        return true;
    }

    /// <summary>The phone digits a WhatsApp participant jid carries (<c>…@s.whatsapp.net</c>), else "".</summary>
    public static string SenderPhone(string? senderKey)
    {
        var s = (senderKey ?? "").Trim();
        var at = s.IndexOf('@');
        if (at <= 0 || !s[at..].Equals("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)) return "";
        var local = s[..at];
        var colon = local.IndexOf(':'); // a device suffix ("6012…:3@s.whatsapp.net")
        if (colon > 0) local = local[..colon];
        return local.All(char.IsAsciiDigit) ? local : "";
    }

    /// <summary>A sender clipped to what the columns keep; null when it names nobody.</summary>
    public static InboundSender? Clean(InboundSender? sender)
    {
        if (sender == null) return null;
        var key = (sender.Key ?? "").Trim();
        var name = CleanName(sender.Name);
        var phone = new string((sender.Phone ?? "").Where(char.IsAsciiDigit).ToArray());
        if (phone.Length == 0) phone = SenderPhone(key);
        if (key.Length > MaxSenderKeyChars) key = key[..MaxSenderKeyChars];
        if (name.Length > MaxSenderNameChars) name = name[..MaxSenderNameChars];
        if (phone.Length > 20) phone = phone[..20];
        return key.Length == 0 && name.Length == 0 ? null : new InboundSender(key, name, phone);
    }

    /// <summary>
    /// The <c>sender</c> object of a message payload — <c>{id, name, phone}</c>
    /// (phone null when unknown) — or null when the message carries no author.
    /// </summary>
    public static Dictionary<string, object?>? SenderPayload(Message m)
    {
        if (m.SenderKey.Length == 0 && m.SenderName.Length == 0) return null;
        var phone = m.SenderPhone.Length > 0 ? m.SenderPhone : SenderPhone(m.SenderKey);
        return new Dictionary<string, object?>
        {
            ["id"] = m.SenderKey,
            ["name"] = m.SenderName,
            ["phone"] = phone.Length > 0 ? phone : null,
        };
    }

    /// <summary>The name a group thread shows before its subject is known.</summary>
    public static string DefaultName(string channel) => channel switch
    {
        ChatBranch.Domain.Channels.WhatsApp => "WhatsApp group",
        ChatBranch.Domain.Channels.Telegram => "Telegram group",
        ChatBranch.Domain.Channels.Line => "LINE group",
        _ => "Group chat",
    };

    /// <summary>The line the AI is given in a group conversation.</summary>
    public static string AiSystemLine(Thread t) =>
        $"This is the group chat \"{(t.Name.Length > 0 ? t.Name : DefaultName(t.Channel))}\" with several people; "
        + "each of their messages starts with the sender's name. Reply only to the latest message addressed to you, "
        + "briefly, and never share one member's personal details with the group.";
}
