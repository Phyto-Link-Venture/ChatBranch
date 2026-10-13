namespace ChatBranch.Domain;

// Calendar bookings made by the AI through a "Connected app" skill (kind
// "app", app "calendar"), or recorded/cancelled by an operator. A booking is
// RESERVED while the AI writes its reply (status pending / cancelling) and
// MADE when that reply is delivered (booked / cancelled) — the AI monitor may
// hold the reply, and a held reply must not have booked anything.

public static class BookingStatus
{
    /// <summary>Reserved by an AI reply that has not been delivered yet (holds the slot).</summary>
    public const string Pending = "pending";
    /// <summary>Made: the event exists (Google) / the slot is taken (ChatBranch).</summary>
    public const string Booked = "booked";
    /// <summary>An undelivered AI reply cancels it (still holds the slot until delivery).</summary>
    public const string Cancelling = "cancelling";
    public const string Cancelled = "cancelled";
    /// <summary>A reservation whose reply was never delivered (rejected, expired, shadow…).</summary>
    public const string Released = "released";
    /// <summary>Delivery tried to make it and could not (slot taken, Google error…).</summary>
    public const string Failed = "failed";

    public static readonly string[] All = { Pending, Booked, Cancelling, Cancelled, Released, Failed };

    /// <summary>Statuses that hold a calendar slot (two conversations can't grab one slot).</summary>
    public static readonly string[] Holding = { Pending, Booked, Cancelling };
}

public static class BookingSources
{
    /// <summary>Google Calendar through the integration's OAuth grant.</summary>
    public const string Google = "google";
    /// <summary>Bookings kept only in ChatBranch (works with zero credentials).</summary>
    public const string ChatBranch = "chatbranch";
}

public class Booking : IEntityScoped
{
    public int Id { get; set; }
    /// <summary>The conversation's entity (ScopeOwned reads, like threads).</summary>
    public int? EntityId { get; set; }
    public int ContactId { get; set; }
    public int? ThreadId { get; set; }
    /// <summary>The app skill (AgentSkill.SkillId) that made it; "" for none.</summary>
    public string SkillId { get; set; } = "";
    /// <summary><see cref="BookingSources"/>.</summary>
    public string Source { get; set; } = BookingSources.ChatBranch;
    public int? IntegrationId { get; set; }
    /// <summary>
    /// The calendar a slot belongs to, for overlap checks:
    /// <c>google:&lt;integration id&gt;:&lt;calendar id&gt;</c> or
    /// <c>chatbranch:&lt;skill entity id | global&gt;</c>.
    /// </summary>
    public string CalendarKey { get; set; } = "";
    /// <summary>Human label of that calendar ("Google Calendar · aisha", "ChatBranch bookings").</summary>
    public string CalendarLabel { get; set; } = "";
    public string ExternalEventId { get; set; } = "";
    public string EventLink { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the customer asked to note on the booking (the book tool's `note`).</summary>
    public string Note { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>IANA zone the times were offered in.</summary>
    public string Timezone { get; set; } = "";
    /// <summary><see cref="BookingStatus"/>.</summary>
    public string Status { get; set; } = BookingStatus.Pending;
    /// <summary><c>ai</c> or <c>agent:&lt;username&gt;</c>.</summary>
    public string CreatedBy { get; set; } = "ai";
    /// <summary>The AI draft whose delivery makes the booking.</summary>
    public int? DraftId { get; set; }
    /// <summary>The AI draft whose delivery cancels it (while status = cancelling).</summary>
    public int? CancelDraftId { get; set; }
    /// <summary>Why it failed / was released / who cancelled it.</summary>
    public string Detail { get; set; } = "";
    /// <summary>The video meeting made with the booking: <c>zoom</c> | <c>google_meet</c> | <c>link</c>
    /// (the calendar skill's own link); "" = none.</summary>
    public string MeetingProvider { get; set; } = "";
    /// <summary>The link the customer joins with ("" = none).</summary>
    public string MeetingUrl { get; set; } = "";
    /// <summary>The provider's meeting id (Zoom) — what cancelling deletes.</summary>
    public string MeetingId { get; set; } = "";
    /// <summary>The Zoom connection that made the meeting.</summary>
    public int? MeetingIntegrationId { get; set; }
    /// <summary>When the link was sent to the customer (null = not yet).</summary>
    public DateTime? MeetingSentAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
