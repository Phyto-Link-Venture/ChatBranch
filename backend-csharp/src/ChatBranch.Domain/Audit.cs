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
}
