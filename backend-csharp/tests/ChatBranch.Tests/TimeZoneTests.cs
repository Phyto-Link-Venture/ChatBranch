using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Times: every timestamp the API writes is UTC and says so ("Z") — a notification
/// sent at 1:30 PM in Kuala Lumpur no longer reads 5:30 AM in the browser — and
/// Settings → Time zone: the organization's business time zone (Setting
/// org_timezone, with the org defaults, settings.appearance.org) and each person's
/// "show times in" choice on their own preferences.
/// </summary>
public class TimeZoneTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public TimeZoneTests(ApiFactory f) => _f = f;

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");
    private Task<HttpClient> Agent() => SettingsTestKit.Client(_f, "agent", "agent123");

    [Fact]
    public void Unspecified_and_local_times_are_written_as_UTC()
    {
        var o = new JsonSerializerOptions();
        o.Converters.Add(new UtcDateTimeConverter());
        var stored = new DateTime(2026, 10, 12, 5, 30, 0, DateTimeKind.Unspecified); // as the database hands it back
        Assert.Equal("\"2026-10-12T05:30:00Z\"", JsonSerializer.Serialize(stored, o));
        Assert.Equal("\"2026-10-12T05:30:00Z\"", JsonSerializer.Serialize(DateTime.SpecifyKind(stored, DateTimeKind.Utc), o));
        Assert.Equal("{\"at\":\"2026-10-12T05:30:00Z\",\"none\":null}",
            JsonSerializer.Serialize(new { at = (DateTime?)stored, none = (DateTime?)null }, o));
        var local = new DateTime(2026, 10, 12, 13, 30, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(local, o), o));
    }

    [Fact]
    public async Task A_notification_time_says_it_is_UTC()
    {
        var admin = await Admin();
        var at = new DateTime(2026, 10, 12, 5, 30, 0);
        SettingsTestKit.WithDb(_f, db =>
        {
            db.Notifications.Add(new Notification
            {
                UserId = db.Users.First(u => u.Username == "admin").Id, Kind = "system", Category = "system",
                Title = "TZ check", CreatedAt = at,
            });
            db.SaveChanges();
        });
        var page = await admin.GetFromJsonAsync<JsonElement>("/api/notifications?page_size=100");
        var row = page.GetProperty("items").EnumerateArray().First(n => n.GetProperty("title").GetString() == "TZ check");
        Assert.Equal("2026-10-12T05:30:00Z", row.GetProperty("created_at").GetString());
    }

    [Fact]
    public async Task The_business_time_zone_is_set_with_the_org_defaults()
    {
        var admin = await Admin();
        var put = await admin.PutAsJsonAsync("/api/settings/appearance", new { time_zone = "Asia/Kuala_Lumpur" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Asia/Kuala_Lumpur", "setting"),
            (body.GetProperty("time_zone").GetString(), body.GetProperty("time_zone_source").GetString()));
        // the organization's clock follows (the AI, bookings, journeys, quiet hours)
        Assert.Equal(8, SettingsTestKit.WithDb(_f, db =>
            OrgClock.Zone(new ScopingService(db)).GetUtcOffset(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)).Hours));
        Assert.True(SettingsTestKit.WithDb(_f, db => db.AuditLogs.Any(a => a.Target == "time_zone"
            && a.Detail.Contains("business time zone set to Asia/Kuala_Lumpur"))));

        // everyone reads it — the shell shows times in it for those who pick it
        var agent = await Agent();
        var read = await agent.GetFromJsonAsync<JsonElement>("/api/settings/appearance");
        Assert.Equal("Asia/Kuala_Lumpur", read.GetProperty("time_zone").GetString());
        Assert.False(read.GetProperty("can_manage").GetBoolean());
        var prefs = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal("Asia/Kuala_Lumpur", prefs.GetProperty("org_time_zone").GetString());

        // only settings.appearance.org changes it; a zone the server doesn't know is refused
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/settings/appearance", new { time_zone = "Asia/Singapore" })).StatusCode);
        foreach (var bad in new object[] { "Mars/Olympus_Mons", "posix/Asia/Tokyo", 8 })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PutAsJsonAsync("/api/settings/appearance", new { time_zone = bad })).StatusCode);
        Assert.Equal("Asia/Kuala_Lumpur",
            (await admin.GetFromJsonAsync<JsonElement>("/api/settings/appearance")).GetProperty("time_zone").GetString());

        // cleared: back to the server's ORG_TIMEZONE / TZ, else its own clock
        var cleared = await (await admin.PutAsJsonAsync("/api/settings/appearance", new { time_zone = (string?)null }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("setting", cleared.GetProperty("time_zone_source").GetString());
        Assert.Equal("", SettingsTestKit.WithDb(_f, db => new ScopingService(db).SettingValue(OrgClock.SettingKey)));
    }

    [Fact]
    public async Task Each_person_picks_which_zone_times_are_shown_in()
    {
        var agent = await Agent();
        var prefs = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, prefs.GetProperty("time_zone").ValueKind);   // this device's own

        foreach (var (send, stored) in new[] { ("org", "org"), ("Europe/London", "Europe/London") })
        {
            var res = await agent.PutAsJsonAsync("/api/auth/preferences", new { time_zone = send });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(stored, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("time_zone").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PutAsJsonAsync("/api/auth/preferences", new { time_zone = "Nowhere/Special" })).StatusCode);

        // other preferences stay; "auto" / null = back to the device
        (await agent.PutAsJsonAsync("/api/auth/preferences", new { chats = new { sort = "customer_newest" } }))
            .EnsureSuccessStatusCode();
        prefs = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal("Europe/London", prefs.GetProperty("time_zone").GetString());
        (await agent.PutAsJsonAsync("/api/auth/preferences", new { time_zone = "auto" })).EnsureSuccessStatusCode();
        prefs = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, prefs.GetProperty("time_zone").ValueKind);
        Assert.Equal("customer_newest", prefs.GetProperty("chats").GetProperty("sort").GetString());
    }

    [Fact]
    public void Zone_ids_are_normalized_to_what_a_browser_understands()
    {
        Assert.Equal("Asia/Kuala_Lumpur", OrgClock.Normalize(" Asia/Kuala_Lumpur "));
        Assert.Equal("UTC", OrgClock.Normalize("UTC"));
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId("Pacific Standard Time", out _))   // needs ICU
            Assert.Equal("America/Los_Angeles", OrgClock.Normalize("Pacific Standard Time")); // a Windows id
        foreach (var bad in new[] { "", "Nope/Zone", "posix/Asia/Tokyo", "right/UTC", "Factory", "localtime" })
            Assert.Null(OrgClock.Normalize(bad));
    }
}
