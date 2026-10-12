using System.Globalization;

namespace ChatBranch.Data.Bookings;

/// <summary>
/// Free appointment slots, as pure arithmetic: working hours (per weekday, in
/// the calendar's timezone) minus busy spans (Google free/busy + ChatBranch's
/// own holding bookings) widened by the buffer, from <c>now + min notice</c> to
/// <c>today + max_days_ahead</c>, each <c>duration</c> long, starting on the
/// <c>slot_step</c> grid of its window. <see cref="Spread"/> then picks a
/// handful spread across the days instead of one morning's worth (a search over
/// several days); a single day is listed whole, and <see cref="CheckTime"/> answers
/// for one exact start.
/// </summary>
public static class BookingAvailability
{
    public const int MaxOffered = 8;
    /// <summary>A one-day search lists every free time — this many at most (a safety net).</summary>
    public const int MaxSingleDay = 48;

    /// <summary>A free slot: UTC instants plus the wall-clock times the customer sees.</summary>
    public sealed record Slot(DateTime StartUtc, DateTime EndUtc, DateTime LocalStart, DateTime LocalEnd);

    public static DateOnly Today(DateTime nowUtc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), tz));

    /// <summary>The last bookable local date (today + max_days_ahead).</summary>
    public static DateOnly LastDay(CalendarAppConfig cfg, DateTime nowUtc, TimeZoneInfo tz) =>
        Today(nowUtc, tz).AddDays(cfg.MaxDaysAhead);

    /// <summary>The UTC span covering local dates [from, to] (for busy look-ups).</summary>
    public static (DateTime FromUtc, DateTime ToUtc) UtcSpan(DateOnly from, DateOnly to, TimeZoneInfo tz) =>
        (ToUtc(from.ToDateTime(TimeOnly.MinValue), tz).AddHours(-1),
         ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz).AddHours(1));

    /// <summary>Every free slot on the local dates [from, to], clamped to today … last day,
    /// each <paramref name="durationMinutes"/> long (default: the config's default length) — a
    /// longer booking is only free when the WHOLE span is (busy spans + buffer, working hours).</summary>
    public static List<Slot> FreeSlots(CalendarAppConfig cfg, TimeZoneInfo tz, DateTime nowUtc,
        DateOnly from, DateOnly to, IEnumerable<(DateTime Start, DateTime End)> busyUtc, int? durationMinutes = null)
    {
        nowUtc = Utc(nowUtc);
        var today = Today(nowUtc, tz);
        var last = LastDay(cfg, nowUtc, tz);
        if (from < today) from = today;
        if (to > last) to = last;
        var slots = new List<Slot>();
        if (to < from) return slots;

        var earliest = nowUtc.AddMinutes(cfg.MinNoticeMinutes);
        var buffer = TimeSpan.FromMinutes(cfg.BufferMinutes);
        var busy = busyUtc.Select(b => (Start: Utc(b.Start) - buffer, End: Utc(b.End) + buffer))
            .Where(b => b.End > b.Start).OrderBy(b => b.Start).ToList();
        var duration = TimeSpan.FromMinutes(durationMinutes ?? cfg.DurationMinutes);
        var step = TimeSpan.FromMinutes(Math.Max(5, cfg.SlotStepMinutes));

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (!cfg.Hours.TryGetValue(day.DayOfWeek, out var windows)) continue;
            foreach (var (wFrom, wTo) in windows)
            {
                for (var t = wFrom; t + duration <= wTo; t += step)
                {
                    var localStart = day.ToDateTime(TimeOnly.MinValue).Add(t);
                    if (tz.IsInvalidTime(localStart)) continue; // skipped by a DST jump
                    var startUtc = ToUtc(localStart, tz);
                    var endUtc = startUtc + duration;
                    if (startUtc < earliest) continue;
                    if (busy.Any(b => startUtc < b.End && endUtc > b.Start)) continue;
                    slots.Add(new Slot(startUtc, endUtc, localStart, localStart + duration));
                }
            }
        }
        return slots.OrderBy(s => s.StartUtc).ToList();
    }

    /// <summary>Why an exact start time is not bookable — <see cref="CheckTime"/>'s reasons.</summary>
    public static class Reasons
    {
        public const string OutsideHours = "outside_hours";
        public const string TooSoon = "too_soon";
        public const string Busy = "busy";
        public const string OffGrid = "off_grid";
        public const string BeyondLastDay = "beyond_last_day";
    }

    /// <summary>The answer for one exact start: the free <see cref="Slot"/> when available, else the reason.</summary>
    public sealed record TimeCheck(bool Available, string Reason, Slot? Slot, DateTime LocalStart);

    /// <summary>
    /// Is the exact local start <paramref name="time"/> on <paramref name="day"/> bookable?
    /// <paramref name="free"/> is <see cref="FreeSlots"/> for that day — the list the tools offer
    /// from — so a time is available exactly when that list holds it and the two can never
    /// disagree. Otherwise the reason is the first rule it breaks: beyond_last_day, too_soon (a
    /// past day), outside_hours (no working window holds the whole appointment), off_grid (not
    /// on its window's step), too_soon (inside the minimum notice), else the calendar was busy.
    /// </summary>
    public static TimeCheck CheckTime(CalendarAppConfig cfg, TimeZoneInfo tz, DateTime nowUtc, DateOnly day,
        TimeOnly time, IReadOnlyList<Slot> free, int? durationMinutes = null)
    {
        nowUtc = Utc(nowUtc);
        var localStart = day.ToDateTime(time);
        var hit = free.FirstOrDefault(s => s.LocalStart == localStart);
        if (hit != null) return new TimeCheck(true, "", hit, localStart);
        TimeCheck No(string reason) => new(false, reason, null, localStart);

        if (day > LastDay(cfg, nowUtc, tz)) return No(Reasons.BeyondLastDay);
        if (day < Today(nowUtc, tz)) return No(Reasons.TooSoon);
        var start = time.ToTimeSpan();
        var duration = TimeSpan.FromMinutes(durationMinutes ?? cfg.DurationMinutes);
        var step = TimeSpan.FromMinutes(Math.Max(5, cfg.SlotStepMinutes));
        var holding = (cfg.Hours.TryGetValue(day.DayOfWeek, out var windows) ? windows : new())
            .Where(w => start >= w.From && start + duration <= w.To).ToList();
        if (holding.Count == 0 || tz.IsInvalidTime(localStart)) return No(Reasons.OutsideHours);
        if (!holding.Any(w => (start - w.From).Ticks % step.Ticks == 0)) return No(Reasons.OffGrid);
        if (ToUtc(localStart, tz) < nowUtc.AddMinutes(cfg.MinNoticeMinutes)) return No(Reasons.TooSoon);
        return No(Reasons.Busy);
    }

    /// <summary>
    /// At most <paramref name="max"/> slots spread over the days that have any:
    /// an equal share per day (a single day may fill the whole list; a long range
    /// gets one or two per day), evenly spaced within each day so the customer
    /// sees a morning and an afternoon option rather than 9:00, 9:30, 10:00.
    /// </summary>
    public static List<Slot> Spread(IReadOnlyList<Slot> free, int max = MaxOffered)
    {
        var days = free.GroupBy(s => DateOnly.FromDateTime(s.LocalStart)).OrderBy(g => g.Key).ToList();
        if (days.Count == 0 || max <= 0) return new List<Slot>();
        var perDay = Math.Clamp((int)Math.Ceiling(max / (double)days.Count), 1, max);
        var picked = new List<Slot>();
        foreach (var day in days)
        {
            var list = day.ToList();
            var k = Math.Min(perDay, Math.Min(list.Count, max - picked.Count));
            if (k <= 0) break;
            if (k == 1) { picked.Add(list[0]); continue; }
            // the middle of each of k equal stretches of the day — e.g. 10:30 and
            // 14:30 out of 09:00–16:00 — reads like a person offering "a morning
            // or an afternoon", not the day's first and last minute
            var indexes = Enumerable.Range(0, k)
                .Select(i => Math.Min(list.Count - 1, (int)Math.Floor((i + 0.5) * list.Count / k)))
                .Distinct();
            picked.AddRange(indexes.Select(i => list[i]));
        }
        return picked.OrderBy(s => s.StartUtc).Take(max).ToList();
    }

    // ---- formatting -------------------------------------------------------------

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"2026-10-14T15:00" — the wall-clock form the tools speak.</summary>
    public static string LocalIso(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm", Inv);

    /// <summary>"Tue 14 Oct, 3:00 PM".</summary>
    public static string StartLabel(DateTime local) => local.ToString("ddd d MMM, h:mm tt", Inv);

    /// <summary>"Tue 14 Oct, 3:00–3:30 PM" (or "11:30 AM–12:00 PM" across noon).</summary>
    public static string RangeLabel(DateTime localStart, DateTime localEnd)
    {
        var day = localStart.ToString("ddd d MMM", Inv);
        if (localStart.Date != localEnd.Date && localEnd.TimeOfDay != TimeSpan.Zero)
            return $"{day}, {localStart.ToString("h:mm tt", Inv)}–{localEnd.ToString("ddd d MMM, h:mm tt", Inv)}";
        var samePeriod = localStart.ToString("tt", Inv) == localEnd.ToString("tt", Inv) && localEnd.TimeOfDay != TimeSpan.Zero;
        return samePeriod
            ? $"{day}, {localStart.ToString("h:mm", Inv)}–{localEnd.ToString("h:mm tt", Inv)}"
            : $"{day}, {localStart.ToString("h:mm tt", Inv)}–{localEnd.ToString("h:mm tt", Inv)}";
    }

    /// <summary>ISO 8601 with the zone's offset ("2026-10-14T15:00:00+08:00").</summary>
    public static string OffsetIso(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), tz);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv);
    }

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), tz);

    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, tz), DateTimeKind.Utc);
    }

    /// <summary>SQLite hands DateTimes back as Unspecified; every stored instant is UTC.</summary>
    public static DateTime Utc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };
}
