namespace ChatBranch.Domain;

// Flow execution state + the outbound pacing queue (Django app: crm; engine in
// flows/). FlowRun = one live traversal of a Workflow for one contact.
// OutboundJob = a paced outbound send/dial claimed atomically by the queue
// worker.

public class FlowRun
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string TriggerKey { get; set; } = "";
    public string Status { get; set; } = "running";  // running|waiting|done|failed|cancelled
    public string CurrentNode { get; set; } = "";
    public DateTime? WaitUntil { get; set; }
    public string WaitFor { get; set; } = "";         // '' | reply | record
    public Dictionary<string, object?> Context { get; set; } = new();
    public List<object?> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Why the run ended: the End step's outcome code, "end of path",
    /// or the failure reason ("node x not in workflow", "stalled mid-step"…).</summary>
    public string Detail { get; set; } = "";

    public ICollection<OutboundJob> OutboundJobs { get; set; } = new List<OutboundJob>();
}

public class OutboundJob
{
    public int Id { get; set; }
    public string Channel { get; set; } = "whatsapp";
    public string Kind { get; set; } = "text";        // text|template|ai_reply|call
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string ToAddress { get; set; } = "";
    public Dictionary<string, object?> Payload { get; set; } = new();
    public int? AccountId { get; set; }
    public ChannelAccount? Account { get; set; }
    public int? FlowRunId { get; set; }
    public FlowRun? FlowRun { get; set; }
    public string FlowNode { get; set; } = "";
    public string Status { get; set; } = "queued";    // queued|sent|failed|cancelled
    public int Attempts { get; set; }
    public DateTime? NotBefore { get; set; }           // pacing gate
    public string LastError { get; set; } = "";
    public int? SentMessageId { get; set; }
    public Message? SentMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>
/// One step of one run, as it happened: a run ENTERED a node, COMPLETED it,
/// parked WAITING at it, FAILED at it, SKIPPED part of it (a channel with no
/// address…), or sent a FOLLOWUP from it. Append-only and cheap — the engine
/// writes one row per transition and never lets a failed insert break a run.
/// Read back per flow per day ("how many people reached each step") by the
/// stats/runs endpoints. <see cref="Day"/> is the organization-local calendar
/// day, so "today" means the operator's today, not UTC's.
/// </summary>
public class FlowStepEvent
{
    public int Id { get; set; }
    /// <summary>The workflow's FlowId (the builder's string id, e.g. "wf-…").</summary>
    public string WorkflowId { get; set; } = "";
    public int RunId { get; set; }
    public int? ContactId { get; set; }
    public string NodeId { get; set; } = "";
    public string Kind { get; set; } = "";   // see FlowStepKind
    public string Detail { get; set; } = "";
    public DateTime At { get; set; }
    public string Day { get; set; } = "";    // yyyy-MM-dd, org time
}

public static class FlowStepKind
{
    public const string Entered = "entered";
    public const string Completed = "completed";
    public const string Waiting = "waiting";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    /// <summary>A "Follow up if no reply" step sent one of its follow-ups.</summary>
    public const string FollowUp = "followup";
}
