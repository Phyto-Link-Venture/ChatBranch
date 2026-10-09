using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>The AI monitor, human handover, unknown customers and compliance.</summary>
public static class ConversationScenarios
{
    public static IEnumerable<LabScenario> All() => new[]
    {
        // ---- AI monitor ------------------------------------------------------------------
        new LabScenario("review-all-approve", "review",
            "“Review every reply” holds the draft — approving sends it",
            "With the AI monitor on review_all nothing reaches the customer until a person approves, and approving goes through the normal send path.",
            new[]
            {
                "An inbox with “Who checks the AI” = review every reply.",
                "A known contact asks when the shop opens.",
                "The lab's front-desk agent approves the draft in the AI monitor.",
            },
            new[]
            {
                "Before approval: a pending draft with the reason “Every AI reply is reviewed”, no AI message.",
                "After approval: the AI message is in the chat and the draft is marked approved by the agent.",
            },
            ApproveAsync),

        new LabScenario("review-all-reject", "review",
            "“Review every reply” — rejecting sends nothing",
            "A rejected draft never reaches the customer and keeps the reviewer's reason.",
            new[] { "Same review-every-reply inbox.", "The agent rejects the draft with a reason." },
            new[] { "The draft is rejected with the reason; no AI message in the chat." },
            RejectAsync),

        new LabScenario("review-shadow-mode", "review",
            "Shadow mode writes drafts but never sends",
            "Shadow mode is a safe trial: the AI's answer is recorded for review and nothing goes out.",
            new[] { "An inbox in shadow mode.", "A contact asks a question." },
            new[] { "A shadow draft exists; the chat has no AI message." },
            ShadowAsync),

        new LabScenario("review-flagged-risky", "review",
            "“Review risky replies” holds the one that mentions money",
            "review_flagged sends ordinary answers straight away and holds replies with prices, dates or risky words.",
            new[]
            {
                "An inbox on “review risky replies” (first-N off).",
                "The AI answers a greeting, then quotes “RM 250”.",
            },
            new[] { "The greeting reply is sent automatically.", "The price reply is held with a money / keyword reason." },
            FlaggedAsync),

        // ---- handover ----------------------------------------------------------------------
        new LabScenario("handover-asks-for-person", "handover",
            "The customer asks for a person — handover, the bot stands down, then “Let AI continue”",
            "A rule hands the chat over: it lands in Needs human, the chatbot stays quiet until an agent lets it continue.",
            new[]
            {
                "A lab automation rule: message contains “speak to a person” → hand over.",
                "The customer writes “I'd like to speak to a person about my order”, then “Hello? Anyone there?”.",
                "The front-desk agent clicks “Let AI continue”; the customer asks a new question.",
            },
            new[]
            {
                "The chat is handed over (kind rule), pending, listed in Needs human, and the AI says nothing to either message.",
                "After “Let AI continue” the handover is cleared, the chatbot is on and it answers the new question.",
            },
            AsksForPersonAsync),

        new LabScenario("handover-ai-promises-a-person", "handover",
            "The AI says a person will help — a tracked handover, cleared by a human reply",
            "When the AI's own answer hands over (“a member of our team will…”), the reply still goes out, the chat shows in Needs human, and an agent's reply answers it.",
            new[]
            {
                "The AI answers “Let me connect you with a member of our team.”",
                "The front-desk agent replies by hand.",
            },
            new[]
            {
                "The AI's reply is sent and the chat is handed over (the AI asked for a person).",
                "The agent's reply clears the handover.",
            },
            AiPromisesPersonAsync),

        // ---- unknown customers --------------------------------------------------------------
        new LabScenario("identity-ask-and-create", "identity",
            "Unknown customer (ask & create) — asked who they are, contact created and linked",
            "An inbox set to ask_and_create asks a stranger for their details, creates the contact and lets the AI answer.",
            new[]
            {
                "An inbox with Unknown customers = ask and create.",
                "A number nobody knows writes “Hi, I want to order a cake”.",
                "They answer “My name is Siti Rahman, siti…@testlab.invalid”.",
            },
            new[]
            {
                "First the ask goes out (system message) and the AI waits.",
                "Then a contact “Siti Rahman” exists in the lab with that email, the chat is linked, and the AI answers.",
            },
            AskAndCreateAsync),

        new LabScenario("identity-several-matches", "identity",
            "Unknown customer matches several contacts — handed to a person",
            "When the details a stranger gives match more than one contact, nothing is guessed: the chat goes to Needs human.",
            new[]
            {
                "Two lab contacts share one email address.",
                "An unknown number writes, is asked, and answers with that email.",
            },
            new[] { "The chat stays unlinked, handed over as “unknown customer”, and is listed in Needs human." },
            SeveralMatchesAsync),

        new LabScenario("identity-ask-then-create-contact", "identity",
            "Unknown customer (ask) — an agent uses “Create contact”",
            "In ask mode an unmatched stranger is left for a person; the chat's Create contact action links them and the AI answers once.",
            new[]
            {
                "An inbox with Unknown customers = ask.",
                "A stranger writes and answers “This is Farid Hassan”.",
                "An admin uses Create contact on the chat.",
            },
            new[]
            {
                "Before: no contact, handover “unknown customer”.",
                "After: contact created in the lab, chat linked, handover cleared, the customer got an answer.",
            },
            AskThenCreateAsync),

        new LabScenario("identity-matches-one", "identity",
            "Unknown customer gives an email we know — linked at once",
            "A first message that carries a known email links the chat to that contact straight away.",
            new[] { "An inbox in ask mode; a lab contact with an email.", "A new number writes “Hi, it's Mei, <that email>”." },
            new[] { "The chat is linked to the existing contact and the AI answers." },
            MatchesOneAsync),

        new LabScenario("invite-link-telegram", "identity",
            "An invite link binds a Telegram chat to the contact",
            "“Invite to another app” makes a code; the customer's /start CODE on Telegram links the new chat to them.",
            new[] { "Make a Telegram invite for a contact (contact page endpoint).", "The customer sends “/start CODE” to the lab bot." },
            new[] { "The Telegram chat is linked to the contact, the code is stripped from the message and marked used." },
            InviteAsync),

        // ---- compliance -------------------------------------------------------------------
        new LabScenario("stop-opts-out", "compliance",
            "STOP opts the customer out of everything automated",
            "STOP sets the conversation opted out: no AI reply, queued messages cancelled, and a manual send needs force.",
            new[]
            {
                "A known contact chats; a reminder is queued for later.",
                "The customer writes STOP.",
                "An agent tries to send by hand, then sends with force.",
            },
            new[]
            {
                "Opted out, the queued reminder cancelled, no AI reply to STOP.",
                "The plain send is refused (409); the forced one goes through.",
            },
            StopAsync),

        new LabScenario("start-opts-back-in", "compliance",
            "START opts the customer back in",
            "After STOP, START re-enables the conversation and the AI answers again.",
            new[] { "The customer writes STOP, then START, then asks a question." },
            new[] { "The chat is opted in again with a note, and the AI answers the question." },
            StartAsync),

        new LabScenario("cold-text-guard", "compliance",
            "Cold-text guard: free text to someone who never wrote is refused",
            "A personal (conversation) number never opens a chat with free text — an admin may override with force.",
            new[] { "Start a new WhatsApp chat with a contact who never wrote.", "Send free text; then send with force as an admin." },
            new[] { "The plain send is refused (409, cold text).", "The admin's forced send goes through." },
            ColdTextAsync),
    };

    // ---- AI monitor -----------------------------------------------------------------------------

    private static (ChannelAccount Inbox, string Key, ChatBranch.Domain.Thread Thread) ReviewAll(LabContext ctx, string name)
    {
        var inbox = ctx.Inbox("review every reply", new() { ["ai_review_mode"] = "review_all" });
        var c = ctx.NewContact(name);
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("We open at 9am every day, and close at 6pm.");
        ctx.Say(inbox, key, "What time do you open tomorrow?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Link("AI monitor", "/ai-monitor?tab=review");
        return (inbox, key, t);
    }

    private static async Task ApproveAsync(LabContext ctx)
    {
        var (_, _, t) = ReviewAll(ctx, "Kevin Ong");
        var d = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("The draft is held for review", d?.Status == AiDraftStatus.Pending, d?.Status ?? "no draft");
        ctx.Check("…because every reply is reviewed", d?.HoldReason.Contains("Every AI reply is reviewed") == true, d?.HoldReason ?? "");
        ctx.Check("Nothing reached the customer yet", ctx.Replies(t.Id).Count == 0);
        if (d == null) return;
        var list = await ctx.Api(ctx.Super, "GET", "/api/ai/monitor", query: new() { ["status"] = "pending", ["entity"] = LabFixture.HqSlug });
        ctx.Check("It is listed in the AI monitor's waiting list", list.Ok && list.Body.GetRawText().Contains($"\"id\":{d.Id},"),
            $"HTTP {list.Status}");

        var agent = ctx.As(ctx.Lab.HqAgent);
        var ok = await ctx.Api(agent, "POST", "/api/ai/monitor/{id:int}/approve", new { },
            route: new() { ["id"] = d.Id.ToString() });
        ctx.Check("The front-desk agent's approval is accepted", ok.Ok, $"HTTP {ok.Status} {ok.Detail()}");
        var after = ctx.Db.AiDrafts.AsNoTracking().First(x => x.Id == d.Id);
        ctx.Check("The draft is approved by the agent", after.Status == AiDraftStatus.Approved && after.DecidedBy == ctx.Lab.HqAgent.Username,
            $"{after.Status} by {after.DecidedBy}");
        var replies = ctx.Replies(t.Id);
        ctx.Check("The approved reply is in the chat", replies.Count == 1 && replies[0].Text == d.Content,
            replies.FirstOrDefault()?.Text ?? "no message");
    }

    private static async Task RejectAsync(LabContext ctx)
    {
        var (_, _, t) = ReviewAll(ctx, "Lina Chong");
        var d = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("The draft is held for review", d?.Status == AiDraftStatus.Pending, d?.Status ?? "no draft");
        if (d == null) return;
        var agent = ctx.As(ctx.Lab.HqAgent);
        var res = await ctx.Api(agent, "POST", "/api/ai/monitor/{id:int}/reject",
            new { reason = "Opening hours changed this week" }, route: new() { ["id"] = d.Id.ToString() });
        ctx.Check("The rejection is accepted", res.Ok, $"HTTP {res.Status} {res.Detail()}");
        var after = ctx.Db.AiDrafts.AsNoTracking().First(x => x.Id == d.Id);
        ctx.Check("The draft is rejected with the reviewer's reason",
            after.Status == AiDraftStatus.Rejected && after.RejectReason == "Opening hours changed this week", after.RejectReason);
        ctx.Check("Nothing reached the customer", ctx.Replies(t.Id).Count == 0);
    }

    private static Task ShadowAsync(LabContext ctx)
    {
        var inbox = ctx.Inbox("shadow mode", new() { ["ai_review_mode"] = "shadow" });
        var c = ctx.NewContact("Omar Yusof");
        var key = LabContext.WaKey(c);
        ctx.Say(inbox, key, "Do you take card payments?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Link("AI monitor", "/ai-monitor?tab=activity");
        var d = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("A shadow draft was recorded", d?.Status == AiDraftStatus.Shadow, d?.Status ?? "no draft");
        ctx.Check("Nothing reached the customer", ctx.Replies(t.Id).Count == 0);
        return Task.CompletedTask;
    }

    private static Task FlaggedAsync(LabContext ctx)
    {
        var inbox = ctx.Inbox("review risky replies", new()
        {
            ["ai_review_mode"] = "review_flagged", ["ai_review_first_n"] = "0",
        });
        var c = ctx.NewContact("Siew Ling Goh");
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("Happy to help — what would you like to order?", "The total comes to RM 250, payable today.");
        ctx.Say(inbox, key, "Hi there!", c.Name);
        ctx.Say(inbox, key, "Two birthday cakes please, how much altogether?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var drafts = ctx.DraftsOf(t.Id);
        ctx.Check("The ordinary reply was sent automatically", drafts.FirstOrDefault()?.Status == AiDraftStatus.AutoSent,
            drafts.FirstOrDefault()?.Status ?? "no draft");
        var held = drafts.Skip(1).FirstOrDefault();
        ctx.Check("The reply quoting a price is held", held?.Status == AiDraftStatus.Pending, held?.HoldReason ?? "no second draft");
        ctx.Check("Only the ordinary reply reached the customer", ctx.Replies(t.Id).Count == 1);
        return Task.CompletedTask;
    }

    // ---- handover --------------------------------------------------------------------------------

    private static async Task<bool> InNeedsHuman(LabContext ctx, string channel, string key, string? kind = null)
    {
        var q = new Dictionary<string, string?> { ["handover"] = "open", ["thread"] = $"{channel}:{key}", ["count_only"] = "1" };
        if (kind != null) q["handover_kind"] = kind;
        var res = await ctx.Api(ctx.Super, "GET", "/api/inbox/threads", query: q);
        return res.Ok && LabContext.Int(res.Body, "count") == 1;
    }

    private static async Task AsksForPersonAsync(LabContext ctx)
    {
        var ruleId = await LabKit.Rule(ctx, "Test lab · wants a person", "message_created",
            new object[]
            {
                new { field = "message", op = "contains", value = "speak to a person" },
                new { field = "handed_over", op = "equals", value = "no" },
            },
            new object[] { new { type = "hand_over", value = "The customer asked for a person." } });
        ctx.Check("The hand-over rule is active", ruleId != null);
        ctx.Link("Automations", "/automations");
        var c = ctx.NewContact("Melissa Teo");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "I'd like to speak to a person about my order", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The chat is handed over by the rule", Handover.IsOpen(t) && t.HandoverKind == HandoverKinds.Rule,
            $"{t.HandoverKind}: {t.HandoverReason}");
        ctx.Check("The chat is pending", t.Status == ThreadStatus.Pending, t.Status);
        ctx.Check("It is listed in Needs human", await InNeedsHuman(ctx, "whatsapp", key));
        ctx.Say(ctx.Lab.Conversation, key, "Hello? Anyone there?", c.Name);
        ctx.Check("The chatbot stood down — no AI reply to either message", ctx.Replies(t.Id).Count == 0);
        var v = ctx.Verdict(t);
        ctx.Check("The chat says it is waiting for a human", !v.Enabled && v.Reason.Contains("handed to a person"), v.Reason);

        var agent = ctx.As(ctx.Lab.HqAgent);
        var cont = await ctx.Api(agent, "POST", "/api/inbox/threads/{channel}/{key}/handover",
            new { action = "continue" }, route: new() { ["channel"] = "whatsapp", ["key"] = key });
        ctx.Check("“Let AI continue” is accepted", cont.Ok && cont.Body.TryGetProperty("ai_enabled", out var on)
                                                          && on.ValueKind == JsonValueKind.True, $"HTTP {cont.Status} {cont.Detail()}");
        var fresh = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The handover is cleared", !Handover.IsOpen(fresh) && fresh.HandoverResolvedAt != null);
        ctx.Brain.Say("We close at 6pm today.");
        ctx.Say(ctx.Lab.Conversation, key, "Thanks — what time do you close today?", c.Name);
        ctx.Check("The AI answers again", ctx.Replies(t.Id).Count == 1, $"{ctx.Replies(t.Id).Count} AI message(s)");
    }

    private static async Task AiPromisesPersonAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Benjamin Koh");
        var key = LabContext.WaKey(c);
        ctx.Brain.Say("Let me connect you with a member of our team — they'll be with you shortly.");
        ctx.Say(ctx.Lab.Conversation, key, "This is the third time my order is late, I'm not happy", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("The AI's reply was sent", ctx.Replies(t.Id).Count == 1);
        ctx.Check("The chat is handed over (the AI asked for a person)",
            Handover.IsOpen(t) && t.HandoverKind == HandoverKinds.AskedForHuman, $"{t.HandoverKind}");
        ctx.Check("It is listed in Needs human", await InNeedsHuman(ctx, "whatsapp", key, HandoverKinds.AskedForHuman));
        var agent = ctx.As(ctx.Lab.HqAgent);
        var sent = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Hi Benjamin, I'm sorry about this — I'm checking your order now." },
            route: new() { ["key"] = key });
        ctx.Check("The agent's reply is sent", sent.Ok, $"HTTP {sent.Status} {sent.Detail()}");
        var after = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The agent's reply cleared the handover", !Handover.IsOpen(after), $"resolved at {after.HandoverResolvedAt:o}");
    }

    // ---- unknown customers -----------------------------------------------------------------------

    private static ChannelAccount IdentityInbox(LabContext ctx, string mode) =>
        ctx.Inbox($"unknown customers ({mode})", new() { ["identify_unknown"] = mode });

    private static IdentityRequest? Request(LabContext ctx, int threadId) =>
        ctx.Db.IdentityRequests.AsNoTracking().FirstOrDefault(r => r.ThreadId == threadId);

    private static Task AskAndCreateAsync(LabContext ctx)
    {
        var inbox = IdentityInbox(ctx, IdentifyModes.AskAndCreate);
        var key = ctx.NewPhone();
        ctx.Say(inbox, key, "Hi, I want to order a cake");
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var msgs = ctx.MessagesOf(t.Id);
        ctx.Check("The customer is asked who they are", msgs.Any(m => m.Source == MessageSource.System && !m.Private
            && m.Text.Contains("could you tell us your name")), "the ask (system message)");
        ctx.Check("The request is waiting for their answer", Request(ctx, t.Id)?.State == IdentityStates.Asking);
        ctx.Check("The AI waits while they are unknown", ctx.Replies(t.Id).Count == 0);

        var email = ctx.NewEmail("siti");
        ctx.Say(inbox, key, $"My name is Siti Rahman, {email}");
        var after = ctx.ThreadOf("whatsapp", key)!;
        var contact = ctx.Db.Contacts.AsNoTracking().FirstOrDefault(c => c.Email == email);
        ctx.Check("A contact “Siti Rahman” was created with that email", contact?.Name == "Siti Rahman", contact?.Name ?? "none");
        if (contact != null)
        {
            ctx.LinkContact(contact.Id);
            ctx.Check("…in the lab", ctx.Db.ContactEntities.Any(l => l.ContactId == contact.Id && l.EntityId == ctx.Lab.Hq.Id));
        }
        ctx.Check("The chat is linked to the new contact", contact != null && after.ContactId == contact.Id);
        ctx.Check("The request is resolved as created", Request(ctx, t.Id)?.State == IdentityStates.Created);
        ctx.Check("The AI answers now that they are known", ctx.Replies(t.Id).Count >= 1);
        return Task.CompletedTask;
    }

    private static async Task SeveralMatchesAsync(LabContext ctx)
    {
        var inbox = IdentityInbox(ctx, IdentifyModes.Ask);
        var email = ctx.NewEmail("twin");
        var a = ctx.NewContact("Lee Chen", email: email);
        var b = ctx.NewContact("Lee Chen (work)", email: email);
        var key = ctx.NewPhone();
        ctx.Say(inbox, key, "Hello, I have a question about my booking");
        ctx.Say(inbox, key, $"I'm Lee, {email}");
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.LinkContact(a.Id, "Open first match");
        ctx.LinkContact(b.Id, "Open second match");
        ctx.Check("The chat stays unlinked", t.ContactId == null);
        ctx.Check("The request is ambiguous", Request(ctx, t.Id)?.State == IdentityStates.Ambiguous, Request(ctx, t.Id)?.Note ?? "");
        ctx.Check("The chat is handed over as an unknown customer",
            Handover.IsOpen(t) && t.HandoverKind == HandoverKinds.UnknownCustomer, t.HandoverReason);
        ctx.Check("It is listed in Needs human", await InNeedsHuman(ctx, "whatsapp", key, HandoverKinds.UnknownCustomer));
    }

    private static async Task AskThenCreateAsync(LabContext ctx)
    {
        var inbox = IdentityInbox(ctx, IdentifyModes.Ask);
        var key = ctx.NewPhone();
        ctx.Say(inbox, key, "Good morning, is the pandan cake available?");
        ctx.Say(inbox, key, "This is Farid Hassan");
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("No contact yet — the request needs a person", Request(ctx, t.Id)?.State == IdentityStates.NeedsContact,
            Request(ctx, t.Id)?.State ?? "no request");
        ctx.Check("The chat is handed over as an unknown customer", Handover.IsOpen(t) && t.HandoverKind == HandoverKinds.UnknownCustomer);

        var res = await ctx.Api(ctx.Super, "POST", "/api/contacts/identify/{channel}/{key}/contact",
            new { name = "Farid Hassan", entity = LabFixture.HqSlug },
            route: new() { ["channel"] = "whatsapp", ["key"] = key });
        ctx.Check("“Create contact” succeeds", res.Ok, $"HTTP {res.Status} {res.Detail()}");
        var contactId = LabContext.Int(res.Body, "contact_id");
        if (contactId is int cid) ctx.LinkContact(cid);
        var after = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The chat is linked to the new contact", contactId != null && after.ContactId == contactId);
        ctx.Check("The contact is in the lab", contactId is int id2
            && ctx.Db.ContactEntities.Any(l => l.ContactId == id2 && l.EntityId == ctx.Lab.Hq.Id));
        ctx.Check("The handover is cleared", !Handover.IsOpen(after));
        ctx.Check("The waiting customer got an answer", ctx.Replies(t.Id).Count >= 1);
    }

    private static Task MatchesOneAsync(LabContext ctx)
    {
        var inbox = IdentityInbox(ctx, IdentifyModes.Ask);
        var email = ctx.NewEmail("mei");
        var known = ctx.NewContact("Mei Ling Tan", email: email);
        var key = ctx.NewPhone();
        ctx.Say(inbox, key, $"Hi, it's Mei, {email} — is my order ready?");
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.LinkContact(known.Id);
        ctx.Check("The chat is linked to the existing contact", t.ContactId == known.Id, $"contact #{t.ContactId}");
        ctx.Check("The request is resolved as linked", Request(ctx, t.Id)?.State == IdentityStates.Linked);
        ctx.Check("No ask was needed", !ctx.MessagesOf(t.Id).Any(m => m.Source == MessageSource.System && !m.Private
            && m.Text.Contains("could you tell us")));
        ctx.Check("The AI answered", ctx.Replies(t.Id).Count >= 1);
        return Task.CompletedTask;
    }

    private static async Task InviteAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Nur Aina");
        ctx.LinkContact(c.Id);
        var res = await ctx.Api(ctx.Super, "POST", "/api/contacts/{id:int}/invites",
            new { channel = "telegram", accountId = ctx.Lab.Telegram.Id }, route: new() { ["id"] = c.Id.ToString() });
        var code = LabContext.Str(res.Body, "code");
        ctx.Check("An invite code was made", res.Status == 201 && code.Length >= 4, $"HTTP {res.Status} {res.Detail()}");
        if (code.Length == 0) return;
        var chat = ctx.NewChatId();
        ctx.Say(ctx.Lab.Telegram, chat, $"/start {code}", "Nur on Telegram");
        var t = ctx.ThreadOf("telegram", chat)!;
        ctx.LinkChat("telegram", chat, "Open Telegram chat");
        ctx.Check("The Telegram chat is linked to the contact", t.ContactId == c.Id, $"contact #{t.ContactId}");
        var first = ctx.MessagesOf(t.Id).FirstOrDefault(m => m.Direction == MessageDirection.In);
        ctx.Check("The code is stripped from the customer's message", first != null && !first.Text.Contains(code), first?.Text ?? "");
        var invite = ctx.Db.ContactInvites.AsNoTracking().FirstOrDefault(i => i.Code == code);
        ctx.Check("The invite is marked used", invite?.UsedAt != null);
        ctx.Check("Nothing was sent on the Telegram bot", !ctx.MessagesOf(t.Id).Any(m => m.Direction == MessageDirection.Out && !m.Private
            && m.Source != MessageSource.System));
    }

    // ---- compliance ---------------------------------------------------------------------------------

    private static async Task StopAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Zainab Ali");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "Hi, quick question about delivery", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var job = ctx.Services.GetRequiredService<QueueService>().Enqueue(ChatBranch.Domain.Channels.WhatsApp,
            JobKind.Text, key, new() { ["text"] = "Reminder: your order is out for delivery tomorrow." },
            contactId: c.Id, accountId: ctx.Lab.Conversation.Id, notBefore: DateTime.UtcNow.AddHours(1));
        var before = ctx.Replies(t.Id).Count;

        ctx.Say(ctx.Lab.Conversation, key, "STOP", c.Name);
        var after = ctx.ThreadOf("whatsapp", key)!;
        ctx.Check("The conversation is opted out", after.OptedOut);
        ctx.Check("The queued reminder was cancelled", ctx.Job(job.Id)?.Status == JobStatus.Cancelled, ctx.Job(job.Id)?.Status ?? "");
        ctx.Check("No AI reply to STOP", ctx.Replies(t.Id).Count == before);
        ctx.Check("A private note records the opt-out", ctx.MessagesOf(t.Id).Any(m => m.Private && m.Text.Contains("opted out")));

        var agent = ctx.As(ctx.Lab.HqAgent);
        var refused = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Sorry to see you go — can we help?" }, route: new() { ["key"] = key });
        ctx.Check("A plain manual send is refused (409)", refused.Status == 409, refused.Detail());
        var forced = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Your refund has been processed.", force = true }, route: new() { ["key"] = key });
        ctx.Check("A forced manual send goes through", forced.Ok, $"HTTP {forced.Status} {forced.Detail()}");
    }

    private static Task StartAsync(LabContext ctx)
    {
        var c = ctx.NewContact("Ahmad Firdaus");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "STOP", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        ctx.Check("STOP opts out", t.OptedOut);
        ctx.Say(ctx.Lab.Conversation, key, "START", c.Name);
        ctx.Check("START opts back in", !ctx.ThreadOf("whatsapp", key)!.OptedOut);
        ctx.Check("A note records the opt-in", ctx.MessagesOf(t.Id).Any(m => m.Private && m.Text.Contains("opted back in")));
        var before = ctx.Replies(t.Id).Count;
        ctx.Brain.Say("Yes — there is free parking behind the shop.");
        ctx.Say(ctx.Lab.Conversation, key, "Do you have parking?", c.Name);
        ctx.Check("The AI answers again", ctx.Replies(t.Id).Count == before + 1
            && ctx.Replies(t.Id).Last().Text.Contains("parking"));
        return Task.CompletedTask;
    }

    private static async Task ColdTextAsync(LabContext ctx)
    {
        var guard = ctx.Sc.SettingValue("cold_text_guard");
        if (guard is "warn" or "allow")
            ctx.Skip($"The organization's cold-text guard is “{guard}” (Account assignment → routing rules), so a cold send is allowed by design.");
        var c = ctx.NewContact("Patrick Lau");
        var key = LabContext.WaKey(c);
        var created = await ctx.Api(ctx.Super, "POST", "/api/whatsapp/threads",
            new { phone = key, name = c.Name, account_id = ctx.Lab.Conversation.Id.ToString() });
        ctx.Check("A new chat is opened on the lab's conversation line", created.Ok, $"HTTP {created.Status} {created.Detail()}");
        ctx.LinkChat("whatsapp", key);
        var agent = ctx.As(ctx.Lab.HqAgent);
        var cold = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Hi Patrick! We have a promotion this week." }, route: new() { ["key"] = key });
        ctx.Check("Free text to someone who never wrote is refused (409)", cold.Status == 409
            && cold.Detail().Contains("never received a message"), cold.Detail());
        var agentForce = await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Hi Patrick! We have a promotion this week.", force = true }, route: new() { ["key"] = key });
        ctx.Check("An agent cannot force it (admins only)", agentForce.Status == 409, $"HTTP {agentForce.Status}");
        var forced = await ctx.Api(ctx.Super, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Hi Patrick! We have a promotion this week.", force = true }, route: new() { ["key"] = key });
        ctx.Check("An admin's forced send goes through", forced.Ok, $"HTTP {forced.Status} {forced.Detail()}");
    }
}
