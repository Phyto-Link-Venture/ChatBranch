using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The shell-wide surfaces: top-bar search (/api/search), the Live chip
/// (/api/live) and the account-health audit trail Analytics reads. The point of
/// every test here is SCOPE — a palette or a live feed must never show a row the
/// corresponding screen would hide.
///
/// Fixture: two sibling entities (north, south). The demo agent is assigned to
/// NORTH only; each entity owns one contact and one conversation.
/// </summary>
public class ShellSearchLiveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShellSearchLiveTests(ApiFactory factory)
    {
        _factory = factory;
        _ = _factory.CreateClient(); // boot + seed
        Arrange();
    }

    private static readonly object Gate = new();

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Entities.Any(e => e.Slug == "north")) return;

            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var north = new Entity { TenantId = tenant.Id, Name = "North Branch", Slug = "north", CreatedAt = now, UpdatedAt = now };
            var south = new Entity { TenantId = tenant.Id, Name = "South Branch", Slug = "south", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(north, south);
            db.SaveChanges();

            var agent = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            agent.Entities.Add(north);

            var cNorth = new Contact { Name = "Zelda Northwind", Phone = "+60111000001", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            var cSouth = new Contact { Name = "Zora Southgate", Phone = "+60111000002", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.AddRange(cNorth, cSouth);
            db.SaveChanges();
            db.ContactEntities.AddRange(
                new ContactEntity { ContactId = cNorth.Id, EntityId = north.Id, CreatedAt = now },
                new ContactEntity { ContactId = cSouth.Id, EntityId = south.Id, CreatedAt = now });

            var tNorth = new Thread { Channel = "whatsapp", Key = "60111000001", Name = "Zelda Northwind", ContactId = cNorth.Id, EntityId = north.Id, CreatedAt = now, UpdatedAt = now };
            var tSouth = new Thread { Channel = "whatsapp", Key = "60111000002", Name = "Zora Southgate", ContactId = cSouth.Id, EntityId = south.Id, CreatedAt = now, UpdatedAt = now,
                CallStatus = "in_call", CallId = "wacid.sandbox" };
            db.Threads.AddRange(tNorth, tSouth);
            db.SaveChanges();
            db.Messages.AddRange(
                new Message { ThreadId = tNorth.Id, Direction = "in", Source = "customer", Text = "Is the pumpkin soup vegan?", CreatedAt = now.AddMinutes(-20) },
                new Message { ThreadId = tSouth.Id, Direction = "in", Source = "customer", Text = "Need the pumpkin invoice please", CreatedAt = now.AddMinutes(-40) },
                new Message { ThreadId = tSouth.Id, Direction = "out", Source = "agent", Text = "On it", CreatedAt = now.AddMinutes(-35) },
                new Message { ThreadId = tSouth.Id, Direction = "in", Source = "customer", Text = "Thanks — and the receipt?", CreatedAt = now.AddMinutes(-30) });
            db.SaveChanges();
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

    private static List<JsonElement> Results(JsonElement body, string type) =>
        body.GetProperty("results").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == type).ToList();

    // ---- /api/search -----------------------------------------------------------

    [Fact]
    public async Task Search_requires_auth_and_two_characters()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/search?q=zelda")).StatusCode);

        var admin = await Authed("admin", "admin123");
        var tooShort = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=z");
        Assert.Empty(tooShort.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Admin_finds_contacts_and_conversations_in_every_entity()
    {
        var admin = await Authed("admin", "admin123");
        var body = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=zor");
        var contact = Assert.Single(Results(body, "contact"));
        Assert.Equal("Zora Southgate", contact.GetProperty("title").GetString());
        Assert.StartsWith("/contacts/", contact.GetProperty("url").GetString());

        var convo = Assert.Single(Results(body, "conversation"));
        Assert.Equal("/chats?channel=whatsapp&key=60111000002", convo.GetProperty("url").GetString());
        Assert.Equal("whatsapp", convo.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Conversations_match_message_text_case_insensitively()
    {
        var admin = await Authed("admin", "admin123");
        var body = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=PUMPKIN&types=conversations");
        var hits = Results(body, "conversation");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Contains("pumpkin", h.GetProperty("subtitle").GetString()!,
            StringComparison.OrdinalIgnoreCase));
        // types= limits the answer to the asked-for groups
        Assert.Empty(Results(body, "contact"));
    }

    [Fact]
    public async Task Agent_cannot_find_another_entitys_contact_or_conversation()
    {
        var agent = await Authed("agent", "agent123");

        var own = await agent.GetFromJsonAsync<JsonElement>("/api/search?q=zelda");
        Assert.Single(Results(own, "contact"));
        Assert.Single(Results(own, "conversation"));

        var other = await agent.GetFromJsonAsync<JsonElement>("/api/search?q=zora");
        Assert.Empty(Results(other, "contact"));
        Assert.Empty(Results(other, "conversation"));

        // message text in the other entity's thread stays hidden too
        var text = await agent.GetFromJsonAsync<JsonElement>("/api/search?q=pumpkin");
        var convo = Assert.Single(Results(text, "conversation"));
        Assert.Contains("60111000001", convo.GetProperty("url").GetString());

        // asking for the other outlet narrows, never widens
        var widened = await agent.GetFromJsonAsync<JsonElement>("/api/search?q=zora&entity=south");
        Assert.Empty(Results(widened, "contact"));
        Assert.Empty(Results(widened, "conversation"));
    }

    [Fact]
    public async Task Users_and_entities_are_capability_gated()
    {
        var agent = await Authed("agent", "agent123");
        var asAgent = await agent.GetFromJsonAsync<JsonElement>("/api/search?q=admin");
        Assert.Empty(Results(asAgent, "user"));            // no users.manage

        var admin = await Authed("admin", "admin123");
        var asAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=agent");
        Assert.Contains(Results(asAdmin, "user"), u => u.GetProperty("title").GetString() == "agent");

        var ents = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=branch&types=entities");
        Assert.Equal(2, Results(ents, "entity").Count);
    }

    // ---- /api/live -------------------------------------------------------------

    [Fact]
    public async Task Live_reports_calls_and_waiting_for_admin()
    {
        var admin = await Authed("admin", "admin123");
        var live = await admin.GetFromJsonAsync<JsonElement>("/api/live");
        Assert.True(live.GetProperty("active_call_count").GetInt32() >= 1);
        Assert.Contains(live.GetProperty("active_calls").EnumerateArray(),
            c => c.GetProperty("key").GetString() == "60111000002");

        var waiting = live.GetProperty("waiting").EnumerateArray().ToList();
        Assert.True(live.GetProperty("waiting_count").GetInt32() >= 2);
        var north = waiting.First(w => w.GetProperty("key").GetString() == "60111000001");
        var south = waiting.First(w => w.GetProperty("key").GetString() == "60111000002");
        // south's wait restarted at the inbound AFTER our reply (30 min ago, not
        // its first message 40 min ago); north has waited 20 min.
        Assert.True(south.GetProperty("waiting_seconds").GetInt64() > north.GetProperty("waiting_seconds").GetInt64());
        Assert.True(south.GetProperty("waiting_seconds").GetInt64() < 35 * 60);
        // oldest wait first
        Assert.True(waiting.IndexOf(south) < waiting.IndexOf(north));
        Assert.NotEmpty(live.GetProperty("recent").EnumerateArray());
    }

    [Fact]
    public async Task Live_is_entity_scoped_for_agents()
    {
        var agent = await Authed("agent", "agent123");
        var live = await agent.GetFromJsonAsync<JsonElement>("/api/live");
        Assert.DoesNotContain(live.GetProperty("active_calls").EnumerateArray(),
            c => c.GetProperty("key").GetString() == "60111000002");
        Assert.DoesNotContain(live.GetProperty("waiting").EnumerateArray(),
            w => w.GetProperty("key").GetString() == "60111000002");
        Assert.Contains(live.GetProperty("waiting").EnumerateArray(),
            w => w.GetProperty("key").GetString() == "60111000001");
        // the feed carries no message from the other entity, and no audit rows
        foreach (var r in live.GetProperty("recent").EnumerateArray())
        {
            Assert.Equal("message", r.GetProperty("kind").GetString());
            Assert.DoesNotContain("60111000002", r.GetProperty("url").GetString());
        }

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/live")).StatusCode);
    }

    [Fact]
    public async Task Live_entity_filter_narrows_for_admin()
    {
        var admin = await Authed("admin", "admin123");
        var live = await admin.GetFromJsonAsync<JsonElement>("/api/live?entity=north");
        Assert.DoesNotContain(live.GetProperty("waiting").EnumerateArray(),
            w => w.GetProperty("key").GetString() == "60111000002");
        Assert.Equal(0, live.GetProperty("active_call_count").GetInt32());
    }

    // ---- account health audit --------------------------------------------------

    [Fact]
    public void Health_writer_audits_once_per_real_transition()
    {
        using var db = new TestDb();
        var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = "Sales line",
            HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
        db.Db.ChannelAccounts.Add(acct);
        db.Db.SaveChanges();

        Assert.True(AccountHealth.SetStatus(db.Db, acct, Health.Degraded, "disconnected: timeout"));
        Assert.False(AccountHealth.SetStatus(db.Db, acct, Health.Degraded, "disconnected: timeout"));
        Assert.True(AccountHealth.SetStatus(db.Db, acct, Health.Healthy, "connected"));
        db.Db.SaveChanges();

        var rows = db.Db.AuditLogs.Where(a => a.Action == "account.health").OrderBy(a => a.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Sales line", rows[0].Target);
        Assert.Equal("healthy → degraded — disconnected: timeout", rows[0].Detail);
        Assert.StartsWith("degraded → healthy", rows[1].Detail);
        Assert.NotNull(acct.LastOkAt);
    }

    [Fact]
    public async Task Baileys_status_event_lands_in_analytics_health_events()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.ChannelAccounts.Any(a => a.ExternalId == "gw-health-test"))
            {
                db.ChannelAccounts.Add(new ChannelAccount { Channel = "whatsapp", Provider = "baileys",
                    Name = "Health probe", ExternalId = "gw-health-test", HealthStatus = Health.Healthy,
                    CreatedAt = DateTime.UtcNow });
                db.SaveChanges();
            }
        }
        var gw = _factory.CreateClient();
        gw.DefaultRequestHeaders.Add("X-Baileys-Secret", "baileys-local-dev");
        var push = await gw.PostAsJsonAsync("/api/whatsapp/baileys/status-event",
            new { account_id = "gw-health-test", @event = "disconnected", reason = "timeout" });
        push.EnsureSuccessStatusCode();

        var admin = await Authed("admin", "admin123");
        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/summary?days=7");
        Assert.Contains(summary.GetProperty("health_events").EnumerateArray(),
            e => e.GetProperty("target").GetString() == "Health probe"
                 && e.GetProperty("detail").GetString()!.StartsWith("healthy → degraded"));
        // the KPI strip's new fields ride on the same payload
        Assert.True(summary.TryGetProperty("first_response", out _));
        Assert.True(summary.GetProperty("prior").TryGetProperty("sent", out _));
    }

    [Fact]
    public async Task Baileys_qr_expired_marks_an_unpaired_number_disconnected()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.ChannelAccounts.Any(a => a.ExternalId == "gw-qr-test"))
            {
                db.ChannelAccounts.Add(new ChannelAccount { Channel = "whatsapp", Provider = "baileys",
                    Name = "Unpaired line", ExternalId = "gw-qr-test", CreatedAt = DateTime.UtcNow });
                db.SaveChanges();
            }
        }
        var gw = _factory.CreateClient();
        gw.DefaultRequestHeaders.Add("X-Baileys-Secret", "baileys-local-dev");
        var push = await gw.PostAsJsonAsync("/api/whatsapp/baileys/status-event",
            new { account_id = "gw-qr-test", @event = "qr_expired", reason = "not paired" });
        push.EnsureSuccessStatusCode();

        using var check = _factory.Services.CreateScope();
        var acct = check.ServiceProvider.GetRequiredService<AppDbContext>().ChannelAccounts
            .Single(a => a.ExternalId == "gw-qr-test");
        Assert.Equal(Health.Disconnected, acct.HealthStatus);
        Assert.Contains("not paired", acct.HealthDetail);
    }
}
