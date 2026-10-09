using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// Runs scenarios one after the other, each in its OWN DI scope (a fresh
/// DbContext) with a timeout, cleans up after each (restored settings, paused
/// flows, no WhatsApp lookups for lab numbers) and records the run.
/// </summary>
public static class LabRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public const int KeepRuns = 50;

    /// <summary>The Test lab is on unless env/config <c>TEST_LAB</c> says 0 / false / off.</summary>
    public static bool Enabled(IConfiguration cfg) =>
        (cfg["TEST_LAB"] ?? "").Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    public static async Task<LabResult> RunOneAsync(HttpContext http, LabScenario scenario, ClaimsPrincipal super)
    {
        var sw = Stopwatch.StartNew();
        var result = new LabResult
        {
            Id = scenario.Id, Title = scenario.Title, Category = scenario.Category, RanAt = DateTime.UtcNow,
        };
        var scope = http.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
        LabContext? ctx = null;
        var work = Task.Run(async () =>
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
            var lab = LabFixture.Ensure(db, sc, Caller.TenantId(super));
            ctx = new LabContext(scope.ServiceProvider, http, super, lab);
            try { await scenario.Run(ctx); }
            finally { ctx.RunCleanup(); }
        });
        var finished = await Task.WhenAny(work, Task.Delay(Timeout));
        if (finished != work)
        {
            result.Error = $"Timed out after {Timeout.TotalSeconds:0} s.";
            // the scope is released when the scenario finally returns
            _ = work.ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);
        }
        else
        {
            try { await work; }
            catch (LabSkipException skip)
            {
                result.Skipped = true;
                result.SkipReason = skip.Message;
            }
            catch (Exception ex)
            {
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally { scope.Dispose(); }
        }
        if (ctx != null)
        {
            // (a timed-out scenario may still be writing — take what is there)
            try { result.Checks = ctx.Checks.ToList(); } catch { result.Checks = new(); }
            try { result.Links = ctx.Links.ToList(); } catch { result.Links = new(); }
            result.Brain = ctx.BrainName;
        }
        result.Passed = !result.Skipped && result.Error.Length == 0 && result.Checks.Count > 0
                        && result.Checks.All(c => c.Ok);
        if (!result.Skipped && result.Error.Length == 0 && result.Checks.Count == 0)
            result.Error = "The scenario recorded no checks.";
        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    // ---- run history ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static List<LabResult> ResultsOf(TestLabRun run)
    {
        try { return JsonSerializer.Deserialize<List<LabResult>>(run.Results, Json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Append results to a run row (a new one when <paramref name="runId"/> is null).</summary>
    public static async Task<TestLabRun> RecordAsync(AppDbContext db, int? runId, string startedBy, string source,
        IReadOnlyList<LabResult> results)
    {
        TestLabRun? run = runId is int id ? await db.TestLabRuns.FirstOrDefaultAsync(r => r.Id == id) : null;
        var now = DateTime.UtcNow;
        if (run == null)
        {
            run = new TestLabRun { StartedBy = startedBy, Source = source, CreatedAt = now };
            db.TestLabRuns.Add(run);
        }
        // a scenario re-run inside the same run replaces its earlier result
        var all = ResultsOf(run).Where(r => results.All(n => n.Id != r.Id)).Concat(results).ToList();
        run.Results = JsonSerializer.Serialize(all, Json);
        run.Total = all.Count;
        run.Passed = all.Count(r => r.Passed);
        run.Skipped = all.Count(r => r.Skipped);
        run.Failed = all.Count(r => !r.Passed && !r.Skipped);
        run.DurationMs = all.Sum(r => r.DurationMs);
        run.UpdatedAt = now;
        await db.SaveChangesAsync();

        // keep the last 50
        var stale = await db.TestLabRuns.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip(KeepRuns).Select(r => r.Id).ToListAsync();
        if (stale.Count > 0) await db.TestLabRuns.Where(r => stale.Contains(r.Id)).ExecuteDeleteAsync();
        return run;
    }

    public static object RunSummary(TestLabRun r) => new
    {
        id = r.Id,
        started_by = r.StartedBy,
        source = r.Source,
        total = r.Total,
        passed = r.Passed,
        failed = r.Failed,
        skipped = r.Skipped,
        duration_ms = r.DurationMs,
        created_at = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        updated_at = DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc),
    };
}
