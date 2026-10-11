namespace ChatBranch.Domain;

/// <summary>
/// How far the IMAP sync has read one folder of one mailbox
/// (<c>crm_mailboxcursor</c>). The sync no longer looks at <c>\Seen</c> — mail a
/// person already opened in Outlook must still arrive, and ChatBranch must never
/// mark anything read — so it remembers the last UID it considered instead. UIDs
/// only mean something within one UIDVALIDITY: when the server reports another
/// value the folder was rebuilt and the sync starts again from its window (the
/// Message-ID check keeps that from importing anything twice). One row per
/// (mailbox, folder); it covers account mailboxes AND the organization's default
/// mailbox, which has no <see cref="ChannelAccount"/>.
/// </summary>
public class MailboxCursor
{
    public int Id { get; set; }
    /// <summary>"imap_host|imap_username", trimmed and lower-cased.</summary>
    public string Mailbox { get; set; } = "";
    /// <summary>The folder's full IMAP name as the server lists it ("INBOX", "Sent Items", "INBOX.Sent").</summary>
    public string Folder { get; set; } = "";
    /// <summary>The folder's UIDVALIDITY when <see cref="LastUid"/> was read (an unsigned 32-bit value).</summary>
    public long UidValidity { get; set; }
    /// <summary>Every UID up to this one has been considered (imported, skipped or a duplicate).</summary>
    public long LastUid { get; set; }
    public DateTime UpdatedAt { get; set; }
}
