using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The website widget's typing indicator, over the EXISTING poll: the visitor is
/// shown "…" only while an AI reply is genuinely on its way to their own
/// conversation (<see cref="AiTypingSignal"/>), and never when the AI is going to
/// stay silent — nothing queued, AI off, an acknowledgement skipped, a reply
/// parked for a human reviewer — because dots with nothing behind them are worse
/// than silence.
///
/// This half drives it through the public HTTP surface (no new endpoint, no new
/// auth: <c>POST /api/widget/{token}/messages</c> and
/// <c>GET /api/widget/{token}/messages</c> simply carry one more field).
/// </summary>
public class WidgetTypingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WidgetTypingTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Admin()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<(int Id, string Token)> NewWidget(string name)
    {
        var admin = await Admin();
        var created = await admin.PostAsJsonAsync("/api/widgets", new { name });
        created.EnsureSuccessStatusCode();
        var w = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (w.GetProperty("id").GetInt32(), w.GetProperty("token").GetString()!);
    }

    private async Task<string> NewSession(HttpClient c, string token)
    {
        var res = await c.PostAsJsonAsync($"/api/widget/{token}/session", new { });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetString()!;
    }

    private async Task<JsonElement> Send(HttpClient c, string token, string session, string text)
    {
        var res = await c.PostAsJsonAsync($"/api/widget/{token}/messages", new { session, text });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> Poll(HttpClient c, string token, string session, int after = 0) =>
        await c.GetFromJsonAsync<JsonElement>(
            $"/api/widget/{token}/messages?session={Uri.EscapeDataString(session)}&after={after}");

    private static bool Typing(JsonElement d) => d.GetProperty("typing").GetBoolean();

    private T Scope<T>(Func<IServiceProvider, T> fn)
    {
        using var scope = _factory.Services.CreateScope();
        return fn(scope.ServiceProvider);
    }

    private static Thread ThreadOf(AppDbContext db, string session) =>
        db.Threads.First(t => t.Channel == Channels.Website && t.Key == session);

    // ---- the signal on the existing poll --------------------------------------

    [Fact]
    public async Task Poll_reports_typing_while_an_ai_reply_is_queued_and_clears_when_it_is_delivered()
    {
        var (_, token) = await NewWidget("Typing — queued");
        var c = _factory.CreateClient();
        var session = await NewSession(c, token);

        // nothing queued yet (no LLM in the suite, so the inline path answers
        // nothing): the send and the poll both say "not typing"
        var sent = await Send(c, token, session, "Do you deliver to Penang?");
        Assert.False(Typing(sent));
        Assert.False(Typing(await Poll(c, token, session)));

        // the real producer of the signal: the debounced ai_reply job
        // (AiResponder.ScheduleReply — "wait for the customer to finish")
        var jobId = Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var thread = ThreadOf(db, session);
            return sp.GetRequiredService<AiResponder>().ScheduleReply(thread, 5, 25).Id;
        });
        Assert.True(Typing(await Poll(c, token, session)));

        // …and it stays true across the compose itself, which the queue runs with
        // the job CLAIMED ("sending") — the longest part of the wait
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var job = db.OutboundJobs.First(j => j.Id == jobId);
            job.Status = "sending";
            job.UpdatedAt = DateTime.UtcNow;
            return db.SaveChanges();
        });
        Assert.True(Typing(await Poll(c, token, session)));

        // the reply lands: the job is done, so the very same poll that delivers
        // the message reports typing false
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var store = sp.GetRequiredService<ThreadStore>();
            var thread = ThreadOf(db, session);
            var msg = store.AppendMessage(thread, MessageDirection.Out, MessageSource.Ai,
                "Yes — we deliver to Penang.", status: "sent", accountId: thread.AccountId);
            var job = db.OutboundJobs.First(j => j.Id == jobId);
            job.Status = JobStatus.Sent;
            job.SentAt = DateTime.UtcNow;
            job.SentMessageId = msg.Id;
            return db.SaveChanges();
        });

        var landed = await Poll(c, token, session);
        Assert.False(Typing(landed));
        Assert.Contains("Yes — we deliver to Penang.",
            landed.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Send_answers_typing_immediately_when_the_wait_queued_a_reply_and_false_once_it_leaves_the_queue()
    {
        var (id, token) = await NewWidget("Typing — send");
        // the inbox's own "wait for the customer to finish" (what the live widget
        // inbox has): the send path queues instead of answering inline
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var inbox = db.ChannelAccounts.First(a => a.Id == id);
            InboxSettings.SetBehaviour(inbox, AiBehaviourSettings.WaitSeconds, "5");
            return db.SaveChanges();
        });

        var c = _factory.CreateClient();
        var session = await NewSession(c, token);
        // the send itself says a reply is coming — the dots appear at once rather
        // than up to one 4 s poll later
        Assert.True(Typing(await Send(c, token, session, "Can I change my order?")));
        Assert.True(Typing(await Poll(c, token, session)));

        // the queue takes it (no model configured in the suite → the AI hands over
        // and the job is closed): the indicator clears on the next poll, with no
        // stuck "typing" left behind
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var thread = ThreadOf(db, session);
            foreach (var j in db.OutboundJobs.Where(j => j.ToAddress == thread.Key).ToList())
                j.NotBefore = DateTime.UtcNow.AddSeconds(-1);
            db.SaveChanges();
            return sp.GetRequiredService<QueueService>().Drain();
        });
        Assert.False(Typing(await Poll(c, token, session)));
        Assert.Empty(Scope(sp => sp.GetRequiredService<AppDbContext>().OutboundJobs
            .Where(j => j.Kind == JobKind.AiReply && j.Status == JobStatus.Queued
                        && j.Channel == Channels.Website && j.ToAddress == session)
            .ToList()));
    }

    [Fact]
    public async Task Poll_reports_no_typing_when_the_ai_is_off_for_the_conversation()
    {
        var (id, token) = await NewWidget("Typing — AI off");
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var inbox = db.ChannelAccounts.First(a => a.Id == id);
            InboxSettings.SetBehaviour(inbox, AiBehaviourSettings.WaitSeconds, "5");
            // Channels → "Who answers it": this inbox does not answer with AI
            InboxSettings.Apply(db, inbox, null, "false", null);
            return db.SaveChanges();
        });

        var c = _factory.CreateClient();
        var session = await NewSession(c, token);
        var sent = await Send(c, token, session, "Hello?");

        Assert.False(Typing(sent));
        Assert.False(Typing(await Poll(c, token, session)));
        // nothing was queued at all — the signal has nothing to be honest about
        Assert.Empty(Scope(sp => sp.GetRequiredService<AppDbContext>().OutboundJobs
            .Where(j => j.Channel == Channels.Website && j.ToAddress == session).ToList()));
    }

    [Fact]
    public async Task A_reply_held_for_review_or_a_stale_job_never_leaves_the_indicator_stuck_on()
    {
        var (_, token) = await NewWidget("Typing — held");
        var c = _factory.CreateClient();
        var session = await NewSession(c, token);
        await Send(c, token, session, "What does a repair cost?");

        int jobId = Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return sp.GetRequiredService<AiResponder>().ScheduleReply(ThreadOf(db, session), 5, 25).Id;
        });
        Assert.True(Typing(await Poll(c, token, session)));

        // Held for review: the draft exists, a PERSON is now the bottleneck and may
        // take hours. The job is closed as "held", so typing stops here — it only
        // ever covers the compose/queue window.
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var job = db.OutboundJobs.First(j => j.Id == jobId);
            job.Status = QueueService.HeldStatus;
            job.UpdatedAt = DateTime.UtcNow;
            return db.SaveChanges();
        });
        Assert.False(Typing(await Poll(c, token, session)));

        // A job that somehow stays queued (a stalled drain, pacing, quiet hours)
        // stops counting once it is stale, so an abandoned job cannot animate dots
        // forever.
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var job = db.OutboundJobs.First(j => j.Id == jobId);
            job.Status = JobStatus.Queued;
            var old = DateTime.UtcNow.AddSeconds(-(AiTypingSignal.MaxSeconds + 60));
            job.CreatedAt = old;
            job.UpdatedAt = old;
            job.NotBefore = old;
            return db.SaveChanges();
        });
        Assert.False(Typing(await Poll(c, token, session)));
    }

    [Fact]
    public async Task Typing_never_leaks_across_sessions_threads_or_widgets()
    {
        var (_, tokenA) = await NewWidget("Typing — widget A");
        var (_, tokenB) = await NewWidget("Typing — widget B");
        var c = _factory.CreateClient();
        var one = await NewSession(c, tokenA);
        var two = await NewSession(c, tokenA);
        var other = await NewSession(c, tokenB);
        foreach (var s in new[] { one, two }) await Send(c, tokenA, s, "hi");
        await Send(c, tokenB, other, "hi");

        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return sp.GetRequiredService<AiResponder>().ScheduleReply(ThreadOf(db, one), 5, 25).Id;
        });

        Assert.True(Typing(await Poll(c, tokenA, one)));
        // the visitor in the next tab, and a visitor of another company's widget,
        // see their own conversation only
        Assert.False(Typing(await Poll(c, tokenA, two)));
        Assert.False(Typing(await Poll(c, tokenB, other)));
        // a session id is not readable through another widget's token at all
        var crossed = await Poll(c, tokenB, one);
        Assert.False(Typing(crossed));
        Assert.Empty(crossed.GetProperty("messages").EnumerateArray());

        // a job addressed to one session but PINNED to another thread stays with
        // the thread it was queued for (ThreadStore.JobBelongsTo)
        Scope(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var mine = ThreadOf(db, one);
            db.OutboundJobs.Add(new OutboundJob
            {
                Channel = Channels.Website, Kind = JobKind.AiReply, ToAddress = two,
                Payload = new Dictionary<string, object?> { [ThreadStore.JobThreadKey] = mine.Id },
                AccountId = mine.AccountId, Status = JobStatus.Queued,
                NotBefore = DateTime.UtcNow.AddSeconds(5),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        Assert.False(Typing(await Poll(c, tokenA, two)));
    }

    // ---- the script ----------------------------------------------------------

    [Fact]
    public async Task Widget_script_animates_a_server_driven_typing_bubble_in_every_language()
    {
        var js = await _factory.CreateClient().GetStringAsync("/api/widget.js");

        // the label comes from the I18N table, in the same three languages it
        // already covers — never a hardcoded English string
        Assert.Contains("typing: 'Typing…'", js);
        Assert.Contains("typing: 'Sedang menaip…'", js);
        Assert.Contains("typing: '正在输入…'", js);
        Assert.Contains("aria-label', L.typing", js);

        // three dots in the configured accent, inside the existing shadow root
        Assert.Contains("@keyframes cb-dot", js);
        Assert.Contains("background:currentColor", js);
        Assert.Contains("typingEl.style.color = cfg.accent", js);
        // a visitor who asked for less motion gets a still bubble with the word
        Assert.Contains("prefers-reduced-motion:reduce", js);
        Assert.Contains("prefers-reduced-motion: reduce", js);

        // server-driven: the indicator follows the poll's and the send's `typing`
        Assert.Contains("applyTyping(!!(d && d.typing))", js);
        // and is cleared on error and on close
        Assert.Contains(".catch(function () { applyTyping(false); })", js);
        Assert.Contains("applyTyping(false); };", js);
        // the close-attention poll is bounded
        Assert.Contains("FAST_MAX = 20", js);
        Assert.Contains("fastLeft <= 0", js);
    }
}

/// <summary>
/// The same signal at the layer that produces it, with a scripted model and a
/// hand-moved clock (the <c>AiRig</c> of <see cref="AiOpsTests"/>): a widget
/// conversation taken through the real inbound → queue → gate path, asserting
/// what the visitor would be shown at each step.
/// </summary>
public class WidgetTypingSignalTests
{
    private static ChannelAccount Widget(AiRig r)
    {
        var a = new ChannelAccount
        {
            Channel = Channels.Website, Provider = "widget", Name = "Website chat",
            ExternalId = "wgt_signal", HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
        };
        r.Db.ChannelAccounts.Add(a);
        r.Db.SaveChanges();
        return a;
    }

    /// <summary>A visitor's message, stored and handled exactly as WidgetEndpoints does.</summary>
    private static Thread Say(AiRig r, ChannelAccount inbox, string session, string text)
    {
        var thread = r.Threads.GetOrCreateThread(Channels.Website, session, "Website visitor", inbox.Id);
        r.Threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, text,
            status: "received", accountId: inbox.Id);
        r.Inbound.MaybeAutoReply(thread, text);
        return thread;
    }

    private static bool Typing(AiRig r, Thread thread) =>
        AiTypingSignal.Pending(r.Db, r.Threads, thread, r.Clock.Utc);

    [Fact]
    public void The_wait_window_and_the_compose_read_as_typing_and_the_delivered_reply_clears_it()
    {
        using var r = new AiRig();
        r.Set("ai_wait_seconds", "5");
        var inbox = Widget(r);
        r.Llm.Replies.Enqueue("We're open until 6pm today.");

        var thread = Say(r, inbox, "sess-wait", "What time do you close?");
        // the debounced ai_reply job is in the queue: a reply really is coming
        Assert.True(Typing(r, thread));

        // still the visitor's wait, five seconds in
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(Typing(r, thread));

        // the queue answers: the reply is stored and the job is done
        r.Queue.Drain();
        Assert.Equal("We're open until 6pm today.",
            Assert.Single(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai)).Text);
        Assert.False(Typing(r, thread));
    }

    [Fact]
    public void An_acknowledgement_the_ai_skips_clears_the_indicator_instead_of_animating_at_nothing()
    {
        using var r = new AiRig();
        r.Set("ai_ack_skip", "true");
        var inbox = Widget(r);
        r.Llm.Replies.Enqueue("Your order ships tomorrow.");

        var thread = Say(r, inbox, "sess-ack", "When does my order ship?");
        r.Queue.Drain();
        Assert.Single(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));
        Assert.False(Typing(r, thread));

        // "ok thanks" with no wait configured: the skip happens inline, so nothing
        // is ever queued and the visitor is shown nothing
        Say(r, inbox, "sess-ack", "ok thanks");
        Assert.False(Typing(r, thread));
        Assert.Single(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));

        // with a wait configured the job IS queued (nobody knows yet whether the
        // AI will answer), and the moment the skip decides, the indicator clears
        r.Set("ai_wait_seconds", "5");
        Say(r, inbox, "sess-ack", "thanks!");
        Assert.True(Typing(r, thread));
        r.Clock.Advance(TimeSpan.FromSeconds(6));
        r.Queue.Drain();
        Assert.False(Typing(r, thread));
        Assert.Single(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));
    }

    [Fact]
    public void A_reply_held_for_review_stops_the_indicator_once_the_draft_exists()
    {
        using var r = new AiRig();
        r.Set("ai_wait_seconds", "5");
        r.Set("ai_review_mode", "review_all");
        var inbox = Widget(r);
        r.Llm.Replies.Enqueue("I can refund that for you.");

        var thread = Say(r, inbox, "sess-review", "Can I get a refund?");
        Assert.True(Typing(r, thread));

        r.Clock.Advance(TimeSpan.FromSeconds(6));
        r.Queue.Drain();

        // the draft is waiting for a person — who may take hours
        Assert.Equal(AiDraftStatus.Pending,
            Assert.Single(r.Db.AiDrafts.Where(d => d.ThreadId == thread.Id)).Status);
        Assert.Empty(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));
        Assert.False(Typing(r, thread));

        // and it stays false however long the reviewer takes
        r.Clock.Advance(TimeSpan.FromHours(3));
        Assert.False(Typing(r, thread));
    }

    [Fact]
    public void The_ai_typing_pause_is_what_the_indicator_fills_and_it_clears_when_the_bubble_lands()
    {
        using var r = new AiRig();
        // what the widget inbox can now switch back on: pause in proportion to the
        // reply's length — previously dead air on this channel
        r.Set("ai_typing", "true");
        var inbox = Widget(r);
        r.Llm.Replies.Enqueue("Yes, we carry that part in stock and can fit it this week.");

        var thread = Say(r, inbox, "sess-pause", "Do you have the part in stock?");
        // nothing is delivered yet: the gate queued the bubble behind a typing pause
        Assert.Empty(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));
        Assert.True(Typing(r, thread));

        r.Clock.Advance(TimeSpan.FromSeconds(30));
        r.Queue.Drain();
        Assert.Single(r.Db.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai));
        Assert.False(Typing(r, thread));
    }

    [Fact]
    public void A_long_wait_a_stale_job_and_another_conversation_are_all_reported_as_not_typing()
    {
        using var r = new AiRig();
        var inbox = Widget(r);
        var mine = r.Threads.GetOrCreateThread(Channels.Website, "sess-mine", "Website visitor", inbox.Id);
        var theirs = r.Threads.GetOrCreateThread(Channels.Website, "sess-theirs", "Website visitor", inbox.Id);
        r.Db.SaveChanges();

        // a deliberate minutes-long debounce is a wait, not composition: nothing is
        // claimed to be happening until the reply is genuinely close
        var job = r.Responder.ScheduleReply(mine, 300, 600);
        Assert.False(Typing(r, mine));
        r.Clock.Advance(TimeSpan.FromSeconds(300 - AiTypingSignal.LeadSeconds + 1));
        Assert.True(Typing(r, mine));

        // the other visitor's conversation is never affected
        Assert.False(Typing(r, theirs));

        // and a job nothing ever drained goes quiet rather than animating forever
        // (well past its own due time: stuck, parked, or simply forgotten)
        r.Clock.Advance(TimeSpan.FromSeconds(AiTypingSignal.LeadSeconds + AiTypingSignal.MaxSeconds + 10));
        Assert.False(Typing(r, mine));
        Assert.Equal(JobStatus.Queued, r.Db.OutboundJobs.First(j => j.Id == job.Id).Status);
    }
}
