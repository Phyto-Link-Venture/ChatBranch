using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The AI on/off controls — ONE precedence rule (opted out → master switch /
/// schedule → contact never / never-tag → per-chat tick → contact always →
/// inbox default → role) with a visible reason — and human-handover tracking:
/// every handover site stamps the columns, a person's reply or a resolve
/// clears them. Pure rule tests plus the real inbound path, flows, automations
/// and the AI gate over an in-memory database.
/// </summary>
public class AiControlsTests
{
    private static readonly TimeZoneInfo Kl = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");

    private static AiPolicyInputs Inputs(string master = AiMasterModes.On, string contactMode = "",
        string[]? tags = null, string[]? never = null, AiSchedule? schedule = null,
        string scheduleMode = AiScheduleModes.Inside, DateTime? now = null, string scope = "the organization") =>
        new(new AiMaster(master, schedule ?? new AiSchedule(), scheduleMode, null, scope, "organization"),
            now ?? DateTime.UtcNow, Kl, contactMode, tags ?? Array.Empty<string>(), never ?? Array.Empty<string>());

    private static ChannelAccount Wa(int id, string role, string? aiDefault = null)
    {
        var cfg = new Dictionary<string, object?> { ["role"] = role };
        if (aiDefault != null) cfg["ai_default"] = aiDefault;
        return new ChannelAccount { Id = id, Channel = "whatsapp", Provider = Providers.Cloud, Name = $"Line {id}", Config = cfg };
    }

    private static Thread WaThread(int? accountId, bool aiEnabled = true, bool? tick = null, bool optedOut = false)
    {
        var t = new Thread { Channel = "whatsapp", Key = "6012", AccountId = accountId, AiEnabled = aiEnabled, OptedOut = optedOut };
        if (tick is bool b && accountId is int a) t.AiOverrides = new Dictionary<string, object?> { [a.ToString()] = b };
        return t;
    }

    // =========================================================================
    // 1. the precedence table
    // =========================================================================

    [Fact]
    public void Precedence_table_every_rule_in_order()
    {
        var conv = Wa(1, AccountRoles.Conversation);
        var reminder = Wa(2, AccountRoles.Reminder);
        var inboxOff = Wa(3, AccountRoles.Conversation, "off");
        var inboxOn = Wa(4, AccountRoles.Reminder, "on");

        // (thread, account, reminderDefaultOn, inputs) → (enabled, rule, forced)
        var cases = new (string Name, Thread T, ChannelAccount? A, bool Rem, AiPolicyInputs P, bool On, string Rule, bool Forced)[]
        {
            ("opted out beats always + tick on", WaThread(1, tick: true, optedOut: true), conv, false,
                Inputs(contactMode: "always"), false, AiRules.OptedOut, true),
            ("master off beats tick on", WaThread(1, tick: true), conv, false,
                Inputs(master: AiMasterModes.Off), false, AiRules.MasterOff, true),
            ("master off beats contact always", WaThread(3), inboxOff, false,
                Inputs(master: AiMasterModes.Off, contactMode: "always"), false, AiRules.MasterOff, true),
            ("opted out beats master off", WaThread(1, optedOut: true), conv, false,
                Inputs(master: AiMasterModes.Off), false, AiRules.OptedOut, true),
            ("contact never beats tick on", WaThread(1, tick: true), conv, false,
                Inputs(contactMode: "never"), false, AiRules.ContactNever, true),
            ("never-tag beats tick on", WaThread(1, tick: true), conv, false,
                Inputs(tags: new[] { "VIP" }, never: new[] { "vip" }), false, AiRules.NeverTag, true),
            ("tick off beats contact always", WaThread(1, tick: false), conv, false,
                Inputs(contactMode: "always"), false, AiRules.ChatTick, false),
            ("tick on beats a reminder default", WaThread(2, tick: true), reminder, false,
                Inputs(), true, AiRules.ChatTick, false),
            ("contact always beats the inbox default off", WaThread(3, aiEnabled: false), inboxOff, false,
                Inputs(contactMode: "always"), true, AiRules.ContactAlways, false),
            ("contact always beats the reminder role", WaThread(2), reminder, false,
                Inputs(contactMode: "always"), true, AiRules.ContactAlways, false),
            ("inbox default off", WaThread(3, aiEnabled: false), inboxOff, false,
                Inputs(), false, AiRules.InboxDefault, false),
            ("inbox default on beats the reminder role", WaThread(4), inboxOn, false,
                Inputs(), true, AiRules.InboxDefault, false),
            ("reminder role, rule off", WaThread(2), reminder, false, Inputs(), false, AiRules.RoleDefault, false),
            ("reminder role, rule on", WaThread(2), reminder, true, Inputs(), true, AiRules.RoleDefault, false),
            ("conversation role", WaThread(1), conv, false, Inputs(), true, AiRules.RoleDefault, false),
            ("a legacy explicit off stays off, even for always", WaThread(1, aiEnabled: false), conv, false,
                Inputs(contactMode: "always"), false, AiRules.ChatTick, false),
            ("follow mode changes nothing", WaThread(1), conv, false, Inputs(contactMode: ""), true, AiRules.RoleDefault, false),
            ("an unrelated tag does nothing", WaThread(1), conv, false,
                Inputs(tags: new[] { "gold" }, never: new[] { "vip" }), true, AiRules.RoleDefault, false),
        };
        foreach (var c in cases)
        {
            var v = AiEligibility.Decide(c.T, c.A, c.Rem, c.P);
            Assert.True(c.On == v.Enabled, $"{c.Name}: enabled {v.Enabled}");
            Assert.True(c.Rule == v.Rule, $"{c.Name}: rule {v.Rule}");
            Assert.True(c.Forced == v.Forced, $"{c.Name}: forced {v.Forced}");
            Assert.StartsWith(v.Enabled ? "On:" : "Off:", v.Reason);
        }
    }

    [Fact]
    public void Reasons_name_the_scope_the_tag_and_the_contact()
    {
        var t = WaThread(1);
        var conv = Wa(1, AccountRoles.Conversation);
        Assert.Equal("Off: AI is switched off for North Branch",
            AiEligibility.Decide(t, conv, false, Inputs(master: AiMasterModes.Off, scope: "North Branch")).Reason);
        Assert.Equal("Off: this contact is set to never use AI",
            AiEligibility.Decide(t, conv, false, Inputs(contactMode: "never")).Reason);
        Assert.Contains("“VIP Client”", AiEligibility.Decide(t, conv, false,
            Inputs(tags: new[] { "VIP Client" }, never: new[] { "vip-client" })).Reason);
        Assert.Equal("Off: this contact opted out (STOP)",
            AiEligibility.Decide(WaThread(1, optedOut: true), conv, false, Inputs()).Reason);
        Assert.Equal("Off: the Line 3 inbox has AI off by default",
            AiEligibility.Decide(WaThread(3, aiEnabled: false), Wa(3, AccountRoles.Conversation, "off"), false, Inputs()).Reason);
        // a handed-over chat whose tick the handover turned off says so
        var handed = WaThread(1, tick: false);
        handed.HandoverAt = DateTime.UtcNow;
        Assert.Equal("Off: handed to a person — waiting for a human", AiEligibility.Decide(handed, conv, false, Inputs()).Reason);
    }

    [Fact]
    public void Threads_without_an_account_use_their_flag_and_always_beats_a_stamped_default()
    {
        var tg = new Thread { Channel = "telegram", Key = "t1", AiEnabled = false };   // inbox default stamped off
        Assert.False(AiEligibility.Decide(tg, null, false, Inputs()).Enabled);
        Assert.True(AiEligibility.Decide(tg, null, false, Inputs(contactMode: "always")).Enabled);

        AiEligibility.Set(tg, false);                                                    // an operator's explicit OFF
        var v = AiEligibility.Decide(tg, null, false, Inputs(contactMode: "always"));
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.ChatTick, v.Rule);

        AiEligibility.Set(tg, true);
        Assert.True(AiEligibility.Decide(tg, null, false, Inputs()).Enabled);
        // the old three-argument rule is unchanged for every existing caller
        Assert.True(AiEligibility.Effective(tg, null, false));
        var wa = WaThread(2);
        Assert.False(AiEligibility.Effective(wa, Wa(2, AccountRoles.Reminder), false));
        Assert.True(AiEligibility.Effective(wa, Wa(2, AccountRoles.Reminder), true));
    }

    [Fact]
    public void An_account_less_tick_carries_over_when_a_number_is_attached()
    {
        var t = new Thread { Channel = "whatsapp", Key = "6013" };
        AiEligibility.Set(t, false);                    // ticked off while account-less
        t.AccountId = 7;
        var conv = Wa(7, AccountRoles.Conversation);
        // the AI never turns itself on just because a number was attached
        Assert.False(AiEligibility.Decide(t, conv, false, Inputs()).Enabled);
    }

    // =========================================================================
    // 2. the schedule (org timezone)
    // =========================================================================

    [Fact]
    public void Schedule_inside_and_outside_in_the_org_timezone()
    {
        var s = AiSchedule.Parse("{\"mon\":[[\"18:00\",\"23:59\"]],\"tue\":[\"09:00\",\"12:00\"],\"wed\":[[9,17]]}");
        Assert.Equal(3, s.Windows.Count);
        // Monday 6 Oct 2025, 11:00 UTC = 19:00 in Kuala Lumpur (UTC+8)
        var monEveningKl = new DateTime(2025, 10, 6, 11, 0, 0, DateTimeKind.Utc);
        var monAfternoonKl = new DateTime(2025, 10, 6, 9, 0, 0, DateTimeKind.Utc);   // 17:00 KL
        var monLateKl = new DateTime(2025, 10, 6, 15, 59, 30, DateTimeKind.Utc);     // 23:59:30 KL — "…–23:59" is to midnight

        var t = WaThread(1);
        var conv = Wa(1, AccountRoles.Conversation);
        AiVerdict At(DateTime now, string mode) =>
            AiEligibility.Decide(t, conv, false, Inputs(master: AiMasterModes.Schedule, schedule: s, scheduleMode: mode, now: now));

        Assert.True(At(monEveningKl, AiScheduleModes.Inside).Enabled);
        Assert.EndsWith("(inside the AI schedule)", At(monEveningKl, AiScheduleModes.Inside).Reason);
        Assert.True(At(monLateKl, AiScheduleModes.Inside).Enabled);
        var off = At(monAfternoonKl, AiScheduleModes.Inside);
        Assert.False(off.Enabled);
        Assert.Equal(AiRules.Schedule, off.Rule);
        Assert.True(off.Forced);
        Assert.Equal("Off: outside the AI schedule", off.Reason);
        // "outside" = humans during the windows, the AI the rest of the time
        Assert.False(At(monEveningKl, AiScheduleModes.Outside).Enabled);
        Assert.True(At(monAfternoonKl, AiScheduleModes.Outside).Enabled);
        // a single pair and whole hours were accepted on the way in
        var tueMorningKl = new DateTime(2025, 10, 7, 2, 0, 0, DateTimeKind.Utc);    // Tue 10:00 KL
        var wedNoonKl = new DateTime(2025, 10, 8, 4, 0, 0, DateTimeKind.Utc);       // Wed 12:00 KL
        Assert.True(At(tueMorningKl, AiScheduleModes.Inside).Enabled);
        Assert.True(At(wedNoonKl, AiScheduleModes.Inside).Enabled);
        // the same UTC instant is Monday evening in KL but Monday morning in UTC
        var utcInputs = new AiPolicyInputs(new AiMaster(AiMasterModes.Schedule, s, AiScheduleModes.Inside, null, "x", "organization"),
            monEveningKl, TimeZoneInfo.Utc, "", Array.Empty<string>(), Array.Empty<string>());
        Assert.False(AiEligibility.Decide(t, conv, false, utcInputs).Enabled);
    }

    [Fact]
    public void Overnight_windows_and_the_next_change()
    {
        var s = AiSchedule.Parse("{\"mon\":[[\"22:00\",\"06:00\"]]}");
        var master = new AiMaster(AiMasterModes.Schedule, s, AiScheduleModes.Inside, null, "x", "organization");
        Assert.True(master.AllowsAt(new DateTime(2025, 10, 6, 15, 0, 0, DateTimeKind.Utc), Kl));   // Mon 23:00 KL
        Assert.True(master.AllowsAt(new DateTime(2025, 10, 6, 20, 0, 0, DateTimeKind.Utc), Kl));   // Tue 04:00 KL
        Assert.False(master.AllowsAt(new DateTime(2025, 10, 6, 23, 0, 0, DateTimeKind.Utc), Kl));  // Tue 07:00 KL
        var next = master.NextChangeUtc(new DateTime(2025, 10, 6, 15, 0, 0, DateTimeKind.Utc), Kl);
        Assert.Equal(new DateTime(2025, 10, 6, 22, 0, 0, DateTimeKind.Utc), next);                  // Tue 06:00 KL
        Assert.Equal("{\"mon\":[[\"22:00\",\"06:00\"]]}", s.ToJson());
        Assert.True(AiSchedule.Parse("not json").IsEmpty);
    }

    // =========================================================================
    // 3. settings: up the entity tree, never-tags add up
    // =========================================================================

    [Fact]
    public void Master_switch_resolves_up_the_entity_tree_and_never_tags_add_up()
    {
        using var t = new TestDb();
        var tenant = new Tenant { Name = "T", Slug = "t" };
        t.Db.Tenants.Add(tenant);
        t.Db.SaveChanges();
        var hq = new Entity { TenantId = tenant.Id, Name = "Head Office", Slug = "hq" };
        t.Db.Entities.Add(hq);
        t.Db.SaveChanges();
        var north = new Entity { TenantId = tenant.Id, Name = "North Branch", Slug = "north", ParentId = hq.Id };
        var south = new Entity { TenantId = tenant.Id, Name = "South Branch", Slug = "south", ParentId = hq.Id };
        t.Db.Entities.AddRange(north, south);
        t.Db.SaveChanges();
        var sc = new ScopingService(t.Db);

        Assert.Equal(AiMasterModes.On, new AiControls(t.Db, sc).Master(north.Id).Mode);   // nothing set: on
        sc.SetSetting(AiControlKeys.Master, "off", hq.Id);
        sc.SetSetting(AiControlKeys.Master, "on", south.Id);
        var ai = new AiControls(t.Db, sc);
        var m = ai.Master(north.Id);
        Assert.Equal(AiMasterModes.Off, m.Mode);
        Assert.Equal("Head Office", m.ScopeName);
        Assert.Equal("inherited", m.ModeFrom);
        Assert.Equal(AiMasterModes.On, ai.Master(south.Id).Mode);
        Assert.Equal("entity", ai.Master(south.Id).ModeFrom);
        Assert.Equal(AiMasterModes.On, ai.Master(null).Mode);

        sc.SetSetting(AiControlKeys.NeverTags, "VIP, complaint");
        sc.SetSetting(AiControlKeys.NeverTags, "legal", north.Id);
        var tags = new AiControls(t.Db, sc).NeverTags(north.Id);
        Assert.Equal(new[] { "complaint", "legal", "vip" }, tags.Select(x => x.ToLowerInvariant()).OrderBy(x => x));
        Assert.DoesNotContain("legal", new AiControls(t.Db, sc).NeverTags(south.Id));
    }

    // =========================================================================
    // 4. master off stops replies, follow-ups and flow AI steps
    // =========================================================================

    [Fact]
    public void Master_off_stops_replies_and_follow_ups()
    {
        using var r = new AiRig();
        r.Contact("Mo", "60120009001");
        r.Set(AiControlKeys.Master, "off");
        r.Say("60120009001", "Are you open?");
        Assert.Empty(r.AiMessages("60120009001"));
        Assert.Empty(r.Drafts("60120009001"));                     // not even generated
        var t = r.ThreadOf("60120009001");
        Assert.False(AiEligibility.Effective(r.Db, r.Sc, t));
        Assert.Equal("Off: AI is switched off for the organization", AiEligibility.Decide(r.Db, r.Sc, t).Reason);

        // back on: the AI answers and leaves a question open…
        r.Set(AiControlKeys.Master, "on");
        r.Llm.Replies.Enqueue("Which day suits you?");
        r.Say("60120009001", "I want to book");
        Assert.Single(r.AiMessages("60120009001"));
        // …then the switch goes off again: no follow-up, whenever it is due
        r.Set("ai_followup", "true");
        r.Set("ai_followup_first_hours", "1");
        r.Set(AiControlKeys.Master, "off");
        r.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, r.FollowUps.Tick().Submitted);
        r.Set(AiControlKeys.Master, "on");
        Assert.Equal(1, r.FollowUps.Tick().Submitted);
    }

    [Fact]
    public void Master_off_skips_a_flows_ai_message_step_with_a_note()
    {
        using var r = new AiRig();
        var c = r.Contact("Flo", "60120009002");
        var wf = AddAiFlow(r, "wf-ai-off");
        r.Set(AiControlKeys.Master, "off");
        var engine = new FlowEngine(r.Db, r.Sc, r.Queue);
        var run = Start(r, engine, wf, c.Id);

        Assert.Empty(r.T.Fresh().OutboundJobs.Where(j => j.FlowRunId == run.Id));
        var skipped = r.T.Fresh().FlowStepEvents.Where(e => e.RunId == run.Id && e.Kind == FlowStepKind.Skipped).ToList();
        Assert.Contains(skipped, e => e.Detail.Contains("AI message skipped") && e.Detail.Contains("switched off"));

        // with the switch on (and no model) the step sends its script as before
        r.Set(AiControlKeys.Master, "on");
        var run2 = Start(r, engine, wf, c.Id);
        Assert.NotEqual(run.Id, run2.Id);
        Assert.Single(r.T.Fresh().OutboundJobs.Where(j => j.FlowRunId == run2.Id));
    }

    /// <summary>Start a Manual / API flow for one contact and return its run (it executes inline).</summary>
    private static FlowRun Start(AiRig r, FlowEngine engine, Workflow wf, int contactId)
    {
        var (started, _) = engine.StartManual(wf, new[] { contactId });
        Assert.Equal(1, started);
        return r.T.Fresh().FlowRuns.Where(x => x.WorkflowId == wf.Id && x.ContactId == contactId)
            .OrderByDescending(x => x.Id).First();
    }

    private static Workflow AddAiFlow(AiRig r, string flowId)
    {
        var wf = new Workflow
        {
            FlowId = flowId, Name = flowId, Active = true, LiveSince = DateTime.UtcNow.AddDays(-1),
            Nodes = new List<object?>
            {
                new { id = "trg", type = "trigger", data = new { label = "trg", config = new { when = "Manual / API", repeat = "Every time" } } },
                new { id = "ai", type = "action", data = new { label = "ai", config = new { channels = new[] { "AI message", "WhatsApp" }, script = "Hi {{first_name}}" } } },
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "ai" } },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        return wf;
    }

    // =========================================================================
    // 5. per contact: never / always / never-tags, through the real inbound path
    // =========================================================================

    [Fact]
    public void Contact_never_always_and_never_tags_on_the_inbound_path()
    {
        using var r = new AiRig();
        var never = r.Contact("Nev", "60120009011");
        never.AiMode = ContactAiModes.Never;
        var vip = r.Contact("Vip", "60120009012");
        vip.Tags = new List<string> { "VIP" };
        r.Db.SaveChanges();
        r.Set(AiControlKeys.NeverTags, "vip");

        r.Say("60120009011", "hello?");
        r.Say("60120009012", "hello?");
        Assert.Empty(r.AiMessages("60120009011"));
        Assert.Empty(r.AiMessages("60120009012"));
        Assert.Equal(AiRules.NeverTag, AiEligibility.Decide(r.Db, r.Sc, r.ThreadOf("60120009012")).Rule);

        // a business (reminder) number has the chatbot off by default…
        var biz = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = "Biz", ExternalId = "PN-BIZ", AccessToken = "tok",
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Reminder }, CreatedAt = DateTime.UtcNow,
        };
        r.Db.ChannelAccounts.Add(biz);
        var always = r.Contact("Al", "60120009013");
        r.Db.SaveChanges();
        r.Say("60120009013", "hi", accountId: biz.Id);
        Assert.Empty(r.AiMessages("60120009013"));
        // …but a contact set to "always" is answered there
        always.AiMode = ContactAiModes.Always;
        r.Db.SaveChanges();
        r.Say("60120009013", "anyone there?", accountId: biz.Id);
        Assert.Single(r.AiMessages("60120009013"));
        Assert.Equal(AiRules.ContactAlways, AiEligibility.Decide(r.Db, r.Sc, r.ThreadOf("60120009013")).Rule);
        // still subject to the master switch
        r.Set(AiControlKeys.Master, "off");
        r.Say("60120009013", "and now?", accountId: biz.Id);
        Assert.Single(r.AiMessages("60120009013"));
    }

    // =========================================================================
    // 6. the flow's "Set chatbot on/off" step
    // =========================================================================

    [Fact]
    public void Flow_set_chatbot_step_turns_the_tick_on_and_off_per_channel_or_all()
    {
        using var r = new AiRig();
        var c = r.Contact("Fs", "60120009021");
        r.Say("60120009021", "hi");
        var tg = r.Threads.GetOrCreateThread("telegram", "tg-9021", "Fs");
        tg.ContactId = c.Id;
        r.Db.SaveChanges();

        var off = SetChatbotFlow(r, "wf-bot-off", new { op = "chatbot", value = "off", scope = "channels", channels = new[] { "whatsapp" } });
        var engine = new FlowEngine(r.Db, r.Sc, r.Queue);
        Start(r, engine, off, c.Id);
        var fresh = r.T.Fresh();
        Assert.False(AiEligibility.Effective(fresh, new ScopingService(fresh),
            fresh.Threads.Include(x => x.Account).First(x => x.Key == "60120009021")));
        Assert.True(fresh.Threads.First(x => x.Key == "tg-9021").AiEnabled);          // other channels untouched

        var allOff = SetChatbotFlow(r, "wf-bot-all-off", new { updateType = "chatbot", value = "off" }); // the alias, scope all
        Start(r, engine, allOff, c.Id);
        Assert.False(r.T.Fresh().Threads.First(x => x.Key == "tg-9021").AiEnabled);

        var on = SetChatbotFlow(r, "wf-bot-on", new { op = "chatbot", value = "on", scope = "all" });
        var run = Start(r, engine, on, c.Id);
        fresh = r.T.Fresh();
        Assert.True(AiEligibility.Effective(fresh, new ScopingService(fresh), fresh.Threads.First(x => x.Key == "60120009021")));
        Assert.True(fresh.Threads.First(x => x.Key == "tg-9021").AiEnabled);
        Assert.Contains(fresh.FlowStepEvents, e => e.RunId == run.Id && e.Detail.Contains("chatbot on · 2 conversations"));
    }

    private static Workflow SetChatbotFlow(AiRig r, string flowId, object config)
    {
        var wf = new Workflow
        {
            FlowId = flowId, Name = flowId, Active = true, LiveSince = DateTime.UtcNow.AddDays(-1),
            Nodes = new List<object?>
            {
                new { id = "trg", type = "trigger", data = new { label = "trg", config = new { when = "Manual / API", repeat = "Every time" } } },
                new { id = "bot", type = "update", data = new { label = "bot", config } },
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "bot" } },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        return wf;
    }

    // =========================================================================
    // 7. automations: ai_on / handed_over conditions, hand_over action
    // =========================================================================

    [Fact]
    public void Automation_conditions_and_the_hand_over_action()
    {
        using var r = new AiRig();
        r.Contact("Au", "60120009031");
        r.Db.AutomationRules.Add(new AutomationRule
        {
            Name = "Refunds to a person", Event = "message_created", Active = true,
            Conditions = new List<object?>
            {
                new Dictionary<string, object?> { ["field"] = "message", ["op"] = "contains", ["value"] = "refund" },
                new Dictionary<string, object?> { ["field"] = "ai_on", ["op"] = "equals", ["value"] = "yes" },
                new Dictionary<string, object?> { ["field"] = "handed_over", ["op"] = "equals", ["value"] = "no" },
            },
            Actions = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "hand_over", ["value"] = "Refund requests need a person" },
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();
        var rule = r.Db.AutomationRules.Single();

        r.Say("60120009031", "I want a refund");
        var t = r.T.Fresh().Threads.Single(x => x.Key == "60120009031");
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(HandoverKinds.Rule, t.HandoverKind);
        Assert.Equal($"rule:{rule.Id}", t.HandoverBy);
        Assert.Equal("Refund requests need a person", t.HandoverReason);
        Assert.Equal(ThreadStatus.Pending, t.Status);
        Assert.Empty(r.AiMessages("60120009031"));                                   // the AI stood down
        Assert.Equal(1, r.T.Fresh().AutomationRules.Single().TimesFired);

        // handed_over = yes now, ai_on = no: the rule no longer matches
        r.Say("60120009031", "refund please!!");
        Assert.Equal(1, r.T.Fresh().AutomationRules.Single().TimesFired);
        Assert.Equal(1, r.T.Fresh().Threads.Single(x => x.Key == "60120009031").HandoverCount);
        var svc = new AutomationService(r.Db, r.Sc);
        var probe = new AutomationRule
        {
            Conditions = new List<object?>
            {
                new Dictionary<string, object?> { ["field"] = "handed_over", ["op"] = "equals", ["value"] = "true" },
                new Dictionary<string, object?> { ["field"] = "ai_on", ["op"] = "equals", ["value"] = "off" },
            },
        };
        Assert.True(svc.WouldMatch(probe, r.ThreadOf("60120009031"), ""));
        Assert.Contains(AutomationService.ActionTypes, a => a.Id == "hand_over");
        Assert.Contains(AutomationService.Fields, f => f.Id == "ai_on");
        Assert.Contains(AutomationService.Fields, f => f.Id == "handed_over");
    }

    // =========================================================================
    // 8. every handover site stamps the columns
    // =========================================================================

    [Fact]
    public void The_ai_gate_hands_over_when_its_checks_refuse_a_reply()
    {
        using var r = new AiRig();
        r.Contact("Gh", "60120009041");
        r.Say("60120009041", "hello");
        var thread = r.ThreadOf("60120009041");
        var o = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "We can refund you in full.",
            new Dictionary<string, object?> { ["action"] = AiActions.Handover, ["reason"] = "refund policy: promises a refund" }));
        Assert.Equal(AiGateStatus.Held, o.Status);

        var t = r.T.Fresh().Threads.Single(x => x.Id == thread.Id);
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(HandoverKinds.CheckFailed, t.HandoverKind);
        Assert.Equal("ai", t.HandoverBy);
        Assert.Contains("promises a refund", t.HandoverReason);
        Assert.Equal(1, t.HandoverCount);
        Assert.Equal(ThreadStatus.Pending, t.Status);
        Assert.False(AiEligibility.Effective(r.T.Fresh(), r.Sc, t));                  // the AI stood down
        Assert.Contains(r.T.Fresh().Messages, m => m.ThreadId == t.Id && m.Private && m.Text.Contains("handed this conversation to a person"));
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == Handover.AuditAction && a.Detail.StartsWith("check_failed:"));

        // approved by a person, it still goes out although the AI is off now
        var approved = r.Gate.Approve(o.DraftId, "admin");
        Assert.True(approved.Delivered);
    }

    [Fact]
    public void The_ai_saying_a_person_will_help_is_a_handover_that_keeps_the_reply()
    {
        using var r = new AiRig();
        var boss = new AppUser { Username = "boss", IsActive = true };
        r.Db.Users.Add(boss);
        r.Db.SaveChanges();
        r.Db.Profiles.Add(new Profile { UserId = boss.Id, Role = Roles.SuperAdmin });
        r.Db.SaveChanges();
        r.Contact("Ah", "60120009042");
        r.Llm.Replies.Enqueue("Thanks — a colleague will get back to you shortly.");
        r.Say("60120009042", "I need to speak to someone");
        Assert.Single(r.AiMessages("60120009042"));                                  // the customer got the answer
        var t = r.T.Fresh().Threads.Single(x => x.Key == "60120009042");
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(HandoverKinds.AskedForHuman, t.HandoverKind);
        Assert.Contains(r.T.Fresh().Notifications, n => n.Title.StartsWith("Needs a human"));
    }

    [Fact]
    public void A_reply_blocked_at_send_time_hands_over()
    {
        using var r = new AiRig();
        var live = r.LiveAccount();
        r.Contact("Sb", "60120009043");
        // the customer's message arrived 30 h late: WhatsApp's 24h window is closed
        r.Say("60120009043", "still there?", accountId: live.Id, providerTs: DateTime.UtcNow.AddHours(-30));
        Assert.Empty(r.AiMessages("60120009043"));
        var t = r.T.Fresh().Threads.Single(x => x.Key == "60120009043");
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(HandoverKinds.SendBlocked, t.HandoverKind);
        Assert.Contains("24-hour window", t.HandoverReason);
    }

    [Fact]
    public void The_assistant_outcome_and_a_flow_hand_over_stamp_the_columns()
    {
        using var r = new AiRig();
        var c = r.Contact("Ao", "60120009044");
        r.Say("60120009044", "hi");
        var thread = r.ThreadOf("60120009044");
        r.Assistant.ApplyOutcome(thread, new AiReplyResult { Action = AiActions.Handover, Reason = "Legal threat." });
        var t = r.T.Fresh().Threads.Single(x => x.Id == thread.Id);
        Assert.Equal(HandoverKinds.CheckFailed, t.HandoverKind);
        Assert.Equal("Legal threat.", t.HandoverReason);
        Assert.Single(r.T.Fresh().Messages.Where(m => m.ThreadId == t.Id && m.Private));  // the assistant's own note only

        // a flow's Hand-over step on another contact
        var c2 = r.Contact("Fh", "60120009045");
        r.Say("60120009045", "hello");
        var wf = new Workflow
        {
            FlowId = "wf-hand", Name = "Escalate", Active = true, LiveSince = DateTime.UtcNow.AddDays(-1),
            Nodes = new List<object?>
            {
                new { id = "trg", type = "trigger", data = new { label = "trg", config = new { when = "Manual / API", repeat = "Every time" } } },
                new { id = "ho", type = "handover", data = new { label = "ho", config = new { team = "", priority = "Normal", reason = "Wants a demo" } } },
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "ho" } },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        Start(r, new FlowEngine(r.Db, r.Sc, r.Queue), wf, c2.Id);
        var t2 = r.T.Fresh().Threads.Single(x => x.Key == "60120009045");
        Assert.True(Handover.IsOpen(t2));
        Assert.Equal(HandoverKinds.Rule, t2.HandoverKind);
        Assert.Equal("flow", t2.HandoverBy);
        Assert.Equal("Wants a demo", t2.HandoverReason);
        Assert.Equal(ThreadStatus.Open, t2.Status);                                     // the team's queue picks it up
        Assert.False(t2.AiEnabled);
        _ = c;
    }

    [Fact]
    public void A_booking_that_fails_at_send_time_hands_over_as_booking_failed()
    {
        using var r = new BookingRig();
        var c = r.Contact();
        r.Skill(new { source = "chatbranch" });
        r.Set("ai_review_mode", "review_all");
        r.Say("60123000001", "Book me in please");
        var draft = Assert.Single(r.Drafts());
        var held = Assert.Single(r.FreshBookings());
        // the hold lapsed and someone else took that very time
        r.Db.Bookings.First(b => b.Id == held.Id).Status = BookingStatus.Released;
        r.Db.Bookings.Add(new Booking
        {
            ContactId = c.Id + 100, SkillId = "cal-1", CalendarKey = held.CalendarKey, Status = BookingStatus.Booked,
            StartUtc = held.StartUtc, EndUtc = held.EndUtc, Timezone = held.Timezone,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();

        Assert.Equal(AiGateStatus.Blocked, r.Gate.Approve(draft.Id, "admin").Status);
        var t = r.T.Fresh().Threads.Single();
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(HandoverKinds.BookingFailed, t.HandoverKind);   // not a generic send_blocked
        Assert.Equal(1, t.HandoverCount);
        Assert.Contains("booking could not be made", t.HandoverReason);
        // the booking's own note is the only one (no duplicate handover note)
        Assert.Single(r.Messages("60123000001").Where(m => m.Private));
    }

    [Fact]
    public void Mark_counts_once_while_open_and_clear_keeps_history()
    {
        using var r = new AiRig();
        r.Contact("Mk", "60120009046");
        r.Say("60120009046", "hi");
        var t = r.ThreadOf("60120009046");
        Assert.True(Handover.Mark(r.Db, t, HandoverKinds.LowConfidence, "Not sure", "ai"));
        var firstAt = t.HandoverAt;
        Assert.False(Handover.Mark(r.Db, t, HandoverKinds.BookingFailed, "Slot taken", "ai"));   // still open: not a new one
        Assert.Equal(1, t.HandoverCount);
        Assert.Equal(firstAt, t.HandoverAt);
        Assert.Equal(HandoverKinds.BookingFailed, t.HandoverKind);                           // the latest reason

        Assert.True(Handover.OnHumanReply(r.Db, t, "alice"));
        var fresh = r.T.Fresh().Threads.Single(x => x.Id == t.Id);
        Assert.False(Handover.IsOpen(fresh));
        Assert.NotNull(fresh.HandoverResolvedAt);
        Assert.Equal("Slot taken", fresh.HandoverReason);                                   // kept for history
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == Handover.ClearedAuditAction && a.Actor == "agent:alice");
        Assert.False(Handover.OnResolved(r.Db, t, "alice"));                                // nothing open any more

        Assert.True(Handover.Mark(r.Db, t, HandoverKinds.Manual, "again", "agent:bob"));
        Assert.Equal(2, r.T.Fresh().Threads.Single(x => x.Id == t.Id).HandoverCount);
        Assert.Null(r.T.Fresh().Threads.Single(x => x.Id == t.Id).HandoverResolvedAt);
    }
}
