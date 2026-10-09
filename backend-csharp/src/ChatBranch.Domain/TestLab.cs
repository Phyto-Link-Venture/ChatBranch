namespace ChatBranch.Domain;

/// <summary>
/// One Test lab run (crm_testlabrun): a batch of scripted scenarios that ran
/// end to end through the real pipelines, started from the /test-lab page, the
/// <c>npm run test:scenarios</c> CLI, the API or the xUnit suite. The page runs
/// scenarios one at a time and appends each result to the same row, so a
/// "Run all" is ONE history entry. Only the last 50 runs are kept.
/// </summary>
public class TestLabRun
{
    public int Id { get; set; }
    /// <summary>The superadmin who started it (username).</summary>
    public string StartedBy { get; set; } = "";
    /// <summary>page | cli | api | test — where the run was started from.</summary>
    public string Source { get; set; } = "api";
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    /// <summary>JSON array of scenario results (id, title, passed, checks, links, duration…).</summary>
    public string Results { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
