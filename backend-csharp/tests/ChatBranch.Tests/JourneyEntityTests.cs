using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// "Runs for" on a journey (GET/PUT /api/flows/library): every journey runs for
/// the whole organization or one entity (+ its branches). An organization-wide
/// admin creates and moves journeys at any level; a company admin only inside
/// their own entities — never organization-wide, never another company — and the
/// journeys above them stay read-only (never written, never pruned). A one-level
/// save (the MCP tools) never removes another level's journeys.
/// </summary>
public class JourneyEntityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public JourneyEntityTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private sealed record World(int A, int ABranch, int B, string SlugA, string SlugBranch, string SlugB, string Sfx);

    private static int _n;

    /// <summary>Company A (with a branch) and company B.</summary>
    private World Seed()
    {
        var sfx = $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
        return WithDb(db =>
        {
            var tenant = db.Tenants.First();
            var now = DateTime.UtcNow;
            Entity Ent(string slug, int? parent)
            {
                var e = new Entity { TenantId = tenant.Id, Name = $"Co {slug}", Slug = slug, ParentId = parent, CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var a = Ent($"je-a-{sfx}", null);
            var branch = Ent($"je-a-kl-{sfx}", a.Id);
            var b = Ent($"je-b-{sfx}", null);
            return new World(a.Id, branch.Id, b.Id, a.Slug, branch.Slug, b.Slug, sfx);
        });
    }

    private Workflow AddJourney(string flowId, int? entityId, bool active = false) => WithDb(db =>
    {
        var w = new Workflow
        {
            FlowId = flowId, Name = flowId, EntityId = entityId, Active = active,
            LiveSince = active ? DateTime.UtcNow.AddDays(-1) : null,
            Nodes = new List<object?> { new { id = "t1", type = "trigger", position = new { x = 0, y = 0 }, data = new { label = "", config = new { when = "Tag added", tag = "x" } } } },
            Edges = new List<object?>(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(w);
        db.SaveChanges();
        return w;
    });

    private Workflow? Row(string flowId) => WithDb(db => db.Workflows.AsNoTracking().FirstOrDefault(w => w.FlowId == flowId));

    private async Task<HttpClient> CompanyAdmin(World w)
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var username = $"jeadmin{w.Sfx}";
        var res = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role = "admin", entity_ids = new[] { w.A } });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await Platform.As(_f, username, "password123");
    }

    /// <summary>The library as the builder holds it (every journey the caller reads).</summary>
    private static async Task<List<JsonElement>> Library(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/flows/library")).GetProperty("workflows").EnumerateArray().ToList();

    /// <summary>The builder's whole-library save: the loaded journeys (as loaded, with their
    /// "Runs for") plus <paramref name="extra"/>, minus <paramref name="drop"/>.</summary>
    private static async Task<HttpResponseMessage> SaveAll(HttpClient c, List<JsonElement> lib,
        IEnumerable<object>? extra = null, Func<JsonElement, object>? edit = null, params string[] drop)
    {
        var rows = lib.Where(w => !drop.Contains(w.GetProperty("flowId").GetString()))
            .Select((w, i) => edit?.Invoke(w) ?? (object)new
            {
                flowId = w.GetProperty("flowId").GetString(), name = w.GetProperty("name").GetString(),
                hint = w.GetProperty("hint").GetString(), active = w.GetProperty("active").GetBoolean(), position = i,
                nodes = w.GetProperty("nodes"), edges = w.GetProperty("edges"),
                entity = w.GetProperty("entity").ValueKind == JsonValueKind.String ? w.GetProperty("entity").GetString() : "",
            }).ToList();
        if (extra != null) rows.AddRange(extra);
        return await c.PutAsJsonAsync("/api/flows/library", new { workflows = rows });
    }

    private static object NewJourney(string flowId, string? entity, int position = 99) => new
    {
        flowId, name = flowId, hint = "", active = false, position,
        nodes = new object[] { new { id = "t1", type = "trigger", position = new { x = 0, y = 0 }, data = new { label = "", config = new { when = "Tag added", tag = "x" } } } },
        edges = Array.Empty<object>(), entity,
    };

    [Fact]
    public async Task The_library_says_who_each_journey_runs_for_and_whether_it_can_be_changed()
    {
        var w = Seed();
        AddJourney($"je-org-{w.Sfx}", null);
        AddJourney($"je-a-{w.Sfx}", w.A);
        var admin = await Platform.As(_f, "admin", "admin123");
        var lib = await Library(admin);
        var org = lib.Single(x => x.GetProperty("flowId").GetString() == $"je-org-{w.Sfx}");
        var a = lib.Single(x => x.GetProperty("flowId").GetString() == $"je-a-{w.Sfx}");
        Assert.Equal(JsonValueKind.Null, org.GetProperty("entity").ValueKind);
        Assert.Equal(w.SlugA, a.GetProperty("entity").GetString());
        Assert.Equal($"Co {w.SlugA}", a.GetProperty("entity_name").GetString());
        Assert.True(org.GetProperty("editable").GetBoolean());
        Assert.True(a.GetProperty("editable").GetBoolean());

        // a company admin of A: their own journey is theirs, the organization's is read-only
        var co = await CompanyAdmin(w);
        var mine = await Library(co);
        Assert.False(mine.Single(x => x.GetProperty("flowId").GetString() == $"je-org-{w.Sfx}").GetProperty("editable").GetBoolean());
        Assert.True(mine.Single(x => x.GetProperty("flowId").GetString() == $"je-a-{w.Sfx}").GetProperty("editable").GetBoolean());

        // view-only (an agent without flows.manage): nothing is editable
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.All(await Library(agent), x => Assert.False(x.GetProperty("editable").GetBoolean()));
    }

    [Fact]
    public async Task An_organization_wide_admin_creates_and_moves_journeys_at_any_level()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var lib = await Library(admin);
        var res = await SaveAll(admin, lib, new[]
        {
            NewJourney($"je-new-org-{w.Sfx}", ""),
            NewJourney($"je-new-b-{w.Sfx}", w.SlugB),
            NewJourney($"je-new-default-{w.Sfx}", null),     // no "Runs for": the organization
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Null(Row($"je-new-org-{w.Sfx}")!.EntityId);
        Assert.Equal(w.B, Row($"je-new-b-{w.Sfx}")!.EntityId);
        Assert.Null(Row($"je-new-default-{w.Sfx}")!.EntityId);

        // move B's journey (live) to A's branch: a new audience, so its clock restarts
        WithDb(db =>
        {
            var row = db.Workflows.Single(x => x.FlowId == $"je-new-b-{w.Sfx}");
            row.Active = true; row.LiveSince = DateTime.UtcNow.AddDays(-3);
            return db.SaveChanges();
        });
        lib = await Library(admin);
        var before = DateTime.UtcNow;
        res = await SaveAll(admin, lib, edit: x => x.GetProperty("flowId").GetString() == $"je-new-b-{w.Sfx}"
            ? new
            {
                flowId = $"je-new-b-{w.Sfx}", name = "moved", hint = "", active = true, position = 0,
                nodes = x.GetProperty("nodes"), edges = x.GetProperty("edges"), entity = w.SlugBranch,
            }
            : null!);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var moved = Row($"je-new-b-{w.Sfx}")!;
        Assert.Equal(w.ABranch, moved.EntityId);
        Assert.True(moved.LiveSince >= before.AddSeconds(-1));
        Assert.True(WithDb(db => db.AuditLogs.Any(l => l.Action == "flow.move" && l.Detail.Contains("moved"))));

        // an unknown entity is refused and changes nothing
        res = await SaveAll(admin, await Library(admin), new[] { NewJourney($"je-bad-{w.Sfx}", "no-such-entity-xyz") });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(Row($"je-bad-{w.Sfx}"));
    }

    [Fact]
    public async Task A_company_admin_keeps_journeys_inside_their_own_entities()
    {
        var w = Seed();
        AddJourney($"je-org-{w.Sfx}", null, active: true);
        AddJourney($"je-b-{w.Sfx}", w.B);
        var co = await CompanyAdmin(w);
        var lib = await Library(co);
        Assert.DoesNotContain(lib, x => x.GetProperty("flowId").GetString() == $"je-b-{w.Sfx}");   // never sees B's

        // a new journey with no "Runs for" lands in their company, a branch is fine
        var res = await SaveAll(co, lib, new[] { NewJourney($"je-co-{w.Sfx}", null), NewJourney($"je-co-kl-{w.Sfx}", w.SlugBranch) });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(w.A, Row($"je-co-{w.Sfx}")!.EntityId);
        Assert.Equal(w.ABranch, Row($"je-co-kl-{w.Sfx}")!.EntityId);

        // never organization-wide, never another company
        lib = await Library(co);
        res = await SaveAll(co, lib, new[] { NewJourney($"je-co-org-{w.Sfx}", "") });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("whole organization", await res.Content.ReadAsStringAsync());
        res = await SaveAll(co, lib, new[] { NewJourney($"je-co-b-{w.Sfx}", w.SlugB) });
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(Row($"je-co-org-{w.Sfx}"));
        Assert.Null(Row($"je-co-b-{w.Sfx}"));

        // their journey cannot be moved out to the organization or to B
        res = await SaveAll(co, lib, edit: x => x.GetProperty("flowId").GetString() == $"je-co-{w.Sfx}"
            ? new { flowId = $"je-co-{w.Sfx}", name = "x", hint = "", active = false, position = 0, nodes = x.GetProperty("nodes"), edges = x.GetProperty("edges"), entity = "" }
            : null!);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(w.A, Row($"je-co-{w.Sfx}")!.EntityId);
    }

    [Fact]
    public async Task Journeys_above_a_company_admin_are_never_written_or_removed_by_them()
    {
        var w = Seed();
        var org = AddJourney($"je-org-{w.Sfx}", null, active: true);
        var co = await CompanyAdmin(w);
        var lib = await Library(co);

        // pausing / renaming the organization's journey: reported, not applied
        var res = await SaveAll(co, lib, edit: x => x.GetProperty("flowId").GetString() == org.FlowId
            ? new { flowId = org.FlowId, name = "hijacked", hint = "", active = false, position = 0, nodes = x.GetProperty("nodes"), edges = x.GetProperty("edges"), entity = "" }
            : null!);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("hijacked", body.GetProperty("read_only").EnumerateArray().Select(x => x.GetString()));
        var row = Row(org.FlowId)!;
        Assert.Equal(org.FlowId, row.Name);
        Assert.True(row.Active);

        // leaving it out of the save never deletes it
        res = await SaveAll(co, await Library(co), drop: org.FlowId);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.NotNull(Row(org.FlowId));

        // their own journey still saves alongside it, and leaving it out removes it
        res = await SaveAll(co, await Library(co), new[] { NewJourney($"je-co-{w.Sfx}", w.SlugA) });
        Assert.True(res.IsSuccessStatusCode);
        Assert.NotNull(Row($"je-co-{w.Sfx}"));
        res = await SaveAll(co, await Library(co), drop: $"je-co-{w.Sfx}");
        Assert.True(res.IsSuccessStatusCode);
        Assert.Null(Row($"je-co-{w.Sfx}"));
    }

    private static object NewJourneyFor(string flowId, params string[] entities) => new
    {
        flowId, name = flowId, hint = "", active = false, position = 99,
        nodes = new object[] { new { id = "t1", type = "trigger", position = new { x = 0, y = 0 }, data = new { label = "", config = new { when = "Tag added", tag = "x" } } } },
        edges = Array.Empty<object>(), entities,
    };

    private List<int> RunsFor(string flowId) => WithDb(db =>
    {
        var w = db.Workflows.AsNoTracking().Single(x => x.FlowId == flowId);
        return ChatBranch.Data.LibraryEntities.Ids(db, LibraryKinds.Workflow, w.Id, w.EntityId);
    });

    [Fact]
    public async Task A_journey_can_run_for_several_entities_and_be_moved_between_them()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var res = await SaveAll(admin, await Library(admin), new[] { NewJourneyFor($"je-two-{w.Sfx}", w.SlugA, w.SlugB) });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(new List<int> { w.A, w.B }, RunsFor($"je-two-{w.Sfx}"));

        var row = (await Library(admin)).Single(x => x.GetProperty("flowId").GetString() == $"je-two-{w.Sfx}");
        Assert.False(row.GetProperty("global").GetBoolean());
        Assert.Equal(new[] { w.SlugA, w.SlugB }, row.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToArray());

        // down to B alone, then the whole organization: the ticks follow
        res = await SaveAll(admin, await Library(admin), edit: x => x.GetProperty("flowId").GetString() == $"je-two-{w.Sfx}"
            ? new { flowId = $"je-two-{w.Sfx}", name = "two", hint = "", active = false, position = 0, nodes = x.GetProperty("nodes"), edges = x.GetProperty("edges"), entities = new[] { w.SlugB } }
            : null!);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(new List<int> { w.B }, RunsFor($"je-two-{w.Sfx}"));
        res = await SaveAll(admin, await Library(admin), edit: x => x.GetProperty("flowId").GetString() == $"je-two-{w.Sfx}"
            ? new { flowId = $"je-two-{w.Sfx}", name = "two", hint = "", active = false, position = 0, nodes = x.GetProperty("nodes"), edges = x.GetProperty("edges"), entities = Array.Empty<string>() }
            : null!);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Empty(RunsFor($"je-two-{w.Sfx}"));

        // deleting it forgets its ticks
        res = await SaveAll(admin, await Library(admin), new[] { NewJourneyFor($"je-gone-{w.Sfx}", w.SlugA, w.SlugB) });
        Assert.True(res.IsSuccessStatusCode);
        var goneId = WithDb(db => db.Workflows.Single(x => x.FlowId == $"je-gone-{w.Sfx}").Id);
        res = await SaveAll(admin, await Library(admin), drop: $"je-gone-{w.Sfx}");
        Assert.True(res.IsSuccessStatusCode);
        Assert.False(WithDb(db => db.LibraryEntities.Any(l => l.Kind == LibraryKinds.Workflow && l.ItemId == goneId)));
    }

    [Fact]
    public async Task A_company_admin_picks_several_of_their_own_entities_never_another_company()
    {
        var w = Seed();
        var co = await CompanyAdmin(w);
        var res = await SaveAll(co, await Library(co), new[] { NewJourneyFor($"je-co-two-{w.Sfx}", w.SlugA, w.SlugBranch) });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(new List<int> { w.A, w.ABranch }, RunsFor($"je-co-two-{w.Sfx}"));

        res = await SaveAll(co, await Library(co), new[] { NewJourneyFor($"je-co-ab-{w.Sfx}", w.SlugA, w.SlugB) });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Null(Row($"je-co-ab-{w.Sfx}"));

        // a journey an org-wide admin shared between A and B: visible to A's admin, read-only, never removed
        var admin = await Platform.As(_f, "admin", "admin123");
        (await SaveAll(admin, await Library(admin), new[] { NewJourneyFor($"je-shared-{w.Sfx}", w.SlugA, w.SlugB) })).EnsureSuccessStatusCode();
        var mine = await Library(co);
        var shared = mine.Single(x => x.GetProperty("flowId").GetString() == $"je-shared-{w.Sfx}");
        Assert.False(shared.GetProperty("editable").GetBoolean());
        res = await SaveAll(co, mine, drop: $"je-shared-{w.Sfx}");
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal(new List<int> { w.A, w.B }, RunsFor($"je-shared-{w.Sfx}"));
    }

    [Fact]
    public async Task Saving_one_journey_over_MCP_never_removes_another_levels_journeys()
    {
        var w = Seed();
        AddJourney($"je-org-{w.Sfx}", null);
        AddJourney($"je-a-{w.Sfx}", w.A);
        var key = Platform.Client(_f, Platform.MintKey(_f, "admin"));

        // a new journey for company A: the organization's journey stays
        var r = await Platform.CallTool(key, "flows_save_flow", new { name = "A reminder", entity = w.SlugA });
        Assert.False(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.NotNull(Row($"je-org-{w.Sfx}"));
        // an organization-wide one: A's journey stays
        r = await Platform.CallTool(key, "flows_save_flow", new { name = "Org welcome" });
        Assert.False(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.NotNull(Row($"je-a-{w.Sfx}"));
        // deleting A's journey removes only that one
        r = await Platform.CallTool(key, "flows_delete_flow", new { flow_id = $"je-a-{w.Sfx}", entity = w.SlugA, confirm = true });
        Assert.False(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.Null(Row($"je-a-{w.Sfx}"));
        Assert.NotNull(Row($"je-org-{w.Sfx}"));
    }

    [Fact]
    public async Task A_company_admin_cannot_change_an_organization_wide_journey_over_MCP()
    {
        var w = Seed();
        var org = AddJourney($"je-org-{w.Sfx}", null);
        await CompanyAdmin(w);
        var key = Platform.Client(_f, Platform.MintKey(_f, $"jeadmin{w.Sfx}"));

        var r = await Platform.CallTool(key, "flows_set_live", new { flow_id = org.FlowId, live = true });
        Assert.True(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.Contains("whole organization", r.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(Row(org.FlowId)!.Active);
        r = await Platform.CallTool(key, "flows_delete_flow", new { flow_id = org.FlowId, confirm = true });
        Assert.True(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.NotNull(Row(org.FlowId));
        // but a journey for their own company is fine
        r = await Platform.CallTool(key, "flows_save_flow", new { name = "Mine", entity = w.SlugA });
        Assert.False(r.GetProperty("isError").GetBoolean(), r.ToString());
        Assert.True(WithDb(db => db.Workflows.Any(x => x.Name == "Mine" && x.EntityId == w.A)));
    }
}
