namespace ChatBranch.Data.Ai;

/// <summary>What the generator decided for a draft (wire values).</summary>
public static class AiActions
{
    /// <summary>Text is ready to send.</summary>
    public const string Send = "send";
    /// <summary>Checks kept failing: a human must answer. Text is empty.</summary>
    public const string Handover = "handover";
    /// <summary>A check blocked the draft outright. Text is empty.</summary>
    public const string Block = "block";
    /// <summary>The model produced nothing. Text is empty.</summary>
    public const string Empty = "empty";
    /// <summary>Generation failed (exception swallowed). Text is empty.</summary>
    public const string Error = "error";
}

/// <summary>One tool the model called while writing the reply.</summary>
public sealed record AiToolCall(string Name, string Args, string Result, bool Ok);

/// <summary>One reply check run against one draft (attempt 0 = the first draft).</summary>
public sealed record AiCheckRun(string SkillId, string Name, bool Ok, string Reason, string Fix,
    string? Error, int Attempt);

/// <summary>
/// The result of an AI generation — reply, flow message or follow-up. The
/// generator NEVER sends: the caller decides, using <see cref="ShouldSend"/>
/// (and, for a review gate, the metadata). <see cref="Text"/> is empty whenever
/// nothing may be sent; <see cref="Draft"/> keeps the last thing the model
/// wrote, so a reviewer can see what was held back and why.
/// </summary>
public sealed record AiReplyResult
{
    public string Text { get; init; } = "";
    public string Draft { get; init; } = "";
    public string Action { get; init; } = AiActions.Send;
    public string Reason { get; init; } = "";
    public int Retries { get; init; }
    public IReadOnlyList<AiToolCall> Tools { get; init; } = Array.Empty<AiToolCall>();
    public IReadOnlyList<AiCheckRun> Checks { get; init; } = Array.Empty<AiCheckRun>();
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    /// <summary>chat_reply | flow_message | follow_up | flow_static.</summary>
    public string Feature { get; init; } = "chat_reply";
    /// <summary>The skill a flow step composed with (SkillId), if any.</summary>
    public string? SkillId { get; init; }
    /// <summary>False when the text is not AI-written (a flow step's static script).</summary>
    public bool Composed { get; init; } = true;
    /// <summary>The knowledge items / chunks and lessons the prompt carried (retrieval).</summary>
    public IReadOnlyList<AiGrounding> Grounding { get; init; } = Array.Empty<AiGrounding>();

    /// <summary>A reply check asked for a human (or retries ran out).</summary>
    public bool HandoverRequested => Action == AiActions.Handover;

    public bool ShouldSend => Action == AiActions.Send && !string.IsNullOrWhiteSpace(Text);

    public static AiReplyResult Failed(string feature, string reason) => new()
    {
        Action = AiActions.Error, Reason = reason, Feature = feature,
    };

    /// <summary>Compact, JSON-friendly audit shape (queue payloads, usage rows, API).</summary>
    public Dictionary<string, object?> ToMeta() => new()
    {
        ["feature"] = Feature,
        ["action"] = Action,
        ["reason"] = Reason,
        ["skill_id"] = SkillId,
        ["retries"] = Retries,
        ["handover"] = HandoverRequested,
        ["provider"] = Provider,
        ["model"] = Model,
        ["prompt_tokens"] = PromptTokens,
        ["completion_tokens"] = CompletionTokens,
        ["grounding"] = Grounding.Select(g => g.ToMeta()).ToList(),
        ["tools"] = Tools.Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name, ["args"] = t.Args, ["result"] = t.Result, ["ok"] = t.Ok,
        }).ToList(),
        ["checks"] = Checks.Select(c => new Dictionary<string, object?>
        {
            ["skill_id"] = c.SkillId, ["name"] = c.Name, ["ok"] = c.Ok, ["reason"] = c.Reason,
            ["fix"] = c.Fix, ["error"] = c.Error, ["attempt"] = c.Attempt,
        }).ToList(),
    };
}
