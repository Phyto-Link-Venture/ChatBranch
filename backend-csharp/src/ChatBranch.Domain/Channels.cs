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
    public string Provider { get; set; } = "cloud";  // whatsapp: cloud|baileys|ycloud|twilio
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
    /// <summary>Owning team (Chatwoot-style two-level assignment). null = unassigned.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public List<string> Labels { get; set; } = new();
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? AccountId { get; set; }
    public ChannelAccount? Account { get; set; }
    // Denormalized entity, recomputed by ScopingService.SyncThreadEntity at
    // every seam that moves a thread — always inside the thread's company.
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    /// <summary>
    /// The COMPANY (top-level entity) this conversation belongs to — part of its
    /// identity: one conversation per customer per company, unique on
    /// (channel, key, company). Set when the thread is created (the receiving
    /// account's company, else its entity's) or the first time an unattributed
    /// thread gets an entity; never changed afterwards — nothing moves a thread
    /// into another company. Null = unattributed (no account, no entity: the
    /// organization-wide bucket every viewer sees).
    /// </summary>
    public int? CompanyEntityId { get; set; }
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

    // Human handover (Data/Ai/Handover.cs is the only writer): when the AI — or
    // a rule, a flow, a person — passed this conversation to a human, why, and
    // whether that is still open. Cleared (HandoverResolvedAt set, the rest kept
    // for history) when a person replies or resolves the conversation.
    public DateTime? HandoverAt { get; set; }
    public string HandoverReason { get; set; } = "";
    /// <summary>ai | rule:&lt;id&gt; | flow | agent:&lt;username&gt;</summary>
    public string HandoverBy { get; set; } = "";
    /// <summary>check_failed | asked_for_human | send_blocked | booking_failed | low_confidence | rule | manual</summary>
    public string HandoverKind { get; set; } = "";
    public DateTime? HandoverResolvedAt { get; set; }
    public int HandoverCount { get; set; }

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
    /// <summary>The attachment's original file name as the sender gave it ("" = unknown —
    /// the payload then derives one from the type + extension).</summary>
    public string MediaName { get; set; } = "";
    /// <summary>The provider's MIME type for the attachment ("" = derive from the extension).</summary>
    public string MediaMime { get; set; } = "";
    /// <summary>Stored size in bytes (0 = unknown — older rows; the payload reads the file).</summary>
    public long MediaSize { get; set; }
    /// <summary>Provider failure detail for a failed delivery ("131047: Re-engagement
    /// message"), written by the WhatsApp Cloud status webhook. "" = none.</summary>
    public string Error { get; set; } = "";
    /// <summary>The message this one quotes / replies to (a row of the SAME thread),
    /// resolved from <see cref="ReplyToExternalId"/> when we hold it. Null = not a reply,
    /// or the quoted message is older than our history.</summary>
    public int? ReplyToId { get; set; }
    /// <summary>The provider id of the quoted message (WhatsApp Cloud <c>context.id</c>,
    /// Baileys <c>contextInfo.stanzaId</c>, Telegram <c>reply_to_message.message_id</c>,
    /// Messenger <c>reply_to.mid</c>). "" = not a reply.</summary>
    public string ReplyToExternalId { get; set; } = "";
    /// <summary>What a voice note / audio message SAYS — written once by the speech-to-text
    /// step (<c>Ai/Voice/VoiceTranscriber.cs</c>) so the AI reads words, not "[audio]". "" = none.</summary>
    public string Transcript { get; set; } = "";
    /// <summary>
    /// The caller's own key for a transactional send (<c>POST /api/messaging/notify</c>),
    /// so a retried request reads this message back instead of sending a second one.
    /// Unique across non-empty values; "" = not a transactional send (nearly every row).
    /// Callers prefix their own keys (<c>onel-otp-…</c>) — see <c>MessagingNotifyEndpoints</c>.
    /// </summary>
    public string IdempotencyKey { get; set; } = "";
    /// <summary>When the message was RECALLED ("delete for everyone" / unsend) on the
    /// channel — by the customer, or from our own number's phone. Null = never. What
    /// is left depends on the inbox's <c>keep_recalled</c> switch: kept and marked, or
    /// its words and file erased (<c>Channels/MessageRecalls.cs</c>). The AI never
    /// answers or reads a recalled message.</summary>
    public DateTime? RecalledAt { get; set; }
    /// <summary>Who recalled it: <c>customer</c> or <c>business</c> ("" = not recalled).</summary>
    public string RecalledBy { get; set; } = "";
    /// <summary>
    /// When a message WE sent was last edited on the channel (WhatsApp's own
    /// ~15-minute edit window; <c>Channels/MessageEdits.cs</c>). Null = never
    /// edited. <see cref="Text"/> holds the current wording — the previous one is
    /// kept in the audit row, so the chat shows what the customer now sees while
    /// the trail still says what it used to say.
    /// </summary>
    public DateTime? EditedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One emoji reaction on a message (<c>crm_messagereaction</c>): the customer's
/// (<c>actor = "contact"</c>) or one of ours (<c>"user:&lt;username&gt;"</c>). One per
/// message × actor — reacting again replaces it, an empty emoji removes the row.
/// A reaction is never a message: it doesn't make a bubble, wake the AI, run
/// automations or reopen a chat (<c>Channels/MessageReactions.cs</c>).
/// </summary>
public class MessageReaction
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public int ThreadId { get; set; }
    /// <summary>contact | user:&lt;username&gt;</summary>
    public string Actor { get; set; } = "";
    public string Emoji { get; set; } = "";
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
