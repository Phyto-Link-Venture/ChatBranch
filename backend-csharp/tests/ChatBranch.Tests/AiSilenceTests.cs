using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The AI went silent after a booking: a customer with an appointment (Mon 12 Oct,
/// 3:15–3:45 PM) said "Ignore the invoice things"; the AI's honest restatement ("Your
/// appointment is confirmed for Mon 12 Oct, 3:15–3:45 PM") failed the booking-claim
/// check, the whole reply was held and the AI stood down; the journey's reminder
/// handed over and stood it down again; "Let the AI continue" turned it on but the
/// customer's waiting question ("Can change the appointment time to 345-415 pm?") was
/// never answered. Covered here: the claim check accepts a restated booking, a failing
/// part is dropped instead of the whole reply (the AI stays on), "Let the AI continue"
/// answers the waiting message once, a journey hand-over can keep the AI on, and a
/// reply sees every waiting message with the every-topic rule.
/// </summary>
public class AiSilenceTests
{
    private const string Phone = "60123000001";
    private const string Incident =
        "Understood, I'll leave the invoice matters aside. Your appointment is confirmed for Mon 12 Oct, 3:15–3:45 PM";

    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static BookingClaims.KnownBooking Active(int hour = 15, int minute = 15, int length = 30) =>
        new(new DateTime(2026, 10, 12, hour, minute, 0), new DateTime(2026, 10, 12, hour, minute, 0).AddMinutes(length),
            BookingStatus.Booked, new DateTime(2026, 10, 11, 9, 0, 0));

    // ---- the claim check (pure) ----------------------------------------------------------

    [Theory]
    [InlineData(Incident)]
    [InlineData("Your appointment is confirmed for 3.15 PM.")]
    [InlineData("Your booking is confirmed for 15:15.")]
    [InlineData("Your appointment is confirmed for today at 3:15pm.")]
    [InlineData("Your slot is reserved for Monday.")]
    [InlineData("Your booking is confirmed. See you on 12 October at 3:15!")]
    public void Restating_an_active_booking_is_backed(string draft) =>
        Assert.Null(BookingClaims.Unbacked(draft, false, false, new[] { Active() }, Monday));

    [Fact]
    public void A_made_claim_with_no_active_booking_still_fails() =>
        Assert.Equal("made", BookingClaims.Unbacked(Incident, false, false, Array.Empty<BookingClaims.KnownBooking>(), Monday));

    [Theory]
    [InlineData("Your appointment is confirmed for 4:00 PM.")]
    [InlineData("Your appointment is confirmed for Mon 12 Oct, 3:45–4:15 PM.")]   // the end time is not a start
    [InlineData("Your appointment is confirmed for Tuesday at 3:15 PM.")]
    [InlineData("Your appointment is confirmed.")]                               // names nothing
    public void A_made_claim_for_another_time_fails(string draft) =>
        Assert.Equal("made", BookingClaims.Unbacked(draft, false, false, new[] { Active() }, Monday));

    [Fact]
    public void A_cancel_claim_needs_a_cancel()
    {
        Assert.Equal("cancelled", BookingClaims.Unbacked("Your 3:15 PM appointment has been cancelled.", false, false,
            new[] { Active() }, Monday));
        // this turn's cancel backs it
        Assert.Null(BookingClaims.Unbacked("Your 3:15 PM appointment has been cancelled.", false, true,
            new[] { Active() }, Monday));
        // so does a booking for that time that is already cancelled (its latest row)
        var cancelled = Active() with { Status = BookingStatus.Cancelled, UpdatedAt = new DateTime(2026, 10, 12, 1, 0, 0) };
        Assert.Null(BookingClaims.Unbacked("Your 3:15 PM appointment has been cancelled.", false, false,
            new[] { cancelled }, Monday));
        // …but not a booking for another time
        Assert.Equal("cancelled", BookingClaims.Unbacked("Your 5:00 PM appointment has been cancelled.", false, false,
            new[] { cancelled }, Monday));
    }

    [Fact]
    public void Prices_and_dates_are_not_times()
    {
        // "RM 12.50" and "12.10.2026" never count as a time that isn't the booking's
        Assert.Null(BookingClaims.Unbacked("Your appointment is confirmed for Mon 3:15 PM, the fee is RM 12.50.", false, false,
            new[] { Active() }, Monday));
        Assert.Null(BookingClaims.Unbacked("Your booking is confirmed for 3:15 PM on 12.10.2026.", false, false,
            new[] { Active() }, Monday));
    }

    [Fact]
    public void The_fix_lists_the_existing_booking()
    {
        Assert.Equal("Existing booking: Mon 12 Oct, 3:15–3:45 PM. You may say it is booked for that time; "
                     + "don't say a NEW booking was made unless you call the book tool.",
            BookingClaims.FixText(new[] { Active() }));
        Assert.Equal("Call the calendar tool first, or don't say it is done.", BookingClaims.FixText(null));
    }

    [Fact]
    public void The_failing_sentence_is_pinned_and_dropped()
    {
        var claims = BookingClaims.UnbackedClaims(
            "Sure, I'll leave the invoice aside. Your appointment has been booked for 4:00 PM. Anything else?",
            false, false, null, Monday);
        var claim = Assert.Single(claims);
        Assert.Equal("Your appointment has been booked for 4:00 PM.", claim.Sentence);
        Assert.Equal("Sure, I'll leave the invoice aside. Anything else?",
            PartialReply.Without("Sure, I'll leave the invoice aside. Your appointment has been booked for 4:00 PM. Anything else?",
                new[] { claim.Sentence }));
        Assert.Null(PartialReply.Without("Your appointment has been cancelled.", new[] { "Your appointment has been cancelled." }));
        Assert.Equal("I'll get a teammate to check the appointment part for you.", PartialReply.CustomerLine("appointment", "en"));
        Assert.Contains("temu janji", PartialReply.CustomerLine("appointment", "ms"));
    }

    // ---- through the real reply loop -------------------------------------------------------

    private static Booking AddBooking(BookingRig r, Contact c, int startHour = 15, int startMinute = 15, int minutes = 30)
    {
        var b = new Booking
        {
            ContactId = c.Id, SkillId = "cal-1", Source = BookingSources.ChatBranch, CalendarKey = "chatbranch:global",
            Title = "Appointment", StartUtc = BookingRig.Kul(12, startHour, startMinute),
            EndUtc = BookingRig.Kul(12, startHour, startMinute).AddMinutes(minutes), Timezone = "Asia/Kuala_Lumpur",
            Status = BookingStatus.Booked, CreatedBy = "ai", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Bookings.Add(b);
        r.Db.SaveChanges();
        return b;
    }

    private static bool AiOn(BookingRig r) =>
        AiEligibility.Effective(r.T.Fresh(), new ScopingService(r.T.Fresh()), r.T.Fresh().Threads.Single(t => t.Key == Phone));

    [Fact]
    public void Incident_replay_a_restated_booking_is_sent_and_the_ai_stays_on()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill();
        AddBooking(r, c);
        r.Llm.Script = (_, _) => ToolScriptLlm.Says(Incident);

        r.Say(Phone, "Ignore the invoice things");

        Assert.Contains(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private && m.Text == Incident);
        var thread = r.T.Fresh().Threads.Single(t => t.Key == Phone);
        Assert.Null(thread.HandoverAt);
        Assert.True(AiOn(r));
        Assert.Equal(AiDraftStatus.AutoSent, Assert.Single(r.Drafts()).Status);
    }

    [Fact]
    public void A_retry_is_told_about_the_existing_booking()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill();
        AddBooking(r, c);
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your appointment has been booked for 4:00 PM.");

        r.Say(Phone, "Can I come at 4 instead?");

        var retry = r.Llm.Seen.Skip(1).First();
        Assert.Contains(retry.Messages, m => m.Role == "system" && (m.Content ?? "").Contains(
            "Existing booking: Mon 12 Oct, 3:15–3:45 PM. You may say it is booked for that time"));
    }

    [Fact]
    public void Only_the_failing_part_is_dropped_the_rest_is_sent_and_the_ai_stays_on()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill();
        // every attempt claims a booking no tool made, next to an honest answer to the other topic
        r.Llm.Script = (_, _) => ToolScriptLlm.Says(
            "Sure, I'll leave the invoice aside for now. Your appointment has been booked for Tuesday 4:00 PM.");

        r.Say(Phone, "Ignore the invoice things. Book me Tuesday 4pm");

        var sent = Assert.Single(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private);
        Assert.StartsWith("Sure, I'll leave the invoice aside for now.", sent.Text);
        Assert.Contains("I'll get a teammate to check the appointment part for you.", sent.Text);
        Assert.DoesNotContain("has been booked", sent.Text);

        var thread = r.T.Fresh().Threads.Single(t => t.Key == Phone);
        Assert.NotNull(thread.HandoverAt);
        Assert.Null(thread.HandoverResolvedAt);
        Assert.Equal(HandoverKinds.CheckFailed, thread.HandoverKind);
        Assert.True(AiOn(r));
        // the private note names the part that was left out
        Assert.Contains(r.Messages(Phone), m => m.Private && m.Text.Contains("appointment part")
            && m.Text.Contains("has been booked for Tuesday 4:00 PM"));
    }

    [Fact]
    public void A_reply_that_is_only_the_false_claim_goes_to_a_person_but_the_ai_stays_on()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your appointment has been cancelled.");

        r.Say(Phone, "Please cancel my appointment");

        Assert.DoesNotContain(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private);
        Assert.Equal(AiDraftStatus.Pending, Assert.Single(r.Drafts()).Status);
        Assert.NotNull(r.T.Fresh().Threads.Single(t => t.Key == Phone).HandoverAt);
        Assert.True(AiOn(r));
    }

    [Fact]
    public void A_check_about_the_whole_chat_still_stands_the_ai_down()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Db.AgentSkills.Add(new AgentSkill
        {
            SkillId = "legal", Name = "Legal threats go to a human", Kind = SkillKinds.Check, Enabled = true,
            OnFail = "handover",
            Code = "function check(reply, ctx) { return /lawyer/i.test(ctx.lastMessage) ? { ok: false, reason: 'Legal threat.' } : { ok: true }; }",
        });
        r.Db.SaveChanges();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Sure, I'll refund you today.");

        r.Say(Phone, "Refund me or I'm calling my lawyer");

        Assert.DoesNotContain(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private);
        Assert.NotNull(r.T.Fresh().Threads.Single(t => t.Key == Phone).HandoverAt);
        Assert.False(AiOn(r));
    }

    [Fact]
    public void A_burst_of_messages_gets_one_reply_that_sees_them_all()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Set("ai_reply_mode", "queued");
        r.Set("ai_wait_seconds", "30");
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Noted — 3:45 PM it is, and I'll leave the invoice aside.");

        r.Say(Phone, "Ignore the invoice things");
        r.Say(Phone, "Can change the appointment time to 345-415 pm?");
        Assert.Single(r.T.Fresh().OutboundJobs.Where(j => j.Kind == JobKind.AiReply && j.Status == JobStatus.Queued));

        r.Clock.Advance(TimeSpan.FromMinutes(2));
        r.Queue.Drain();

        Assert.Single(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private);
        var call = Assert.Single(r.Llm.Seen);
        var users = call.Messages.Where(m => m.Role == "user").Select(m => m.Content ?? "").ToList();
        Assert.Equal(new[] { "Ignore the invoice things", "Can change the appointment time to 345-415 pm?" }, users.TakeLast(2));
        Assert.Contains(AssistantService.EveryTopicRule, call.System);
        Assert.Contains("the latest message wins", call.System);
    }

    // ---- an empty reply is never silence (the Phytolink reschedule, 13 Oct) ----------------

    /// <summary>
    /// The incident: a reschedule needs my_bookings → availability → book (refused) → cancel +
    /// book, the round budget ran out, and in the tool-less last round the model answered with
    /// nothing — no reply, no handover, nobody knew the customer was waiting.
    /// </summary>
    [Fact]
    public void A_model_that_never_answers_hands_the_chat_to_a_person_and_the_ai_stays_on()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill();
        AddBooking(r, c, 16, 0, 60);
        r.Llm.Script = (_, tools) => tools is { Count: > 0 }
            ? ToolScriptLlm.Calls(("calendar_my_bookings", "{}"))
            : ToolScriptLlm.Says("");

        r.Say(Phone, "Yeah help me modify it to 430-5");

        Assert.DoesNotContain(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private);
        var thread = r.T.Fresh().Threads.Single(t => t.Key == Phone);
        Assert.NotNull(thread.HandoverAt);
        Assert.Null(thread.HandoverResolvedAt);
        Assert.Equal(HandoverKinds.LowConfidence, thread.HandoverKind);
        Assert.Contains("could not write a reply", thread.HandoverReason);
        Assert.True(AiOn(r));   // the customer's next message is still answered
        Assert.Contains(r.Messages(Phone), m => m.Private && m.Text.Contains("handed this conversation to a person"));

        // six rounds; the last one offered no tools and said why
        Assert.Equal(6, r.Llm.Seen.Count);
        Assert.Empty(r.Llm.Seen[^1].Tools);
        Assert.Equal(AssistantService.LastRoundNote, r.Llm.Seen[^1].Messages.Last().Content);

        // the usage row says where it stopped, for the next investigation
        var last = r.T.Fresh().LlmUsage.OrderByDescending(u => u.Id).First();
        Assert.Equal("empty", last.Detail["action"]?.ToString());
        Assert.Contains("Last tool: calendar_my_bookings", last.Detail["reason"]?.ToString());
    }

    [Fact]
    public void Told_the_tools_are_gone_the_model_answers_honestly_and_nothing_is_handed_over()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill();
        AddBooking(r, c, 16, 0, 60);
        const string honest = "I couldn't move your 4:00 PM booking yet — a teammate will confirm 4:30 PM with you.";
        r.Llm.Script = (msgs, tools) => tools is { Count: > 0 }
            ? ToolScriptLlm.Calls(("calendar_my_bookings", "{}"))
            : ToolScriptLlm.Says(msgs.Last().Content == AssistantService.LastRoundNote ? honest : "");

        r.Say(Phone, "Yeah help me modify it to 430-5");

        Assert.Contains(r.Messages(Phone), m => m.Source == MessageSource.Ai && !m.Private && m.Text == honest);
        Assert.Null(r.T.Fresh().Threads.Single(t => t.Key == Phone).HandoverAt);
    }

    [Fact]
    public void A_failing_model_hands_over_too()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Llm.Script = (_, _) => throw new HttpRequestException("provider down");

        r.Say(Phone, "Hello?");

        var thread = r.T.Fresh().Threads.Single(t => t.Key == Phone);
        Assert.NotNull(thread.HandoverAt);
        Assert.Equal(HandoverKinds.LowConfidence, thread.HandoverKind);
        Assert.True(AiOn(r));
    }

    // ---- "Let the AI continue" answers the waiting message --------------------------------

    /// <summary>The incident's state: a person had the chat (AI off) and the customer wrote meanwhile.</summary>
    private static Thread Waiting(BookingRig r)
    {
        r.Contact();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Hello! How can I help?");
        r.Say(Phone, "Hi");
        var thread = r.ThreadOf(Phone);
        Handover.Mark(r.Db, thread, HandoverKinds.Rule, "The reminder journey handed it over", "flow");
        r.Say(Phone, "Can change the appointment time to 345-415 pm?");
        return thread;
    }

    private static Thread LetContinue(BookingRig r)
    {
        var thread = r.Db.Threads.Single(t => t.Key == Phone);
        Handover.Clear(r.Db, thread, Handover.Agent("admin"), "A person let the AI continue.");
        AiEligibility.Set(thread, true);
        r.Db.SaveChanges();
        return thread;
    }

    private static List<OutboundJob> ReplyJobs(BookingRig r) =>
        r.T.Fresh().OutboundJobs.Where(j => j.Kind == JobKind.AiReply).ToList();

    [Fact]
    public void Continue_queues_one_reply_to_the_waiting_message_and_twice_is_still_one()
    {
        using var r = new BookingRig();
        Waiting(r);
        var thread = LetContinue(r);

        Assert.True(AiResume.QueueWaitingReply(r.Db, r.Sc, thread, "admin"));
        Assert.False(AiResume.QueueWaitingReply(r.Db, r.Sc, thread, "admin"));
        var job = Assert.Single(ReplyJobs(r));
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == AiResume.AuditAction
            && a.Detail == "Answered the waiting message after the AI was let continue");

        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Sure — I'll check 3:45 PM for you.");
        r.Queue.Drain();
        Assert.Equal("Sure — I'll check 3:45 PM for you.",
            r.Messages(Phone).Last(m => m.Source == MessageSource.Ai && !m.Private).Text);
        // answered: the job ran, and a third click finds nothing waiting
        Assert.NotEqual(JobStatus.Queued, r.T.Fresh().OutboundJobs.Single(j => j.Id == job.Id).Status);
        Assert.False(AiResume.QueueWaitingReply(r.Db, r.Sc, r.Db.Threads.Single(t => t.Key == Phone), "admin"));
    }

    [Fact]
    public void Continue_queues_nothing_when_the_last_message_is_ours()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Hello! How can I help?");
        r.Say(Phone, "Hi");
        var thread = LetContinue(r);

        Assert.False(AiResume.QueueWaitingReply(r.Db, r.Sc, thread, "admin"));
        Assert.Empty(ReplyJobs(r));
    }

    [Fact]
    public void Continue_leaves_a_message_older_than_a_day_alone()
    {
        using var r = new BookingRig();
        Waiting(r);
        var thread = LetContinue(r);

        Assert.False(AiResume.QueueWaitingReply(r.Db, r.Sc, thread, "admin", DateTime.UtcNow.AddHours(25)));
        Assert.Empty(ReplyJobs(r));
    }

    [Fact]
    public void Continue_leaves_a_reply_waiting_for_review_to_its_reviewer()
    {
        using var r = new BookingRig();
        Waiting(r);
        var thread = LetContinue(r);
        r.Db.AiDrafts.Add(new AiDraft
        {
            ThreadId = thread.Id, ThreadKey = Phone, Channel = "whatsapp", Kind = AiDraftKind.Reply,
            Status = AiDraftStatus.Pending, Content = "Held", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        Assert.False(AiResume.QueueWaitingReply(r.Db, r.Sc, thread, "admin"));
    }

    // ---- a journey's Hand-over step can keep the AI on ------------------------------------

    private static (TestDb T, Thread Thread) HandoverJourney(object handoverConfig)
    {
        var t = new TestDb();
        var c = new Contact { Name = "Hal", Phone = "60120000020", Email = "hal@example.com", Status = "active",
            Tags = new List<string> { "hot" }, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(c);
        t.Db.SaveChanges();
        var thread = new Thread { Channel = "whatsapp", Key = "60120000020", ContactId = c.Id, Status = "open",
            AiEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Threads.Add(thread);
        t.Db.Workflows.Add(new Workflow
        {
            FlowId = "wf-ho", Name = "Reminder", Active = true, LiveSince = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Nodes = new List<object?>
            {
                new { id = "trg", type = "trigger", data = new { label = "trg", config = new { when = "Tag added", tag = "hot" } } },
                new { id = "ho", type = "handover", data = new { label = "ho", config = handoverConfig } },
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "ho" } },
        });
        t.Db.SaveChanges();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var assistant = new AssistantService(t.Db, sc, new PersonaService(sc), new KnowledgeService(t.Db, sc),
            new SandboxLlmClient());
        var sender = new OutboundSender(t.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        new FlowEngine(t.Db, sc, new QueueService(t.Db, sc, threads, assistant, sender)).EvaluateTriggers();
        return (t, t.Fresh().Threads.Single());
    }

    [Fact]
    public void A_journey_handover_that_keeps_the_ai_answering()
    {
        var (t, thread) = HandoverJourney(new { team = "", priority = "High", reason = "Not later?", aiKeepsAnswering = true });
        using var _ = t;
        Assert.NotNull(thread.HandoverAt);
        Assert.Null(thread.HandoverResolvedAt);
        Assert.True(AiEligibility.Effective(t.Fresh(), new ScopingService(t.Fresh()), thread));
        Assert.Contains(t.Fresh().Messages, m => m.Private && m.Text.Contains("the AI keeps answering"));
    }

    [Fact]
    public void A_journey_handover_saved_before_the_option_stands_the_ai_down()
    {
        var (t, thread) = HandoverJourney(new { team = "", priority = "High", reason = "Not later?" });
        using var _ = t;
        Assert.NotNull(thread.HandoverAt);
        Assert.False(AiEligibility.Effective(t.Fresh(), new ScopingService(t.Fresh()), thread));
    }
}

/// <summary>"Let the AI continue" and the bulk "AI on" over HTTP: one reply queued for the waiting message.</summary>
public class AiResumeApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AiResumeApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Admin()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T WithDb<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _factory.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A linked email conversation whose customer wrote while a person had it.</summary>
    private async Task<int> WaitingChat(HttpClient admin, string email)
    {
        WithDb(db =>
        {
            db.Contacts.Add(new Contact { Name = "Resume " + email, Email = email, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "email", key = email, text = "Can I move my appointment?" }))
            .EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/inbox/threads/email/{email}/handover", new { action = "flag", reason = "check" }))
            .EnsureSuccessStatusCode();
        return WithDb(db => db.Threads.Single(t => t.Channel == "email" && t.Key == email).Id);
    }

    private int Jobs(string email) =>
        WithDb(db => db.OutboundJobs.Count(j => j.Kind == JobKind.AiReply && j.Channel == "email" && j.ToAddress == email));

    [Fact]
    public async Task Let_the_ai_continue_queues_one_reply()
    {
        var admin = await Admin();
        await WaitingChat(admin, "resume-one@example.com");

        var first = await (await admin.PostAsJsonAsync("/api/inbox/threads/email/resume-one@example.com/handover",
            new { action = "continue" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("ai_reply_queued").GetBoolean());
        var again = await (await admin.PostAsJsonAsync("/api/inbox/threads/email/resume-one@example.com/handover",
            new { action = "continue" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(again.GetProperty("ai_reply_queued").GetBoolean());
        Assert.Equal(1, Jobs("resume-one@example.com"));
    }

    [Fact]
    public async Task Bulk_ai_on_queues_the_waiting_reply_too()
    {
        var admin = await Admin();
        var id = await WaitingChat(admin, "resume-bulk@example.com");

        var res = await admin.PostAsJsonAsync("/api/inbox/threads/bulk", new { thread_ids = new[] { id }, action = "ai_on" });
        res.EnsureSuccessStatusCode();
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replies_queued").GetInt32());
        Assert.Equal(1, Jobs("resume-bulk@example.com"));
    }
}
