using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A booking's video meeting: a calendar skill set to Zoom (one meeting per booking through
/// the Zoom connection), Google Meet (made with the Google event) or the skill's own link is
/// MADE with the booking at delivery and its link SENT to the customer in its own message
/// right after the reply that booked — never by the model. A held / rejected reply makes no
/// meeting, a cancelled booking deletes its Zoom meeting, and a meeting that can't be made
/// never stops the booking (the note tells a person to send a link).
/// </summary>
public class MeetingLinkTests
{
    private const string Phone = "60123000001";

    /// <summary>A shared Zoom Server-to-Server app (unique credentials: the token cache is per credentials).</summary>
    private static Integration ZoomRow(BookingRig r, int? entityId = null)
    {
        var row = new Integration
        {
            Provider = ZoomClient.Provider, EntityId = entityId, Status = "unknown",
            Config = new Dictionary<string, object?> { ["account_id"] = "acc-1", ["client_id"] = "client-1" },
            Credentials = new Dictionary<string, object?> { ["client_secret"] = $"secret-{Guid.NewGuid():N}" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Integrations.Add(row);
        r.Db.SaveChanges();
        return row;
    }

    /// <summary>What went out to the customer (no notes, no inbound).</summary>
    private static List<Message> Sent(BookingRig r) =>
        r.Messages(Phone).Where(m => !m.Private && m.Direction == MessageDirection.Out).ToList();

    private static string LinkMessage(Booking b) =>
        $"Here's the {MeetingKinds.Label(b.MeetingProvider)} link: {b.MeetingUrl}";

    [Fact]
    public void A_zoom_booking_gets_its_own_meeting_and_the_link_follows_the_confirmation()
    {
        using var r = new BookingRig();
        r.Contact();
        var zoom = ZoomRow(r);
        r.Skill(new { source = "chatbranch", meeting = "zoom" });

        r.Say(Phone, "Can I book a consultation this week?");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal(MeetingKinds.Zoom, booking.MeetingProvider);
        Assert.StartsWith("https://us06web.zoom.us/j/", booking.MeetingUrl);
        Assert.NotEmpty(booking.MeetingId);
        Assert.Equal(zoom.Id, booking.MeetingIntegrationId);
        Assert.NotNull(booking.MeetingSentAt);

        // the Server-to-Server token, then ONE scheduled meeting covering the booking
        var token = Assert.Single(r.Zoom.Tokens);
        Assert.Equal("POST", token.Method);
        Assert.Contains("grant_type=account_credentials", token.Query);
        Assert.Contains("account_id=acc-1", token.Query);
        Assert.StartsWith("Basic ", token.Auth);
        var create = Assert.Single(r.Zoom.Creates);
        Assert.Equal("Bearer zoom-token", create.Auth);
        var body = JsonDocument.Parse(create.Body).RootElement;
        Assert.Equal(2, body.GetProperty("type").GetInt32());
        Assert.Equal(30, body.GetProperty("duration").GetInt32());
        Assert.Equal(BookingAvailability.Utc(booking.StartUtc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            body.GetProperty("start_time").GetString());
        Assert.Equal("Asia/Kuala_Lumpur", body.GetProperty("timezone").GetString());
        Assert.False(body.GetProperty("settings").GetProperty("join_before_host").GetBoolean());

        // the confirmation (no link of the model's), then the link in its own message
        var sent = Sent(r);
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain("zoom.us", sent[0].Text);
        Assert.Equal(LinkMessage(booking), sent[1].Text);
        Assert.Equal(MessageSource.Ai, sent[1].Source);
        var note = Assert.Single(r.Messages(Phone), m => m.Private);
        Assert.Contains($"Zoom link: {booking.MeetingUrl}", note.Text);
        Assert.True(note.Id < sent[0].Id);

        // the model knew the link is sent for it — never to write one
        Assert.Contains("Every booking comes with a Zoom meeting", r.Llm.Seen[0].System);
        var booked = r.Llm.Seen.Last().Messages.Last(m => m.Role == "tool");
        Assert.Contains("never write a meeting link yourself", booked.Content);
        Assert.Contains("\"meeting\":\"Zoom\"", booked.Content);
        // the connection's health is on its row
        Assert.Equal("connected", r.T.Fresh().Integrations.Single(i => i.Id == zoom.Id).Status);
    }

    [Fact]
    public void A_google_meet_comes_with_the_google_event_and_is_sent_the_same_way()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        r.Skill(new { source = "google", calendar = "shared", meeting = "google_meet" });

        r.Say(Phone, "Book me a call tomorrow please");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal(MeetingKinds.GoogleMeet, booking.MeetingProvider);
        Assert.Equal(r.Google.MeetLink, booking.MeetingUrl);
        var insert = Assert.Single(r.Google.Inserts);
        Assert.Contains("conferenceDataVersion=1", insert.Query);
        Assert.Contains("sendUpdates=all", insert.Query);
        var ev = JsonDocument.Parse(insert.Body).RootElement;
        Assert.Equal("hangoutsMeet", ev.GetProperty("conferenceData").GetProperty("createRequest")
            .GetProperty("conferenceSolutionKey").GetProperty("type").GetString());
        Assert.Empty(r.Zoom.Calls);
        Assert.Equal(LinkMessage(booking), Sent(r).Last().Text);
    }

    [Fact]
    public void The_skills_own_link_rides_on_the_google_event_and_goes_out_in_its_own_words()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        const string room = "https://us02web.zoom.us/j/1234567890?pwd=room";
        r.Skill(new
        {
            source = "google", calendar = "shared", meeting = "link", meeting_link = room,
            meeting_message = "Join us here: {{link}} ({{time}}) — see you, {{name}}!",
        });

        r.Say(Phone, "Can I book an appointment?");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(MeetingKinds.Link, booking.MeetingProvider);
        Assert.Equal(room, booking.MeetingUrl);
        var ev = JsonDocument.Parse(Assert.Single(r.Google.Inserts).Body).RootElement;
        Assert.Equal(room, ev.GetProperty("location").GetString());
        Assert.DoesNotContain("conferenceDataVersion", Assert.Single(r.Google.Inserts).Query);
        Assert.Equal($"Join us here: {room} ({BookingService.LabelOf(booking)}) — see you, Aisha Rahman!", Sent(r).Last().Text);
        Assert.Empty(r.Zoom.Calls);
    }

    [Fact]
    public void The_default_message_follows_the_contacts_language()
    {
        var b = new Booking
        {
            MeetingUrl = "https://meet.example.com/room", Timezone = "Asia/Kuala_Lumpur",
            StartUtc = BookingRig.Kul(13, 10), EndUtc = BookingRig.Kul(13, 10, 30),
        };
        var cfg = new CalendarAppConfig();
        // the reply right before it already said the time — the link message doesn't repeat it
        Assert.Equal("Ini pautan mesyuarat: https://meet.example.com/room",
            BookingService.MeetingMessage(b, cfg, new Contact { Language = "ms" }));
        Assert.Equal("这是会议链接：https://meet.example.com/room",
            BookingService.MeetingMessage(b, cfg, new Contact { Language = "zh" }));
        Assert.Equal("Here's the meeting link: https://meet.example.com/room", BookingService.MeetingMessage(b, cfg, null));
        b.MeetingProvider = MeetingKinds.GoogleMeet;
        Assert.Equal("Here's the Google Meet link: https://meet.example.com/room", BookingService.MeetingMessage(b, cfg, null));
    }

    [Fact]
    public void Without_a_zoom_connection_the_booking_stands_and_a_person_is_told_to_send_a_link()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch", meeting = "zoom" });

        r.Say(Phone, "Can I book a consultation this week?");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal("", booking.MeetingUrl);
        Assert.Null(booking.MeetingSentAt);
        Assert.Single(Sent(r));   // the confirmation only — no link message
        var note = Assert.Single(r.Messages(Phone), m => m.Private);
        Assert.Contains("No meeting link: Zoom is not connected", note.Text);
        Assert.Contains("Send the customer a link yourself", note.Text);
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "booking.created" && a.Detail.Contains("Zoom is not connected"));
        Assert.Empty(r.Zoom.Calls);
    }

    [Fact]
    public void A_zoom_sign_in_failure_never_stops_the_booking()
    {
        using var r = new BookingRig();
        r.Contact();
        var zoom = ZoomRow(r);
        r.Zoom.FailToken = HttpStatusCode.BadRequest;
        r.Skill(new { source = "chatbranch", meeting = "zoom" });

        r.Say(Phone, "Can I book a consultation this week?");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal("", booking.MeetingUrl);
        Assert.Single(Sent(r));
        Assert.Contains("the Zoom meeting could not be made", Assert.Single(r.Messages(Phone), m => m.Private).Text);
        Assert.Equal("error", r.T.Fresh().Integrations.Single(i => i.Id == zoom.Id).Status);
    }

    [Fact]
    public void A_held_reply_makes_no_meeting_until_it_is_approved()
    {
        using var r = new BookingRig();
        r.Contact();
        ZoomRow(r);
        r.Skill(new { source = "chatbranch", meeting = "zoom" });
        r.Set("ai_review_mode", "review_all");

        r.Say(Phone, "Can I book a consultation this week?");
        var draft = Assert.Single(r.Drafts());
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.Equal(BookingStatus.Pending, Assert.Single(r.FreshBookings()).Status);
        Assert.Empty(r.Zoom.Calls);   // a held reply never made a meeting
        Assert.Empty(Sent(r));

        Assert.Equal(AiGateStatus.Sent, r.Gate.Approve(draft.Id, "admin").Status);
        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Single(r.Zoom.Creates);
        var sent = Sent(r);
        Assert.Equal(2, sent.Count);
        Assert.Equal(LinkMessage(booking), sent[1].Text);

        // delivering the same draft's extras again never sends the link twice
        r.Gate.DeliverExtras(draft.Id, r.ThreadOf(Phone), null, "sent");
        Assert.Equal(2, Sent(r).Count);
    }

    [Fact]
    public void A_rejected_reply_never_touches_zoom()
    {
        using var r = new BookingRig();
        r.Contact();
        ZoomRow(r);
        r.Skill(new { source = "chatbranch", meeting = "zoom" });
        r.Set("ai_review_mode", "review_all");

        r.Say(Phone, "Can I book a consultation this week?");
        r.Gate.Reject(Assert.Single(r.Drafts()).Id, "admin", "not now");

        Assert.Equal(BookingStatus.Released, Assert.Single(r.FreshBookings()).Status);
        Assert.Empty(r.Zoom.Calls);
    }

    [Fact]
    public async Task Cancelling_a_booking_deletes_its_zoom_meeting()
    {
        using var r = new BookingRig();
        r.Contact();
        ZoomRow(r);
        r.Skill(new { source = "chatbranch", meeting = "zoom" });
        r.Say(Phone, "Can I book a consultation this week?");
        var booking = r.Db.Bookings.Single();
        var meetingId = booking.MeetingId;

        Assert.Null(await r.Bookings.OperatorCancelAsync(booking, "admin"));

        var del = Assert.Single(r.Zoom.Deletes);
        Assert.Equal($"/v2/meetings/{meetingId}", del.Path);
        Assert.Equal(BookingStatus.Cancelled, r.FreshBookings().Single().Status);
        Assert.DoesNotContain(r.Messages(Phone), m => m.Private && m.Text.Contains("could not be deleted"));
    }

    [Fact]
    public void A_google_event_that_cannot_be_made_takes_its_zoom_meeting_with_it()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        ZoomRow(r);
        r.Skill(new { source = "google", calendar = "shared", meeting = "zoom" });
        r.Google.FailInsert = HttpStatusCode.Forbidden;

        r.Say(Phone, "Can I book a consultation this week?");

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Failed, booking.Status);
        Assert.Single(r.Zoom.Creates);
        // the meeting made a moment before is deleted again — no orphan room in Zoom
        Assert.StartsWith("/v2/meetings/85", Assert.Single(r.Zoom.Deletes).Path);
        Assert.Equal("", booking.MeetingUrl);
        Assert.Empty(Sent(r));   // the reply was blocked — and no link went out
    }

    [Fact]
    public async Task My_bookings_gives_the_real_link_back_even_without_the_cancel_action()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new
        {
            source = "chatbranch", meeting = "link", meeting_link = "https://meet.example.com/room",
            actions = new[] { "availability", "book" },
        });
        var cfg = CalendarAppConfig.From(skill.Config);
        var tools = BookingService.ToolDefinitions("calendar", cfg, "Asia/Kuala_Lumpur").Select(t => t.Name).ToList();
        Assert.Contains("calendar_my_bookings", tools);
        Assert.DoesNotContain("calendar_cancel", tools);
        // without a meeting nothing changes: no cancel ⇒ no my_bookings
        Assert.DoesNotContain("calendar_my_bookings", BookingService.ToolDefinitions("calendar",
            CalendarAppConfig.From(JsonSerializer.SerializeToElement(new { actions = new[] { "availability", "book" } }), out _),
            "Asia/Kuala_Lumpur").Select(t => t.Name));

        var key = r.Bookings.ResolveTarget(skill, cfg, null, null).Key;
        r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id, SkillId = skill.SkillId, CalendarKey = key, Status = BookingStatus.Booked,
            StartUtc = BookingRig.Kul(14, 15), EndUtc = BookingRig.Kul(14, 15, 30), Timezone = "Asia/Kuala_Lumpur",
            MeetingProvider = MeetingKinds.Link, MeetingUrl = "https://meet.example.com/room",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();
        var binding = new BookingService.ToolBinding(skill, cfg, "my_bookings", "calendar");
        var outcome = await r.Bookings.ExecuteToolAsync(binding, "{}", new SkillContext { ContactId = c.Id });
        var listed = JsonDocument.Parse(outcome.Json).RootElement.GetProperty("bookings")[0];
        Assert.Equal("https://meet.example.com/room", listed.GetProperty("meeting_link").GetString());
    }

    [Theory]
    [InlineData("{\"meeting\":\"link\"}", "meeting_link is needed")]
    [InlineData("{\"meeting\":\"link\",\"meeting_link\":\"http://zoom.us/j/1\"}", "full https:// link")]
    [InlineData("{\"meeting\":\"link\",\"meeting_link\":\"zoom.us/j/1\"}", "full https:// link")]
    [InlineData("{\"meeting\":\"google_meet\",\"source\":\"chatbranch\"}", "Google Meet links come with a Google Calendar")]
    [InlineData("{\"meeting\":\"teams\"}", "meeting must be")]
    [InlineData("{\"meeting\":\"zoom\",\"meeting_message\":\"Your link is coming\"}", "must contain {{link}}")]
    public void Meeting_settings_are_validated(string json, string error)
    {
        CalendarAppConfig.From(JsonDocument.Parse(json).RootElement, out var problem);
        Assert.NotNull(problem);
        Assert.Contains(error, problem);
    }

    [Fact]
    public void A_config_without_a_meeting_behaves_as_before()
    {
        var cfg = CalendarAppConfig.From(JsonDocument.Parse("{\"source\":\"google\",\"meeting\":\"google_meet\"}").RootElement,
            out var problem);
        Assert.Null(problem);
        Assert.Equal(MeetingKinds.GoogleMeet, cfg.Meeting);
        var old = CalendarAppConfig.From(JsonDocument.Parse("{\"source\":\"chatbranch\"}").RootElement, out problem);
        Assert.Null(problem);
        Assert.Equal("", old.Meeting);
        Assert.Equal("", old.ToDictionary()["meeting"]);
    }
}

/// <summary>Zoom on the Integrations page and in the skill wizard's catalog (HTTP level).</summary>
public class ZoomIntegrationApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ZoomIntegrationApiTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task A_zoom_app_is_added_with_its_secret_kept_and_the_skill_catalog_sees_it()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var providers = await su.GetFromJsonAsync<JsonElement>("/api/integrations/providers");
        var zoom = providers.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "zoom");
        Assert.Equal("apikey", zoom.GetProperty("kind").GetString());
        Assert.Equal(new[] { "account_id", "client_id" },
            zoom.GetProperty("config_fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToArray());
        Assert.Equal("client_secret", zoom.GetProperty("secret_fields")[0].GetProperty("key").GetString());

        var secret = $"zoom-secret-{Guid.NewGuid():N}";
        var made = await su.PostAsJsonAsync("/api/integrations/accounts", new
        {
            provider = "zoom", name = "Clinic Zoom",
            config = new { account_id = "acc-9", client_id = "cid-9" }, client_secret = secret,
        });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var text = await made.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, text);   // never echoed
        var row = JsonDocument.Parse(text).RootElement;
        Assert.True(row.GetProperty("connected").GetBoolean());
        var id = row.GetProperty("id").GetInt32();
        var stored = Platform.WithDb(_f, db => db.Integrations.Single(i => i.Id == id));
        Assert.Equal(secret, stored.Credentials["client_secret"]?.ToString());
        Assert.Equal("acc-9", stored.Config["account_id"]?.ToString());

        var apps = await su.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps");
        var calendar = apps.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("app").GetString() == "calendar");
        var zoomChoice = calendar.GetProperty("meetings").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "zoom");
        Assert.True(zoomChoice.GetProperty("connected").GetBoolean());
        Assert.Equal("", calendar.GetProperty("defaults").GetProperty("meeting").GetString());

        Platform.WithDb(_f, db =>
        {
            db.Integrations.Remove(db.Integrations.Single(i => i.Id == id));
            db.SaveChanges();
            return 0;
        });
    }
}
