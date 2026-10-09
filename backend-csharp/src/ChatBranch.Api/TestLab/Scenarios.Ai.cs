using ChatBranch.Data.Ai;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>AI replies: answering from the knowledge base, and the rules that keep the AI quiet.</summary>
public static class AiScenarios
{
    public static IEnumerable<LabScenario> All() => new[]
    {
        new LabScenario("ai-faq-knowledge", "ai",
            "A known customer asks a FAQ — the AI answers from the knowledge base",
            "An inbound message from a known contact reaches the AI, which answers through the AI monitor, grounded in the knowledge base.",
            new[]
            {
                "Add the FAQ “Delivery to Penang” to the lab's knowledge base.",
                "A known contact writes on the lab WhatsApp line: “Hi! Do you deliver to Penang?”",
            },
            new[]
            {
                "The chat is linked to the contact.",
                "The AI's reply is recorded in the AI monitor as sent automatically and reaches the chat.",
                "The reply is grounded in the FAQ (with a real AI key: it mentions the delivery days).",
            },
            FaqAsync, NeedsAiKey: true),

        new LabScenario("ai-ack-skip", "ai",
            "“OK thanks” after an answer gets no reply",
            "With “Acknowledgements don't need a reply” on, the AI stays quiet when the customer only says thanks.",
            new[]
            {
                "An inbox with acknowledgement-skipping on.",
                "The customer asks “When does my order ship?” — the AI answers.",
                "The customer replies “ok thanks 👍”.",
            },
            new[] { "Exactly one AI reply in the chat, and no second draft in the AI monitor." },
            AckAsync),

        new LabScenario("ai-master-switch-off", "ai",
            "The AI master switch is off for a branch — no reply",
            "Switching the AI off for an outlet silences it there, with the reason the chat header shows.",
            new[]
            {
                "Set the AI master switch to Off for “Test lab · North”.",
                "A known North contact writes on the North line.",
            },
            new[] { "No AI reply and no draft.", "The chat's AI verdict is off because of the master switch." },
            MasterOffAsync),

        new LabScenario("ai-schedule-outside-hours", "ai",
            "Outside the AI schedule — no reply",
            "An outlet on “AI on a schedule” answers only inside its windows; outside them the AI stays quiet.",
            new[]
            {
                "Put “Test lab · South” on a schedule whose only window is on another weekday.",
                "A known South contact writes now.",
            },
            new[] { "No AI reply.", "The verdict says “outside the AI schedule”." },
            ScheduleAsync),

        new LabScenario("ai-contact-never", "ai",
            "A contact set to “never use AI” is never answered by it",
            "The per-contact AI mode beats every inbox default.",
            new[]
            {
                "Set the contact's AI mode to Never through the contact page's endpoint.",
                "The contact writes on the lab WhatsApp line.",
            },
            new[] { "No AI reply.", "The verdict names the contact's never-AI mode." },
            ContactNeverAsync),

        new LabScenario("ai-follow-up-open-question", "ai",
            "The AI follows up when the customer goes quiet after a question",
            "A follow-up goes out only after an AI message that asked something, and a reply stops the sequence.",
            new[]
            {
                "An inbox with follow-ups on (first one after 3 minutes, at most one).",
                "The customer asks about colours; the AI answers with a question.",
                "Ten minutes pass (the AI's message is back-dated) and the AI monitor's tick runs.",
                "The customer answers; the tick runs again.",
            },
            new[]
            {
                "A follow-up draft is recorded and sent.",
                "After the customer's answer no further follow-up goes out.",
            },
            FollowUpAsync),

        new LabScenario("ai-reply-check-guardrail", "ai",
            "A reply check stops a risky answer and hands over",
            "A “check” skill runs on every AI draft; when it fails with on-fail = hand over, nothing is sent and a person is asked to step in.",
            new[]
            {
                "Turn on the lab's check skill “no refund promises”.",
                "The customer asks for their money back; the AI drafts “we'll give you a full refund”.",
            },
            new[]
            {
                "Nothing reaches the customer.",
                "The draft waits in the AI monitor, and the chat is handed over (reply check failed).",
            },
            GuardrailAsync),
    };

    private static Task FaqAsync(LabContext ctx)
    {
        ctx.UseRealModel = ctx.HasRealModel;
        var kb = LabKit.Knowledge(ctx, "Delivery to Penang", "faq", "Do you deliver to Penang?",
            "Yes — Test lab Bakery delivers to Penang every Tuesday and Friday. Delivery is free for orders over RM 100.");
        var c = ctx.NewContact("Aisha Rahman");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "Hi! Do you deliver to Penang?", "Aisha Rahman");
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.LinkContact(c.Id);
        ctx.Link("Knowledge base", "/knowledge");

        ctx.Check("The chat is linked to the known contact", t.ContactId == c.Id, $"contact #{t.ContactId}");
        var draft = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("The AI's reply went through the AI monitor (sent automatically)",
            draft?.Status == AiDraftStatus.AutoSent, $"draft status: {draft?.Status ?? "none"}");
        var replies = ctx.Replies(t.Id);
        ctx.Check("The reply reached the chat", replies.Count == 1, $"{replies.Count} AI message(s)");
        ctx.Check("The reply is grounded in the FAQ", draft != null && LabKit.GroundedIn(draft, kb.Id),
            $"knowledge item #{kb.Id}");
        if (ctx.UseRealModel)
        {
            var text = replies.LastOrDefault()?.Text ?? "";
            ctx.Check("The real model used the FAQ (mentions Tuesday or Friday)",
                text.Contains("Tuesday", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Friday", StringComparison.OrdinalIgnoreCase), Trim(text));
        }
        else
            ctx.Check("Deterministic variant: the FAQ was in the prompt the AI received",
                ctx.Brain.Prompts.Any(p => p.Contains("every Tuesday and Friday")), "no AI key — the lab's test brain answered");
        return Task.CompletedTask;
    }

    private static Task AckAsync(LabContext ctx)
    {
        var inbox = ctx.Inbox("acknowledgements", new() { ["ai_ack_skip"] = "true" });
        var c = ctx.NewContact("Daniel Wong");
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("Your order ships tomorrow morning.");
        ctx.Say(inbox, key, "When does my order ship?", c.Name);
        ctx.Say(inbox, key, "ok thanks 👍", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The question got its answer", ctx.Replies(t.Id).FirstOrDefault()?.Text == "Your order ships tomorrow morning.");
        ctx.Check("The acknowledgement got no reply", ctx.Replies(t.Id).Count == 1, $"{ctx.Replies(t.Id).Count} AI message(s)");
        ctx.Check("No second draft in the AI monitor", ctx.DraftsOf(t.Id).Count == 1, $"{ctx.DraftsOf(t.Id).Count} draft(s)");
        return Task.CompletedTask;
    }

    private static Task MasterOffAsync(LabContext ctx)
    {
        ctx.SetEntitySetting(ctx.Lab.North.Id, AiControlKeys.Master, AiMasterModes.Off);
        var c = ctx.NewContact("Farah Aziz", ctx.Lab.North.Id);
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.NorthLine, key, "Hello, are you open today?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Link("AI master switch", "/ai-monitor");
        ctx.Check("The chat belongs to the North branch", t.EntityId == ctx.Lab.North.Id);
        ctx.Check("No AI reply", ctx.Replies(t.Id).Count == 0);
        ctx.Check("No draft was written", ctx.DraftsOf(t.Id).Count == 0);
        var v = ctx.Verdict(t);
        ctx.Check("The verdict is off because of the master switch", !v.Enabled && v.Rule == AiRules.MasterOff, v.Reason);
        return Task.CompletedTask;
    }

    private static Task ScheduleAsync(LabContext ctx)
    {
        var zone = ChatBranch.Data.Ai.Skills.BuiltinSkills.OrgTimeZone(ctx.Sc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var otherDay = AiSchedule.Key(local.AddDays(3).DayOfWeek);
        ctx.SetEntitySetting(ctx.Lab.South.Id, AiControlKeys.Master, AiMasterModes.Schedule);
        ctx.SetEntitySetting(ctx.Lab.South.Id, AiControlKeys.Schedule, $"{{\"{otherDay}\":[[\"09:00\",\"10:00\"]]}}");
        ctx.SetEntitySetting(ctx.Lab.South.Id, AiControlKeys.ScheduleMode, AiScheduleModes.Inside);
        var c = ctx.NewContact("Ravi Kumar", ctx.Lab.South.Id);
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.SouthLine, key, "Is the shop open now?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("No AI reply", ctx.Replies(t.Id).Count == 0);
        var v = ctx.Verdict(t);
        ctx.Check("The verdict says it is outside the AI schedule", !v.Enabled && v.Rule == AiRules.Schedule, v.Reason);
        return Task.CompletedTask;
    }

    private static async Task ContactNeverAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Grace Lim");
        var put = await ctx.Api(ctx.Super, "PUT", "/api/contacts/{id:int}/ai-mode", new { mode = "never" },
            route: new() { ["id"] = c.Id.ToString() });
        ctx.Check("The contact's AI mode is saved as Never", put.Ok && LabContext.Str(put.Body, "mode") == "never",
            $"HTTP {put.Status} {put.Detail()}");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "Can you tell me my balance?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.LinkContact(c.Id);
        ctx.Check("No AI reply", ctx.Replies(t.Id).Count == 0);
        var v = ctx.Verdict(t);
        ctx.Check("The verdict names the contact's never-AI mode", !v.Enabled && v.Rule == AiRules.ContactNever, v.Reason);
    }

    private static async Task FollowUpAsync(LabContext ctx)
    {
        var inbox = ctx.Inbox("follow-ups", new()
        {
            ["ai_followup"] = "true", ["ai_followup_first_hours"] = "0.05", ["ai_followup_max"] = "1",
        });
        var c = ctx.NewContact("Hannah Lee");
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("We have it in blue and black. Which colour would you like?");
        ctx.Say(inbox, key, "Do you have the backpack in other colours?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var anchor = ctx.Replies(t.Id).LastOrDefault();
        ctx.Check("The AI answered with a question", anchor?.Text.EndsWith('?') == true, anchor?.Text ?? "no reply");
        if (anchor == null) return;

        // ten minutes pass: the customer has gone quiet
        ctx.Db.Messages.Where(m => m.Id == anchor.Id)
            .ExecuteUpdate(u => u.SetProperty(m => m.CreatedAt, DateTime.UtcNow.AddMinutes(-10)));
        var tick = await ctx.Api(ctx.Super, "POST", "/api/ai/monitor/tick");
        ctx.Check("The AI monitor's tick ran", tick.Ok, $"HTTP {tick.Status}");
        var follow = ctx.DraftsOf(t.Id).Where(d => d.Kind == AiDraftKind.FollowUp).ToList();
        ctx.Check("A follow-up was written and sent",
            follow.Any(d => d.Status is AiDraftStatus.AutoSent or AiDraftStatus.Sent),
            follow.Count == 0 ? "no follow-up draft" : string.Join(", ", follow.Select(d => d.Status)));
        ctx.Check("The follow-up reached the chat", ctx.Replies(t.Id).Count >= 2, $"{ctx.Replies(t.Id).Count} AI message(s)");

        // the customer answers: the sequence stops (the next AI answer asks nothing)
        ctx.Brain.Say("Black it is — I've noted that for you.");
        ctx.Say(inbox, key, "Black please", c.Name);
        await ctx.Api(ctx.Super, "POST", "/api/ai/monitor/tick");
        var after = ctx.DraftsOf(t.Id).Count(d => d.Kind == AiDraftKind.FollowUp);
        ctx.Check("After the customer's answer no further follow-up went out", after == follow.Count,
            $"{after} follow-up draft(s)");
    }

    private static async Task GuardrailAsync(LabContext ctx)
    {
        var on = await LabKit.Skills(ctx, guardrail: true);
        ctx.Check("The check skill is switched on (through the skills endpoint)", on.Ok, on.Detail);
        ctx.Link("AI skills", "/skills");
        var c = ctx.NewContact("Joseph Tan");
        var key = LabContext.WaKey(c);
        // the model keeps promising, so every rewrite fails too
        ctx.Brain.Say("Sure — we'll give you a full refund today.", "Of course, a full refund is on its way.",
            "You'll get a full refund, no problem.");
        ctx.Say(ctx.Lab.Conversation, key, "My cake arrived squashed, I want my money back", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Link("AI monitor", "/ai-monitor?tab=review");
        ctx.Check("Nothing reached the customer", ctx.Replies(t.Id).Count == 0);
        var d = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("The draft waits in the AI monitor", d?.Status == AiDraftStatus.Pending, d?.HoldReason ?? "no draft");
        ctx.Check("The chat is handed over because a reply check failed",
            Handover.IsOpen(t) && t.HandoverKind == HandoverKinds.CheckFailed, $"{t.HandoverKind}: {t.HandoverReason}");
    }

    internal static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";
}
