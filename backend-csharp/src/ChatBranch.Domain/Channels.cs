namespace ChatBranch.Domain;

// Provider accounts, runtime settings, and the unified-inbox thread/message
// model (Django app: crm). Setting is the entity-scoped config store — see
// ScopingService for the resolution rule that MUST guard every read.

public class ChannelAccount : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }               // null = global (org-wide)
    public Entity? Entity { get; set; }
    public string Channel { get; set; } = "";        // whatsapp|facebook|instagram|telegram|email
    public string Provider { get; set; } = "cloud";  // cloud|baileys
    public string Name { get; set; } = "";
    public string ExternalId { get; set; } = "";     // phone_number_id / page id
    public string WabaId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string ApiVersion { get; set; } = "v23.0";
    public string Owner { get; set; } = "admin";
    public Dictionary<string, object?> Config { get; set; } = new();

    public string HealthStatus { get; set; } = "unknown";
    public string HealthDetail { get; set; } = "";
    public DateTime? LastOkAt { get; set; }
    public DateTime? LastInboundAt { get; set; }
    public int FailStreak { get; set; }
    public DateTime? HealthChangedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<Thread> Threads { get; set; } = new List<Thread>();
}

// Runtime config override. entity == null is the ORGANIZATION-wide value; a row
// WITH an entity overrides it for that entity only. Partial unique constraints
// (one row per (key, entity); exactly one global row per key) are configured in
// AppDbContext and MUST be honoured — a bare read on key alone leaks another
// entity's override into the global value.
public class Setting : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class Thread : IEntityScoped
{
    public int Id { get; set; }
    public string Channel { get; set; } = "";
    public string Key { get; set; } = "";            // wa_id / email / PSID / IGSID / TG chat id
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "open";
    public string Assignee { get; set; } = "";
    public List<string> Labels { get; set; } = new();
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? AccountId { get; set; }
    public ChannelAccount? Account { get; set; }
    // Denormalized entity, recomputed by ScopingService.SyncThreadEntity at
    // every seam that moves a thread.
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public bool AiEnabled { get; set; } = true;
    // Per-sending-account chatbot ticks: {account_pk(str): bool}.
    public Dictionary<string, object?> AiOverrides { get; set; } = new();
    public bool OptedOut { get; set; }

    public string CallPermission { get; set; } = "none";
    public string? CallId { get; set; }
    public string CallStatus { get; set; } = "idle";
    public string? CallRoom { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}

public class Message
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }
    public int? AccountId { get; set; }
    public ChannelAccount? Account { get; set; }
    public string ExternalId { get; set; } = "";     // wamid/mid/smtp/tg id
    public string Direction { get; set; } = "";       // in|out
    public string Source { get; set; } = "";          // customer|agent|ai|system
    public bool Private { get; set; }
    public string Text { get; set; } = "";
    public string Status { get; set; } = "sent";
    public string MediaType { get; set; } = "";
    public string MediaPath { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AccountAssignment
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string Channel { get; set; } = "whatsapp";
    public int AccountId { get; set; }
    public ChannelAccount? Account { get; set; }
    public bool Pinned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
