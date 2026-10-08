namespace ChatBranch.Domain;

// Audit trail + per-user notifications (Django app: crm).

public class AuditLog
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    public string Actor { get; set; } = "system";   // username | system | webhook
    public string Action { get; set; } = "";         // login, settings.update, …
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    // ---- provenance (activity log) -----------------------------------------
    /// <summary>Who drove the action: user | api | mcp | assistant | automation |
    /// flow | webhook | system (<see cref="AuditSources"/>).</summary>
    public string Source { get; set; } = "";
    /// <summary>The outlet the action belongs to, when known (no FK on purpose:
    /// the trail must outlive a deleted entity).</summary>
    public int? EntityId { get; set; }
    /// <summary>HTTP method + path (never the query string) of the request that
    /// caused the row; empty for background writers.</summary>
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>The response status of that request (null for background rows).</summary>
    public int? Status { get; set; }
    public string UserAgent { get; set; } = "";
    public string RequestId { get; set; } = "";
}

/// <summary>The closed vocabulary of <see cref="AuditLog.Source"/>.</summary>
public static class AuditSources
{
    public const string User = "user";
    public const string Api = "api";
    public const string Mcp = "mcp";
    public const string Assistant = "assistant";
    public const string Automation = "automation";
    public const string Flow = "flow";
    public const string Webhook = "webhook";
    public const string System = "system";

    public static readonly string[] All =
        { User, Api, Mcp, Assistant, Automation, Flow, Webhook, System };
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Kind { get; set; } = "system";     // message | call | system
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Link { get; set; } = "";
    public bool Read { get; set; }
    public DateTime CreatedAt { get; set; }
    // Inbox grouping + mute group: messages | account_health | flows | admin |
    // system ("" on rows written before the column existed → derived from Kind).
    public string Category { get; set; } = "";
    // Throttle key ("thread:<id>"): at most one UNREAD row per key per user.
    public string RefKey { get; set; } = "";
}
