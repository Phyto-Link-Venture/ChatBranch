namespace ChatBranch.Domain;

// Django stores these as short strings. We keep the exact wire values as
// constants (not C# enums) so the DB column round-trips byte-for-byte and the
// Next.js frontend contract is unchanged. Group them by the model they belong
// to for readability.

public static class Roles
{
    public const string SuperAdmin = "superadmin";
    public const string Admin = "admin";
    public const string Agent = "agent";
}

public static class Channels
{
    public const string WhatsApp = "whatsapp";
    public const string Facebook = "facebook";
    public const string Instagram = "instagram";
    public const string Telegram = "telegram";
    public const string Email = "email";
    /// <summary>Embeddable site widget — the visitor's browser is the transport.</summary>
    public const string Website = "website";
}

public static class Providers
{
    public const string Cloud = "cloud";
    public const string Baileys = "baileys";
}

public static class Health
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Disconnected = "disconnected";
    public const string Banned = "banned";
}

public static class ThreadStatus
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string Snoozed = "snoozed";
    public const string Resolved = "resolved";
}

public static class MessageDirection
{
    public const string In = "in";
    public const string Out = "out";
}

public static class MessageSource
{
    public const string Customer = "customer";
    public const string Agent = "agent";
    public const string Ai = "ai";
    public const string System = "system";
}

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class JobKind
{
    public const string Text = "text";
    public const string Template = "template";
    public const string AiReply = "ai_reply";
    public const string Call = "call";
}

public static class FlowRunStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
