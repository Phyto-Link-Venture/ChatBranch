using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The unified, server-filtered inbox list (GET /api/inbox/threads) and the
/// per-user saved views behind its tabs. Every test uses its own thread keys so
/// the class can share one database.
/// </summary>
public class InboxThreadsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public InboxThreadsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task Simulate(HttpClient c, string channel, string key, string text, string? name = null)
    {
        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel, key, text, name });
        res.EnsureSuccessStatusCode();
    }

    private static async Task<List<JsonElement>> List(HttpClient c, string query)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?limit=200&{query}");
        return body.GetProperty("threads").EnumerateArray().ToList();
    }

    private static async Task<int> Count(HttpClient c, string query)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?count_only=1&{query}");
        Assert.False(body.TryGetProperty("threads", out _)); // count only — no rows
        return body.GetProperty("count").GetInt32();
    }

    private static bool Has(List<JsonElement> rows, string channel, string key) =>
        rows.Any(r => r.GetProperty("channel").GetString() == channel && r.GetProperty("key").GetString() == key);

    private T WithDb<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _factory.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task A_chat_with_no_messages_stays_out_of_the_list_unless_asked_for_by_name()
    {
        var c = await Authed();
        // "open a chat" from a contact and walk away: the thread exists, nothing was said
        (await c.PostAsJsonAsync("/api/whatsapp/threads", new { phone = "+60177700991" })).EnsureSuccessStatusCode();
        Assert.False(Has(await List(c, "limit=200&q=60177700991"), "whatsapp", "60177700991"));
        Assert.Equal(1, await Count(c, "thread=whatsapp:60177700991"));   // the open chat's scope check still finds it

        await Simulate(c, "whatsapp", "60177700991", "hello?");
        Assert.True(Has(await List(c, "limit=200&q=60177700991"), "whatsapp", "60177700991"));
    }

    [Fact]
    public async Task Every_channel_lists_once_with_its_own_channel_and_status()
    {
        var c = await Authed();
        await Simulate(c, "whatsapp", "60111000001", "hi from whatsapp");
        await Simulate(c, "telegram", "tg-unique-1", "hi from telegram");

        var all = await List(c, "q=hi from");
        // the old list served the telegram chat through the whatsapp list too
        Assert.Single(all, r => r.GetProperty("key").GetString() == "tg-unique-1");
        Assert.True(Has(all, "telegram", "tg-unique-1"));
        Assert.True(Has(all, "whatsapp", "60111000001"));

        var tg = await List(c, "channel=telegram");
        Assert.All(tg, r => Assert.Equal("telegram", r.GetProperty("channel").GetString()));
        Assert.True(Has(tg, "telegram", "tg-unique-1"));

        // WhatsApp status is real and filterable
        var resolve = await c.PostAsJsonAsync("/api/whatsapp/threads/60111000001/status", new { status = "resolved" });
        resolve.EnsureSuccessStatusCode();
        var resolved = await List(c, "status=resolved");
        Assert.True(Has(resolved, "whatsapp", "60111000001"));
        Assert.False(Has(resolved, "telegram", "tg-unique-1"));
        var open = await List(c, "status=open,pending");
        Assert.False(Has(open, "whatsapp", "60111000001"));
        Assert.True(Has(open, "telegram", "tg-unique-1"));
        var row = resolved.First(r => r.GetProperty("key").GetString() == "60111000001");
        Assert.Equal("resolved", row.GetProperty("status").GetString());
        Assert.True(row.TryGetProperty("team_id", out _));
    }

    [Fact]
    public async Task Assignee_me_and_unassigned_and_labels_any_all()
    {
        var c = await Authed();
        await Simulate(c, "email", "mine@example.com", "please help");
        await Simulate(c, "email", "free@example.com", "please help too");
        (await c.PostAsJsonAsync("/api/messaging/threads/email/mine@example.com/assign",
            new { assignee = "admin" })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/messaging/threads/email/mine@example.com/labels",
            new { labels = new[] { "urgent", "vip" } })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/messaging/threads/email/free@example.com/labels",
            new { labels = new[] { "urgent" } })).EnsureSuccessStatusCode();

        var mine = await List(c, "assignee=me");
        Assert.True(Has(mine, "email", "mine@example.com"));
        Assert.False(Has(mine, "email", "free@example.com"));

        var unassigned = await List(c, "assignee=unassigned&channel=email");
        Assert.False(Has(unassigned, "email", "mine@example.com"));
        Assert.True(Has(unassigned, "email", "free@example.com"));

        var both = await List(c, "assignee=me,unassigned&channel=email");
        Assert.True(Has(both, "email", "mine@example.com"));
        Assert.True(Has(both, "email", "free@example.com"));

        var any = await List(c, "labels=urgent,vip&labels_mode=any");
        Assert.True(Has(any, "email", "mine@example.com"));
        Assert.True(Has(any, "email", "free@example.com"));
        var all = await List(c, "labels=urgent,vip&labels_mode=all");
        Assert.True(Has(all, "email", "mine@example.com"));
        Assert.False(Has(all, "email", "free@example.com"));
    }

    [Fact]
    public async Task Waiting_linked_and_search_over_message_text()
    {
        var c = await Authed();
        var contact = await c.PostAsJsonAsync("/api/contacts",
            new { name = "Linked Person", phone = "60122000333" });
        contact.EnsureSuccessStatusCode();
        await Simulate(c, "whatsapp", "60122000333", "where is my ZebraLasagna order");
        await Simulate(c, "whatsapp", "60122000444", "unknown sender here");

        // inbound last → waiting on us; replying clears it
        var waiting = await List(c, "waiting=1");
        Assert.True(Has(waiting, "whatsapp", "60122000333"));
        (await c.PostAsJsonAsync("/api/whatsapp/threads/whatsapp/60122000444/send",
            new { text = "we're on it" })).EnsureSuccessStatusCode();
        waiting = await List(c, "waiting=1");
        Assert.False(Has(waiting, "whatsapp", "60122000444"));

        // linked = matched to a CRM contact by phone
        var linked = await List(c, "linked=1&channel=whatsapp");
        Assert.True(Has(linked, "whatsapp", "60122000333"));
        Assert.False(Has(linked, "whatsapp", "60122000444"));
        var unlinked = await List(c, "linked=0&channel=whatsapp");
        Assert.True(Has(unlinked, "whatsapp", "60122000444"));

        // q reaches message text, case-insensitively, and contact names
        var byText = await List(c, "q=zebralasagna");
        Assert.True(Has(byText, "whatsapp", "60122000333"));
        Assert.False(Has(byText, "whatsapp", "60122000444"));
        var byName = await List(c, "q=linked%20person");
        Assert.True(Has(byName, "whatsapp", "60122000333"));
        // a literal % is not a wildcard
        Assert.Empty(await List(c, "q=%25%25%25"));
    }

    [Fact]
    public async Task Count_only_matches_the_list_and_paging_walks_it()
    {
        var c = await Authed();
        for (var i = 0; i < 5; i++) await Simulate(c, "telegram", $"page-{i}", $"pagetest message {i}");
        var n = await Count(c, "q=pagetest");
        Assert.Equal(5, n);

        var first = await c.GetFromJsonAsync<JsonElement>("/api/inbox/threads?q=pagetest&limit=2");
        Assert.Equal(2, first.GetProperty("threads").GetArrayLength());
        Assert.Equal(5, first.GetProperty("count").GetInt32());
        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var seen = first.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        while (cursor != null)
        {
            var page = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?q=pagetest&limit=2&cursor={cursor}");
            seen.AddRange(page.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("key").GetString()));
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("next_cursor").GetString();
        }
        Assert.Equal(5, seen.Distinct().Count());

        // a single thread by id
        Assert.Equal(1, await Count(c, "thread=telegram:page-3"));
    }

    /// <summary>
    /// sort=customer_newest orders by the customer's last inbound message (our own
    /// later replies don't lift a chat), chats they never wrote in go last, ties
    /// break by activity then id — and the offset cursor walks it page by page.
    /// </summary>
    [Fact]
    public async Task Customer_newest_sorts_by_the_last_inbound_message_and_pages_with_the_cursor()
    {
        var c = await Authed();
        var now = DateTime.UtcNow;
        WithDb(db =>
        {
            void Chat(string key, params (string Dir, int MinutesAgo)[] msgs)
            {
                var t = new Domain.Thread
                {
                    Channel = "telegram", Key = key, Name = key, AiEnabled = false,
                    CreatedAt = now.AddHours(-2), UpdatedAt = now.AddHours(-2),
                };
                db.Threads.Add(t);
                db.SaveChanges();
                foreach (var (dir, ago) in msgs)
                    db.Messages.Add(new Message
                    {
                        ThreadId = t.Id, Direction = dir, Source = dir == "in" ? "customer" : "agent",
                        Text = $"custsort {key}", Status = dir == "in" ? "received" : "sent",
                        CreatedAt = now.AddMinutes(-ago),
                    });
                db.SaveChanges();
            }
            Chat("cs-replied", ("in", 60), ("out", 1));   // newest ACTIVITY, an old customer message
            Chat("cs-fresh", ("in", 10));                 // the customer wrote most recently
            Chat("cs-ours", ("out", 0));                  // we started it, they never wrote
            Chat("cs-tie-old", ("in", 30));               // same customer time as cs-tie-new …
            Chat("cs-tie-new", ("in", 30), ("out", 2));   // … but newer activity
            return 0;
        });

        static List<string> Keys(List<JsonElement> rows) =>
            rows.Select(r => r.GetProperty("key").GetString()!).ToList();
        Assert.Equal(new[] { "cs-ours", "cs-replied", "cs-tie-new", "cs-fresh", "cs-tie-old" },
            Keys(await List(c, "q=custsort")));
        var expected = new[] { "cs-fresh", "cs-tie-new", "cs-tie-old", "cs-replied", "cs-ours" };
        Assert.Equal(expected, Keys(await List(c, "q=custsort&sort=customer_newest")));
        Assert.Equal(expected, Keys(await List(c, "q=custsort&sort=CUSTOMER_NEWEST"))); // case-insensitive

        // the offset cursor pages this order: no gaps, no repeats
        var walked = new List<string>();
        string? cursor = "";
        while (cursor != null)
        {
            var page = await c.GetFromJsonAsync<JsonElement>(
                $"/api/inbox/threads?q=custsort&sort=customer_newest&limit=2{(cursor.Length > 0 ? $"&cursor={cursor}" : "")}");
            Assert.Equal(5, page.GetProperty("count").GetInt32());
            walked.AddRange(page.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("key").GetString()!));
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("next_cursor").GetString();
        }
        Assert.Equal(expected, walked);

        // `before` is the newest sort's keyset cursor — ignored by this order
        var before = Uri.EscapeDataString(now.AddMinutes(-5).ToString("o"));
        Assert.Equal(expected, Keys(await List(c, $"q=custsort&sort=customer_newest&before={before}")));
        Assert.Equal(new[] { "cs-fresh", "cs-tie-old" }, Keys(await List(c, $"q=custsort&before={before}")));

        // filters still apply: the quick "Not in contacts" toggle + a channel
        Assert.Equal(expected, Keys(await List(c, "q=custsort&sort=customer_newest&linked=0&channel=telegram")));
        Assert.Empty(await List(c, "q=custsort&sort=customer_newest&linked=1"));

        // the bulk "everything this filter matches" path takes the same sort
        var bulk = await c.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { filter = new { q = "custsort", sort = "customer_newest", linked = "0" }, action = "assign", value = "admin" });
        bulk.EnsureSuccessStatusCode();
        Assert.Equal(5, (await bulk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matched").GetInt32());
        Assert.Equal(expected, Keys(await List(c, "q=custsort&sort=customer_newest&assignee=me")));
    }

    [Fact]
    public async Task Entity_scoping_and_agent_narrowing()
    {
        var admin = await Authed();
        await Simulate(admin, "telegram", "ent-a", "scoped a");
        await Simulate(admin, "telegram", "ent-b", "scoped b");
        await Simulate(admin, "telegram", "ent-none", "scoped none");

        var (a, b) = WithDb(db =>
        {
            var tenant = db.Tenants.First();
            var ea = new Entity { TenantId = tenant.Id, Name = "Branch A", Slug = "inbox-branch-a",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var eb = new Entity { TenantId = tenant.Id, Name = "Branch B", Slug = "inbox-branch-b",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Entities.AddRange(ea, eb);
            db.SaveChanges();
            db.Threads.First(t => t.Key == "ent-a").EntityId = ea.Id;
            db.Threads.First(t => t.Key == "ent-b").EntityId = eb.Id;
            db.SaveChanges();
            return (ea.Id, eb.Id);
        });

        // admin: an entity narrows to it (+ the unassigned bucket); none = all
        var onlyA = await List(admin, "entity=inbox-branch-a&q=scoped");
        Assert.True(Has(onlyA, "telegram", "ent-a"));
        Assert.True(Has(onlyA, "telegram", "ent-none"));
        Assert.False(Has(onlyA, "telegram", "ent-b"));
        var both = await List(admin, "entity=inbox-branch-a,inbox-branch-b&q=scoped");
        Assert.True(Has(both, "telegram", "ent-a") && Has(both, "telegram", "ent-b"));
        Assert.Equal(3, (await List(admin, "q=scoped")).Count);

        // agent assigned to A: never sees B, even when asking for it
        WithDb(db =>
        {
            var profile = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            profile.Entities.Add(db.Entities.First(e => e.Id == a));
            db.SaveChanges();
            return 0;
        });
        var agent = await Authed("agent", "agent123");
        var mine = await List(agent, "q=scoped");
        Assert.True(Has(mine, "telegram", "ent-a"));
        Assert.True(Has(mine, "telegram", "ent-none"));
        Assert.False(Has(mine, "telegram", "ent-b"));
        var askB = await List(agent, "entity=inbox-branch-b&q=scoped");
        Assert.False(Has(askB, "telegram", "ent-b"));
        Assert.True(Has(askB, "telegram", "ent-a")); // narrowed to their own scope
        Assert.Equal(0, await Count(agent, "thread=telegram:ent-b"));
        _ = b;

        WithDb(db =>
        {
            var profile = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            profile.Entities.Clear();
            db.SaveChanges();
            return 0;
        });
    }

    [Fact]
    public async Task Saved_views_crud_is_per_user()
    {
        var admin = await Authed();
        var agent = await Authed("agent", "agent123");

        var create = await admin.PostAsJsonAsync("/api/views",
            new { scope = "chats", name = "VIP drivers", filters = new { status = new[] { "open" }, waiting = true } });
        create.EnsureSuccessStatusCode();
        var view = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = view.GetProperty("id").GetInt32();
        Assert.True(view.GetProperty("filters").GetProperty("waiting").GetBoolean());

        var second = await admin.PostAsJsonAsync("/api/views", new { scope = "chats", name = "Second", filters = new { } });
        second.EnsureSuccessStatusCode();
        var id2 = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // duplicate name → 409; bad filters → 400
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/views", new { scope = "chats", name = "vip DRIVERS" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/views", new { scope = "chats", name = "x", filters = new[] { 1 } })).StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/views?scope=chats");
        Assert.Equal(new[] { id, id2 }, list.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetInt32()).ToArray());

        // somebody else's view is invisible and untouchable
        var theirs = await agent.GetFromJsonAsync<JsonElement>("/api/views?scope=chats");
        Assert.DoesNotContain(theirs.GetProperty("views").EnumerateArray(), v => v.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PatchAsJsonAsync($"/api/views/{id}", new { name = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.DeleteAsync($"/api/views/{id}")).StatusCode);

        // rename + reorder + delete by the owner
        var rename = await admin.PatchAsJsonAsync($"/api/views/{id}", new { name = "VIPs" });
        rename.EnsureSuccessStatusCode();
        Assert.Equal("VIPs", (await rename.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        var reorder = await admin.PostAsJsonAsync("/api/views/reorder", new { scope = "chats", ids = new[] { id2, id } });
        reorder.EnsureSuccessStatusCode();
        list = await admin.GetFromJsonAsync<JsonElement>("/api/views?scope=chats");
        Assert.Equal(new[] { id2, id }, list.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetInt32()).ToArray());
        (await admin.DeleteAsync($"/api/views/{id}")).EnsureSuccessStatusCode();
        list = await admin.GetFromJsonAsync<JsonElement>("/api/views?scope=chats");
        Assert.DoesNotContain(list.GetProperty("views").EnumerateArray(), v => v.GetProperty("id").GetInt32() == id);

        // every mutation is audited
        var audited = WithDb(db => db.AuditLogs.Count(l => l.Action.StartsWith("view.")));
        Assert.True(audited >= 5);
    }
}
