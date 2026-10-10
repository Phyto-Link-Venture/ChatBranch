using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>Routing &amp; delivery, scoping &amp; access, flows &amp; automations.</summary>
public static class OperationScenarios
{
    public static IEnumerable<LabScenario> All() => new[]
    {
        // ---- routing & delivery -----------------------------------------------------------
        new LabScenario("redirect-reminder-number", "routing",
            "A customer replies to the business (reminder) number — one redirect to the chat number",
            "A reminder number with the chatbot off answers ONE queued pointer to the conversation number per 24 hours.",
            new[]
            {
                "A known contact writes to the lab's business line.",
                "The queue drains; the customer writes again.",
            },
            new[]
            {
                "One redirect is queued on the business line with a wa.me link to the conversation line, and sent.",
                "No AI reply, and no second redirect within 24 hours.",
            },
            RedirectAsync),

        new LabScenario("failover-banned-number", "routing",
            "A banned number fails over — the chat moves to a healthy number",
            "When the number a conversation lives on is banned, the next send moves it to a healthy number with a note and an audit row.",
            new[]
            {
                "A contact chats on lab number A.",
                "Number A is reported banned (the health writer a gateway push uses).",
                "The front-desk agent sends a reply.",
            },
            new[] { "The reply goes out from another lab number; the chat moved there with a system note and an account.failover audit row." },
            FailoverAsync),

        new LabScenario("quiet-hours-wait", "routing",
            "Quiet hours make sends wait — then they go out",
            "During an inbox's quiet hours the AI's reply and queued messages wait in the queue instead of being sent.",
            new[]
            {
                "An inbox quiet all day, every day.",
                "A contact writes; a message is queued for them.",
                "The queue drains; quiet hours are lifted; the queue drains again.",
            },
            new[] { "While quiet: nothing is sent, both jobs stay queued.", "After: the AI reply and the message are delivered." },
            QuietHoursAsync),

        // ---- scoping & access ----------------------------------------------------------------
        new LabScenario("scope-branch-chat", "access",
            "An agent can't open another branch's chat",
            "Agents assigned to a branch see only that branch's conversations: another branch's chat is a 404 and never listed.",
            new[]
            {
                "One chat on the North line, one on the South line.",
                "The North agent opens both, lists the inbox, and tries to reply in the South chat.",
            },
            new[] { "North chat: 200 and listed. South chat: 404, not listed, reply refused with 404." },
            BranchChatAsync),

        new LabScenario("scope-link-out-of-scope", "access",
            "An agent can't link another branch's contact",
            "Linking a chat to a contact needs a contact inside the caller's scope.",
            new[] { "An unknown number writes to the North line.", "The North agent links it to a South contact, then to a North contact." },
            new[] { "The South contact: 404 (not found). The North contact: linked." },
            LinkScopeAsync),

        new LabScenario("read-gates-no-capability", "access",
            "A role with no capabilities is refused every gated page",
            "Knowing a page's URL isn't enough: each gated API answers 403 to a role without its capability, while the shell still loads.",
            new[] { "The lab's no-access role calls the APIs behind Contacts, Chats, AI monitor, Flows, Dashboard and Tags." },
            new[] { "Each answers 403; whoami answers 200 with no capabilities; the Live chip shows nothing waiting." },
            ReadGatesAsync),

        // ---- flows & automations ---------------------------------------------------------------
        new LabScenario("flow-welcome-new-contact", "automation",
            "Welcome flow: a new contact gets a welcome message",
            "A live “Contact created” flow enrols a contact created after it went live, queues the step's message and the queue delivers it.",
            new[]
            {
                "Arm the lab flow “Welcome series”.",
                "Create a contact; run the flow tick; drain the queue.",
            },
            new[] { "A run exists for the contact; the welcome (with their first name) is queued, then delivered in their WhatsApp chat." },
            WelcomeFlowAsync),

        new LabScenario("flow-keyword-menu", "automation",
            "“Message received” keyword flow answers MENU",
            "A flow triggered by a keyword starts from the inbound message and sends its reply through the queue.",
            new[] { "Arm the lab flow “MENU keyword”.", "A contact writes “MENU please”; drain the queue." },
            new[] { "A run started from the message; today's menu is delivered in the same chat." },
            KeywordFlowAsync),

        new LabScenario("automation-refund-label-team", "automation",
            "Automation: “refund” labels the chat and routes it to Billing",
            "An event-driven rule matches the message text and applies its actions in order.",
            new[] { "Activate the lab rule: message contains “refund” → add label refund, assign team Billing.", "A contact asks for a refund." },
            new[] { "The chat carries the label and belongs to the Billing team; the rule counted the firing." },
            RefundRuleAsync),

        new LabScenario("team-round-robin", "automation",
            "Round-robin assigns new chats to team members in turn",
            "An inbox owned by an auto-assign team hands each new conversation to the next member.",
            new[] { "An inbox owned by the lab's round-robin team (two agents).", "Three new customers write." },
            new[] { "The team owns all three; assignees alternate first, second, first." },
            RoundRobinAsync),
    };

    // ---- routing & delivery ----------------------------------------------------------------------

    private static Task RedirectAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Suresh Pillai");
        var key = LabContext.WaKey(c);
        // the contact's home is the lab's conversation line (their sticky assignment), so the
        // pointer never depends on which other lab numbers earlier runs left behind
        ctx.Db.AccountAssignments.Add(new AccountAssignment
        {
            ContactId = c.Id, Channel = "whatsapp", AccountId = ctx.Lab.Conversation.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.Db.SaveChanges();
        ctx.Say(ctx.Lab.Business, key, "Hi, I got your reminder — can I change my slot?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The chat is on the business line", t.AccountId == ctx.Lab.Business.Id);
        List<OutboundJob> Redirects() => ctx.Db.OutboundJobs.AsNoTracking()
            .Where(j => j.Channel == "whatsapp" && j.ToAddress == key).ToList()
            .Where(j => AiGate.Bool(j.Payload, "redirect") == true).ToList();
        var jobs = Redirects();
        var job = jobs.FirstOrDefault();
        ctx.Check("One redirect was queued on the business line", jobs.Count == 1 && job!.AccountId == ctx.Lab.Business.Id,
            $"{jobs.Count} redirect job(s)");
        var text = job == null ? "" : AiGate.Str(job.Payload, "text", "");
        ctx.Check("It points to the conversation line (wa.me link)", text.Contains("wa.me/60300000101"), AiScenarios.Trim(text));
        ctx.Check("No AI reply on the business line", ctx.Replies(t.Id).Count == 0);
        if (job == null) return Task.CompletedTask;
        ctx.Drain(() => ctx.Job(job.Id)?.Status is JobStatus.Sent or JobStatus.Failed);
        ctx.Check("The redirect was delivered", ctx.Job(job.Id)?.Status == JobStatus.Sent, ctx.Job(job.Id)?.LastError ?? "");
        ctx.Check("…into the chat", ctx.MessagesOf(t.Id).Any(m => m.Direction == MessageDirection.Out && m.Text.Contains("wa.me/")));
        ctx.Say(ctx.Lab.Business, key, "Hello?", c.Name);
        ctx.Check("No second redirect within 24 hours", Redirects().Count == 1, $"{Redirects().Count} redirect job(s)");
        return Task.CompletedTask;
    }

    private static async Task FailoverAsync(LabContext ctx)
    {
        var a = ctx.Inbox("failover A", new());
        ctx.Inbox("failover B", new());
        var c = ctx.NewContact("Wei Jie Ng");
        var key = LabContext.WaKey(c);
        ctx.Say(a, key, "Hi, is my parcel shipped?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The chat starts on number A", t.AccountId == a.Id);
        var tracked = ctx.Db.ChannelAccounts.First(x => x.Id == a.Id);
        AccountHealth.SetStatus(ctx.Db, tracked, Health.Banned, "Test lab: number banned", actor: "testlab");
        ctx.Db.SaveChanges();
        ctx.Link("Channels", "/channels");

        var agent = ctx.As(ctx.Lab.HqAgent);
        var sent = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Yes — it left our warehouse this morning." }, route: new() { ["key"] = key });
        ctx.Check("The agent's reply is sent", sent.Ok, $"HTTP {sent.Status} {sent.Detail()}");
        var after = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The chat moved off the banned number to a healthy lab number",
            after.AccountId != a.Id && after.Account?.EntityId == ctx.Lab.Hq.Id && !Routing.IsDown(after.Account!),
            after.Account?.Name ?? "no account");
        var last = ctx.MessagesOf(t.Id).LastOrDefault(m => m.Source == MessageSource.Agent);
        ctx.Check("The reply went out from the new number", last != null && last.AccountId == after.AccountId);
        ctx.Check("A system note explains the move", ctx.MessagesOf(t.Id).Any(m => m.Source == MessageSource.System
            && m.Text.Contains("is banned")));
        ctx.Check("An account.failover audit row was written", ctx.Db.AuditLogs.AsNoTracking()
            .Any(x => x.Action == "account.failover" && x.Target == $"whatsapp:{key}"));
    }

    private static Task QuietHoursAsync(LabContext ctx)
    {
        var allDay = "{" + string.Join(",", AccountDelivery.Days.Select(d =>
            $"\"{d}\":{{\"on\":true,\"start\":\"00:00\",\"end\":\"00:00\"}}")) + "}";
        var inbox = ctx.Inbox("quiet hours", new() { ["quiet_schedule"] = allDay });
        var c = ctx.NewContact("Mary Fernandez");
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("Thanks Mary — we'll get back to you in the morning.");
        ctx.Say(inbox, key, "Hi, can I collect my order tomorrow?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var job = ctx.Services.GetRequiredService<QueueService>().Enqueue(ChatBranch.Domain.Channels.WhatsApp,
            JobKind.Text, key, new() { ["text"] = "Your order is ready for collection." },
            contactId: c.Id, accountId: inbox.Id);
        var aiJob = ctx.Db.OutboundJobs.AsNoTracking()
            .Where(j => j.ToAddress == key && j.Status == JobStatus.Queued && j.Id != job.Id).ToList()
            .FirstOrDefault(j => j.Payload.ContainsKey("draft_id"));
        ctx.Check("The AI's reply waits in the queue for quiet hours to end", aiJob != null && ctx.Replies(t.Id).Count == 0);
        ctx.Drain();
        ctx.Check("During quiet hours the drain sends nothing", ctx.Job(job.Id)?.Status == JobStatus.Queued
            && (aiJob == null || ctx.Job(aiJob.Id)?.Status == JobStatus.Queued) && ctx.Replies(t.Id).Count == 0);

        // quiet hours lifted (the inbox's own setting goes back to "never quiet")
        ctx.Inbox("quiet hours", new());
        ctx.Drain(() => ctx.Job(job.Id)?.Status == JobStatus.Sent
                        && (aiJob == null || ctx.Job(aiJob.Id)?.Status == JobStatus.Sent));
        ctx.Check("After quiet hours the queued message is delivered", ctx.Job(job.Id)?.Status == JobStatus.Sent,
            ctx.Job(job.Id)?.LastError ?? "");
        ctx.Check("…and so is the AI's reply", ctx.Replies(t.Id).Count == 1);
        return Task.CompletedTask;
    }

    // ---- scoping & access ----------------------------------------------------------------------------

    private static async Task BranchChatAsync(LabContext ctx)
    {
        var north = ctx.NewContact("Aminah Yusof", ctx.Lab.North.Id);
        var south = ctx.NewContact("Kok Leong Chan", ctx.Lab.South.Id);
        var nKey = LabContext.WaKey(north);
        var sKey = LabContext.WaKey(south);
        ctx.Say(ctx.Lab.NorthLine, nKey, "Hi from the north", north.Name);
        ctx.Say(ctx.Lab.SouthLine, sKey, "Hi from the south", south.Name);
        ctx.LinkChat("whatsapp", nKey, "Open North chat");
        ctx.LinkChat("whatsapp", sKey, "Open South chat");
        var agent = ctx.As(ctx.Lab.NorthAgent);
        var own = await ctx.Api(agent, "GET", "/api/whatsapp/threads/{key}", route: new() { ["key"] = nKey });
        ctx.Check("The North agent opens the North chat (200)", own.Ok, $"HTTP {own.Status}");
        var other = await ctx.Api(agent, "GET", "/api/whatsapp/threads/{key}", route: new() { ["key"] = sKey });
        ctx.Check("The South chat answers 404", other.Status == 404, $"HTTP {other.Status}");
        async Task<int?> Listed(string key) => LabContext.Int((await ctx.Api(agent, "GET", "/api/inbox/threads",
            query: new() { ["thread"] = $"whatsapp:{key}", ["count_only"] = "1" })).Body, "count");
        ctx.Check("The North chat is in the agent's inbox", await Listed(nKey) == 1);
        ctx.Check("The South chat is not", await Listed(sKey) == 0);
        var reply = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send", new { text = "Hello!" },
            route: new() { ["key"] = sKey });
        ctx.Check("Replying in the South chat is refused (404)", reply.Status == 404, $"HTTP {reply.Status}");
    }

    private static async Task LinkScopeAsync(LabContext ctx)
    {
        var north = ctx.NewContact("Hafiz Rahman", ctx.Lab.North.Id);
        var south = ctx.NewContact("Jessica Lim", ctx.Lab.South.Id);
        var key = ctx.NewPhone();
        ctx.Say(ctx.Lab.NorthLine, key, "Hello, I'm a new customer");
        ctx.LinkChat("whatsapp", key);
        var agent = ctx.As(ctx.Lab.NorthAgent);
        var route = new Dictionary<string, string> { ["channel"] = "whatsapp", ["key"] = key };
        var wrong = await ctx.Api(agent, "POST", "/api/messaging/threads/{channel}/{key}/link",
            new Dictionary<string, object> { ["contact_id"] = south.Id }, route: route);
        ctx.Check("Linking a South contact is refused (404)", wrong.Status == 404, $"HTTP {wrong.Status} {wrong.Detail()}");
        ctx.Check("The chat is still unlinked", ctx.ThreadOf("whatsapp", key)!.ContactId == null);
        var right = await ctx.Api(agent, "POST", "/api/messaging/threads/{channel}/{key}/link",
            new Dictionary<string, object> { ["contact_id"] = north.Id }, route: route);
        ctx.Check("Linking a North contact works", right.Ok && ctx.ThreadOf("whatsapp", key)!.ContactId == north.Id,
            $"HTTP {right.Status} {right.Detail()}");
        ctx.LinkContact(north.Id);
    }

    private static async Task ReadGatesAsync(LabContext ctx)
    {
        var none = ctx.As(ctx.Lab.NoAccess);
        foreach (var (route, page) in new[]
                 {
                     ("/api/contacts", "Contacts"), ("/api/inbox/threads", "Chats"), ("/api/ai/monitor", "AI monitor"),
                     ("/api/flows/library", "Journeys"), ("/api/analytics/summary", "Analytics"), ("/api/tags", "Tags"),
                     ("/api/bookings", "Bookings"), ("/api/audit", "Activity"),
                 })
        {
            var r = await ctx.Api(none, "GET", route);
            ctx.Check($"{page} ({route}) answers 403", r.Status == 403, $"HTTP {r.Status}");
        }
        var who = await ctx.Api(none, "GET", "/api/auth/whoami");
        ctx.Check("whoami answers 200 with no capabilities", who.Ok && LabKit.Count(who.Body, "capabilities") == 0,
            $"HTTP {who.Status}");
        var live = await ctx.Api(none, "GET", "/api/live");
        ctx.Check("The Live chip shows nothing waiting", live.Ok && LabContext.Int(live.Body, "waiting_count") == 0,
            $"HTTP {live.Status}");
        ctx.Link("Permissions", "/permissions");
    }

    // ---- flows & automations ------------------------------------------------------------------------

    private static void SkipInOrgQuietHours(LabContext ctx)
    {
        if (AccountDelivery.InQuietHours(null, ctx.Sc))
            ctx.Skip("The organization's quiet hours are on right now — flow messages wait for them (try again later).");
    }

    private static Task WelcomeFlowAsync(LabContext ctx)
    {
        SkipInOrgQuietHours(ctx);
        var wf = LabKit.Flow(ctx, "wf-testlab-welcome", "Test lab · Welcome series",
            new List<object?>
            {
                LabKit.Node("trg", "trigger", new { when = FlowEngine.WhenContactCreated, repeat = "Once", onlyTag = LabFixture.TagName }),
                LabKit.Node("act", "action", new
                {
                    channels = new[] { "WhatsApp" },
                    script = "Welcome to Test lab Bakery, {{first_name}}! Reply MENU to see today's cakes.",
                }),
                LabKit.Node("end", "end", new { logCode = "WELCOMED" }),
            },
            new List<object?> { LabKit.Edge("e1", "trg", "act"), LabKit.Edge("e2", "act", "end") });
        ctx.Link("Journeys", "/journeys");
        var c = ctx.NewContact("Nadia Ismail");
        var key = LabContext.WaKey(c);
        ctx.LinkContact(c.Id);
        ctx.FlowTick();
        var run = ctx.Db.FlowRuns.AsNoTracking().FirstOrDefault(r => r.WorkflowId == wf.Id && r.ContactId == c.Id);
        ctx.Check("The flow enrolled the new contact", run != null, run?.Detail ?? "no run");
        var job = ctx.Db.OutboundJobs.AsNoTracking().FirstOrDefault(j => j.FlowRunId == run!.Id);
        var text = job == null ? "" : AiGate.Str(job.Payload, "text", "");
        ctx.Check("The welcome was queued with their first name", text.StartsWith("Welcome to Test lab Bakery, Nadia!"), text);
        if (job == null) return Task.CompletedTask;
        ctx.Drain(() => ctx.Job(job.Id)?.Status is JobStatus.Sent or JobStatus.Failed);
        ctx.Check("The queue delivered it", ctx.Job(job.Id)?.Status == JobStatus.Sent, ctx.Job(job.Id)?.LastError ?? "");
        var t = ctx.ThreadOf("whatsapp", key);
        ctx.LinkChat("whatsapp", key);
        ctx.Check("It is in the contact's WhatsApp chat", t != null && t.ContactId == c.Id
            && ctx.MessagesOf(t.Id).Any(m => m.Text.StartsWith("Welcome to Test lab Bakery")));
        return Task.CompletedTask;
    }

    private static Task KeywordFlowAsync(LabContext ctx)
    {
        SkipInOrgQuietHours(ctx);
        var wf = LabKit.Flow(ctx, "wf-testlab-menu", "Test lab · MENU keyword",
            new List<object?>
            {
                LabKit.Node("trg", "trigger", new { when = FlowEngine.WhenMessageReceived, keyword = "MENU", channel = "whatsapp", repeat = "Every time" }),
                LabKit.Node("act", "action", new
                {
                    channels = new[] { "WhatsApp" },
                    script = "Today's cakes: chocolate, pandan, red velvet. Reply with a name to order.",
                }),
                LabKit.Node("end", "end", new { logCode = "MENU_SENT" }),
            },
            new List<object?> { LabKit.Edge("e1", "trg", "act"), LabKit.Edge("e2", "act", "end") });
        ctx.Link("Journeys", "/journeys");
        var c = ctx.NewContact("Ong Bee Hoon");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "MENU please", c.Name);
        ctx.LinkChat("whatsapp", key);
        var run = ctx.Db.FlowRuns.AsNoTracking().FirstOrDefault(r => r.WorkflowId == wf.Id && r.ContactId == c.Id);
        ctx.Check("The message started a run", run != null);
        var job = run == null ? null : ctx.Db.OutboundJobs.AsNoTracking().FirstOrDefault(j => j.FlowRunId == run.Id);
        ctx.Check("Today's menu was queued", job != null);
        if (job == null) return Task.CompletedTask;
        ctx.Drain(() => ctx.Job(job.Id)?.Status is JobStatus.Sent or JobStatus.Failed);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The menu was delivered in the same chat", ctx.Job(job.Id)?.Status == JobStatus.Sent
            && ctx.MessagesOf(t.Id).Any(m => m.Text.StartsWith("Today's cakes")), ctx.Job(job.Id)?.LastError ?? "");
        return Task.CompletedTask;
    }

    private static async Task RefundRuleAsync(LabContext ctx)
    {
        var billing = LabKit.Team(ctx, "Test lab · Billing", false, ctx.Lab.HqAgent.Id);
        var ruleId = await LabKit.Rule(ctx, "Test lab · refunds to Billing", "message_created",
            new object[] { new { field = "message", op = "contains", value = "refund" } },
            new object[]
            {
                new { type = "add_label", value = "refund" },
                new { type = "assign_team", value = billing.Id.ToString() },
            });
        ctx.Check("The rule is active", ruleId != null);
        ctx.Link("Automations", "/automations");
        var firedBefore = ctx.Db.AutomationRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => r.TimesFired).FirstOrDefault();
        var c = ctx.NewContact("Thomas Chua");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "I'd like a refund for order #1234", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The chat carries the label “refund”", t.Labels.Contains("refund"), string.Join(", ", t.Labels));
        ctx.Check("The chat belongs to the Billing team", t.TeamId == billing.Id, $"team #{t.TeamId}");
        var firedAfter = ctx.Db.AutomationRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => r.TimesFired).FirstOrDefault();
        ctx.Check("The rule counted the firing", firedAfter == firedBefore + 1, $"{firedBefore} → {firedAfter}");
    }

    private static Task RoundRobinAsync(LabContext ctx)
    {
        var team = LabKit.Team(ctx, "Test lab · Round robin", true, ctx.Lab.NorthAgent.Id, ctx.Lab.SouthAgent.Id);
        // restart the cursor on the TRACKED row (a bulk update would leave the context's copy stale)
        var cursor = ctx.Db.Teams.First(x => x.Id == team.Id);
        cursor.LastAssignee = "";
        ctx.Db.SaveChanges();
        var members = ctx.Db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team.Id).OrderBy(m => m.Id)
            .Select(m => m.User!.Username).ToList();
        var inbox = ctx.Inbox("round robin", new(), teamId: team.Id);
        ctx.Link("Teams", "/teams");
        var got = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var key = ctx.NewPhone();
            ctx.Say(inbox, key, $"Hello, customer number {i + 1} here");
            var t = ctx.ThreadOf("whatsapp", key)!;
            if (i == 0) ctx.LinkChat("whatsapp", key, "Open first chat");
            ctx.Check($"Chat {i + 1} is owned by the round-robin team", t.TeamId == team.Id);
            got.Add(t.Assignee);
        }
        var expected = new[] { members[0], members[1], members[0] };
        ctx.Check("Assignees alternate first, second, first", got.SequenceEqual(expected),
            $"got {string.Join(" → ", got)}; expected {string.Join(" → ", expected)}");
        return Task.CompletedTask;
    }
}
