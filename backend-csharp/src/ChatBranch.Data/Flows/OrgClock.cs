using System.Globalization;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The organization's wall clock. Flow statistics are bucketed per calendar
/// day, and "today" must be the operator's today — not UTC's, which flips at
/// 08:00 in Kuala Lumpur. The zone comes from the <c>org_timezone</c> Setting
/// row (an IANA id such as "Asia/Kuala_Lumpur" — the one the AI skills' date
/// tools use, set at Settings → Time zone), then the legacy
/// <c>timezone</c> row, then env <c>ORG_TIMEZONE</c> / <c>TZ</c>, then the
/// server's local zone — the same clock quiet hours already use.
/// </summary>
public static class OrgClock
{
    public const string SettingKey = "org_timezone";
    public const string LegacySettingKey = "timezone";

    public static TimeZoneInfo Zone(ScopingService scoping)
    {
        var id = scoping.SettingValue(SettingKey);
        if (id.Length == 0) id = scoping.SettingValue(LegacySettingKey);
        if (id.Length == 0) id = Environment.GetEnvironmentVariable("ORG_TIMEZONE") ?? "";
        if (id.Length == 0) id = Environment.GetEnvironmentVariable("TZ") ?? "";
        if (id.Length > 0)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* unknown id — fall back to the server clock */ }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Where the business time zone comes from: <c>setting</c> (Settings → Time zone),
    /// <c>env</c> (ORG_TIMEZONE / TZ) or <c>server</c> (the machine's own zone — nobody set one).</summary>
    public static (string Id, string Source) Describe(ScopingService scoping)
    {
        foreach (var (raw, source) in new[]
                 {
                     (scoping.SettingValue(SettingKey), "setting"),
                     (scoping.SettingValue(LegacySettingKey), "setting"),
                     (Environment.GetEnvironmentVariable("ORG_TIMEZONE") ?? "", "env"),
                     (Environment.GetEnvironmentVariable("TZ") ?? "", "env"),
                 })
            if (Normalize(raw) is { } id) return (id, source);
        return (IanaId(TimeZoneInfo.Local), "server");
    }

    private static readonly System.Text.RegularExpressions.Regex ZoneShape =
        new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$");

    /// <summary>
    /// The IANA id ("Asia/Kuala_Lumpur") of a zone this server knows, else null — what a
    /// browser's Intl understands too, so the dashboard can show times in it. A Windows
    /// id is converted; the zoneinfo folder's non-zones (posix/, right/, Factory…) are not zones.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (id.Length == 0 || id.Length > 64) return null;
        if (id.StartsWith("posix/", StringComparison.Ordinal) || id.StartsWith("right/", StringComparison.Ordinal)
            || id is "Factory" or "posixrules" or "localtime")
            return null;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            var iana = tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var x) ? x : null;
            return iana != null && ZoneShape.IsMatch(iana) ? iana : null;
        }
        catch
        {
            return null;
        }
    }

    private static string IanaId(TimeZoneInfo tz) =>
        tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var x) ? x : "UTC";

    public static DateTime ToOrg(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public static DateTime ToUtc(DateTime orgLocal, TimeZoneInfo zone)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(orgLocal, DateTimeKind.Unspecified), zone);
        }
        catch (ArgumentException)
        {
            // a wall time skipped by a DST jump — nudge forward an hour
            return TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(orgLocal.AddHours(1), DateTimeKind.Unspecified), zone);
        }
    }

    /// <summary>The org-local calendar day of a UTC instant, as yyyy-MM-dd.</summary>
    public static string Day(DateTime utc, TimeZoneInfo zone) =>
        ToOrg(utc, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>UTC instant at which today's org-local day began.</summary>
    public static DateTime TodayStartUtc(TimeZoneInfo zone, DateTime? nowUtc = null) =>
        ToUtc(ToOrg(nowUtc ?? DateTime.UtcNow, zone).Date, zone);

    /// <summary>The last <paramref name="days"/> org-local days, oldest first,
    /// ending today.</summary>
    public static List<string> LastDays(int days, TimeZoneInfo zone, DateTime? nowUtc = null)
    {
        var today = ToOrg(nowUtc ?? DateTime.UtcNow, zone).Date;
        var outv = new List<string>();
        for (var i = Math.Max(1, days) - 1; i >= 0; i--)
            outv.Add(today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return outv;
    }
}
