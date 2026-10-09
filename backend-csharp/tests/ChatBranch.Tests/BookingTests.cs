using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// googleapis.com/calendar stand-in at the HTTP level, so the REAL
/// <see cref="GoogleCalendarClient"/> (URLs, bodies, error parsing) is what runs.
/// </summary>
internal sealed class FakeGoogleCalendar : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Method, string Path, string Query, string Body, string? Auth);

    public List<Call> Calls { get; } = new();
    /// <summary>Busy spans freeBusy reports (UTC).</summary>
    public List<(DateTime Start, DateTime End)> Busy { get; } = new();
    public HttpStatusCode? FailInsert { get; set; }
    public HttpStatusCode? FailFreeBusy { get; set; }
    private int _events;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public IEnumerable<Call> Inserts => Calls.Where(c => c.Method == "POST" && c.Path.EndsWith("/events"));
    public IEnumerable<Call> Deletes => Calls.Where(c => c.Method == "DELETE");
    public IEnumerable<Call> FreeBusyCalls => Calls.Where(c => c.Path.EndsWith("/freeBusy"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls)
            Calls.Add(new Call(request.Method.Method, path, request.RequestUri.Query, body,
                request.Headers.Authorization?.ToString()));
        if (path.EndsWith("/freeBusy"))
        {
            if (FailFreeBusy is HttpStatusCode fs) return Json(fs, new { error = new { message = "freeBusy refused" } });
            var id = JsonDocument.Parse(body).RootElement.GetProperty("items")[0].GetProperty("id").GetString()!;
            var busy = Busy.Select(b => new { start = b.Start.ToString("o"), end = b.End.ToString("o") }).ToArray();
            return Json(HttpStatusCode.OK, new Dictionary<string, object>
            {
                ["calendars"] = new Dictionary<string, object> { [id] = new { busy } },
            });
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/events"))
        {
            if (FailInsert is HttpStatusCode fi) return Json(fi, new { error = new { message = "Calendar says no" } });
            var n = Interlocked.Increment(ref _events);
            return Json(HttpStatusCode.OK, new { id = $"evt-{n}", htmlLink = $"https://calendar.google.com/event?eid={n}" });
        }
        if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
}

/// <summary>A model that runs a script per round: (messages, tools) → the round's answer.</summary>
internal sealed class ToolScriptLlm : ILlmClient
{
    public Func<IReadOnlyList<LlmMessage>, IReadOnlyList<LlmTool>?, LlmChatResult> Script { get; set; }
    public List<(string System, List<LlmMessage> Messages, List<string> Tools)> Seen { get; } = new();
    public bool IsConfigured => true;

    public ToolScriptLlm(Func<IReadOnlyList<LlmMessage>, IReadOnlyList<LlmTool>?, LlmChatResult> script) =>
        Script = script;

    public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default) => Task.FromResult(new LlmResult("ok", 1, 1, "test", "script"));

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        Seen.Add((system, messages.ToList(), (tools ?? Array.Empty<LlmTool>()).Select(t => t.Name).ToList()));
        return Task.FromResult(Script(messages, tools));
    }

    public static LlmChatResult Calls(params (string Name, string Args)[] calls) =>
        new("", calls.Select((c, i) => new LlmToolCall($"call-{Guid.NewGuid():N}-{i}", c.Name, c.Args)).ToList(),
            10, 2, "test", "script");

    public static LlmChatResult Says(string text) => new(text, Array.Empty<LlmToolCall>(), 10, 4, "test", "script");

    /// <summary>The usual customer journey in one reply: availability → book the
    /// first offered slot → confirm it in words.</summary>
    public static LlmChatResult BookFirstSlot(IReadOnlyList<LlmMessage> msgs, IReadOnlyList<LlmTool>? tools)
    {
        var last = msgs.LastOrDefault();
        if (last?.Role != "tool") return Calls(("calendar_availability", "{}"));
        var json = JsonDocument.Parse(last.Content!).RootElement;
        if (json.TryGetProperty("slots", out var slots) && slots.GetArrayLength() > 0)
            return Calls(("calendar_book", $"{{\"start\":\"{slots[0].GetProperty("start").GetString()}\",\"note\":\"Haircut\"}}"));
        if (json.TryGetProperty("status", out var st) && st.GetString() == "reserved")
            return Says($"You're booked for {json.GetProperty("label").GetString()} ({json.GetProperty("timezone").GetString()}).");
        return Says("Sorry — a teammate will confirm a time with you.");
    }
}

/// <summary>The AI-ops stack (AiRig's wiring) with a scripted tool-calling model,
/// a hand-moved clock and a fake Google Calendar behind the booking service.</summary>
internal sealed class BookingRig : IDisposable
{
    /// <summary>Monday 12 Oct 2026, 08:00 in Kuala Lumpur.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Kl = BuiltinSkills.ResolveTimeZone("Asia/Kuala_Lumpur")!;

    public TestDb T { get; } = new();
    public AppDbContext Db => T.Db;
    public ManualClock Clock { get; } = new(Start);
    public ToolScriptLlm Llm { get; } = new(ToolScriptLlm.BookFirstSlot);
    public FakeGoogleCalendar Google { get; } = new();
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public BookingService Bookings { get; }
    public SkillService Skills { get; }
    public AssistantService Assistant { get; }
    public AiGate Gate { get; }
    public AiResponder Responder { get; }
    public QueueService Queue { get; }
    public InboundProcessor Inbound { get; }

    public BookingRig()
    {
        Sc = new ScopingService(Db);
        Sc.SetSetting("org_timezone", "Asia/Kuala_Lumpur");
        Threads = new ThreadStore(Db, Sc);
        var sender = new OutboundSender(Db, Threads, new LiveWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        var calendar = new GoogleCalendarClient(Db, new GoogleOAuth(Db, Sc, Google), Google);
        Bookings = new BookingService(Db, Sc, new IntegrationResolver(Db, Sc), calendar, Clock);
        Skills = new SkillService(Db, Sc, Clock, Bookings);
        Assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), Llm, Skills);
        var composer = new AiComposer(Db, Assistant, Llm);
        Gate = new AiGate(Db, Sc, Threads, sender, Clock, new RecordingTyping(), Bookings);
        Responder = new AiResponder(Db, Sc, composer, Gate, Clock);
        var queue = Queue = new QueueService(Db, Sc, Threads, Assistant, sender, Responder, Gate, Clock);
        Inbound = new InboundProcessor(Db, Threads, Assistant, sender, new FlowEngine(Db, Sc, queue), queue, Sc,
            new AutomationService(Db, Sc), Responder);
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["ai_review_mode"] = "auto", ["ai_review_first_n"] = "0", ["ai_review_notify"] = "false",
                     ["ai_wait_seconds"] = "0", ["ai_typing"] = "false", ["ai_split"] = "off",
                     ["ai_ack_skip"] = "false", ["ai_followup"] = "false", ["ai_reply_mode"] = "instant",
                 })
            Sc.SetSetting(k, v);
    }

    public Contact Contact(string name = "Aisha Rahman", string phone = "60123000001", string email = "aisha@example.com")
    {
        var c = new Contact { Name = name, Phone = phone, Email = email, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        return c;
    }

    public AgentSkill Skill(object? config = null, string id = "cal-1", int? entityId = null, bool enabled = true)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(config ?? new { source = "chatbranch" }))!;
        var s = new AgentSkill
        {
            SkillId = id, Name = "Book appointments", Kind = SkillKinds.App, Enabled = enabled, EntityId = entityId,
            UseWhen = "The customer wants to book, reschedule or cancel an appointment",
            Instructions = "Offer two or three free times and book only after they pick one.",
            Config = CalendarAppConfig.From(JsonSerializer.SerializeToElement(dict), out _).ToDictionary(),
        };
        Db.AgentSkills.Add(s);
        Db.SaveChanges();
        return s;
    }

    public Integration GoogleRow(int? ownerUserId = null, int? entityId = null, string token = "ya29.test")
    {
        var row = new Integration
        {
            Provider = "google_calendar", OwnerUserId = ownerUserId, EntityId = entityId, Status = "healthy",
            Credentials = new Dictionary<string, object?> { ["access_token"] = token },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.Integrations.Add(row);
        Db.SaveChanges();
        return row;
    }

    public void Set(string key, string value) => Sc.SetSetting(key, value);

    public Message Say(string phone, string text) => Inbound.ProcessInbound("whatsapp", phone, text);

    public Thread ThreadOf(string phone) => Db.Threads.First(t => t.Channel == "whatsapp" && t.Key == phone);

    public List<Booking> FreshBookings() => T.Fresh().Bookings.OrderBy(b => b.Id).ToList();

    public List<Message> Messages(string phone)
    {
        var tid = ThreadOf(phone).Id;
        return T.Fresh().Messages.Where(m => m.ThreadId == tid).OrderBy(m => m.Id).ToList();
    }

    public List<AiDraft> Drafts() => T.Fresh().AiDrafts.OrderBy(d => d.Id).ToList();

    /// <summary>A KL wall-clock time → UTC.</summary>
    public static DateTime Kul(int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), Kl);

    public void Dispose() => T.Dispose();
}

/// <summary>
/// Calendar booking for the AI ("Connected app" skills): the availability math,
/// the Google free/busy + events calls through a fake, calendar resolution,
/// the reserve-at-generation → make-at-delivery lifecycle through the AI gate
/// (auto, review_all approve / reject, shadow, slot taken in between), the abuse
/// limits, and a google skill with no connection never pretending.
/// </summary>
public class BookingTests
{
    private static CalendarAppConfig Cfg(object raw)
    {
        var cfg = CalendarAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
        Assert.Null(error);
        return cfg;
    }

    // ---- availability math ------------------------------------------------------

    [Fact]
    public void Availability_applies_hours_windows_notice_buffer_and_busy_spans()
    {
        var cfg = Cfg(new
        {
            hours = new Dictionary<string, object>
            {
                ["mon"] = new[] { new[] { "09:00", "12:00" }, new[] { "14:00", "17:00" } },
                ["tue"] = new[] { new[] { "09:00", "10:00" } },
            },
            duration_minutes = 30, buffer_minutes = 15, min_notice_minutes = 120, max_days_ahead = 1,
        });
        // now = Mon 08:00 KL ⇒ nothing before 10:00; busy Mon 14:30–15:00 (+15 min buffer each side)
        var busy = new[] { (BookingRig.Kul(12, 14, 30), BookingRig.Kul(12, 15, 0)) };
        var free = BookingAvailability.FreeSlots(cfg, BookingRig.Kl, BookingRig.Start.UtcDateTime,
            new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 20), busy);

        var labels = free.Select(s => s.LocalStart.ToString("ddd HH:mm")).ToList();
        Assert.Equal(new[]
        {
            "Mon 10:00", "Mon 10:30", "Mon 11:00", "Mon 11:30",
            "Mon 15:30", "Mon 16:00", "Mon 16:30",
            "Tue 09:00", "Tue 09:30",
        }, labels);   // max_days_ahead 1 ⇒ nothing after Tuesday; Wed+ closed anyway
        Assert.All(free, s => Assert.Equal(TimeSpan.FromMinutes(30), s.EndUtc - s.StartUtc));
        Assert.Equal(BookingRig.Kul(12, 10), free[0].StartUtc);
    }

    [Fact]
    public void Slots_step_on_their_own_grid_and_spread_across_days()
    {
        var cfg = Cfg(new { duration_minutes = 60, slot_step_minutes = 30, min_notice_minutes = 0 });
        var free = BookingAvailability.FreeSlots(cfg, BookingRig.Kl, BookingRig.Start.UtcDateTime,
            new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 16), Array.Empty<(DateTime, DateTime)>());
        // 09:00 … 16:00 every 30 min (the last hour-long slot starts at 16:00) × 5 days
        Assert.Equal(15 * 5, free.Count);
        Assert.Contains(free, s => s.LocalStart.ToString("HH:mm") == "09:30");

        var offered = BookingAvailability.Spread(free);
        Assert.Equal(8, offered.Count);
        var perDay = offered.GroupBy(s => s.LocalStart.Date).ToList();
        Assert.True(perDay.Count >= 4, "a spread covers several days, not one morning");
        Assert.All(perDay, g => Assert.True(g.Count() <= 2));
        // within a day: a morning and an afternoon time (the middle of each half),
        // not 9:00 and 9:30, nor the day's first and last minute
        Assert.Equal(new[] { "10:30", "14:30" }, perDay[0].Select(s => s.LocalStart.ToString("HH:mm")).ToArray());

        var oneDay = BookingAvailability.Spread(free.Where(s => s.LocalStart.Day == 13).ToList());
        Assert.Equal(8, oneDay.Count);   // a single-day query gets the whole list
    }

    [Fact]
    public void Config_validation_names_the_problem()
    {
        string? Problem(object raw)
        {
            CalendarAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
            return error;
        }
        Assert.Null(Problem(new { }));
        Assert.Contains("HH:mm", Problem(new { hours = new { mon = new[] { new[] { "9am", "17:00" } } } }));
        Assert.Contains("overlap", Problem(new { hours = new { mon = new[] { new[] { "09:00", "12:00" }, new[] { "11:00", "13:00" } } } }));
        Assert.Contains("ends before", Problem(new { hours = new { tue = new[] { new[] { "17:00", "09:00" } } } }));
        Assert.Contains("duration_minutes", Problem(new { duration_minutes = 3 }));
        Assert.Contains("duration_minutes", Problem(new { duration_minutes = 500 }));
        Assert.Contains("max_days_ahead", Problem(new { max_days_ahead = 91 }));
        Assert.Contains("unknown action", Problem(new { actions = new[] { "book", "teleport" } }));
        Assert.Contains("source", Problem(new { source = "outlook" }));
        Assert.Contains("timezone", Problem(new { timezone = "Mars/Olympus" }));

        var d = CalendarAppConfig.Defaults();
        Assert.Equal(30, d["duration_minutes"]);
        Assert.Equal("chatbranch", d["source"]);
        var hours = (Dictionary<string, object?>)d["hours"]!;
        Assert.Single((System.Collections.IList)hours["mon"]!);
        Assert.Empty((System.Collections.IList)hours["sat"]!);
    }

    [Fact]
    public async Task Pending_and_booked_holds_block_their_slot_released_ones_do_not()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        var cfg = CalendarAppConfig.From(skill.Config);
        var target = r.Bookings.ResolveTarget(skill, cfg, null, null);
        Assert.Equal("chatbranch:global", target.Key);
        void Hold(int hour, string status) => r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id, SkillId = "x", CalendarKey = target.Key, Status = status, Timezone = "Asia/Kuala_Lumpur",
            StartUtc = BookingRig.Kul(12, hour), EndUtc = BookingRig.Kul(12, hour, 30),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Hold(9, BookingStatus.Pending);
        Hold(10, BookingStatus.Booked);
        Hold(11, BookingStatus.Released);
        Hold(12, BookingStatus.Cancelled);
        r.Db.SaveChanges();

        var day = new DateOnly(2026, 10, 12);
        var res = await r.Bookings.FreeSlotsAsync(cfg, target, BookingRig.Kl, day, day);
        var starts = res.Free.Select(s => s.LocalStart.ToString("HH:mm")).ToList();
        Assert.DoesNotContain("09:00", starts);
        Assert.DoesNotContain("10:00", starts);
        Assert.Contains("11:00", starts);
        Assert.Contains("12:00", starts);
    }

    // ---- calendar resolution ----------------------------------------------------------

    [Fact]
    public void Assignee_calendar_is_the_agents_own_grant_else_shared_and_shared_skips_the_user()
    {
        using var r = new BookingRig();
        var agent = new AppUser { Username = "nadia", Password = "x", IsActive = true };
        r.Db.Users.Add(agent);
        r.Db.SaveChanges();
        var org = r.GoogleRow();
        var personal = r.GoogleRow(ownerUserId: agent.Id);
        var skill = r.Skill(new { source = "google", calendar = "assignee" });
        var cfg = CalendarAppConfig.From(skill.Config);

        var mine = r.Bookings.ResolveTarget(skill, cfg, null, agent.Id);
        Assert.True(mine.Ok);
        Assert.Equal(personal.Id, mine.Row!.Id);
        Assert.Equal($"google:{personal.Id}:primary", mine.Key);
        Assert.Equal("Google Calendar · nadia", mine.Label);

        var unassigned = r.Bookings.ResolveTarget(skill, cfg, null, null);
        Assert.Equal(org.Id, unassigned.Row!.Id);

        var shared = r.Bookings.ResolveTarget(skill, CalendarAppConfig.From(r.Skill(
            new { source = "google", calendar = "shared", calendar_id = "team@group.calendar.google.com" }, id: "cal-2").Config),
            null, agent.Id);
        Assert.Equal(org.Id, shared.Row!.Id);
        Assert.Equal($"google:{org.Id}:team@group.calendar.google.com", shared.Key);

        var pinned = r.Bookings.ResolveTarget(skill, CalendarAppConfig.From(r.Skill(
            new { source = "google", calendar = personal.Id.ToString() }, id: "cal-3").Config), null, null);
        Assert.Equal(personal.Id, pinned.Row!.Id);
    }

    // ---- end to end through the AI gate ------------------------------------------------

    [Fact]
    public void Auto_mode_books_on_delivery_through_google_calendar()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        r.Skill(new { source = "google", calendar = "shared" });
        r.Google.Busy.Add((BookingRig.Kul(12, 10), BookingRig.Kul(12, 11)));   // Mon 10–11 taken in Google

        r.Say("60123000001", "Can I book a haircut tomorrow or later this week?");

        // the model saw the tools and the how-to, then booked the first offered slot
        var first = r.Llm.Seen[0];
        Assert.Contains("calendar_availability", first.Tools);
        Assert.Contains("calendar_book", first.Tools);
        Assert.Contains("calendar_cancel", first.Tools);
        Assert.Contains("calendar_my_bookings", first.Tools);
        Assert.Contains("wait until the customer picks ONE specific time", first.System);
        Assert.Contains("Current date and time: Monday, 12 October 2026, 08:00", first.System);

        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        // the first OFFERED slot: on Monday, clear of the hour Google says is busy
        var bookedAt = BookingAvailability.Utc(booking.StartUtc);
        Assert.True(bookedAt >= BookingRig.Kul(12, 11), $"booked {bookedAt:o} overlaps the busy 10–11");
        Assert.True(bookedAt < BookingRig.Kul(13, 0), $"booked {bookedAt:o} is not on Monday");
        Assert.Equal("evt-1", booking.ExternalEventId);
        Assert.StartsWith("https://calendar.google.com/", booking.EventLink);
        Assert.Equal("Aisha Rahman — appointment", booking.Title);
        Assert.Equal("Haircut", booking.Note);
        Assert.Equal("ai", booking.CreatedBy);
        Assert.NotNull(booking.DraftId);

        // freeBusy at reserve time AND again at delivery; one insert with the attendee
        Assert.True(r.Google.FreeBusyCalls.Count() >= 2);
        var insert = Assert.Single(r.Google.Inserts);
        Assert.Equal("?sendUpdates=all", insert.Query);
        Assert.Equal("Bearer ya29.test", insert.Auth);
        var ev = JsonDocument.Parse(insert.Body).RootElement;
        Assert.Equal("aisha@example.com", ev.GetProperty("attendees")[0].GetProperty("email").GetString());
        Assert.Equal("Asia/Kuala_Lumpur", ev.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Contains("Note: Haircut", ev.GetProperty("description").GetString());

        // the reply went out, after a private note; audit + notification
        var msgs = r.Messages("60123000001");
        var note = Assert.Single(msgs, m => m.Private);
        var localStart = BookingAvailability.ToLocal(bookedAt, BookingRig.Kl);
        Assert.Equal($"📅 Booked {BookingAvailability.RangeLabel(localStart, localStart.AddMinutes(30))} (Asia/Kuala_Lumpur) on Google Calendar.", note.Text);
        var ai = Assert.Single(msgs, m => m.Source == MessageSource.Ai);
        Assert.Contains(BookingAvailability.StartLabel(localStart), ai.Text);
        Assert.True(note.Id < ai.Id);
        var fresh = r.T.Fresh();
        Assert.Contains(fresh.AuditLogs, a => a.Action == "booking.created" && a.Target == $"booking:{booking.Id}");
        var draft = Assert.Single(r.Drafts());
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
        Assert.Single(AiPendingAction.From(draft.Context));
    }

    [Fact]
    public void Review_all_holds_the_reservation_then_approve_books_it()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_review_mode", "review_all");

        r.Say("60123000001", "Book me in please");

        var draft = Assert.Single(r.Drafts());
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        var held = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Pending, held.Status);   // reserved, NOT made
        Assert.Equal(draft.Id, held.DraftId);
        Assert.DoesNotContain(r.Messages("60123000001"), m => m.Source == MessageSource.Ai);

        var o = r.Gate.Approve(draft.Id, "admin");
        Assert.Equal(AiGateStatus.Sent, o.Status);
        var made = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, made.Status);
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "booking.created" && a.Actor == "admin");
        Assert.Single(r.Messages("60123000001"), m => m.Source == MessageSource.Ai);
    }

    [Fact]
    public void Rejecting_or_superseding_a_held_reply_releases_the_reservation()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_review_mode", "review_all");

        r.Say("60123000001", "Book me in please");
        var draft = Assert.Single(r.Drafts());
        r.Gate.Reject(draft.Id, "admin", "wrong service");
        var released = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Released, released.Status);
        Assert.Contains("rejected", released.Detail);

        // a second held reply expires when the customer writes again
        r.Say("60123000001", "Actually, anything Tuesday?");
        r.Say("60123000001", "Hello?");
        var bookings = r.FreshBookings();
        Assert.Equal(3, bookings.Count);
        Assert.Equal(BookingStatus.Released, bookings[1].Status);   // the expired reply's hold
        Assert.Equal(BookingStatus.Pending, bookings[2].Status);    // the newest, still held
    }

    [Fact]
    public void A_slot_taken_between_reserve_and_approve_blocks_the_reply_with_a_note()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_review_mode", "review_all");
        r.Say("60123000001", "Book me in please");
        var draft = Assert.Single(r.Drafts());
        var held = Assert.Single(r.FreshBookings());

        // meanwhile the hold lapsed and someone else took that very time
        var row = r.Db.Bookings.First(b => b.Id == held.Id);
        row.Status = BookingStatus.Released;
        r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id + 100, SkillId = "cal-1", CalendarKey = held.CalendarKey, Status = BookingStatus.Booked,
            StartUtc = held.StartUtc, EndUtc = held.EndUtc, Timezone = held.Timezone,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        var o = r.Gate.Approve(draft.Id, "admin");
        Assert.Equal(AiGateStatus.Blocked, o.Status);
        Assert.Contains("taken", o.Detail);
        var failed = r.FreshBookings().First(b => b.Id == held.Id);
        Assert.Equal(BookingStatus.Failed, failed.Status);
        Assert.Equal(AiDraftStatus.Failed, r.Drafts().Single().Status);
        var msgs = r.Messages("60123000001");
        Assert.DoesNotContain(msgs, m => m.Source == MessageSource.Ai);
        Assert.Contains(msgs, m => m.Private && m.Text.StartsWith("📅 Booking failed"));
        Assert.Equal(ThreadStatus.Pending, r.T.Fresh().Threads.Single().Status);
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "booking.failed");
    }

    [Fact]
    public void A_google_insert_error_at_delivery_blocks_the_reply()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        r.Skill(new { source = "google", calendar = "shared" });
        r.Google.FailInsert = HttpStatusCode.Forbidden;

        r.Say("60123000001", "Book me in please");

        var b = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Failed, b.Status);
        Assert.Contains("Calendar says no", b.Detail);
        Assert.Equal(AiDraftStatus.Failed, r.Drafts().Single().Status);
        Assert.DoesNotContain(r.Messages("60123000001"), m => m.Source == MessageSource.Ai);
    }

    [Fact]
    public void Shadow_mode_never_books()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_review_mode", "shadow");

        r.Say("60123000001", "Book me in please");

        Assert.Equal(AiDraftStatus.Shadow, r.Drafts().Single().Status);
        Assert.Equal(BookingStatus.Released, r.FreshBookings().Single().Status);
        Assert.Empty(r.T.Fresh().AuditLogs.Where(a => a.Action.StartsWith("booking.")));

        // a person choosing to send the AI's words makes the booking then — re-checked
        Assert.Equal(AiGateStatus.Sent, r.Gate.Approve(r.Drafts().Single().Id, "admin").Status);
        Assert.Equal(BookingStatus.Booked, r.FreshBookings().Single().Status);
    }

    [Fact]
    public void A_google_skill_without_a_connection_says_so_and_books_nothing()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "google", calendar = "assignee" });

        r.Say("60123000001", "Book me in please");

        Assert.Empty(r.FreshBookings());
        Assert.Empty(r.Google.Calls);
        var draft = r.Drafts().Single();
        var tool = AiGate.Items(draft.Context, "tools").First();
        Assert.Equal("calendar_availability", AiGate.Str(tool, "name", ""));
        Assert.False(AiGate.Bool(tool, "ok"));
        var result = AiGate.Str(tool, "result", "");
        Assert.Contains("not available", result);
        Assert.Contains("teammate will confirm", result);
        Assert.Contains("a teammate will confirm", r.Messages("60123000001").Single(m => m.Source == MessageSource.Ai).Text);
    }

    // ---- limits ------------------------------------------------------------------------

    [Fact]
    public void A_contact_with_an_upcoming_booking_is_offered_a_reschedule_not_a_second_booking()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch" });
        r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id, SkillId = skill.SkillId, CalendarKey = "chatbranch:global", Status = BookingStatus.Booked,
            StartUtc = BookingRig.Kul(14, 15), EndUtc = BookingRig.Kul(14, 15, 30), Timezone = "Asia/Kuala_Lumpur",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();
        string? bookResult = null;
        r.Llm.Script = (msgs, _) =>
        {
            var last = msgs.LastOrDefault();
            if (last?.Role != "tool") return ToolScriptLlm.Calls(("calendar_book", "{\"start\":\"2026-10-15T10:00\"}"));
            bookResult = last.Content;
            return ToolScriptLlm.Says("You already have Wed 14 Oct at 3:00 PM — want to move it?");
        };

        r.Say("60123000001", "Book me Thursday 10am");

        Assert.Single(r.FreshBookings());   // nothing new
        var json = JsonDocument.Parse(bookResult!).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("calendar_cancel", json.GetProperty("advice").GetString());
        Assert.Equal("2026-10-14T15:00", json.GetProperty("existing_booking").GetProperty("start").GetString());
    }

    [Fact]
    public void One_booking_per_reply_and_only_offered_times()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch" });
        var results = new List<string>();
        r.Llm.Script = (msgs, _) =>
        {
            var tools = msgs.Where(m => m.Role == "tool").ToList();
            if (tools.Count == 0)
                return ToolScriptLlm.Calls(
                    ("calendar_book", "{\"start\":\"2026-10-13T07:15\"}"),   // before opening, off the grid
                    ("calendar_book", "{\"start\":\"2026-10-13T10:00\"}"),
                    ("calendar_book", "{\"start\":\"2026-10-13T11:00\"}"),   // a second booking
                    ("calendar_book", "{\"start\":\"2026-10-13T10:00\"}"));  // a repeat of the first is fine
            results.AddRange(tools.Select(t => t.Content!));
            return ToolScriptLlm.Says("Booked for Tue 13 Oct, 10:00 AM.");
        };

        r.Say("60123000001", "Tuesday 10am please");

        var j = results.Select(x => JsonDocument.Parse(x).RootElement).ToList();
        Assert.False(j[0].GetProperty("ok").GetBoolean());
        Assert.Contains("not an available time", j[0].GetProperty("error").GetString());
        Assert.True(j[0].GetProperty("alternatives").GetArrayLength() > 0);
        Assert.Equal("reserved", j[1].GetProperty("status").GetString());
        Assert.False(j[2].GetProperty("ok").GetBoolean());
        Assert.Contains("only one booking per reply", j[2].GetProperty("error").GetString());
        Assert.Equal(j[1].GetProperty("booking_id").GetInt32(), j[3].GetProperty("booking_id").GetInt32());
        var b = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, b.Status);
        Assert.Equal(BookingRig.Kul(13, 10), BookingAvailability.Utc(b.StartUtc));
    }

    [Fact]
    public void Reschedule_cancels_the_old_booking_and_books_the_new_time_on_delivery()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var skill = r.Skill(new { source = "google", calendar = "shared" });
        var key = r.Bookings.ResolveTarget(skill, CalendarAppConfig.From(skill.Config), null, null).Key;
        var old = new Booking
        {
            ContactId = c.Id, SkillId = skill.SkillId, CalendarKey = key, Source = BookingSources.Google,
            IntegrationId = r.Db.Integrations.Single().Id, ExternalEventId = "evt-old", Status = BookingStatus.Booked,
            StartUtc = BookingRig.Kul(14, 15), EndUtc = BookingRig.Kul(14, 15, 30), Timezone = "Asia/Kuala_Lumpur",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Bookings.Add(old);
        r.Db.SaveChanges();
        r.Set("ai_review_mode", "review_all");
        r.Llm.Script = (msgs, _) =>
        {
            var n = msgs.Count(m => m.Role == "tool");
            return n switch
            {
                0 => ToolScriptLlm.Calls(("calendar_my_bookings", "{}")),
                1 => ToolScriptLlm.Calls(("calendar_cancel", $"{{\"booking_id\":{old.Id}}}"),
                    ("calendar_book", "{\"start\":\"2026-10-15T10:00\"}")),
                _ => ToolScriptLlm.Says("Done — moved from Wed 14 Oct, 3:00 PM to Thu 15 Oct, 10:00 AM (Asia/Kuala_Lumpur)."),
            };
        };

        r.Say("60123000001", "Can I move my Wednesday appointment to Thursday 10am?");
        var draft = r.Drafts().Single();
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        var rows = r.FreshBookings();
        Assert.Equal(BookingStatus.Cancelling, rows[0].Status);
        Assert.Equal(draft.Id, rows[0].CancelDraftId);
        Assert.Equal(BookingStatus.Pending, rows[1].Status);
        Assert.Empty(r.Google.Inserts);
        Assert.Empty(r.Google.Deletes);

        Assert.Equal(AiGateStatus.Sent, r.Gate.Approve(draft.Id, "admin").Status);
        rows = r.FreshBookings();
        Assert.Equal(BookingStatus.Cancelled, rows[0].Status);
        Assert.Equal(BookingStatus.Booked, rows[1].Status);
        Assert.Single(r.Google.Inserts);
        var del = Assert.Single(r.Google.Deletes);
        Assert.EndsWith("/events/evt-old", del.Path);
        Assert.Equal(2, r.Messages("60123000001").Count(m => m.Private && m.Text.StartsWith("📅")));
    }

    [Fact]
    public void Rejecting_a_reschedule_restores_the_old_booking()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch" });
        var old = new Booking
        {
            ContactId = c.Id, SkillId = skill.SkillId, CalendarKey = "chatbranch:global", Status = BookingStatus.Booked,
            StartUtc = BookingRig.Kul(14, 15), EndUtc = BookingRig.Kul(14, 15, 30), Timezone = "Asia/Kuala_Lumpur",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Bookings.Add(old);
        r.Db.SaveChanges();
        r.Set("ai_review_mode", "review_all");
        r.Llm.Script = (msgs, _) => msgs.Any(m => m.Role == "tool")
            ? ToolScriptLlm.Says("Cancelled.")
            : ToolScriptLlm.Calls(("calendar_cancel", $"{{\"booking_id\":{old.Id}}}"));

        r.Say("60123000001", "Cancel my appointment");
        Assert.Equal(BookingStatus.Cancelling, r.FreshBookings().Single().Status);
        r.Gate.Reject(r.Drafts().Single().Id, "admin", null);
        Assert.Equal(BookingStatus.Booked, r.FreshBookings().Single().Status);
    }

    [Fact]
    public void Housekeeping_releases_orphaned_and_undelivered_reservations()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        Booking Row(string status, int? draftId, int hour) => new()
        {
            ContactId = c.Id, SkillId = "cal-1", CalendarKey = "chatbranch:global", Status = status, DraftId = draftId,
            StartUtc = BookingRig.Kul(15, hour), EndUtc = BookingRig.Kul(15, hour, 30), Timezone = "Asia/Kuala_Lumpur",
            CreatedAt = BookingRig.Start.UtcDateTime, UpdatedAt = BookingRig.Start.UtcDateTime,
        };
        var rejected = new AiDraft { Status = AiDraftStatus.Rejected, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var waiting = new AiDraft { Status = AiDraftStatus.Pending, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.Db.AiDrafts.AddRange(rejected, waiting);
        r.Db.SaveChanges();
        r.Db.Bookings.AddRange(Row(BookingStatus.Pending, rejected.Id, 9), Row(BookingStatus.Pending, waiting.Id, 10),
            Row(BookingStatus.Pending, null, 11));
        r.Db.SaveChanges();

        Assert.Equal(1, r.Bookings.Housekeep());   // the rejected draft's hold; the orphan is still young
        r.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, r.Bookings.Housekeep());   // now the orphan too; the held draft keeps its hold
        var rows = r.FreshBookings();
        Assert.Equal(new[] { BookingStatus.Released, BookingStatus.Pending, BookingStatus.Released },
            rows.Select(b => b.Status).ToArray());
    }

    [Fact]
    public async Task An_ai_flow_message_reserves_while_composing_and_books_when_the_queue_delivers_it()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = c.Phone, Name = c.Name, ContactId = c.Id,
            Status = ThreadStatus.Open, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var wf = new Workflow { FlowId = "wf-book", Name = "Book", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        var run = new FlowRun { WorkflowId = wf.Id, ContactId = c.Id, CurrentNode = "act", Status = FlowRunStatus.Running,
            StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.Db.FlowRuns.Add(run);
        r.Db.SaveChanges();

        var result = await r.Assistant.ComposeActionTextAsync(run, c,
            JsonSerializer.SerializeToElement(new { skill = "cal-1", channels = new[] { "WhatsApp" } }),
            "Offer {{name}} a consultation slot.");
        Assert.True(result.ShouldSend);
        var action = Assert.Single(result.PendingActions);
        Assert.Equal(BookingStatus.Pending, r.FreshBookings().Single().Status);

        // what FlowEngine.StampAiJobs queues — read back from the DB as JSON
        r.Queue.Enqueue("whatsapp", JobKind.Text, c.Phone, new Dictionary<string, object?>
        {
            ["text"] = result.Text, ["source"] = "ai", ["ai_meta"] = result.ToMeta(),
        }, c.Id, flowRunId: run.Id, flowNode: "act");
        r.Db.ChangeTracker.Clear();
        r.Queue.Drain();

        var booked = r.FreshBookings().Single();
        Assert.Equal(action.BookingId, booked.Id);
        Assert.Equal(BookingStatus.Booked, booked.Status);
        var draft = r.Drafts().Single();
        Assert.Equal(AiDraftKind.FlowMessage, draft.Kind);
        Assert.Equal(draft.Id, booked.DraftId);
    }

    [Fact]
    public void A_booking_whose_typed_confirmation_is_refused_later_stays_and_is_flagged()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_typing", "true");   // the reply goes out after a typing pause, via the queue

        r.Say("60123000001", "Book me in please");
        Assert.Equal(BookingStatus.Booked, r.FreshBookings().Single().Status);   // made at delivery
        Assert.DoesNotContain(r.Messages("60123000001"), m => m.Source == MessageSource.Ai);

        var t = r.Db.Threads.Single();
        t.OptedOut = true;                 // STOP arrives during the typing pause
        r.Db.SaveChanges();
        r.Clock.Advance(TimeSpan.FromMinutes(2));
        r.Queue.Drain();

        Assert.Equal(AiDraftStatus.Failed, r.Drafts().Single().Status);
        Assert.Equal(BookingStatus.Booked, r.FreshBookings().Single().Status);
        Assert.Contains(r.Messages("60123000001"), m => m.Private && m.Text.Contains("was not delivered"));
    }

    [Fact]
    public async Task An_operator_preview_books_nothing()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill(new { source = "chatbranch" });
        var t = new Thread { Channel = "whatsapp", Key = c.Phone, Name = c.Name, ContactId = c.Id,
            Status = ThreadStatus.Open, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.Db.Threads.Add(t);
        r.Db.SaveChanges();
        r.Db.Messages.Add(new Message { ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
            Text = "Book me in", CreatedAt = DateTime.UtcNow });
        r.Db.SaveChanges();

        var preview = await r.Assistant.GenerateReplyAsync(t, applyOutcome: false);
        Assert.Single(preview.PendingActions);
        r.Assistant.ReleasePending(preview, "preview");     // what POST /api/aicore/reply does
        Assert.Equal(BookingStatus.Released, r.FreshBookings().Single().Status);
    }

    // ---- the flow actions still run on the shared client --------------------------------

    [Fact]
    public async Task Flow_calendar_actions_run_on_the_shared_google_client()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var oauth = new GoogleOAuth(r.Db, r.Sc, r.Google);
        var actions = new IntegrationActions(r.Db, new IntegrationResolver(r.Db, r.Sc), oauth, r.Google);
        r.Google.Busy.Add((new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc)));

        var free = await actions.RunAsync("Find free slot", c, new Dictionary<string, string>
        {
            ["eventStart"] = "2026-10-20T01:00:00Z", ["eventMinutes"] = "30",
        }, "", null, null);
        Assert.True(free.Ok, free.Detail);
        Assert.Equal("2026-10-20T02:00:00.0000000Z", free.Data!["slot"]);

        var created = await actions.RunAsync("Create calendar event", c, new Dictionary<string, string>
        {
            ["eventStart"] = "2026-10-20T02:00:00Z", ["eventTitle"] = "Consultation",
        }, "Notes", null, null);
        Assert.True(created.Ok, created.Detail);
        Assert.Equal("evt-1", created.Data!["event_id"]);
        var insert = r.Google.Inserts.Single();
        Assert.Equal("", insert.Query);   // flow events keep their old behaviour: no sendUpdates
        Assert.Contains("aisha@example.com", insert.Body);

        r.Db.Integrations.Single().Credentials = new Dictionary<string, object?>();
        r.Db.SaveChanges();
        var skipped = await actions.RunAsync("Create calendar event", c, new Dictionary<string, string>(), "x", null, null);
        Assert.True(skipped.Skipped);
    }
}

/// <summary>
/// The HTTP surface: /api/bookings (scoped like conversations) + operator
/// cancel, the skills PUT validation of app configs, the connected-app catalog,
/// the availability dry run, and bookings on AI-monitor draft payloads.
/// </summary>
public class BookingApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly object Gate = new();

    public BookingApiTests(ApiFactory factory)
    {
        _factory = factory;
        _ = _factory.CreateClient();
        Arrange();
    }

    private sealed record Ids(int East, int West, int EastContact, int WestContact, int EastThread);
    private static Ids? _ids;

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Entities.Any(e => e.Slug == "bk-east"))
            {
                var e = db.Entities.First(x => x.Slug == "bk-east").Id;
                var w = db.Entities.First(x => x.Slug == "bk-west").Id;
                _ids = new Ids(e, w, db.Contacts.First(c => c.Name == "Eve East").Id,
                    db.Contacts.First(c => c.Name == "Walt West").Id, db.Threads.First(t => t.Key == "60190000001").Id);
                return;
            }
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var east = new Entity { TenantId = tenant.Id, Name = "Booking East", Slug = "bk-east", CreatedAt = now, UpdatedAt = now };
            var west = new Entity { TenantId = tenant.Id, Name = "Booking West", Slug = "bk-west", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(east, west);
            db.SaveChanges();
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities.Add(east);
            var eve = new Contact { Name = "Eve East", Phone = "60190000001", CreatedAt = now, UpdatedAt = now };
            var walt = new Contact { Name = "Walt West", Phone = "60190000002", CreatedAt = now, UpdatedAt = now };
            db.Contacts.AddRange(eve, walt);
            db.SaveChanges();
            var t1 = new Thread { Channel = "whatsapp", Key = "60190000001", Name = "Eve", ContactId = eve.Id,
                EntityId = east.Id, Status = ThreadStatus.Open, CreatedAt = now, UpdatedAt = now };
            var t2 = new Thread { Channel = "whatsapp", Key = "60190000002", Name = "Walt", ContactId = walt.Id,
                EntityId = west.Id, Status = ThreadStatus.Open, CreatedAt = now, UpdatedAt = now };
            db.Threads.AddRange(t1, t2);
            db.SaveChanges();
            Booking Row(Contact c, Thread? t, int? entity, int days, string status = BookingStatus.Booked) => new()
            {
                ContactId = c.Id, ThreadId = t?.Id, EntityId = entity, SkillId = "cal-api", CalendarKey = "chatbranch:global",
                CalendarLabel = "ChatBranch bookings", Source = BookingSources.ChatBranch, Title = $"{c.Name} — appointment",
                Status = status, StartUtc = now.Date.AddDays(days).AddHours(2), EndUtc = now.Date.AddDays(days).AddHours(2.5),
                Timezone = "Asia/Kuala_Lumpur", CreatedBy = "ai", CreatedAt = now, UpdatedAt = now,
            };
            db.Bookings.AddRange(
                Row(eve, t1, east.Id, 3),
                Row(eve, t1, east.Id, -3),
                Row(walt, t2, west.Id, 4),
                Row(walt, null, null, 5),
                Row(eve, t1, east.Id, 6, BookingStatus.Released));
            db.SaveChanges();
            _ids = new Ids(east.Id, west.Id, eve.Id, walt.Id, t1.Id);
        }
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T Db<T>(Func<AppDbContext, T> work)
    {
        using var scope = _factory.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Bookings_list_is_scoped_like_conversations()
    {
        var admin = await Authed("admin", "admin123");
        var all = (await admin.GetFromJsonAsync<JsonElement>("/api/bookings")).GetProperty("bookings");
        var adminNames = all.EnumerateArray().Select(b => b.GetProperty("contact_name").GetString()).ToList();
        Assert.Contains("Walt West", adminNames);
        Assert.Contains(all.EnumerateArray(), b => b.GetProperty("status").GetString() == "released"); // every status

        var agent = await Authed("agent", "agent123");
        var mine = (await agent.GetFromJsonAsync<JsonElement>("/api/bookings?status=booked,cancelled")).GetProperty("bookings");
        // East's two + the unassigned one; never West's
        Assert.Equal(3, mine.GetArrayLength());
        Assert.DoesNotContain(mine.EnumerateArray(), b => b.GetProperty("thread").GetString() == "whatsapp:60190000002");
        var widened = (await agent.GetFromJsonAsync<JsonElement>("/api/bookings?entity=bk-west")).GetProperty("bookings");
        Assert.DoesNotContain(widened.EnumerateArray(), b => b.GetProperty("thread").GetString() == "whatsapp:60190000002");

        // upcoming = soonest first, not ended; thread filter; shape
        var up = (await admin.GetFromJsonAsync<JsonElement>("/api/bookings?thread=whatsapp:60190000001&upcoming=1&status=booked,cancelled"))
            .GetProperty("bookings");
        var one = Assert.Single(up.EnumerateArray());
        Assert.Equal("whatsapp:60190000001", one.GetProperty("thread").GetString());
        Assert.Equal("Eve East", one.GetProperty("contact_name").GetString());
        Assert.EndsWith("+08:00", one.GetProperty("start").GetString());
        Assert.Matches(@"^\w{3} \d{1,2} \w{3}, \d{1,2}:\d{2}–\d{1,2}:\d{2} (AM|PM)$", one.GetProperty("label").GetString());
        foreach (var key in new[] { "id", "contact_id", "end", "timezone", "title", "status", "source", "calendar_label",
                     "event_link", "created_by", "skill_id", "draft_id", "detail", "created_at" })
            Assert.True(one.TryGetProperty(key, out _), key);
        var past = (await admin.GetFromJsonAsync<JsonElement>($"/api/bookings?contact_id={_ids!.EastContact}&upcoming=0"))
            .GetProperty("bookings");
        Assert.Single(past.EnumerateArray());
        var released = (await admin.GetFromJsonAsync<JsonElement>($"/api/bookings?contact_id={_ids.EastContact}&status=released"))
            .GetProperty("bookings");
        Assert.Single(released.EnumerateArray());
    }

    [Fact]
    public async Task Operator_cancel_cancels_now_with_a_note_and_stays_in_scope()
    {
        var id = Db(db => db.Bookings.First(b => b.ContactId == _ids!.EastContact && b.Status == BookingStatus.Booked
                                                 && b.StartUtc > DateTime.UtcNow).Id);
        var westId = Db(db => db.Bookings.First(b => b.ThreadId != null && b.ContactId == _ids!.WestContact).Id);
        var agent = await Authed("agent", "agent123");

        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsync($"/api/bookings/{westId}/cancel", null)).StatusCode);

        var res = await agent.PostAsync($"/api/bookings/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", body.GetProperty("booking").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsync($"/api/bookings/{id}/cancel", null)).StatusCode);

        Db(db =>
        {
            Assert.Contains(db.Messages, m => m.ThreadId == _ids!.EastThread && m.Private && m.Text.StartsWith("📅 Cancelled"));
            Assert.Contains(db.AuditLogs, a => a.Action == "booking.cancelled" && a.Target == $"booking:{id}" && a.Actor == "agent");
            return 0;
        });
    }

    [Fact]
    public async Task Skills_put_validates_app_configs_and_round_trips_them()
    {
        var admin = await Authed("admin", "admin123");
        async Task<HttpResponseMessage> Put(object config) => await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            entity = "bk-east",
            skills = new object[]
            {
                new { id = "cal-east", name = "Bookings", useWhen = "book", instructions = "Offer times", position = 0,
                      kind = "app", config },
            },
        });
        async Task<string> Detail(HttpResponseMessage r) =>
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

        var bad = await Put(new { hours = new { mon = new[] { new[] { "09:00", "12:00" }, new[] { "11:30", "13:00" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("overlap", await Detail(bad));
        Assert.Contains("duration_minutes", await Detail(await Put(new { duration_minutes = 2 })));
        Assert.Contains("max_days_ahead", await Detail(await Put(new { max_days_ahead = 0 })));
        Assert.Contains("unknown action", await Detail(await Put(new { actions = new[] { "delete_everything" } })));
        Assert.Contains("not a Google Calendar connection",
            await Detail(await Put(new { source = "google", calendar = "987654" })));

        var ok = await Put(new { source = "chatbranch", duration_minutes = 45, actions = new[] { "availability", "book" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var skill = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "cal-east");
        Assert.Equal("app", skill.GetProperty("kind").GetString());
        var cfg = skill.GetProperty("config");
        Assert.Equal(45, cfg.GetProperty("duration_minutes").GetInt32());
        Assert.Equal(45, cfg.GetProperty("slot_step_minutes").GetInt32());   // step defaults to the duration
        Assert.Equal("09:00", cfg.GetProperty("hours").GetProperty("tue")[0][0].GetString());
        Assert.Equal(2, cfg.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task Apps_catalog_and_the_availability_dry_run()
    {
        var admin = await Authed("admin", "admin123");
        var shared = Db(db =>
        {
            var agentId = db.Users.First(u => u.Username == "agent").Id;
            var adminId = db.Users.First(u => u.Username == "admin").Id;
            Integration Row(int? owner) => new()
            {
                Provider = "google_calendar", OwnerUserId = owner, Status = "healthy",
                Credentials = new Dictionary<string, object?> { ["access_token"] = "tok" },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            if (!db.Integrations.Any(i => i.Provider == "google_calendar"))
            {
                db.Integrations.AddRange(Row(null), Row(agentId), Row(adminId));
                db.SaveChanges();
            }
            return db.Integrations.First(i => i.Provider == "google_calendar" && i.OwnerUserId == null).Id;
        });

        var apps = (await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps")).GetProperty("apps");
        var cal = Assert.Single(apps.EnumerateArray());
        Assert.Equal("calendar", cal.GetProperty("app").GetString());
        Assert.Equal(3, cal.GetProperty("actions").GetArrayLength());
        Assert.Equal(30, cal.GetProperty("defaults").GetProperty("duration_minutes").GetInt32());
        var google = cal.GetProperty("sources")[0];
        Assert.True(google.GetProperty("connected").GetBoolean());
        Assert.Equal(3, google.GetProperty("connections").GetArrayLength());   // admins see every personal calendar
        Assert.True(cal.GetProperty("sources")[1].GetProperty("connected").GetBoolean());

        var agent = await Authed("agent", "agent123");
        var agentConns = (await agent.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps"))
            .GetProperty("apps")[0].GetProperty("sources")[0].GetProperty("connections");
        // the shared calendar + their own — never the admin's personal one
        Assert.Equal(2, agentConns.GetArrayLength());
        Assert.Contains(agentConns.EnumerateArray(), c => c.GetProperty("owner").GetString() == "org" && c.GetProperty("id").GetInt32() == shared);
        Assert.DoesNotContain(agentConns.EnumerateArray(), c => c.GetProperty("owner_name").GetString() == "admin");

        // dry run: every hour of every day open, no notice ⇒ there are always free times
        var everyDay = new Dictionary<string, object>();
        foreach (var d in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
            everyDay[d] = new[] { new[] { "00:00", "24:00" } };
        var before = Db(db => db.Bookings.Count());
        var dry = await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "app", app_action = "availability",
            config = new { source = "chatbranch", hours = everyDay, min_notice_minutes = 0 },
        });
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var body = await dry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        Assert.Equal(8, body.GetProperty("slots").GetArrayLength());
        Assert.Equal("chatbranch", body.GetProperty("source").GetString());
        Assert.Contains("Nothing was booked", body.GetProperty("detail").GetString());
        Assert.Equal(before, Db(db => db.Bookings.Count()));

        var invalid = await (await admin.PostAsJsonAsync("/api/aicore/skills/test",
            new { kind = "app", config = new { duration_minutes = 1 } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(invalid.GetProperty("ok").GetBoolean());
        Assert.Contains("duration_minutes", invalid.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Monitor_drafts_say_what_approving_will_book()
    {
        var draftId = Db(db =>
        {
            var b = db.Bookings.First(x => x.ContactId == _ids!.EastContact);
            var d = new AiDraft
            {
                ThreadId = _ids!.EastThread, ThreadKey = "60190000001", Channel = "whatsapp", ContactId = _ids.EastContact,
                EntityId = _ids.East, Kind = AiDraftKind.Reply, Content = "You're booked!", Status = AiDraftStatus.Pending,
                Context = new Dictionary<string, object?>
                {
                    ["pending_actions"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["type"] = "book", ["booking_id"] = b.Id },
                    },
                },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.AiDrafts.Add(d);
            db.SaveChanges();
            return d.Id;
        });
        var admin = await Authed("admin", "admin123");
        var draft = (await admin.GetFromJsonAsync<JsonElement>($"/api/ai/monitor/{draftId}")).GetProperty("draft");
        var item = Assert.Single(draft.GetProperty("bookings").EnumerateArray());
        Assert.Equal("book", item.GetProperty("type").GetString());
        Assert.Equal("ChatBranch bookings", item.GetProperty("calendar_label").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("label").GetString()));
        Assert.True(item.TryGetProperty("status", out _));

        var list = (await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?status=pending")).GetProperty("drafts");
        Assert.All(list.EnumerateArray(), d => Assert.True(d.TryGetProperty("bookings", out _)));
    }
}
