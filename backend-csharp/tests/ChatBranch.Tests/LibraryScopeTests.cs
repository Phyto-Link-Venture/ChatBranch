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
/// RBAC on the libraries an outlet inherits (skills, knowledge, quick replies,
/// call scripts, integrations): an agent assigned to EAST reads the org-wide
/// rows plus East's, never West's — and an out-of-scope <c>?entity=</c> narrows
/// rather than widens. Integrations honour integrations.view / .manage, and a
/// person's own connection stays theirs.
/// </summary>
public class LibraryScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly object Gate = new();
    private static int _westContactId;

    public LibraryScopeTests(ApiFactory factory)
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
            if (db.Entities.Any(e => e.Slug == "east"))
            {
                _westContactId = db.Contacts.First(c => c.Name == "Wendy West").Id;
                return;
            }
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var east = new Entity { TenantId = tenant.Id, Name = "East", Slug = "east", CreatedAt = now, UpdatedAt = now };
            var west = new Entity { TenantId = tenant.Id, Name = "West", Slug = "west", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(east, west);
            db.SaveChanges();
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities.Add(east);

            db.AgentSkills.AddRange(
                new AgentSkill { SkillId = "sk-global", Name = "Global skill", UseWhen = "always", Instructions = "x" },
                new AgentSkill { SkillId = "sk-east", Name = "East skill", UseWhen = "always", Instructions = "x", EntityId = east.Id },
                new AgentSkill { SkillId = "sk-west", Name = "West skill", UseWhen = "always", Instructions = "x", EntityId = west.Id });
            db.KnowledgeItems.AddRange(
                new KnowledgeItem { Title = "Global fact", Content = "g", CreatedAt = now, UpdatedAt = now },
                new KnowledgeItem { Title = "West secret pricing", Content = "w", EntityId = west.Id, CreatedAt = now, UpdatedAt = now });
            db.CannedResponses.AddRange(
                new CannedResponse { ShortCode = "hello", Content = "Hi {{name}}", CreatedAt = now },
                new CannedResponse { ShortCode = "westonly", Content = "West only", EntityId = west.Id, CreatedAt = now });
            var wendy = new Contact { Name = "Wendy West", Phone = "+60111222333", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(wendy);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = wendy.Id, EntityId = west.Id, CreatedAt = now });

            var adminId = db.Users.First(u => u.Username == "admin").Id;
            var agentId = db.Users.First(u => u.Username == "agent").Id;
            db.Integrations.AddRange(
                new Integration { Provider = "stripe", Status = "connected", Config = new() { ["name"] = "Shared Stripe" }, CreatedAt = now, UpdatedAt = now },
                new Integration { Provider = "google_calendar", OwnerUserId = adminId, Status = "connected", Config = new() { ["name"] = "Admin calendar" }, CreatedAt = now, UpdatedAt = now },
                new Integration { Provider = "google_calendar", OwnerUserId = agentId, Status = "connected", Config = new() { ["name"] = "Agent calendar" }, CreatedAt = now, UpdatedAt = now });
            db.SaveChanges();
            _westContactId = wendy.Id;
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

    private static List<string> Names(JsonElement list, string field) =>
        list.EnumerateArray().Select(x => x.GetProperty(field).GetString() ?? "").ToList();

    [Fact]
    public async Task An_assigned_agent_reads_org_wide_plus_their_outlet_never_another_outlets_library()
    {
        var agent = await Authed("agent", "agent123");
        foreach (var url in new[] { "/api/aicore/skills", "/api/aicore/skills?entity=west" })
        {
            var skills = Names((await agent.GetFromJsonAsync<JsonElement>(url)).GetProperty("skills"), "name");
            Assert.Contains("Global skill", skills);
            Assert.Contains("East skill", skills);
            Assert.DoesNotContain("West skill", skills); // ?entity=west narrows, never widens
        }
        var kb = await agent.GetFromJsonAsync<JsonElement>("/api/aicore/knowledge?entity=west");
        Assert.DoesNotContain("West secret pricing", kb.GetRawText());
        Assert.Contains("Global fact", kb.GetRawText());
        var canned = Names((await agent.GetFromJsonAsync<JsonElement>("/api/messaging/canned")).GetProperty("canned"), "short_code");
        Assert.Contains("hello", canned);
        Assert.DoesNotContain("westonly", canned);

        // the admin's management view still sees every outlet's rows
        var admin = await Authed("admin", "admin123");
        var all = Names((await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills")).GetProperty("skills"), "name");
        Assert.Contains("West skill", all);
    }

    [Fact]
    public async Task Quick_replies_never_render_another_outlets_contact()
    {
        var agent = await Authed("agent", "agent123");
        var rows = (await agent.GetFromJsonAsync<JsonElement>($"/api/messaging/canned?contact_id={_westContactId}"))
            .GetProperty("canned").EnumerateArray();
        var hello = rows.First(r => r.GetProperty("short_code").GetString() == "hello");
        Assert.DoesNotContain("Wendy", hello.GetProperty("rendered").GetString());

        var admin = await Authed("admin", "admin123");
        var adminRows = (await admin.GetFromJsonAsync<JsonElement>($"/api/messaging/canned?contact_id={_westContactId}"))
            .GetProperty("canned").EnumerateArray();
        Assert.Contains("Wendy", adminRows.First(r => r.GetProperty("short_code").GetString() == "hello")
            .GetProperty("rendered").GetString());
    }

    [Fact]
    public async Task Integrations_honour_view_and_manage_and_a_personal_connection_stays_personal()
    {
        var agent = await Authed("agent", "agent123");
        // no integrations.view: the catalog, rows and settings are closed…
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/integrations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/integrations/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/integrations/settings")).StatusCode);
        // …and running a real action (mail, charge, book) needs integrations.manage or flows.manage
        var act = await agent.PostAsJsonAsync("/api/integrations/action",
            new { action = "Send via Gmail", args = new Dictionary<string, object?> { ["text"] = "hi" } });
        Assert.Equal(HttpStatusCode.Forbidden, act.StatusCode);

        // …but their OWN connection is theirs to see and disconnect
        var mine = (await agent.GetFromJsonAsync<JsonElement>("/api/integrations/accounts")).GetProperty("accounts");
        var names = mine.EnumerateArray().Select(a => a.GetRawText()).ToList();
        Assert.Single(names);
        Assert.Contains("Agent calendar", names[0]);
        int AgentRow() => mine.EnumerateArray().First().GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK,
            (await agent.PostAsync($"/api/integrations/accounts/{AgentRow()}/disconnect", null)).StatusCode);

        int adminRow, sharedRow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var agentRow = AgentRow();
            adminRow = db.Integrations.AsNoTracking().AsEnumerable()
                .First(i => i.Provider == "google_calendar" && i.OwnerUserId != null && i.Id != agentRow).Id;
            sharedRow = db.Integrations.AsNoTracking().First(i => i.Provider == "stripe").Id;
        }
        // someone else's personal row is invisible to them; a shared one is not theirs to cut
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PostAsync($"/api/integrations/accounts/{adminRow}/disconnect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await agent.PostAsync($"/api/integrations/accounts/{sharedRow}/disconnect", null)).StatusCode);

        // admins hold integrations.view/.manage by default
        var admin = await Authed("admin", "admin123");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/integrations/providers")).StatusCode);
        var adminView = (await admin.GetFromJsonAsync<JsonElement>("/api/integrations/accounts")).GetRawText();
        Assert.Contains("Shared Stripe", adminView);
        Assert.Contains("Admin calendar", adminView);
    }
}
