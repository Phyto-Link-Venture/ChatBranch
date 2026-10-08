using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Compliance, AI eligibility, routing at send time, the business redirect and
/// conversation-account recovery — driven through the real inbound path
/// (InboundProcessor) and the real queue drain over one in-memory database.
/// </summary>
public class ComplianceRoutingTests
{
    // ---- rig helpers ------------------------------------------------------------

    private static AiRig Rig()
    {
        var r = new AiRig();
        // no pacing gap: several jobs on one number can go out in one drain
        r.Set("queue_min_gap_s", "0");
        r.Set("queue_max_gap_s", "0");
        return r;
    }

    private static ChannelAccount Account(AiRig r, string name, string provider, string? role = null,
        string health = Health.Healthy, Dictionary<string, object?>? config = null)
    {
        var cfg = config ?? new Dictionary<string, object?>();
        if (role != null) cfg["role"] = role;
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = provider, Name = name,
            ExternalId = provider == Providers.Cloud ? $"PN-{name}" : $"bl-{name}",
            AccessToken = provider == Providers.Cloud ? "tok" : "",
            HealthStatus = health, Config = cfg, CreatedAt = DateTime.UtcNow,
        };
        r.Db.ChannelAccounts.Add(a);
        r.Db.SaveChanges();
        return a;
    }

    private static void Admin(AiRig r)
    {
        var u = new AppUser { Username = "boss", IsActive = true };
        r.Db.Users.Add(u);
        r.Db.SaveChanges();
        r.Db.Profiles.Add(new Profile { UserId = u.Id, Role = Roles.SuperAdmin });
        r.Db.SaveChanges();
    }

    private static List<OutboundJob> Jobs(AiRig r) => r.T.Fresh().OutboundJobs.OrderBy(j => j.Id).ToList();

    private static Domain.Thread Fresh(AiRig r, string key) =>
        r.T.Fresh().Threads.Include(t => t.Account).First(t => t.Channel == "whatsapp" && t.Key == key);

    // =========================================================================
    // 1. STOP / START on every channel
    // =========================================================================

    [Fact]
    public void Stop_opts_the_conversation_out_cancels_queued_jobs_notes_audits_and_silences_the_ai()
    {
        using var r = Rig();
        r.Contact("Stoppy", "60120000701");
        r.Say("60120000701", "hi there");                 // AI answers (sandbox, instant)
        Assert.Single(r.AiMessages("60120000701"));
        var queued = r.Queue.Enqueue("whatsapp", JobKind.Text, "+60120000701",
            new Dictionary<string, object?> { ["text"] = "promo" });
        var forced = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000701",
            new Dictionary<string, object?> { ["text"] = "operator exception", ["force"] = true });

        r.Say("60120000701", "Stop!");

        var t = Fresh(r, "60120000701");
        Assert.True(t.OptedOut);
        Assert.Single(r.AiMessages("60120000701"));       // no reply to STOP, nothing sent back
        var note = r.T.Fresh().Messages.Where(m => m.ThreadId == t.Id && m.Private).ToList();
        Assert.Contains(note, m => m.Text.Contains("opted out (STOP)"));
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "contact.opt_out" && a.Target == "whatsapp:60120000701");
        var jobs = r.T.Fresh().OutboundJobs.ToDictionary(j => j.Id);
        Assert.Equal(JobStatus.Cancelled, jobs[queued.Id].Status);   // "+60…" address matched by digits
        Assert.Equal(JobStatus.Queued, jobs[forced.Id].Status);      // an explicit force survives

        // later messages get no AI answer either
        r.Say("60120000701", "what about my order?");
        Assert.Single(r.AiMessages("60120000701"));
    }

    [Fact]
    public void Start_opts_back_in_and_configured_keywords_extend_the_builtins()
    {
        using var r = Rig();
        r.Set(OptOut.OutKey, "batal, cancel please");
        r.Set(OptOut.InKey, "[\"ya\"]");
        r.Contact("Words", "60120000702");

        r.Say("60120000702", "don't stop the updates");  // not a keyword: the whole message must be one
        Assert.False(Fresh(r, "60120000702").OptedOut);

        r.Say("60120000702", "Cancel please.");
        Assert.True(Fresh(r, "60120000702").OptedOut);

        r.Say("60120000702", "ya");
        Assert.False(Fresh(r, "60120000702").OptedOut);
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "contact.opt_in");

        r.Say("60120000702", "BERHENTI");
        Assert.True(Fresh(r, "60120000702").OptedOut);
        r.Say("60120000702", "mula");
        Assert.False(Fresh(r, "60120000702").OptedOut);
    }

    [Fact]
    public void Stop_works_on_non_whatsapp_channels_too()
    {
        using var r = Rig();
        var t = r.Threads.GetOrCreateThread("telegram", "777001", "Tele");
        r.Inbound.ProcessInbound("telegram", "777001", "unsubscribe");
        Assert.True(r.T.Fresh().Threads.First(x => x.Id == t.Id).OptedOut);

        var w = r.Threads.GetOrCreateThread("website", "sess-1", "Visitor");
        r.Threads.AppendMessage(w, MessageDirection.In, MessageSource.Customer, "STOP");
        r.Inbound.MaybeAutoReply(w, "STOP");
        Assert.True(r.T.Fresh().Threads.First(x => x.Id == w.Id).OptedOut);
    }

    [Fact]
    public void An_opted_out_conversation_fails_queued_free_text_at_send_time()
    {
        using var r = Rig();
        r.Contact("Late", "60120000703");
        r.Say("60120000703", "STOP");
        var job = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000703",
            new Dictionary<string, object?> { ["text"] = "hello" });
        r.Queue.Drain();
        var after = r.T.Fresh().OutboundJobs.First(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, after.Status);
        Assert.Contains("opted out", after.LastError);
    }

    // =========================================================================
    // 2. AI on/off: the gate reads the SAME rule the UI shows
    // =========================================================================

    [Fact]
    public void Reminder_number_without_a_tick_does_not_auto_reply()
    {
        using var r = Rig();
        r.Set("business_redirect", "false");
        var biz = Account(r, "Biz", Providers.Cloud);              // cloud → reminder role by default
        r.Contact("Rem", "60120000711");
        r.Say("60120000711", "hello?", biz.Id);

        Assert.Empty(r.AiMessages("60120000711"));
        Assert.Empty(r.Wa.Sent);
        Assert.DoesNotContain(Jobs(r), j => j.Kind == JobKind.AiReply);
        var t = Fresh(r, "60120000711");
        Assert.False(AiEligibility.Effective(r.Db, r.Sc, t));       // what the dashboard shows
    }

    [Fact]
    public void Conversation_number_auto_replies()
    {
        using var r = Rig();
        var conv = Account(r, "Conv", Providers.Cloud, AccountRoles.Conversation);
        r.Contact("Conv", "60120000712");
        r.Say("60120000712", "hello?", conv.Id);
        Assert.Single(r.AiMessages("60120000712"));
        Assert.True(AiEligibility.Effective(r.Db, r.Sc, Fresh(r, "60120000712")));
    }

    [Fact]
    public void An_explicit_per_account_tick_wins_over_the_role()
    {
        using var r = Rig();
        r.Set("business_redirect", "false");
        var biz = Account(r, "Biz2", Providers.Cloud);
        var conv = Account(r, "Conv2", Providers.Cloud, AccountRoles.Conversation);
        r.Contact("On", "60120000713");
        r.Contact("Off", "60120000714");

        var on = r.Threads.GetOrCreateThread("whatsapp", "60120000713", accountId: biz.Id);
        AiEligibility.Set(on, true);
        var off = r.Threads.GetOrCreateThread("whatsapp", "60120000714", accountId: conv.Id);
        AiEligibility.Set(off, false);
        r.Db.SaveChanges();

        r.Say("60120000713", "anyone?", biz.Id);
        r.Say("60120000714", "anyone?", conv.Id);
        Assert.Single(r.AiMessages("60120000713"));   // reminder number, ticked ON
        Assert.Empty(r.AiMessages("60120000714"));    // conversation number, ticked OFF
    }

    [Fact]
    public void The_inbox_ai_default_and_the_reminder_rule_apply()
    {
        using var r = Rig();
        r.Set("business_redirect", "false");
        var quietInbox = Account(r, "QuietInbox", Providers.Cloud, AccountRoles.Conversation,
            config: new Dictionary<string, object?> { [InboxSettings.AiDefaultKey] = "off" });
        var biz = Account(r, "Biz3", Providers.Cloud);
        r.Contact("Inbox", "60120000715");
        r.Contact("RuleOn", "60120000716");

        r.Say("60120000715", "hello", quietInbox.Id);
        Assert.Empty(r.AiMessages("60120000715"));

        r.Set("reminder_ai_default", "on");
        r.Say("60120000716", "hello", biz.Id);
        Assert.Single(r.AiMessages("60120000716"));
    }

    [Fact]
    public void The_queued_ai_reply_rechecks_the_effective_tick_at_drain()
    {
        using var r = Rig();
        r.Set("ai_wait_seconds", "30");                       // debounced → a queued ai_reply job
        var conv = Account(r, "Conv4", Providers.Cloud, AccountRoles.Conversation);
        r.Contact("Later", "60120000717");
        r.Say("60120000717", "question", conv.Id);
        var job = Jobs(r).Single(j => j.Kind == JobKind.AiReply);

        // the operator turns the chatbot off for this number before it is due
        var t = r.Db.Threads.First(x => x.Key == "60120000717");
        AiEligibility.Set(t, false);
        r.Db.SaveChanges();
        r.Clock.Advance(TimeSpan.FromMinutes(5));
        r.Queue.Drain();

        Assert.Equal(JobStatus.Cancelled, r.T.Fresh().OutboundJobs.First(j => j.Id == job.Id).Status);
        Assert.Empty(r.AiMessages("60120000717"));
    }

    // =========================================================================
    // 3. Routing at send time
    // =========================================================================

    [Fact]
    public void Least_loaded_auto_assign_spreads_contacts_evenly()
    {
        using var r = Rig();
        var a = Account(r, "A", Providers.Cloud, AccountRoles.Conversation);
        var b = Account(r, "B", Providers.Cloud, AccountRoles.Conversation);
        var contacts = Enumerable.Range(0, 4).Select(i => r.Contact($"C{i}", $"6012000072{i}")).ToList();
        foreach (var c in contacts)
        {
            r.Threads.GetOrCreateThread("whatsapp", c.Phone, contactId: c.Id);
            r.Queue.Enqueue("whatsapp", JobKind.Text, c.Phone, new() { ["text"] = "hi" }, contactId: c.Id);
        }
        // inbound history so the cold-text guard doesn't apply
        foreach (var c in contacts)
            r.Threads.AppendMessage(r.Db.Threads.First(t => t.Key == c.Phone), MessageDirection.In,
                MessageSource.Customer, "earlier");

        Assert.Equal(4, r.Queue.Drain(10));
        var load = r.T.Fresh().AccountAssignments.GroupBy(x => x.AccountId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, load[a.Id]);
        Assert.Equal(2, load[b.Id]);
        Assert.All(r.T.Fresh().AccountAssignments, x => Assert.False(x.Pinned));
        Assert.All(contacts, c => Assert.NotNull(Fresh(r, c.Phone).AccountId));
    }

    [Fact]
    public void Failover_moves_the_thread_clears_the_pin_notes_and_notifies()
    {
        using var r = Rig();
        Admin(r);
        var dead = Account(r, "Dead", Providers.Cloud, AccountRoles.Conversation, Health.Banned);
        var alive = Account(r, "Alive", Providers.Cloud, AccountRoles.Conversation);
        var c = r.Contact("Moved", "60120000731");
        var t = r.Threads.GetOrCreateThread("whatsapp", "60120000731", accountId: dead.Id, contactId: c.Id);
        r.Threads.AppendMessage(t, MessageDirection.In, MessageSource.Customer, "hi", accountId: dead.Id);
        r.Db.AccountAssignments.Add(new AccountAssignment
        {
            ContactId = c.Id, Channel = "whatsapp", AccountId = dead.Id, Pinned = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        var job = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000731", new() { ["text"] = "update" },
            contactId: c.Id, accountId: dead.Id);
        Assert.Equal(1, r.Queue.Drain());

        var fresh = r.T.Fresh();
        var sent = fresh.OutboundJobs.First(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Sent, sent.Status);
        Assert.Equal(alive.Id, sent.AccountId);
        Assert.Equal(alive.Id, fresh.Messages.First(m => m.Id == sent.SentMessageId).AccountId);
        var asg = fresh.AccountAssignments.Single(x => x.ContactId == c.Id);
        Assert.Equal(alive.Id, asg.AccountId);
        Assert.False(asg.Pinned);                                      // failover clears the pin
        Assert.Equal(alive.Id, Fresh(r, "60120000731").AccountId);     // the thread moved
        Assert.Contains(fresh.Messages, m => m.ThreadId == t.Id && m.Source == MessageSource.System
                                             && m.Text.Contains("Dead is banned"));
        Assert.Contains(fresh.Notifications, n => n.Title.Contains("moved off Dead"));
        Assert.Contains(fresh.AuditLogs, a => a.Action == "account.failover");
    }

    [Fact]
    public void Conversation_failover_never_lands_on_the_business_number()
    {
        using var r = Rig();
        var dead = Account(r, "DeadConv", Providers.Cloud, AccountRoles.Conversation, Health.Disconnected);
        Account(r, "Business", Providers.Cloud);                       // healthy, but reminder
        var c = r.Contact("Stuck", "60120000732");
        var t = r.Threads.GetOrCreateThread("whatsapp", "60120000732", accountId: dead.Id, contactId: c.Id);
        var job = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000732", new() { ["text"] = "hi" },
            contactId: c.Id);
        r.Queue.Drain();

        var after = r.T.Fresh().OutboundJobs.First(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, after.Status);
        Assert.Contains("No healthy WhatsApp conversation number", after.LastError);
        Assert.Equal(dead.Id, Fresh(r, "60120000732").AccountId);      // thread untouched
        Assert.Empty(r.Wa.Sent);
    }

    [Fact]
    public void Templates_prefer_a_reminder_cloud_and_never_fail_over_to_baileys()
    {
        using var r = Rig();
        var personal = Account(r, "Personal", Providers.Baileys);      // conversation by default
        var biz = Account(r, "Biz5", Providers.Cloud);
        var c = r.Contact("Tmpl", "60120000741");
        r.Threads.GetOrCreateThread("whatsapp", "60120000741", accountId: personal.Id, contactId: c.Id);
        r.Db.AccountAssignments.Add(new AccountAssignment
        {
            ContactId = c.Id, Channel = "whatsapp", AccountId = personal.Id, Pinned = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        var job = r.Queue.Enqueue("whatsapp", JobKind.Template, "60120000741",
            new() { ["name"] = "order_update", ["language"] = "en", ["variables"] = "" }, contactId: c.Id);
        Assert.Equal(1, r.Queue.Drain());
        Assert.Equal(biz.Id, r.T.Fresh().OutboundJobs.First(j => j.Id == job.Id).AccountId);
        Assert.Single(r.Wa.Sent);                                       // went out on the Cloud number
        // the sticky (pinned) conversation home is NOT moved by a template
        Assert.Equal(personal.Id, r.T.Fresh().AccountAssignments.Single(x => x.ContactId == c.Id).AccountId);

        // the only Cloud number is banned: the template fails, nothing goes to Baileys
        var bizRow = r.Db.ChannelAccounts.First(a => a.Id == biz.Id);
        bizRow.HealthStatus = Health.Banned;
        r.Db.SaveChanges();
        var t = r.Db.Threads.First(x => x.Key == "60120000741");
        t.AccountId = personal.Id;
        r.Db.SaveChanges();
        var job2 = r.Queue.Enqueue("whatsapp", JobKind.Template, "60120000741",
            new() { ["name"] = "order_update", ["language"] = "en", ["variables"] = "" }, contactId: c.Id);
        r.Queue.Drain();
        var failed = r.T.Fresh().OutboundJobs.First(j => j.Id == job2.Id);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("never fall back to a Baileys number", failed.LastError);
        Assert.Equal(personal.Id, Fresh(r, "60120000741").AccountId);
    }

    [Fact]
    public void With_no_configured_account_nothing_is_routed_and_the_send_stays_sandbox()
    {
        using var r = Rig();
        var c = r.Contact("Sandy", "60120000742");
        var job = r.Queue.Enqueue("whatsapp", JobKind.Text, "60120000742", new() { ["text"] = "hi" }, contactId: c.Id);
        Assert.Equal(1, r.Queue.Drain());
        var fresh = r.T.Fresh();
        var sent = fresh.OutboundJobs.First(j => j.Id == job.Id);
        Assert.Null(sent.AccountId);
        Assert.Equal("sandbox", fresh.Messages.First(m => m.Id == sent.SentMessageId).Status);
        Assert.Empty(fresh.AccountAssignments);
    }

    // =========================================================================
    // 4. Business redirect + recovery
    // =========================================================================

    [Fact]
    public void Business_number_inbound_with_ai_off_queues_one_redirect_per_24h()
    {
        using var r = Rig();
        var biz = Account(r, "Biz6", Providers.Cloud);
        Account(r, "Chat line", Providers.Baileys,
            config: new Dictionary<string, object?> { ["phone"] = "+60 12-345 0000" });
        r.Contact("Aisyah Rahman", "60120000751");

        r.Say("60120000751", "is my order ready?", biz.Id);
        var job = Jobs(r).Single(j => AiGate.Bool(j.Payload, "redirect") == true);
        Assert.Equal(JobKind.Text, job.Kind);
        Assert.Equal(biz.Id, job.AccountId);
        Assert.Equal(biz.Id, (int)AiGate.Num(job.Payload, "account_id")!.Value);
        Assert.Equal("system", AiGate.Str(job.Payload, "source", ""));
        Assert.Contains("https://wa.me/60123450000", AiGate.Str(job.Payload, "text", ""));
        Assert.Contains("Hi Aisyah!", AiGate.Str(job.Payload, "text", ""));
        Assert.Empty(r.AiMessages("60120000751"));

        r.Say("60120000751", "hello??", biz.Id);                        // throttled
        Assert.Single(Jobs(r), j => AiGate.Bool(j.Payload, "redirect") == true);

        // the redirect goes out on the business number itself, as a system message
        Assert.Equal(1, r.Queue.Drain());
        var sentId = r.T.Fresh().OutboundJobs.First(j => j.Id == job.Id).SentMessageId;
        var sent = r.T.Fresh().Messages.First(m => m.Id == sentId);
        Assert.Equal(MessageSource.System, sent.Source);
        Assert.Equal(biz.Id, sent.AccountId);

        // a day later the throttle (read off the queue rows) lets one more through
        var first = r.Db.OutboundJobs.First(j => j.Id == job.Id);
        first.CreatedAt = first.CreatedAt.AddHours(-25);
        r.Db.SaveChanges();
        r.Say("60120000751", "anyone?", biz.Id);
        Assert.Equal(2, Jobs(r).Count(j => AiGate.Bool(j.Payload, "redirect") == true));
    }

    [Fact]
    public void Redirect_targets_support_and_template_and_stands_down_when_the_ai_is_on()
    {
        using var r = Rig();
        var biz = Account(r, "Biz7", Providers.Cloud);
        r.Contact("Sup", "60120000752");
        r.Contact("Tpl", "60120000753");
        r.Contact("Ai", "60120000754");

        r.Set("business_redirect_target", "support");
        r.Set("support_contact", "help@example.com");
        r.Say("60120000752", "help", biz.Id);
        Assert.Contains("help@example.com",
            AiGate.Str(Jobs(r).Single(j => j.ToAddress == "60120000752").Payload, "text", ""));

        r.Set("business_redirect_target", "template");
        r.Set("business_redirect_template", "talk_to_us");
        r.Say("60120000753", "help", biz.Id);
        var tpl = Jobs(r).Single(j => j.ToAddress == "60120000753");
        Assert.Equal(JobKind.Template, tpl.Kind);
        Assert.Equal("talk_to_us", AiGate.Str(tpl.Payload, "name", ""));

        r.Set("reminder_ai_default", "on");
        r.Say("60120000754", "help", biz.Id);
        Assert.DoesNotContain(Jobs(r), j => j.ToAddress == "60120000754" && AiGate.Bool(j.Payload, "redirect") == true);
        Assert.Single(r.AiMessages("60120000754"));                     // the AI answered instead
    }

    [Fact]
    public void Inbound_moves_the_conversation_onto_the_number_that_received_it()
    {
        using var r = Rig();
        var personal = Account(r, "Personal2", Providers.Cloud, AccountRoles.Conversation);
        var biz = Account(r, "Biz8", Providers.Cloud);
        r.Contact("Hop", "60120000755");
        r.Say("60120000755", "hi", personal.Id);
        Assert.Equal(personal.Id, Fresh(r, "60120000755").AccountId);
        r.Say("60120000755", "hi again", biz.Id);
        Assert.Equal(biz.Id, Fresh(r, "60120000755").AccountId);
    }

    [Fact]
    public void A_conversation_number_going_down_notifies_and_auto_queues_the_recovery_template()
    {
        using var r = Rig();
        Admin(r);
        r.Set("recovery_mode", "auto");
        r.Set("recovery_template", "we_moved");
        var biz = Account(r, "Biz9", Providers.Cloud);
        var personal = Account(r, "Sales phone", Providers.Baileys);
        var c1 = r.Contact("One", "+60 12-000 0761");
        var c2 = r.Contact("Two", "60120000762");
        foreach (var c in new[] { c1, c2 })
            r.Db.AccountAssignments.Add(new AccountAssignment
            {
                ContactId = c.Id, Channel = "whatsapp", AccountId = personal.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        r.Db.SaveChanges();
        var t1 = r.Threads.GetOrCreateThread("whatsapp", "60120000761", accountId: personal.Id, contactId: c1.Id);

        var row = r.Db.ChannelAccounts.First(a => a.Id == personal.Id);
        Assert.True(AccountHealth.SetStatus(r.Db, row, Health.Banned, "logged_out"));
        r.Db.SaveChanges();

        var jobs = Jobs(r).Where(j => j.Kind == JobKind.Template).ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.ToAddress == "60120000761");       // wa_id (digits), not "+60 …"
        Assert.All(jobs, j => Assert.Equal("we_moved", AiGate.Str(j.Payload, "name", "")));
        Assert.All(jobs, j => Assert.Null(j.AccountId));                // routed at send time
        Assert.Contains(r.T.Fresh().Notifications, n => n.Title == "Account Sales phone down — 2 contacts affected");

        // a flap (healthy → banned again) within 24h queues nothing new
        AccountHealth.SetStatus(r.Db, row, Health.Healthy, "connected");
        AccountHealth.SetStatus(r.Db, row, Health.Banned, "logged_out");
        r.Db.SaveChanges();
        Assert.Equal(2, Jobs(r).Count(j => j.Kind == JobKind.Template));

        // at send time the template rides the healthy business number and the thread follows
        Assert.Equal(2, r.Queue.Drain(10));
        Assert.All(r.T.Fresh().OutboundJobs.Where(j => j.Kind == JobKind.Template),
            j => Assert.Equal(biz.Id, j.AccountId));
        Assert.Equal(biz.Id, Fresh(r, "60120000761").AccountId);
        Assert.Contains(r.T.Fresh().Messages, m => m.ThreadId == t1.Id && m.Text.Contains("Sales phone is banned"));
    }

    [Fact]
    public void One_click_recovery_only_notifies_and_the_reminder_role_never_triggers_it()
    {
        using var r = Rig();
        Admin(r);
        r.Set("recovery_template", "we_moved");
        Account(r, "Biz10", Providers.Cloud);
        var personal = Account(r, "Phone2", Providers.Baileys);
        var c = r.Contact("Solo", "60120000771");
        r.Db.AccountAssignments.Add(new AccountAssignment
        {
            ContactId = c.Id, Channel = "whatsapp", AccountId = personal.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        AccountHealth.SetStatus(r.Db, r.Db.ChannelAccounts.First(a => a.Id == personal.Id), Health.Disconnected);
        r.Db.SaveChanges();
        Assert.DoesNotContain(Jobs(r), j => j.Kind == JobKind.Template);
        Assert.Contains(r.T.Fresh().Notifications, n => n.Title.Contains("Phone2 down — 1 contact affected"));

        // the manual one-click path queues it
        var result = Recovery.Enqueue(r.Db, r.Sc, r.Db.ChannelAccounts.First(a => a.Id == personal.Id));
        Assert.Equal(1, result.Queued);

        // a business (reminder) number going down is not a conversation-recovery event
        var biz = r.Db.ChannelAccounts.First(a => a.Name == "Biz10");
        AccountHealth.SetStatus(r.Db, biz, Health.Banned);
        r.Db.SaveChanges();
        Assert.DoesNotContain(r.T.Fresh().Notifications, n => n.Title.Contains("Biz10 down"));
    }
}
