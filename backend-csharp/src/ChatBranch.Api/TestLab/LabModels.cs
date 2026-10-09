using System.Text.Json.Serialization;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// One scripted use case: what it proves, the steps it takes, what should
/// happen — and the code that does it through the real pipelines
/// (<see cref="Run"/>). <see cref="NeedsAiKey"/> = its strongest checks need a
/// real language model; without a key it runs its deterministic variant.
/// </summary>
public sealed record LabScenario(
    string Id,
    string Category,
    string Title,
    string Proves,
    string[] Steps,
    string[] Expect,
    Func<LabContext, Task> Run,
    bool NeedsAiKey = false);

/// <summary>The categories, in the order the page and the CLI list them.</summary>
public static class LabCategories
{
    public static readonly (string Id, string Label, string Covers)[] All =
    {
        ("ai", "AI replies", "The chatbot answering from the knowledge base, and every rule that keeps it quiet."),
        ("review", "AI monitor", "Review policies: held drafts, approve / reject, shadow mode, risky replies."),
        ("handover", "Human handover", "When the AI hands a conversation to a person, and how it gets it back."),
        ("identity", "Unknown customers", "Asking unknown customers who they are, matching, creating, invite links."),
        ("compliance", "Compliance", "STOP / START opt-outs and the cold-text guard."),
        ("routing", "Routing & delivery", "Business-number redirects, failover off a banned number, quiet hours."),
        ("access", "Scoping & access", "Branch scoping for agents and the read gates of a role with no access."),
        ("automation", "Flows & automations", "Flows on contact created / message received, automation rules, round-robin."),
        ("contacts", "Contacts & data", "CSV import, tag groups, template variables."),
        ("booking", "Bookings", "Calendar booking through an AI skill: offer, book, cancel, release."),
        ("platform", "Platform", "Ask ChatBranch history, language preference, the website widget."),
    };
}

public sealed record LabCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record LabLink(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url);

/// <summary>The outcome of one scenario.</summary>
public sealed class LabResult
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("passed")] public bool Passed { get; set; }
    [JsonPropertyName("skipped")] public bool Skipped { get; set; }
    [JsonPropertyName("skip_reason")] public string SkipReason { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("checks")] public List<LabCheck> Checks { get; set; } = new();
    [JsonPropertyName("links")] public List<LabLink> Links { get; set; } = new();
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    /// <summary>"lab" (the deterministic test brain), "configured" (the real model) or "" (no AI).</summary>
    [JsonPropertyName("brain")] public string Brain { get; set; } = "";
    [JsonPropertyName("ran_at")] public DateTime RanAt { get; set; }
}

/// <summary>Thrown by <see cref="LabContext.Skip"/>: the environment can't run this scenario.</summary>
public sealed class LabSkipException : Exception
{
    public LabSkipException(string reason) : base(reason) { }
}
