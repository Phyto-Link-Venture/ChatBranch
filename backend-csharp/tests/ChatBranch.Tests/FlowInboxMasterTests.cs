using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A journey running FOR A CONVERSATION obeys that conversation's inbox-level AI master
/// switch: an inbox switched "off" stops the journey's AI decision ("Let AI decide") and its
/// AI message step, exactly as it stops the chat's AI replies. A journey with no
/// conversation (started by hand / API) is unchanged — there is no inbox to ask. And an
/// inbox can only ever switch the AI OFF: "on" at the inbox never overrides the tree.
/// </summary>
public class FlowInboxMasterTests
{
    private static int _n;
    private static string Phone() => "60126" + (100000 + Interlocked.Increment(ref _n)) + Random.Shared.Next(10, 99);

    private static ChannelAccount InboxOff(AiRig r, string? mode = "off")
    {
        var a = r.LiveAccount();
        if (mode != null)
        {
            InboxSettings.SetMaster(a, AiControlKeys.Master, mode);
            r.Db.SaveChanges();
        }
        return a;
    }

    private static object N(string id, string type, object config) => new { id, type, data = new { label = id, config } };

    private static Workflow DecisionFlow(AiRig r, string when = "Message received")
    {
        var wf = new Workflow
        {
            FlowId = "wf-inbox-dec-" + Guid.NewGuid().ToString("n")[..8], Name = "dec", Active = true,
            LiveSince = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Nodes = new List<object?>
            {
                N("trg", "trigger", new { when, repeat = "Every time" }),
                N("dec", "decision", new Dictionary<string, object?>
                {
                    ["basedOn"] = "AI yes/no", ["question"] = "Is the customer ready to book?", ["unsure"] = "yes",
                }),
                N("yes", "end", new { logCode = "YES" }),
                N("no", "end", new { logCode = "NO" }),
            },
            Edges = new List<object?>
            {
                new { id = "e1", source = "trg", target = "dec" },
                new { id = "e2", source = "dec", target = "yes", sourceHandle = "yes" },
                new { id = "e3", source = "dec", target = "no", sourceHandle = "no" },
            },
        };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        return wf;
    }

    private static Workflow AiMessageFlow(AiRig r, string when = "Message received")
    {
        var wf = new Workflow
        {
            FlowId = "wf-inbox-ai-" + Guid.NewGuid().ToString("n")[..8], Name = "ai", Active = true,
            LiveSince = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Nodes = new List<object?>
            {
                N("trg", "trigger", new { when, repeat = "Every time" }),
                N("ai", "action", new { channels = new[] { "AI message", "WhatsApp" }, script = "Hi {{first_name}}" }),
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "ai" } },
        };
        r.Db.Workflows.Add(wf);
        r.Db.SaveChanges();
        return wf;
    }

    private static FlowRun RunOf(AiRig r, Workflow wf) =>
        r.T.Fresh().FlowRuns.Where(x => x.WorkflowId == wf.Id).OrderByDescending(x => x.Id).First();

    private static string DecisionDetail(AiRig r, FlowRun run) =>
        r.T.Fresh().FlowStepEvents.Where(e => e.RunId == run.Id && e.NodeId == "dec" && e.Kind == FlowStepKind.Completed)
            .Select(e => e.Detail).ToList().LastOrDefault() ?? "";

    private static bool AiMessageSkipped(AiRig r, FlowRun run) =>
        r.T.Fresh().FlowStepEvents.Any(e => e.RunId == run.Id && e.Kind == FlowStepKind.Skipped
                                            && e.Detail.Contains("AI message skipped"));

    [Fact]
    public void An_inbox_switched_off_stops_the_ai_decision_of_a_journey_its_conversation_started()
    {
        using var r = new AiRig();
        var phone = Phone();
        r.Contact("Ina", phone);
        var off = InboxOff(r);
        var wf = DecisionFlow(r);
        r.Say(phone, "I would like to book a viewing", off.Id);
        var run = RunOf(r, wf);
        Assert.Contains("switched off", DecisionDetail(r, run));
        Assert.Equal("YES", r.T.Fresh().FlowRuns.Single(x => x.Id == run.Id).Detail);   // the "when unsure" path
    }

    [Fact]
    public void An_inbox_switched_off_skips_the_ai_message_step_of_a_journey_its_conversation_started()
    {
        using var r = new AiRig();
        var phone = Phone();
        r.Contact("Imo", phone);
        var off = InboxOff(r);
        var wf = AiMessageFlow(r);
        r.Say(phone, "hello", off.Id);
        var run = RunOf(r, wf);
        Assert.True(AiMessageSkipped(r, run));
        Assert.Empty(r.T.Fresh().OutboundJobs.Where(j => j.FlowRunId == run.Id));
    }

    [Fact]
    public void An_inbox_left_on_changes_nothing()
    {
        using var r = new AiRig();
        var phone = Phone();
        r.Contact("Ion", phone);
        var on = InboxOff(r, mode: null);
        var dec = DecisionFlow(r);
        var ai = AiMessageFlow(r);
        r.Say(phone, "I would like to book a viewing", on.Id);
        Assert.DoesNotContain("switched off", DecisionDetail(r, RunOf(r, dec)));
        Assert.False(AiMessageSkipped(r, RunOf(r, ai)));
    }

    [Fact]
    public void An_inbox_cannot_switch_the_ai_on_against_the_tree()
    {
        using var r = new AiRig();
        var phone = Phone();
        r.Contact("Ito", phone);
        r.Set(AiControlKeys.Master, "off");
        var inboxOn = InboxOff(r, mode: "on");
        var dec = DecisionFlow(r);
        var ai = AiMessageFlow(r);
        r.Say(phone, "I would like to book a viewing", inboxOn.Id);
        Assert.Contains("switched off", DecisionDetail(r, RunOf(r, dec)));
        Assert.True(AiMessageSkipped(r, RunOf(r, ai)));
    }

    [Fact]
    public void A_journey_with_no_conversation_keeps_todays_behaviour()
    {
        using var r = new AiRig();
        var phone = Phone();
        var c = r.Contact("Ivy", phone);
        var off = InboxOff(r);
        // the contact HAS a conversation on the switched-off inbox, but the journey was started
        // by hand — it does not run for that conversation, so the inbox layer does not apply
        r.Say(phone, "earlier chat", off.Id);
        var dec = DecisionFlow(r, when: "Manual / API");
        var ai = AiMessageFlow(r, when: "Manual / API");
        var engine = new FlowEngine(r.Db, r.Sc, r.Queue);
        engine.StartManual(dec, new[] { c.Id });
        engine.StartManual(ai, new[] { c.Id });
        Assert.DoesNotContain("switched off", DecisionDetail(r, RunOf(r, dec)));
        Assert.False(AiMessageSkipped(r, RunOf(r, ai)));
    }

    [Fact]
    public void FlowBlock_applies_an_inbox_off_only_when_given_the_inbox()
    {
        using var r = new AiRig();
        var off = InboxOff(r);
        var controls = new AiControls(r.Db, r.Sc);
        Assert.Null(controls.FlowBlock(null, null));
        Assert.NotNull(controls.FlowBlock(null, null, off));
    }
}
