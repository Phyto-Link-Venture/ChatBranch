namespace ChatBranch.Domain;

// AI brain persistence + call outcomes (Django app: crm; logic in aicore/).
// CallRecord mirrors the voice agent's file-based records. LlmUsage is the
// metering ledger. PendingToolAction is the AI tool double-confirm state.
// ConversationSummary is the durable memory the prompt builder reads back.

public class CallRecord
{
    public int Id { get; set; }
    public string CallId { get; set; } = "";
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string Phone { get; set; } = "";
    public string Name { get; set; } = "";
    public string StartedAt { get; set; } = "";       // free-text timestamps from the agent
    public string EndedAt { get; set; } = "";
    public string Transcript { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Outcome { get; set; } = "unclear";
    public string? FollowupDate { get; set; }
    public bool NeedsHuman { get; set; }
}

public class LlmUsage
{
    public int Id { get; set; }
    public string Feature { get; set; } = "";         // chat_reply|summary|timeline|playground|voice_call
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Channel { get; set; } = "";
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    /// <summary>The outlet the call was made for (the AI bill's company = its top-level
    /// entity). Null = not known at write time — the bill falls back to the contact's entity.</summary>
    public int? EntityId { get; set; }
    public string Ref { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int AudioPromptTokens { get; set; }
    public int AudioCompletionTokens { get; set; }
    public Dictionary<string, object?> Detail { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class PendingToolAction
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string Channel { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Fingerprint { get; set; } = "";     // canonical JSON of the args
    public string Session { get; set; } = "";
    public DateTime AskedAt { get; set; }
}

public class ConversationSummary
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string Source { get; set; } = "";          // chat|call
    public string Channel { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<object?> Facts { get; set; } = new();
    public string Sentiment { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
