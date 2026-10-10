using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>A clock the test moves by hand (local time = UTC, so quiet hours are predictable).</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now;
    public ManualClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public DateTime Utc => _now.UtcDateTime;
}

/// <summary>Scripted model: replays queued replies (then a default) and records every prompt.</summary>
internal sealed class ScriptedReplyLlm : ILlmClient
{
    public Queue<string> Replies { get; } = new();
    public List<List<(string role, string content)>> Seen { get; } = new();
    public string Default { get; set; } = "Happy to help with that.";
    public bool IsConfigured => true;

    private string Next() => Replies.Count > 0 ? Replies.Dequeue() : Default;

    public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default)
    {
        Seen.Add(messages.ToList());
        return Task.FromResult(new LlmResult(Next(), 12, 8, "test", "scripted-1"));
    }

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        Seen.Add(messages.Select(m => (m.Role, m.Content ?? "")).ToList());
        return Task.FromResult(new LlmChatResult(Next(), Array.Empty<LlmToolCall>(), 12, 8, "test", "scripted-1"));
    }
}

/// <summary>A "configured" WhatsApp Cloud client that records sends instead of calling Meta.</summary>
internal sealed class LiveWhatsApp : IWhatsAppCloudClient
{
    public List<string> Sent { get; } = new();
    public bool IsConfigured(ChannelAccount account) =>
        account.Provider == Providers.Cloud && account.AccessToken.Length > 0 && account.ExternalId.Length > 0;
    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default)
    {
        Sent.Add(text);
        return Task.FromResult(new SendOutcome(true, $"wamid.{Sent.Count}", ""));
    }
    /// <summary>A real template message (OutboundSender.SendTemplate) — recorded by name.</summary>
    public Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default)
    {
        var name = template is IDictionary<string, object?> d && d.TryGetValue("name", out var n)
            ? n?.ToString() ?? "template" : "template";
        Sent.Add($"template:{name}");
        return Task.FromResult(new SendOutcome(true, $"wamid.{Sent.Count}", ""));
    }
}

internal sealed class RecordingTyping : ITypingIndicator
{
    public int Count { get; private set; }
    public void Start(Thread thread, ChannelAccount account) => Count++;
}

/// <summary>Everything the AI-ops layer needs, wired by hand over one in-memory DB.</summary>
internal sealed class AiRig : IDisposable
{
    public TestDb T { get; } = new();
    public AppDbContext Db => T.Db;
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public ScriptedReplyLlm Llm { get; } = new();
    public LiveWhatsApp Wa { get; } = new();
    public RecordingTyping Typing { get; } = new();
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public OutboundSender Sender { get; }
    public AssistantService Assistant { get; }
    public AiComposer Composer { get; }
    public AiGate Gate { get; }
    public AiResponder Responder { get; }
    public QueueService Queue { get; }
    public InboundProcessor Inbound { get; }
    public AiFollowUpService FollowUps { get; }

    public AiRig()
    {
        Sc = new ScopingService(Db);
        Threads = new ThreadStore(Db, Sc);
        Sender = new OutboundSender(Db, Threads, Wa, new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        Assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), Llm);
        Composer = new AiComposer(Db, Assistant, Llm);
        Gate = new AiGate(Db, Sc, Threads, Sender, Clock, Typing);
        Responder = new AiResponder(Db, Sc, Composer, Gate, Clock);
        Queue = new QueueService(Db, Sc, Threads, Assistant, Sender, Responder, Gate, Clock);
        var engine = new FlowEngine(Db, Sc, Queue);
        Inbound = new InboundProcessor(Db, Threads, Assistant, Sender, engine, Queue, Sc,
            new AutomationService(Db, Sc), Responder);
        FollowUps = new AiFollowUpService(Db, Sc, Threads, Composer, Gate, Clock);

        // Deterministic baseline: every behaviour explicitly OFF (the built-in
        // defaults depend on env AI_HUMANIZE, which the HTTP test host flips).
        Set("ai_review_mode", "auto");
        Set("ai_review_first_n", "0");
        Set("ai_review_notify", "false");
        Set("ai_wait_seconds", "0");
        Set("ai_typing", "false");
        Set("ai_split", "off");
        Set("ai_ack_skip", "false");
        Set("ai_followup", "false");
        Set("ai_reply_mode", "instant");
        Set("ai_reactions", "false");
        Set("ai_quote_replies", "off");
    }

    public void Set(string key, string value) => Sc.SetSetting(key, value);

    public Contact Contact(string name, string phone)
    {
        var c = new Contact { Name = name, Phone = phone, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        return c;
    }

    public ChannelAccount LiveAccount()
    {
        // a live Cloud number used as a CONVERSATION number: a Cloud account
        // defaults to the reminder role, whose chatbot tick is off unless the
        // reminder_ai_default rule says otherwise (AiEligibility) — these tests
        // exercise the AI ops, not the business-number rules
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = "Live", ExternalId = "PN-LIVE",
            AccessToken = "tok", CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Conversation },
        };
        Db.ChannelAccounts.Add(a);
        Db.SaveChanges();
        return a;
    }

    /// <summary>A customer message through the real inbound path (contact matched by phone).</summary>
    public Message Say(string phone, string text, int? accountId = null, DateTime? providerTs = null) =>
        Inbound.ProcessInbound("whatsapp", phone, text, accountId: accountId, providerTs: providerTs);

    public Thread ThreadOf(string phone) => Db.Threads.Include(t => t.Account).Include(t => t.Contact)
        .First(t => t.Channel == "whatsapp" && t.Key == phone);

    public List<Message> AiMessages(string phone)
    {
        var t = ThreadOf(phone);
        return T.Fresh().Messages.Where(m => m.ThreadId == t.Id && m.Source == MessageSource.Ai)
            .OrderBy(m => m.Id).ToList();
    }

    public List<AiDraft> Drafts(string phone)
    {
        var t = ThreadOf(phone);
        return T.Fresh().AiDrafts.Where(d => d.ThreadId == t.Id).OrderBy(d => d.Id).ToList();
    }

    public void Dispose() => T.Dispose();
}

/// <summary>
/// The AI monitor gate (auto / review_all / review_flagged / shadow), the
/// approve-edit-reject paths with send-time compliance, the auto-send deadline,
/// stale-draft expiry, the debounce, bubble splitting, acknowledgement skipping
/// and follow-ups — all against a scripted model and a hand-moved clock.
/// </summary>
public class AiOpsTests
{
    // ---- the gate -------------------------------------------------------------

    [Fact]
    public void Auto_mode_sends_and_records_an_auto_sent_draft()
    {
        using var r = new AiRig();
        r.Contact("Nadia", "60120000001");
        r.Llm.Replies.Enqueue("Yes, we're open on Sundays.");

        r.Say("60120000001", "Are you open on Sunday?");

        var ai = Assert.Single(r.AiMessages("60120000001"));
        Assert.Equal("Yes, we're open on Sundays.", ai.Text);
        var draft = Assert.Single(r.Drafts("60120000001"));
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
        Assert.Equal("auto", draft.DecidedBy);
        Assert.Equal(ai.Id, draft.SentMessageId);
        Assert.Equal(AiDraftKind.Reply, draft.Kind);
        // the trail carries what was answered and which model wrote it
        Assert.Contains("Are you open on Sunday?", System.Text.Json.JsonSerializer.Serialize(draft.Context));
        Assert.Contains("scripted-1", System.Text.Json.JsonSerializer.Serialize(draft.Context));
    }

    [Fact]
    public void Review_all_holds_the_reply_and_sends_nothing()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        r.Contact("Omar", "60120000002");

        r.Say("60120000002", "Can I change my order?");

        Assert.Empty(r.AiMessages("60120000002"));
        var draft = Assert.Single(r.Drafts("60120000002"));
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.Contains("Every AI reply is reviewed", draft.HoldReason);
        Assert.Null(draft.SendAfter);   // autosend 0 = wait for a person
    }

    [Fact]
    public void Review_flagged_holds_on_a_failed_check_or_risky_wording_and_sends_otherwise()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_flagged");
        r.Contact("Mei", "60120000003");
        r.Say("60120000003", "hello");          // auto-flagged? no: plain greeting reply goes out
        var thread = r.ThreadOf("60120000003");
        Assert.Single(r.AiMessages("60120000003"));

        var plain = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "Our team will get back to you soon."));
        Assert.Equal(AiGateStatus.Sent, plain.Status);

        var price = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "The price is RM 250 for the package."));
        Assert.Equal(AiGateStatus.Held, price.Status);
        var held = r.T.Fresh().AiDrafts.First(d => d.Id == price.DraftId);
        Assert.Contains("\"price\"", held.HoldReason);
        Assert.Contains("money amount", held.HoldReason);

        var check = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "Sure thing.",
            new Dictionary<string, object?>
            {
                ["checks"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["name"] = "grounded", ["passed"] = false },
                },
            }));
        Assert.Equal(AiGateStatus.Held, check.Status);
        Assert.Contains("Check failed: grounded",
            r.T.Fresh().AiDrafts.First(d => d.Id == check.DraftId).HoldReason);

        // the first N replies of a conversation are held too
        r.Set("ai_review_first_n", "1");
        r.Contact("New", "60120000033");
        r.Say("60120000033", "hi there");
        var first = Assert.Single(r.Drafts("60120000033"));
        Assert.Equal(AiDraftStatus.Pending, first.Status);
        Assert.Contains("The first AI reply in this conversation", first.HoldReason);
        Assert.True(r.Gate.Approve(first.Id, "alice").Delivered);
        r.Say("60120000033", "and another question");
        Assert.Equal(AiDraftStatus.AutoSent, r.Drafts("60120000033").Last().Status);
    }

    [Fact]
    public void A_reply_the_assistant_refused_is_held_even_in_auto_mode()
    {
        using var r = new AiRig();
        r.Contact("Ref", "60120000005");
        r.Say("60120000005", "hello");
        var thread = r.ThreadOf("60120000005");

        // AiReplyResult.ToMeta() of a draft whose checks asked for a person
        var o = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "We can refund you in full today.",
            new Dictionary<string, object?>
            {
                ["action"] = AiActions.Handover, ["reason"] = "policy check failed twice", ["handover"] = true,
                ["checks"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["name"] = "refund policy", ["ok"] = false, ["attempt"] = 0 },
                    new Dictionary<string, object?> { ["name"] = "refund policy", ["ok"] = false, ["attempt"] = 1,
                        ["reason"] = "promises a refund" },
                },
                ["retries"] = 1,
            }));

        Assert.Equal(AiGateStatus.Held, o.Status);
        var held = r.T.Fresh().AiDrafts.First(d => d.Id == o.DraftId);
        Assert.Contains("hand over", held.HoldReason);
        Assert.Contains("Check failed: refund policy — promises a refund", held.HoldReason);
        Assert.Single(r.AiMessages("60120000005"));   // only the earlier greeting reply went out
    }

    [Fact]
    public void Shadow_mode_never_sends()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "shadow");
        r.Set("ai_review_autosend_minutes", "1");
        r.Contact("Siti", "60120000004");

        r.Say("60120000004", "Is delivery free?");
        r.Clock.Advance(TimeSpan.FromMinutes(5));
        r.Gate.ProcessDue();

        Assert.Empty(r.AiMessages("60120000004"));
        var draft = Assert.Single(r.Drafts("60120000004"));
        Assert.Equal(AiDraftStatus.Shadow, draft.Status);
        Assert.Null(draft.SentAt);
    }

    // ---- decisions --------------------------------------------------------------

    [Fact]
    public void Approve_edit_and_reject_each_record_the_decision()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        r.Contact("A", "60120000011");
        r.Contact("B", "60120000012");
        r.Contact("C", "60120000013");
        r.Llm.Default = "We can do that tomorrow.";
        r.Say("60120000011", "q1");
        r.Say("60120000012", "q2");
        r.Say("60120000013", "q3");

        var a = r.Drafts("60120000011").Single();
        var approved = r.Gate.Approve(a.Id, "alice");
        Assert.Equal(AiGateStatus.Sent, approved.Status);
        Assert.Equal("We can do that tomorrow.", r.AiMessages("60120000011").Single().Text);
        var aRow = r.Drafts("60120000011").Single();
        Assert.Equal(AiDraftStatus.Approved, aRow.Status);
        Assert.Equal("alice", aRow.DecidedBy);
        Assert.NotNull(aRow.SentAt);

        var b = r.Drafts("60120000012").Single();
        Assert.True(r.Gate.Approve(b.Id, "bob", "We can do that on Monday instead.").Delivered);
        var bRow = r.Drafts("60120000012").Single();
        Assert.Equal(AiDraftStatus.Edited, bRow.Status);
        Assert.Equal("We can do that tomorrow.", bRow.OriginalContent);
        Assert.Equal("We can do that on Monday instead.", r.AiMessages("60120000012").Single().Text);

        var c = r.Drafts("60120000013").Single();
        Assert.Equal("rejected", r.Gate.Reject(c.Id, "carol", "Promised a date we can't keep").Status);
        var cRow = r.Drafts("60120000013").Single();
        Assert.Equal(AiDraftStatus.Rejected, cRow.Status);
        Assert.Equal("Promised a date we can't keep", cRow.RejectReason);
        Assert.Empty(r.AiMessages("60120000013"));

        // a decided draft cannot be decided again
        Assert.Equal(AiGateStatus.Conflict, r.Gate.Approve(c.Id, "carol").Status);
    }

    [Fact]
    public void Approve_rechecks_compliance_at_send_time()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        r.Contact("Opt", "60120000021");
        r.Say("60120000021", "how much?");
        var draft = r.Drafts("60120000021").Single();

        // the customer opted out while the draft waited
        var thread = r.ThreadOf("60120000021");
        thread.OptedOut = true;
        r.Db.SaveChanges();

        var o = r.Gate.Approve(draft.Id, "alice");
        Assert.Equal(AiGateStatus.Blocked, o.Status);
        Assert.Contains("opted out", o.Detail);
        Assert.Equal(AiDraftStatus.Failed, r.Drafts("60120000021").Single().Status);
        Assert.Empty(r.AiMessages("60120000021"));

        // …and a live WhatsApp Cloud number whose 24h window closed meanwhile
        var acct = r.LiveAccount();
        r.Contact("Late", "60120000022");
        r.Say("60120000022", "are you there?", acct.Id);
        var late = r.Drafts("60120000022").Single();
        r.Clock.Advance(TimeSpan.FromHours(25));
        var lateOutcome = r.Gate.Approve(late.Id, "alice");
        Assert.Equal(AiGateStatus.Blocked, lateOutcome.Status);
        Assert.Contains("24-hour window", lateOutcome.Detail);
        Assert.Empty(r.Wa.Sent);
    }

    [Fact]
    public void Held_reply_sends_itself_after_the_autosend_deadline()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        r.Set("ai_review_autosend_minutes", "10");
        r.Contact("Dee", "60120000031");
        r.Say("60120000031", "Do you ship to Penang?");
        var draft = r.Drafts("60120000031").Single();
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.NotNull(draft.SendAfter);

        r.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, r.Gate.ProcessDue());
        Assert.Empty(r.AiMessages("60120000031"));

        r.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, r.Gate.ProcessDue());
        Assert.Single(r.AiMessages("60120000031"));
        var sent = r.Drafts("60120000031").Single();
        Assert.Equal(AiDraftStatus.Sent, sent.Status);
        Assert.StartsWith("auto-send", sent.DecidedBy);
    }

    [Fact]
    public void A_new_customer_message_expires_the_pending_draft_and_regenerates()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        r.Contact("Lee", "60120000041");
        r.Say("60120000041", "What are your rates?");
        var first = r.Drafts("60120000041").Single();

        r.Say("60120000041", "Also, do you deliver?");

        var drafts = r.Drafts("60120000041");
        Assert.Equal(2, drafts.Count);
        Assert.Equal(AiDraftStatus.Expired, drafts[0].Status);
        Assert.Equal(first.Id, drafts[0].Id);
        Assert.Equal(AiDraftStatus.Pending, drafts[1].Status);
        // the fresh draft was written with BOTH customer messages in view
        var lastPrompt = r.Llm.Seen.Last();
        Assert.Contains(lastPrompt, m => m.role == "user" && m.content == "What are your rates?");
        Assert.Contains(lastPrompt, m => m.role == "user" && m.content == "Also, do you deliver?");
        Assert.Equal(2, AiGate.Items(drafts[1].Context, "answering").Count());
    }

    // ---- wait for the customer to finish ------------------------------------------

    [Fact]
    public void Debounce_combines_two_inbounds_into_one_job_and_one_reply()
    {
        using var r = new AiRig();
        r.Set("ai_wait_seconds", "25");
        r.Set("ai_wait_max_seconds", "120");
        r.Contact("Burst", "60120000051");
        var t0 = r.Clock.Utc;

        r.Say("60120000051", "hi");
        r.Clock.Advance(TimeSpan.FromSeconds(10));
        r.Say("60120000051", "I need to move my booking to Friday");

        var jobs = r.T.Fresh().OutboundJobs.Where(j => j.Kind == JobKind.AiReply).ToList();
        var job = Assert.Single(jobs);
        Assert.Equal(t0.AddSeconds(35), job.NotBefore!.Value, TimeSpan.FromSeconds(1));
        Assert.Empty(r.AiMessages("60120000051"));

        r.Clock.Advance(TimeSpan.FromSeconds(20));   // t0+30: still waiting
        r.Queue.Drain();
        Assert.Empty(r.AiMessages("60120000051"));

        r.Clock.Advance(TimeSpan.FromSeconds(6));    // t0+36: due
        r.Queue.Drain();
        Assert.Single(r.AiMessages("60120000051"));
        var prompt = Assert.Single(r.Llm.Seen);      // ONE generation for both messages
        Assert.Equal(2, prompt.Count(m => m.role == "user"));
    }

    [Fact]
    public void Debounce_never_waits_past_the_maximum()
    {
        using var r = new AiRig();
        r.Set("ai_wait_seconds", "25");
        r.Set("ai_wait_max_seconds", "40");
        r.Contact("Chatty", "60120000052");
        var t0 = r.Clock.Utc;

        r.Say("60120000052", "one");
        r.Clock.Advance(TimeSpan.FromSeconds(20));
        r.Say("60120000052", "two");

        var job = r.T.Fresh().OutboundJobs.Single(j => j.Kind == JobKind.AiReply);
        Assert.Equal(t0.AddSeconds(40), job.NotBefore!.Value, TimeSpan.FromSeconds(1));
    }

    // ---- type like a person / split long replies ------------------------------------

    [Fact]
    public void Splitting_respects_modes_limits_and_sentences()
    {
        var para = "We deliver across the city every weekday. Orders placed before noon arrive the same day.";
        var text = string.Join("\n\n", para, para, para, para);

        Assert.Single(Humanizer.Split(text, "off", 120));
        Assert.Single(Humanizer.Split("Short answer. Two sentences.", "auto", 320));

        var bubbles = Humanizer.Split(text, "auto", 120);
        Assert.InRange(bubbles.Count, 2, 3);
        Assert.Equal(text.Replace("\n\n", " "), string.Join(" ", bubbles).Replace("\n\n", " "));
        Assert.All(bubbles, b => Assert.EndsWith(".", b));   // never cut mid-sentence

        var always = Humanizer.Split("Thanks for asking!\n\nYes, we can deliver tomorrow.", "always", 320);
        Assert.Equal(new[] { "Thanks for asking!", "Yes, we can deliver tomorrow." }, always);

        var delay = Humanizer.TypingDelay(new string('x', 40), 8, 2, 12);
        Assert.Equal(5, delay.TotalSeconds, 3);
        Assert.Equal(12, Humanizer.TypingDelay(new string('x', 400), 8, 2, 12).TotalSeconds, 3);
        Assert.Equal(2, Humanizer.TypingDelay("ok", 8, 2, 12).TotalSeconds, 3);
    }

    [Theory]
    // commitments (the demo's missed cases) — EN, MS, ZH
    [InlineData("That order is above RM 10,000, so I'm passing it to our sales team.", true)]
    [InlineData("I'm sorry about the damage. I'll pass your case to our Customer Care team.", true)]
    [InlineData("For 500 parcels a month I'll hand this over to the team.", true)]
    [InlineData("I'll pass your claim to our After-Sales Support. Could you send a photo?", true)]
    [InlineData("I can't quote that myself, so a colleague will contact you today.", true)]
    [InlineData("Saya akan serahkan kepada pasukan jualan kami.", true)]
    [InlineData("我会把您的问题转给客服团队。", true)]
    // offers are not handovers until the customer accepts
    [InlineData("It's delayed in Kota Kinabalu. I can connect you with our Customer Care team if you'd like.", false)]
    [InlineData("Would you like me to pass this to our Customer Care team?", false)]
    [InlineData("I can pass this to our Customer Care team.", false)]
    [InlineData("需要我帮您转给客服吗？", false)]
    [InlineData("Your parcel arrives on Tuesday.", false)]
    public void Handover_wording_counts_commitments_not_offers(string reply, bool handsOver) =>
        Assert.Equal(handsOver, Humanizer.HandsOver(reply));

    [Fact]
    public void Split_reply_is_delivered_as_ordered_bubbles_with_typing_pauses()
    {
        using var r = new AiRig();
        r.Set("ai_split", "always");
        r.Contact("Split", "60120000061");
        r.Llm.Replies.Enqueue("Thanks for asking!\n\nYes, we can deliver tomorrow morning.");

        // typing off: first bubble now, the rest after a small gap
        r.Say("60120000061", "can you deliver tomorrow?");
        Assert.Equal(new[] { "Thanks for asking!" }, r.AiMessages("60120000061").Select(m => m.Text));
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Queue.Drain();
        Assert.Equal(new[] { "Thanks for asking!", "Yes, we can deliver tomorrow morning." },
            r.AiMessages("60120000061").Select(m => m.Text));
        var draft = r.Drafts("60120000061").Single();
        Assert.Equal(r.AiMessages("60120000061")[0].Id, draft.SentMessageId);

        // typing on: nothing until the typing pause has passed, then bubble by bubble
        r.Set("ai_typing", "true");
        r.Set("ai_typing_cps", "10");
        r.Set("ai_typing_min_s", "2");
        r.Set("ai_typing_max_s", "12");
        r.Contact("Typist", "60120000062");
        r.Llm.Replies.Enqueue("Good morning!\n\nYour order left the warehouse an hour ago.");
        r.Say("60120000062", "where is my order?");
        Assert.Empty(r.AiMessages("60120000062"));
        r.Queue.Drain();
        Assert.Empty(r.AiMessages("60120000062"));      // still "typing"
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Queue.Drain();
        Assert.Single(r.AiMessages("60120000062"));
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        r.Queue.Drain();
        Assert.Equal(new[] { "Good morning!", "Your order left the warehouse an hour ago." },
            r.AiMessages("60120000062").Select(m => m.Text));
    }

    // ---- don't answer every message ---------------------------------------------

    [Fact]
    public void Acknowledgements_get_no_reply_unless_the_AI_asked_something()
    {
        Assert.True(Humanizer.IsAcknowledgement("👍"));
        Assert.True(Humanizer.IsAcknowledgement("ok!!"));
        Assert.True(Humanizer.IsAcknowledgement("Thanks so much"));
        Assert.True(Humanizer.IsAcknowledgement("okkkk"));
        Assert.False(Humanizer.IsAcknowledgement("ok, but what about Sunday?"));

        using var r = new AiRig();
        r.Set("ai_ack_skip", "true");
        r.Contact("Ack", "60120000071");
        r.Llm.Replies.Enqueue("We close at 6pm today.");
        r.Say("60120000071", "What time do you close?");
        r.Say("60120000071", "ok thanks 👍");
        Assert.Single(r.AiMessages("60120000071"));
        Assert.Single(r.Llm.Seen);                       // no generation spent on "ok thanks"

        // an "ok" that ANSWERS a question is not an acknowledgement
        r.Contact("Yes", "60120000072");
        r.Llm.Replies.Enqueue("Shall I book you in for 3pm?");
        r.Llm.Replies.Enqueue("Done — see you at 3pm.");
        r.Say("60120000072", "Can I come today?");
        r.Say("60120000072", "ok");
        Assert.Equal(2, r.AiMessages("60120000072").Count);

        // optional short acknowledgement text
        r.Set("ai_ack_text", "Noted, thanks!");
        r.Contact("Short", "60120000073");
        r.Llm.Replies.Enqueue("Your parcel is on its way.");
        r.Say("60120000073", "Where is my parcel?");
        r.Say("60120000073", "thanks");
        Assert.Equal("Noted, thanks!", r.AiMessages("60120000073").Last().Text);
    }

    // ---- follow-ups ------------------------------------------------------------------

    private static AiRig FollowUpRig()
    {
        var r = new AiRig();
        r.Set("ai_followup", "true");
        r.Set("ai_followup_first_hours", "4");
        r.Set("ai_followup_between_hours", "24");
        r.Set("ai_followup_max", "2");
        return r;
    }

    [Fact]
    public void Follow_ups_are_sent_after_the_AI_asked_and_the_customer_went_quiet()
    {
        using var r = FollowUpRig();
        r.Contact("Quiet", "60120000081");
        r.Llm.Replies.Enqueue("Would you like me to reserve a table for Friday?");
        r.Say("60120000081", "Do you take reservations?");

        r.Clock.Advance(TimeSpan.FromHours(1));
        r.FollowUps.Tick();
        Assert.Single(r.AiMessages("60120000081"));

        r.Clock.Advance(TimeSpan.FromHours(3.1));
        r.Llm.Replies.Enqueue("Just checking in — shall I hold that table for you?");
        Assert.Equal(1, r.FollowUps.Tick().Submitted);
        Assert.Equal(2, r.AiMessages("60120000081").Count);
        Assert.Equal(AiDraftKind.FollowUp, r.Drafts("60120000081").Last().Kind);
        Assert.Equal(0, r.FollowUps.Tick().Submitted);    // not again straight away

        r.Clock.Advance(TimeSpan.FromHours(24.1));
        Assert.Equal(1, r.FollowUps.Tick().Submitted);
        r.Clock.Advance(TimeSpan.FromHours(48));
        Assert.Equal(0, r.FollowUps.Tick().Submitted);    // max 2 per silence
        Assert.Equal(3, r.AiMessages("60120000081").Count);
    }

    [Fact]
    public void Follow_ups_stop_on_reply_opt_out_takeover_or_a_closed_question()
    {
        using var r = FollowUpRig();
        // customer replied and the AI's answer asked nothing
        r.Contact("Done", "60120000082");
        r.Llm.Replies.Enqueue("Would you like a quote?");
        r.Llm.Replies.Enqueue("Great, it's on its way to your inbox.");
        r.Say("60120000082", "hi");
        r.Say("60120000082", "yes please");
        // opted out after the question
        r.Contact("Stop", "60120000083");
        r.Llm.Replies.Enqueue("Anything else I can help with?");
        r.Say("60120000083", "thanks for the info");
        var stop = r.ThreadOf("60120000083");
        stop.OptedOut = true;
        // a teammate took over the conversation
        r.Contact("Human", "60120000084");
        r.Llm.Replies.Enqueue("Shall I send the brochure?");
        r.Say("60120000084", "brochure?");
        r.Threads.AppendMessage(r.ThreadOf("60120000084"), MessageDirection.Out, MessageSource.Agent,
            "Hi, this is Sam — I'll take it from here.");
        // AI switched off
        r.Contact("Off", "60120000085");
        r.Llm.Replies.Enqueue("Do you want the large size?");
        r.Say("60120000085", "sizes?");
        r.ThreadOf("60120000085").AiEnabled = false;
        r.Db.SaveChanges();

        r.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, r.FollowUps.Tick().Submitted);
        Assert.Equal(2, r.AiMessages("60120000082").Count);
        Assert.Single(r.AiMessages("60120000083"));
        Assert.Single(r.AiMessages("60120000084"));
        Assert.Single(r.AiMessages("60120000085"));
    }

    [Fact]
    public void Quiet_hours_postpone_a_follow_up()
    {
        using var r = FollowUpRig();
        r.Contact("Night", "60120000086");
        r.Llm.Replies.Enqueue("Can I call you tomorrow?");
        r.Say("60120000086", "I have a question about my plan");

        r.Clock.Advance(TimeSpan.FromHours(4.1));
        var local = r.Clock.GetLocalNow().DateTime;
        r.Set("quiet_start", local.AddHours(-1).ToString("HH:mm"));
        r.Set("quiet_end", local.AddHours(1).ToString("HH:mm"));

        Assert.Equal(0, r.FollowUps.Tick().Submitted);
        var state = r.FollowUps.StateFor(r.ThreadOf("60120000086").Id)!;
        Assert.Contains("quiet hours", state.Note);
        Assert.NotNull(state.NextFollowUpAt);
        Assert.True(state.NextFollowUpAt > r.Clock.Utc);

        r.Clock.Advance(TimeSpan.FromMinutes(75));
        Assert.Equal(1, r.FollowUps.Tick().Submitted);
        Assert.Equal(2, r.AiMessages("60120000086").Count);
    }

    [Fact]
    public void A_closed_WhatsApp_window_uses_the_template_or_skips_with_a_note()
    {
        using var r = FollowUpRig();
        var acct = r.LiveAccount();
        r.Contact("Window", "60120000087");
        r.Contact("Template", "60120000088");
        r.Llm.Default = "Would you like to continue with the order?";
        r.Say("60120000087", "I want to order", acct.Id);
        Assert.Single(r.Wa.Sent);

        r.Clock.Advance(TimeSpan.FromHours(25));   // window closed AND follow-up due

        // no template → skipped, with a private note in the chat
        r.FollowUps.Tick();
        var thread = r.ThreadOf("60120000087");
        var note = r.T.Fresh().Messages.Where(m => m.ThreadId == thread.Id && m.Private).ToList();
        Assert.Contains(note, m => m.Text.Contains("24-hour window"));
        Assert.Contains("window closed", r.FollowUps.StateFor(thread.Id)!.Note);
        Assert.Single(r.Wa.Sent);                   // nothing free-text went out

        // with a re-engagement template → a template job, recorded in the monitor
        r.Say("60120000088", "I want to order too", acct.Id, providerTs: r.Clock.Utc);  // Meta timestamp = "now"
        Assert.Equal(2, r.Wa.Sent.Count);
        r.Clock.Advance(TimeSpan.FromHours(25));
        r.Set("ai_followup_template", "reengage_v1");
        var tmplThread = r.ThreadOf("60120000088");
        r.FollowUps.Tick();
        var job = r.T.Fresh().OutboundJobs.Single(j => j.Kind == JobKind.Template && j.ToAddress == "60120000088");
        Assert.Equal("reengage_v1", AiGate.Str(job.Payload, "name", ""));
        var draft = r.T.Fresh().AiDrafts.Where(d => d.ThreadId == tmplThread.Id).OrderBy(d => d.Id).Last();
        Assert.Equal(AiDraftKind.FollowUp, draft.Kind);
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
    }

    // ---- flows: AI-composed queue messages pass the gate ----------------------------

    [Fact]
    public void An_AI_composed_flow_message_is_held_like_any_AI_message()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        var c = r.Contact("Flow", "60120000091");
        r.Threads.GetOrCreateThread("whatsapp", "60120000091", contactId: c.Id);
        var job = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000091", new Dictionary<string, object?>
        {
            ["text"] = "Your appointment is confirmed.", ["source"] = "ai",
            ["ai_meta"] = new Dictionary<string, object?> { ["model"] = "flow-model" },
        }, c.Id);

        r.Queue.Drain();

        Assert.Empty(r.AiMessages("60120000091"));
        Assert.Equal(QueueService.HeldStatus, r.T.Fresh().OutboundJobs.Single(j => j.Id == job.Id).Status);
        var draft = r.Drafts("60120000091").Single();
        Assert.Equal(AiDraftKind.FlowMessage, draft.Kind);
        Assert.Equal("flow-model", AiGate.Str(draft.Context, "model", ""));
    }
}
