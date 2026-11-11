using System.Net.Http.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The per-inbox AI MASTER SWITCH — the level below the entity, stored in
/// <see cref="ChannelAccount.Config"/> next to the behaviour overrides.
///
/// Unlike the AI behaviour settings (nearest level wins,
/// <see cref="InboxBehaviourOverrideTests"/>) the master switch is "inherit =
/// follow the level above, off anywhere up the tree wins". So an inbox may
/// switch ITSELF off, or narrow its hours, and may never answer while the
/// entity, the organization or the schedule above it says no. These tests pin
/// that invariant in both directions.
///
/// Not to be confused with <c>ai_default</c> (<see cref="InboxSettings.AiDefault"/>),
/// which is stamped on a conversation when it is created and loses to an
/// operator's per-chat tick — the last test keeps the two apart.
/// </summary>
public class InboxMasterSwitchTests
{
    private static readonly TimeZoneInfo Kl = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");

    // Mon 6 Oct 2025, KL = UTC+8
    private static readonly DateTime MonEveningKl = new(2025, 10, 6, 12, 0, 0, DateTimeKind.Utc);   // Mon 20:00 KL
    private static readonly DateTime MonAfternoonKl = new(2025, 10, 6, 6, 0, 0, DateTimeKind.Utc);  // Mon 14:00 KL

    private static AiPolicyInputs Inputs(string master = AiMasterModes.On, AiSchedule? schedule = null,
        string scheduleMode = AiScheduleModes.Inside, AiInboxMaster? inbox = null, DateTime? now = null,
        string scope = "the organization", string[]? tags = null, string[]? never = null) =>
        new(new AiMaster(master, schedule ?? new AiSchedule(), scheduleMode, null, scope, "organization", inbox),
            now ?? DateTime.UtcNow, Kl, "", tags ?? Array.Empty<string>(), never ?? Array.Empty<string>());

    private static ChannelAccount Inbox((string Key, string Value)[]? config = null,
        string name = "Website chat", string channel = "website", int id = 13, int? entityId = null)
    {
        var cfg = new Dictionary<string, object?>();
        foreach (var (k, v) in config ?? Array.Empty<(string, string)>()) cfg[k] = v;
        return new ChannelAccount
        {
            Id = id, Channel = channel, Provider = "widget", Name = name, EntityId = entityId, Config = cfg,
        };
    }

    private static Thread ThreadOn(ChannelAccount a, int? entityId = null) =>
        new() { Id = 1, Channel = a.Channel, Key = "visitor-1", AccountId = a.Id, EntityId = entityId, AiEnabled = true };

    // =========================================================================
    // 1. an inbox may switch itself OFF
    // =========================================================================

    [Fact]
    public void Inbox_off_beats_the_entity_and_the_organization_and_the_reason_names_the_inbox()
    {
        var acct = Inbox(new[] { (AiControlKeys.Master, "off") });
        var gate = AiControls.InboxMaster(acct);
        Assert.NotNull(gate);

        // the organization and the outlet are both ON — the inbox still wins
        foreach (var scope in new[] { "the organization", "North Branch" })
        {
            var v = AiEligibility.Decide(ThreadOn(acct), acct, false,
                Inputs(master: AiMasterModes.On, inbox: gate, scope: scope));
            Assert.False(v.Enabled);
            Assert.Equal(AiRules.MasterOff, v.Rule);
            Assert.True(v.Forced);                                       // above the per-chat tick
            Assert.Equal("Off: AI is switched off for the Website chat inbox", v.Reason);
            Assert.DoesNotContain(scope, v.Reason);                      // the INBOX is the level that said no
        }
    }

    [Fact]
    public void Inbox_off_beats_a_per_chat_tick_and_a_contact_set_to_always()
    {
        var acct = Inbox(new[] { (AiControlKeys.Master, "off") });
        var gate = AiControls.InboxMaster(acct);
        var t = ThreadOn(acct);
        AiEligibility.Set(t, true);                                      // an operator ticked the AI on
        var p = new AiPolicyInputs(
            new AiMaster(AiMasterModes.On, new AiSchedule(), AiScheduleModes.Inside, null, "the organization",
                "organization", gate),
            DateTime.UtcNow, Kl, ContactAiModes.Always, Array.Empty<string>(), Array.Empty<string>());
        var v = AiEligibility.Decide(t, acct, false, p);
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.MasterOff, v.Rule);
    }

    // =========================================================================
    // 2. THE INVARIANT: an inbox may never switch itself ON
    // =========================================================================

    [Fact]
    public void Inbox_on_never_overrides_the_entity_the_organization_or_the_schedule()
    {
        var acct = Inbox(new[] { (AiControlKeys.Master, "on") });
        // "on" is not a gate at all: there is nothing an inbox can add
        Assert.Null(AiControls.InboxMaster(acct));

        // the entity (or the organization) is off: the inbox's "on" changes nothing
        var off = AiEligibility.Decide(ThreadOn(acct), acct, false,
            Inputs(master: AiMasterModes.Off, inbox: AiControls.InboxMaster(acct), scope: "North Branch"));
        Assert.False(off.Enabled);
        Assert.Equal(AiRules.MasterOff, off.Rule);
        Assert.Equal("Off: AI is switched off for North Branch", off.Reason);

        // outside the level above's schedule: still off
        var hours = AiSchedule.Parse("{\"mon\":[[\"18:00\",\"23:59\"]]}");
        var outside = AiEligibility.Decide(ThreadOn(acct), acct, false,
            Inputs(master: AiMasterModes.Schedule, schedule: hours, inbox: AiControls.InboxMaster(acct),
                now: MonAfternoonKl));
        Assert.False(outside.Enabled);
        Assert.Equal(AiRules.Schedule, outside.Rule);
        Assert.Equal("Off: outside the AI schedule", outside.Reason);

        // and an inbox that asks for WIDER hours than the level above gets the narrower answer
        var wide = Inbox(new[]
        {
            (AiControlKeys.Master, "schedule"),
            (AiControlKeys.Schedule, "{\"mon\":[[\"00:00\",\"23:59\"]]}"),
        });
        var still = AiEligibility.Decide(ThreadOn(wide), wide, false,
            Inputs(master: AiMasterModes.Schedule, schedule: hours, inbox: AiControls.InboxMaster(wide),
                now: MonAfternoonKl));
        Assert.False(still.Enabled);
        Assert.Equal(AiRules.Schedule, still.Rule);
        Assert.Equal("Off: outside the AI schedule", still.Reason);      // the TREE said no first
    }

    [Fact]
    public void An_unreadable_or_empty_inbox_schedule_gates_nothing()
    {
        // "schedule" with no window would silence the inbox by accident
        Assert.Null(AiControls.InboxMaster(Inbox(new[] { (AiControlKeys.Master, "schedule") })));
        Assert.Null(AiControls.InboxMaster(Inbox(new[]
        {
            (AiControlKeys.Master, "schedule"), (AiControlKeys.Schedule, "not json"),
        })));
        // and an inbox that sets nothing inherits
        Assert.Null(AiControls.InboxMaster(Inbox()));
        Assert.Null(AiControls.InboxMaster(null));
    }

    // =========================================================================
    // 3. a per-inbox schedule, in the org timezone
    // =========================================================================

    [Fact]
    public void A_per_inbox_schedule_gates_in_the_org_timezone()
    {
        var acct = Inbox(new[]
        {
            (AiControlKeys.Master, "schedule"),
            (AiControlKeys.Schedule, "{\"mon\":[[\"18:00\",\"23:59\"]]}"),
        });
        var gate = AiControls.InboxMaster(acct)!;
        Assert.Equal(AiMasterModes.Schedule, gate.Mode);

        AiVerdict At(DateTime now) => AiEligibility.Decide(ThreadOn(acct), acct, false,
            Inputs(inbox: gate, now: now));                              // the tree is plainly ON

        Assert.True(At(MonEveningKl).Enabled);                           // Mon 20:00 KL — inside
        Assert.Contains("(inside the AI schedule)", At(MonEveningKl).Reason);
        var off = At(MonAfternoonKl);                                    // Mon 14:00 KL — outside
        Assert.False(off.Enabled);
        Assert.Equal(AiRules.Schedule, off.Rule);
        Assert.True(off.Forced);
        Assert.Equal("Off: outside the AI schedule for the Website chat inbox", off.Reason);

        // the same UTC instant is Monday EVENING in KL but Monday noon in UTC
        var utc = new AiPolicyInputs(
            new AiMaster(AiMasterModes.On, new AiSchedule(), AiScheduleModes.Inside, null, "the organization",
                "organization", gate),
            MonEveningKl, TimeZoneInfo.Utc, "", Array.Empty<string>(), Array.Empty<string>());
        Assert.False(AiEligibility.Decide(ThreadOn(acct), acct, false, utc).Enabled);

        // "outside": people inside the windows, the AI the rest of the time
        var inverted = Inbox(new[]
        {
            (AiControlKeys.Master, "schedule"),
            (AiControlKeys.Schedule, "{\"mon\":[[\"18:00\",\"23:59\"]]}"),
            (AiControlKeys.ScheduleMode, "outside"),
        });
        var ig = AiControls.InboxMaster(inverted)!;
        Assert.False(AiEligibility.Decide(ThreadOn(inverted), inverted, false,
            Inputs(inbox: ig, now: MonEveningKl)).Enabled);
        Assert.True(AiEligibility.Decide(ThreadOn(inverted), inverted, false,
            Inputs(inbox: ig, now: MonAfternoonKl)).Enabled);

        // the next flip is the inbox's, even though the tree has no schedule
        var master = new AiMaster(AiMasterModes.On, new AiSchedule(), AiScheduleModes.Inside, null, "x",
            "organization", gate);
        Assert.Equal(new DateTime(2025, 10, 6, 10, 0, 0, DateTimeKind.Utc),   // Mon 18:00 KL
            master.NextChangeUtc(MonAfternoonKl, Kl));
    }

    // =========================================================================
    // 4. resolved from the database, up the tree and down to the inbox
    // =========================================================================

    private sealed record Tree(TestDb T, ScopingService Sc, Entity Hq, Entity North, ChannelAccount Acct, Thread Thread);

    private static Tree Build(params (string Key, string Value)[] inboxConfig)
    {
        var t = new TestDb();
        var tenant = new Tenant { Name = "T", Slug = "t" };
        t.Db.Tenants.Add(tenant);
        t.Db.SaveChanges();
        var hq = new Entity { TenantId = tenant.Id, Name = "Head Office", Slug = "hq" };
        t.Db.Entities.Add(hq);
        t.Db.SaveChanges();
        var north = new Entity { TenantId = tenant.Id, Name = "North Branch", Slug = "north", ParentId = hq.Id };
        t.Db.Entities.Add(north);
        t.Db.SaveChanges();
        var cfg = new Dictionary<string, object?>();
        foreach (var (k, v) in inboxConfig) cfg[k] = v;
        var acct = new ChannelAccount
        {
            Channel = "website", Provider = "widget", Name = "Website chat", EntityId = north.Id, Config = cfg,
        };
        t.Db.ChannelAccounts.Add(acct);
        t.Db.SaveChanges();
        var thread = new Thread
        {
            Channel = "website", Key = "visitor-1", AccountId = acct.Id, EntityId = north.Id, AiEnabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Threads.Add(thread);
        t.Db.SaveChanges();
        var sc = new ScopingService(t.Db);
        // the schedule is wall-clock in the ORG timezone, which is what
        // AiControls.Zone resolves (Flows.OrgClock.SettingKey)
        sc.SetSetting("org_timezone", "Asia/Kuala_Lumpur");
        return new Tree(t, sc, hq, north, acct, thread);
    }

    [Fact]
    public void Resolved_from_the_database_the_inbox_only_ever_subtracts()
    {
        var r = Build((AiControlKeys.Master, "off"));
        using var _ = r.T;

        // the organization and the outlet are on; the inbox is off
        var v = AiEligibility.Decide(r.T.Db, r.Sc, r.Thread);
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.MasterOff, v.Rule);
        Assert.Equal("Off: AI is switched off for the Website chat inbox", v.Reason);
        // the outlet itself is untouched — a conversation with no inbox still answers
        Assert.True(new AiControls(r.T.Db, r.Sc).Master(r.North.Id).AllowsAt(DateTime.UtcNow, TimeZoneInfo.Utc));

        // now the OUTLET goes off and the inbox says "on": still off, and the
        // reason names the outlet, not the inbox
        r.Sc.SetSetting(AiControlKeys.Master, "off", r.North.Id);
        Assert.True(InboxSettings.SetMaster(r.Acct, AiControlKeys.Master, "on"));
        r.T.Db.SaveChanges();
        var forced = AiEligibility.Decide(r.T.Db, r.Sc, r.T.Fresh().Threads.First(x => x.Id == r.Thread.Id));
        Assert.False(forced.Enabled);
        Assert.Equal("Off: AI is switched off for North Branch", forced.Reason);

        // the HEAD OFFICE off, the outlet inheriting, the inbox still "on"
        r.Sc.ClearSetting(AiControlKeys.Master, r.North.Id);
        r.Sc.SetSetting(AiControlKeys.Master, "off", r.Hq.Id);
        var inherited = AiEligibility.Decide(r.T.Db, r.Sc, r.T.Fresh().Threads.First(x => x.Id == r.Thread.Id));
        Assert.False(inherited.Enabled);
        Assert.Equal("Off: AI is switched off for Head Office", inherited.Reason);

        // the ORGANIZATION off, nothing else set, the inbox still "on"
        r.Sc.ClearSetting(AiControlKeys.Master, r.Hq.Id);
        r.Sc.SetSetting(AiControlKeys.Master, "off");
        var org = AiEligibility.Decide(r.T.Db, r.Sc, r.T.Fresh().Threads.First(x => x.Id == r.Thread.Id));
        Assert.False(org.Enabled);
        Assert.Equal("Off: AI is switched off for the organization", org.Reason);
    }

    [Fact]
    public void An_inbox_schedule_narrows_the_outlets_schedule_and_never_widens_it()
    {
        var r = Build((AiControlKeys.Master, "schedule"),
            (AiControlKeys.Schedule, "{\"mon\":[[\"20:00\",\"23:59\"]]}"));
        using var _ = r.T;
        // the outlet answers Monday evening from 18:00, this inbox only from 20:00
        r.Sc.SetSetting(AiControlKeys.Master, "schedule", r.North.Id);
        r.Sc.SetSetting(AiControlKeys.Schedule, "{\"mon\":[[\"18:00\",\"23:59\"]]}", r.North.Id);

        var ai = new AiControls(r.T.Db, r.Sc, MonEveningKl);             // Mon 20:00 KL
        Assert.True(ai.Master(r.North.Id, r.Acct).AllowsAt(MonEveningKl, Kl));
        var sevenPm = new DateTime(2025, 10, 6, 11, 0, 0, DateTimeKind.Utc);   // Mon 19:00 KL
        // the outlet would answer, the inbox would not
        Assert.True(new AiControls(r.T.Db, r.Sc, sevenPm).Master(r.North.Id).AllowsAt(sevenPm, Kl));
        Assert.False(new AiControls(r.T.Db, r.Sc, sevenPm).Master(r.North.Id, r.Acct).AllowsAt(sevenPm, Kl));

        var v = new AiControls(r.T.Db, r.Sc, sevenPm).Decide(r.Thread, r.Acct);
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.Schedule, v.Rule);
        Assert.Equal("Off: outside the AI schedule for the Website chat inbox", v.Reason);
    }

    [Fact]
    public void Another_inboxs_switch_never_decides_this_conversation()
    {
        // a list payload hands the verdict a STAND-IN inbox when it does not know
        // the conversation's own (the inbox list falls back to the sole configured
        // WhatsApp number) — that inbox's switch must decide nothing here
        var r = Build((AiControlKeys.Master, "off"));
        using var _ = r.T;
        var other = new ChannelAccount
        {
            Channel = "website", Provider = "widget", Name = "Other widget", EntityId = r.North.Id,
            Config = new Dictionary<string, object?>(),
        };
        r.T.Db.ChannelAccounts.Add(other);
        r.T.Db.SaveChanges();

        var onOther = new Thread
        {
            Channel = "website", Key = "visitor-2", AccountId = other.Id, EntityId = r.North.Id, AiEnabled = true,
        };
        var unbound = new Thread
        {
            Channel = "website", Key = "visitor-3", AccountId = null, EntityId = r.North.Id, AiEnabled = true,
        };
        // r.Acct is the inbox that is switched OFF, passed for the wrong thread
        Assert.True(new AiControls(r.T.Db, r.Sc).Decide(onOther, r.Acct, "", null).Enabled);
        Assert.True(new AiControls(r.T.Db, r.Sc).Decide(unbound, r.Acct, "", null).Enabled);
        // its own conversation is still gated
        Assert.False(new AiControls(r.T.Db, r.Sc).Decide(r.Thread, r.Acct, "", null).Enabled);
        // …and the same goes for the never-tags: they come from the thread's inbox
        Assert.True(InboxSettings.SetMaster(r.Acct, AiControlKeys.NeverTags, "walk-in"));
        r.T.Db.SaveChanges();
        Assert.True(new AiControls(r.T.Db, r.Sc)
            .Decide(onOther, r.Acct, "", new[] { "walk-in" }).Enabled);
    }

    // =========================================================================
    // 5. never-AI tags ADD to the inherited set
    // =========================================================================

    [Fact]
    public void Inbox_never_tags_add_to_the_inherited_set_rather_than_replacing_it()
    {
        var r = Build((AiControlKeys.NeverTags, "walk-in"));
        using var _ = r.T;
        r.Sc.SetSetting(AiControlKeys.NeverTags, "VIP, complaint");          // organization
        r.Sc.SetSetting(AiControlKeys.NeverTags, "legal", r.North.Id);       // outlet

        var ai = new AiControls(r.T.Db, r.Sc);
        Assert.Equal(new[] { "complaint", "legal", "vip" },
            ai.NeverTags(r.North.Id).Select(x => x.ToLowerInvariant()).OrderBy(x => x));
        Assert.Equal(new[] { "complaint", "legal", "vip", "walk-in" },
            ai.NeverTags(r.North.Id, r.Acct).Select(x => x.ToLowerInvariant()).OrderBy(x => x));

        // a contact the INBOX silences, through the one eligibility funnel
        var contact = new Contact { Name = "Walk in", Phone = "60120001234", Tags = new List<string> { "Walk-in" },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.T.Db.Contacts.Add(contact);
        r.T.Db.SaveChanges();                                                // it needs its id first
        r.Thread.ContactId = contact.Id;
        r.T.Db.SaveChanges();
        var v = new AiControls(r.T.Db, r.Sc).Decide(r.Thread, r.Acct, "", contact.Tags);
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.NeverTag, v.Rule);
        Assert.Contains("Walk-in", v.Reason);
        // the levels above never lose theirs
        var vip = new AiControls(r.T.Db, r.Sc).Decide(r.Thread, r.Acct, "", new[] { "VIP" });
        Assert.Equal(AiRules.NeverTag, vip.Rule);
        // and another inbox in the same outlet is unaffected by this inbox's list
        Assert.DoesNotContain("walk-in",
            new AiControls(r.T.Db, r.Sc).NeverTags(r.North.Id).Select(x => x.ToLowerInvariant()));
    }

    // =========================================================================
    // 6. clearing an override, and ai_default is a different thing
    // =========================================================================

    [Fact]
    public void Clearing_the_inbox_override_returns_it_to_inheriting()
    {
        var acct = Inbox();
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, "off"));
        Assert.Equal("off", InboxSettings.MasterOwn(acct, AiControlKeys.Master));
        Assert.NotNull(AiControls.InboxMaster(acct));

        Assert.False(InboxSettings.SetMaster(acct, AiControlKeys.Master, "off"));       // no-op
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, ""));           // blank clears
        Assert.Equal("", InboxSettings.MasterOwn(acct, AiControlKeys.Master));
        Assert.False(acct.Config.ContainsKey(AiControlKeys.Master));
        Assert.Null(AiControls.InboxMaster(acct));
        Assert.True(AiEligibility.Decide(ThreadOn(acct), acct, false,
            Inputs(inbox: AiControls.InboxMaster(acct))).Enabled);

        // "inherit" clears too, and the values are normalized on the way in
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, "disabled"));
        Assert.Equal("off", InboxSettings.MasterOwn(acct, AiControlKeys.Master));
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, "inherit"));
        Assert.Null(AiControls.InboxMaster(acct));

        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Schedule, "{\"mon\":[\"18:00\",\"23:59\"]}"));
        Assert.Equal("{\"mon\":[[\"18:00\",\"23:59\"]]}", InboxSettings.MasterOwn(acct, AiControlKeys.Schedule));
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.NeverTags, " VIP , vip , legal "));
        Assert.Equal("VIP, legal", InboxSettings.MasterOwn(acct, AiControlKeys.NeverTags));

        // only the four master keys, and they stay out of the behaviour overrides
        Assert.False(InboxSettings.SetMaster(acct, "not_a_setting", "1"));
        Assert.False(acct.Config.ContainsKey("not_a_setting"));
        Assert.Equal("", InboxSettings.MasterOwn(acct, AiBehaviourSettings.WaitSeconds));
        Assert.Empty(InboxSettings.BehaviourOverrides(acct));
        Assert.Equal(2, InboxSettings.MasterOverrides(acct).Count);
    }

    [Fact]
    public void Ai_default_is_not_the_master_switch()
    {
        // ai_default: stamped on a NEW conversation, and an operator's tick wins
        var legacy = Inbox(new[] { (InboxSettings.AiDefaultKey, "off") }, name: "Line 3", channel: "whatsapp");
        legacy.Config = new Dictionary<string, object?>(legacy.Config) { ["role"] = AccountRoles.Conversation };
        Assert.False(InboxSettings.AiDefault(legacy));
        Assert.Null(AiControls.InboxMaster(legacy));                      // NOT a master-switch override

        var t = new Thread { Channel = "whatsapp", Key = "6012", AccountId = legacy.Id, AiEnabled = false };
        var byDefault = AiEligibility.Decide(t, legacy, false, Inputs());
        Assert.False(byDefault.Enabled);
        Assert.Equal(AiRules.InboxDefault, byDefault.Rule);
        Assert.False(byDefault.Forced);                                   // an operator may still tick it on
        Assert.Equal("Off: the Line 3 inbox has AI off by default", byDefault.Reason);
        AiEligibility.Set(t, true);
        Assert.True(AiEligibility.Decide(t, legacy, false, Inputs()).Enabled);

        // the master switch is a LIVE gate: the same tick cannot beat it
        Assert.True(InboxSettings.SetMaster(legacy, AiControlKeys.Master, "off"));
        var gated = AiEligibility.Decide(t, legacy, false, Inputs(inbox: AiControls.InboxMaster(legacy)));
        Assert.False(gated.Enabled);
        Assert.Equal(AiRules.MasterOff, gated.Rule);
        Assert.True(gated.Forced);
        // and the two live side by side: ai_default survives the master-switch write
        Assert.Equal("off", InboxSettings.AiDefaultWire(legacy));
        Assert.True(InboxSettings.SetMaster(legacy, AiControlKeys.Master, ""));
        Assert.Equal("off", InboxSettings.AiDefaultWire(legacy));
        Assert.Equal(AiRules.ChatTick, AiEligibility.Decide(t, legacy, false, Inputs()).Rule);
    }

    // =========================================================================
    // 7. the real inbound path
    // =========================================================================

    [Fact]
    public void An_inbox_switched_off_answers_nothing_on_the_real_inbound_path()
    {
        using var r = new AiRig();
        r.Contact("Ina", "60120009101");
        var acct = r.LiveAccount();

        // the organization is on, the inbox is off: not a word, not even a draft
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, "off"));
        r.Db.SaveChanges();
        r.Llm.Replies.Enqueue("Sure, we are open!");
        r.Say("60120009101", "Are you open?", accountId: acct.Id);
        Assert.Empty(r.AiMessages("60120009101"));
        Assert.Empty(r.Drafts("60120009101"));
        var t = r.ThreadOf("60120009101");
        Assert.False(AiEligibility.Effective(r.Db, r.Sc, t));
        Assert.Equal("Off: AI is switched off for the Live inbox", AiEligibility.Decide(r.Db, r.Sc, t).Reason);

        // back to inheriting: the AI answers again
        Assert.True(InboxSettings.SetMaster(acct, AiControlKeys.Master, ""));
        r.Db.SaveChanges();
        r.Say("60120009101", "Hello?", accountId: acct.Id);
        Assert.Single(r.AiMessages("60120009101"));
    }
}

/// <summary>
/// The per-inbox master switch over HTTP: <c>?account=</c> on
/// GET/PUT/DELETE /api/ai/controls — which level every value came from, what it
/// inherits, the audit rows, and the invariant that an inbox's "on" never beats
/// the outlet above it.
/// </summary>
public class InboxMasterSwitchApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public InboxMasterSwitchApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T WithDb<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _factory.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>An outlet of its own plus one website inbox inside it.</summary>
    private (string Slug, int AccountId) Outlet(string slug)
    {
        return WithDb(db =>
        {
            var e = db.Entities.FirstOrDefault(x => x.Slug == slug);
            if (e == null)
            {
                e = new Entity
                {
                    TenantId = db.Tenants.First().Id, Name = slug, Slug = slug,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                db.Entities.Add(e);
                db.SaveChanges();
            }
            var a = db.ChannelAccounts.FirstOrDefault(x => x.ExternalId == $"W-{slug}");
            if (a == null)
            {
                a = new ChannelAccount
                {
                    Channel = "website", Provider = "widget", Name = $"{slug} widget", ExternalId = $"W-{slug}",
                    EntityId = e.Id, CreatedAt = DateTime.UtcNow,
                    Config = new Dictionary<string, object?>(),
                };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
            }
            return (e.Slug, a.Id);
        });
    }

    private static async Task<System.Text.Json.JsonElement> Get(HttpClient c, string query) =>
        await c.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/ai/controls{query}");

    private static string Str(System.Text.Json.JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";

    [Fact]
    public async Task Account_level_reports_its_own_values_what_it_inherits_and_can_only_turn_off()
    {
        var c = await Authed();
        var (slug, accountId) = Outlet("inbox-switch");

        // 1. nothing of its own: the inbox follows its outlet
        var fresh = await Get(c, $"?account={accountId}");
        Assert.Equal("account", Str(fresh, "level"));
        Assert.True(fresh.GetProperty("own_only_turns_off").GetBoolean());
        Assert.Equal("", Str(fresh.GetProperty("own"), "mode"));
        Assert.Equal(accountId, fresh.GetProperty("account").GetProperty("id").GetInt32());
        Assert.Equal(slug, Str(fresh.GetProperty("account"), "entity"));          // the owning outlet's slug
        Assert.Equal(0, fresh.GetProperty("account").GetProperty("overrides").GetInt32());
        Assert.True(fresh.GetProperty("now_on").GetBoolean());
        Assert.Equal("on", Str(fresh, "mode"));
        Assert.NotEqual("account", Str(fresh.GetProperty("scopes"), "mode"));

        // 2. the inbox switches ITSELF off — the outlet is untouched
        var off = await c.PutAsJsonAsync($"/api/ai/controls?account={accountId}", new { mode = "off" });
        Assert.Equal(System.Net.HttpStatusCode.OK, off.StatusCode);
        var body = await off.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("off", Str(body.GetProperty("own"), "mode"));
        Assert.Equal("off", Str(body, "mode"));
        Assert.Equal("account", Str(body, "mode_from"));
        Assert.Equal("account", Str(body.GetProperty("scopes"), "mode"));
        Assert.False(body.GetProperty("now_on").GetBoolean());
        Assert.True(body.GetProperty("inherited").GetProperty("now_on").GetBoolean());   // the outlet still answers
        Assert.Contains("widget", Str(body, "label"));
        Assert.Equal(1, body.GetProperty("account").GetProperty("overrides").GetInt32());
        var outlet = await Get(c, $"?entity={slug}");
        Assert.Equal("on", Str(outlet, "mode"));
        Assert.True(outlet.GetProperty("now_on").GetBoolean());
        Assert.Null(outlet.GetProperty("account").ValueKind == System.Text.Json.JsonValueKind.Null ? null : "set");
        Assert.True(WithDb(db => db.AuditLogs.Any(a => a.Action == "ai.controls.update"
            && a.Target == $"inbox:{accountId}")));

        // 3. THE INVARIANT: the outlet goes off, the inbox says "on" — still off
        (await c.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "off" })).EnsureSuccessStatusCode();
        var on = await c.PutAsJsonAsync($"/api/ai/controls?account={accountId}", new { mode = "on" });
        on.EnsureSuccessStatusCode();
        var forced = await on.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("on", Str(forced.GetProperty("own"), "mode"));                      // stored…
        Assert.False(forced.GetProperty("now_on").GetBoolean());                         // …and powerless
        Assert.False(forced.GetProperty("inherited").GetProperty("now_on").GetBoolean());
        Assert.Equal("off", Str(forced.GetProperty("inherited"), "mode"));

        // 4. back to inheriting
        (await c.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "on" })).EnsureSuccessStatusCode();
        var cleared = await c.DeleteAsync($"/api/ai/controls?account={accountId}");
        cleared.EnsureSuccessStatusCode();
        var after = await cleared.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("", Str(after.GetProperty("own"), "mode"));
        Assert.Equal(0, after.GetProperty("account").GetProperty("overrides").GetInt32());
        Assert.True(after.GetProperty("now_on").GetBoolean());
        Assert.True(WithDb(db => db.AuditLogs.Any(a => a.Action == "ai.controls.reset"
            && a.Target == $"inbox:{accountId}")));
    }

    [Fact]
    public async Task An_inbox_schedule_needs_a_window_and_its_tags_add_to_the_ones_above()
    {
        var c = await Authed();
        var (slug, accountId) = Outlet("inbox-switch-2");

        // a schedule with no window of its own is refused (it would silence the inbox)
        var empty = await c.PutAsJsonAsync($"/api/ai/controls?account={accountId}", new { mode = "schedule" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, empty.StatusCode);

        var ok = await c.PutAsJsonAsync($"/api/ai/controls?account={accountId}", new
        {
            mode = "schedule",
            schedule = new Dictionary<string, string[][]> { ["mon"] = new[] { new[] { "18:00", "23:59" } } },
            never_tags = new[] { "walk-in" },
        });
        ok.EnsureSuccessStatusCode();
        var body = await ok.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("schedule", Str(body.GetProperty("own"), "mode"));
        Assert.Equal("18:00", body.GetProperty("own").GetProperty("schedule").GetProperty("mon")[0][0].GetString());
        Assert.Contains("walk-in", body.GetProperty("never_tags").EnumerateArray().Select(x => x.GetString()));

        // the outlet's tags are added to, not replaced
        (await c.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { never_tags = new[] { "legal" } }))
            .EnsureSuccessStatusCode();
        var both = await Get(c, $"?account={accountId}");
        var tags = both.GetProperty("never_tags").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains("walk-in", tags);
        Assert.Contains("legal", tags);
        Assert.Contains("legal", both.GetProperty("inherited_never_tags").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("walk-in",
            both.GetProperty("inherited_never_tags").EnumerateArray().Select(x => x.GetString()));
        // and the outlet itself never sees the inbox's tag
        var outlet = await Get(c, $"?entity={slug}");
        Assert.DoesNotContain("walk-in", outlet.GetProperty("never_tags").EnumerateArray().Select(x => x.GetString()));

        // one key back to inheriting
        (await c.DeleteAsync($"/api/ai/controls?account={accountId}&key=ai_never_tags")).EnsureSuccessStatusCode();
        var narrowed = await Get(c, $"?account={accountId}");
        Assert.DoesNotContain("walk-in", narrowed.GetProperty("never_tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("schedule", Str(narrowed.GetProperty("own"), "mode"));      // the rest is still its own
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await c.DeleteAsync($"/api/ai/controls?account={accountId}&key=nope")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await c.GetAsync("/api/ai/controls?account=999999")).StatusCode);
        // the organization level is edited, not deleted
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.DeleteAsync("/api/ai/controls")).StatusCode);
    }
}
