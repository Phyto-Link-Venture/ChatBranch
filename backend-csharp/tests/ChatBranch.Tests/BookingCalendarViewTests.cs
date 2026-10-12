using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>The API host with a fake Google Calendar behind every outbound HTTP call.</summary>
public class CalendarViewFactory : ApiFactory
{
    internal FakeGoogleCalendar Google { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IHttpClientFactory>(Google));
    }
}

/// <summary>
/// The Bookings page's Calendar tab: the connected Google Calendar's own events, read-only —
/// which calendars a person may read (their own personal grant, the shared ones with
/// integrations.view, never someone else's personal one), the events as Google gives them
/// with ChatBranch's bookings linked inside the caller's scope, and the guards (range,
/// calendar id, Google failures).
/// </summary>
public class BookingCalendarViewTests : IClassFixture<CalendarViewFactory>
{
    private readonly CalendarViewFactory _f;
    private static readonly object Gate = new();
    private static Ids? _ids;

    private sealed record Ids(int Org, int East, int AdminOwn, int AgentOwn, int EastEntity, int WestEntity);

    public BookingCalendarViewTests(CalendarViewFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        Arrange();
    }

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (_ids != null && db.Integrations.Any(i => i.Id == _ids.Org)) return;
            var now = DateTime.UtcNow;
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var east = new Entity { TenantId = tenant.Id, Name = "Cal East", Slug = "cal-east", CreatedAt = now, UpdatedAt = now };
            var west = new Entity { TenantId = tenant.Id, Name = "Cal West", Slug = "cal-west", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(east, west);
            db.SaveChanges();
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities.Add(east);
            var adminId = db.Users.First(u => u.Username == "admin").Id;
            var agentId = db.Users.First(u => u.Username == "agent").Id;
            Integration Row(int? owner, int? entity) => new()
            {
                Provider = "google_calendar", OwnerUserId = owner, EntityId = entity, Status = "healthy",
                Credentials = new Dictionary<string, object?> { ["access_token"] = "ya29.cal" },
                CreatedAt = now, UpdatedAt = now,
            };
            var org = Row(null, null);
            var eastRow = Row(null, east.Id);
            var adminOwn = Row(adminId, null);
            var agentOwn = Row(agentId, null);
            db.Integrations.AddRange(org, eastRow, adminOwn, agentOwn);
            db.SaveChanges();

            // ChatBranch bookings made into the org calendar (east, in the agent's scope) and into the
            // agent's own calendar for a WEST conversation (outside the agent's scope)
            var eve = new Contact { Name = "Eve Calendar", Phone = "60177000001", CreatedAt = now, UpdatedAt = now };
            var walt = new Contact { Name = "Walt Calendar", Phone = "60177000002", CreatedAt = now, UpdatedAt = now };
            db.Contacts.AddRange(eve, walt);
            db.SaveChanges();
            var t1 = new Thread { Channel = "whatsapp", Key = "60177000001", Name = "Eve", ContactId = eve.Id,
                EntityId = east.Id, Status = ThreadStatus.Open, CreatedAt = now, UpdatedAt = now };
            db.Threads.Add(t1);
            db.SaveChanges();
            Booking Made(Contact c, Thread? t, int? entity, Integration row, string eventId) => new()
            {
                ContactId = c.Id, ThreadId = t?.Id, EntityId = entity, SkillId = "cal-view", Source = BookingSources.Google,
                IntegrationId = row.Id, CalendarKey = $"google:{row.Id}:primary", ExternalEventId = eventId,
                CalendarLabel = "Google Calendar", Title = $"{c.Name} — appointment", Status = BookingStatus.Booked,
                StartUtc = now.AddDays(1), EndUtc = now.AddDays(1).AddMinutes(30), Timezone = "Asia/Kuala_Lumpur",
                CreatedBy = "ai", CreatedAt = now, UpdatedAt = now,
            };
            db.Bookings.AddRange(Made(eve, t1, east.Id, org, "evt-ours"), Made(walt, null, west.Id, agentOwn, "evt-west"));
            db.SaveChanges();
            _ids = new Ids(org.Id, eastRow.Id, adminOwn.Id, agentOwn.Id, east.Id, west.Id);

            _f.Google.CalendarItems.AddRange(new object[]
            {
                new { id = "team@group.calendar.google.com", summary = "Team", accessRole = "writer", timeZone = "Asia/Kuala_Lumpur" },
                new { id = "owner@example.com", summary = "owner@example.com", primary = true, accessRole = "owner", timeZone = "Asia/Kuala_Lumpur" },
                new { id = "holidays@group.v.calendar.google.com", summary = "Holidays", accessRole = "freeBusyReader" },
            });
            _f.Google.EventItems.AddRange(new object[]
            {
                new { id = "evt-ours", status = "confirmed", summary = "Eve Calendar — appointment",
                      start = new { dateTime = "2026-10-13T14:00:00+08:00" }, end = new { dateTime = "2026-10-13T14:30:00+08:00" },
                      htmlLink = "https://calendar.google.com/event?eid=ours" },
                new { id = "evt-west", status = "confirmed", summary = "Walt Calendar — appointment",
                      start = new { dateTime = "2026-10-13T06:00:00Z" }, end = new { dateTime = "2026-10-13T06:30:00Z" } },
                new { id = "evt-offsite", status = "confirmed", summary = "Team offsite", location = "Penang",
                      start = new { date = "2026-10-14" }, end = new { date = "2026-10-16" } },
                new { id = "evt-focus", status = "confirmed", summary = "Focus time", transparency = "transparent",
                      start = new { dateTime = "2026-10-15T09:00:00+08:00" }, end = new { dateTime = "2026-10-15T11:00:00+08:00" } },
                new { id = "evt-gone", status = "cancelled", start = new { dateTime = "2026-10-15T12:00:00+08:00" },
                      end = new { dateTime = "2026-10-15T13:00:00+08:00" } },
            });
        }
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static List<int> Ids0(JsonElement calendars) =>
        calendars.GetProperty("calendars").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task You_see_your_own_calendar_and_shared_ones_never_someone_elses_personal_one()
    {
        var ids = _ids!;
        // an org-wide admin (integrations.view): the shared calendars first, then their own
        var admin = await Authed("admin", "admin123");
        var adminSees = Ids0(await admin.GetFromJsonAsync<JsonElement>("/api/bookings/calendars"));
        Assert.Equal(new[] { ids.East, ids.Org, ids.AdminOwn }, adminSees);
        Assert.DoesNotContain(ids.AgentOwn, adminSees);

        // an agent (no integrations.view): only their own personal calendar
        var agent = await Authed("agent", "agent123");
        var agentList = await agent.GetFromJsonAsync<JsonElement>("/api/bookings/calendars");
        Assert.Equal(new[] { ids.AgentOwn }, Ids0(agentList));
        Assert.Equal("personal", agentList.GetProperty("calendars")[0].GetProperty("scope").GetString());

        // and can't read anyone else's by id
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.GetAsync($"/api/bookings/calendar-events?connection={ids.Org}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/bookings/calendar-events?connection={ids.AgentOwn}")).StatusCode);

        // someone with no calendar at all is told so
        SettingsTestKit.CreateUser(_f, "cal-nobody", "pass12345", Roles.Agent);
        var nobody = await Authed("cal-nobody", "pass12345");
        var none = await nobody.GetFromJsonAsync<JsonElement>("/api/bookings/calendar-events");
        Assert.False(none.GetProperty("ok").GetBoolean());
        Assert.Equal("no_calendar", none.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Events_come_from_google_with_chatbranch_bookings_linked()
    {
        var ids = _ids!;
        var admin = await Authed("admin", "admin123");
        var before = _f.Google.EventReads.Count();
        var j = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/bookings/calendar-events?connection={ids.Org}&from=2026-10-12&to=2026-10-18");
        Assert.True(j.GetProperty("ok").GetBoolean(), j.ToString());
        Assert.Equal("Asia/Kuala_Lumpur", j.GetProperty("timezone").GetString());
        Assert.Equal("owner@example.com", j.GetProperty("calendar_id").GetString());   // "primary" resolved
        // the picker lists what the account can read — never a free/busy-only calendar
        var cals = j.GetProperty("calendars").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "owner@example.com", "team@group.calendar.google.com" }, cals);

        // the request: one week, recurring events expanded, only the fields shown
        var read = _f.Google.EventReads.Skip(before).Single();
        Assert.Contains("/calendars/primary/events", read.Path);
        Assert.Contains("singleEvents=true", read.Query);
        Assert.Contains("timeMin=2026-10-11T16%3A00%3A00", read.Query);   // 12 Oct 00:00 in KL
        Assert.Contains("timeMax=2026-10-18T16%3A00%3A00", read.Query);   // 19 Oct 00:00 in KL
        Assert.DoesNotContain("description", Uri.UnescapeDataString(read.Query));
        Assert.Equal("Bearer ya29.cal", read.Auth);

        var events = j.GetProperty("events").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(4, events.Count);                                    // the cancelled one is left out
        var ours = events["evt-ours"];
        Assert.Equal("2026-10-13T14:00:00+08:00", ours.GetProperty("start").GetString());
        Assert.Equal("2026-10-13", ours.GetProperty("day").GetString());
        Assert.True(ours.GetProperty("chatbranch").GetBoolean());
        Assert.Equal("Eve Calendar", ours.GetProperty("booking").GetProperty("contact_name").GetString());
        Assert.Equal("whatsapp:60177000001", ours.GetProperty("booking").GetProperty("thread").GetString());
        Assert.Equal("https://calendar.google.com/event?eid=ours", ours.GetProperty("link").GetString());

        var offsite = events["evt-offsite"];
        Assert.True(offsite.GetProperty("all_day").GetBoolean());
        Assert.Equal("2026-10-14", offsite.GetProperty("start").GetString());
        Assert.Equal("2026-10-16", offsite.GetProperty("end").GetString());   // Google's exclusive end
        Assert.Equal("Penang", offsite.GetProperty("location").GetString());
        Assert.False(offsite.GetProperty("chatbranch").GetBoolean());
        Assert.True(events["evt-focus"].GetProperty("free").GetBoolean());
        // a UTC time is shown in the calendar's zone, on its local day
        Assert.Equal("2026-10-13T14:00:00+08:00", events["evt-west"].GetProperty("start").GetString());

        // another of the account's calendars, by id
        var team = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/bookings/calendar-events?connection={ids.Org}&calendar=team@group.calendar.google.com&from=2026-10-12");
        Assert.True(team.GetProperty("ok").GetBoolean());
        Assert.Contains("/calendars/team%40group.calendar.google.com/events", _f.Google.EventReads.Last().Path
            .Replace("@", "%40"));
    }

    [Fact]
    public async Task A_booking_outside_your_scope_shows_as_chatbranchs_without_its_details()
    {
        var agent = await Authed("agent", "agent123");   // bound to Cal East; the booking is West's
        var j = await agent.GetFromJsonAsync<JsonElement>("/api/bookings/calendar-events?from=2026-10-12&to=2026-10-18");
        Assert.True(j.GetProperty("ok").GetBoolean(), j.ToString());
        Assert.Equal("personal", j.GetProperty("connection").GetProperty("scope").GetString());
        var west = j.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "evt-west");
        Assert.True(west.GetProperty("chatbranch").GetBoolean());
        Assert.Equal(JsonValueKind.Null, west.GetProperty("booking").ValueKind);
    }

    [Fact]
    public async Task Bad_ranges_unknown_calendars_and_google_failures_are_refused_plainly()
    {
        var ids = _ids!;
        var admin = await Authed("admin", "admin123");
        async Task<HttpStatusCode> Status(string q) =>
            (await admin.GetAsync($"/api/bookings/calendar-events?connection={ids.Org}&{q}")).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Status("from=13-10-2026"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status("from=2026-10-18&to=2026-10-12"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status("from=2026-10-01&to=2026-12-31"));
        // only the account's own calendars, never an arbitrary id
        Assert.Equal(HttpStatusCode.NotFound, await Status("calendar=someone.else@example.com&from=2026-10-12"));

        _f.Google.FailEvents = HttpStatusCode.Forbidden;
        try
        {
            var j = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/bookings/calendar-events?connection={ids.East}&from=2026-10-12");
            Assert.False(j.GetProperty("ok").GetBoolean());
            Assert.Equal("google", j.GetProperty("error").GetString());
            Assert.Contains("events refused", j.GetProperty("detail").GetString());
        }
        finally
        {
            _f.Google.FailEvents = null;
        }
    }

    [Fact]
    public void Google_payloads_parse_into_events_and_calendars()
    {
        var (events, tz, next) = GoogleCalendarClient.ParseEvents("""
            {"timeZone":"Asia/Kuala_Lumpur","nextPageToken":"p2","items":[
              {"id":"a","status":"confirmed","start":{"dateTime":"2026-10-13T14:00:00+08:00"},"end":{"dateTime":"2026-10-13T15:00:00+08:00"}},
              {"id":"b","status":"cancelled","start":{"dateTime":"2026-10-13T16:00:00+08:00"},"end":{"dateTime":"2026-10-13T17:00:00+08:00"}},
              {"id":"c","summary":"Holiday","start":{"date":"2026-10-20"},"end":{"date":"2026-10-21"},"transparency":"transparent"},
              {"id":"","start":{"date":"2026-10-20"},"end":{"date":"2026-10-21"}},
              {"id":"d","start":{}}
            ]}
            """);
        Assert.Equal("Asia/Kuala_Lumpur", tz);
        Assert.Equal("p2", next);
        Assert.Equal(new[] { "a", "c" }, events.Select(e => e.Id));
        Assert.Equal(new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc), events[0].StartUtc);
        Assert.Equal("", events[0].Title);
        Assert.True(events[1].AllDay);
        Assert.Equal(new DateOnly(2026, 10, 20), events[1].StartDate);
        Assert.True(events[1].Free);
        Assert.Empty(GoogleCalendarClient.ParseEvents("not json").Events);

        var cals = GoogleCalendarClient.ParseCalendars("""
            {"items":[{"id":"x","summary":"Zeta","accessRole":"reader"},
                      {"id":"me","summary":"me@x.com","summaryOverride":"My calendar","primary":true,"accessRole":"owner"},
                      {"id":"fb","summary":"Busy only","accessRole":"freeBusyReader"}]}
            """);
        Assert.Equal(new[] { "me", "x" }, cals.Select(c => c.Id));
        Assert.Equal("My calendar", cals[0].Summary);
    }
}
