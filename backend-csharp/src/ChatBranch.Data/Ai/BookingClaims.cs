using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The guard behind the built-in check <c>builtin:no_false_booking_claim</c>: a
/// reply may only say an appointment HAS BEEN booked / cancelled when it is true.
/// A "made" claim is backed by this turn's <c>*_book</c> result with status
/// <c>reserved</c>, or by restating one of the conversation's ACTIVE bookings — the
/// claim's sentence names that booking's start time or date ("3:15", "3.15", "15:15",
/// "12 Oct", "Monday", "today") and no other time or date. A "cancelled" claim is
/// backed by this turn's <c>*_cancel</c> result with <c>cancel_pending</c>, or when the
/// most recent booking for the time it names is already cancelled. A model once told
/// a customer "your appointment has been cancelled" without calling the tool, and
/// the booking stayed active; another restated a real booking ("Your appointment is
/// confirmed for Mon 12 Oct, 3:15–3:45 PM") and was stopped for it. Quoting a booking
/// with no claim verb ("your booking is on Monday 1:30 PM") always passes.
/// Pure: the caller loads the bookings (<see cref="Bookings.BookingService.KnownForClaims"/>).
/// </summary>
public static class BookingClaims
{
    /// <summary>One of the conversation's bookings, in its own local time.</summary>
    public sealed record KnownBooking(DateTime LocalStart, DateTime LocalEnd, string Status, DateTime UpdatedAt)
    {
        public bool Active => Status == ChatBranch.Domain.BookingStatus.Booked;
        public bool Cancelled => Status == ChatBranch.Domain.BookingStatus.Cancelled;
    }

    /// <summary>A sentence that claims something nothing backs: <c>Kind</c> = "made" | "cancelled".</summary>
    public sealed record Claim(string Kind, string Sentence);

    private static readonly Regex CancelClaim = new(
        @"\b(has been|have been|is|was|successfully)\s+(cancel+ed)\b|\bcancel+ed (your|the)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BookClaim = new(
        @"\b(has been|is|successfully)\s+(booked|confirmed|reserved)\b|\bbooking is confirmed\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"made" / "cancelled" when the draft claims something no tool did; null when it's honest.</summary>
    public static string? Unbacked(string draft, bool booked, bool cancelled) =>
        Unbacked(draft, booked, cancelled, Array.Empty<KnownBooking>(), null);

    /// <summary>
    /// As above, with the conversation's bookings: a claim that restates one of them
    /// is honest. <paramref name="today"/> = the local date ("today" / "tomorrow").
    /// </summary>
    public static string? Unbacked(string draft, bool booked, bool cancelled,
        IReadOnlyList<KnownBooking> known, DateOnly? today)
    {
        var claims = UnbackedClaims(draft, booked, cancelled, known, today);
        return claims.Any(c => c.Kind == "cancelled") ? "cancelled" : claims.FirstOrDefault()?.Kind;
    }

    /// <summary>Every sentence of the draft whose claim nothing backs, in order.</summary>
    public static IReadOnlyList<Claim> UnbackedClaims(string draft, bool booked, bool cancelled,
        IReadOnlyList<KnownBooking>? known, DateOnly? today)
    {
        var found = new List<Claim>();
        if (string.IsNullOrWhiteSpace(draft) || (booked && cancelled)) return found;
        known ??= Array.Empty<KnownBooking>();
        foreach (var sentence in Sentences(draft))
        {
            if (!cancelled && CancelClaim.IsMatch(sentence) && !CancelBacked(sentence, draft, known, today))
                found.Add(new Claim("cancelled", sentence));
            else if (!booked && BookClaim.IsMatch(sentence) && !MadeBacked(sentence, draft, known, today))
                found.Add(new Claim("made", sentence));
        }
        return found;
    }

    /// <summary>
    /// The check's fix line: what to do, plus the conversation's active bookings so a
    /// rewrite can restate one honestly.
    /// </summary>
    public static string FixText(IReadOnlyList<KnownBooking>? known)
    {
        const string basic = "Call the calendar tool first, or don't say it is done.";
        var active = (known ?? Array.Empty<KnownBooking>()).Where(b => b.Active).OrderBy(b => b.LocalStart).ToList();
        if (active.Count == 0) return basic;
        var labels = string.Join("; ", active.Select(b => Bookings.BookingAvailability.RangeLabel(b.LocalStart, b.LocalEnd)));
        return active.Count == 1
            ? $"Existing booking: {labels}. You may say it is booked for that time; don't say a NEW booking was made unless you call the book tool."
            : $"Existing bookings: {labels}. You may say they are booked for those times; don't say a NEW booking was made unless you call the book tool.";
    }

    // ---- sentences --------------------------------------------------------------------------

    // a sentence ends at . ! ? (or their CJK forms) followed by space, or at a line break;
    // never after "a.m." / "p.m." (a time, not the end of a sentence)
    private static readonly Regex SentenceEnd = new(
        @"(?<=[.!?。！？])(?<![ap]\.m\.)\s+|(?<=[。！？])|\n+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The draft's sentences, exactly as written (trimmed; empty ones dropped).</summary>
    public static IReadOnlyList<string> Sentences(string text) =>
        SentenceEnd.Split(text ?? "").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    // ---- what a sentence names ----------------------------------------------------------------

    private sealed record TimeMention(int Hour, int Minute, string? Meridiem);
    private sealed record DateMention(int? Day, int? Month, DayOfWeek? Weekday);

    private static readonly Regex Clock = new(
        @"(?<![\d.:,]|RM\s?|\$\s?|MYR\s?|USD\s?|SGD\s?)(\d{1,2})[:.](\d{2})(?![.:,]?\d)(?:\s*(am|pm|a\.m\.|p\.m\.)(?![a-z]))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HourOnly = new(
        @"(?<![\d.:,])\b(\d{1,2})\s?(am|pm|a\.m\.|p\.m\.)(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MonthNames =
        { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    private const string MonthWord =
        @"(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private static readonly Regex DayMonth = new(
        $@"\b(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?{MonthWord}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthDay = new(
        $@"\b{MonthWord}\.?\s+(\d{{1,2}})(?:st|nd|rd|th)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Weekday = new(
        @"\b(mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)(?:day|nesday|rsday|urday|sday)?\b|\b(today|tonight|tomorrow)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (List<TimeMention> Times, List<DateMention> Dates) Mentions(string text, DateOnly? today)
    {
        var times = new List<TimeMention>();
        foreach (Match m in Clock.Matches(text))
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (h > 23 || min > 59) continue;
            times.Add(new TimeMention(h, min, Meridiem(m.Groups[3].Value)));
        }
        foreach (Match m in HourOnly.Matches(text))
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (h is < 1 or > 12) continue;
            times.Add(new TimeMention(h, 0, Meridiem(m.Groups[2].Value)));
        }

        var dates = new List<DateMention>();
        foreach (Match m in DayMonth.Matches(text))
            if (Day(m.Groups[1].Value) is { } d) dates.Add(new DateMention(d, MonthOf(m.Groups[2].Value), null));
        foreach (Match m in MonthDay.Matches(text))
            if (Day(m.Groups[2].Value) is { } d) dates.Add(new DateMention(d, MonthOf(m.Groups[1].Value), null));
        foreach (Match m in Weekday.Matches(text))
        {
            if (m.Groups[2].Success)
            {
                if (today is not { } t) continue; // no "today" to measure from: not a mention
                var day = m.Groups[2].Value.ToLowerInvariant() == "tomorrow" ? t.AddDays(1) : t;
                dates.Add(new DateMention(day.Day, day.Month, null));
            }
            else dates.Add(new DateMention(null, null, WeekdayOf(m.Groups[1].Value)));
        }
        return (times, dates);
    }

    private static string? Meridiem(string raw) => raw.Length == 0 ? null
        : raw.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? "pm" : "am";

    private static int? Day(string raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31 ? d : null;

    private static int MonthOf(string word) =>
        Array.IndexOf(MonthNames, word[..3].ToLowerInvariant()) + 1;

    private static DayOfWeek WeekdayOf(string word) => word[..3].ToLowerInvariant() switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    private static bool TimeIs(TimeMention t, DateTime local)
    {
        if (t.Minute != local.Minute) return false;
        if (t.Meridiem == "pm") return (t.Hour % 12) + 12 == local.Hour;
        if (t.Meridiem == "am") return t.Hour % 12 == local.Hour;
        // "3:15" with no am/pm: either half of the day; "15:15" is a 24-hour time
        return t.Hour > 12 ? t.Hour == local.Hour : t.Hour % 12 == local.Hour % 12;
    }

    private static bool DateIs(DateMention d, DateTime local) =>
        d.Weekday is { } w ? local.DayOfWeek == w : local.Day == d.Day && local.Month == d.Month;

    // ---- backing ------------------------------------------------------------------------------

    /// <summary>
    /// A "made" claim restates an active booking: its sentence (else, when the sentence
    /// names nothing, the whole draft) names that booking's start time or date, and
    /// every time it names is that booking's start or end and every date its date.
    /// Several active bookings: one of them must fit every mention.
    /// </summary>
    private static bool MadeBacked(string sentence, string draft, IReadOnlyList<KnownBooking> known, DateOnly? today)
    {
        var active = known.Where(b => b.Active).ToList();
        if (active.Count == 0) return false;
        var (times, dates) = Named(sentence, draft, today);
        if (times.Count == 0 && dates.Count == 0) return false;
        // a time named must include the booking's start ("3:45" alone is its end — or a new time)
        return active.Any(b =>
            times.All(t => TimeIs(t, b.LocalStart) || TimeIs(t, b.LocalEnd))
            && dates.All(d => DateIs(d, b.LocalStart))
            && (times.Count == 0 || times.Any(t => TimeIs(t, b.LocalStart))));
    }

    /// <summary>
    /// A "cancelled" claim is true when the most recent booking for the time / date it
    /// names is cancelled (when it names none: the latest booking is cancelled and no
    /// booking is active).
    /// </summary>
    private static bool CancelBacked(string sentence, string draft, IReadOnlyList<KnownBooking> known, DateOnly? today)
    {
        if (known.Count == 0) return false;
        var (times, dates) = Named(sentence, draft, today);
        if (times.Count == 0 && dates.Count == 0)
            return !known.Any(b => b.Active) && known.MaxBy(b => b.UpdatedAt)!.Cancelled;
        var forThat = known.Where(b =>
            times.All(t => TimeIs(t, b.LocalStart) || TimeIs(t, b.LocalEnd))
            && dates.All(d => DateIs(d, b.LocalStart))).ToList();
        return forThat.Count > 0 && forThat.MaxBy(b => b.UpdatedAt)!.Cancelled;
    }

    private static (List<TimeMention> Times, List<DateMention> Dates) Named(string sentence, string draft, DateOnly? today)
    {
        var own = Mentions(sentence, today);
        return own.Times.Count > 0 || own.Dates.Count > 0 ? own : Mentions(draft, today);
    }

    /// <summary>Does a tool's JSON result carry <c>"status": want</c>?</summary>
    public static bool HasStatus(string? resultJson, string want)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var st)
                   && st.ValueKind == JsonValueKind.String
                   && string.Equals(st.GetString(), want, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
