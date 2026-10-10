using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Record = ChatBranch.Domain.Record;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The simplified step vocabulary (Tag added / Message received triggers,
/// Wait, Ask &amp; branch, Follow up, Update contact, Hand over, End) and the
/// per-step tracking every transition writes to FlowStepEvent — plus the
/// engine gaps the review found: timeout lines, record waits, a run's end
/// reason, audience scoping and the send-target / template-variable options.
/// </summary>
public class FlowStepTests
{
    private static FlowEngine Engine(TestDb t)
    {
        var scoping = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, scoping);
        var assistant = new AssistantService(t.Db, scoping, new PersonaService(scoping),
            new KnowledgeService(t.Db, scoping), new SandboxLlmClient());
        var sender = new OutboundSender(t.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        return new FlowEngine(t.Db, scoping, new QueueService(t.Db, scoping, threads, assistant, sender));
    }

    private static object N(string id, string type, object config, string label = "") =>
        new { id, type, data = new { label = label == "" ? id : label, config } };

    private static object E(string id, string source, string target, string? handle = null,
        string timing = "Immediately", double waitDays = 0) =>
        handle == null
            ? new { id, source, target, data = new { config = new { timing, waitDays } } }
            : new { id, source, target, sourceHandle = handle, data = new { config = new { timing, waitDays } } };

    private static Workflow AddFlow(TestDb t, string flowId, List<object?> nodes, List<object?> edges)
    {
        var wf = new Workflow { FlowId = flowId, Name = flowId, Active = true, Nodes = nodes, Edges = edges,
            LiveSince = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Workflows.Add(wf);
        t.Db.SaveChanges();
        return wf;
    }

    private static Contact AddContact(TestDb t, string name = "Ana", string phone = "60120000001",
        params string[] tags)
    {
        var c = new Contact { Name = name, Phone = phone, Email = $"{name.ToLowerInvariant()}@example.com",
            Status = "active", Tags = tags.ToList(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(c);
        t.Db.SaveChanges();
        return c;
    }

    /// <summary>Pretend the run's wait has run out.</summary>
    private static void Elapse(TestDb t, int runId)
    {
        using var db = t.Fresh();
        var run = db.FlowRuns.Single(r => r.Id == runId);
        run.WaitUntil = DateTime.UtcNow.AddSeconds(-1);
        db.SaveChanges();
    }

    private static List<(string node, string kind, string detail)> Events(TestDb t, int runId) =>
        t.Fresh().FlowStepEvents.Where(e => e.RunId == runId).OrderBy(e => e.Id)
            .Select(e => new { e.NodeId, e.Kind, e.Detail }).ToList()
            .Select(e => (e.NodeId, e.Kind, e.Detail)).ToList();

    private static List<object?> WelcomeNodes() => new()
    {
        N("trg", "trigger", new { when = "Tag added", tag = "VIP", repeat = "Once" }),
        N("act", "action", new { channels = new[] { "WhatsApp" }, script = "Hi {{first_name}}!", humanize = true }),
        N("wait", "wait", new { mode = "reply", timeoutAmount = 2, timeoutUnit = "days" }),
        N("dec", "decision", new { basedOn = "They replied" }),
        N("yes", "end", new { logCode = "REPLIED", updateMemory = "Yes" }),
        N("no", "end", new { logCode = "SILENT" }),
    };

    private static List<object?> WelcomeEdges() => new()
    {
        E("e1", "trg", "act"), E("e2", "act", "wait"), E("e3", "wait", "dec"),
        E("e4", "dec", "yes", "yes"), E("e5", "dec", "no", "no"),
    };

    [Fact]
    public void Events_are_recorded_as_a_run_enters_waits_and_completes()
    {
        using var t = new TestDb();
        AddFlow(t, "wf-welcome", WelcomeNodes(), WelcomeEdges());
        var contact = AddContact(t, "Ana", "60120000001", "vip");
        var engine = Engine(t);

        Assert.Equal(1, engine.EvaluateTriggers());
        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Waiting, run.Status);
        Assert.Equal("wait", run.CurrentNode);
        Assert.Equal("reply", run.WaitFor);
        Assert.NotNull(run.WaitUntil); // the 2-day timeout

        var before = Events(t, run.Id);
        Assert.Contains(("trg", FlowStepKind.Entered, ""), before);
        Assert.Contains(before, e => e.node == "act" && e.kind == FlowStepKind.Completed);
        Assert.Contains(before, e => e.node == "wait" && e.kind == FlowStepKind.Entered);
        Assert.Contains(before, e => e.node == "wait" && e.kind == FlowStepKind.Waiting);
        Assert.DoesNotContain(before, e => e.node == "dec");

        // the queued message carries the flow's payload hints
        var job = t.Fresh().OutboundJobs.Single();
        Assert.Equal("flow", job.Payload["source"]!.ToString());
        Assert.Equal("true", job.Payload["humanize"]!.ToString()!.ToLowerInvariant());
        Assert.Contains("Hi Ana!", job.Payload["text"]!.ToString());

        // a message that arrives while the journey's own message is still queued
        // is not an answer to it: the run stays parked on the reply
        Assert.Equal(0, engine.OnInboundMessage("whatsapp", "60120000001", "earlier message"));
        Assert.Equal(FlowRunStatus.Waiting, t.Fresh().FlowRuns.Single().Status);
        using (var db = t.Fresh())
        {
            var sentJob = db.OutboundJobs.Single();
            sentJob.Status = JobStatus.Sent;
            sentJob.SentAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Equal(1, engine.OnInboundMessage("whatsapp", "60120000001", "hello there"));

        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("yes", done.CurrentNode);
        Assert.Equal("REPLIED", done.Detail);   // Finish keeps WHY the run ended
        var after = Events(t, run.Id);
        Assert.Contains(("wait", FlowStepKind.Completed, "replied"), after);
        Assert.Contains(("dec", FlowStepKind.Completed, "yes"), after);
        Assert.Contains(("yes", FlowStepKind.Completed, "REPLIED"), after);
        // entering the wait node once, even though the run parked and resumed there
        Assert.Single(after, e => e.node == "wait" && e.kind == FlowStepKind.Entered);
        Assert.All(t.Fresh().FlowStepEvents, e => Assert.Equal("wf-welcome", e.WorkflowId));
        Assert.All(t.Fresh().FlowStepEvents, e => Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", e.Day));
        // End "remember the outcome" writes to the contact's AI memory
        Assert.Contains(t.Fresh().ConversationSummaries, s => s.ContactId == contact.Id && s.Source == "flow"
                                                              && s.Summary.Contains("REPLIED"));
        // tag triggers fire once per contact
        Assert.Equal(0, engine.EvaluateTriggers());
    }

    [Fact]
    public void Wait_for_reply_timeout_takes_the_no_branch()
    {
        using var t = new TestDb();
        AddFlow(t, "wf-welcome", WelcomeNodes(), WelcomeEdges());
        AddContact(t, "Ben", "60120000002", "VIP");
        var engine = Engine(t);
        engine.EvaluateTriggers();
        var run = t.Fresh().FlowRuns.Single();

        Elapse(t, run.Id);
        Assert.Equal(1, engine.ResumeDueRuns());
        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("no", done.CurrentNode);
        Assert.Contains(("wait", FlowStepKind.Completed, "no reply in time"), Events(t, run.Id));
    }

    [Fact]
    public void Legacy_no_answer_timeout_edge_fires_when_the_reply_never_comes()
    {
        using var t = new TestDb();
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Contact has tag", tag = "lead", repeat = "Once" }),
            N("act", "action", new { channels = new[] { "In-app push" }, script = "ping" }),
            N("dec", "decision", new { basedOn = "Contact's answer" }),
            N("ok", "end", new { logCode = "ANSWERED" }),
            N("late", "end", new { logCode = "TIMEOUT" }),
        };
        var edges = new List<object?>
        {
            E("e1", "trg", "act"),
            E("e2", "act", "dec", timing: "On contact reply"),
            E("e3", "act", "late", timing: "No answer / timeout", waitDays: 3),
            E("e4", "dec", "ok", "yes"), E("e5", "dec", "ok", "no"),
        };
        AddFlow(t, "wf-legacy", nodes, edges);
        AddContact(t, "Cy", "60120000003", "lead");
        var engine = Engine(t);
        engine.EvaluateTriggers();

        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal("reply", run.WaitFor);
        Assert.NotNull(run.WaitUntil);
        Assert.True(run.WaitUntil > DateTime.UtcNow.AddDays(2.9)); // the line's 3 days

        Elapse(t, run.Id);
        engine.ResumeDueRuns();
        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("late", done.CurrentNode);
        Assert.Equal("TIMEOUT", done.Detail);
    }

    [Fact]
    public void Record_update_wait_resumes_when_a_record_changes()
    {
        using var t = new TestDb();
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Tag added", tag = "order" }),
            N("act", "action", new { channels = new[] { "In-app push" }, script = "order placed" }),
            N("fin", "end", new { logCode = "UPDATED" }),
        };
        var edges = new List<object?> { E("e1", "trg", "act"), E("e2", "act", "fin", timing: "On record update") };
        AddFlow(t, "wf-record", nodes, edges);
        var contact = AddContact(t, "Dee", "60120000004", "order");
        t.Db.Records.Add(new Record { ContactId = contact.Id, Type = "order", Status = "placed",
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1) });
        t.Db.SaveChanges();
        var engine = Engine(t);
        engine.EvaluateTriggers();
        Assert.Equal("record", t.Fresh().FlowRuns.Single().WaitFor);

        // nothing changed yet → stays parked
        engine.ResumeDueRuns();
        Assert.Equal(FlowRunStatus.Waiting, t.Fresh().FlowRuns.Single().Status);

        using (var db = t.Fresh())
        {
            var rec = db.Records.Single();
            rec.Status = "shipped";
            rec.UpdatedAt = DateTime.UtcNow.AddSeconds(5);
            db.SaveChanges();
        }
        Assert.Equal(1, engine.ResumeDueRuns());
        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("UPDATED", done.Detail);
    }

    private static List<object?> FollowUpNodes(object fuConfig) => new()
    {
        N("trg", "trigger", new { when = "Tag added", tag = "quote" }),
        N("fu", "followup", fuConfig),
        N("r", "end", new { logCode = "REPLIED" }),
        N("n", "end", new { logCode = "GAVE_UP" }),
    };

    private static List<object?> FollowUpEdges() => new()
    {
        E("e1", "trg", "fu"), E("e2", "fu", "r", "yes"), E("e3", "fu", "n", "no"),
    };

    [Fact]
    public void Follow_up_sends_each_follow_up_then_gives_up()
    {
        using var t = new TestDb();
        AddFlow(t, "wf-fu", FollowUpNodes(new
        {
            channels = new[] { "WhatsApp" }, script = "Still keen, {{first_name}}?",
            waitAmount = 4, waitUnit = "hours", maxFollowUps = 2,
        }), FollowUpEdges());
        AddContact(t, "Eve", "60120000005", "quote");
        var engine = Engine(t);
        engine.EvaluateTriggers();
        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal("fu", run.CurrentNode);
        Assert.Empty(t.Fresh().OutboundJobs); // first wait before any follow-up

        Elapse(t, run.Id); engine.ResumeDueRuns();
        Assert.Single(t.Fresh().OutboundJobs);
        Elapse(t, run.Id); engine.ResumeDueRuns();
        Assert.Equal(2, t.Fresh().OutboundJobs.Count());
        Assert.Equal(FlowRunStatus.Waiting, t.Fresh().FlowRuns.Single().Status);

        Elapse(t, run.Id); engine.ResumeDueRuns();
        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("n", done.CurrentNode);
        var ev = Events(t, run.Id);
        Assert.Contains(("fu", FlowStepKind.FollowUp, "follow-up 1 sent"), ev);
        Assert.Contains(("fu", FlowStepKind.FollowUp, "follow-up 2 sent"), ev);
        Assert.Contains(("fu", FlowStepKind.Completed, "no reply after 2 follow-ups"), ev);
        Assert.All(t.Fresh().OutboundJobs, j => Assert.Contains("Still keen, Eve?", j.Payload["text"]!.ToString()));
    }

    [Fact]
    public void Follow_up_reply_or_stop_tag_ends_on_the_replied_branch()
    {
        using var t = new TestDb();
        AddFlow(t, "wf-fu", FollowUpNodes(new
        {
            channels = new[] { "AI message", "WhatsApp" }, script = "Nudge them kindly", skill = "as-sales",
            waitAmount = 1, waitUnit = "days", maxFollowUps = 3, stopTag = "booked",
        }), FollowUpEdges());
        AddContact(t, "Fay", "60120000006", "quote");
        var gus = AddContact(t, "Gus", "60120000007", "quote");
        var engine = Engine(t);
        Assert.Equal(2, engine.EvaluateTriggers());
        var fay = t.Fresh().FlowRuns.Single(r => r.ContactId != gus.Id);
        var gusRun = t.Fresh().FlowRuns.Single(r => r.ContactId == gus.Id);

        Elapse(t, fay.Id); engine.ResumeDueRuns();          // follow-up 1 to Fay
        var aiJob = t.Fresh().OutboundJobs.Single();
        Assert.Equal("ai", aiJob.Payload["source"]!.ToString()); // AI-written → gated by the AI monitor
        engine.OnInboundMessage("whatsapp", "60120000006", "sorry, yes!");
        var fayDone = t.Fresh().FlowRuns.Single(r => r.Id == fay.Id);
        Assert.Equal("r", fayDone.CurrentNode);
        Assert.Contains(("fu", FlowStepKind.Completed, "replied after follow-up 1"), Events(t, fay.Id));

        using (var db = t.Fresh())
        {
            var g = db.Contacts.Single(c => c.Id == gus.Id);
            g.Tags = new List<string> { "quote", "booked" };
            db.SaveChanges();
        }
        Elapse(t, gusRun.Id); engine.ResumeDueRuns();
        var gusDone = t.Fresh().FlowRuns.Single(r => r.Id == gusRun.Id);
        Assert.Equal("r", gusDone.CurrentNode);
        Assert.Contains(("fu", FlowStepKind.Completed, "stopped: tag 'booked' added"), Events(t, gusRun.Id));
    }

    [Fact]
    public void Message_received_trigger_honours_keyword_channel_and_contact_scope()
    {
        using var t = new TestDb();
        var ana = AddContact(t, "Ana", "60120000011");
        var bob = AddContact(t, "Bob", "60120000012");
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Message received", keyword = "price, quote", channel = "whatsapp",
                contacts = new[] { ana.Id.ToString() } }),
            N("dec", "decision", new { basedOn = "Reply contains", keyword = "price" }),
            N("y", "end", new { logCode = "PRICE" }),
            N("n", "end", new { logCode = "OTHER" }),
        };
        var edges = new List<object?> { E("e1", "trg", "dec"), E("e2", "dec", "y", "yes"), E("e3", "dec", "n", "no") };
        AddFlow(t, "wf-msg", nodes, edges);
        var engine = Engine(t);

        engine.OnInboundMessage("whatsapp", "60120000011", "hello");          // no keyword
        Assert.Empty(t.Fresh().FlowRuns);
        engine.OnInboundMessage("whatsapp", "60120000012", "what's the price?"); // not in scope
        Assert.Empty(t.Fresh().FlowRuns);
        engine.OnInboundMessage("telegram", "60120000011", "price please");    // wrong channel
        Assert.Empty(t.Fresh().FlowRuns);
        engine.OnInboundMessage("whatsapp", "60120000011", "What's the PRICE?");
        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal(ana.Id, run.ContactId);
        Assert.Equal("PRICE", run.Detail); // the trigger message itself is the first reply
        _ = bob;
    }

    [Fact]
    public void Update_contact_and_hand_over_act_on_the_contact_and_conversation()
    {
        using var t = new TestDb();
        var team = new Team { Name = "Sales", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Teams.Add(team);
        var user = new AppUser { Username = "sam", Password = "x", IsActive = true };
        t.Db.Users.Add(user);
        t.Db.SaveChanges();
        t.Db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        var contact = AddContact(t, "Hal", "60120000020", "hot");
        t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "60120000020", ContactId = contact.Id,
            Status = "resolved", AiEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Tag added", tag = "hot" }),
            N("tag", "update", new { op = "add_tag", tag = "qualified" }),
            N("untag", "update", new { op = "remove_tag", tag = "hot" }),
            N("field", "update", new { op = "set_field", field = "stage", value = "Lead for {{first_name}}" }),
            N("ho", "handover", new { team = team.Id.ToString(), priority = "Normal", reason = "Wants a demo" }),
            N("fin", "end", new { logCode = "HANDED_OVER" }),
        };
        var edges = new List<object?>
        {
            E("e1", "trg", "tag"), E("e2", "tag", "untag"), E("e3", "untag", "field"),
            E("e4", "field", "ho"), E("e5", "ho", "fin"),
        };
        AddFlow(t, "wf-update", nodes, edges);
        Engine(t).EvaluateTriggers();

        var fresh = t.Fresh();
        var c = fresh.Contacts.Single(x => x.Id == contact.Id);
        Assert.Contains("qualified", c.Tags);
        Assert.DoesNotContain("hot", c.Tags);
        Assert.Equal("Lead for Hal", ((JsonElement)c.Attributes["stage"]!).GetString());
        var thread = fresh.Threads.Single();
        Assert.Equal(team.Id, thread.TeamId);
        Assert.Equal("open", thread.Status);
        Assert.False(thread.AiEnabled);
        Assert.Contains(fresh.Messages, m => m.ThreadId == thread.Id && m.Private && m.Text.Contains("Sales"));
        Assert.Contains(fresh.Notifications, n => n.UserId == user.Id && n.Title.Contains("Sales"));
        Assert.Equal("HANDED_OVER", fresh.FlowRuns.Single().Detail);
    }

    [Fact]
    public void Send_to_all_linked_and_template_variables_reach_the_queue()
    {
        using var t = new TestDb();
        var contact = AddContact(t, "Ivy", "+60 12-000 0030", "promo");
        t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "60120000031", ContactId = contact.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "60120000030", ContactId = contact.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }); // same as the CRM phone
        t.Db.SaveChanges();
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Tag added", tag = "promo" }),
            N("act", "action", new
            {
                channels = new[] { "WhatsApp" }, allLinked = true, waMode = "template",
                templateName = "promo_offer", templateLanguage = "en",
                templateVariables = new Dictionary<string, string> { ["2"] = "{{company}}", ["1"] = "{{first_name}}" },
            }),
        };
        AddFlow(t, "wf-tpl", nodes, new List<object?> { E("e1", "trg", "act") });
        Engine(t).EvaluateTriggers();

        var jobs = t.Fresh().OutboundJobs.ToList();
        Assert.Equal(2, jobs.Count); // CRM phone + the one extra linked number (deduped)
        Assert.All(jobs, j =>
        {
            Assert.Equal("template", j.Kind);
            Assert.Equal("Ivy, our company", j.Payload["variables"]!.ToString());
        });
        Assert.Equal(FlowRunStatus.Done, t.Fresh().FlowRuns.Single().Status);
        Assert.Equal("end of path", t.Fresh().FlowRuns.Single().Detail);
    }

    [Fact]
    public void A_missing_step_fails_the_run_with_its_reason()
    {
        using var t = new TestDb();
        var nodes = new List<object?> { N("trg", "trigger", new { when = "Tag added", tag = "x" }) };
        AddFlow(t, "wf-broken", nodes, new List<object?> { E("e1", "trg", "ghost") });
        AddContact(t, "Jo", "60120000040", "x");
        Engine(t).EvaluateTriggers();
        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Failed, run.Status);
        Assert.Contains("ghost", run.Detail);
        Assert.Contains(t.Fresh().FlowStepEvents, e => e.RunId == run.Id && e.Kind == FlowStepKind.Failed
                                                        && e.Detail.Contains("ghost"));
    }

    [Fact]
    public void Contact_created_trigger_only_enrols_contacts_created_after_go_live()
    {
        using var t = new TestDb();
        var old = AddContact(t, "Old", "60120000050");
        old.CreatedAt = DateTime.UtcNow.AddDays(-3);
        t.Db.SaveChanges();
        var nodes = new List<object?>
        {
            N("trg", "trigger", new { when = "Contact created" }),
            N("fin", "end", new { logCode = "WELCOMED" }),
        };
        AddFlow(t, "wf-new", nodes, new List<object?> { E("e1", "trg", "fin") });
        var fresh = AddContact(t, "New", "60120000051");
        Assert.Equal(1, Engine(t).EvaluateTriggers());
        Assert.Equal(fresh.Id, t.Fresh().FlowRuns.Single().ContactId);
    }
}
