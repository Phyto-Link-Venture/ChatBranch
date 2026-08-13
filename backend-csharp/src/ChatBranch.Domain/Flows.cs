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
    public string WaitFor { get; set; } = "";         // '' | reply | payment
    public Dictionary<string, object?> Context { get; set; } = new();
    public List<object?> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

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
