namespace ChatBranch.Domain;

// Contact identities, invite links and "who are you?" requests — the three
// ways a person's channels get bound to their contact record without anyone
// typing an id in by hand.

/// <summary>
/// A channel address registered for a contact WITHOUT a conversation existing
/// yet (crm_contactidentity): the WhatsApp number, voice line, SMS number and
/// email address discovered from the contact's own phone/email. Threads stay
/// the record of conversations; this is the registry of where a contact CAN be
/// reached, plus the WhatsApp lookup verdict (cached 30 days). A registry row
/// never shows up in the inbox — a thread is created the moment a message
/// actually flows (inbound phone/email matching links it automatically).
/// </summary>
public class ContactIdentity
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    /// <summary>whatsapp | voice | sms | email (any thread channel id).</summary>
    public string Channel { get; set; } = "";
    /// <summary>Digits (whatsapp/sms — a wa_id), E.164 (voice), lower-cased address (email).</summary>
    public string Address { get; set; } = "";
    /// <summary>auto (discovered from the contact) | import | manual | invite | self.</summary>
    public string Source { get; set; } = "auto";
    /// <summary>WhatsApp only: pending | verified | not_on_whatsapp | unknown ("" elsewhere).</summary>
    public string Verification { get; set; } = "";
    public string VerificationDetail { get; set; } = "";
    public DateTime? CheckedAt { get; set; }
    public int? CheckedAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class IdentityVerdicts
{
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string NotOnWhatsApp = "not_on_whatsapp";
    public const string Unknown = "unknown";
}

/// <summary>
/// A short invite code that binds the conversation a customer opens on another
/// app (Telegram, Messenger, a second WhatsApp number…) to an existing contact
/// (crm_contactinvite). Single contact, 30-day expiry. A code made for one
/// channel binds once; a code made for "any channel" binds once PER channel
/// (<see cref="UsedChannels"/>).
/// </summary>
public class ContactInvite
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public int ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? EntityId { get; set; }
    /// <summary>The channel the invite was made for, or "" = any channel.</summary>
    public string Channel { get; set; } = "";
    public int? AccountId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public int? UsedThreadId { get; set; }
    /// <summary>Comma list of the channels this code has bound (an any-channel code binds each once).</summary>
    public string UsedChannels { get; set; } = "";
}

/// <summary>
/// The "who are you?" exchange with an unknown customer (crm_identityrequest,
/// one row per thread): a deterministic state machine — ask (max 2), parse
/// name / phone / email out of the replies, then link, create, or hand over.
/// </summary>
public class IdentityRequest
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }
    /// <summary>See <see cref="IdentityStates"/>.</summary>
    public string State { get; set; } = IdentityStates.Asking;
    /// <summary>ask | ask_and_create.</summary>
    public string Mode { get; set; } = "ask";
    public int Attempts { get; set; }
    /// <summary>en | ms — follows the customer's first message.</summary>
    public string Language { get; set; } = "en";
    /// <summary>{name, phone, email} collected from the replies.</summary>
    public Dictionary<string, object?> Collected { get; set; } = new();
    /// <summary>The customer's message that started it (their real question).</summary>
    public int? OriginalMessageId { get; set; }
    public int? ContactId { get; set; }
    /// <summary>The queued ask (live accounts) — cancelled if the customer answers first.</summary>
    public int? PendingJobId { get; set; }
    public string Note { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? LastAskedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class IdentityStates
{
    /// <summary>An automation asked for it; the ask goes out with the customer's message.</summary>
    public const string Armed = "armed";
    /// <summary>Asked; waiting for the customer's answer.</summary>
    public const string Asking = "asking";
    /// <summary>Matched an existing contact and linked the conversation.</summary>
    public const string Linked = "linked";
    /// <summary>No match — a new contact was created and linked (ask_and_create).</summary>
    public const string Created = "created";
    /// <summary>Details collected, no match (ask) — an operator creates the contact.</summary>
    public const string NeedsContact = "needs_contact";
    /// <summary>Several contacts match — handed over with the candidates listed.</summary>
    public const string Ambiguous = "ambiguous";
    /// <summary>Refused / never answered after the last ask — left for a human.</summary>
    public const string GaveUp = "gave_up";
    /// <summary>Closed without an outcome (opted out, linked by hand).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>States in which the customer is still being asked.</summary>
    public static readonly string[] Open = { Armed, Asking };
}

public static class IdentifyModes
{
    public const string Off = "off";
    public const string Ask = "ask";
    public const string AskAndCreate = "ask_and_create";
    public static readonly string[] All = { Off, Ask, AskAndCreate };
}
