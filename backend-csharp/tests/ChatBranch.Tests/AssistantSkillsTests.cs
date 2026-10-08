using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Chat replies with code skills: tools are offered and their results reach the
/// model; reply checks gate every draft (retry with the reason → handover /
/// block); sandbox mode is unchanged; flow steps compose with a skill.
/// </summary>
public class AssistantSkillsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 30, 0, TimeSpan.Zero); // Thu 14:30 KL

    /// <summary>Scripted model: replays canned rounds, records what it saw.</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly Queue<LlmChatResult> _script;
        private readonly LlmChatResult? _repeat;
        public List<(string System, List<LlmMessage> Messages, List<string> Tools)> Seen { get; } = new();
        public ScriptedLlm(params LlmChatResult[] script) => _script = new(script);
        public ScriptedLlm(LlmChatResult repeatForever) { _script = new(); _repeat = repeatForever; }
        public bool IsConfigured => true;

        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
            CancellationToken ct = default)
        {
            Seen.Add((system, messages.ToList(), (tools ?? Array.Empty<LlmTool>()).Select(t => t.Name).ToList()));
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : _repeat ?? Says(""));
        }
    }

    private static LlmChatResult Calls(params LlmToolCall[] calls) => new("", calls, 11, 2, "test", "m");
    private static LlmChatResult Says(string text) => new(text, Array.Empty<LlmToolCall>(), 9, 4, "test", "m");

    private sealed record World(TestDb T, AssistantService Assistant, Thread Thread, Contact Contact, int AdminUserId);

    private static World Build(ILlmClient llm, string inbound = "When will my order arrive?")
    {
        var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        scoping.SetSetting("org_timezone", "Asia/Kuala_Lumpur");
        var skills = new SkillService(t.Db, scoping, new FixedClock(Now));
        var assistant = new AssistantService(t.Db, scoping, new PersonaService(scoping),
            new KnowledgeService(t.Db, scoping), llm, skills);

        var user = new AppUser { Username = "admin", Password = "x", IsActive = true };
        t.Db.Users.Add(user);
        t.Db.SaveChanges();
        t.Db.Profiles.Add(new Profile { UserId = user.Id, Role = Roles.Admin });
        var contact = new Contact { Name = "Aisha Rahman", Phone = "60123456789", Email = "aisha@example.com",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(contact);
        t.Db.SaveChanges();
        var thread = new Thread { Channel = "whatsapp", Key = "60123456789", Name = "Aisha", Status = ThreadStatus.Open,
            ContactId = contact.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Threads.Add(thread);
        t.Db.SaveChanges();
        t.Db.Messages.Add(new Message { ThreadId = thread.Id, Direction = MessageDirection.In,
            Source = MessageSource.Customer, Text = inbound, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        return new World(t, assistant, thread, contact, user.Id);
    }

    private static void AddSkill(TestDb t, string id, string name, string kind, string code = "",
        string onFail = "retry", int? entityId = null, List<string>? channels = null, bool enabled = true,
        string instructions = "", string parameters = "")
    {
        t.Db.AgentSkills.Add(new AgentSkill
        {
            SkillId = id, Name = name, Kind = kind, Code = code, OnFail = onFail, EntityId = entityId,
            Channels = channels ?? new(), Enabled = enabled, Instructions = instructions, Parameters = parameters,
        });
        t.Db.SaveChanges();
    }

    private const string NoGuarantees = """
        function check(reply, ctx) {
          return /guarantee/i.test(reply)
            ? { ok: false, reason: 'Promises a guarantee.', fix: 'Say we aim to.' }
            : { ok: true };
        }
        """;

    [Fact]
    public async Task A_tool_skill_is_offered_called_and_its_result_reaches_the_model()
    {
        var llm = new ScriptedLlm(
            Calls(new LlmToolCall("c1", "delivery_eta", "{\"days\":3}"), new LlmToolCall("c2", "current_week", "{}")),
            Says("It should arrive on Tuesday 13 October."));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-eta", "Delivery ETA", SkillKinds.Tool,
            "function run(args, ctx) { return { arrives: dates.addBusinessDays(ctx.today, args.days) }; }",
            parameters: "{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"integer\"}}}");

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Send, r.Action);
        Assert.Equal("It should arrive on Tuesday 13 October.", r.Text);
        // offered: the five built-ins + the tool skill (snake_case of its name)
        Assert.Contains("delivery_eta", llm.Seen[0].Tools);
        Assert.Contains("current_time", llm.Seen[0].Tools);
        Assert.Contains("Current date and time: Thursday, 8 October 2026, 14:30", llm.Seen[0].System);
        // the sandboxed result went back to the model as a tool message
        var toolMsgs = llm.Seen[1].Messages.Where(m => m.Role == "tool").ToList();
        Assert.Contains(toolMsgs, m => m.ToolCallId == "c1" && m.Content!.Contains("2026-10-13"));
        Assert.Contains(toolMsgs, m => m.ToolCallId == "c2" && m.Content!.Contains("\"iso_week\":41"));
        Assert.Equal(new[] { "delivery_eta", "current_week" }, r.Tools.Select(x => x.Name));
        Assert.All(r.Tools, x => Assert.True(x.Ok));
        // usage is metered per round
        var usage = w.T.Fresh().LlmUsage.Where(u => u.Feature == "chat_reply").ToList();
        Assert.Equal(2, usage.Count);
        Assert.Equal(20, r.PromptTokens);
    }

    [Fact]
    public async Task A_failing_check_retries_with_the_reason_then_hands_over()
    {
        var llm = new ScriptedLlm(Says("We guarantee delivery tomorrow!"));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-ng", "No guarantees", SkillKinds.Check, NoGuarantees);

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Handover, r.Action);
        Assert.True(r.HandoverRequested);
        Assert.False(r.ShouldSend);
        Assert.Equal("", r.Text);
        Assert.Equal("We guarantee delivery tomorrow!", r.Draft);
        Assert.Equal(2, r.Retries);
        Assert.Equal(3, llm.Seen.Count);                 // first draft + 2 retries
        Assert.Contains(llm.Seen[1].Messages, m => m.Role == "system" && m.Content!.Contains("Promises a guarantee."));
        Assert.Equal(3, r.Checks.Count(c => !c.Ok));
        Assert.Contains("No guarantees: Promises a guarantee.", r.Reason);

        var db = w.T.Fresh();
        Assert.Equal(ThreadStatus.Pending, db.Threads.Single().Status);
        var note = db.Messages.Single(m => m.Private);
        Assert.Contains("Promises a guarantee.", note.Text);
        Assert.Contains("We guarantee delivery tomorrow!", note.Text);
        Assert.Equal(1, db.Messages.Count(m => !m.Private)); // nothing was sent
        Assert.Contains(db.Notifications, n => n.UserId == w.AdminUserId && n.Link.Contains("key=60123456789"));
        Assert.Contains(db.AuditLogs, a => a.Action == "ai.reply.handover");
    }

    [Fact]
    public async Task A_handover_check_hands_over_at_once()
    {
        var llm = new ScriptedLlm(Says("Sure, I'll refund you RM5,000 today."));
        var w = Build(llm, "I want a refund or I'm calling my lawyer");
        using var _ = w.T;
        AddSkill(w.T, "as-legal", "Legal threats go to a human", SkillKinds.Check,
            "function check(reply, ctx) { return /lawyer|legal/i.test(ctx.lastMessage) ? { ok: false, reason: 'Legal threat.' } : { ok: true }; }",
            onFail: "handover");

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Handover, r.Action);
        Assert.Equal(0, r.Retries);
        Assert.Single(llm.Seen);
        Assert.Equal(ThreadStatus.Pending, w.T.Fresh().Threads.Single().Status);
    }

    [Fact]
    public async Task A_retry_that_fixes_the_draft_is_sent()
    {
        var llm = new ScriptedLlm(Says("We guarantee it."), Says("We aim to deliver by Friday."));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-ng", "No guarantees", SkillKinds.Check, NoGuarantees);

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Send, r.Action);
        Assert.Equal("We aim to deliver by Friday.", r.Text);
        Assert.Equal(1, r.Retries);
        Assert.Equal(new[] { false, true }, r.Checks.Select(c => c.Ok));
        Assert.Equal(ThreadStatus.Open, w.T.Fresh().Threads.Single().Status);
    }

    [Fact]
    public async Task Block_drops_the_reply_without_a_handover()
    {
        var llm = new ScriptedLlm(Says("We guarantee it."));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-ng", "No guarantees", SkillKinds.Check, NoGuarantees, onFail: "block");

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Block, r.Action);
        var db = w.T.Fresh();
        Assert.Equal(ThreadStatus.Open, db.Threads.Single().Status);
        Assert.Single(db.Messages.Where(m => m.Private));
        Assert.Empty(db.Notifications);
        Assert.Contains(db.AuditLogs, a => a.Action == "ai.reply.blocked");
    }

    [Fact]
    public async Task Disabled_other_channel_and_other_entity_checks_do_not_apply()
    {
        var llm = new ScriptedLlm(Says("We guarantee it."));
        var w = Build(llm);
        using var _ = w.T;
        var tenant = new Tenant { Name = "T", Slug = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.T.Db.Tenants.Add(tenant);
        w.T.Db.SaveChanges();
        var entity = new Entity { TenantId = tenant.Id, Name = "Branch", Slug = "branch",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        w.T.Db.Entities.Add(entity);
        w.T.Db.SaveChanges();
        AddSkill(w.T, "as-off", "Off", SkillKinds.Check, NoGuarantees, enabled: false);
        AddSkill(w.T, "as-mail", "Email only", SkillKinds.Check, NoGuarantees, channels: new() { "email" });
        AddSkill(w.T, "as-branch", "Branch only", SkillKinds.Check, NoGuarantees, entityId: entity.Id);

        var r = await w.Assistant.GenerateReplyAsync(w.Thread); // unassigned whatsapp thread

        Assert.Equal(AiActions.Send, r.Action);
        Assert.Empty(r.Checks);
    }

    [Fact]
    public async Task Sandbox_mode_is_unchanged()
    {
        var w = Build(new SandboxLlmClient(), "do you deliver?");
        using var _ = w.T;
        AddSkill(w.T, "as-p", "Greeting", SkillKinds.Prompt, instructions: "Greet warmly.");

        var r = await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Equal(AiActions.Send, r.Action);
        Assert.Contains("sandbox reply", r.Text);
        Assert.Contains("do you deliver?", r.Text);
        Assert.Empty(r.Tools);
        Assert.Equal("sandbox", r.Provider);
    }

    [Fact]
    public async Task Prompt_skills_ride_the_skills_block_and_code_skills_do_not()
    {
        var llm = new ScriptedLlm(Says("ok"));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-p", "Bulk pricing", SkillKinds.Prompt, instructions: "Collect quantity.");
        AddSkill(w.T, "as-c", "No guarantees", SkillKinds.Check, NoGuarantees);

        await w.Assistant.GenerateReplyAsync(w.Thread);

        Assert.Contains("- Bulk pricing: Collect quantity.", llm.Seen[0].System);
        Assert.DoesNotContain("No guarantees", llm.Seen[0].System);
    }

    [Fact]
    public async Task Follow_up_is_a_pure_generator_with_a_sandbox_nudge_and_never_repeats()
    {
        var sandbox = Build(new SandboxLlmClient());
        using (sandbox.T)
        {
            var r = await sandbox.Assistant.GenerateFollowUpAsync(sandbox.Thread, 1, null);
            Assert.Equal(AiActions.Send, r.Action);
            Assert.Equal("follow_up", r.Feature);
            Assert.StartsWith("Hi Aisha, just checking in", r.Text);
            Assert.Empty(sandbox.T.Fresh().Messages.Where(m => m.Private)); // no side effects
        }

        // a model that parrots the previous message is caught and retried
        var llm = new ScriptedLlm(Says("Your order ships Friday."), Says("Just checking — any questions about Friday's shipment?"));
        var w = Build(llm);
        using var _ = w.T;
        w.T.Db.Messages.Add(new Message { ThreadId = w.Thread.Id, Direction = MessageDirection.Out, Source = MessageSource.Ai,
            Text = "Your order ships Friday.", CreatedAt = DateTime.UtcNow });
        w.T.Db.SaveChanges();
        var f = await w.Assistant.GenerateFollowUpAsync(w.Thread, 2, "mention the shipment");
        Assert.Equal(AiActions.Send, f.Action);
        Assert.Equal("Just checking — any questions about Friday's shipment?", f.Text);
        Assert.Contains(f.Checks, c => c.SkillId == "builtin:no_repeat" && !c.Ok);
        Assert.Contains("Operator guidance: mention the shipment", llm.Seen[0].System);
    }

    // ---- flow steps (#10) ----------------------------------------------------------

    private static FlowEngine Engine(World w, ILlmClient llm, out QueueService queue)
    {
        var scoping = new ScopingService(w.T.Db);
        var threads = new ThreadStore(w.T.Db, scoping);
        var sender = new OutboundSender(w.T.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        queue = new QueueService(w.T.Db, scoping, threads, w.Assistant, sender);
        return new FlowEngine(w.T.Db, scoping, queue, null, w.Assistant);
    }

    private static void SeedFlow(TestDb t, object actionConfig)
    {
        var nodes = new List<object?>
        {
            new { id = "trg", type = "trigger", data = new { config = new { when = "Manual", repeat = "Once" } } },
            new { id = "act", type = "action", data = new { config = actionConfig } },
            new { id = "end", type = "end", data = new { config = new { logCode = "DONE" } } },
        };
        var edges = new List<object?>
        {
            new { id = "e1", source = "trg", target = "act", data = new { config = new { timing = "Immediately" } } },
            new { id = "e2", source = "act", target = "end", data = new { config = new { timing = "Immediately" } } },
        };
        t.Db.Workflows.Add(new Workflow { FlowId = "wf-ai", Name = "AI", Active = true, Nodes = nodes, Edges = edges,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
    }

    private static FlowRun StartRun(TestDb t, Contact c)
    {
        var wf = t.Db.Workflows.Single();
        // parked just before the action, already due — ResumeDueRuns walks it
        var run = new FlowRun { WorkflowId = wf.Id, ContactId = c.Id, CurrentNode = "act", Status = FlowRunStatus.Waiting,
            WaitUntil = DateTime.UtcNow.AddMinutes(-1), StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.FlowRuns.Add(run);
        t.Db.SaveChanges();
        return run;
    }

    private static void Advance(FlowEngine engine, FlowRun run, Workflow wf) => Assert.Equal(1, engine.ResumeDueRuns());

    [Theory]
    [InlineData("as-reminder")]      // the contract: SkillId
    [InlineData("Friendly reminder")] // legacy nodes stored the NAME
    public void A_flow_action_with_a_skill_composes_via_the_llm(string skillRef)
    {
        var llm = new ScriptedLlm(Says("Hi Aisha! Friendly nudge: your booking is on Friday."));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-reminder", "Friendly reminder", SkillKinds.Prompt, instructions: "Be warm and brief.");
        SeedFlow(w.T, new { channels = new[] { "WhatsApp" }, script = "Reminder for {{name}}: booking Friday.", skill = skillRef });
        var engine = Engine(w, llm, out var _q);
        var run = StartRun(w.T, w.Contact);

        Advance(engine, run, w.T.Db.Workflows.Single());

        var job = w.T.Fresh().OutboundJobs.Single();
        Assert.Equal("Hi Aisha! Friendly nudge: your booking is on Friday.", job.Payload["text"]!.ToString());
        Assert.Equal("ai", job.Payload["source"]!.ToString());
        var meta = (JsonElement)job.Payload["ai_meta"]!;
        Assert.Equal("as-reminder", meta.GetProperty("skill_id").GetString());
        Assert.Equal("flow_message", meta.GetProperty("feature").GetString());
        // the skill's instructions and the rendered script steered the model
        Assert.Contains("Be warm and brief.", llm.Seen[0].System);
        Assert.Contains("Reminder for Aisha Rahman: booking Friday.", llm.Seen[0].System);
    }

    [Fact]
    public void Without_a_skill_the_static_script_is_sent_unchanged()
    {
        var llm = new ScriptedLlm(Says("SHOULD NOT BE USED"));
        var w = Build(llm);
        using var _ = w.T;
        SeedFlow(w.T, new { channels = new[] { "WhatsApp" }, script = "Hi {{name}}, see you Friday." });
        var engine = Engine(w, llm, out var _q);

        Advance(engine, StartRun(w.T, w.Contact), w.T.Db.Workflows.Single());

        var job = w.T.Fresh().OutboundJobs.Single();
        Assert.Equal("Hi Aisha Rahman, see you Friday.", job.Payload["text"]!.ToString());
        Assert.False(job.Payload.ContainsKey("ai_meta"));
        Assert.Empty(llm.Seen);
    }

    [Fact]
    public void Ai_message_alone_sends_on_the_contacts_channel_and_a_failing_check_holds_it()
    {
        var llm = new ScriptedLlm(Says("We guarantee your refund today."));
        var w = Build(llm);
        using var _ = w.T;
        AddSkill(w.T, "as-ng", "No guarantees", SkillKinds.Check, NoGuarantees, onFail: "block");
        SeedFlow(w.T, new { channels = new[] { "AI message" }, script = "Tell them about the refund." });
        var engine = Engine(w, llm, out var _q);

        Advance(engine, StartRun(w.T, w.Contact), w.T.Db.Workflows.Single());

        var db = w.T.Fresh();
        Assert.Empty(db.OutboundJobs);   // held: nothing queued
        var run = db.FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, run.Status);
        Assert.Contains(db.AuditLogs, a => a.Action == "ai.flow_message.blocked");
        Assert.Contains("ai message held", JsonSerializer.Serialize(run.Log));
    }

    [Fact]
    public void Ai_message_alone_composes_and_queues_on_whatsapp()
    {
        var llm = new ScriptedLlm(Says("Hi Aisha, your refund was approved."));
        var w = Build(llm);
        using var _ = w.T;
        SeedFlow(w.T, new { channels = new[] { "AI message" }, script = "Refund approved." });
        var engine = Engine(w, llm, out var queue);

        Advance(engine, StartRun(w.T, w.Contact), w.T.Db.Workflows.Single());

        var job = w.T.Fresh().OutboundJobs.Single();
        Assert.Equal("whatsapp", job.Channel);
        Assert.Equal("Hi Aisha, your refund was approved.", job.Payload["text"]!.ToString());
        // delivered as an AI-sourced message
        queue.Drain(5);
        var sent = w.T.Fresh().Messages.Where(m => m.Direction == MessageDirection.Out).ToList();
        Assert.Contains(sent, m => m.Source == MessageSource.Ai && m.Text == "Hi Aisha, your refund was approved.");
    }
}
