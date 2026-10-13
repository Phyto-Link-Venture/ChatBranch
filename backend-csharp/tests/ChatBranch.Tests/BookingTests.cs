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
    /// <summary>The Google Meet link an insert with conferenceData answers ("" = none).</summary>
    public string MeetLink { get; set; } = "https://meet.google.com/abc-defg-hij";
    public HttpStatusCode? FailFreeBusy { get; set; }
    /// <summary>What events.list answers (raw Google items) and its calendar's timezone.</summary>
    public List<object> EventItems { get; } = new();
    public string EventsTimeZone { get; set; } = "Asia/Kuala_Lumpur";
    public HttpStatusCode? FailEvents { get; set; }
    /// <summary>What calendarList answers (raw Google items).</summary>
    public List<object> CalendarItems { get; } = new();
    public IEnumerable<Call> EventReads => Calls.Where(c => c.Method == "GET" && c.Path.EndsWith("/events"));
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
        if (request.Method == HttpMethod.Get && path.EndsWith("/events"))
            return FailEvents is HttpStatusCode fe
                ? Json(fe, new { error = new { message = "events refused" } })
                : Json(HttpStatusCode.OK, new { timeZone = EventsTimeZone, items = EventItems.ToArray() });
        if (request.Method == HttpMethod.Get && path.EndsWith("/users/me/calendarList"))
            return Json(HttpStatusCode.OK, new { items = CalendarItems.ToArray() });
        if (request.Method == HttpMethod.Post && path.EndsWith("/events"))
        {
            if (FailInsert is HttpStatusCode fi) return Json(fi, new { error = new { message = "Calendar says no" } });
            var n = Interlocked.Increment(ref _events);
            // a Google Meet comes back with the event when it was asked for (conferenceDataVersion=1)
            return request.RequestUri.Query.Contains("conferenceDataVersion=1") && MeetLink.Length > 0
                ? Json(HttpStatusCode.OK, new { id = $"evt-{n}", htmlLink = $"https://calendar.google.com/event?eid={n}",
                    hangoutLink = MeetLink })
                : Json(HttpStatusCode.OK, new { id = $"evt-{n}", htmlLink = $"https://calendar.google.com/event?eid={n}" });
        }
        if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
}

/// <summary>
/// zoom.us / api.zoom.us stand-in at the HTTP level, so the REAL <see cref="ZoomClient"/>
/// (the Server-to-Server token, the meeting create / delete bodies) is what runs.
/// </summary>
internal sealed class FakeZoom : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Method, string Host, string Path, string Query, string Body, string? Auth);

    public List<Call> Calls { get; } = new();
    public HttpStatusCode? FailToken { get; set; }
    public HttpStatusCode? FailCreate { get; set; }
    private long _meetings = 85_000_000_000;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public IEnumerable<Call> Tokens => Calls.Where(c => c.Path == "/oauth/token");
    public IEnumerable<Call> Creates => Calls.Where(c => c.Method == "POST" && c.Path == "/v2/users/me/meetings");
    public IEnumerable<Call> Deletes => Calls.Where(c => c.Method == "DELETE");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var uri = request.RequestUri!;
        lock (Calls)
            Calls.Add(new Call(request.Method.Method, uri.Host, uri.AbsolutePath, uri.Query, body,
                request.Headers.Authorization?.ToString()));
        if (uri.Host == "zoom.us" && uri.AbsolutePath == "/oauth/token")
            return FailToken is HttpStatusCode ft
                ? Json(ft, new { reason = "Invalid client_id or client_secret", error = "invalid_client" })
                : Json(HttpStatusCode.OK, new { access_token = "zoom-token", token_type = "bearer", expires_in = 3599 });
        if (uri.Host != "api.zoom.us") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/v2/users/me/meetings")
        {
            if (FailCreate is HttpStatusCode fc) return Json(fc, new { code = 124, message = "Invalid access token." });
            var id = Interlocked.Increment(ref _meetings);
            return Json(HttpStatusCode.Created, new { id, join_url = $"https://us06web.zoom.us/j/{id}?pwd=abc", password = "abc" });
        }
        if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/v2/users/me")
            return Json(HttpStatusCode.OK, new { email = "owner@example.com" });
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
    public FakeZoom Zoom { get; } = new();
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
        Bookings = new BookingService(Db, Sc, new IntegrationResolver(Db, Sc), calendar, Clock, new ZoomClient(Db, Zoom));
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

    // ---- the availability tool: a whole day, an exact time, "missing ≠ busy" --------------
    // (production 2026-10-12: a free 2 PM missing from the 8-slot sample was told "not open")

    /// <summary>Call the availability tool the way the AI does; returns the parsed JSON.</summary>
    private static async Task<JsonElement> Availability(BookingRig r, AgentSkill skill, object args)
    {
        var binding = new BookingService.ToolBinding(skill, CalendarAppConfig.From(skill.Config), "availability", "calendar");
        var outcome = await r.Bookings.ExecuteToolAsync(binding, JsonSerializer.Serialize(args), new SkillContext());
        return JsonDocument.Parse(outcome.Json).RootElement.Clone();
    }

    private static List<string> Starts(JsonElement j, string key = "slots") =>
        j.GetProperty(key).EnumerateArray().Select(s => s.GetProperty("start").GetString()!).ToList();

    [Fact]
    public async Task A_one_day_search_lists_every_free_time_and_says_it_is_complete()
    {
        using var r = new BookingRig();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        // Tue 13 Oct, 09:00–17:00 on a 30-minute grid = 16 free times — twice the multi-day sample
        var j = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-13" });

        Assert.True(j.GetProperty("ok").GetBoolean());
        var starts = Starts(j);
        Assert.Equal(16, starts.Count);
        Assert.Equal(16, j.GetProperty("free_slots_in_range").GetInt32());
        Assert.Equal("2026-10-13T09:00", starts[0]);
        Assert.Equal("2026-10-13T16:30", starts[^1]);
        Assert.True(j.GetProperty("complete").GetBoolean());
        Assert.Contains("all the free times", j.GetProperty("note").GetString());
        Assert.Contains("never tell the customer your earlier times were made up", j.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_multi_day_search_is_a_sample_that_says_a_missing_time_may_be_free()
    {
        using var r = new BookingRig();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        var j = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-16" });

        Assert.True(Starts(j).Count <= BookingAvailability.MaxOffered);
        Assert.False(j.GetProperty("complete").GetBoolean());
        var note = j.GetProperty("note").GetString()!;
        Assert.Contains("A time missing from this list may still be free", note);
        Assert.Contains("call this tool with `time`", note);
        Assert.Contains("Earlier results were real", note);
    }

    [Fact]
    public async Task A_free_two_pm_left_out_of_the_sample_is_still_answered_as_available()
    {
        using var r = new BookingRig();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        // the regression: the multi-day sample skips Tue 14:00 although it is free …
        var sample = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-16" });
        Assert.DoesNotContain("2026-10-13T14:00", Starts(sample));
        // … and the exact check says so
        var j = await Availability(r, skill, new { from_date = "2026-10-13", time = "14:00" });
        Assert.True(j.GetProperty("ok").GetBoolean());
        var req = j.GetProperty("requested");
        Assert.True(req.GetProperty("available").GetBoolean());
        Assert.Equal("2026-10-13T14:00", req.GetProperty("start").GetString());
        Assert.Equal("Tue 13 Oct, 2:00 PM", req.GetProperty("label").GetString());
        Assert.False(j.TryGetProperty("alternatives", out _));
        // a single day with the same date as to_date is the same question
        var same = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-13", time = "14:00" });
        Assert.True(same.GetProperty("requested").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task An_exact_time_that_is_not_bookable_says_why_and_offers_the_nearest()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 120, max_days_ahead = 14 });
        var target = r.Bookings.ResolveTarget(skill, CalendarAppConfig.From(skill.Config), null, null);
        r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id, SkillId = "x", CalendarKey = target.Key, Status = BookingStatus.Booked,
            Timezone = "Asia/Kuala_Lumpur", StartUtc = BookingRig.Kul(13, 14), EndUtc = BookingRig.Kul(13, 14, 30),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        async Task<JsonElement> Ask(string day, string time) => await Availability(r, skill, new { from_date = day, time });
        string Reason(JsonElement j) => j.GetProperty("requested").GetProperty("reason").GetString()!;

        // taken by a booking → busy, with the nearest free times that day
        var busy = await Ask("2026-10-13", "14:00");
        Assert.False(busy.GetProperty("requested").GetProperty("available").GetBoolean());
        Assert.Equal("busy", Reason(busy));
        Assert.Equal(new[] { "2026-10-13T13:00", "2026-10-13T13:30", "2026-10-13T14:30" }, Starts(busy, "alternatives"));
        Assert.Contains("already taken", busy.GetProperty("note").GetString());

        Assert.Equal("outside_hours", Reason(await Ask("2026-10-13", "07:00")));
        Assert.Equal("outside_hours", Reason(await Ask("2026-10-13", "16:45")));   // 30 minutes would run past 17:00
        Assert.Equal("outside_hours", Reason(await Ask("2026-10-17", "10:00")));   // Saturday: closed
        // now = Mon 08:00 KL with 2 hours' notice ⇒ nothing before 10:00 today
        var soon = await Ask("2026-10-12", "09:00");
        Assert.Equal("too_soon", Reason(soon));
        Assert.Contains("10:00", string.Join(",", Starts(soon, "alternatives")));
        Assert.Equal("too_soon", Reason(await Ask("2026-10-11", "10:00")));        // yesterday
        Assert.Equal("off_grid", Reason(await Ask("2026-10-13", "14:10")));        // a 30-minute grid
        Assert.Equal("beyond_last_day", Reason(await Ask("2026-10-30", "10:00"))); // 14 days ahead = 26 Oct
    }

    [Fact]
    public async Task A_bad_time_is_a_refusal_not_an_exception()
    {
        using var r = new BookingRig();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        foreach (var bad in new[] { "2pm", "25:00", "14:60", "14", "noon" })
        {
            var j = await Availability(r, skill, new { from_date = "2026-10-13", time = bad });
            Assert.False(j.GetProperty("ok").GetBoolean());
            Assert.Contains("is not a time", j.GetProperty("error").GetString());
            Assert.Contains("HH:mm", j.GetProperty("advice").GetString());
        }
        // `time` needs one day: no from_date, or a different to_date, is refused too
        var noDay = await Availability(r, skill, new { time = "14:00" });
        Assert.False(noDay.GetProperty("ok").GetBoolean());
        var twoDays = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-14", time = "14:00" });
        Assert.False(twoDays.GetProperty("ok").GetBoolean());
        Assert.Contains("one day", twoDays.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_full_day_that_would_overflow_the_result_shrinks_its_slots_instead_of_being_cut()
    {
        using var r = new BookingRig();
        // 09:00–17:00 every 10 minutes = 48 free times: too long with an `end` on each
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0, duration_minutes = 10, slot_step_minutes = 10 });
        var binding = new BookingService.ToolBinding(skill, CalendarAppConfig.From(skill.Config), "availability", "calendar");
        var outcome = await r.Bookings.ExecuteToolAsync(binding,
            "{\"from_date\":\"2026-10-13\",\"to_date\":\"2026-10-13\"}", new SkillContext());

        Assert.True(outcome.Json.Length <= 4000);
        var j = JsonDocument.Parse(outcome.Json).RootElement;   // whole, valid JSON — never cut mid-string
        var slots = j.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(48, slots.Count);
        Assert.True(j.GetProperty("complete").GetBoolean());
        Assert.All(slots, s => Assert.False(s.TryGetProperty("end", out _)));
        Assert.Equal("2026-10-13T16:50", slots[^1].GetProperty("start").GetString());
    }

    // ---- variable booking length (one booking, N × unit) ------------------------------

    /// <summary>30-minute units up to 2 hours: 30, 60, 90 or 120 minutes, 30 by default.</summary>
    private static readonly object Hourly = new
    {
        source = "google", calendar = "shared", min_notice_minutes = 0,
        duration_minutes = 30, duration_unit_minutes = 30, max_units = 4,
    };

    private static async Task<JsonElement> Tool(BookingRig r, AgentSkill skill, string action, object args,
        SkillContext? ctx = null)
    {
        var binding = new BookingService.ToolBinding(skill, CalendarAppConfig.From(skill.Config), action, "calendar");
        var outcome = await r.Bookings.ExecuteToolAsync(binding, JsonSerializer.Serialize(args), ctx ?? new SkillContext());
        return JsonDocument.Parse(outcome.Json).RootElement.Clone();
    }

    [Fact]
    public async Task A_longer_booking_is_only_offered_where_the_whole_span_is_free()
    {
        using var r = new BookingRig();
        r.GoogleRow();
        var skill = r.Skill(Hourly);
        r.Google.Busy.Add((BookingRig.Kul(13, 10, 30), BookingRig.Kul(13, 11)));   // Tue 10:30–11:00 taken

        // the default length: 10:00–10:30 is still free
        var halfHour = await Availability(r, skill, new { from_date = "2026-10-13", to_date = "2026-10-13" });
        Assert.Contains("2026-10-13T10:00", Starts(halfHour));
        Assert.Equal(30, halfHour.GetProperty("duration_minutes").GetInt32());

        // an hour: 10:00 would run into the busy half hour, 10:30 starts in it; 09:30–10:30 just fits
        var hour = await Availability(r, skill,
            new { from_date = "2026-10-13", to_date = "2026-10-13", duration_minutes = 60 });
        Assert.True(hour.GetProperty("ok").GetBoolean(), hour.ToString());
        Assert.Equal(60, hour.GetProperty("duration_minutes").GetInt32());
        var starts = Starts(hour);
        Assert.DoesNotContain("2026-10-13T10:00", starts);
        Assert.DoesNotContain("2026-10-13T10:30", starts);
        Assert.Contains("2026-10-13T09:30", starts);
        Assert.Contains("2026-10-13T11:00", starts);
        Assert.Equal("2026-10-13T16:00", starts[^1]);                    // 16:00–17:00 still inside the hours
        var first = hour.GetProperty("slots")[0];
        Assert.Equal("2026-10-13T10:00", first.GetProperty("end").GetString());
        Assert.Equal("Tue 13 Oct, 9:00–10:00 AM", first.GetProperty("label").GetString());   // a range, not a start
        Assert.Equal(new[] { 30, 60, 90, 120 },
            hour.GetProperty("allowed_lengths").EnumerateArray().Select(e => e.GetInt32()));

        // the exact-time check measures the requested length too
        var ten = await Availability(r, skill, new { from_date = "2026-10-13", time = "10:00", duration_minutes = 60 });
        var asked = ten.GetProperty("requested");
        Assert.False(asked.GetProperty("available").GetBoolean());
        Assert.Equal(BookingAvailability.Reasons.Busy, asked.GetProperty("reason").GetString());
        var nine = await Availability(r, skill, new { from_date = "2026-10-13", time = "09:30", duration_minutes = 60 });
        Assert.True(nine.GetProperty("requested").GetProperty("available").GetBoolean(), nine.ToString());

        // the pure math: two hours fit only where the whole span is free and inside the window
        var cfg = CalendarAppConfig.From(skill.Config);
        var twoHours = BookingAvailability.FreeSlots(cfg, BookingRig.Kl, BookingRig.Start.UtcDateTime,
            new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13),
            new[] { (BookingRig.Kul(13, 10, 30), BookingRig.Kul(13, 11)) }, durationMinutes: 120);
        // 09:00–10:30 would all run into the busy half hour; 11:00 … 15:00 fit (15:00–17:00 the last)
        Assert.Equal(9, twoHours.Count);
        Assert.Equal(new TimeSpan(11, 0, 0), twoHours[0].LocalStart.TimeOfDay);
        Assert.All(twoHours, s => Assert.Equal(TimeSpan.FromMinutes(120), s.EndUtc - s.StartUtc));
        Assert.Equal(new TimeSpan(15, 0, 0), twoHours[^1].LocalStart.TimeOfDay);
    }

    [Fact]
    public async Task A_length_the_calendar_does_not_book_is_refused_with_the_allowed_ones()
    {
        using var r = new BookingRig();
        r.GoogleRow();
        var c = r.Contact();
        var skill = r.Skill(Hourly);

        var odd = await Availability(r, skill, new { from_date = "2026-10-13", duration_minutes = 45 });
        Assert.False(odd.GetProperty("ok").GetBoolean());
        Assert.Contains("45 minutes is not a length", odd.GetProperty("error").GetString());
        Assert.Contains("30, 60, 90, 120", odd.GetProperty("advice").GetString());
        Assert.Equal(4, odd.GetProperty("allowed_lengths").GetArrayLength());

        var words = await Availability(r, skill, new { from_date = "2026-10-13", duration_minutes = "an hour" });
        Assert.False(words.GetProperty("ok").GetBoolean());                // a refusal, never an exception

        var tooLong = await Tool(r, skill, "book", new { start = "2026-10-13T09:00", duration_minutes = 150 },
            new SkillContext { ContactId = c.Id });
        Assert.False(tooLong.GetProperty("ok").GetBoolean());
        Assert.Contains("150 minutes", tooLong.GetProperty("error").GetString());
        Assert.Empty(r.FreshBookings());

        // a fixed-length calendar: its own length passes, any other is refused
        var fixedSkill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 }, id: "cal-fixed");
        Assert.True((await Availability(r, fixedSkill, new { from_date = "2026-10-13", duration_minutes = 30 }))
            .GetProperty("ok").GetBoolean());
        var notHere = await Availability(r, fixedSkill, new { from_date = "2026-10-13", duration_minutes = 60 });
        Assert.False(notHere.GetProperty("ok").GetBoolean());
        Assert.Contains("always 30 minutes", notHere.GetProperty("advice").GetString());
    }

    [Fact]
    public void A_ninety_minute_booking_is_one_row_and_one_event_covering_the_whole_span()
    {
        using var r = new BookingRig();
        r.Contact();
        r.GoogleRow();
        r.Skill(Hourly);                                                   // max_active_per_contact stays 1
        IReadOnlyList<LlmTool>? offered = null;
        r.Llm.Script = (msgs, tools) =>
        {
            offered ??= tools;
            var last = msgs.LastOrDefault();
            if (last?.Role != "tool")
                return ToolScriptLlm.Calls(("calendar_availability", "{\"from_date\":\"2026-10-13\",\"to_date\":\"2026-10-13\",\"duration_minutes\":90}"));
            var json = JsonDocument.Parse(last.Content!).RootElement;
            if (json.TryGetProperty("slots", out var slots) && slots.GetArrayLength() > 0)
                return ToolScriptLlm.Calls(("calendar_book",
                    $"{{\"start\":\"{slots[0].GetProperty("start").GetString()}\",\"duration_minutes\":90}}"));
            if (json.TryGetProperty("status", out var st) && st.GetString() == "reserved")
                return ToolScriptLlm.Says($"You're booked for {json.GetProperty("label").GetString()} ({json.GetProperty("timezone").GetString()}).");
            return ToolScriptLlm.Says("Sorry — a teammate will confirm a time with you.");
        };

        r.Say("60123000001", "Can I book a 90-minute session on Tuesday?");

        // the model was told to ask how long, and both tools take the length
        Assert.Contains("How long:", r.Llm.Seen[0].System);
        var book = offered!.Single(t => t.Name == "calendar_book");
        Assert.Contains("time range", book.Description);
        var lengths = book.Parameters.GetProperty("properties").GetProperty("duration_minutes").GetProperty("enum");
        Assert.Equal(new[] { 30, 60, 90, 120 }, lengths.EnumerateArray().Select(e => e.GetInt32()));
        Assert.True(offered!.Single(t => t.Name == "calendar_availability").Parameters
            .GetProperty("properties").TryGetProperty("duration_minutes", out _));

        // ONE row covering 9:00–10:30, ONE Google event with the same end — three units, one booking
        var booking = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal(BookingRig.Kul(13, 9), BookingAvailability.Utc(booking.StartUtc));
        Assert.Equal(BookingRig.Kul(13, 10, 30), BookingAvailability.Utc(booking.EndUtc));
        var ev = JsonDocument.Parse(Assert.Single(r.Google.Inserts).Body).RootElement;
        Assert.Equal(GoogleCalendarClient.Iso(BookingRig.Kul(13, 9)), ev.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal(GoogleCalendarClient.Iso(BookingRig.Kul(13, 10, 30)), ev.GetProperty("end").GetProperty("dateTime").GetString());

        // the reply and the note both restate the range
        var msgs = r.Messages("60123000001");
        Assert.Contains("Tue 13 Oct, 9:00–10:30 AM", Assert.Single(msgs, m => m.Source == MessageSource.Ai).Text);
        Assert.Contains("Tue 13 Oct, 9:00–10:30 AM", Assert.Single(msgs, m => m.Private).Text);
    }

    [Fact]
    public async Task My_bookings_and_cancel_use_the_stored_length()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0, duration_unit_minutes = 30, max_units = 4 });
        var ctx = new SkillContext { ContactId = c.Id };
        var reserved = await Tool(r, skill, "book", new { start = "2026-10-14T14:00", duration_minutes = 120 }, ctx);
        Assert.True(reserved.GetProperty("ok").GetBoolean(), reserved.ToString());
        Assert.Equal("2026-10-14T16:00", reserved.GetProperty("end").GetString());
        Assert.Equal(120, reserved.GetProperty("duration_minutes").GetInt32());
        var row = Assert.Single(r.FreshBookings());
        Assert.Equal(TimeSpan.FromHours(2), row.EndUtc - row.StartUtc);

        // another conversation's search sees the whole two hours taken (a pending row holds them)
        var after = await Availability(r, skill, new { from_date = "2026-10-14", to_date = "2026-10-14" });
        var starts = Starts(after);
        Assert.Contains("2026-10-14T13:30", starts);
        Assert.DoesNotContain("2026-10-14T15:30", starts);
        Assert.Contains("2026-10-14T16:00", starts);

        var mine = await Tool(r, skill, "my_bookings", new { }, new SkillContext { ContactId = c.Id });
        var listed = mine.GetProperty("bookings")[0];
        Assert.Equal("2026-10-14T16:00", listed.GetProperty("end").GetString());
        Assert.Equal(120, listed.GetProperty("duration_minutes").GetInt32());
        Assert.Equal("Wed 14 Oct, 2:00–4:00 PM", listed.GetProperty("range").GetString());

        // once booked, a cancellation names the stored span too
        var stored = r.Db.Bookings.Single(x => x.Id == row.Id);
        stored.Status = BookingStatus.Booked;
        r.Db.SaveChanges();
        var cancel = await Tool(r, skill, "cancel", new { booking_id = row.Id }, new SkillContext { ContactId = c.Id });
        Assert.True(cancel.GetProperty("ok").GetBoolean(), cancel.ToString());
        Assert.Equal("2026-10-14T16:00", cancel.GetProperty("end").GetString());
    }

    [Fact]
    public void An_older_config_without_the_length_keys_behaves_as_before()
    {
        var old = Cfg(new { duration_minutes = 45 });
        Assert.Equal(45, old.DurationUnitMinutes);
        Assert.Equal(1, old.MaxUnits);
        Assert.Equal(new[] { 45 }, old.Lengths);
        Assert.False(old.VariableLength);
        Assert.Equal(45, old.ToDictionary()["duration_unit_minutes"]);
        Assert.Equal(1, old.ToDictionary()["max_units"]);
        // a stale unit on a fixed length is ignored, never an error
        Assert.Equal(45, Cfg(new { duration_minutes = 45, duration_unit_minutes = 30 }).DurationUnitMinutes);
        Assert.Equal(new[] { 30 }, Cfg(new { }).Lengths);

        // the same tools as before: a fixed length, no duration_minutes argument
        var tools = BookingService.ToolDefinitions("calendar", old, "Asia/Kuala_Lumpur").ToList();
        var avail = tools.Single(t => t.Action == "availability").Tool;
        Assert.Contains("45-minute", avail.Description);
        Assert.DoesNotContain("duration_minutes", avail.Parameters.GetRawText());
        Assert.DoesNotContain("duration_minutes", tools.Single(t => t.Action == "book").Tool.Parameters.GetRawText());

        // the same slots with and without the default length passed explicitly
        var day = new DateOnly(2026, 10, 13);
        var implicitLength = BookingAvailability.FreeSlots(old, BookingRig.Kl, BookingRig.Start.UtcDateTime, day, day,
            Array.Empty<(DateTime, DateTime)>());
        var explicitLength = BookingAvailability.FreeSlots(old, BookingRig.Kl, BookingRig.Start.UtcDateTime, day, day,
            Array.Empty<(DateTime, DateTime)>(), durationMinutes: 45);
        Assert.Equal(implicitLength.Select(s => s.StartUtc), explicitLength.Select(s => s.StartUtc));

        // variable lengths validate: whole units, a default within the longest, units of 15 / 30 / 60
        string? Problem(object raw)
        {
            CalendarAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
            return error;
        }
        Assert.Contains("whole number of 30-minute units",
            Problem(new { duration_minutes = 45, duration_unit_minutes = 30, max_units = 4 }));
        Assert.Contains("longer than the longest booking",
            Problem(new { duration_minutes = 120, duration_unit_minutes = 30, max_units = 2 }));
        Assert.Contains("15, 30 or 60", Problem(new { duration_unit_minutes = 20, max_units = 3 }));
        Assert.Contains("max_units", Problem(new { max_units = 17 }));
        Assert.Null(Problem(new { duration_minutes = 60, duration_unit_minutes = 15, max_units = 16 }));
        // a non-standard length may still grow by itself: 45 / 90 / 135
        Assert.Equal(new[] { 45, 90, 135 }, Cfg(new { duration_minutes = 45, max_units = 3 }).Lengths);
    }

    [Fact]
    public async Task A_playground_preview_of_a_longer_booking_holds_nothing()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0, duration_unit_minutes = 60, max_units = 3, duration_minutes = 60 });
        var ctx = new SkillContext { ContactId = c.Id, Apps = new BookingService.Session { Preview = true } };
        var preview = await Tool(r, skill, "book", new { start = "2026-10-13T10:00", duration_minutes = 180 }, ctx);
        Assert.True(preview.GetProperty("ok").GetBoolean(), preview.ToString());
        Assert.Equal("2026-10-13T13:00", preview.GetProperty("end").GetString());
        var shown = Assert.Single(ctx.Apps.Previews);
        Assert.Equal("Tue 13 Oct, 10:00 AM–1:00 PM", shown.Label);
        Assert.Empty(ctx.Apps.Actions);
        Assert.Empty(r.FreshBookings());
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

    // ---- rescheduling: the customer's own booking is not "busy", and a swap is all-or-nothing --------

    /// <summary>A confirmed booking of this skill's calendar (Google: with its own event).</summary>
    private static Booking OwnBooking(BookingRig r, int contactId, AgentSkill skill, DateTime startUtc, DateTime endUtc,
        string eventId = "evt-old")
    {
        var cfg = CalendarAppConfig.From(skill.Config);
        var google = cfg.Source == BookingSources.Google;
        var row = new Booking
        {
            ContactId = contactId, SkillId = skill.SkillId,
            CalendarKey = r.Bookings.ResolveTarget(skill, cfg, null, null).Key,
            Source = google ? BookingSources.Google : BookingSources.ChatBranch,
            IntegrationId = google ? r.Db.Integrations.Single().Id : null, ExternalEventId = google ? eventId : "",
            Status = BookingStatus.Booked, StartUtc = startUtc, EndUtc = endUtc, Timezone = "Asia/Kuala_Lumpur",
            Title = "Aisha Rahman — appointment", CalendarLabel = google ? "Google Calendar" : "ChatBranch bookings",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Bookings.Add(row);
        r.Db.SaveChanges();
        return row;
    }

    /// <summary><see cref="Hourly"/> with a per-customer limit.</summary>
    private static object HourlyLimit(int limit) => new
    {
        source = "google", calendar = "shared", min_notice_minutes = 0,
        duration_minutes = 30, duration_unit_minutes = 30, max_units = 4, max_active_per_contact = limit,
    };

    private static List<JsonElement> ToolResults(BookingRig r) => r.Llm.Seen.Last().Messages
        .Where(m => m.Role == "tool").Select(m => JsonDocument.Parse(m.Content!).RootElement.Clone()).ToList();

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]     // Google free/busy also reports the old booking's own event
    [InlineData(false, true, 1)]    // book first: refused by the one-booking limit → cancel → book
    [InlineData(false, true, 2)]    // book first: refused "your own booking is in the way" → cancel → book
    public void A_reschedule_into_its_own_time_swaps_the_bookings_in_one_reply(bool cancelFirst, bool googleShowsOld,
        int limit)
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var skill = r.Skill(HourlyLimit(limit));
        var old = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        if (googleShowsOld) r.Google.Busy.Add((BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30)));
        var cancel = ("calendar_cancel", $"{{\"booking_id\":{old.Id}}}");
        var book = ("calendar_book", "{\"start\":\"2026-10-13T14:00\",\"duration_minutes\":60}");
        r.Llm.Script = (msgs, _) =>
        {
            var tools = msgs.Where(m => m.Role == "tool").ToList();
            if (tools.Count == 0) return cancelFirst ? ToolScriptLlm.Calls(cancel, book) : ToolScriptLlm.Calls(book);
            if (!cancelFirst && tools.Count == 1) return ToolScriptLlm.Calls(cancel, book);
            var last = JsonDocument.Parse(tools[^1].Content!).RootElement;
            return last.TryGetProperty("status", out var st) && st.GetString() == "reserved"
                ? ToolScriptLlm.Says($"Done — you're now booked for {last.GetProperty("label").GetString()} (Asia/Kuala_Lumpur).")
                : ToolScriptLlm.Says("Sorry — a teammate will confirm a time with you.");
        };

        r.Say("60123000001", "Can I make my 2pm on Tuesday an hour instead?");

        if (!cancelFirst)
        {
            var refused = ToolResults(r)[0];
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Contains(limit == 1 ? "the limit is 1" : $"own booking {old.Id}", refused.GetProperty("error").GetString());
            Assert.Contains($"_cancel for booking {old.Id}", refused.GetProperty("advice").GetString());
            // the cancel that follows says it needs the new booking
            Assert.True(ToolResults(r)[1].GetProperty("requires_new_booking").GetBoolean());
        }
        var rows = r.FreshBookings();
        Assert.Equal(BookingStatus.Cancelled, rows.Single(b => b.Id == old.Id).Status);
        var made = rows.Single(b => b.Id != old.Id);
        Assert.Equal(BookingStatus.Booked, made.Status);
        Assert.Equal(BookingRig.Kul(13, 14), BookingAvailability.Utc(made.StartUtc));
        Assert.Equal(BookingRig.Kul(13, 15), BookingAvailability.Utc(made.EndUtc));
        // the Google event is replaced: one new event, the old one deleted
        var ev = JsonDocument.Parse(Assert.Single(r.Google.Inserts).Body).RootElement;
        Assert.Equal(GoogleCalendarClient.Iso(BookingRig.Kul(13, 15)), ev.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.EndsWith("/events/evt-old", Assert.Single(r.Google.Deletes).Path);
        Assert.Equal(AiDraftStatus.AutoSent, r.Drafts().Single().Status);
        Assert.Contains("Tue 13 Oct, 2:00–3:00 PM",
            r.Messages("60123000001").Single(m => m.Source == MessageSource.Ai).Text);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]   // book first (the time is busy), then the cancel is refused
    [InlineData(false, 1)]   // book first (the limit), cancel, never book again → the cancel is given back
    public void A_reschedule_to_a_busy_time_keeps_the_old_booking(bool cancelFirst, int limit)
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var skill = r.Skill(HourlyLimit(limit));
        var old = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        r.Google.Busy.Add((BookingRig.Kul(13, 16), BookingRig.Kul(13, 17)));   // someone else's 4–5 PM
        var cancel = ("calendar_cancel", $"{{\"booking_id\":{old.Id}}}");
        var book = ("calendar_book", "{\"start\":\"2026-10-13T16:00\",\"duration_minutes\":60}");
        r.Llm.Script = (msgs, _) => msgs.Count(m => m.Role == "tool") switch
        {
            0 => cancelFirst ? ToolScriptLlm.Calls(cancel, book) : ToolScriptLlm.Calls(book),
            1 when !cancelFirst => ToolScriptLlm.Calls(cancel),
            _ => ToolScriptLlm.Says("Sorry, 4 PM is taken — your 2:00 PM appointment stays. Would 11 AM suit you?"),
        };

        r.Say("60123000001", "Please move my 2pm on Tuesday to 4pm, an hour");

        var results = ToolResults(r);
        if (cancelFirst)
        {
            // the book's refusal says the old booking was kept
            var refused = results[1];
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Contains($"The old booking ({old.Id}) was kept because the new time could not be booked.",
                refused.GetProperty("error").GetString());
            Assert.Equal(old.Id, refused.GetProperty("old_booking_kept").GetInt32());
        }
        else if (limit == 2)
        {
            var cancelRefused = results[1];
            Assert.False(cancelRefused.GetProperty("ok").GetBoolean());
            Assert.Contains($"Booking {old.Id} was kept", cancelRefused.GetProperty("error").GetString());
        }
        else
        {
            // the cancel was accepted only as part of a reschedule — no new booking, so it was given back
            Assert.True(results[1].GetProperty("requires_new_booking").GetBoolean());
        }
        // nothing changed: the old booking stands, no Google insert or delete, nothing to deliver
        var row = Assert.Single(r.FreshBookings());
        Assert.Equal(BookingStatus.Booked, row.Status);
        Assert.Null(row.CancelDraftId);
        Assert.Empty(r.Google.Inserts);
        Assert.Empty(r.Google.Deletes);
        Assert.Empty(AiPendingAction.From(r.Drafts().Single().Context));
        Assert.DoesNotContain(r.Messages("60123000001"), m => m.Private && m.Text.StartsWith("📅 Cancelled"));
    }

    [Fact]
    public void A_new_time_taken_before_delivery_keeps_the_old_booking_too()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var skill = r.Skill(Hourly);
        var old = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        r.Set("ai_review_mode", "review_all");
        r.Llm.Script = (msgs, _) => msgs.Count(m => m.Role == "tool") == 0
            ? ToolScriptLlm.Calls(("calendar_cancel", $"{{\"booking_id\":{old.Id}}}"),
                ("calendar_book", "{\"start\":\"2026-10-13T16:00\",\"duration_minutes\":60}"))
            : ToolScriptLlm.Says("Moved to Tue 13 Oct, 4:00–5:00 PM (Asia/Kuala_Lumpur).");

        r.Say("60123000001", "Please move my 2pm on Tuesday to 4pm, an hour");
        var draft = r.Drafts().Single();
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.Equal(BookingStatus.Cancelling, r.FreshBookings().Single(b => b.Id == old.Id).Status);

        // the new time is taken in Google while the reply waits for review
        r.Google.Busy.Add((BookingRig.Kul(13, 16), BookingRig.Kul(13, 17)));
        Assert.Equal(AiGateStatus.Blocked, r.Gate.Approve(draft.Id, "admin").Status);

        var rows = r.FreshBookings();
        Assert.Equal(BookingStatus.Booked, rows.Single(b => b.Id == old.Id).Status);   // the cancel was released
        Assert.Equal(BookingStatus.Failed, rows.Single(b => b.Id != old.Id).Status);
        Assert.Empty(r.Google.Inserts);
        Assert.Empty(r.Google.Deletes);
        var note = Assert.Single(r.Messages("60123000001"), m => m.Private && m.Text.StartsWith("📅 Booking failed"));
        Assert.Contains("The booking it was replacing", note.Text);
    }

    [Fact]
    public async Task Ignore_booking_id_frees_only_the_customers_own_booking()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var other = r.Contact("Ben Tan", "60123000002", "ben@example.com");
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0 });
        var mine = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        var theirs = OwnBooking(r, other.Id, skill, BookingRig.Kul(13, 15), BookingRig.Kul(13, 15, 30));
        var ctx = () => new SkillContext { ContactId = c.Id };
        var day = new { from_date = "2026-10-13", to_date = "2026-10-13" };

        var plain = await Tool(r, skill, "availability", day, ctx());
        Assert.DoesNotContain("2026-10-13T14:00", Starts(plain));

        var ignoring = await Tool(r, skill, "availability",
            new { from_date = "2026-10-13", to_date = "2026-10-13", ignore_booking_id = mine.Id }, ctx());
        Assert.True(ignoring.GetProperty("ok").GetBoolean(), ignoring.ToString());
        Assert.Contains("2026-10-13T14:00", Starts(ignoring));
        Assert.DoesNotContain("2026-10-13T15:00", Starts(ignoring));             // another customer's stays busy
        Assert.Equal(mine.Id, ignoring.GetProperty("ignored_booking_id").GetInt32());
        var exact = await Tool(r, skill, "availability",
            new { from_date = "2026-10-13", time = "14:00", ignore_booking_id = mine.Id }, ctx());
        Assert.True(exact.GetProperty("requested").GetProperty("available").GetBoolean());

        var refused = await Tool(r, skill, "availability",
            new { from_date = "2026-10-13", ignore_booking_id = theirs.Id }, ctx());
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("isn't one of this customer's upcoming bookings", refused.GetProperty("error").GetString());
        Assert.False((await Tool(r, skill, "availability", new { ignore_booking_id = "abc" }, ctx()))
            .GetProperty("ok").GetBoolean());

        // the per-customer limit's refusal points at it
        var limited = await Tool(r, skill, "book", new { start = "2026-10-14T10:00" }, ctx());
        Assert.Contains($"ignore_booking_id = {mine.Id}", limited.GetProperty("advice").GetString());
    }

    [Fact]
    public void A_plain_cancel_still_cancels()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.GoogleRow();
        var skill = r.Skill(Hourly);
        var old = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        r.Llm.Script = (msgs, _) => msgs.Count(m => m.Role == "tool") == 0
            ? ToolScriptLlm.Calls(("calendar_cancel", $"{{\"booking_id\":{old.Id}}}"))
            : ToolScriptLlm.Says("Your appointment on Tue 13 Oct, 2:00 PM is cancelled.");

        r.Say("60123000001", "Please cancel my 2pm on Tuesday");

        Assert.Equal(BookingStatus.Cancelled, Assert.Single(r.FreshBookings()).Status);
        Assert.EndsWith("/events/evt-old", Assert.Single(r.Google.Deletes).Path);
        Assert.Empty(r.Google.Inserts);
    }

    [Fact]
    public async Task A_playground_reschedule_previews_the_swap_and_touches_nothing()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        var skill = r.Skill(new { source = "chatbranch", min_notice_minutes = 0, duration_unit_minutes = 30, max_units = 4 });
        var old = OwnBooking(r, c.Id, skill, BookingRig.Kul(13, 14), BookingRig.Kul(13, 14, 30));
        var ctx = new SkillContext { ContactId = c.Id, Apps = new BookingService.Session { Preview = true } };

        Assert.True((await Tool(r, skill, "cancel", new { booking_id = old.Id }, ctx)).GetProperty("ok").GetBoolean());
        // the booking still reads "booked" in a preview, yet the limit and the slot both treat it as replaced
        var swapped = await Tool(r, skill, "book", new { start = "2026-10-13T14:00", duration_minutes = 60 }, ctx);
        Assert.True(swapped.GetProperty("ok").GetBoolean(), swapped.ToString());
        Assert.Equal(new[] { "cancel", "book" }, ctx.Apps.Previews.Select(p => p.Type));
        Assert.Equal(BookingStatus.Booked, Assert.Single(r.FreshBookings()).Status);
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
        Assert.Equal(45, cfg.GetProperty("duration_unit_minutes").GetInt32());   // one fixed length, as before
        Assert.Equal(1, cfg.GetProperty("max_units").GetInt32());

        // variable length: validated, then stored canonically
        Assert.Contains("whole number of 30-minute units",
            await Detail(await Put(new { duration_minutes = 45, duration_unit_minutes = 30, max_units = 3 })));
        var variable = await Put(new { source = "chatbranch", duration_minutes = 60, duration_unit_minutes = 30, max_units = 4 });
        Assert.Equal(HttpStatusCode.OK, variable.StatusCode);
        var vcfg = (await variable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "cal-east").GetProperty("config");
        Assert.Equal(30, vcfg.GetProperty("duration_unit_minutes").GetInt32());
        Assert.Equal(4, vcfg.GetProperty("max_units").GetInt32());
        Assert.Equal(60, vcfg.GetProperty("duration_minutes").GetInt32());
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
        var cal = apps.EnumerateArray().First(); // calendar first, then documents, qr, payments, sheets
        Assert.Equal("calendar", cal.GetProperty("app").GetString());
        Assert.Equal(3, cal.GetProperty("actions").GetArrayLength());
        Assert.Equal(30, cal.GetProperty("defaults").GetProperty("duration_minutes").GetInt32());
        var google = cal.GetProperty("sources")[0];
        Assert.True(google.GetProperty("connected").GetBoolean());
        Assert.Equal(3, google.GetProperty("connections").GetArrayLength());   // admins see every personal calendar
        Assert.True(cal.GetProperty("sources")[1].GetProperty("connected").GetBoolean());

        var agent = await Authed("agent", "agent123");
        // the catalog is the Skills page's (settings.ai.manage) — a plain agent is refused…
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/aicore/skills/apps")).StatusCode);
        // …and an agent-level curator holding it sees the shared calendar + their own,
        // never the admin's personal one
        Db(db =>
        {
            db.RolePermissions.Add(new RolePermission
                { Role = Roles.Agent, Capability = "settings.ai.manage", Allowed = true, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        try
        {
            var agentConns = (await agent.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps"))
                .GetProperty("apps")[0].GetProperty("sources")[0].GetProperty("connections");
            Assert.Equal(2, agentConns.GetArrayLength());
            Assert.Contains(agentConns.EnumerateArray(), c => c.GetProperty("owner").GetString() == "org" && c.GetProperty("id").GetInt32() == shared);
            Assert.DoesNotContain(agentConns.EnumerateArray(), c => c.GetProperty("owner_name").GetString() == "admin");
        }
        finally
        {
            Db(db =>
            {
                db.RolePermissions.RemoveRange(db.RolePermissions.Where(p => p.Role == Roles.Agent
                                                                         && p.Capability == "settings.ai.manage"));
                return db.SaveChanges();
            });
        }

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
        Assert.Equal(30, cal.GetProperty("defaults").GetProperty("duration_unit_minutes").GetInt32());
        Assert.Equal(1, cal.GetProperty("defaults").GetProperty("max_units").GetInt32());

        // a calendar that books 1–3 hours previews too (at its default length)
        var longer = await (await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "app", app_action = "availability",
            config = new { source = "chatbranch", hours = everyDay, min_notice_minutes = 0,
                           duration_minutes = 60, duration_unit_minutes = 60, max_units = 3 },
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(longer.GetProperty("ok").GetBoolean(), longer.ToString());
        var firstSlot = longer.GetProperty("slots")[0];
        Assert.Equal(TimeSpan.FromHours(1),
            DateTime.Parse(firstSlot.GetProperty("end").GetString()!) - DateTime.Parse(firstSlot.GetProperty("start").GetString()!));
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
