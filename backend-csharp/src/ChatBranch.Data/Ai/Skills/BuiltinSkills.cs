using System.Globalization;
using System.Text.Json;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// Skills the AI always has as TOOLS, implemented in C# because date maths is
/// exactly where a language model is confidently wrong (which weekday is the
/// 14th, what ISO week it is, "three business days from Friday"). Listed
/// read-only in the UI. Every function takes the clock as a parameter so tests
/// pin it; the default zone is the organization's (<c>org_timezone</c> Setting,
/// then the TZ environment variable, then UTC).
/// </summary>
public static class BuiltinSkills
{
    public sealed record Builtin(string Name, string Label, string Description, string ParametersJson);

    private const string TzProp =
        "\"timezone\":{\"type\":\"string\",\"description\":\"IANA timezone, e.g. Asia/Kuala_Lumpur. Defaults to the business timezone.\"}";

    public static readonly IReadOnlyList<Builtin> All = new[]
    {
        new Builtin("current_time", "Current time",
            "The current date, time and weekday. Call this before stating the time, today's date or the day of the week.",
            "{\"type\":\"object\",\"properties\":{" + TzProp + "}}"),
        new Builtin("current_week", "Current week",
            "ISO week number, the Monday–Sunday range and the day of week for today (or for a given date).",
            "{\"type\":\"object\",\"properties\":{\"date\":{\"type\":\"string\",\"description\":\"YYYY-MM-DD; defaults to today\"}," + TzProp + "}}"),
        new Builtin("date_add", "Add to a date",
            "Add (or subtract, with a negative amount) days, weeks or business days (Mon–Fri) to a date.",
            "{\"type\":\"object\",\"properties\":{\"date\":{\"type\":\"string\",\"description\":\"YYYY-MM-DD; defaults to today\"}," +
            "\"amount\":{\"type\":\"integer\"},\"unit\":{\"type\":\"string\",\"enum\":[\"days\",\"weeks\",\"business_days\"]}," + TzProp +
            "},\"required\":[\"amount\"]}"),
        new Builtin("days_between", "Days between dates",
            "Calendar days and business days (Mon–Fri) between two dates; the end date defaults to today.",
            "{\"type\":\"object\",\"properties\":{\"from\":{\"type\":\"string\",\"description\":\"YYYY-MM-DD\"}," +
            "\"to\":{\"type\":\"string\",\"description\":\"YYYY-MM-DD; defaults to today\"}," + TzProp + "},\"required\":[\"from\"]}"),
        new Builtin("convert_timezone", "Convert timezone",
            "Convert a date-time (or a HH:mm time today) from one timezone to another.",
            "{\"type\":\"object\",\"properties\":{\"time\":{\"type\":\"string\",\"description\":\"ISO date-time or HH:mm; defaults to now\"}," +
            "\"from\":{\"type\":\"string\",\"description\":\"IANA timezone of `time`; defaults to the business timezone\"}," +
            "\"to\":{\"type\":\"string\",\"description\":\"IANA timezone to convert into\"}},\"required\":[\"to\"]}"),
    };

    private static readonly HashSet<string> Names = All.Select(b => b.Name).ToHashSet();

    public static bool IsBuiltin(string? name) => name != null && Names.Contains(name);

    public static IEnumerable<LlmTool> Tools() => All.Select(b =>
        new LlmTool(b.Name, b.Description, JsonDocument.Parse(b.ParametersJson).RootElement.Clone()));

    /// <summary>
    /// Run a built-in. Returns the JSON result; bad arguments throw
    /// <see cref="ArgumentException"/>, which the tool loop reports back to the
    /// model as data rather than breaking the reply.
    /// </summary>
    public static string Execute(string name, string? argsJson, DateTimeOffset utcNow, TimeZoneInfo orgTz)
    {
        JsonElement args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(argsJson).RootElement.Clone();
        }
        catch { throw new ArgumentException("arguments are not valid JSON"); }
        if (args.ValueKind != JsonValueKind.Object) args = JsonDocument.Parse("{}").RootElement;

        object result = name switch
        {
            "current_time" => CurrentTime(utcNow, Zone(args, "timezone", orgTz)),
            "current_week" => CurrentWeek(utcNow, Zone(args, "timezone", orgTz), Arg(args, "date")),
            "date_add" => DateAdd(utcNow, Zone(args, "timezone", orgTz), Arg(args, "date"),
                IntArg(args, "amount") ?? throw new ArgumentException("`amount` is required"),
                Arg(args, "unit") is { Length: > 0 } u ? u : "days"),
            "days_between" => DaysBetween(utcNow, Zone(args, "timezone", orgTz),
                Arg(args, "from") is { Length: > 0 } f ? f : throw new ArgumentException("`from` is required"),
                Arg(args, "to")),
            "convert_timezone" => ConvertTimezone(utcNow, Arg(args, "time"),
                Zone(args, "from", orgTz),
                Arg(args, "to") is { Length: > 0 } to
                    ? ResolveTimeZone(to) ?? throw new ArgumentException($"unknown timezone '{to}'")
                    : throw new ArgumentException("`to` is required")),
            _ => throw new ArgumentException($"unknown built-in '{name}'"),
        };
        return JsonSerializer.Serialize(result);
    }

    // ---- the five ------------------------------------------------------------

    public static object CurrentTime(DateTimeOffset utcNow, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        return new
        {
            timezone = tz.Id,
            iso = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv),
            date = local.ToString("yyyy-MM-dd", Inv),
            time = local.ToString("HH:mm", Inv),
            time_12h = local.ToString("h:mm tt", Inv),
            weekday = local.DayOfWeek.ToString(),
            utc_offset = local.ToString("zzz", Inv),
            human = local.ToString("dddd, d MMMM yyyy, h:mm tt", Inv),
        };
    }

    public static object CurrentWeek(DateTimeOffset utcNow, TimeZoneInfo tz, string? date)
    {
        var day = date is { Length: > 0 } ? ParseDate(date) : Today(utcNow, tz);
        var dt = day.ToDateTime(TimeOnly.MinValue);
        var isoDow = IsoDow(day);
        var start = day.AddDays(1 - isoDow);
        return new
        {
            date = Fmt(day),
            iso_week = ISOWeek.GetWeekOfYear(dt),
            iso_year = ISOWeek.GetYear(dt),
            week_start = Fmt(start),
            week_end = Fmt(start.AddDays(6)),
            day_of_week = day.DayOfWeek.ToString(),
            day_of_week_number = isoDow,
            timezone = tz.Id,
        };
    }

    public static object DateAdd(DateTimeOffset utcNow, TimeZoneInfo tz, string? date, int amount, string unit)
    {
        var from = date is { Length: > 0 } ? ParseDate(date) : Today(utcNow, tz);
        var result = unit.ToLowerInvariant() switch
        {
            "day" or "days" => from.AddDays(amount),
            "week" or "weeks" => from.AddDays(amount * 7),
            "business_day" or "business_days" or "businessdays" or "workdays" => AddBusinessDays(from, amount),
            _ => throw new ArgumentException("`unit` must be days, weeks or business_days"),
        };
        return new
        {
            from = Fmt(from),
            amount,
            unit,
            result = Fmt(result),
            weekday = result.DayOfWeek.ToString(),
        };
    }

    public static object DaysBetween(DateTimeOffset utcNow, TimeZoneInfo tz, string from, string? to)
    {
        var a = ParseDate(from);
        var b = to is { Length: > 0 } ? ParseDate(to) : Today(utcNow, tz);
        var days = b.DayNumber - a.DayNumber;
        return new
        {
            from = Fmt(a),
            to = Fmt(b),
            days,
            weeks = Math.Round(days / 7.0, 2),
            business_days = BusinessDaysBetween(a, b),
        };
    }

    public static object ConvertTimezone(DateTimeOffset utcNow, string? time, TimeZoneInfo from, TimeZoneInfo to)
    {
        DateTimeOffset instant;
        if (string.IsNullOrWhiteSpace(time)) instant = utcNow;
        else if (TimeOnly.TryParseExact(time.Trim(), new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" }, Inv,
                     DateTimeStyles.None, out var t))
        {
            var today = Today(utcNow, from).ToDateTime(t);
            instant = new DateTimeOffset(today, from.GetUtcOffset(today));
        }
        else if (DateTimeOffset.TryParse(time, Inv, DateTimeStyles.None, out var dto)
                 && HasOffset(time))
            instant = dto;
        else if (DateTime.TryParse(time, Inv, DateTimeStyles.None, out var dt))
        {
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            instant = new DateTimeOffset(unspecified, from.GetUtcOffset(unspecified));
        }
        else throw new ArgumentException($"could not read the time '{time}'");

        var src = TimeZoneInfo.ConvertTime(instant, from);
        var dst = TimeZoneInfo.ConvertTime(instant, to);
        return new
        {
            from_timezone = from.Id,
            to_timezone = to.Id,
            input = src.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv),
            result = dst.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv),
            result_date = dst.ToString("yyyy-MM-dd", Inv),
            result_time = dst.ToString("HH:mm", Inv),
            result_weekday = dst.DayOfWeek.ToString(),
        };
    }

    // ---- timezone resolution --------------------------------------------------

    /// <summary>IANA ("Asia/Kuala_Lumpur") or Windows id; null when unknown.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals("GMT", StringComparison.OrdinalIgnoreCase)
            || id == "Etc/UTC")
            return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            try
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
                    return TimeZoneInfo.FindSystemTimeZoneById(win);
            }
            catch { }
            return null;
        }
    }

    /// <summary>The business timezone — the same chain the flow clock uses
    /// (<see cref="Flows.OrgClock"/>): Setting <c>org_timezone</c> → legacy
    /// <c>timezone</c> → ORG_TIMEZONE / TZ env → UTC.</summary>
    public static TimeZoneInfo OrgTimeZone(ScopingService? scoping)
    {
        return ResolveTimeZone(scoping?.SettingValue(Flows.OrgClock.SettingKey))
               ?? ResolveTimeZone(scoping?.SettingValue(Flows.OrgClock.LegacySettingKey))
               ?? ResolveTimeZone(Environment.GetEnvironmentVariable("ORG_TIMEZONE"))
               ?? ResolveTimeZone(Environment.GetEnvironmentVariable("TZ"))
               ?? TimeZoneInfo.Utc;
    }

    // ---- helpers --------------------------------------------------------------

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string? Arg(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int? IntArg(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
        return null;
    }

    private static TimeZoneInfo Zone(JsonElement args, string key, TimeZoneInfo fallback)
    {
        var raw = Arg(args, key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return ResolveTimeZone(raw) ?? throw new ArgumentException($"unknown timezone '{raw}'");
    }

    private static DateOnly Today(DateTimeOffset utcNow, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, tz).DateTime);

    private static DateOnly ParseDate(string raw)
    {
        var s = raw.Trim();
        if (DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(s, Inv, DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
        throw new ArgumentException($"could not read the date '{raw}' (use YYYY-MM-DD)");
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static int IsoDow(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static bool Weekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static DateOnly AddBusinessDays(DateOnly from, int n)
    {
        var step = n < 0 ? -1 : 1;
        var left = Math.Abs(n);
        var cur = from;
        while (left > 0)
        {
            cur = cur.AddDays(step);
            if (!Weekend(cur)) left--;
        }
        return cur;
    }

    /// <summary>Business days strictly after `a` up to and including `b` (signed).</summary>
    public static int BusinessDaysBetween(DateOnly a, DateOnly b)
    {
        if (a == b) return 0;
        var sign = b > a ? 1 : -1;
        var (lo, hi) = b > a ? (a, b) : (b, a);
        var total = hi.DayNumber - lo.DayNumber;
        var count = total / 7 * 5;            // every full week holds 5 weekdays
        var tailStart = lo.AddDays(total / 7 * 7);
        for (var i = 1; i <= total % 7; i++)
            if (!Weekend(tailStart.AddDays(i))) count++;
        return sign * count;
    }

    private static bool HasOffset(string s) =>
        s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$");
}
