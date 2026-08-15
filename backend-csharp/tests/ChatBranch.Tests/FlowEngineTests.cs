using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Record = ChatBranch.Domain.Record;

namespace ChatBranch.Tests;

/// <summary>
/// End-to-end flow interpreter: a trigger enrolls an overdue contact, the action
/// node queues a WhatsApp send, the run parks on reply, and an inbound message
/// resumes it through a Decision to the matching End.
/// </summary>
public class FlowEngineTests
{
    private static FlowEngine BuildEngine(TestDb t, out QueueService queue)
    {
        var scoping = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, scoping);
        var assistant = new AssistantService(t.Db, scoping, new PersonaService(scoping),
            new KnowledgeService(t.Db, scoping), new SandboxLlmClient());
        var sender = new OutboundSender(t.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        queue = new QueueService(t.Db, scoping, threads, assistant, sender);
        return new FlowEngine(t.Db, scoping, queue);
    }

    private static void SeedWorkflow(TestDb t)
    {
        // trigger --immediately--> action --on reply--> decision --yes--> end-yes
        //                                                        \--no---> end-no
        var nodes = new List<object?>
        {
            new { id = "trg", type = "trigger", data = new { config = new { when = "Days after due date", days = "1", repeat = "Once" } } },
            new { id = "act", type = "action", data = new { config = new { channels = new[] { "WhatsApp" }, script = "Hi {{name}}, you owe {{amount}}." } } },
            new { id = "dec", type = "decision", data = new { config = new { basedOn = "Contact's answer" } } },
            new { id = "yes", type = "end", data = new { config = new { logCode = "PROMISED" } } },
            new { id = "no", type = "end", data = new { config = new { logCode = "REFUSED" } } },
        };
        var edges = new List<object?>
        {
            new { id = "e1", source = "trg", target = "act", data = new { config = new { timing = "Immediately" } } },
            new { id = "e2", source = "act", target = "dec", data = new { config = new { timing = "On contact reply" } } },
            new { id = "e3", source = "dec", target = "yes", sourceHandle = "yes", data = new { config = new { timing = "Immediately" } } },
            new { id = "e4", source = "dec", target = "no", sourceHandle = "no", data = new { config = new { timing = "Immediately" } } },
        };
        t.Db.Workflows.Add(new Workflow { FlowId = "wf-test", Name = "Reminder", Active = true,
            Nodes = nodes, Edges = edges, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
    }

    [Fact]
    public void Trigger_enrolls_queues_send_parks_then_reply_resumes_to_end()
    {
        using var t = new TestDb();
        SeedWorkflow(t);
        var contact = new Contact { Name = "Sam", Phone = "60123456789", Language = "en",
            Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(contact);
        t.Db.SaveChanges();
        // overdue record: due 5 days ago → od = +5 ≥ 1 → matches "Days after due date"
        t.Db.Records.Add(new Record { ContactId = contact.Id, Type = "invoice", Status = "open",
            Amount = 250m, Currency = "MYR", DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var engine = BuildEngine(t, out var queue);

        // 1) trigger enrolls + advances to the action, which queues a WhatsApp job,
        //    then parks on reply.
        var started = engine.EvaluateTriggers();
        Assert.Equal(1, started);

        var run = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Waiting, run.Status);
        Assert.Equal("reply", run.WaitFor);

        var job = t.Fresh().OutboundJobs.Single();
        Assert.Equal("whatsapp", job.Channel);
        Assert.Equal("text", job.Kind);
        Assert.Contains("Hi Sam", job.Payload["text"]!.ToString());
        Assert.Contains("MYR 250.00", job.Payload["text"]!.ToString());

        // 2) re-running triggers must NOT enroll a second run (dedupe)
        Assert.Equal(0, engine.EvaluateTriggers());

        // 3) an affirmative inbound resumes the run → Decision yes → end
        var resumed = engine.OnInboundMessage("whatsapp", "60123456789", "yes ok I will pay");
        Assert.Equal(1, resumed);

        var done = t.Fresh().FlowRuns.Single();
        Assert.Equal(FlowRunStatus.Done, done.Status);
        Assert.Equal("yes", done.CurrentNode);
    }

    [Fact]
    public void Reply_classifier_is_deterministic_en_and_bm()
    {
        Assert.Equal("yes", FlowEngine.ClassifyReply("boleh, saya bayar"));
        Assert.Equal("no", FlowEngine.ClassifyReply("tak boleh lah"));
        Assert.Equal("yes", FlowEngine.ClassifyReply("sure thing"));
        Assert.Equal("no", FlowEngine.ClassifyReply("no comment"));
    }
}
