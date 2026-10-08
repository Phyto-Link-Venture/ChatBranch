using System.Globalization;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The organization's wall clock. Flow statistics are bucketed per calendar
/// day, and "today" must be the operator's today — not UTC's, which flips at
/// 08:00 in Kuala Lumpur. The zone comes from the <c>timezone</c> Setting row
/// (an IANA id such as "Asia/Kuala_Lumpur"), then env <c>ORG_TIMEZONE</c>,
/// then the server's local zone — the same clock quiet hours already use.
/// </summary>
public static class OrgClock
{
    public const string SettingKey = "timezone";

    public static TimeZoneInfo Zone(ScopingService scoping)
    {
        var id = scoping.SettingValue(SettingKey);
        if (id.Length == 0) id = Environment.GetEnvironmentVariable("ORG_TIMEZONE") ?? "";
        if (id.Length > 0)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* unknown id — fall back to the server clock */ }
        }
        return TimeZoneInfo.Local;
    }

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
