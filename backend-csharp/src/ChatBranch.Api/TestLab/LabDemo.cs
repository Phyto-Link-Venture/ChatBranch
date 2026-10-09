using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// <c>POST /api/testlab/demo</c>: a realistic dataset INSIDE the lab fence for
/// clicking around — a head office and two branches, ~30 contacts with tags and
/// custom fields, conversations on WhatsApp, Telegram and the website widget in
/// every state (open, pending, resolved, snoozed, handed over, opted out,
/// waiting on AI review, an unknown customer), paused flows, knowledge items, a
/// booking skill with bookings and automation rules. Every conversation is made
/// through the same paths a real one takes (the inbound pipeline, the AI gate,
/// the dashboard's endpoints), then aged so the inbox reads like a real week.
/// Loaded once: a second call reports it is already there.
/// </summary>
public static class LabDemo
{
    public const string DemoTagName = "test-lab-demo";

    public sealed record Outcome(bool Already, Dictionary<string, int> Created, List<LabLink> Links);

    public static int DemoTagCount(AppDbContext db) =>
        db.TagAssignments.AsNoTracking().Count(a => a.Tag!.Name == DemoTagName && a.Tag.EntityId != null);

    public static Outcome Load(HttpContext http, ClaimsPrincipal me)
    {
        using var scope = http.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        var lab = LabFixture.Ensure(db, sc, Caller.TenantId(me));
        if (DemoTagCount(db) > 0) return new Outcome(true, new(), new());
        var ctx = new LabContext(scope.ServiceProvider, http, me, lab);
        try
        {
            return BuildAsync(ctx).GetAwaiter().GetResult();
        }
        finally
        {
            ctx.RunCleanup();
        }
    }

    private static Tag LabTag(LabContext ctx, string name, string color, string group = "")
    {
        var tag = ctx.Db.Tags.FirstOrDefault(t => t.EntityId == ctx.Lab.Hq.Id && t.Name == name && t.AppliesTo == "contact");
        if (tag != null) return tag;
        tag = new Tag
        {
            Name = name, Slug = TagService.UniqueSlug(ctx.Db, name), AppliesTo = TagService.ContactTarget, Color = color,
            GroupName = group, EntityId = ctx.Lab.Hq.Id, Description = "Test lab demo data.",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.Db.Tags.Add(tag);
        ctx.Db.SaveChanges();
        return tag;
    }

    /// <summary>Move a conversation back in time (messages, thread) so the inbox reads like a real week.</summary>
    private static void Age(LabContext ctx, string channel, string key, double hours)
    {
        var t = ctx.Db.Threads.FirstOrDefault(x => x.Channel == channel && x.Key == key);
        if (t == null) return;
        var shift = TimeSpan.FromHours(hours);
        foreach (var m in ctx.Db.Messages.Where(m => m.ThreadId == t.Id).ToList()) m.CreatedAt -= shift;
        t.CreatedAt -= shift;
        t.UpdatedAt = DateTime.UtcNow.AddSeconds(-1) - shift;
        ctx.Db.SaveChanges();
    }

    private static readonly (string Name, string Branch, string Lang, string Company, string Tier, string Favourite, string[] Tags)[] People =
    {
        ("Aisha Rahman", "hq", "ms", "", "Gold", "Pandan chiffon", new[] { "VIP" }),
        ("Tan Wei Ming", "hq", "zh", "Tan Brothers Catering", "Silver", "Black forest", new[] { "Wholesale" }),
        ("Priya Nair", "hq", "en", "", "Bronze", "Red velvet", new[] { "New customer" }),
        ("Daniel Wong", "hq", "en", "", "", "Cheesecake", Array.Empty<string>()),
        ("Nurul Huda", "hq", "ms", "", "Gold", "Kek lapis", new[] { "VIP" }),
        ("Kevin Ong", "hq", "en", "Ong Events", "Silver", "Tiramisu", new[] { "Wholesale" }),
        ("Melissa Teo", "hq", "en", "", "", "Carrot cake", new[] { "New customer" }),
        ("Siti Aminah", "hq", "ms", "", "Bronze", "Ondeh-ondeh cake", Array.Empty<string>()),
        ("Lim Mei Ling", "hq", "zh", "", "Gold", "Durian crepe", new[] { "VIP" }),
        ("Arjun Menon", "hq", "en", "Menon Dental", "", "Chocolate fudge", Array.Empty<string>()),
        ("Farah Aziz", "hq", "ms", "", "Silver", "Pandan chiffon", Array.Empty<string>()),
        ("Benjamin Koh", "hq", "en", "", "", "Lemon tart", new[] { "New customer" }),
        ("Chong Kar Yan", "hq", "zh", "", "Bronze", "Matcha roll", Array.Empty<string>()),
        ("Hannah Lee", "hq", "en", "", "Gold", "Red velvet", new[] { "VIP" }),
        ("Hafiz Rahman", "north", "ms", "", "Silver", "Kek batik", Array.Empty<string>()),
        ("Aminah Yusof", "north", "ms", "", "", "Pandan chiffon", new[] { "New customer" }),
        ("Ravi Kumar", "north", "en", "Kumar Textiles", "Gold", "Black forest", new[] { "Wholesale" }),
        ("Goh Siew Ling", "north", "zh", "", "Bronze", "Cheesecake", Array.Empty<string>()),
        ("Joseph Tan", "north", "en", "", "", "Tiramisu", Array.Empty<string>()),
        ("Zainab Ali", "north", "ms", "", "Silver", "Kek lapis", new[] { "VIP" }),
        ("Patrick Lau", "north", "en", "", "", "Carrot cake", Array.Empty<string>()),
        ("Nur Aina", "north", "ms", "", "Bronze", "Ondeh-ondeh cake", new[] { "New customer" }),
        ("Kok Leong Chan", "south", "zh", "Chan Hardware", "Gold", "Durian crepe", new[] { "Wholesale" }),
        ("Jessica Lim", "south", "en", "", "Silver", "Matcha roll", Array.Empty<string>()),
        ("Ahmad Firdaus", "south", "ms", "", "", "Kek batik", Array.Empty<string>()),
        ("Mary Fernandez", "south", "en", "", "Bronze", "Lemon tart", new[] { "New customer" }),
        ("Suresh Pillai", "south", "en", "Pillai & Sons", "Gold", "Chocolate fudge", new[] { "VIP", "Wholesale" }),
        ("Ong Bee Hoon", "south", "zh", "", "", "Pandan chiffon", Array.Empty<string>()),
        ("Irfan Hakim", "south", "ms", "", "Silver", "Red velvet", Array.Empty<string>()),
        ("Rachel Ng", "south", "en", "", "Bronze", "Cheesecake", new[] { "VIP" }),
    };

    private static async Task<Outcome> BuildAsync(LabContext ctx)
    {
        var created = new Dictionary<string, int>();
        void Count(string what, int n = 1) => created[what] = created.GetValueOrDefault(what) + n;
        var links = new List<LabLink>();
        var lab = ctx.Lab;

        // ---- custom fields, tags ---------------------------------------------------------
        LabKit.CustomField(ctx, "testlab_membership", "Test lab · Membership", "select", "Gold", "Silver", "Bronze");
        LabKit.CustomField(ctx, "testlab_favourite", "Test lab · Favourite cake", "text");
        LabKit.CustomField(ctx, "testlab_birthday", "Test lab · Birthday", "text");
        var demoTag = LabTag(ctx, DemoTagName, "ink");
        var tags = new Dictionary<string, Tag>
        {
            ["VIP"] = LabTag(ctx, "VIP", "tangerine"),
            ["Wholesale"] = LabTag(ctx, "Wholesale", "teal"),
            ["New customer"] = LabTag(ctx, "New customer", "green"),
        };
        var tiers = new Dictionary<string, Tag>
        {
            ["Gold"] = LabTag(ctx, "Lab Gold", "ochre", "Lab tier"),
            ["Silver"] = LabTag(ctx, "Lab Silver", "ink", "Lab tier"),
            ["Bronze"] = LabTag(ctx, "Lab Bronze", "plum", "Lab tier"),
        };
        Count("tags", 1 + tags.Count + tiers.Count);

        // ---- contacts ----------------------------------------------------------------------
        var contacts = new Dictionary<string, Contact>();
        var rnd = new Random(42);
        foreach (var p in People)
        {
            var entity = p.Branch switch { "north" => lab.North.Id, "south" => lab.South.Id, _ => lab.Hq.Id };
            var attrs = new Dictionary<string, object?> { ["testlab_favourite"] = p.Favourite };
            if (p.Tier.Length > 0) attrs["testlab_membership"] = p.Tier;
            if (rnd.Next(3) == 0) attrs["testlab_birthday"] = $"{rnd.Next(1970, 2004)}-{rnd.Next(1, 13):00}-{rnd.Next(1, 29):00}";
            var c = ctx.NewContact(p.Name, entity, email: ctx.NewEmail(p.Name.Split(' ')[0]), language: p.Lang,
                attributes: attrs);
            if (p.Company.Length > 0) c.CompanyName = p.Company;
            c.Notes = "Test lab demo contact.";
            ctx.Db.SaveChanges();
            TagService.Assign(ctx.Db, demoTag, TagService.ContactTarget, c.Id, new(), "testlab");
            foreach (var t in p.Tags) TagService.Assign(ctx.Db, tags[t], TagService.ContactTarget, c.Id, new(), "testlab");
            if (p.Tier.Length > 0) TagService.Assign(ctx.Db, tiers[p.Tier], TagService.ContactTarget, c.Id, new(), "testlab");
            contacts[p.Name] = c;
        }
        Count("contacts", contacts.Count);

        // ---- knowledge ----------------------------------------------------------------------
        LabKit.Knowledge(ctx, "Delivery to Penang", "faq", "Do you deliver to Penang?",
            "Yes — Test lab Bakery delivers to Penang every Tuesday and Friday. Delivery is free for orders over RM 100.");
        LabKit.Knowledge(ctx, "Opening hours", "fact", "",
            "Test lab Bakery is open 9am–6pm Monday to Saturday and 10am–4pm on Sunday.");
        LabKit.Knowledge(ctx, "Returns and refunds", "policy", "Can I get a refund?",
            "Cakes can be exchanged within 24 hours with the receipt. Refunds are decided by a manager — the AI never promises one.");
        LabKit.Knowledge(ctx, "Custom cake orders", "faq", "How early should I order a custom cake?",
            "Order custom cakes at least 3 days ahead. Wedding cakes need 2 weeks.");
        var notice = LabKit.Knowledge(ctx, "Public holiday notice", "notice", "",
            "All Test lab outlets close early (2pm) on the coming public holiday.");
        notice.StartsAt = DateTime.UtcNow.AddDays(-1);
        notice.EndsAt = DateTime.UtcNow.AddDays(14);
        ctx.Db.SaveChanges();
        Count("knowledge_items", 5);

        // ---- the booking skill + rules + flows ------------------------------------------------
        await LabKit.Skills(ctx, calendar: true, restore: false);
        Count("skills");
        var billing = LabKit.Team(ctx, "Test lab · Billing", false, lab.HqAgent.Id);
        await DemoRule(ctx, "Demo · refunds go to Billing", true,
            new object[] { new { field = "message", op = "contains", value = "refund" } },
            new object[] { new { type = "add_label", value = "refund" }, new { type = "assign_team", value = billing.Id.ToString() } });
        await DemoRule(ctx, "Demo · VIPs get a note", false,
            new object[] { new { field = "contact_tags", op = "contains", value = "VIP" } },
            new object[] { new { type = "add_note", value = "VIP customer — reply within the hour." } });
        Count("automation_rules", 2);
        DemoFlow(ctx, "wf-testlab-demo-welcome", "Demo · Welcome series", new List<object?>
        {
            LabKit.Node("trg", "trigger", new { when = "Contact created", repeat = "Once" }),
            LabKit.Node("act", "action", new { channels = new[] { "WhatsApp" }, script = "Welcome to Test lab Bakery, {{first_name}}! Reply MENU to see today's cakes." }),
            LabKit.Node("wait", "wait", new { mode = "time", amount = 1, unit = "days" }),
            LabKit.Node("act2", "action", new { channels = new[] { "WhatsApp" }, script = "Hi {{first_name}}, your {{cf_testlab_membership}} perks start today." }),
            LabKit.Node("end", "end", new { logCode = "WELCOMED" }),
        }, new List<object?>
        {
            LabKit.Edge("e1", "trg", "act"), LabKit.Edge("e2", "act", "wait"), LabKit.Edge("e3", "wait", "act2"), LabKit.Edge("e4", "act2", "end"),
        });
        DemoFlow(ctx, "wf-testlab-demo-menu", "Demo · MENU keyword", new List<object?>
        {
            LabKit.Node("trg", "trigger", new { when = "Message received", keyword = "MENU", repeat = "Every time" }),
            LabKit.Node("act", "action", new { channels = new[] { "WhatsApp" }, script = "Today's cakes: chocolate, pandan, red velvet." }),
            LabKit.Node("end", "end", new { logCode = "MENU_SENT" }),
        }, new List<object?> { LabKit.Edge("e1", "trg", "act"), LabKit.Edge("e2", "act", "end") });
        DemoFlow(ctx, "wf-testlab-demo-birthday", "Demo · Birthday treat", new List<object?>
        {
            LabKit.Node("trg", "trigger", new { when = "Date field", field = "testlab_birthday", dateWhen = "on", days = "0", yearly = true, repeat = "Once a day" }),
            LabKit.Node("act", "action", new { channels = new[] { "WhatsApp" }, script = "Happy birthday {{first_name}}! A free slice of {{cf_testlab_favourite}} is waiting for you." }),
            LabKit.Node("end", "end", new { logCode = "BIRTHDAY" }),
        }, new List<object?> { LabKit.Edge("e1", "trg", "act"), LabKit.Edge("e2", "act", "end") });
        Count("flows", 3);

        // ---- conversations ------------------------------------------------------------------------
        var agent = ctx.As(lab.HqAgent);
        string Chat(string person, string[] lines, string[]? replies = null, ChannelAccount? on = null, double age = 0)
        {
            var c = contacts[person];
            var key = LabContext.WaKey(c);
            var inbox = on ?? (c.EntityLinks.Count > 0 ? lab.Conversation : lab.Conversation);
            if (replies != null) ctx.Brain.Say(replies);
            foreach (var l in lines) ctx.Say(inbox, key, l, c.Name);
            if (age > 0) Age(ctx, "whatsapp", key, age);
            Count("conversations");
            return key;
        }
        async Task Status(string key, string status) =>
            await ctx.Api(ctx.Super, "POST", "/api/messaging/threads/{channel}/{key}/status",
                new Dictionary<string, object> { ["status"] = status }, route: new() { ["channel"] = "whatsapp", ["key"] = key });

        var open = Chat("Aisha Rahman", new[] { "Hi! Do you deliver to Penang?" },
            new[] { "Yes — we deliver to Penang every Tuesday and Friday, and it's free over RM 100. Shall I note an order for you?" }, age: 2);
        links.Add(new LabLink("Open conversation (AI answered)", $"/chats?thread=whatsapp:{open}"));

        var byHand = Chat("Tan Wei Ming", new[] { "Can you do 40 boxes of egg tarts for Friday?" }, new[] { "Let me check with the kitchen for you." }, age: 5);
        await ctx.Api(agent, "POST", "/api/whatsapp/threads/{key}/send",
            new { text = "Hi Mr Tan, yes — 40 boxes for Friday 10am is confirmed. Invoice to follow." }, route: new() { ["key"] = byHand });

        var flagged = Chat("Nurul Huda", new[] { "Saya nak tukar pesanan untuk majlis perkahwinan." }, new[] { "Baik, saya semak dahulu ya." }, age: 3);
        await ctx.Api(agent, "POST", "/api/inbox/threads/{channel}/{key}/handover",
            new { action = "flag", reason = "Wedding order change — needs the manager." }, route: new() { ["channel"] = "whatsapp", ["key"] = flagged });
        links.Add(new LabLink("Open conversation (needs human)", $"/chats?thread=whatsapp:{flagged}"));

        var resolved = Chat("Priya Nair", new[] { "What time do you close today?", "Thanks!" },
            new[] { "We close at 6pm today.", "You're welcome — see you soon!" }, age: 30);
        await Status(resolved, "resolved");
        var snoozed = Chat("Kevin Ong", new[] { "I'll confirm the corporate order next week." }, new[] { "No problem — I'll check back next week." }, age: 20);
        await Status(snoozed, "snoozed");

        var stop = Chat("Daniel Wong", new[] { "Please stop sending promotions", "STOP" }, new[] { "Sorry about that — noted." }, age: 26);
        var refund = Chat("Melissa Teo", new[] { "The cake was damaged, I'd like a refund please." },
            new[] { "I'm sorry to hear that — a manager will review your refund request today." }, age: 1);
        links.Add(new LabLink("Open conversation (refund rule)", $"/chats?thread=whatsapp:{refund}"));

        // waiting on AI review (an inbox that reviews every reply)
        var reviewInbox = ctx.Inbox("review every reply", new() { ["ai_review_mode"] = "review_all" });
        var review = Chat("Lim Mei Ling", new[] { "Is the durian crepe cake available this weekend? How much is it?" },
            new[] { "Yes! The durian crepe cake is RM 128 and available Saturday and Sunday." }, reviewInbox, age: 0.5);
        links.Add(new LabLink("AI monitor (a reply waiting for review)", "/ai-monitor?tab=review"));

        // branch conversations
        Chat("Hafiz Rahman", new[] { "Assalamualaikum, cawangan utara buka pukul berapa?" }, new[] { "Waalaikumsalam! Kami buka pukul 9 pagi." }, lab.NorthLine, 8);
        Chat("Ravi Kumar", new[] { "Need 3 dozen cupcakes for the office on Monday." }, new[] { "Noted — 3 dozen cupcakes for Monday. Any flavour preference?" }, lab.NorthLine, 12);
        Chat("Kok Leong Chan", new[] { "你们周日营业吗？" }, new[] { "周日营业时间是上午10点到下午4点。" }, lab.SouthLine, 6);
        Chat("Suresh Pillai", new[] { "Can I pick up my order from the south outlet instead?" }, new[] { "Of course — it'll be ready at the south outlet from 11am." }, lab.SouthLine, 15);

        // business line: the redirect
        Chat("Arjun Menon", new[] { "Got your reminder — can I reschedule my tasting?" }, on: lab.Business, age: 4);

        // an unknown customer waiting for a person
        var askInbox = ctx.Inbox("unknown customers (ask)", new() { ["identify_unknown"] = "ask" });
        var stranger = ctx.NewPhone();
        ctx.Say(askInbox, stranger, "Good morning, is the pandan cake available?");
        ctx.Say(askInbox, stranger, "This is Farid Hassan");
        Age(ctx, "whatsapp", stranger, 1.5);
        Count("conversations");

        // bookings through the calendar skill
        ctx.Brain.BookingSkillName = LabKit.CalendarSkillName;
        foreach (var person in new[] { "Hannah Lee", "Farah Aziz", "Benjamin Koh" })
        {
            var key = Chat(person, new[] { "Hi, can I book a cake tasting this week?", "The first one please" });
            if (person == "Benjamin Koh")
            {
                var b = ctx.Db.Bookings.AsNoTracking().Where(x => x.ContactId == contacts[person].Id)
                    .OrderByDescending(x => x.Id).FirstOrDefault();
                if (b != null)
                    await ctx.Api(agent, "POST", "/api/bookings/{id:int}/cancel", route: new() { ["id"] = b.Id.ToString() });
            }
            _ = key;
        }
        ctx.Brain.BookingSkillName = null;
        created["bookings"] = ctx.Db.Bookings.Count(b => b.EntityId == lab.Hq.Id);
        links.Add(new LabLink("Bookings", "/bookings"));

        // Telegram + the website widget
        var tg = ctx.NewChatId();
        ctx.Say(lab.Telegram, tg, "Hi, I saw your cakes on Telegram — do you ship to Johor?", "Telegram visitor");
        Age(ctx, "telegram", tg, 7);
        Count("conversations");
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        var route = new Dictionary<string, string> { ["token"] = lab.Widget.ExternalId };
        var s = await ctx.Api(anon, "POST", "/api/widget/{token}/session",
            new { name = "Website visitor", email = ctx.NewEmail("webvisitor") }, route: route);
        var sid = LabContext.Str(s.Body, "session");
        if (sid.Length > 0)
        {
            await ctx.Api(anon, "POST", "/api/widget/{token}/messages",
                new { session = sid, text = "Hello! Do you have eggless cakes?" }, route: route);
            Count("conversations");
        }

        links.Insert(0, new LabLink("Chats in the Test lab", "/chats"));
        links.Add(new LabLink("Contacts", "/contacts"));
        links.Add(new LabLink("Journeys (paused demo journeys)", "/journeys"));
        _ = review;
        _ = stop;
        return new Outcome(false, created, links);
    }

    private static async Task DemoRule(LabContext ctx, string name, bool active, object[] conditions, object[] actions)
    {
        if (ctx.Db.AutomationRules.Any(r => r.EntityId == ctx.Lab.Hq.Id && r.Name == name)) return;
        await ctx.Api(ctx.Super, "POST", "/api/automations", new
        {
            name, @event = "message_created", entity = LabFixture.HqSlug, active, conditions, actions,
            description = "Test lab demo rule.",
        });
    }

    private static void DemoFlow(LabContext ctx, string flowId, string name, List<object?> nodes, List<object?> edges)
    {
        if (ctx.Db.Workflows.Any(w => w.EntityId == ctx.Lab.Hq.Id && w.FlowId == flowId)) return;
        ctx.Db.Workflows.Add(new Workflow
        {
            EntityId = ctx.Lab.Hq.Id, FlowId = flowId, Name = name, Hint = "Test lab demo flow (paused).",
            Nodes = nodes, Edges = edges, Active = false, UpdatedBy = "testlab",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.Db.SaveChanges();
    }
}
