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
/// Multi-entity library availability ("Available to: Everyone / Selected
/// entities"): an item ticked for two sibling companies reaches both and their
/// branches but never a third; no tick = global; a legacy single-EntityId row
/// keeps working; an assigned agent can only tick inside their scope; and the
/// set round-trips through create / update.
/// </summary>
public class MultiEntityLibraryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    private static readonly object Gate = new();

    public MultiEntityLibraryTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
        Arrange();
    }

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Entities.Any(e => e.Slug == "ml-a")) return;
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            Entity Add(string slug, string name, int? parent)
            {
                var e = new Entity { TenantId = tenant.Id, Name = name, Slug = slug, ParentId = parent, CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var a = Add("ml-a", "Company A", null);
            Add("ml-a-kl", "A KL", a.Id);
            var b = Add("ml-b", "Company B", null);
            Add("ml-b-pg", "B Penang", b.Id);
            Add("ml-c", "Company C", null);
            // the agent is assigned to Company A and may curate templates + AI libraries
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities.Add(a);
            foreach (var cap in new[] { "templates.manage", "settings.ai.manage", "script_templates.manage" })
                db.RolePermissions.Add(new RolePermission { Role = Roles.Agent, Capability = cap, Allowed = true, UpdatedAt = now });
            // a legacy row: one EntityId, no tick rows
            db.CannedResponses.Add(new CannedResponse { ShortCode = "ml-legacy", Content = "legacy", EntityId = a.Id, CreatedAt = now });
            db.SaveChanges();
        }
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<List<string>> Codes(HttpClient c, string? entity)
    {
        var url = "/api/messaging/canned" + (entity == null ? "" : $"?entity={entity}");
        return (await c.GetFromJsonAsync<JsonElement>(url)).GetProperty("canned").EnumerateArray()
            .Select(x => x.GetProperty("short_code").GetString() ?? "").ToList();
    }

    private static List<string> Slugs(JsonElement item) =>
        item.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("slug").GetString() ?? "").ToList();

    [Fact]
    public async Task Ticked_for_two_siblings_reaches_both_and_their_branches_never_a_third()
    {
        var admin = await Authed("admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-ab", content = "For A and B", entities = new[] { "ml-a", "ml-b" } });
        res.EnsureSuccessStatusCode();
        var item = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(item.GetProperty("global").GetBoolean());
        Assert.Equal(new[] { "ml-a", "ml-b" }, Slugs(item));

        foreach (var ent in new[] { "ml-a", "ml-a-kl", "ml-b", "ml-b-pg" })
            Assert.Contains("ml-ab", await Codes(admin, ent));
        Assert.DoesNotContain("ml-ab", await Codes(admin, "ml-c"));
        Assert.Contains("ml-ab", await Codes(admin, null)); // the admin's "All" view

        // the same rule for every library kind: knowledge, automations, skills
        var kb = await admin.PostAsJsonAsync("/api/aicore/knowledge",
            new { title = "ML shared fact", content = "x", entities = new[] { "ml-a", "ml-b" } });
        kb.EnsureSuccessStatusCode();
        Assert.Contains("ML shared fact", await admin.GetStringAsync("/api/aicore/knowledge?entity=ml-b-pg"));
        Assert.DoesNotContain("ML shared fact", await admin.GetStringAsync("/api/aicore/knowledge?entity=ml-c"));

        var rule = await admin.PostAsJsonAsync("/api/automations", new
        {
            name = "ML shared rule", @event = "message_created",
            actions = new[] { new { type = "add_label", value = "vip" } },
            entities = new[] { "ml-a", "ml-b" },
        });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);
        Assert.Contains("ML shared rule", await admin.GetStringAsync("/api/automations?entity=ml-a-kl"));
        Assert.DoesNotContain("ML shared rule", await admin.GetStringAsync("/api/automations?entity=ml-c"));

        var put = await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            entity = "ml-a",
            skills = new[] { new { id = "ml-skill", name = "ML shared skill", useWhen = "always", instructions = "x",
                position = 0, entities = new[] { "ml-b" } } },
        });
        put.EnsureSuccessStatusCode();
        var skills = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills?entity=ml-b-pg");
        var skill = skills.GetProperty("skills").EnumerateArray().First(s => s.GetProperty("id").GetString() == "ml-skill");
        Assert.Equal(new[] { "ml-a", "ml-b" }, Slugs(skill));
        Assert.DoesNotContain("ML shared skill", await admin.GetStringAsync("/api/aicore/skills?entity=ml-c"));

        // and the AI's own reads (ScopeInherited) follow the ticks too
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        var bpg = db.Entities.First(e => e.Slug == "ml-b-pg").Id;
        var c = db.Entities.First(e => e.Slug == "ml-c").Id;
        Assert.Contains(sc.ScopeInherited(db.KnowledgeItems.AsNoTracking(), bpg).ToList(), k => k.Title == "ML shared fact");
        Assert.DoesNotContain(sc.ScopeInherited(db.KnowledgeItems.AsNoTracking(), c).ToList(), k => k.Title == "ML shared fact");
        Assert.Contains(sc.ScopeInherited(db.AgentSkills.AsNoTracking(), bpg).ToList(), s => s.SkillId == "ml-skill");
    }

    [Fact]
    public async Task No_tick_is_global_and_visible_everywhere()
    {
        var admin = await Authed("admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-all", content = "Everyone", entities = Array.Empty<string>() });
        res.EnsureSuccessStatusCode();
        var item = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(item.GetProperty("global").GetBoolean());
        Assert.Empty(Slugs(item));
        foreach (var ent in new[] { "ml-a", "ml-b-pg", "ml-c" })
            Assert.Contains("ml-all", await Codes(admin, ent));
    }

    [Fact]
    public async Task A_legacy_single_entity_row_still_works()
    {
        var admin = await Authed("admin", "admin123");
        Assert.Contains("ml-legacy", await Codes(admin, "ml-a"));
        Assert.Contains("ml-legacy", await Codes(admin, "ml-a-kl"));
        Assert.DoesNotContain("ml-legacy", await Codes(admin, "ml-b"));
        var row = (await admin.GetFromJsonAsync<JsonElement>("/api/messaging/canned?entity=ml-a"))
            .GetProperty("canned").EnumerateArray().First(x => x.GetProperty("short_code").GetString() == "ml-legacy");
        Assert.Equal(new[] { "ml-a" }, Slugs(row));
        Assert.False(row.GetProperty("global").GetBoolean());
        Assert.Equal("ml-a", row.GetProperty("entity").GetString()); // the primary, as before
    }

    [Fact]
    public async Task An_assigned_agent_can_only_tick_inside_their_scope()
    {
        var agent = await Authed("agent", "agent123");
        var outside = await agent.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-agent-b", content = "x", entities = new[] { "ml-a", "ml-b" } });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        var global = await agent.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-agent-g", content = "x", entities = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);
        var kbOutside = await agent.PostAsJsonAsync("/api/aicore/knowledge",
            new { title = "ML agent fact", content = "x", entities = new[] { "ml-c" } });
        Assert.Equal(HttpStatusCode.Forbidden, kbOutside.StatusCode);

        var inside = await agent.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-agent-a", content = "x", entities = new[] { "ml-a", "ml-a-kl" } });
        inside.EnsureSuccessStatusCode();
        var id = (await inside.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        // widening it to another company is refused too
        var widen = await agent.PatchAsJsonAsync($"/api/messaging/canned/{id}", new { entities = new[] { "ml-a", "ml-c" } });
        Assert.Equal(HttpStatusCode.Forbidden, widen.StatusCode);
        // an admin's item ticked for A + C: the agent may not drop C (outside their scope)
        var admin = await Authed("admin", "admin123");
        var shared = await admin.PostAsJsonAsync("/api/calls/script-templates",
            new { name = "ML shared script", content = "x", entities = new[] { "ml-a", "ml-c" } });
        shared.EnsureSuccessStatusCode();
        var sid = (await shared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var drop = await agent.PatchAsJsonAsync($"/api/calls/script-templates/{sid}", new { entities = new[] { "ml-a" } });
        Assert.Equal(HttpStatusCode.Forbidden, drop.StatusCode);
    }

    [Fact]
    public async Task The_set_round_trips_through_create_and_update()
    {
        var admin = await Authed("admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/calls/script-templates",
            new { name = "ML round trip", content = "Hello", entities = new[] { "ml-b", "ml-a" } });
        res.EnsureSuccessStatusCode();
        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(new[] { "ml-b", "ml-a" }, Slugs(created)); // order kept, first = primary
        Assert.Equal("ml-b", created.GetProperty("entity").GetString());

        // content-only edits keep the set
        var kept = await (await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}", new { content = "Hi" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "ml-b", "ml-a" }, Slugs(kept));

        var moved = await (await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}", new { entities = new[] { "ml-c" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "ml-c" }, Slugs(moved));
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(db.LibraryEntities.Where(l => l.Kind == LibraryKinds.Script && l.ItemId == id));
        }
        var scripts = await admin.GetStringAsync("/api/calls/script-templates?entity=ml-a");
        Assert.DoesNotContain("ML round trip", scripts);

        // the older single `entity` still works…
        var legacy = await (await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}", new { entity = "ml-a" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "ml-a" }, Slugs(legacy));
        // …and [] makes it global
        var global = await (await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}", new { entities = Array.Empty<string>() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(global.GetProperty("global").GetBoolean());
        Assert.Empty(Slugs(global));

        // an unknown entity is refused, never silently global
        var bad = await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}", new { entities = new[] { "no-such" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_primary_entity_promotes_another_tick()
    {
        var admin = await Authed("admin", "admin123");
        var ent = await admin.PostAsJsonAsync("/api/v1/entities", new { name = "ML Temp", slug = "ml-temp" });
        ent.EnsureSuccessStatusCode();
        var tempId = (await ent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var res = await admin.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "ml-promote", content = "x", entities = new[] { "ml-temp", "ml-c" } });
        res.EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/v1/entities/{tempId}")).EnsureSuccessStatusCode();
        var row = (await admin.GetFromJsonAsync<JsonElement>("/api/messaging/canned?entity=ml-c"))
            .GetProperty("canned").EnumerateArray().First(x => x.GetProperty("short_code").GetString() == "ml-promote");
        Assert.Equal(new[] { "ml-c" }, Slugs(row));
        Assert.False(row.GetProperty("global").GetBoolean());
    }
}
