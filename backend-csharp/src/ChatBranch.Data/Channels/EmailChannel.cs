using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>The mailbox an outbound mail leaves through.</summary>
public sealed record EmailRoute(ChannelAccount? Account, EmailServerSettings Server);

/// <summary>
/// Outbound email for <see cref="OutboundSender"/> (port of messaging/views
/// <c>_email_send_account</c> + <c>send_message</c>'s email branch).
///
/// Mailbox resolution, first match wins:
///  1. the pinned / thread's own email account, while configured and routable;
///  2. the contact's sticky <see cref="AccountAssignment"/> for "email";
///  3. any configured, routable email account — the thread's outlet's own
///     mailboxes first, then the organization-wide ones;
///  4. a configured but currently unhealthy account (better to try than to
///     pretend: the attempt itself re-tests it);
///  5. the global SMTP settings (Channels → Advanced / SMTP_* env);
///  6. nothing → the message is stored as "sandbox".
/// A routed account is stamped onto the thread (with the entity re-synced) so
/// replies keep leaving through the same mailbox.
/// </summary>
public sealed class EmailChannel
{
    private static readonly string[] Ineligible = { Health.Disconnected, Health.Banned };

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly IEmailTransport _transport;
    private readonly string _dataDir;

    public EmailChannel(AppDbContext db, ScopingService scoping, ThreadStore threads,
        IEmailTransport transport, VoiceAgentBridge bridge)
        : this(db, scoping, threads, transport, bridge.DataDir) { }

    public EmailChannel(AppDbContext db, ScopingService scoping, ThreadStore threads,
        IEmailTransport transport, string dataDir)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _transport = transport;
        _dataDir = dataDir;
    }

    public bool WouldSendLive(Thread thread, int? accountId) => Route(thread, accountId) != null;

    public EmailRoute? Route(Thread thread, int? accountId)
    {
        try
        {
            var accounts = _db.ChannelAccounts.Where(a => a.Channel == ChannelIds.Email).ToList()
                .Where(EmailConfig.AccountConfigured).ToList();
            bool Routable(ChannelAccount a) => !Ineligible.Contains(a.HealthStatus);

            var preferred = (accountId ?? thread.AccountId) is int pid
                ? accounts.FirstOrDefault(a => a.Id == pid) : null;
            if (preferred != null && Routable(preferred)) return Make(preferred);

            if (thread.ContactId is int cid)
            {
                var sticky = _db.AccountAssignments.AsNoTracking()
                    .Where(x => x.ContactId == cid && x.Channel == ChannelIds.Email)
                    .Select(x => (int?)x.AccountId).FirstOrDefault();
                var pinned = sticky is int sid ? accounts.FirstOrDefault(a => a.Id == sid) : null;
                if (pinned != null && Routable(pinned)) return Make(pinned);
            }

            var ordered = accounts
                .OrderBy(a => thread.EntityId != null && a.EntityId == thread.EntityId ? 0
                              : a.EntityId == null ? 1 : 2)
                .ThenBy(a => a.Id).ToList();
            var healthy = ordered.FirstOrDefault(Routable);
            if (healthy != null) return Make(healthy);
            var any = preferred ?? ordered.FirstOrDefault();
            if (any != null) return Make(any);

            var global = EmailConfig.GlobalSettings(_scoping);
            return global.SmtpConfigured ? new EmailRoute(null, global) : null;
        }
        catch
        {
            return null; // routing trouble degrades to sandbox, never to an exception
        }

        EmailRoute Make(ChannelAccount a) => new(a, EmailConfig.ForAccount(a, _scoping));
    }

    /// <summary>
    /// Send (or sandbox-store) one outbound email on this thread. The stored
    /// message carries status sent | failed | sandbox and — when sent — the
    /// real Message-ID as its external id.
    /// </summary>
    public Message Send(Thread thread, string text, string source, int? accountId,
        string? subject = null, OutboundMedia? media = null)
    {
        var route = Route(thread, accountId);
        if (route == null)
        {
            var sandbox = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: accountId ?? thread.AccountId);
            return WithMedia(sandbox, media);
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
        try
        {
            var attachments = new List<EmailAttachment>();
            if (media != null && File.Exists(media.AbsolutePath)
                && new FileInfo(media.AbsolutePath).Length <= MessagingMedia.MaxBytes)
                attachments.Add(new EmailAttachment(media.FileName, File.ReadAllBytes(media.AbsolutePath),
                    MessagingMedia.ContentTypeFor(media.FileName)));
            var mime = EmailComposer.Build(route.Server, thread.Address.Length > 0 ? thread.Address : thread.Key,
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
        return WithMedia(msg, media);
    }

    /// <summary>Remember a mail's envelope so the next reply threads under it.</summary>
    public void RecordHeader(Message msg, Thread thread, string headerMessageId, string subject, string inReplyTo)
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
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* threading metadata is best-effort */ }
    }

    private Message WithMedia(Message msg, OutboundMedia? media)
    {
        if (media == null) return msg;
        msg.MediaType = media.MediaType;
        msg.MediaPath = media.RelPath;
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
