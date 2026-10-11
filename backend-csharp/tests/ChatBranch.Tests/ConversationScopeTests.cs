using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The inbox's scope holds on every per-conversation route, not just the list:
/// an agent assigned to EAST can't open, answer, note, label, resolve or link a
/// WEST conversation (404 — as if it didn't exist), can't link any conversation
/// to a WEST contact, and the contact lists / combined conversation / timeline
/// the chat and contact pages read are bounded the same way.
/// </summary>
public class ConversationScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly object Gate = new();
    private static int _wendyId, _eddieId;

    public ConversationScopeTests(ApiFactory factory)
    {
        _factory = factory;
        _ = _factory.CreateClient(); // boot + seed
        Arrange();
    }

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Entities.Any(e => e.Slug == "cs-east"))
            {
                _wendyId = db.Contacts.First(c => c.Name == "Wendy CS").Id;
                _eddieId = db.Contacts.First(c => c.Name == "Eddie CS").Id;
                return;
            }
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var east = new Entity { TenantId = tenant.Id, Name = "CS East", Slug = "cs-east", CreatedAt = now, UpdatedAt = now };
            var west = new Entity { TenantId = tenant.Id, Name = "CS West", Slug = "cs-west", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(east, west);
            db.SaveChanges();
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities.Add(east);

            var wendy = new Contact { Name = "Wendy CS", Phone = "+60111000222", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            var eddie = new Contact { Name = "Eddie CS", Phone = "+60111000333", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            var vera = new Contact { Name = "Vera CS", Phone = "+60111000444", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.AddRange(wendy, eddie, vera);
            db.SaveChanges();
            db.ContactEntities.AddRange(
                new ContactEntity { ContactId = wendy.Id, EntityId = west.Id, CreatedAt = now },
                new ContactEntity { ContactId = eddie.Id, EntityId = east.Id, CreatedAt = now },
                new ContactEntity { ContactId = vera.Id, EntityId = west.Id, CreatedAt = now });

            Thread T(string channel, string key, int? entity, int? contact = null) => new()
            {
                Channel = channel, Key = key, Name = key, EntityId = entity, ContactId = contact,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Threads.AddRange(
                T("telegram", "cs-west", west.Id, wendy.Id),
                T("telegram", "cs-east", east.Id),
                T("telegram", "cs-nobody", null),
                T("whatsapp", "60111000222", west.Id, wendy.Id),
                // West's conversations the start-a-chat test reaches for from East
                T("telegram", "tg-start-w", west.Id),
                T("whatsapp", "60111000444", west.Id, vera.Id));
            db.SaveChanges();
            // (EnsureCreated + seed ran before these rows: they carry their company like upgraded rows do)
            ChatBranch.Data.Stores.ThreadCompanies.Backfill(db);
            foreach (var t in db.Threads.Where(t => t.Key.StartsWith("cs-") || t.Key == "tg-start-w" || t.Key == "60111000222" || t.Key == "60111000444").ToList())
                db.Messages.Add(new Message
                {
                    ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                    Text = $"hello from {t.Key}", Status = "received", CreatedAt = now,
                });
            db.SaveChanges();
            _wendyId = wendy.Id;
            _eddieId = eddie.Id;
        }
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private Thread ThreadOf(string channel, string key)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Threads.AsNoTracking()
            .Single(t => t.Channel == channel && t.Key == key);
    }

    private List<Thread> ThreadsOf(string channel, string key)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Threads.AsNoTracking()
            .Where(t => t.Channel == channel && t.Key == key).OrderBy(t => t.Id).ToList();
    }

    private int EntityIdOf(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Entities.Single(e => e.Slug == slug).Id;
    }

    private int NoteCount(string channel, string key)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Messages
            .Count(m => m.Thread!.Channel == channel && m.Thread.Key == key && m.Private);
    }

    [Fact]
    public async Task Another_outlets_conversation_is_not_found_for_reading_or_acting()
    {
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/messaging/threads/telegram/cs-west")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/whatsapp/threads/telegram/cs-west/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/messaging/threads/telegram/cs-west/summary")).StatusCode);
        // WhatsApp keys are matched the way the handlers normalize them
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/whatsapp/threads/60111000222")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/whatsapp/threads/%2B60111000222")).StatusCode);

        var before = ThreadOf("telegram", "cs-west");
        foreach (var (path, body) in new (string, object)[]
        {
            ("note", new { text = "peek" }),
            ("status", new { status = "resolved" }),
            ("labels", new { labels = new[] { "vip" } }),
            ("assign", new { assignee = "agent" }),
            ("send", new { text = "hi" }),
            ("ai", new { enabled = false }),
            ("link", new { contact_id = _eddieId }),
        })
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.PostAsJsonAsync($"/api/messaging/threads/telegram/cs-west/{path}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PostAsJsonAsync("/api/whatsapp/threads/60111000222/send", new { text = "hi" })).StatusCode);
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "x.jpg" } })
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.PostAsync("/api/messaging/threads/telegram/cs-west/send-media", form)).StatusCode);
        var after = ThreadOf("telegram", "cs-west");
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ContactId, after.ContactId);
        Assert.Empty(after.Labels);
        Assert.Equal(0, NoteCount("telegram", "cs-west"));

        // their own outlet and unassigned conversations stay fully usable
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/messaging/threads/telegram/cs-east")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/messaging/threads/telegram/cs-nobody")).StatusCode);
        (await agent.PostAsJsonAsync("/api/messaging/threads/telegram/cs-east/note", new { text = "mine" })).EnsureSuccessStatusCode();

        // an admin manages every outlet
        var admin = await Authed("admin", "admin123");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/messaging/threads/telegram/cs-west")).StatusCode);
    }

    [Fact]
    public async Task A_conversation_can_only_be_linked_to_a_contact_the_caller_can_see()
    {
        var agent = await Authed("agent", "agent123");
        var refused = await agent.PostAsJsonAsync("/api/messaging/threads/telegram/cs-nobody/link", new { contact_id = _wendyId });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Null(ThreadOf("telegram", "cs-nobody").ContactId);

        (await agent.PostAsJsonAsync("/api/messaging/threads/telegram/cs-nobody/link", new { contact_id = _eddieId }))
            .EnsureSuccessStatusCode();
        Assert.Equal(_eddieId, ThreadOf("telegram", "cs-nobody").ContactId);
        (await agent.PostAsJsonAsync("/api/messaging/threads/telegram/cs-nobody/link", new { contact_id = (int?)null }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Drafting_or_starting_a_conversation_respects_the_same_scope()
    {
        var agent = await Authed("agent", "agent123");
        // an AI draft names its conversation in the body — still scoped
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PostAsJsonAsync("/api/aicore/reply", new { channel = "telegram", key = "cs-west" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await agent.PostAsJsonAsync("/api/aicore/reply", new { channel = "telegram", key = "cs-east" })).StatusCode);

        // "start a chat" with a customer ANOTHER company talks to opens the caller's OWN
        // conversation (one per customer per company): the other company's is neither
        // opened nor moved, and nothing of it — contact, number, messages — comes across
        var east = EntityIdOf("cs-east");
        var west = EntityIdOf("cs-west");
        var westTg = ThreadOf("telegram", "tg-start-w");
        var westWa = ThreadOf("whatsapp", "60111000444");
        var startedTg = await agent.PostAsJsonAsync("/api/messaging/threads", new { channel = "telegram", to = "tg-start-w" });
        Assert.Equal(HttpStatusCode.OK, startedTg.StatusCode);
        var startedWa = await agent.PostAsJsonAsync("/api/whatsapp/threads", new { phone = "+60111000444" });
        Assert.Equal(HttpStatusCode.OK, startedWa.StatusCode);
        foreach (var (channel, address, theirs) in new[] { ("telegram", "tg-start-w", westTg), ("whatsapp", "60111000444", westWa) })
        {
            var both = ThreadsOf(channel, address);
            Assert.Equal(2, both.Count);
            var stillTheirs = both.Single(t => t.Id == theirs.Id);
            Assert.Equal(west, stillTheirs.EntityId);
            Assert.Equal(west, stillTheirs.CompanyEntityId);
            Assert.Equal(theirs.AccountId, stillTheirs.AccountId);
            Assert.Equal(theirs.ContactId, stillTheirs.ContactId);
            var mine = both.Single(t => t.Id != theirs.Id);
            Assert.Equal(east, mine.EntityId);
            Assert.Equal(east, mine.CompanyEntityId);
            Assert.Null(mine.ContactId);          // Vera is West's contact: never linked from East
            using var scope = _factory.Services.CreateScope();
            Assert.Equal(0, scope.ServiceProvider.GetRequiredService<AppDbContext>().Messages.Count(m => m.ThreadId == mine.Id));
        }
        // …and what East reads under that key is its own, empty conversation
        var eastView = await agent.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60111000444");
        Assert.DoesNotContain("hello from", eastView.GetRawText());
        Assert.DoesNotContain("Vera CS", eastView.GetRawText());

        // a new chat never links a contact from another outlet's book
        (await agent.PostAsJsonAsync("/api/messaging/threads", new { channel = "sms", to = "+60111000222" }))
            .EnsureSuccessStatusCode();
        Assert.Null(ThreadOf("sms", "60111000222").ContactId);

        // starting a conversation is a send capability (a key scoped without it)
        var key = Platform.MintKey(_factory, "admin", "chats.view", "contacts.manage");
        var viewer = Platform.Client(_factory, key);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/messaging/threads", new { channel = "telegram", to = "cs-new" })).StatusCode);
    }

    [Fact]
    public async Task A_scoped_key_holds_only_its_scopes_inside_handlers_too()
    {
        // admin's ROLE may manage entities, but this key was scoped to contacts only:
        // the in-handler check (not just the route filter) must say no
        var key = Platform.Client(_factory, Platform.MintKey(_factory, "admin", "contacts.view", "contacts.manage"));
        var refused = await key.PostAsJsonAsync("/api/v1/entities", new { name = "Key Made", slug = "cs-key-made" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var scope = _factory.Services.CreateScope())
            Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().Entities.Any(e => e.Slug == "cs-key-made"));
        // … and what it was scoped to still works
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/v1/contacts")).StatusCode);

        // an unscoped key acts as its owner
        var full = Platform.Client(_factory, Platform.MintKey(_factory, "admin"));
        var made = await full.PostAsJsonAsync("/api/v1/entities", new { name = "Key Made", slug = "cs-key-made" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Contact_lists_conversations_and_timelines_are_bounded_by_scope()
    {
        var agent = await Authed("agent", "agent123");
        var picker = (await agent.GetFromJsonAsync<JsonElement>("/api/messaging/contacts")).GetRawText();
        Assert.Contains("Eddie CS", picker);
        Assert.DoesNotContain("Wendy CS", picker);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/messaging/contacts/{_wendyId}/conversation")).StatusCode);
        // a phone only another outlet's contact has: none of theirs comes back — no messages, no
        // contact, no summary (the same answer as for a number nobody knows)
        var timeline = await agent.GetAsync("/api/messaging/timeline?phone=%2B60111000222");
        if (timeline.StatusCode != HttpStatusCode.NotFound)
        {
            Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
            var tl = await timeline.Content.ReadFromJsonAsync<JsonElement>();
            Assert.DoesNotContain("hello from", tl.GetRawText());
            Assert.Equal(JsonValueKind.Null, tl.GetProperty("summary").ValueKind);
        }
        var summarized = await agent.PostAsJsonAsync("/api/messaging/timeline/summary", new { name = "Wendy", phone = "+60111000222" });
        if (summarized.StatusCode != HttpStatusCode.NotFound)
        {
            Assert.Equal(HttpStatusCode.OK, summarized.StatusCode);
            var sum = await summarized.Content.ReadFromJsonAsync<JsonElement>();
            Assert.DoesNotContain("hello from", sum.GetRawText());
            Assert.Equal(0, sum.GetProperty("event_count").GetInt32());
        }
        // exporting the address book is a manager's job
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/messaging/contacts/vcard")).StatusCode);

        var list = (await agent.GetFromJsonAsync<JsonElement>("/api/messaging/threads")).GetRawText();
        Assert.Contains("cs-east", list);
        Assert.Contains("cs-nobody", list);
        Assert.DoesNotContain("cs-west", list);

        var admin = await Authed("admin", "admin123");
        Assert.Contains("Wendy CS", (await admin.GetFromJsonAsync<JsonElement>("/api/messaging/contacts")).GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/messaging/contacts/{_wendyId}/conversation")).StatusCode);
        Assert.Contains("cs-west", (await admin.GetFromJsonAsync<JsonElement>("/api/messaging/threads")).GetRawText());
    }
}
