namespace ChatBranch.Domain;

// AI operations: the human-in-the-loop monitor (AiDraft) and the per-thread
// follow-up state (AiConversationState). Every AI-written outbound passes the
// AiGate, which records one AiDraft row whether it sent the message straight
// away (auto_sent) or held it for a person — so the monitor is ALSO the
// complete trail of what the AI said and did.

public static class AiDraftStatus
{
    /// <summary>Held for a person to approve, edit or reject.</summary>
    public const string Pending = "pending";
    /// <summary>A person approved it unchanged; it went through the send path.</summary>
    public const string Approved = "approved";
    /// <summary>A person edited it, then sent it (original_content keeps the AI's text).</summary>
    public const string Edited = "edited";
    public const string Rejected = "rejected";
    /// <summary>Nobody acted before the auto-send deadline, so it went out on its own.</summary>
    public const string Sent = "sent";
    /// <summary>The review policy did not hold it — sent immediately.</summary>
    public const string AutoSent = "auto_sent";
    /// <summary>Superseded: the customer wrote again (or a teammate answered) first.</summary>
    public const string Expired = "expired";
    /// <summary>A send-time guard refused it (opt-out, closed window, no linked contact…).</summary>
    public const string Failed = "failed";
    /// <summary>Shadow mode: written, recorded, never sent.</summary>
    public const string Shadow = "shadow";

    public static readonly string[] All =
        { Pending, Approved, Edited, Rejected, Sent, AutoSent, Expired, Failed, Shadow };
}

public static class AiDraftKind
{
    public const string Reply = "reply";
    public const string FollowUp = "follow_up";
    public const string FlowMessage = "flow_message";

    public static readonly string[] All = { Reply, FollowUp, FlowMessage };
}

public class AiDraft : IEntityScoped
{
    public int Id { get; set; }
    public int? ThreadId { get; set; }
    /// <summary>Denormalized so the chat banner can filter on "&lt;channel&gt;:&lt;key&gt;".</summary>
    public string ThreadKey { get; set; } = "";
    public string Channel { get; set; } = "";
    public int? ContactId { get; set; }
    /// <summary>The thread's entity at draft time — the monitor list scopes on it.</summary>
    public int? EntityId { get; set; }
    public int? AccountId { get; set; }
    public string Kind { get; set; } = AiDraftKind.Reply;
    public string Content { get; set; } = "";
    /// <summary>The AI's own text when a person edited it before sending ("" otherwise).</summary>
    public string OriginalContent { get; set; } = "";
    /// <summary>What the reviewer needs to judge it: the customer messages being
    /// answered, tools + results, checks + verdicts, model + tokens, flow refs,
    /// and why it was held.</summary>
    public Dictionary<string, object?> Context { get; set; } = new();
    public string Status { get; set; } = AiDraftStatus.Pending;
    /// <summary>Plain-language reasons it was held ("" when sent straight away).</summary>
    public string HoldReason { get; set; } = "";
    public string DecidedBy { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
    public string RejectReason { get; set; } = "";
    /// <summary>Auto-approve deadline (null = wait for a person).</summary>
    public DateTime? SendAfter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int? SentMessageId { get; set; }
    public string Error { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Follow-up bookkeeping for one conversation. Counting restarts whenever the
/// customer writes again (<see cref="AnchorMessageId"/> = the latest inbound the
/// count belongs to), so a reply always resets the sequence.
/// </summary>
public class AiConversationState
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public int FollowUpsSent { get; set; }
    public DateTime? LastFollowUpAt { get; set; }
    public DateTime? NextFollowUpAt { get; set; }
    public int? AnchorMessageId { get; set; }
    /// <summary>Why follow-ups stopped or were postponed (shown to operators).</summary>
    public string Note { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
