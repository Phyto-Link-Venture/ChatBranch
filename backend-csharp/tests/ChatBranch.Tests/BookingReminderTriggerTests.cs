using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The journey trigger "Before a booking" (FlowEngine.Bookings.cs): N minutes /
/// hours / days before a ChatBranch booking, one run PER BOOKING — the window
/// (opens at start − lead, closes at the start), a booking made inside its window,
/// a journey armed after the window opened, per-booking dedupe, reschedules,
/// cancellations (before and after the run started), the calendar filter, the
/// journey's entity scope, the booking's own chat and the {{booking_…}} variables.
/// The engine reads the real clock, so "time passes" by moving every stored time
/// back (<see cref="Later"/>).
/// </summary>
public class BookingReminderTriggerTests
{
    private const string Kl = "Asia/Kuala_Lumpur";

    private static FlowEngine Engine(AppDbContext db)
    {
        var scoping = new ScopingService(db);
        var threads = new ThreadStore(db, scoping);
        var assistant = new AssistantService(db, scoping, new PersonaService(scoping),
            new KnowledgeService(db, scoping), new SandboxLlmClient());
        var sender = new OutboundSender(db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        return new FlowEngine(db, scoping, new QueueService(db, scoping, threads, assistant, sender));
    }

    private static object N(string id, string type, object config) =>
        new { id, type, data = new { label = id, config } };

    private static object E(string id, string source, string target) =>
        new { id, source, target, data = new { config = new { timing = "Immediately" } } };

    private static Dictionary<string, object?> Trigger(int amount = 1, string unit = "hours", string calendar = "") => new()
    {
        ["when"] = FlowEngine.WhenBookingStarts, ["amount"] = amount, ["unit"] = unit,
        ["calendar"] = calendar, ["repeat"] = "Every time",
    };

    /// <summary>trigger → send a WhatsApp reminder → (optional steps) → end.</summary>
    private static Workflow Journey(TestDb t, Dictionary<string, object?>? trigger = null, object? send = null,
        int? entityId = null, bool waitFirst = false, bool waitForReply = false)
    {
        var nodes = new List<object?> { N("trg", "trigger", trigger ?? Trigger()) };
        var edges = new List<object?>();
        var from = "trg";
        if (waitFirst)
        {
            nodes.Add(N("wait", "wait", new { mode = "time", amount = 30, unit = "minutes" }));
            edges.Add(E("ew", from, "wait"));
            from = "wait";
        }
        nodes.Add(N("send", "action", send ?? new
        {
            channels = new[] { "WhatsApp" }, waMode = "text",
            script = "Hi {{first_name}}, see you {{booking_time}} ({{booking_length}}).",
        }));
        edges.Add(E("es", from, "send"));
        from = "send";
        if (waitForReply)
        {
            nodes.Add(N("reply", "wait", new { mode = "reply", timeoutAmount = 0, timeoutUnit = "days" }));
            edges.Add(E("er", from, "reply"));
            from = "reply";
        }
        nodes.Add(N("end", "end", new { logCode = "DONE" }));
        edges.Add(E("ee", from, "end"));
        var wf = new Workflow
        {
            FlowId = "wf-remind-" + Guid.NewGuid().ToString("n")[..8], Name = "Reminder", Active = true,
            LiveSince = DateTime.UtcNow.AddDays(-2), EntityId = entityId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Nodes = nodes, Edges = edges,
        };
        t.Db.Workflows.Add(wf);
        t.Db.SaveChanges();
        return wf;
    }

    private static Contact AddContact(TestDb t, string name = "Aisha Rahman", string phone = "+60111111111")
    {
        var c = new Contact
        {
            Name = name, Phone = phone, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@example.com",
            Language = "en", Status = "active", CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Contacts.Add(c);
        t.Db.SaveChanges();
        return c;
    }

    private static Thread AddThread(TestDb t, Contact c, string key, string channel = "whatsapp", int? entityId = null)
    {
        var th = new Thread
        {
            Channel = channel, Key = key, ContactId = c.Id, EntityId = entityId, CompanyEntityId = entityId,
            Status = "open", CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Threads.Add(th);
        t.Db.SaveChanges();
        return th;
    }

    /// <summary>A booked appointment <paramref name="inMinutes"/> from now (made yesterday unless said).</summary>
    private static Booking Book(TestDb t, Contact c, double inMinutes, int length = 45, string skill = "cal-1",
        int? threadId = null, int? entityId = null, DateTime? createdAt = null, string status = BookingStatus.Booked)
    {
        var start = Minute(DateTime.UtcNow.AddMinutes(inMinutes));
        var b = new Booking
        {
            ContactId = c.Id, ThreadId = threadId, EntityId = entityId, SkillId = skill, Status = status,
            CalendarKey = "chatbranch:global", CalendarLabel = "ChatBranch bookings", Timezone = Kl,
            EventLink = "https://calendar.example.com/e/1",
            StartUtc = start, EndUtc = start.AddMinutes(length),
            CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Bookings.Add(b);
        t.Db.SaveChanges();
        return b;
    }

    /// <summary>Whole seconds (what the run stores as booking_start).</summary>
    private static DateTime Minute(DateTime d) => new(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>Let <paramref name="minutes"/> pass: every stored time moves back by that much.</summary>
    private static void Later(TestDb t, double minutes)
    {
        var d = TimeSpan.FromMinutes(minutes);
        foreach (var b in t.Db.Bookings.ToList())
        {
            b.StartUtc -= d; b.EndUtc -= d; b.CreatedAt -= d;
        }
        foreach (var wf in t.Db.Workflows.ToList())
            if (wf.LiveSince is DateTime live) wf.LiveSince = live - d;
        foreach (var r in t.Db.FlowRuns.ToList())
        {
            r.StartedAt -= d;
            if (r.WaitUntil is DateTime w) r.WaitUntil = w - d;
            // the start the run remembers moves with its booking (the clock moved, not the booking)
            if (DateTime.TryParse(CtxOf(r, "booking_start"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var s))
                r.Context = new Dictionary<string, object?>(r.Context) { ["booking_start"] = FlowEngine.BookingStartStamp(s - d) };
        }
        t.Db.SaveChanges();
    }

    private static List<FlowRun> Runs(TestDb t, Workflow wf) =>
        t.Db.FlowRuns.Where(r => r.WorkflowId == wf.Id).OrderBy(r => r.Id).ToList();

    private static string CtxOf(FlowRun r, string key) =>
        r.Context.TryGetValue(key, out var v) && v != null
            ? v is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString()) : v.ToString() ?? ""
            : "";

    private static List<OutboundJob> Jobs(TestDb t, FlowRun r) =>
        t.Db.OutboundJobs.Where(j => j.FlowRunId == r.Id).ToList();

    private static string Text(OutboundJob j) =>
        j.Payload.TryGetValue("text", out var v) && v != null
            ? v is JsonElement je ? je.GetString() ?? "" : v.ToString() ?? ""
            : "";

    // ---- the window --------------------------------------------------------------------

    [Fact]
    public void Fires_when_the_window_opens_and_never_after_the_start()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t);
        var b = Book(t, c, 61);                       // 13:59 — the 15:00 booking is 61 minutes away
        var engine = Engine(t.Db);

        Assert.Equal(0, engine.EvaluateTriggers());
        Assert.Empty(Runs(t, wf));

        Later(t, 1.5);                                // 14:00 — the window has opened
        Assert.Equal(1, engine.EvaluateTriggers());
        var run = Assert.Single(Runs(t, wf));
        Assert.Equal(c.Id, run.ContactId);
        Assert.Equal(b.Id.ToString(), CtxOf(run, "booking_id"));
        Assert.Equal(FlowEngine.BookingStartStamp(t.Db.Bookings.Single().StartUtc), CtxOf(run, "booking_start"));
        Assert.Single(Jobs(t, run));
        Assert.Contains(run.Log, l => JsonSerializer.Serialize(l).Contains("1 hours before a booking"));

        // a booking the engine only looks at after it started gets nothing
        var late = AddContact(t, "Ben Lim", "+60133333333");
        Book(t, late, 61);
        Later(t, 62);
        Assert.Equal(0, engine.EvaluateTriggers());
        Assert.Single(Runs(t, wf));
    }

    [Fact]
    public void Each_booking_gets_its_own_reminder_and_only_one()
    {
        using var t = new TestDb();
        var wf = Journey(t, Trigger(6, "hours"));
        var c = AddContact(t);
        var morning = Book(t, c, 60);                 // 10:00 and 15:00, both inside 6 hours
        var afternoon = Book(t, c, 300);
        var engine = Engine(t.Db);

        Assert.Equal(2, engine.EvaluateTriggers());
        Assert.Equal(0, engine.EvaluateTriggers());   // a second tick inside the window: nothing new
        Later(t, 10);
        Assert.Equal(0, engine.EvaluateTriggers());

        var runs = Runs(t, wf);
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { morning.Id.ToString(), afternoon.Id.ToString() },
            runs.Select(r => CtxOf(r, "booking_id")).OrderBy(x => x).ToArray());
        Assert.All(runs, r => Assert.Equal(FlowRunStatus.Done, r.Status));
    }

    [Fact]
    public void A_booking_made_inside_its_window_gets_no_reminder()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t);
        // made at 14:30 for 15:00: "your appointment is in 1 hour" would be wrong
        Book(t, c, 30, createdAt: DateTime.UtcNow);
        Assert.Equal(0, Engine(t.Db).EvaluateTriggers());
        Assert.Empty(Runs(t, wf));
    }

    [Fact]
    public void A_journey_armed_after_the_window_opened_never_back_fires()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t);
        Book(t, c, 30);                               // the window opened 30 minutes ago
        wf.LiveSince = DateTime.UtcNow;               // armed just now
        t.Db.SaveChanges();
        var engine = Engine(t.Db);
        Assert.Equal(0, engine.EvaluateTriggers());

        var next = Book(t, c, 90);                    // the next booking's window opens after the arming
        Later(t, 31);
        Assert.Equal(1, engine.EvaluateTriggers());
        Assert.Equal(next.Id.ToString(), CtxOf(Assert.Single(Runs(t, wf)), "booking_id"));
    }

    // ---- cancellations + reschedules -----------------------------------------------------

    [Fact]
    public void A_cancelled_booking_gets_no_reminder()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t);
        Book(t, c, 30, status: BookingStatus.Cancelled);
        Book(t, c, 40, status: BookingStatus.Pending);   // not made yet
        Assert.Equal(0, Engine(t.Db).EvaluateTriggers());
        Assert.Empty(Runs(t, wf));
    }

    [Fact]
    public void Cancelling_while_the_run_waits_ends_it_and_sends_nothing()
    {
        using var t = new TestDb();
        var wf = Journey(t, waitFirst: true);         // trigger → wait 30 min → send
        var c = AddContact(t);
        var b = Book(t, c, 50);
        var engine = Engine(t.Db);
        Assert.Equal(1, engine.EvaluateTriggers());
        var run = Assert.Single(Runs(t, wf));
        Assert.Equal(FlowRunStatus.Waiting, run.Status);

        b.Status = BookingStatus.Cancelled;
        t.Db.SaveChanges();
        Later(t, 31);                                 // the wait is over
        engine.ResumeDueRuns();

        t.Db.Entry(run).Reload();
        Assert.Equal(FlowRunStatus.Cancelled, run.Status);
        Assert.Equal("booking cancelled", run.Detail);
        Assert.Empty(Jobs(t, run));
        Assert.Contains(run.Log, l => JsonSerializer.Serialize(l).Contains("booking cancelled"));
    }

    [Fact]
    public void Cancelling_ends_a_run_waiting_for_a_reply_at_the_next_tick()
    {
        using var t = new TestDb();
        var wf = Journey(t, waitForReply: true);      // trigger → send → wait for a reply (no timeout)
        var c = AddContact(t);
        var b = Book(t, c, 50);
        var engine = Engine(t.Db);
        engine.EvaluateTriggers();
        var run = Assert.Single(Runs(t, wf));
        Assert.Equal(FlowRunStatus.Waiting, run.Status);
        Assert.Equal("reply", run.WaitFor);

        b.Status = BookingStatus.Cancelled;
        t.Db.SaveChanges();
        engine.EvaluateTriggers();
        t.Db.Entry(run).Reload();
        Assert.Equal(FlowRunStatus.Cancelled, run.Status);
        Assert.Equal("booking cancelled", run.Detail);
    }

    [Fact]
    public void A_rescheduled_booking_gets_a_new_reminder_for_its_new_time()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t);
        var old = Book(t, c, 59);                     // 14:01: reminded for 15:00
        var engine = Engine(t.Db);
        Assert.Equal(1, engine.EvaluateTriggers());

        // 14:10: moved to 17:00 — the reschedule cancels it and books a new one
        Later(t, 9);
        old.Status = BookingStatus.Cancelled;
        var moved = Book(t, c, 170, createdAt: DateTime.UtcNow);
        Assert.Equal(0, engine.EvaluateTriggers());   // 17:00 is not an hour away yet

        Later(t, 111);                                // 16:01
        Assert.Equal(1, engine.EvaluateTriggers());
        var runs = Runs(t, wf);
        Assert.Equal(2, runs.Count);
        Assert.Equal(moved.Id.ToString(), CtxOf(runs[1], "booking_id"));
        Assert.Single(Jobs(t, runs[1]));
    }

    [Fact]
    public void A_booking_moved_in_place_ends_the_old_run_and_reminds_for_the_new_time()
    {
        using var t = new TestDb();
        var wf = Journey(t, Trigger(2, "hours"), waitForReply: true);
        var c = AddContact(t);
        var b = Book(t, c, 100);
        var engine = Engine(t.Db);
        Assert.Equal(1, engine.EvaluateTriggers());

        b.StartUtc = b.StartUtc.AddMinutes(15);       // the same row, a later start
        b.EndUtc = b.EndUtc.AddMinutes(15);
        t.Db.SaveChanges();
        Assert.Equal(1, engine.EvaluateTriggers());

        var runs = Runs(t, wf);
        Assert.Equal(2, runs.Count);
        Assert.Equal(FlowRunStatus.Cancelled, runs[0].Status);
        Assert.Equal("booking moved to another time", runs[0].Detail);
        Assert.Equal(FlowEngine.BookingStartStamp(b.StartUtc), CtxOf(runs[1], "booking_start"));
        Assert.Equal(FlowRunStatus.Waiting, runs[1].Status);
    }

    // ---- which bookings ------------------------------------------------------------------

    [Fact]
    public void The_calendar_filter_only_fires_for_that_skills_bookings()
    {
        using var t = new TestDb();
        var wf = Journey(t, Trigger(calendar: "cal-dentist"));
        var c = AddContact(t);
        var dentist = Book(t, c, 30, skill: "cal-dentist");
        Book(t, c, 40, skill: "cal-spa");
        Assert.Equal(1, Engine(t.Db).EvaluateTriggers());
        Assert.Equal(dentist.Id.ToString(), CtxOf(Assert.Single(Runs(t, wf)), "booking_id"));
    }

    [Fact]
    public void An_outlet_journey_never_fires_for_another_outlets_booking()
    {
        using var t = new TestDb();
        t.Db.Tenants.Add(new Tenant { Id = 1, Name = "T", Slug = "t" });
        var mine = new Entity { Name = "Bangsar", Slug = "bangsar", TenantId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var other = new Entity { Name = "Penang", Slug = "penang", TenantId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Entities.AddRange(mine, other);
        t.Db.SaveChanges();
        var branch = new Entity { Name = "Bangsar South", Slug = "bangsar-south", TenantId = 1, ParentId = mine.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Entities.Add(branch);
        t.Db.SaveChanges();

        var wf = Journey(t, entityId: mine.Id);
        var c = AddContact(t);
        // the contact belongs to both outlets: the BOOKING's entity decides
        t.Db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = mine.Id, CreatedAt = DateTime.UtcNow });
        t.Db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = other.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var penang = Book(t, c, 20, entityId: other.Id);
        var atBranch = Book(t, c, 30, entityId: branch.Id);
        var unassigned = Book(t, c, 40);              // no entity: the contact's links decide
        var stranger = AddContact(t, "Chen Wei", "+60144444444");
        Book(t, stranger, 50);                        // no entity, contact not in the outlet

        Assert.Equal(2, Engine(t.Db).EvaluateTriggers());
        var ids = Runs(t, wf).Select(r => CtxOf(r, "booking_id")).ToHashSet();
        Assert.Equal(new HashSet<string> { atBranch.Id.ToString(), unassigned.Id.ToString() }, ids);
        Assert.DoesNotContain(penang.Id.ToString(), ids);
    }

    // ---- where it goes + what it says ----------------------------------------------------------

    [Fact]
    public void The_reminder_goes_to_the_bookings_chat_in_the_bookings_timezone()
    {
        using var t = new TestDb();
        var wf = Journey(t);
        var c = AddContact(t, phone: "+60111111111");
        AddThread(t, c, "60111111111");               // the CRM phone's chat
        var booked = AddThread(t, c, "60122222222");  // they booked from another number
        var b = Book(t, c, 45, length: 45, threadId: booked.Id);
        var engine = Engine(t.Db);
        Assert.Equal(1, engine.EvaluateTriggers());

        var run = Assert.Single(Runs(t, wf));
        Assert.Equal(booked.Id.ToString(), CtxOf(run, "trigger_thread_id"));
        var job = Assert.Single(Jobs(t, run));
        Assert.Equal("60122222222", job.ToAddress);
        var scoping = new ScopingService(t.Db);
        Assert.Equal(booked.Id, new ThreadStore(t.Db, scoping).FindForJob(job)!.Id);

        var kl = TimeZoneInfo.FindSystemTimeZoneById(Kl);
        var local = TimeZoneInfo.ConvertTimeFromUtc(b.StartUtc, kl);
        var label = local.ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Hi Aisha, see you {label} (45 min).", Text(job));

        // the queue delivers it into that chat
        engine.Tick();
        Assert.Contains(t.Db.Messages.Where(m => m.ThreadId == booked.Id && m.Direction == MessageDirection.Out).ToList(),
            m => m.Text.Contains(label));
    }

    [Fact]
    public void Booking_variables_fill_whatsapp_template_parameters()
    {
        using var t = new TestDb();
        var wf = Journey(t, send: new
        {
            channels = new[] { "WhatsApp" }, waMode = "template", templateName = "appointment_reminder",
            templateVariables = new Dictionary<string, string>
            {
                ["1"] = "{{first_name}}", ["2"] = "{{booking_date}}", ["3"] = "{{booking_start_time}}",
                ["4"] = "{{booking_end_time}}", ["5"] = "{{booking_calendar}}", ["6"] = "{{booking_link}}",
            },
        });
        var c = AddContact(t);
        var b = Book(t, c, 30, length: 90);
        Engine(t.Db).EvaluateTriggers();

        var job = Assert.Single(Jobs(t, Assert.Single(Runs(t, wf))));
        Assert.Equal(JobKind.Template, job.Kind);
        var parameters = JsonSerializer.SerializeToElement(job.Payload["parameters"]).EnumerateArray()
            .Select(p => p.GetString()).ToList();
        var kl = TimeZoneInfo.FindSystemTimeZoneById(Kl);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var start = TimeZoneInfo.ConvertTimeFromUtc(b.StartUtc, kl);
        var end = TimeZoneInfo.ConvertTimeFromUtc(b.EndUtc, kl);
        Assert.Equal(new[]
        {
            "Aisha", start.ToString("ddd d MMM yyyy", inv), start.ToString("h:mm tt", inv),
            end.ToString("h:mm tt", inv), "ChatBranch bookings", "https://calendar.example.com/e/1",
        }, parameters);
    }

    [Fact]
    public void Booking_variables_are_empty_outside_a_booking_journey()
    {
        using var t = new TestDb();
        var wf = Journey(t, trigger: new Dictionary<string, object?> { ["when"] = "Manual / API" }, send: new
        {
            channels = new[] { "WhatsApp" }, waMode = "text", script = "Hello{{booking_time}}{{booking_length}}!",
        });
        var c = AddContact(t);
        var (started, _) = Engine(t.Db).StartManual(wf, new[] { c.Id });
        Assert.Equal(1, started);
        Assert.Equal("Hello!", Text(Assert.Single(Jobs(t, Assert.Single(Runs(t, wf))))));
    }

    [Theory]
    [InlineData(30, "30 min")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(90, "1 h 30 min")]
    public void Lengths_read_naturally(int minutes, string expected) =>
        Assert.Equal(expected, FlowEngine.LengthText(minutes));

    [Theory]
    [InlineData(15, "minutes", 15)]
    [InlineData(2, "hours", 120)]
    [InlineData(1, "days", 1440)]
    [InlineData(0, "hours", 60)]          // amount below 1 counts as 1
    [InlineData(3, "", 180)]              // no unit = hours
    public void The_lead_time_follows_amount_and_unit(int amount, string unit, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes),
            FlowEngine.BookingLead(JsonSerializer.SerializeToElement(new { amount, unit })));
}
