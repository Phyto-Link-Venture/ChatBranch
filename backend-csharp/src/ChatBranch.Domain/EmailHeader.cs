namespace ChatBranch.Domain;

/// <summary>
/// The RFC 5322 envelope of one email <see cref="Message"/> (inbound or sent):
/// its Message-ID, subject and the id it replied to. Kept beside the message
/// rather than on it so the unified <c>crm_message</c> table stays
/// channel-neutral. Outbound replies read it back to thread properly —
/// <c>In-Reply-To</c> / <c>References</c> and "Re: &lt;last subject&gt;" — so a
/// customer's mail client shows one conversation, not a pile of new ones.
/// </summary>
public class EmailHeader
{
    public int Id { get; set; }
    public int MessageId { get; set; }               // crm_message.id
    public int ThreadId { get; set; }                // crm_thread.id
    public string HeaderMessageId { get; set; } = ""; // "<id@host>" as on the wire
    public string Subject { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    // the mail as a mail app shows it (inbound / imported mail only; "" = show the text):
    // sanitized HTML with inline images embedded, and its address lines
    public string Html { get; set; } = "";
    public string FromLine { get; set; } = "";
    public string ToLine { get; set; } = "";
    public string CcLine { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
