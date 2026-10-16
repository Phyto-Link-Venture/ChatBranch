using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The mailbox an outbound mail leaves through. <see cref="Account"/> null =
/// the organization's default mail settings. <see cref="Via"/> is
/// <see cref="Routing"/>'s reason (current / sticky / auto / failover / sole)
/// or "default". <see cref="Error"/> set = every mailbox that may carry this
/// conversation is down: the send is refused (stored as failed, nothing sent).
/// </summary>
public sealed record EmailRoute(ChannelAccount? Account, EmailServerSettings Server, string Via = "")
{
    public string? Error { get; init; }
    public bool Ok => Error == null;
}

/// <summary>
/// Outbound email for <see cref="OutboundSender"/> (port of messaging/views
/// <c>_email_send_account</c> + <c>send_message</c>'s email branch).
///
/// Mailbox resolution follows the SAME send-time rules as WhatsApp numbers
/// (<see cref="Routing.ForThread"/>, channel "email"):
///  1. the pinned / thread's own mailbox while configured and not
///     disconnected/banned;
///  2. the contact's sticky <see cref="AccountAssignment"/> for "email";
///  3. no assignment yet → the least-loaded eligible mailbox, which becomes the
///     contact's unpinned assignment (a conversation with no contact still takes
///     the least-loaded mailbox, without an assignment);
///  4. the thread's / assigned mailbox is disconnected or banned → FAILOVER:
///     the assignment moves and loses its pin, the thread moves, a system note,
///     an <c>account.failover</c> audit row and an admin notification;
///  5. nothing eligible while mailboxes ARE configured → refused (a failed
///     message naming the dead mailbox — never a guess, never sandbox);
///  6. no configured mailbox at all → the default SMTP settings (Channels →
///     Advanced / SMTP_* env), else the message is stored as "sandbox".
/// Eligible mailboxes are the conversation's outlet's own (nearest level of the
/// entity tree that has one), else the organization-wide ones. A routed
/// mailbox is stamped onto the thread (entity re-synced) so replies keep
/// leaving through it.
/// </summary>
public sealed class EmailChannel
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly IEmailTransport _transport;
    private readonly string _dataDir;
    private readonly SentFolderCopier? _sentCopies;

    public EmailChannel(AppDbContext db, ScopingService scoping, ThreadStore threads,
        IEmailTransport transport, VoiceAgentBridge bridge, SentFolderCopier? sentCopies = null)
        : this(db, scoping, threads, transport, bridge.DataDir, sentCopies) { }

    /// <param name="sentCopies">files a copy of every mail sent into the mailbox's Sent folder
    /// (IMAP mailboxes only); null = no copies (unit tests)</param>
    public EmailChannel(AppDbContext db, ScopingService scoping, ThreadStore threads,
        IEmailTransport transport, string dataDir, SentFolderCopier? sentCopies = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _transport = transport;
        _dataDir = dataDir;
        _sentCopies = sentCopies;
    }

    /// <summary>True when a send would leave through a real mailbox now (a refusal is not live).</summary>
    public bool WouldSendLive(Thread thread, int? accountId) => Route(thread, accountId) is { Ok: true };

    /// <summary>
    /// The mailbox a mail on this thread leaves through (null = sandbox).
    /// <paramref name="apply"/>=false is a dry run; with it, routing writes the
    /// contact's assignment and a failover moves the thread (announced).
    /// </summary>
    public EmailRoute? Route(Thread thread, int? accountId, bool apply = false)
    {
        try
        {
            var r = new Routing(_db, _scoping).ForThread(thread, RouteNeed.Any, accountId ?? thread.AccountId, apply);
            if (!r.Ok) return new EmailRoute(null, new EmailServerSettings(), "none") { Error = r.Error };
            // tracked, so the thread stamp and the health verdict after the send stick
            if (r.Account is { } a && a.Channel == ChannelIds.Email && Routing.IsEligible(a)
                && _db.ChannelAccounts.Find(a.Id) is { } mailbox)
                return new EmailRoute(mailbox, EmailConfig.ForAccount(mailbox, _scoping), r.Via);
            // no configured mailbox to route between: the organization's default mail settings
            var global = EmailConfig.GlobalSettings(_scoping);
            return global.SmtpConfigured ? new EmailRoute(null, global, "default") : null;
        }
        catch
        {
            return null; // routing trouble degrades to sandbox, never to an exception
        }
    }

    /// <summary>
    /// Send (or sandbox-store) one outbound email on this thread. The stored
    /// message carries status sent | failed | sandbox and — when sent — the
    /// real Message-ID as its external id (which is how the IMAP sync knows the
    /// copy in the Sent folder is ours). After a successful send through a mailbox
    /// with IMAP, the very same MIME message is filed in its Sent folder by
    /// <see cref="SentFolderCopier"/> — off the request, never delaying, failing or
    /// repeating the send.
    /// </summary>
    public Message Send(Thread thread, string text, string source, int? accountId,
        string? subject = null, OutboundMedia? media = null)
    {
        var route = Route(thread, accountId, apply: true);
        if (route == null)
        {
            var sandbox = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: accountId ?? thread.AccountId);
            return WithMedia(sandbox, media);
        }
        if (!route.Ok)
        {
            // every mailbox this conversation may use is down: refused, thread untouched
            var refused = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {route.Error}] {text}", status: "failed", accountId: thread.AccountId);
            refused.Error = route.Error!.Length > 300 ? route.Error[..300] : route.Error;
            _db.SaveChanges();
            return WithMedia(refused, media);
        }

        var account = route.Account;
        if (account != null && thread.AccountId != account.Id)
        {
            thread.AccountId = account.Id;
            thread.Account = account;
            _scoping.SyncThreadEntity(thread, save: false);
            _db.SaveChanges();
        }

        var history = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.ExternalId != "")
            .OrderBy(m => m.Id)
            .Select(m => new { m.ExternalId, m.Direction })
            .ToList()
            .Where(m => EmailComposer.LooksLikeMessageId(m.ExternalId))
            .ToList();
        var references = history.Select(h => h.ExternalId).ToList();
        var inReplyTo = history.LastOrDefault(h => h.Direction == MessageDirection.In)?.ExternalId
                        ?? history.LastOrDefault()?.ExternalId;
        var lastSubject = _db.EmailHeaders.AsNoTracking()
            .Where(h => h.ThreadId == thread.Id && h.Subject != "")
            .OrderByDescending(h => h.Id).Select(h => h.Subject).FirstOrDefault();
        var subj = !string.IsNullOrWhiteSpace(subject) ? subject.Trim()
            : EmailComposer.ReplySubject(lastSubject) ?? route.Server.DefaultSubject;

        EmailResult result;
        string messageId = "";
        MimeKit.MimeMessage? mime = null;
        try
        {
            var attachments = new List<EmailAttachment>();
            if (media != null && File.Exists(media.AbsolutePath)
                && new FileInfo(media.AbsolutePath).Length <= MessagingMedia.MaxBytes)
                attachments.Add(new EmailAttachment(media.FileName, File.ReadAllBytes(media.AbsolutePath),
                    MessagingMedia.ContentTypeFor(media.FileName)));
            mime = EmailComposer.Build(route.Server, thread.Address.Length > 0 ? thread.Address : thread.Key,
                thread.Name, subj, text, references, inReplyTo, attachments, out messageId);
            // RFC 3834: tell the other side's autoresponder not to answer the bot
            if (source == MessageSource.Ai) mime.Headers.Add(MimeKit.HeaderId.AutoSubmitted, "auto-replied");
            result = _transport.SendAsync(route.Server, mime).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // a recipient MimeKit cannot even parse is the recipient's problem
            result = new EmailResult(false, MailKitEmailTransport.Scrub(ex.Message, route.Server),
                ex is MimeKit.ParseException or ArgumentException ? EmailFailure.Recipient : EmailFailure.Other);
        }

        Message msg;
        if (result.Ok)
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: messageId, accountId: account?.Id);
            RecordHeader(msg, thread, messageId, subj, inReplyTo ?? "");
            if (account != null) HealthRecorder.Ok(_db, account);
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {result.Detail}] {text}", status: "failed", accountId: account?.Id);
            if (account != null) HealthRecorder.EmailSendFailure(_db, account, result);
        }
        _db.SaveChanges();
        // after the row is stored, so a sync reading the copy back already knows its Message-ID
        if (result.Ok && mime != null && route.Server.ImapConfigured) _sentCopies?.Enqueue(route.Server, mime);
        return WithMedia(msg, media);
    }

    /// <summary>Remember a mail's envelope so the next reply threads under it.</summary>
    public void RecordHeader(Message msg, Thread thread, string headerMessageId, string subject, string inReplyTo,
        EmailView? view = null)
    {
        try
        {
            _db.EmailHeaders.Add(new EmailHeader
            {
                MessageId = msg.Id,
                ThreadId = thread.Id,
                HeaderMessageId = headerMessageId.Length > 500 ? headerMessageId[..500] : headerMessageId,
                Subject = subject.Length > 500 ? subject[..500] : subject,
                InReplyTo = inReplyTo.Length > 500 ? inReplyTo[..500] : inReplyTo,
                Html = view?.Html ?? "",
                FromLine = Cap(view?.From), ToLine = Cap(view?.To), CcLine = Cap(view?.Cc),
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* threading metadata is best-effort */ }
    }

    private static string Cap(string? s) => s is { Length: > 2000 } ? s[..2000] : s ?? "";

    private Message WithMedia(Message msg, OutboundMedia? media)
    {
        if (media == null) return msg;
        MediaStore.Stamp(msg, media.MediaType, media.RelPath, MessagingMedia.ContentTypeFor(media.FileName),
            media.FileName, MediaStore.SizeOf(media.AbsolutePath));
        _db.SaveChanges();
        return msg;
    }

    /// <summary>A plain test mail through the given mailbox — Settings' "Send test".</summary>
    public async Task<EmailResult> SendTestAsync(EmailServerSettings server, string to, CancellationToken ct = default)
    {
        try
        {
            var mime = EmailComposer.Build(server, to, null, "ChatBranch SMTP test",
                "Test email from ChatBranch — these SMTP settings work.",
                Array.Empty<string>(), null, null, out _);
            return await _transport.SendAsync(server, mime, ct);
        }
        catch (Exception ex)
        {
            return new EmailResult(false, MailKitEmailTransport.Scrub(ex.Message, server), EmailFailure.Recipient);
        }
    }
}
