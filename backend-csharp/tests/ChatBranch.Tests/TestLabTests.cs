using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.TestLab;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>The same host, with the Test lab switched off (config TEST_LAB=0 — never the process env).</summary>
public class NoLabFactory : ApiFactory
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["TEST_LAB"] = "0" }));
        return base.CreateHost(builder);
    }
}

/// <summary>
/// The Test lab: every scripted scenario passes against the test host (so CI
/// keeps each use case green), reset deletes the fenced data and nothing else,
/// only a superadmin may use it, and TEST_LAB=0 takes it off the map.
/// </summary>
public class TestLabTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public TestLabTests(ApiFactory f) => _f = f;

    private Task<HttpClient> Super() => SettingsTestKit.Client(_f, "superadmin", "super123");

    private static string Failures(JsonElement results) => string.Join("\n", results.EnumerateArray()
        .Where(r => !r.GetProperty("passed").GetBoolean())
        .Select(r => $"{r.GetProperty("id").GetString()}: {r.GetProperty("error").GetString()} "
                     + (r.GetProperty("skipped").GetBoolean() ? $"[skipped: {r.GetProperty("skip_reason").GetString()}] " : "")
                     + string.Join("; ", r.GetProperty("checks").EnumerateArray().Where(c => !c.GetProperty("ok").GetBoolean())
                         .Select(c => $"✗ {c.GetProperty("name").GetString()} ({c.GetProperty("detail").GetString()})"))));

    [Fact]
    public async Task Every_scenario_in_the_catalog_passes()
    {
        var super = await Super();
        var catalog = await super.GetFromJsonAsync<JsonElement>("/api/testlab/scenarios");
        var ids = catalog.GetProperty("categories").EnumerateArray()
            .SelectMany(c => c.GetProperty("scenarios").EnumerateArray()).Select(s => s.GetProperty("id").GetString()!).ToList();
        Assert.True(ids.Count >= 30, $"only {ids.Count} scenarios");
        Assert.Equal(LabCatalog.All.Count, ids.Count);

        var res = await super.PostAsJsonAsync("/api/testlab/run", new { source = "test" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var results = body.GetProperty("results");
        Assert.Equal(ids.Count, results.GetArrayLength());
        Assert.True(results.EnumerateArray().All(r => r.GetProperty("passed").GetBoolean()), Failures(results));
        Assert.True(results.EnumerateArray().All(r => r.GetProperty("checks").GetArrayLength() > 0));
        Assert.True(results.EnumerateArray().All(r => r.GetProperty("links").GetArrayLength() > 0),
            "no links: " + string.Join(", ", results.EnumerateArray().Where(r => r.GetProperty("links").GetArrayLength() == 0)
                .Select(r => r.GetProperty("id").GetString())));

        // the run is recorded and readable
        var runId = body.GetProperty("run").GetProperty("id").GetInt32();
        var runs = await super.GetFromJsonAsync<JsonElement>("/api/testlab/runs");
        Assert.Contains(runs.GetProperty("runs").EnumerateArray(), r => r.GetProperty("id").GetInt32() == runId);
        var one = await super.GetFromJsonAsync<JsonElement>($"/api/testlab/runs/{runId}");
        Assert.Equal(ids.Count, one.GetProperty("results").GetArrayLength());
        Assert.Equal(ids.Count, one.GetProperty("run").GetProperty("passed").GetInt32());
        SettingsTestKit.WithDb(_f, db => Assert.Contains(db.AuditLogs, a => a.Action == "testlab.run" && a.Target == $"run:{runId}"));

        // the page's way: one scenario per call, appended to the same run
        var first = await super.PostAsJsonAsync("/api/testlab/run", new { ids = new[] { ids[0] }, source = "page" });
        var pageRun = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetInt32();
        var second = await super.PostAsJsonAsync("/api/testlab/run", new { ids = new[] { ids[1] }, run_id = pageRun, source = "page" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var appended = await super.GetFromJsonAsync<JsonElement>($"/api/testlab/runs/{pageRun}");
        Assert.Equal(2, appended.GetProperty("run").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await super.PostAsJsonAsync("/api/testlab/run", new { ids = new[] { "no-such-scenario" } })).StatusCode);
    }

    /// <summary>Row counts OUTSIDE the lab fence — reset must leave every one of them as it was.</summary>
    private static Dictionary<string, int> Outside(AppDbContext db)
    {
        var s = LabScope.Load(db);
        var labUsers = s.UserIds;
        return new Dictionary<string, int>
        {
            ["contacts"] = db.Contacts.Count(c => !s.ContactIds.Contains(c.Id)),
            ["threads"] = db.Threads.Count(t => !s.ThreadIds.Contains(t.Id)),
            ["messages"] = db.Messages.Count(m => !s.ThreadIds.Contains(m.ThreadId)),
            ["entities"] = db.Entities.Count(e => !s.EntityIds.Contains(e.Id)),
            ["accounts"] = db.ChannelAccounts.Count(a => !s.AccountIds.Contains(a.Id)),
            ["users"] = db.Users.Count(u => !labUsers.Contains(u.Id)),
            ["roles"] = db.Roles.Count(r => !r.Slug.StartsWith(LabFixture.RolePrefix)),
            ["tags"] = db.Tags.Count(t => !s.TagIds.Contains(t.Id)),
            ["teams"] = db.Teams.Count(t => !s.TeamIds.Contains(t.Id)),
            ["workflows"] = db.Workflows.Count(w => !s.WorkflowIds.Contains(w.Id)),
            ["knowledge"] = db.KnowledgeItems.Count(k => !s.KnowledgeIds.Contains(k.Id)),
            ["settings"] = db.Settings.Count(x => x.EntityId == null || !s.EntityIds.Contains(x.EntityId.Value)),
            ["rules"] = db.AutomationRules.Count(r => r.EntityId == null || !s.EntityIds.Contains(r.EntityId.Value)),
            ["bookings"] = db.Bookings.Count(b => !s.ContactIds.Contains(b.ContactId)),
            ["drafts"] = db.AiDrafts.Count(d => d.ThreadId == null || !s.ThreadIds.Contains(d.ThreadId.Value)),
            ["skills"] = db.AgentSkills.Count(x => x.EntityId == null || !s.EntityIds.Contains(x.EntityId.Value)),
        };
    }

    [Fact]
    public async Task Reset_deletes_the_lab_and_nothing_else()
    {
        // data that is NOT the lab's: a contact, a conversation with a message, a setting, a tag
        var outsideKey = "60188" + Random.Shared.Next(100000, 999999);
        var (outsideContact, customFieldsBefore) = SettingsTestKit.WithDb(_f, db =>
        {
            var c = new Contact { Name = "Not a lab contact", Phone = "+" + outsideKey, TenantId = db.Tenants.First().Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.Tags.Add(new Tag { Name = "Outside tag", Slug = "outside-tag-" + outsideKey, AppliesTo = "contact",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
            var t = new ChatBranch.Domain.Thread { Channel = "whatsapp", Key = outsideKey, ContactId = c.Id, Name = c.Name,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            db.Messages.Add(new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = "real customer",
                CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return (c.Id, db.Settings.AsNoTracking().FirstOrDefault(x => x.Key == "custom_fields" && x.EntityId == null)?.Value);
        });
        var start = SettingsTestKit.WithDb(_f, Outside);

        var super = await Super();
        var demo = await (await super.PostAsJsonAsync("/api/testlab/demo", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(demo.GetProperty("already_loaded").GetBoolean());
        Assert.True(demo.GetProperty("created").GetProperty("contacts").GetInt32() >= 30);
        Assert.True(demo.GetProperty("created").GetProperty("conversations").GetInt32() >= 15);
        var again = await (await super.PostAsJsonAsync("/api/testlab/demo", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(again.GetProperty("already_loaded").GetBoolean());
        await super.PostAsJsonAsync("/api/testlab/run", new { ids = new[] { "identity-ask-and-create", "booking-book-and-cancel", "widget-visitor", "template-variables" } });

        var status = await super.GetFromJsonAsync<JsonElement>("/api/testlab/status");
        Assert.True(status.GetProperty("exists").GetBoolean());
        Assert.True(status.GetProperty("demo_loaded").GetBoolean());
        SettingsTestKit.WithDb(_f, db =>
        {
            var s = LabScope.Load(db);
            Assert.True(s.ContactIds.Count >= 30 && s.ThreadIds.Count >= 15);
            // the lab made states worth clicking through
            Assert.Contains(db.Threads, t => s.ThreadIds.Contains(t.Id) && t.Status == ThreadStatus.Resolved);
            Assert.Contains(db.Threads, t => s.ThreadIds.Contains(t.Id) && t.Status == ThreadStatus.Snoozed);
            Assert.Contains(db.Threads, t => s.ThreadIds.Contains(t.Id) && t.OptedOut);
            Assert.Contains(db.Threads, t => s.ThreadIds.Contains(t.Id) && t.HandoverAt != null && t.HandoverResolvedAt == null);
            Assert.Contains(db.AiDrafts, d => d.ThreadId != null && s.ThreadIds.Contains(d.ThreadId.Value) && d.Status == AiDraftStatus.Pending);
            Assert.Contains(db.Bookings, b => s.ContactIds.Contains(b.ContactId) && b.Status == BookingStatus.Booked);
        });
        // running the lab touched nothing outside it (the product's own "self-identified"
        // tag, made the first time ANY unknown customer identifies themselves, aside)
        var before = SettingsTestKit.WithDb(_f, Outside);
        Assert.Equal(start.Where(kv => kv.Key != "tags"), before.Where(kv => kv.Key != "tags"));
        Assert.InRange(before["tags"] - start["tags"], 0, 1);

        var reset = await super.PostAsJsonAsync("/api/testlab/reset", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var after = SettingsTestKit.WithDb(_f, Outside);
        Assert.Equal(before, after);
        SettingsTestKit.WithDb(_f, db =>
        {
            Assert.True(LabScope.Load(db).IsEmpty, "something of the lab survived the reset");
            Assert.Null(LabFixture.FindHq(db));
            Assert.False(db.Users.Any(u => u.Username.StartsWith(LabFixture.UserPrefix)));
            Assert.False(db.Contacts.Any(c => c.Email.EndsWith("@" + LabFixture.EmailDomain)));
            Assert.Contains(db.Contacts, c => c.Id == outsideContact);
            Assert.Contains(db.Messages, m => m.Text == "real customer");
            Assert.Equal(customFieldsBefore, db.Settings.AsNoTracking().First(x => x.Key == "custom_fields" && x.EntityId == null).Value);
            Assert.Contains(db.AuditLogs, a => a.Action == "testlab.reset");
            return 0;
        });
        var gone = await super.GetFromJsonAsync<JsonElement>("/api/testlab/status");
        Assert.False(gone.GetProperty("exists").GetBoolean());

        // and the lab rebuilds itself on the next run
        var rerun = await (await super.PostAsJsonAsync("/api/testlab/run", new { ids = new[] { "stop-opts-out" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rerun.GetProperty("results")[0].GetProperty("passed").GetBoolean(), Failures(rerun.GetProperty("results")));
    }

    [Fact]
    public async Task Only_a_superadmin_may_use_the_lab()
    {
        foreach (var (user, pass) in new[] { ("admin", "admin123"), ("agent", "agent123") })
        {
            var c = await SettingsTestKit.Client(_f, user, pass);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/testlab/scenarios")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/testlab/runs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/testlab/run", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/testlab/reset", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/testlab/demo", new { })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/testlab/scenarios")).StatusCode);
    }
}

/// <summary>TEST_LAB=0: the whole lab answers 404, for everyone.</summary>
public class TestLabDisabledTests : IClassFixture<NoLabFactory>
{
    private readonly NoLabFactory _f;
    public TestLabDisabledTests(NoLabFactory f) => _f = f;

    [Fact]
    public async Task With_TEST_LAB_0_every_lab_route_is_404()
    {
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        Assert.Equal(HttpStatusCode.NotFound, (await super.GetAsync("/api/testlab/scenarios")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.GetAsync("/api/testlab/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.GetAsync("/api/testlab/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.PostAsJsonAsync("/api/testlab/run", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.PostAsJsonAsync("/api/testlab/reset", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.PostAsJsonAsync("/api/testlab/demo", new { })).StatusCode);
    }
}
