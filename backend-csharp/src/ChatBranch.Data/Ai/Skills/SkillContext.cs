using System.Globalization;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// The read-only world a code skill sees as <c>ctx</c>: the current time in the
/// organization's timezone (already converted, so skills never do timezone
/// math), the channel, the customer's latest message and a small, safe view of
/// the contact. Built once per reply and serialized INTO the sandbox as JSON —
/// no CLR object ever crosses into JavaScript.
/// </summary>
public sealed class SkillContext
{
    public DateTimeOffset UtcNow { get; init; } = DateTimeOffset.UtcNow;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public string Channel { get; init; } = "";
    public string LastMessage { get; init; } = "";
    public Dictionary<string, object?> Contact { get; init; } = new();

    // ---- the conversation, for app tools (C# side only — never serialized into JS) ----
    /// <summary>The conversation the reply is for (null for a flow message without one).</summary>
    public int? ThreadId { get; init; }
    public int? ContactId { get; init; }
    /// <summary>The conversation's entity (bookings are owned by it; calendars resolve through it).</summary>
    public int? EntityId { get; init; }
    /// <summary>The conversation's assigned agent (username) — "the assignee's calendar".</summary>
    public string Assignee { get; init; } = "";
    /// <summary>The full contact row (name, phone, email for an event title / attendee).</summary>
    public Contact? ContactRecord { get; init; }
    /// <summary>What this generation's app tools reserved (one book + one cancel at most).</summary>
    public Bookings.BookingService.Session Apps { get; init; } = new();
    /// <summary>What this generation's file tools made (documents, QR codes) and the
    /// connected-app actions deferred to delivery (Sheets rows).</summary>
    public Documents.DocumentService.Session Docs { get; init; } = new();

    public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(UtcNow, TimeZone);

    public Dictionary<string, object?> ToDictionary()
    {
        var local = LocalNow;
        var inv = CultureInfo.InvariantCulture;
        return new Dictionary<string, object?>
        {
            ["now"] = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", inv),
            ["timezone"] = TimeZone.Id,
            ["today"] = local.ToString("yyyy-MM-dd", inv),
            ["time"] = local.ToString("HH:mm", inv),
            ["weekday"] = local.DayOfWeek.ToString(),
            ["isoWeekday"] = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek,
            ["hour"] = local.Hour,
            ["minute"] = local.Minute,
            ["utcOffset"] = local.ToString("zzz", inv),
            ["channel"] = Channel,
            ["lastMessage"] = LastMessage,
            ["contact"] = Contact,
        };
    }

    /// <summary>
    /// The contact as a skill sees it: name, language, tags and the operator's
    /// custom fields. Phone/email are deliberately left out — a validation
    /// script has no business with them, and nothing in the sandbox needs them.
    /// </summary>
    public static Dictionary<string, object?> ContactView(Contact? c)
    {
        if (c == null) return new Dictionary<string, object?>();
        var first = (c.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return new Dictionary<string, object?>
        {
            ["name"] = c.Name ?? "",
            ["firstName"] = first,
            ["language"] = c.Language ?? "",
            ["tags"] = c.Tags.ToList(),
            ["company"] = c.CompanyName ?? "",
            ["fields"] = new Dictionary<string, object?>(c.Attributes),
        };
    }
}
