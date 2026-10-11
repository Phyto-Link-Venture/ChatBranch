using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// The Test lab API — the <c>testlab.run</c> capability (superadmin only by default
/// and organization-level: never a person bound to entities; a run drives the
/// shared queue / flow / monitor ticks, so grant it to trusted operators only), and
/// not mapped at all (404) when env / config <c>TEST_LAB</c> is 0 / false / off.
/// <list type="bullet">
///   <item><c>GET /api/testlab/scenarios</c> — the catalog grouped by category.</item>
///   <item><c>GET /api/testlab/status</c> — does the fenced data exist, how much of it.</item>
///   <item><c>POST /api/testlab/run {ids?, run_id?, source?}</c> — run (none = all), sequentially,
///         each with a timeout; results are appended to <c>run_id</c>'s row when given.</item>
///   <item><c>GET /api/testlab/runs</c>, <c>GET /api/testlab/runs/{id}</c> — the last 50 runs.</item>
///   <item><c>POST /api/testlab/reset</c> — delete everything inside the fence, nothing else.</item>
///   <item><c>POST /api/testlab/demo</c> — load the realistic demo dataset inside the fence.</item>
/// </list>
/// </summary>
public static class TestLabEndpoints
{
    private static readonly string[] Sources = { "page", "cli", "api", "test" };

    /// <summary>The Permissions-page capability every Test lab route needs.</summary>
    public const string Capability = "testlab.run";

    public static void MapTestLabEndpoints(this WebApplication app)
    {
        if (!LabRunner.Enabled(app.Configuration)) return;

        app.MapGet("/api/testlab/scenarios", (ClaimsPrincipal me, IServiceProvider sp) =>
        {
            var llm = sp.GetService<ILlmClient>();
            var list = LabCatalog.All;
            return Results.Ok(new
            {
                total = list.Count,
                has_ai_key = llm is { IsConfigured: true },
                categories = LabCategories.All.Select(c => new
                {
                    id = c.Id,
                    label = c.Label,
                    covers = c.Covers,
                    scenarios = list.Where(s => s.Category == c.Id).Select(s => new
                    {
                        id = s.Id,
                        title = s.Title,
                        proves = s.Proves,
                        steps = s.Steps,
                        expect = s.Expect,
                        needs_ai_key = s.NeedsAiKey,
                    }),
                }).Where(c => c.scenarios.Any()),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapGet("/api/testlab/status", (AppDbContext db) =>
        {
            var hq = LabFixture.FindHq(db);
            var scope = LabScope.Load(db);
            var demoTag = LabDemo.DemoTagCount(db);
            return Results.Ok(new
            {
                exists = hq != null,
                entity = hq?.Slug,
                entity_name = hq?.Name,
                demo_loaded = demoTag > 0,
                counts = new
                {
                    entities = scope.EntityIds.Count,
                    accounts = scope.AccountIds.Count,
                    users = scope.UserIds.Count,
                    contacts = scope.ContactIds.Count,
                    conversations = scope.ThreadIds.Count,
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapPost("/api/testlab/run", async (HttpContext http, ClaimsPrincipal me, AppDbContext db) =>
        {
            JsonElement body = default;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body); }
            catch { /* empty body = run everything */ }
            var ids = new List<string>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ids", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                ids.AddRange(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            var unknown = ids.Where(id => LabCatalog.Find(id) == null).ToList();
            if (unknown.Count > 0)
                return Results.Json(new { detail = $"Unknown scenario(s): {string.Join(", ", unknown)}." }, statusCode: 400);
            var picked = ids.Count == 0 ? LabCatalog.All.ToList() : ids.Distinct().Select(id => LabCatalog.Find(id)!).ToList();
            int? runId = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("run_id", out var r)
                         && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rid) ? rid : null;
            var source = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("source", out var s)
                         && s.ValueKind == JsonValueKind.String && Sources.Contains(s.GetString()) ? s.GetString()! : "api";
            if (runId is int existing && !await db.TestLabRuns.AnyAsync(x => x.Id == existing))
                return Results.Json(new { detail = "Unknown run_id." }, statusCode: 404);

            var results = new List<LabResult>();
            foreach (var scenario in picked)
                results.Add(await LabRunner.RunOneAsync(http, scenario, me));

            var run = await LabRunner.RecordAsync(db, runId, me.Identity?.Name ?? "", source, results);
            var passed = results.Count(x => x.Passed);
            var failed = results.Count(x => !x.Passed && !x.Skipped);
            AuditTrail.Write(db, "testlab.run", $"run:{run.Id}",
                AuditTrail.Cap($"{results.Count} scenario(s): {passed} passed, {failed} failed, "
                               + $"{results.Count(x => x.Skipped)} skipped — "
                               + string.Join(", ", results.Select(x => $"{x.Id}={(x.Passed ? "pass" : x.Skipped ? "skip" : "FAIL")}")), 1000),
                actor: me.Identity?.Name);
            return Results.Ok(new { run = LabRunner.RunSummary(run), results });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapGet("/api/testlab/runs", async (AppDbContext db, int? limit) =>
        {
            var rows = await db.TestLabRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Take(Math.Clamp(limit ?? LabRunner.KeepRuns, 1, LabRunner.KeepRuns)).ToListAsync();
            return Results.Ok(new { runs = rows.Select(LabRunner.RunSummary) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapGet("/api/testlab/runs/{id:int}", async (int id, AppDbContext db) =>
        {
            var run = await db.TestLabRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            if (run == null) return Results.NotFound(new { detail = "Run not found." });
            return Results.Ok(new { run = LabRunner.RunSummary(run), results = LabRunner.ResultsOf(run) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapPost("/api/testlab/reset", (AppDbContext db, ClaimsPrincipal me) =>
        {
            var deleted = LabReset.Run(db);
            db.ChangeTracker.Clear();
            AuditTrail.Write(db, "testlab.reset", "test-lab",
                deleted.Count == 0 ? "nothing to delete"
                    : AuditTrail.Cap(string.Join(", ", deleted.Select(kv => $"{kv.Key}={kv.Value}")), 1000),
                actor: me.Identity?.Name);
            return Results.Ok(new { ok = true, deleted });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);

        app.MapPost("/api/testlab/demo", (HttpContext http, AppDbContext db, ClaimsPrincipal me) =>
        {
            var outcome = LabDemo.Load(http, me);
            AuditTrail.Write(db, "testlab.demo", "test-lab",
                outcome.Already ? "demo data already loaded"
                    : AuditTrail.Cap(string.Join(", ", outcome.Created.Select(kv => $"{kv.Key}={kv.Value}")), 1000),
                actor: me.Identity?.Name);
            return Results.Ok(new
            {
                ok = true,
                already_loaded = outcome.Already,
                created = outcome.Created,
                links = outcome.Links,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);
    }
}
