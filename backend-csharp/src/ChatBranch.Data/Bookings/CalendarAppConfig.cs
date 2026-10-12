using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Ai.Skills;

namespace ChatBranch.Data.Bookings;

/// <summary>
/// The configuration of a calendar "Connected app" skill (<c>AgentSkill.Config</c>,
/// kind <c>app</c>, <c>app: "calendar"</c>), snake_case on the wire and in the
/// column. Missing keys take <see cref="Defaults"/>; <see cref="Validate"/> is
/// what the skills PUT refuses a bad config with.
/// </summary>
public sealed class CalendarAppConfig
{
    public const string AppId = "calendar";

    public static readonly string[] Days = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    public static readonly string[] Actions = { "availability", "book", "cancel" };
    public static readonly string[] Sources = { "google", "chatbranch" };
    /// <summary>The length units a calendar may grow by (a config whose unit equals its
    /// default length — every config written before units existed — is valid too).</summary>
    public static readonly int[] Units = { 15, 30, 60 };
    public const int MaxUnitsLimit = 16;

    public string App { get; init; } = AppId;
    public string Source { get; init; } = "chatbranch";
    /// <summary>assignee | shared | an integration id (as a string).</summary>
    public string Calendar { get; init; } = "assignee";
    public string CalendarId { get; init; } = "primary";
    public List<string> AllowedActions { get; init; } = Actions.ToList();
    /// <summary>"" = the organization's timezone.</summary>
    public string Timezone { get; init; } = "";
    /// <summary>Working windows per weekday (local wall-clock), sorted, non-overlapping.</summary>
    public Dictionary<DayOfWeek, List<(TimeSpan From, TimeSpan To)>> Hours { get; init; } = DefaultHours();
    /// <summary>The DEFAULT appointment length (what a tool call without <c>duration_minutes</c> books).</summary>
    public int DurationMinutes { get; init; } = 30;
    /// <summary>The step a booking's length grows by (15 | 30 | 60; = <see cref="DurationMinutes"/> when not set).</summary>
    public int DurationUnitMinutes { get; init; } = 30;
    /// <summary>The longest booking, in units (1–16; 1 = one fixed length, as before).</summary>
    public int MaxUnits { get; init; } = 1;
    public int SlotStepMinutes { get; init; } = 30;
    public int BufferMinutes { get; init; }
    public int MinNoticeMinutes { get; init; } = 120;
    public int MaxDaysAhead { get; init; } = 14;
    public int MaxActivePerContact { get; init; } = 1;
    public string EventTitle { get; init; } = "{{name}} — appointment";
    public string EventDescription { get; init; } = "";

    public bool Allows(string action) => AllowedActions.Contains(action);

    /// <summary>Every length a booking may have, shortest first: unit × 1 … max_units (plus the
    /// default length, which validation keeps among them).</summary>
    public IReadOnlyList<int> Lengths => MaxUnits <= 1
        ? new[] { DurationMinutes }
        : Enumerable.Range(1, Math.Min(MaxUnits, MaxUnitsLimit)).Select(i => i * Math.Max(5, DurationUnitMinutes))
            .Append(DurationMinutes).Distinct().OrderBy(m => m).ToList();

    /// <summary>More than one length — the customer picks how long (N × unit).</summary>
    public bool VariableLength => Lengths.Count > 1;

    public bool AllowsLength(int minutes) => Lengths.Contains(minutes);

    /// <summary>The numeric integration id when <see cref="Calendar"/> names one.</summary>
    public int? IntegrationId => int.TryParse(Calendar, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id : null;

    // ---- defaults ---------------------------------------------------------------

    public static Dictionary<DayOfWeek, List<(TimeSpan, TimeSpan)>> DefaultHours()
    {
        var hours = new Dictionary<DayOfWeek, List<(TimeSpan, TimeSpan)>>();
        foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            hours[d] = new() { (TimeSpan.FromHours(9), TimeSpan.FromHours(17)) };
        return hours;
    }

    /// <summary>The default config in its wire shape (GET /api/aicore/skills/apps).</summary>
    public static Dictionary<string, object?> Defaults() => new CalendarAppConfig().ToDictionary();

    // ---- wire <-> typed -------------------------------------------------------------

    public Dictionary<string, object?> ToDictionary()
    {
        var hours = new Dictionary<string, object?>();
        foreach (var key in Days)
        {
            var dow = DayOf(key);
            hours[key] = (Hours.TryGetValue(dow, out var w) ? w : new())
                .Select(x => new List<string> { Hhmm(x.From), Hhmm(x.To) }).ToList();
        }
        return new Dictionary<string, object?>
        {
            ["app"] = App,
            ["source"] = Source,
            ["calendar"] = Calendar,
            ["calendar_id"] = CalendarId,
            ["actions"] = AllowedActions.ToList(),
            ["timezone"] = Timezone,
            ["hours"] = hours,
            ["duration_minutes"] = DurationMinutes,
            ["duration_unit_minutes"] = DurationUnitMinutes,
            ["max_units"] = MaxUnits,
            ["slot_step_minutes"] = SlotStepMinutes,
            ["buffer_minutes"] = BufferMinutes,
            ["min_notice_minutes"] = MinNoticeMinutes,
            ["max_days_ahead"] = MaxDaysAhead,
            ["max_active_per_contact"] = MaxActivePerContact,
            ["event_title"] = EventTitle,
            ["event_description"] = EventDescription,
        };
    }

    /// <summary>Lenient read of a stored config (values may be CLR objects or JsonElements):
    /// anything unusable falls back to its default. Use <see cref="Validate"/> on writes.</summary>
    public static CalendarAppConfig From(Dictionary<string, object?>? raw)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(raw ?? new Dictionary<string, object?>());
            return From(el, out _);
        }
        catch { return new CalendarAppConfig(); }
    }

    /// <summary>Parse a wire config. <paramref name="error"/> is the first problem
    /// found (null = valid); the returned config has defaults where it was bad.</summary>
    public static CalendarAppConfig From(JsonElement el, out string? error)
    {
        error = null;
        var errors = new List<string>();
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "config must be a JSON object.";
            return new CalendarAppConfig();
        }
        var d = new CalendarAppConfig();

        var app = Str(el, "app") ?? AppId;
        if (app != AppId) errors.Add($"unknown app '{app}' (only \"calendar\" is available).");

        var source = (Str(el, "source") ?? d.Source).Trim().ToLowerInvariant();
        if (!Sources.Contains(source)) { errors.Add($"source must be \"google\" or \"chatbranch\", not '{source}'."); source = d.Source; }

        var calendar = (Str(el, "calendar") ?? d.Calendar).Trim().ToLowerInvariant();
        if (calendar.Length == 0) calendar = d.Calendar;
        if (calendar is not ("assignee" or "shared") && !Regex.IsMatch(calendar, "^[0-9]+$"))
        {
            errors.Add("calendar must be \"assignee\", \"shared\" or the id of a Google Calendar connection.");
            calendar = d.Calendar;
        }

        var calendarId = (Str(el, "calendar_id") ?? d.CalendarId).Trim();
        if (calendarId.Length > 200) { errors.Add("calendar_id is too long."); calendarId = d.CalendarId; }

        var actions = d.AllowedActions;
        if (el.TryGetProperty("actions", out var acts) && acts.ValueKind != JsonValueKind.Null)
        {
            if (acts.ValueKind != JsonValueKind.Array) errors.Add("actions must be a list.");
            else
            {
                actions = new List<string>();
                foreach (var a in acts.EnumerateArray())
                {
                    var name = (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.ToString()).Trim().ToLowerInvariant();
                    if (!Actions.Contains(name)) errors.Add($"unknown action '{name}' (use availability, book, cancel).");
                    else if (!actions.Contains(name)) actions.Add(name);
                }
                if (actions.Count == 0) errors.Add("pick at least one action.");
                // booking without being able to see the free times makes no sense
                if (actions.Contains("book") && !actions.Contains("availability"))
                    actions.Insert(0, "availability");
                actions = Actions.Where(actions.Contains).ToList();
            }
        }

        var tz = (Str(el, "timezone") ?? "").Trim();
        if (tz.Length > 0 && BuiltinSkills.ResolveTimeZone(tz) == null) { errors.Add($"unknown timezone '{tz}'."); tz = ""; }

        var hours = d.Hours;
        if (el.TryGetProperty("hours", out var h) && h.ValueKind != JsonValueKind.Null)
        {
            if (h.ValueKind != JsonValueKind.Object) errors.Add("hours must map weekdays (mon…sun) to time windows.");
            else hours = ParseHours(h, errors);
        }

        int Int(string key, int dflt, int min, int max, string label)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return dflt;
            int n;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n)) { }
            else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out n)) { }
            else { errors.Add($"{label} must be a whole number."); return dflt; }
            if (n < min || n > max) { errors.Add($"{label} must be between {min} and {max}."); return dflt; }
            return n;
        }

        var duration = Int("duration_minutes", d.DurationMinutes, 5, 480, "duration_minutes");
        // variable length: one booking of N × unit, N = 1 … max_units; duration_minutes stays the
        // default length. max_units 1 (or absent) = one fixed length and the unit IS that length —
        // a stale unit is ignored, so an older config behaves exactly as before.
        var unit = Int("duration_unit_minutes", duration, 5, 480, "duration_unit_minutes");
        var maxUnits = Int("max_units", 1, 1, MaxUnitsLimit, "max_units");
        if (maxUnits <= 1) unit = duration;
        else if (unit != duration && !Units.Contains(unit))
        {
            errors.Add("duration_unit_minutes must be 15, 30 or 60.");
            unit = duration;
        }
        else if (duration % unit != 0)
        {
            errors.Add($"duration_minutes ({duration}) must be a whole number of {unit}-minute units.");
            unit = duration;
            maxUnits = 1;
        }
        else if (duration / unit > maxUnits)
        {
            errors.Add($"the default length ({duration} minutes) is longer than the longest booking "
                       + $"({unit * maxUnits} minutes): raise max_units or shorten duration_minutes.");
            maxUnits = duration / unit;
        }
        var step = Int("slot_step_minutes", duration, 5, 480, "slot_step_minutes");
        var buffer = Int("buffer_minutes", d.BufferMinutes, 0, 240, "buffer_minutes");
        var notice = Int("min_notice_minutes", d.MinNoticeMinutes, 0, 60 * 24 * 30, "min_notice_minutes");
        var ahead = Int("max_days_ahead", d.MaxDaysAhead, 1, 90, "max_days_ahead");
        var maxActive = Int("max_active_per_contact", d.MaxActivePerContact, 1, 20, "max_active_per_contact");

        var title = Str(el, "event_title") ?? d.EventTitle;
        if (title.Trim().Length == 0) title = d.EventTitle;
        if (title.Length > 200) { errors.Add("event_title is longer than 200 characters."); title = d.EventTitle; }
        var description = Str(el, "event_description") ?? "";
        if (description.Length > 2000) { errors.Add("event_description is longer than 2,000 characters."); description = ""; }

        // a window too short for even one appointment is a mistake worth naming
        foreach (var (day, windows) in hours)
            foreach (var (from, to) in windows)
                if ((to - from).TotalMinutes < duration)
                {
                    errors.Add($"the {Key(day)} window {Hhmm(from)}–{Hhmm(to)} is shorter than one {duration}-minute appointment.");
                    break;
                }

        error = errors.Count > 0 ? errors[0] : null;
        return new CalendarAppConfig
        {
            App = AppId, Source = source, Calendar = calendar, CalendarId = calendarId.Length > 0 ? calendarId : "primary",
            AllowedActions = actions, Timezone = tz, Hours = hours, DurationMinutes = duration,
            DurationUnitMinutes = unit, MaxUnits = maxUnits,
            SlotStepMinutes = step, BufferMinutes = buffer, MinNoticeMinutes = notice, MaxDaysAhead = ahead,
            MaxActivePerContact = maxActive, EventTitle = title, EventDescription = description,
        };
    }

    private static Dictionary<DayOfWeek, List<(TimeSpan, TimeSpan)>> ParseHours(JsonElement h, List<string> errors)
    {
        var hours = new Dictionary<DayOfWeek, List<(TimeSpan, TimeSpan)>>();
        foreach (var p in h.EnumerateObject())
        {
            var key = p.Name.Trim().ToLowerInvariant();
            if (key.Length > 3) key = key[..3];
            if (!Days.Contains(key)) { errors.Add($"unknown weekday '{p.Name}' in hours (use mon…sun)."); continue; }
            var windows = new List<(TimeSpan, TimeSpan)>();
            if (p.Value.ValueKind is JsonValueKind.Null) { hours[DayOf(key)] = windows; continue; }
            if (p.Value.ValueKind != JsonValueKind.Array) { errors.Add($"hours.{key} must be a list of [from, to] windows."); continue; }
            foreach (var w in p.Value.EnumerateArray())
            {
                string? a = null, b = null;
                if (w.ValueKind == JsonValueKind.Array && w.GetArrayLength() == 2)
                {
                    a = w[0].ValueKind == JsonValueKind.String ? w[0].GetString() : null;
                    b = w[1].ValueKind == JsonValueKind.String ? w[1].GetString() : null;
                }
                else if (w.ValueKind == JsonValueKind.Object)
                {
                    a = Str(w, "from") ?? Str(w, "start");
                    b = Str(w, "to") ?? Str(w, "end");
                }
                var from = ParseTime(a);
                var to = ParseTime(b);
                if (from == null || to == null)
                {
                    errors.Add($"hours.{key}: times must be HH:mm (got '{a}'–'{b}').");
                    continue;
                }
                if (to <= from)
                {
                    errors.Add($"hours.{key}: {a}–{b} ends before it starts.");
                    continue;
                }
                windows.Add((from.Value, to.Value));
            }
            windows = windows.OrderBy(x => x.Item1).ToList();
            for (var i = 1; i < windows.Count; i++)
                if (windows[i].Item1 < windows[i - 1].Item2)
                {
                    errors.Add($"hours.{key}: the windows {Hhmm(windows[i - 1].Item1)}–{Hhmm(windows[i - 1].Item2)} "
                               + $"and {Hhmm(windows[i].Item1)}–{Hhmm(windows[i].Item2)} overlap.");
                    break;
                }
            hours[DayOf(key)] = windows;
        }
        return hours;
    }

    /// <summary>"09:00" / "9:30" / "24:00" → time of day; anything else null.</summary>
    public static TimeSpan? ParseTime(string? raw)
    {
        var m = Regex.Match((raw ?? "").Trim(), @"^([01]?\d|2[0-4]):([0-5]\d)$");
        if (!m.Success) return null;
        var t = new TimeSpan(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        return t > TimeSpan.FromHours(24) ? null : t;
    }

    public static string Hhmm(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    public static DayOfWeek DayOf(string key) => key switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    public static string Key(DayOfWeek d) => Days[d == DayOfWeek.Sunday ? 6 : (int)d - 1];

    private static string? Str(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
