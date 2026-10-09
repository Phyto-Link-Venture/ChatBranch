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
    /// <summary>
    /// What this reply's app tools RESERVED (a calendar booking to make, a
    /// booking to cancel). Nothing is made until the reply is delivered: the AI
    /// gate confirms them on delivery and releases them when the reply is
    /// rejected, superseded, expired or shadowed (see Bookings.BookingService).
    /// </summary>
    public IReadOnlyList<AiPendingAction> PendingActions { get; init; } = Array.Empty<AiPendingAction>();
    /// <summary>The knowledge items / chunks and lessons the prompt carried (retrieval).</summary>
    public IReadOnlyList<AiGrounding> Grounding { get; init; } = Array.Empty<AiGrounding>();

    /// <summary>A reply check asked for a human (or retries ran out).</summary>
    public bool HandoverRequested => Action == AiActions.Handover;

    /// <summary>
    /// The model itself called <c>request_handover</c>: the reply still goes out
    /// (it tells the customer a person will follow up) and the gate flags the
    /// conversation for a person — the structured twin of
    /// <see cref="Humanizer.HandsOver"/>'s wording match.
    /// </summary>
    public bool HandoverAsked { get; init; }
    /// <summary>The reason the model gave with <c>request_handover</c>.</summary>
    public string HandoverReason { get; init; } = "";

    public bool ShouldSend => Action == AiActions.Send && !string.IsNullOrWhiteSpace(Text);

    public static AiReplyResult Failed(string feature, string reason) => new()
    {
        Action = AiActions.Error, Reason = reason, Feature = feature,
    };

    /// <summary>Compact, JSON-friendly audit shape (queue payloads, usage rows, API).
    /// <c>pending_actions</c> ([{type, booking_id}]) only when the reply reserved something.</summary>
    public Dictionary<string, object?> ToMeta()
    {
        var meta = BaseMeta();
        if (PendingActions.Count > 0) meta["pending_actions"] = AiPendingAction.ToMeta(PendingActions);
        return meta;
    }

    private Dictionary<string, object?> BaseMeta() => new()
    {
        ["feature"] = Feature,
        ["action"] = Action,
        ["reason"] = Reason,
        ["skill_id"] = SkillId,
        ["retries"] = Retries,
        ["handover"] = HandoverRequested || HandoverAsked,
        ["handover_asked"] = HandoverAsked,
        ["handover_reason"] = HandoverReason,
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

/// <summary>
/// Something an app tool reserved while the AI wrote a reply — made when the
/// reply is delivered, released when it is not. Travels on
/// <see cref="AiReplyResult.PendingActions"/> and, through the draft context,
/// as <c>pending_actions: [{type, booking_id}]</c>.
/// </summary>
public sealed record AiPendingAction(string Type, int BookingId)
{
    public const string Book = "book";
    public const string Cancel = "cancel";

    public static List<Dictionary<string, object?>> ToMeta(IEnumerable<AiPendingAction> actions) =>
        actions.Select(a => new Dictionary<string, object?> { ["type"] = a.Type, ["booking_id"] = a.BookingId })
            .ToList();

    /// <summary>The actions recorded in a draft / job context (CLR objects or JSON).</summary>
    public static List<AiPendingAction> From(Dictionary<string, object?>? ctx)
    {
        var list = new List<AiPendingAction>();
        if (ctx == null) return list;
        try
        {
            foreach (var item in AiGate.Items(ctx, "pending_actions"))
            {
                var type = AiGate.Str(item, "type", "");
                if (type is not (Book or Cancel) || AiGate.Num(item, "booking_id") is not double id) continue;
                list.Add(new AiPendingAction(type, (int)id));
            }
        }
        catch { /* a malformed context reserves nothing */ }
        return list.Distinct().ToList();
    }
}
