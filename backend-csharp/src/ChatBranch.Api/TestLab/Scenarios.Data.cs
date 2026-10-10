using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>Contacts &amp; data, bookings and the platform (assistant, language, widget).</summary>
public static class DataScenarios
{
    public static IEnumerable<LabScenario> All() => new[]
    {
        // ---- contacts & data ------------------------------------------------------------------
        new LabScenario("csv-import-duplicates", "contacts",
            "CSV import fills a duplicate's blanks and merges repeats",
            "The import wizard's “fill empty” mode matches existing contacts by phone, fills only their blanks, and merges repeated rows within the file.",
            new[]
            {
                "A lab contact exists without an email or company.",
                "Import three rows into the lab: that contact (with email + company) and a new person listed twice.",
                "Import the same file again with “skip duplicates”.",
            },
            new[]
            {
                "First import: the existing contact gains the email and company; the new person is created once.",
                "Second import: nothing new is created.",
            },
            ImportAsync),

        new LabScenario("tag-group-exclusive", "contacts",
            "Tags in one group are exclusive",
            "A contact holds at most one tag of an exclusive group: adding Silver removes Gold, and saving both is refused.",
            new[] { "Two lab tags, Gold and Silver, in the group “Lab tier”.", "Tag a contact Gold, then Silver; then try to save both." },
            new[] { "Only Silver remains; saving both answers 400." },
            TagGroupAsync),

        new LabScenario("template-variables", "contacts",
            "Template variables render {{first_name}} and {{cf_*}}",
            "Quick replies fill contact variables and custom fields; an empty field renders as nothing, never a raw placeholder.",
            new[]
            {
                "A lab custom field “Membership” and a lab quick reply using {{first_name}}, {{cf_testlab_membership}} and {{cf_testlab_birthday}}.",
                "Render it for a Gold member with no birthday on file.",
            },
            new[] { "“Hi Mei, your membership is Gold.” — and no {{…}} left over." },
            TemplateVarsAsync),

        // ---- bookings ------------------------------------------------------------------------------
        new LabScenario("booking-book-and-cancel", "booking",
            "The AI offers times, books the one picked, an agent cancels it",
            "A calendar skill (ChatBranch bookings, no credentials) offers free slots, reserves the chosen one while writing, makes it when the reply is delivered, and an operator cancel frees it.",
            new[]
            {
                "Turn on the lab skill “Test lab bookings”.",
                "The customer asks for an appointment; the AI offers two times.",
                "The customer picks the first one; the AI books it.",
                "The front-desk agent cancels it from Bookings.",
            },
            new[]
            {
                "Offer: two times, nothing booked yet.",
                "Pick: a booked row with a 📅 note and a confirmation naming the time.",
                "Cancel: the booking is cancelled with a 📅 note.",
            },
            BookAsync),

        new LabScenario("booking-held-reply-releases", "booking",
            "A held reply books nothing — rejecting it releases the slot",
            "With review on, the reservation waits for the reviewer; rejecting the reply releases it instead of booking.",
            new[] { "An inbox on review-every-reply; the customer picks a time.", "The draft is rejected." },
            new[] { "While held: the booking is only reserved (pending).", "After rejection: released, and nothing was sent." },
            HeldBookingAsync),

        // ---- platform ---------------------------------------------------------------------------------
        new LabScenario("assistant-history-isolation", "platform",
            "Ask ChatBranch history is private to each user",
            "One person's assistant conversation can't be opened or listed by another login.",
            new[] { "The front-desk agent asks Ask ChatBranch a question.", "The North agent tries to open it." },
            new[] { "Its owner reads it back; the other agent gets 404 and doesn't see it in their history." },
            AssistantAsync),

        new LabScenario("language-preference", "platform",
            "A user's language preference round-trips",
            "Settings → Appearance → Language saves per user: Bahasa Melayu, 简体中文, back to the organization default; unknown codes are refused.",
            new[] { "As the front-desk agent: set ms, read it, set zh, try xx, reset." },
            new[] { "Each choice reads back; xx answers 400; reset returns null (the organization default)." },
            LanguageAsync),

        new LabScenario("widget-visitor", "platform",
            "A website visitor chats through the widget and gets an answer",
            "The widget's public endpoints start a session, the message lands in the lab's inbox like any channel, and the AI's answer comes back to the visitor.",
            new[] { "Open a widget session with a name and email.", "Send “Are you open on Sunday?”; poll for replies." },
            new[]
            {
                "The conversation is in the lab, owned by the front desk, with a lab contact for the visitor.",
                "The visitor's poll returns the AI's answer.",
            },
            WidgetAsync),

        new LabScenario("ai-playground-no-side-effects", "platform",
            "The AI playground never touches real data",
            "Chatting with the AI in the playground — as a made-up customer or on a copy of a real conversation — runs the real generator and shows what production would do, but sends, stores and books nothing; only the model usage is metered.",
            new[]
            {
                "A lab customer writes on the lab WhatsApp line (a real conversation to copy).",
                "As the front-desk agent, chat twice with a made-up customer in the lab's playground.",
                "Try a persona draft as the agent; copy the lab conversation into the playground and answer it.",
                "As the North agent, try to copy the head office's conversation.",
            },
            new[]
            {
                "Each reply comes back with what production would do; nothing new in the lab's conversations, messages, AI monitor, bookings or notifications.",
                "The persona draft needs “Change AI behaviour” (403); the North agent gets 404; the usage is metered as “playground”.",
            },
            PlaygroundAsync),
    };

    // ---- contacts & data --------------------------------------------------------------------------

    private static async Task ImportAsync(LabContext ctx)
    {
        var existing = ctx.NewContact("Aisha Binti Omar");
        var newPhone = "+" + ctx.NewPhone();
        var email = ctx.NewEmail("aisha");
        object Rows() => new object[]
        {
            new Dictionary<string, string> { ["Name"] = "Aisha Binti Omar", ["Phone"] = existing.Phone, ["Email"] = email, ["Company"] = "Omar Trading" },
            new Dictionary<string, string> { ["Name"] = "Daniel Tan", ["Phone"] = newPhone, ["Email"] = "", ["Company"] = "" },
            new Dictionary<string, string> { ["Name"] = "Daniel Tan (again)", ["Phone"] = newPhone, ["Email"] = "", ["Company"] = "" },
        };
        var first = await ctx.Api(ctx.Super, "POST", "/api/contacts/import", new
        {
            rows = Rows(), mode = "update_empty", entity = LabFixture.HqSlug, tags = new[] { LabFixture.TagName },
            discover = false, source = "csv",
        });
        ctx.Check("The import is accepted", first.Ok, $"HTTP {first.Status} {first.Detail()}");
        ctx.Link("Contacts", "/contacts");
        ctx.LinkContact(existing.Id, "Open the updated contact");
        var updated = ctx.ContactOf(existing.Id)!;
        ctx.Check("The existing contact gained the email", updated.Email == email, updated.Email);
        ctx.Check("…and the company", updated.CompanyName == "Omar Trading", updated.CompanyName);
        var tail = PhoneMatch.Tail(newPhone);
        var created = ctx.Db.Contacts.AsNoTracking().Where(c => c.Phone != "").AsEnumerable()
            .Where(c => PhoneMatch.Tail(c.Phone) == tail).ToList();
        ctx.Check("The repeated new person was created once", created.Count == 1, $"{created.Count} contact(s)");
        if (created.Count == 1)
        {
            ctx.Check("…in the lab, tagged test-lab", ctx.Db.ContactEntities.Any(l => l.ContactId == created[0].Id
                && l.EntityId == ctx.Lab.Hq.Id) && created[0].Tags.Contains(LabFixture.TagName), string.Join(", ", created[0].Tags));
        }
        ctx.Check("The report counts a created and an updated contact",
            LabContext.Int(first.Body, "created") == 1 && LabContext.Int(first.Body, "updated") == 1,
            $"created {LabContext.Int(first.Body, "created")}, updated {LabContext.Int(first.Body, "updated")}, "
            + $"merged {LabContext.Int(first.Body, "merged")}, duplicates {LabContext.Int(first.Body, "duplicates")}");

        var again = await ctx.Api(ctx.Super, "POST", "/api/contacts/import", new
        {
            rows = Rows(), mode = "skip", entity = LabFixture.HqSlug, tags = new[] { LabFixture.TagName },
            discover = false, source = "csv",
        });
        ctx.Check("Importing again with “skip duplicates” creates nothing", again.Ok && LabContext.Int(again.Body, "created") == 0,
            $"created {LabContext.Int(again.Body, "created")}, skipped {LabContext.Int(again.Body, "skipped")}");
    }

    private static async Task<int?> EnsureTag(LabContext ctx, string name, string color)
    {
        var row = ctx.Db.Tags.AsNoTracking().FirstOrDefault(t => t.EntityId == ctx.Lab.Hq.Id && t.Name == name);
        if (row != null) return row.Id;
        var res = await ctx.Api(ctx.Super, "POST", "/api/tags", new
        {
            name, applies_to = "contact", entity = LabFixture.HqSlug, group = "Lab tier", color,
            description = "Created by the Test lab.",
        });
        return LabContext.Int(res.Body, "id");
    }

    private static async Task TagGroupAsync(LabContext ctx)
    {
        var gold = await EnsureTag(ctx, "Lab Gold", "ochre");
        var silver = await EnsureTag(ctx, "Lab Silver", "ink");
        ctx.Check("The two tags exist in the group “Lab tier”", gold != null && silver != null);
        if (gold == null || silver == null) return;
        ctx.Link("Tags", "/tags");
        var c = ctx.NewContact("Rachel Ng");
        ctx.LinkContact(c.Id);
        var add = await ctx.Api(ctx.Super, "POST", "/api/tags/{id:int}/targets", new { add = new[] { c.Id } },
            route: new() { ["id"] = gold.Value.ToString() });
        ctx.Check("Tagging Gold works", add.Ok, $"HTTP {add.Status} {add.Detail()}");
        await ctx.Api(ctx.Super, "POST", "/api/tags/{id:int}/targets", new { add = new[] { c.Id } },
            route: new() { ["id"] = silver.Value.ToString() });
        var names = ctx.ContactOf(c.Id)!.Tags;
        ctx.Check("Adding Silver removed Gold", names.Contains("Lab Silver") && !names.Contains("Lab Gold"), string.Join(", ", names));
        var both = await ctx.Api(ctx.Super, "PUT", "/api/contacts/{id:int}/tags",
            new { tags = new[] { new { tag_id = gold.Value }, new { tag_id = silver.Value } } },
            route: new() { ["id"] = c.Id.ToString() });
        ctx.Check("Saving both tags of the group is refused (400)", both.Status == 400, both.Detail());
    }

    private static async Task TemplateVarsAsync(LabContext ctx)
    {
        LabKit.CustomField(ctx, "testlab_membership", "Test lab · Membership", "select", "Gold", "Silver", "Bronze");
        LabKit.CustomField(ctx, "testlab_birthday", "Test lab · Birthday", "text");
        const string code = "testlab-membership";
        if (!ctx.Db.CannedResponses.Any(c => c.EntityId == ctx.Lab.Hq.Id && c.ShortCode == code))
        {
            var made = await ctx.Api(ctx.Super, "POST", "/api/messaging/canned", new
            {
                short_code = code, entity = LabFixture.HqSlug,
                content = "Hi {{first_name}}, your membership is {{cf_testlab_membership}}.{{cf_testlab_birthday}}",
            });
            ctx.Check("The lab quick reply is saved", made.Ok, $"HTTP {made.Status} {made.Detail()}");
        }
        ctx.Link("Templates", "/templates?tab=messages");
        var c = ctx.NewContact("Mei Ling Tan", attributes: new() { ["testlab_membership"] = "Gold" });
        ctx.LinkContact(c.Id);
        var res = await ctx.Api(ctx.Super, "GET", "/api/messaging/canned",
            query: new() { ["entity"] = LabFixture.HqSlug, ["contact_id"] = c.Id.ToString() });
        var rendered = "";
        if (res.Ok && res.Body.TryGetProperty("canned", out var list))
            foreach (var item in list.EnumerateArray())
                if (LabContext.Str(item, "short_code") == code) rendered = LabContext.Str(item, "rendered");
        ctx.Check("{{first_name}} and {{cf_testlab_membership}} render", rendered == "Hi Mei, your membership is Gold.", rendered);
        ctx.Check("No raw placeholder is left over", !rendered.Contains("{{"), rendered);
    }

    // ---- bookings --------------------------------------------------------------------------------------

    private static async Task<bool> CalendarOn(LabContext ctx)
    {
        var on = await LabKit.Skills(ctx, calendar: true);
        ctx.Check("The lab's calendar skill is on (through the skills endpoint)", on.Ok, on.Detail);
        ctx.Brain.BookingSkillName = LabKit.CalendarSkillName;
        ctx.Link("AI skills", "/skills");
        return on.Ok;
    }

    private static async Task BookAsync(LabContext ctx)
    {
        if (!await CalendarOn(ctx)) return;
        var c = ctx.NewContact("Sarah Abdullah");
        var key = LabContext.WaKey(c);
        ctx.LinkContact(c.Id);
        ctx.Say(ctx.Lab.Conversation, key, "Hi, can I book an appointment this week?", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var offer = ctx.Replies(t.Id).LastOrDefault()?.Text ?? "";
        ctx.Check("The AI offered two times", offer.StartsWith("I can book you on") && offer.Contains(" or "), offer);
        ctx.Check("Nothing is booked yet", !ctx.Db.Bookings.Any(b => b.ContactId == c.Id && b.Status == BookingStatus.Booked));

        ctx.Say(ctx.Lab.Conversation, key, "The first one please", c.Name);
        var booking = ctx.Db.Bookings.AsNoTracking().Where(b => b.ContactId == c.Id).OrderByDescending(b => b.Id).FirstOrDefault();
        ctx.Check("The booking is made", booking?.Status == BookingStatus.Booked, booking?.Status ?? "no booking");
        ctx.Link("Bookings", $"/bookings?contact={c.Id}");
        ctx.Check("A 📅 note records it", ctx.MessagesOf(t.Id).Any(m => m.Private && m.Text.StartsWith("📅 Booked")));
        var confirm = ctx.Replies(t.Id).LastOrDefault()?.Text ?? "";
        ctx.Check("The AI confirmed the time", confirm.StartsWith("You're booked for"), confirm);
        if (booking == null) return;

        var agent = ctx.As(ctx.Lab.HqAgent);
        var cancel = await ctx.Api(agent, "POST", "/api/bookings/{id:int}/cancel", route: new() { ["id"] = booking.Id.ToString() });
        ctx.Check("The agent cancels it from Bookings", cancel.Ok, $"HTTP {cancel.Status} {cancel.Detail()}");
        var after = ctx.Db.Bookings.AsNoTracking().First(b => b.Id == booking.Id);
        ctx.Check("The booking is cancelled", after.Status == BookingStatus.Cancelled, after.Status);
        ctx.Check("A 📅 note records the cancellation", ctx.MessagesOf(t.Id).Any(m => m.Private && m.Text.StartsWith("📅 Cancelled")));
    }

    private static async Task HeldBookingAsync(LabContext ctx)
    {
        if (!await CalendarOn(ctx)) return;
        var inbox = ctx.Inbox("review every reply", new() { ["ai_review_mode"] = "review_all" });
        var c = ctx.NewContact("Irfan Hakim");
        var key = LabContext.WaKey(c);
        ctx.Say(inbox, key, "The first one please — book it", c.Name);
        var t = ctx.ThreadOf("whatsapp", key)!;
        ctx.LinkChat("whatsapp", key);
        var d = ctx.DraftsOf(t.Id).LastOrDefault();
        ctx.Check("The reply is held for review", d?.Status == AiDraftStatus.Pending, d?.Status ?? "no draft");
        var held = ctx.Db.Bookings.AsNoTracking().Where(b => b.ContactId == c.Id).OrderByDescending(b => b.Id).FirstOrDefault();
        ctx.Check("The slot is only reserved (pending)", held?.Status == BookingStatus.Pending, held?.Status ?? "no booking");
        if (d == null) return;
        var rej = await ctx.Api(ctx.Super, "POST", "/api/ai/monitor/{id:int}/reject", new { reason = "Wrong branch" },
            route: new() { ["id"] = d.Id.ToString() });
        ctx.Check("The reviewer rejects it", rej.Ok, $"HTTP {rej.Status} {rej.Detail()}");
        var after = held == null ? null : ctx.Db.Bookings.AsNoTracking().First(b => b.Id == held.Id);
        ctx.Check("The reservation is released", after?.Status == BookingStatus.Released, after?.Status ?? "");
        ctx.Check("Nothing was sent", ctx.Replies(t.Id).Count == 0);
    }

    // ---- platform ---------------------------------------------------------------------------------------

    private static async Task AssistantAsync(LabContext ctx)
    {
        var owner = ctx.As(ctx.Lab.HqAgent);
        var other = ctx.As(ctx.Lab.NorthAgent);
        ctx.Link("Ask ChatBranch (bottom-right on every page)", "/dashboard");
        var chat = await ctx.Api(owner, "POST", "/api/assistant/chat", new { message = "How do I hand a chat to a person?" });
        var id = LabContext.Int(chat.Body, "conversation_id");
        ctx.Check("The question is answered and stored", chat.Ok && id != null, $"HTTP {chat.Status} {chat.Detail()}");
        if (id == null) return;
        var route = new Dictionary<string, string> { ["id"] = id.Value.ToString() };
        var mine = await ctx.Api(owner, "GET", "/api/assistant/conversations/{id:int}", route: route);
        ctx.Check("Its owner reads it back", mine.Ok && LabKit.Count(mine.Body, "messages") >= 2, $"HTTP {mine.Status}");
        var theirs = await ctx.Api(other, "GET", "/api/assistant/conversations/{id:int}", route: route);
        ctx.Check("Another login gets 404", theirs.Status == 404, $"HTTP {theirs.Status}");
        var list = await ctx.Api(other, "GET", "/api/assistant/conversations");
        ctx.Check("…and doesn't see it in their history", list.Ok && !list.Body.GetRawText().Contains($"\"id\":{id},"));
    }

    private static async Task LanguageAsync(LabContext ctx)
    {
        var me = ctx.As(ctx.Lab.HqAgent);
        async Task<string?> Put(object? locale)
        {
            var r = await ctx.Api(me, "PUT", "/api/auth/preferences", new Dictionary<string, object?> { ["locale"] = locale });
            return r.Ok ? (r.Body.TryGetProperty("locale", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null)
                : $"HTTP {r.Status}";
        }
        ctx.Check("Bahasa Melayu is saved", await Put("ms") == "ms");
        var read = await ctx.Api(me, "GET", "/api/auth/preferences");
        ctx.Check("…and reads back", LabContext.Str(read.Body, "locale") == "ms", LabContext.Str(read.Body, "locale"));
        ctx.Check("简体中文 is saved", await Put("zh") == "zh");
        ctx.Check("An unknown language is refused (400)", await Put("xx") == "HTTP 400");
        ctx.Check("Reset returns to the organization default (null)", await Put(null) == null);
        ctx.Link("Appearance → Language", "/settings/appearance");
    }

    private static async Task PlaygroundAsync(LabContext ctx)
    {
        // a real lab conversation to copy (made before the "nothing changes" snapshot)
        var c = ctx.NewContact("Playground Pat");
        var key = LabContext.WaKey(c);
        ctx.Say(ctx.Lab.Conversation, key, "Hi, do you have gluten-free cakes?", "Playground Pat");
        ctx.LinkChat("whatsapp", key, "The conversation copied into the playground");
        ctx.Link("AI playground", "/ai-playground");

        var lab = ctx.Lab.EntityIds.ToList();
        var labUsers = new[] { ctx.Lab.HqAgent.Id, ctx.Lab.NorthAgent.Id };
        var madeUp = $"Playground Quinn {ctx.Tag}";
        var notifFrom = ctx.Db.Notifications.AsNoTracking().Max(n => (int?)n.Id) ?? 0;
        var usageFrom = ctx.Db.LlmUsage.AsNoTracking().Max(u => (int?)u.Id) ?? 0;
        (int Threads, int Messages, int Drafts, int Bookings) Fence() => (
            ctx.Db.Threads.AsNoTracking().Count(t => t.EntityId != null && lab.Contains(t.EntityId.Value)),
            ctx.Db.Messages.AsNoTracking().Count(m => m.Thread!.EntityId != null && lab.Contains(m.Thread.EntityId.Value)),
            ctx.Db.AiDrafts.AsNoTracking().Count(d => d.EntityId != null && lab.Contains(d.EntityId.Value)),
            ctx.Db.Bookings.AsNoTracking().Count(b => b.EntityId != null && lab.Contains(b.EntityId.Value)));
        var before = Fence();

        var agent = ctx.As(ctx.Lab.HqAgent);
        var opts = await ctx.Api(agent, "GET", "/api/ai/playground/options", query: new() { ["entity"] = LabFixture.HqSlug });
        ctx.Check("The front-desk agent opens the playground for the lab", opts.Ok, $"HTTP {opts.Status} {opts.Detail()}");
        ctx.Check("…without the admin-only “try changes”",
            opts.Ok && opts.Body.TryGetProperty("can_override", out var co) && co.ValueKind == JsonValueKind.False);

        object Body(object[] messages, object? options = null, string? thread = null) => new Dictionary<string, object?>
        {
            ["entity"] = LabFixture.HqSlug, ["channel"] = "whatsapp", ["thread"] = thread,
            ["profile"] = new { name = madeUp, language = "en", tags = new[] { "VIP" } },
            ["messages"] = messages, ["options"] = options,
        };
        var first = await ctx.Api(agent, "POST", "/api/ai/playground/reply",
            Body(new object[] { new { role = "customer", text = "Hello! What time do you open on Saturday?" } }));
        var verdict = first.Ok && first.Body.TryGetProperty("production", out var p) ? LabContext.Str(p, "verdict") : "";
        ctx.Check("A made-up customer gets a reply with what production would do",
            first.Ok && LabContext.Str(first.Body, "draft").Length > 0 && verdict.Length > 0,
            $"HTTP {first.Status} {first.Detail()} · verdict {verdict}");
        var second = await ctx.Api(agent, "POST", "/api/ai/playground/reply", Body(new object[]
        {
            new { role = "customer", text = "Hello! What time do you open on Saturday?" },
            new { role = "ai", text = LabContext.Str(first.Body, "draft") },
            new { role = "customer", text = "Great — and can I book a table?" },
        }));
        ctx.Check("…and a second turn on the same transcript", second.Ok, $"HTTP {second.Status} {second.Detail()}");

        var persona = await ctx.Api(agent, "POST", "/api/ai/playground/reply",
            Body(new object[] { new { role = "customer", text = "Hi" } }, new { persona = "Be very formal." }));
        ctx.Check("A persona draft needs “Change AI behaviour” (403)", persona.Status == 403, $"HTTP {persona.Status}");

        var imported = await ctx.Api(agent, "POST", "/api/ai/playground/import", new { channel = "whatsapp", key });
        var turns = imported.Ok && imported.Body.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array
            ? ms.EnumerateArray().Select(m => new { role = LabContext.Str(m, "role"), text = LabContext.Str(m, "text") })
                .ToList()
            : new();
        ctx.Check("The lab conversation is copied into the playground", imported.Ok && turns.Count > 0,
            $"HTTP {imported.Status} · {turns.Count} message(s)");
        if (turns.Count > 0)
        {
            var transcript = turns.Cast<object>().ToList();
            if (turns[^1].role != "customer") transcript.Add(new { role = "customer", text = "Is that all of them?" });
            var copy = await ctx.Api(agent, "POST", "/api/ai/playground/reply",
                Body(transcript.ToArray(), thread: LabContext.Str(imported.Body, "thread")));
            ctx.Check("…and answered there", copy.Ok, $"HTTP {copy.Status} {copy.Detail()}");
        }
        var north = await ctx.Api(ctx.As(ctx.Lab.NorthAgent), "POST", "/api/ai/playground/import",
            new { channel = "whatsapp", key });
        ctx.Check("The North agent can't copy the head office's conversation (404)", north.Status == 404,
            $"HTTP {north.Status}");

        var after = Fence();
        ctx.Check("No conversation, message, AI-monitor draft or booking was added in the lab", before == after,
            $"before {before} · after {after}");
        var notes = ctx.Db.Notifications.AsNoTracking().Count(n => n.Id > notifFrom
            && (labUsers.Contains(n.UserId) || n.Title.Contains(madeUp) || n.Body.Contains(madeUp)));
        ctx.Check("Nobody was notified", notes == 0, $"{notes} notification(s)");
        var usage = ctx.Db.LlmUsage.AsNoTracking().Count(u => u.Id > usageFrom && u.Feature == "playground"
            && u.Ref == "playground:" + ctx.Lab.HqAgent.Username);
        ctx.Check("The model usage is metered as “playground”", usage >= 3, $"{usage} usage row(s)");
    }

    private static async Task WidgetAsync(LabContext ctx)
    {
        var token = ctx.Lab.Widget.ExternalId;
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        var route = new Dictionary<string, string> { ["token"] = token };
        var email = ctx.NewEmail("visitor");
        var session = await ctx.Api(anon, "POST", "/api/widget/{token}/session", new { name = "Lab visitor", email }, route: route);
        var sid = LabContext.Str(session.Body, "session");
        ctx.Check("A visitor session starts", session.Ok && sid.Length > 0, $"HTTP {session.Status} {session.Detail()}");
        if (sid.Length == 0) return;
        var sent = await ctx.Api(anon, "POST", "/api/widget/{token}/messages", new { session = sid, text = "Are you open on Sunday?" },
            route: route);
        ctx.Check("The visitor's message is accepted", sent.Ok, $"HTTP {sent.Status} {sent.Detail()}");
        var t = ctx.ThreadOf("website", sid);
        ctx.LinkChat("website", sid);
        ctx.Check("The conversation is in the lab, owned by the front desk",
            t != null && t.EntityId == ctx.Lab.Hq.Id && t.TeamId == ctx.Lab.FrontDesk.Id);
        var visitor = ctx.Db.Contacts.AsNoTracking().FirstOrDefault(c => c.Email == email);
        ctx.Check("The visitor became a lab contact (lab email)", visitor != null && t?.ContactId == visitor.Id);
        var poll = await ctx.Api(anon, "GET", "/api/widget/{token}/messages", route: route, query: new() { ["session"] = sid });
        var raw = poll.Body.GetRawText();
        ctx.Check("The visitor's poll returns an answer", poll.Ok && t != null && ctx.MessagesOf(t.Id)
            .Any(m => m.Direction == MessageDirection.Out && !m.Private && raw.Contains(JsonEncodedText.Encode(m.Text.Length > 20 ? m.Text[..20] : m.Text).ToString())),
            $"HTTP {poll.Status}");
    }
}
