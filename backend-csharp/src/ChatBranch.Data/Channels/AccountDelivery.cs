using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Per-account delivery + AI settings. The knobs that used to live only as
/// organization-wide Setting rows (Settings → AI Configuration) — quiet hours,
/// send pacing, the voice-call LLM brain and STT engine — are now configured
/// ON THE CHANNEL ACCOUNT itself (Settings → Channels → edit account), stored
/// in <see cref="ChannelAccount.Config"/> next to <c>role</c>.
///
/// Resolution is account → entity/global Setting row → env → built-in default,
/// so an account with nothing set behaves exactly as before. Every read path
/// (queue drain, template send gate, call dispatch) goes through here so the
/// precedence lives in ONE place.
/// </summary>
public static class AccountDelivery
{
    public const string QuietStart = "quiet_start";
    public const string QuietEnd = "quiet_end";
    public const string MinGap = "queue_min_gap_s";
    public const string MaxGap = "queue_max_gap_s";
    public const string LlmProvider = "llm_provider";
    public const string SttProvider = "stt_provider";
    /// <summary>Optional per-weekday quiet hours, JSON:
    /// <c>{"mon":{"on":true,"start":"21:00","end":"09:00"}, …}</c>. When present
    /// it REPLACES the single quiet_start/quiet_end window.</summary>
    public const string QuietSchedule = "quiet_schedule";

    public const int DefaultMinGap = 10;
    public const int DefaultMaxGap = 30;

    /// <summary>Config keys the accounts API reads/writes for this feature.</summary>
    public static readonly string[] Keys =
        { QuietStart, QuietEnd, QuietSchedule, MinGap, MaxGap, LlmProvider, SttProvider };

    /// <summary>Weekday keys, Monday first — the order the editor renders.</summary>
    public static readonly string[] Days = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

    /// <summary>The raw per-account value ("" = not overridden, inherit).</summary>
    public static string Own(ChannelAccount? account, string key) =>
        account != null && account.Config.TryGetValue(key, out var v) ? (v?.ToString() ?? "").Trim() : "";

    /// <summary>Account override → Setting row (entity then global) → env.</summary>
    public static string Resolve(ChannelAccount? account, string key, ScopingService scoping)
    {
        var own = Own(account, key);
        if (own.Length > 0) return own;
        var v = scoping.SettingValue(key, account?.EntityId);
        if (v.Length > 0) return v;
        return Environment.GetEnvironmentVariable(key.ToUpperInvariant()) ?? "";
    }

    /// <summary>Jittered pacing window (min, max) in seconds for this account.</summary>
    public static (int min, int max) PacingGap(ChannelAccount? account, ScopingService scoping)
    {
        var min = int.TryParse(Resolve(account, MinGap, scoping), out var lo) ? lo : DefaultMinGap;
        var max = int.TryParse(Resolve(account, MaxGap, scoping), out var hi) ? hi : DefaultMaxGap;
        if (max < min) max = min;
        return (min, max);
    }

    /// <summary>The account's quiet-hours window as raw HH:MM strings ("" = none).</summary>
    public static (string start, string end) QuietWindow(ChannelAccount? account, ScopingService scoping) =>
        (Resolve(account, QuietStart, scoping), Resolve(account, QuietEnd, scoping));

    /// <summary>
    /// wa_config.in_quiet_hours — server-local HH:MM window that may wrap
    /// midnight; both empty (or unparsable) = never quiet.
    ///
    /// A per-weekday schedule (<see cref="QuietSchedule"/>) takes precedence
    /// when configured: a business that is quiet from 6pm on weekdays but all
    /// day Sunday cannot be expressed as one window.
    /// </summary>
    public static bool InQuietHours(ChannelAccount? account, ScopingService scoping, DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;
        var schedule = ParseSchedule(Resolve(account, QuietSchedule, scoping));
        if (schedule.Count > 0) return InScheduledQuietHours(schedule, moment);

        var (s, e) = QuietWindow(account, scoping);
        if (!TryParseHhMm(s, out var qs) || !TryParseHhMm(e, out var qe)) return false;
        var t = TimeOnly.FromDateTime(moment);
        return qs <= qe ? t >= qs && t < qe : t >= qs || t < qe;
    }

    /// <summary>One weekday's rule: off = that day is never quiet.</summary>
    public record DayRule(bool On, TimeOnly Start, TimeOnly End);

    /// <summary>
    /// Parse the schedule JSON. Unknown days, malformed times and bad JSON are
    /// dropped rather than throwing — quiet hours must never break a send path.
    /// An empty result means "no schedule configured".
    /// </summary>
    public static Dictionary<string, DayRule> ParseSchedule(string raw)
    {
        var into = new Dictionary<string, DayRule>();
        if (string.IsNullOrWhiteSpace(raw)) return into;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return into;
            foreach (var day in Days)
            {
                if (!doc.RootElement.TryGetProperty(day, out var el)
                    || el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var on = !el.TryGetProperty("on", out var onEl)
                         || onEl.ValueKind != System.Text.Json.JsonValueKind.False;
                var start = el.TryGetProperty("start", out var sEl) ? sEl.GetString() ?? "" : "";
                var end = el.TryGetProperty("end", out var eEl) ? eEl.GetString() ?? "" : "";
                if (!TryParseHhMm(start, out var ps) || !TryParseHhMm(end, out var pe)) continue;
                into[day] = new DayRule(on, ps, pe);
            }
        }
        catch { /* malformed schedule = no schedule */ }
        return into;
    }

    /// <summary>
    /// Evaluate the weekday schedule. A window whose end is at or before its
    /// start wraps past midnight, so YESTERDAY's rule can still be running now —
    /// both days are checked.
    /// </summary>
    private static bool InScheduledQuietHours(Dictionary<string, DayRule> schedule, DateTime moment)
    {
        var t = TimeOnly.FromDateTime(moment);
        var today = DayKey(moment.DayOfWeek);
        if (schedule.TryGetValue(today, out var rule) && rule.On)
        {
            if (rule.Start == rule.End) return true;              // 24h quiet
            if (rule.Start < rule.End && t >= rule.Start && t < rule.End) return true;
            if (rule.Start > rule.End && t >= rule.Start) return true;   // wraps into tomorrow
        }
        var yesterday = DayKey(moment.AddDays(-1).DayOfWeek);
        if (schedule.TryGetValue(yesterday, out var prev) && prev.On
            && prev.Start > prev.End && t < prev.End)
            return true;                                          // still inside last night's window
        return false;
    }

    public static string DayKey(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        _ => "sun",
    };

    public static bool TryParseHhMm(string value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return TimeOnly.TryParseExact(value.Trim(), new[] { "HH:mm", "H:mm" }, out time);
    }
}
