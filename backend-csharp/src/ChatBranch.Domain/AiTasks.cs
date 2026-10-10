namespace ChatBranch.Domain;

// Background AI tasks ("silent AI workers"): an AI that keeps reading the
// customers' inbound messages and acts through tools — store an invoice in a
// Google Sheet, file the attachment in Drive, post it to another app — but
// NEVER writes to the customer. One AiTask per definition (a multi-entity
// library row, like skills), one AiTaskRun per (task, inbound message).

/// <summary>The attachment condition of a task's trigger.</summary>
public static class AiTaskAttachments
{
    /// <summary>No condition — any message, with or without a file.</summary>
    public const string None = "";
    public const string Any = "any";
    public const string Pdf = "pdf";
    public const string Image = "image";
    public static readonly string[] All = { None, Any, Pdf, Image };
}

/// <summary>The linked-contact condition of a task's trigger.</summary>
public static class AiTaskLinked
{
    /// <summary>No condition.</summary>
    public const string Any = "";
    public const string Yes = "yes";
    public const string No = "no";
    public static readonly string[] All = { Any, Yes, No };
}

public static class AiTaskRunStatus
{
    /// <summary>Waiting for its due time (the attachment may still be downloading).</summary>
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Ok = "ok";
    public const string Failed = "failed";
    /// <summary>Not run: the AI switch is off, the quota is used up, no model, or the message was not relevant.</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// One background AI task (<c>crm_aitask</c>). "Available to" follows the
/// multi-entity library rule (<see cref="IMultiEntityItem"/>): <see cref="EntityId"/>
/// is the primary entity, further ticks are <c>crm_libraryentity</c> rows
/// (kind <c>ai_task</c>); none = every conversation of the organization.
/// </summary>
public class AiTask : IMultiEntityItem
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Channels whose inbound messages it reads; empty = every channel.</summary>
    public List<string> Channels { get; set; } = new();
    /// <summary>What starts it. Today: <c>inbound_message</c> (a customer's message, after it is stored).</summary>
    public string Trigger { get; set; } = "inbound_message";
    /// <summary>"" (no condition) | any | pdf | image — see <see cref="AiTaskAttachments"/>.</summary>
    public string Attachment { get; set; } = "";
    /// <summary>The message must contain ANY of these words (case-insensitive); empty = no condition.</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>"" | yes | no — the conversation must (not) be linked to a contact.</summary>
    public string Linked { get; set; } = "";
    /// <summary>What to look for and what to do, in the operator's own words.</summary>
    public string Instructions { get; set; } = "";
    /// <summary>The tools it may call (ids from the catalog: <c>sheets_append_row</c>,
    /// <c>mcp:&lt;server&gt;__&lt;tool&gt;</c>…). Nothing outside this list is ever offered.</summary>
    public List<string> Tools { get; set; } = new();
    /// <summary>Per-tool settings (spreadsheet_id, sheet_range, drive_folder_id,
    /// webhook_url, webhook_secret — the secret only ever leaves masked).</summary>
    public Dictionary<string, object?> Config { get; set; } = new();
    public int RunCount { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One run of a task on one inbound message (<c>crm_aitaskrun</c>, unique per (task, message)).</summary>
public class AiTaskRun
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int ThreadId { get; set; }
    public int MessageId { get; set; }
    /// <summary>The conversation's entity when the run was queued (scopes who may read it).</summary>
    public int? EntityId { get; set; }
    public string Channel { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    /// <summary>pending | running | ok | failed | skipped (<see cref="AiTaskRunStatus"/>).</summary>
    public string Status { get; set; } = AiTaskRunStatus.Pending;
    public DateTime DueAt { get; set; }
    /// <summary>JSON object: what the AI extracted from the message.</summary>
    public string Extracted { get; set; } = "{}";
    /// <summary>JSON list: [{tool, args, result, ok}] — secrets redacted, capped.</summary>
    public string Steps { get; set; } = "[]";
    /// <summary>The one-sentence outcome (also the private note's text).</summary>
    public string Summary { get; set; } = "";
    public string Error { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double? CostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
