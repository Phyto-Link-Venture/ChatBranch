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

/// <summary>
/// "Request access" (crm_contactaccessrequest): a user bound to a branch tried to
/// add a phone / email that already belongs to a contact of their company under a
/// branch they can't see, and asked an admin to share that contact with
/// <see cref="EntityId"/>. Approving links the contact there (a
/// <see cref="ContactEntity"/> row; its other entities stay). At most one PENDING
/// request per (contact, entity).
/// </summary>
public class ContactAccessRequest
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    /// <summary>The branch the requester wants the contact added to.</summary>
    public int EntityId { get; set; }
    public string RequestedBy { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>See <see cref="ContactAccessStatus"/>.</summary>
    public string Status { get; set; } = ContactAccessStatus.Pending;
    public string DecidedBy { get; set; } = "";
    /// <summary>A denial's reason (shown to the requester).</summary>
    public string Reason { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ContactAccessStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
    public static readonly string[] All = { Pending, Approved, Denied, Cancelled };
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
