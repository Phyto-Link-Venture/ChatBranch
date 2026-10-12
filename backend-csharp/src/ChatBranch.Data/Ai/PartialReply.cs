using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// A reply that answers several things where ONE part failed its check: the failing
/// sentences are left out, the rest still goes to the customer with one line saying
/// a teammate will check that part, and a handover that leaves the AI on asks a
/// person to look (<see cref="AiReplyResult.Partial"/>). Only a check that pins its
/// problem to sentences (<see cref="Skills.SkillService.CheckRun.Sentences"/> — today
/// the booking-claim check) can do this; any other failure keeps the whole-reply
/// handover. Pure.
/// </summary>
public static class PartialReply
{
    /// <summary>The topic key of the booking-claim check (worded per language below).</summary>
    public const string Appointment = "appointment";

    private static readonly Dictionary<string, Dictionary<string, string>> Topics = new()
    {
        [Appointment] = new() { ["en"] = "appointment", ["ms"] = "temu janji", ["zh"] = "预约" },
    };

    /// <summary>The draft without <paramref name="sentences"/>; null when what is left is not a reply.</summary>
    public static string? Without(string draft, IEnumerable<string> sentences)
    {
        var text = draft ?? "";
        foreach (var s in sentences.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
        {
            var at = text.IndexOf(s, StringComparison.Ordinal);
            if (at < 0) return null;   // can't find it as written — don't guess
            text = text.Remove(at, s.Length);
        }
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        // a dangling joiner left where a sentence was ("Sure, and" / "Also,")
        text = Regex.Replace(text, @"[\s,;:–—-]+$", "").Trim();
        return Sensible(text) ? text : null;
    }

    /// <summary>One part of a reply without the dropped sentences, tidied ("" when nothing is left).</summary>
    public static string Strip(string text, IEnumerable<string> sentences)
    {
        foreach (var s in sentences.Where(s => !string.IsNullOrWhiteSpace(s)))
            text = text.Replace(s, "", StringComparison.Ordinal);
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        text = Regex.Replace(text, @"[\s,;:–—-]+$", "").Trim();
        return Sensible(text) || text.Any(char.IsLetter) && text.Length >= 2 ? text : "";
    }

    /// <summary>Enough left to be an answer: three words (or four CJK characters).</summary>
    public static bool Sensible(string? text)
    {
        var s = text ?? "";
        var cjk = s.Count(ch => ch >= '一' && ch <= '鿿');
        if (cjk >= 4) return true;
        return Regex.Matches(s, @"\p{L}[\p{L}'’]*").Count >= 3;
    }

    /// <summary>"I'll get a teammate to check the appointment part for you." in the reply's language.</summary>
    public static string CustomerLine(string topic, string language)
    {
        var lang = language is "ms" or "zh" ? language : "en";
        var word = Topics.TryGetValue(topic, out var names) ? names[lang] : topic;
        return lang switch
        {
            "ms" => $"Saya akan minta rakan sepasukan semak bahagian {word} untuk anda.",
            "zh" => $"关于{word}的部分，我会请同事帮您确认。",
            _ => $"I'll get a teammate to check the {word} part for you.",
        };
    }

    /// <summary>The topic as staff read it (the private note, the handover reason).</summary>
    public static string TopicLabel(string topic) =>
        Topics.TryGetValue(topic, out var names) ? names["en"] : topic;
}

/// <summary>
/// The part of a reply that was left out (see <see cref="PartialReply"/>): what it was
/// about, why, and the sentences dropped. Rides on the draft as <c>partial_handover</c>.
/// </summary>
public sealed record AiPartialHandover(string Topic, string Reason, IReadOnlyList<string> Dropped)
{
    public Dictionary<string, object?> ToMeta() => new()
    {
        ["topic"] = Topic, ["reason"] = Reason, ["dropped"] = Dropped.ToList(),
    };

    /// <summary>The handover's reason: names the topic and quotes what was left out.</summary>
    public string HandoverReason() =>
        $"The AI answered the rest but left out the {PartialReply.TopicLabel(Topic)} part ({Reason})"
        + (Dropped.Count > 0 ? $" — left out: \"{string.Join(" ", Dropped)}\"" : "");

    /// <summary>The partial handover recorded in a draft / job context (CLR objects or JSON), else null.</summary>
    public static AiPartialHandover? From(object? ctx)
    {
        try
        {
            var item = AiGate.Get(ctx, "partial_handover");
            if (item == null || item is System.Text.Json.JsonElement { ValueKind: not System.Text.Json.JsonValueKind.Object })
                return null;
            return new AiPartialHandover(AiGate.Str(item, "topic", ""), AiGate.Str(item, "reason", ""),
                AiGate.StrList(item, "dropped"));
        }
        catch { return null; }
    }
}
