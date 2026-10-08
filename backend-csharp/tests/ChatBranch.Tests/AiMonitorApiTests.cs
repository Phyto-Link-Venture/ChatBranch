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

/// <summary>The AI monitor over HTTP: entity scoping, capability gates, decisions + audit, behaviour settings.</summary>
public class AiMonitorApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AiMonitorApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _factory.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private (int EntityA, int EntityB) Entities() => WithDb(db =>
    {
        var tenant = db.Tenants.First();
        Entity Get(string slug)
        {
            var e = db.Entities.FirstOrDefault(x => x.Slug == slug);
            if (e != null) return e;
            e = new Entity { TenantId = tenant.Id, Name = slug.ToUpperInvariant(), Slug = slug,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Entities.Add(e);
            db.SaveChanges();
            return e;
        }
        return (Get("outlet-a").Id, Get("outlet-b").Id);
    });

    private int Draft(string key, int? entityId, string status = AiDraftStatus.Pending, string content = "Draft reply")
        => WithDb(db =>
        {
            var contact = new Contact { Name = $"C {key}", Phone = key, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(contact);
            db.SaveChanges();
            var thread = new Thread
            {
                Channel = "whatsapp", Key = key, Address = key, Name = $"C {key}", ContactId = contact.Id,
                EntityId = entityId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            db.Messages.Add(new Message { ThreadId = thread.Id, Direction = "in", Source = "customer",
                Text = "question?", Status = "received", CreatedAt = DateTime.UtcNow });
            var d = new AiDraft
            {
                ThreadId = thread.Id, ThreadKey = key, Channel = "whatsapp", ContactId = contact.Id,
                EntityId = entityId, Kind = AiDraftKind.Reply, Content = content, Status = status,
                HoldReason = status == AiDraftStatus.Pending ? "Every AI reply is reviewed" : "",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.AiDrafts.Add(d);
            db.SaveChanges();
            return d.Id;
        });

    private static List<int> Ids(JsonElement list) =>
        list.GetProperty("drafts").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task Monitor_list_is_entity_scoped_like_the_inbox()
    {
        var (a, b) = Entities();
        var inA = Draft("60133000001", a);
        var inB = Draft("60133000002", b);
        var unassigned = Draft("60133000003", null);

        // the agent works outlet A only
        WithDb(db =>
        {
            var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User)
                .First(p => p.User!.Username == "agent");
            var entA = db.Entities.First(e => e.Id == a);
            if (!profile.Entities.Any(e => e.Id == a)) profile.Entities.Add(entA);
            return db.SaveChanges();
        });
        try
        {
            var agent = await Authed("agent", "agent123");
            var mine = Ids(await agent.GetFromJsonAsync<JsonElement>("/api/ai/monitor?page_size=100"));
            Assert.Contains(inA, mine);
            Assert.Contains(unassigned, mine);
            Assert.DoesNotContain(inB, mine);
            // asking for another outlet narrows to their own scope instead of widening
            var sneaky = Ids(await agent.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=outlet-b&page_size=100"));
            Assert.DoesNotContain(inB, sneaky);
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.GetAsync($"/api/ai/monitor/{inB}")).StatusCode);

            var admin = await Authed("admin", "admin123");
            var all = Ids(await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?page_size=100"));
            Assert.Contains(inA, all);
            Assert.Contains(inB, all);
            var onlyB = Ids(await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=outlet-b&page_size=100"));
            Assert.Contains(inB, onlyB);
            Assert.DoesNotContain(inA, onlyB);

            // the chat-banner seam: one thread's pending drafts
            var banner = await admin.GetFromJsonAsync<JsonElement>(
                "/api/ai/monitor?thread=whatsapp:60133000002&status=pending");
            Assert.Equal(new[] { inB }, Ids(banner));
            Assert.True(banner.GetProperty("counts").GetProperty("pending").GetInt32() >= 1);
        }
        finally
        {
            WithDb(db =>
            {
                var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User)
                    .First(p => p.User!.Username == "agent");
                profile.Entities.Clear();
                return db.SaveChanges();
            });
        }
    }

    [Fact]
    public async Task Monitor_needs_the_ai_monitor_capability()
    {
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/ai/monitor")).StatusCode);

        WithDb(db =>
        {
            db.RolePermissions.Add(new RolePermission
            {
                Role = Roles.Agent, Capability = "ai.monitor", Allowed = false, UpdatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/ai/monitor")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await agent.PostAsJsonAsync("/api/ai/monitor/1/approve", new { })).StatusCode);
        }
        finally
        {
            WithDb(db =>
            {
                db.RolePermissions.RemoveRange(db.RolePermissions.Where(p => p.Role == Roles.Agent
                    && p.Capability == "ai.monitor"));
                return db.SaveChanges();
            });
        }

        // whoami advertises the new capabilities for the UI gates
        var who = await agent.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var caps = who.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("ai.monitor", caps);
        Assert.DoesNotContain("ai.behaviour.manage", caps);
    }

    [Fact]
    public async Task Approve_sends_and_audits_and_reject_keeps_the_reason()
    {
        var admin = await Authed("admin", "admin123");
        var approveId = Draft("60133000011", null, content: "We open at nine.");
        var rejectId = Draft("60133000012", null, content: "Everything is free!");

        var ok = await admin.PostAsJsonAsync($"/api/ai/monitor/{approveId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sent", body.GetProperty("status").GetString());
        Assert.Equal("approved", body.GetProperty("draft").GetProperty("status").GetString());
        var msgs = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/whatsapp/60133000011/messages");
        Assert.Contains(msgs.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("source").GetString() == "ai" && m.GetProperty("text").GetString() == "We open at nine.");

        // deciding twice is a conflict
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync($"/api/ai/monitor/{approveId}/approve", new { })).StatusCode);

        var rej = await admin.PostAsJsonAsync($"/api/ai/monitor/{rejectId}/reject", new { reason = "Made up an offer" });
        Assert.Equal(HttpStatusCode.OK, rej.StatusCode);

        var stats = await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor/stats?days=7");
        Assert.True(stats.GetProperty("approved").GetInt32() >= 1);
        Assert.Contains(stats.GetProperty("top_reject_reasons").EnumerateArray(),
            r => r.GetProperty("reason").GetString() == "Made up an offer");

        WithDb(db =>
        {
            Assert.True(db.AuditLogs.Any(l => l.Action == "ai.draft.approve" && l.Target == $"draft:{approveId}"));
            Assert.True(db.AuditLogs.Any(l => l.Action == "ai.draft.reject" && l.Detail == "Made up an offer"));
            return 0;
        });
    }

    [Fact]
    public async Task Behaviour_settings_are_gated_validated_and_entity_overridable()
    {
        Entities();
        var agent = await Authed("agent", "agent123");
        var read = await agent.GetFromJsonAsync<JsonElement>("/api/ai/behaviour");
        Assert.False(read.GetProperty("can_manage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/ai/behaviour", new { values = new { ai_review_mode = "review_all" } })).StatusCode);

        var admin = await Authed("admin", "admin123");
        var bad = await admin.PutAsJsonAsync("/api/ai/behaviour", new { values = new { ai_review_mode = "sometimes" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var org = await admin.PutAsJsonAsync("/api/ai/behaviour",
            new { values = new { ai_review_mode = "review_flagged", ai_wait_seconds = 30 } });
        org.EnsureSuccessStatusCode();
        string Value(JsonElement payload, string key) => payload.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("key").GetString() == key).GetProperty("value").GetString()!;
        string Scope(JsonElement payload, string key) => payload.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("key").GetString() == key).GetProperty("scope").GetString()!;

        // an entity inherits, then overrides, then resets
        var inherited = await admin.GetFromJsonAsync<JsonElement>("/api/ai/behaviour?entity=outlet-a");
        Assert.Equal("review_flagged", Value(inherited, "ai_review_mode"));
        Assert.Equal("global", Scope(inherited, "ai_review_mode"));

        var over = await admin.PutAsJsonAsync("/api/ai/behaviour?entity=outlet-a",
            new { values = new { ai_review_mode = "review_all" } });
        over.EnsureSuccessStatusCode();
        var overridden = await over.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("review_all", Value(overridden, "ai_review_mode"));
        Assert.Equal("entity", Scope(overridden, "ai_review_mode"));
        Assert.Equal("review_flagged", Value(await admin.GetFromJsonAsync<JsonElement>("/api/ai/behaviour"),
            "ai_review_mode"));

        var reset = await admin.DeleteAsync("/api/ai/behaviour?entity=outlet-a&key=ai_review_mode");
        reset.EnsureSuccessStatusCode();
        Assert.Equal("global", Scope(await reset.Content.ReadFromJsonAsync<JsonElement>(), "ai_review_mode"));

        // leave the organization default as the other tests expect it
        (await admin.PutAsJsonAsync("/api/ai/behaviour",
            new { values = new { ai_review_mode = "auto", ai_wait_seconds = 0 } })).EnsureSuccessStatusCode();
    }
}
