namespace ChatBranch.Domain;

// The on-demand AI reviewer ("AI to monitor AI"): an admin clicks "Review now",
// a stronger model reads a sample of the AI's past replies and returns a
// verdict per reply plus suggestions for the team's setup. Nothing runs on its
// own. One AiReviewRun per click (the last 50 are kept), one AiReviewFinding
// per reviewed reply.

public static class AiReviewStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class AiReviewMode
{
    /// <summary>A model reviewed the replies.</summary>
    public const string Model = "model";
    /// <summary>No model (or it failed): deterministic checks only — and the result says so.</summary>
    public const string Heuristic = "heuristic";
}

public static class AiReviewVocabulary
{
    public static readonly string[] Verdicts = { "good", "issue" };
    public static readonly string[] Severities = { "low", "medium", "high" };
    public static readonly string[] Categories =
    {
        "wrong_info", "unsupported_claim", "missed_question", "tone", "too_long", "should_handover",
        "privacy", "policy_violation", "language_mismatch", "other",
    };
    public static readonly string[] FixTypes =
        { "knowledge", "house_rule", "lesson", "persona", "review_setting", "none" };
}

public class AiReviewRun
{
    public int Id { get; set; }
    /// <summary>Username of the person who clicked "Review now".</summary>
    public string CreatedBy { get; set; } = "";
    /// <summary>The picked entity (first pick) — the level house rules are added to
    /// and the AI bill's company. Null = organization-wide (or the caller's whole scope).</summary>
    public int? EntityId { get; set; }
    /// <summary>Every entity whose replies the run could read (the scope it ran
    /// with). Empty = unrestricted. A caller sees the run only when this sits in their scope.</summary>
    public List<int> EntityIds { get; set; } = new();
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int Limit { get; set; }
    /// <summary>model | heuristic.</summary>
    public string Mode { get; set; } = AiReviewMode.Heuristic;
    public string Provider { get; set; } = "";
    /// <summary>The model that reviewed ("" in heuristic mode).</summary>
    public string Model { get; set; } = "";
    /// <summary>The reviewer the person picked for THIS run, as "provider:model"
    /// ("" = the default: Setting <c>ai_reviewer_model</c> → env → built-in).
    /// <see cref="Provider"/> / <see cref="Model"/> say what actually reviewed.</summary>
    public string RequestedModel { get; set; } = "";
    /// <summary>running | done | failed.</summary>
    public string Status { get; set; } = AiReviewStatus.Running;
    /// <summary>Plain-language notes (fallbacks taken, batches that failed…).</summary>
    public string Note { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>JSON: {summary, top_problems:[{problem, count}], suggestions:[{type, text, why}]}.</summary>
    public string Summary { get; set; } = "{}";
    public int Reviewed { get; set; }
    public int Issues { get; set; }
    public int HighSeverity { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double? CostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AiReviewFinding
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public int? DraftId { get; set; }
    public int? ThreadId { get; set; }
    public string Channel { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    /// <summary>The conversation's entity (the draft's) — what scopes the finding.</summary>
    public int? EntityId { get; set; }
    public int? ContactId { get; set; }
    /// <summary>The draft's status when reviewed (auto_sent, approved, edited, rejected, sent).</summary>
    public string DraftStatus { get; set; } = "";
    /// <summary>good | issue.</summary>
    public string Verdict { get; set; } = "good";
    /// <summary>low | medium | high ("" for a good reply).</summary>
    public string Severity { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public string Explanation { get; set; } = "";
    /// <summary>The knowledge item / rule the reviewer cited ("" = none).</summary>
    public string Evidence { get; set; } = "";
    /// <summary>knowledge | house_rule | lesson | persona | review_setting | none.</summary>
    public string FixType { get; set; } = "none";
    public string FixText { get; set; } = "";
    /// <summary>The customer's latest message(s) before the reply (clipped).</summary>
    public string CustomerExcerpt { get; set; } = "";
    /// <summary>The AI's own reply text (clipped).</summary>
    public string ReplyExcerpt { get; set; } = "";
    /// <summary>model | heuristic — who judged this one.</summary>
    public string Source { get; set; } = AiReviewMode.Model;
    public bool Applied { get; set; }
    /// <summary>lesson | knowledge | house_rule.</summary>
    public string AppliedAs { get; set; } = "";
    /// <summary>"lesson:12", "knowledge:4", "house_rule".</summary>
    public string AppliedRef { get; set; } = "";
    public string AppliedBy { get; set; } = "";
    public DateTime? AppliedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
