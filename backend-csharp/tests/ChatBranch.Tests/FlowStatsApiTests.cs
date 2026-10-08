using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The flow statistics surface: per-step counts per day (/api/flows/{id}/stats),
/// the paged + filtered run list and one run's path (/api/flows/runs[/{id}]),
/// the Analytics card feed (/api/analytics/flows) and the manual start.
/// </summary>
public class FlowStatsApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public FlowStatsApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static object N(string id, string type, string label) =>
        new { id, type, data = new { label, config = new { } } };

    /// <summary>Seed a 3-step flow with runs + step events spread over two days.</summary>
    private (string flowId, int[] contactIds) Seed(string flowId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        var tenantId = db.Tenants.First().Id;
        var wf = new Workflow
        {
            FlowId = flowId, Name = $"Flow {flowId}", Active = true, CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Nodes = new List<object?> { N("t", "trigger", "Tagged"), N("a", "action", "Say hi"), N("z", "end", "Done") },
            Edges = new List<object?>
            {
                new { id = "e1", source = "t", target = "a" }, new { id = "e2", source = "a", target = "z" },
            },
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        var zone = OrgClock.Zone(sc);
        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1);
        var ids = new List<int>();
        // 4 people: all entered; 3 got past "Say hi"; 2 finished; 1 failed at "a"
        for (var i = 0; i < 4; i++)
        {
            var contact = new Contact { Name = $"{flowId} person {i}", Phone = $"6019{flowId.GetHashCode() & 0xffff:00000}{i}",
                TenantId = tenantId, Status = "active", CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(contact);
            db.SaveChanges();
            ids.Add(contact.Id);
            var day = i < 2 ? yesterday : now;
            var status = i switch { 0 or 1 => FlowRunStatus.Done, 2 => FlowRunStatus.Waiting, _ => FlowRunStatus.Failed };
            var run = new FlowRun
            {
                WorkflowId = wf.Id, ContactId = contact.Id, TriggerKey = "t", Status = status,
                CurrentNode = status == FlowRunStatus.Done ? "z" : "a", StartedAt = day, UpdatedAt = day,
                FinishedAt = status is FlowRunStatus.Done or FlowRunStatus.Failed ? day : null,
                Detail = status == FlowRunStatus.Failed ? "no WhatsApp account" : "",
            };
            db.FlowRuns.Add(run);
            db.SaveChanges();
            void Ev(string node, string kind, string detail = "") => db.FlowStepEvents.Add(new FlowStepEvent
            {
                WorkflowId = flowId, RunId = run.Id, ContactId = contact.Id, NodeId = node, Kind = kind,
                Detail = detail, At = day, Day = OrgClock.Day(day, zone),
            });
            Ev("t", FlowStepKind.Entered); Ev("t", FlowStepKind.Completed);
            Ev("a", FlowStepKind.Entered);
            if (i < 2) { Ev("a", FlowStepKind.Completed); Ev("z", FlowStepKind.Entered); Ev("z", FlowStepKind.Completed); }
            if (i == 2) Ev("a", FlowStepKind.Waiting, "waiting for a reply");
            if (i == 3) Ev("a", FlowStepKind.Failed, "no WhatsApp account");
        }
        db.SaveChanges();
        return (flowId, ids.ToArray());
    }

    [Fact]
    public async Task Stats_count_people_per_step_per_day_with_drop_off_and_failures()
    {
        var (flowId, _) = Seed("wf-stats");
        var c = await Authed("superadmin", "super123");
        var stats = await c.GetFromJsonAsync<JsonElement>($"/api/flows/{flowId}/stats?days=7");

        var nodes = stats.GetProperty("nodes");
        Assert.Equal(4, nodes.GetProperty("t").GetProperty("entered").GetInt32());
        Assert.Equal(4, nodes.GetProperty("a").GetProperty("entered").GetInt32());
        Assert.Equal(2, nodes.GetProperty("a").GetProperty("completed").GetInt32());
        Assert.Equal(1, nodes.GetProperty("a").GetProperty("waitingNow").GetInt32());
        Assert.Equal(1, nodes.GetProperty("a").GetProperty("failed").GetInt32());
        Assert.Equal(2, nodes.GetProperty("z").GetProperty("entered").GetInt32());

        var series = stats.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(7, series.Count);
        var today = series[^1];
        var yesterday = series[^2];
        Assert.Equal(2, today.GetProperty("started").GetInt32());
        Assert.Equal(2, yesterday.GetProperty("started").GetInt32());
        Assert.Equal(2, yesterday.GetProperty("completed").GetInt32());
        Assert.Equal(1, today.GetProperty("failed").GetInt32());
        Assert.Equal(2, today.GetProperty("nodes").GetProperty("a").GetInt32());

        // "Say hi" → "Done": 4 in, 2 across = 50% drop
        var drop = stats.GetProperty("dropoff").EnumerateArray()
            .Single(d => d.GetProperty("from").GetString() == "a");
        Assert.Equal(50, drop.GetProperty("dropPct").GetDouble());
        var order = stats.GetProperty("order").EnumerateArray().Select(o => o.GetProperty("nodeId").GetString()).ToList();
        Assert.Equal(new[] { "t", "a", "z" }, order);

        var failure = stats.GetProperty("failures").EnumerateArray().Single();
        Assert.Equal("no WhatsApp account", failure.GetProperty("reason").GetString());
        Assert.Equal(1, stats.GetProperty("totals").GetProperty("waitingNow").GetInt32());

        // "Today" only sees today's people
        var todayOnly = await c.GetFromJsonAsync<JsonElement>($"/api/flows/{flowId}/stats?days=1");
        Assert.Equal(2, todayOnly.GetProperty("nodes").GetProperty("t").GetProperty("entered").GetInt32());

        var missing = await c.GetAsync("/api/flows/wf-nope/stats");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Runs_are_paged_filtered_and_expose_each_path()
    {
        var (flowId, contacts) = Seed("wf-runs");
        Seed("wf-other");
        var c = await Authed("admin", "admin123");

        var page1 = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?flow={flowId}&page_size=3");
        Assert.Equal(4, page1.GetProperty("count").GetInt32());
        Assert.Equal(2, page1.GetProperty("pages").GetInt32());
        Assert.Equal(3, page1.GetProperty("runs").GetArrayLength());
        var page2 = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?flow={flowId}&page_size=3&page=2");
        Assert.Equal(1, page2.GetProperty("runs").GetArrayLength());

        var failed = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?flow={flowId}&status=failed");
        var row = failed.GetProperty("runs").EnumerateArray().Single();
        Assert.Equal("no WhatsApp account", row.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("finishedAt").GetString()));
        Assert.Equal("Say hi", row.GetProperty("currentLabel").GetString());

        var byContact = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?contact={contacts[0]}");
        Assert.Equal(1, byContact.GetProperty("count").GetInt32());
        var byName = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?flow={flowId}&contact=person 1");
        Assert.Equal(1, byName.GetProperty("count").GetInt32());
        var reached = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs?flow={flowId}&node=z");
        Assert.Equal(2, reached.GetProperty("count").GetInt32());

        var id = row.GetProperty("id").GetInt32();
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/flows/runs/{id}");
        var path = detail.GetProperty("path").EnumerateArray().ToList();
        Assert.Equal(new[] { "entered", "completed", "entered", "failed" },
            path.Select(p => p.GetProperty("kind").GetString()));
        Assert.Equal("Say hi", path[^1].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Analytics_flows_card_and_capability_gate()
    {
        Seed("wf-card");
        var c = await Authed("admin", "admin123");
        var card = await c.GetFromJsonAsync<JsonElement>("/api/analytics/flows?days=7");
        var flow = card.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("flowId").GetString() == "wf-card");
        Assert.Equal(4, flow.GetProperty("entered").GetInt32());
        Assert.Equal(2, flow.GetProperty("reachedEnd").GetInt32());
        Assert.Equal(1, flow.GetProperty("failed").GetInt32());
        Assert.Equal(1, flow.GetProperty("inProgress").GetInt32());
        Assert.Equal(7, flow.GetProperty("series").GetArrayLength());

        // agents may read flows (flows.view) but not save the library (flows.manage)
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/analytics/flows")).StatusCode);
        var put = await agent.PutAsJsonAsync("/api/flows/library", new { workflows = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Manual_start_runs_a_flow_for_picked_contacts_once()
    {
        var c = await Authed("admin", "admin123");
        await c.PutAsJsonAsync("/api/flows/library", new
        {
            workflows = new[]
            {
                new
                {
                    flowId = "wf-manual", name = "Manual", hint = "", active = true, position = 0,
                    nodes = new object[]
                    {
                        new { id = "t", type = "trigger", data = new { label = "Manual", config = new { when = "Manual / API" } } },
                        new { id = "w", type = "wait", data = new { label = "Wait", config = new { mode = "time", amount = 1, unit = "days" } } },
                    },
                    edges = new object[] { new { id = "e", source = "t", target = "w" } },
                },
            },
        });
        var (_, contacts) = Seed("wf-manual-people");
        var first = await c.PostAsJsonAsync("/api/flows/wf-manual/start", new { contactIds = new[] { contacts[0], contacts[1] } });
        first.EnsureSuccessStatusCode();
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("started").GetInt32());
        // already running for contacts[0] → skipped
        var again = await c.PostAsJsonAsync("/api/flows/wf-manual/start", new { contactIds = new[] { contacts[0] } });
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("started").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());

        var runs = await c.GetFromJsonAsync<JsonElement>("/api/flows/runs?flow=wf-manual&status=waiting");
        Assert.Equal(2, runs.GetProperty("count").GetInt32());
        Assert.All(runs.GetProperty("runs").EnumerateArray(), r => Assert.Equal("w", r.GetProperty("currentNode").GetString()));

        // the library PUT recorded the flow's creation in the activity log
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(db.AuditLogs, a => a.Action == "flow.create" && a.Detail.Contains("Manual"));
        Assert.Contains(db.AuditLogs, a => a.Action == "flow.start");
        Assert.NotNull(db.Workflows.Single(w => w.FlowId == "wf-manual").LiveSince);
    }
}
