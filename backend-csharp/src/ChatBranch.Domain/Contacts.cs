namespace ChatBranch.Domain;

// The identity + business-object side of the platform (Django app: crm).
// Contact = a person we talk to. Record = the business object a conversation is
// about (order/invoice/booking/ticket). How a contact relates to the business
// (boss, employee, driver…) is expressed with registry Tags (see Tags.cs).

public class Contact
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";          // E.164
    public string Email { get; set; } = "";
    public string Language { get; set; } = "en";     // auto|en|ms|zh
    public string Status { get; set; } = "pending";
    public string Method { get; set; } = "ai-call";
    public string CompanyName { get; set; } = "";
    public bool DoNotCall { get; set; }
    public string Notes { get; set; } = "";

    public string ExternalId { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object?> Attributes { get; set; } = new();

    // AI memory (durable, learned from conversations).
    public string PreferredContactTime { get; set; } = "";
    public string CommunicationStyle { get; set; } = "";
    public string SentimentTrend { get; set; } = "";
    public string AiProfileSummary { get; set; } = "";
    /// <summary>"" follow the chat/inbox · "never" the AI never answers this person ·
    /// "always" the AI answers even where the inbox default is off (Ai/AiEligibility).</summary>
    public string AiMode { get; set; } = "";

    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ContactEntity> EntityLinks { get; set; } = new List<ContactEntity>();
    public ICollection<Record> Records { get; set; } = new List<Record>();
    public ICollection<Thread> Threads { get; set; } = new List<Thread>();
}

public class ContactEntity
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string ExternalRef { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Record
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Type { get; set; } = "custom";     // order|shipment|invoice|booking|ticket|custom
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "open";
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = "MYR";
    public DateOnly? DueDate { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<RecordEvent> Events { get; set; } = new List<RecordEvent>();
}

public class RecordEvent
{
    public int Id { get; set; }
    public int RecordId { get; set; }
    public Record? Record { get; set; }
    public string Kind { get; set; } = "note";       // status|payment|note|scan
    public DateTime At { get; set; }
    public decimal? Amount { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
