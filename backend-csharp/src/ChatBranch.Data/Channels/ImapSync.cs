using System.Net;
using System.Text.RegularExpressions;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The IMAP side of email (port of messaging/imap_client.py). Fetching marks
/// every returned message <c>\Seen</c> — whether or not the inbox keeps it — so
/// each mail is considered exactly once even before Message-ID dedup.
/// </summary>
public interface IMailboxReader
{
    Task<IReadOnlyList<MimeMessage>> FetchUnseenAsync(EmailServerSettings mailbox, DateTime sinceUtc,
        int max, CancellationToken ct = default);
}

/// <summary>An IMAP failure with a readable, secret-free message.</summary>
public sealed class MailboxException : Exception
{
    public bool AuthFailure { get; }
    public MailboxException(string message, bool authFailure) : base(message) => AuthFailure = authFailure;
}

public sealed class MailKitMailboxReader : IMailboxReader
{
    private readonly int _timeoutMs;
    public MailKitMailboxReader(int timeoutMs = 20_000) => _timeoutMs = timeoutMs;

    public async Task<IReadOnlyList<MimeMessage>> FetchUnseenAsync(EmailServerSettings mailbox,
        DateTime sinceUtc, int max, CancellationToken ct = default)
    {
        using var client = new ImapClient { Timeout = _timeoutMs };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs * 3);
        try
        {
            // 143 speaks STARTTLS; everything else (993 by default) implicit TLS, as Django's IMAP4_SSL
            var security = mailbox.ImapPort == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
            await client.ConnectAsync(mailbox.ImapHost, mailbox.ImapPort, security, cts.Token);
            await client.AuthenticateAsync(mailbox.ImapUsername, mailbox.ImapPassword, cts.Token);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadWrite, cts.Token);
            var query = SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(sinceUtc.Date));
            var uids = await inbox.SearchAsync(query, cts.Token);
            // newest last in UID order — take the newest few so a backlog drains over syncs
            var batch = uids.Skip(Math.Max(0, uids.Count - max)).ToList();
            var outv = new List<MimeMessage>();
            var seen = new List<UniqueId>();
            foreach (var uid in batch)
            {
                try { outv.Add(await inbox.GetMessageAsync(uid, cts.Token)); }
                catch (Exception) when (!cts.IsCancellationRequested) { /* a broken message must not stall the rest */ }
                seen.Add(uid);
            }
            if (seen.Count > 0) await inbox.AddFlagsAsync(seen, MessageFlags.Seen, true, cts.Token);
            try { await client.DisconnectAsync(true, cts.Token); } catch { }
            return outv;
        }
        catch (AuthenticationException ex)
        {
            throw new MailboxException($"IMAP login refused: {Scrub(ex.Message, mailbox)}", authFailure: true);
        }
        catch (Exception ex) when (ex is not MailboxException)
        {
            throw new MailboxException($"IMAP sync failed: {Scrub(ex.Message, mailbox)}", authFailure: false);
        }
    }

    private static string Scrub(string text, EmailServerSettings s) => MailKitEmailTransport.Scrub(text, s);
}

/// <summary>One inbound mail, parsed into what the inbox stores.</summary>
public sealed record ParsedEmail(string FromAddress, string FromName, string Subject, string Body,
    string MessageId, string InReplyTo, DateTime? Date, IReadOnlyList<EmailAttachment> Attachments);

/// <summary>
/// MIME → inbox text. Prefers text/plain (HTML-only mail is flattened), strips
/// quoted reply history conservatively, and keeps named attachments (capped).
/// Pure, so it is unit-tested with raw RFC 822 bytes.
/// </summary>
public static partial class EmailParser
{
    public const int MaxBodyChars = 5000;
    public const int MaxAttachments = 5;
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;

    [GeneratedRegex(@"^\s*On\b.{0,300}\bwrote:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteLine();
    [GeneratedRegex(@"^\s*On\b.{0,200}$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteStart();
    [GeneratedRegex(@"^.{0,200}\bwrote:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteEnd();
    [GeneratedRegex(@"^\s*-{2,}\s*Original Message\s*-{2,}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();
    [GeneratedRegex(@"^\s*From:\s.+$", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookFrom();
    [GeneratedRegex(@"^\s*(Sent|Date):\s.+$", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookSent();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"<(script|style|head|title|noscript|template|svg)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Invisible();
    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)')[^>]*>(?<text>.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SourceSpace();
    [GeneratedRegex(@"<\s*br\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();
    [GeneratedRegex(@"<\s*/?\s*(p|h[1-6]|blockquote|ul|ol|table|hr|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphBreak();
    [GeneratedRegex(@"<\s*/\s*(div|tr|dd|dt|section|article|header|footer|address)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();
    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"<\s*/\s*(td|th)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnd();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBreaks();

    public static ParsedEmail Parse(MimeMessage msg)
    {
        var from = msg.From.Mailboxes.FirstOrDefault();
        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(msg.HtmlBody))
            body = HtmlToText(msg.HtmlBody);
        var text = StripQuotes(body ?? "");
        if (text.Length > MaxBodyChars) text = text[..MaxBodyChars];

        DateTime? date = msg.Date == DateTimeOffset.MinValue ? null : msg.Date.UtcDateTime;
        // a sender clock far in the future would pin the thread to the top forever
        if (date is DateTime d && d > DateTime.UtcNow.AddHours(1)) date = null;

        return new ParsedEmail(
            (from?.Address ?? "").Trim().ToLowerInvariant(),
            (from?.Name ?? "").Trim(),
            (msg.Subject ?? "").Trim(),
            text,
            string.IsNullOrWhiteSpace(msg.MessageId) ? "" : $"<{EmailComposer.Bare(msg.MessageId)}>",
            string.IsNullOrWhiteSpace(msg.InReplyTo) ? "" : $"<{EmailComposer.Bare(msg.InReplyTo)}>",
            date,
            Attachments(msg));
    }

    /// <summary>
    /// Drop quoted reply history so the thread shows what the contact actually
    /// typed. Conservative: cut only at an unmistakable marker ("On … wrote:",
    /// "-----Original Message-----", an Outlook From:/Sent: header block, or a
    /// TRAILING run of "&gt;" lines), and only when something is left above it —
    /// an inline reply interleaved with quotes is kept whole.
    /// </summary>
    public static string StripQuotes(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cut = -1;
        for (var i = 0; i < lines.Length && cut < 0; i++)
        {
            var line = lines[i];
            if (WroteLine().IsMatch(line)) cut = i;
            else if (i + 1 < lines.Length && WroteStart().IsMatch(line) && !line.TrimEnd().EndsWith("wrote:")
                     && WroteEnd().IsMatch(lines[i + 1])) cut = i; // "On …\n… wrote:" wrapped
            else if (OriginalMessage().IsMatch(line)) cut = i;
            else if (OutlookFrom().IsMatch(line) && i + 1 < lines.Length
                     && (OutlookSent().IsMatch(lines[i + 1])
                         || (i + 2 < lines.Length && OutlookSent().IsMatch(lines[i + 2]))))
            {
                cut = i;
                if (i > 0 && lines[i - 1].Trim().Trim('_').Length == 0) cut = i - 1;
            }
        }
        if (cut < 0)
        {
            // a trailing block made only of quoted (or blank) lines
            var j = lines.Length - 1;
            while (j >= 0 && (lines[j].TrimStart().StartsWith('>') || lines[j].Trim().Length == 0)) j--;
            if (j < lines.Length - 1 && lines.Skip(j + 1).Any(l => l.TrimStart().StartsWith('>'))) cut = j + 1;
        }
        var kept = cut >= 0 ? string.Join("\n", lines.Take(cut)).Trim() : string.Join("\n", lines).Trim();
        return kept.Length > 0 ? kept : string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Flatten an HTML-only mail into readable text: comments, scripts, styles
    /// and the head are dropped; source whitespace collapses (it is not layout in
    /// HTML); &lt;br&gt; and block ends become line breaks, paragraphs a blank
    /// line, list items "• "; a link keeps its URL next to its text; every
    /// remaining tag is removed and only THEN entities are decoded (so escaped
    /// markup in the mail stays literal text, never a tag).
    /// </summary>
    public static string HtmlToText(string html)
    {
        var s = Comments().Replace(html ?? "", "");
        s = Invisible().Replace(s, "");
        s = SourceSpace().Replace(s, " ");
        s = Anchor().Replace(s, m =>
        {
            var inner = m.Groups["text"].Value;
            var href = m.Groups["href"].Value.Trim();
            var plainHref = WebUtility.HtmlDecode(href);
            var plainText = WebUtility.HtmlDecode(AnyTag().Replace(inner, "")).Trim();
            var web = plainHref.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || plainHref.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!web || plainText.Contains(plainHref, StringComparison.OrdinalIgnoreCase)) return inner;
            return plainText.Length == 0 ? href : $"{inner} ({href})";
        });
        s = LineBreak().Replace(s, "\n");
        s = ParagraphBreak().Replace(s, "\n\n");
        s = ListItem().Replace(s, "\n• ");
        s = BlockEnd().Replace(s, "\n");
        s = CellEnd().Replace(s, " ");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace(' ', ' ').Replace("​", "").Replace("‌", "").Replace("﻿", "");
        s = Spaces().Replace(s, " ");
        s = string.Join("\n", s.Split('\n').Select(l => l.Trim()));
        return ManyBreaks().Replace(s, "\n\n").Trim();
    }

    /// <summary>Named parts only (attachments, or a named inline image such as a photographed receipt).</summary>
    public static IReadOnlyList<EmailAttachment> Attachments(MimeMessage msg)
    {
        var outv = new List<EmailAttachment>();
        try
        {
            foreach (var part in msg.BodyParts.OfType<MimePart>())
            {
                if (outv.Count >= MaxAttachments) break;
                var name = part.FileName;
                if (string.IsNullOrWhiteSpace(name) || part.Content == null) continue;
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                if (ms.Length == 0 || ms.Length > MaxAttachmentBytes) continue;
                outv.Add(new EmailAttachment(name.Trim(), ms.ToArray(), part.ContentType.MimeType));
            }
        }
        catch { /* a malformed part must not lose the mail itself */ }
        return outv;
    }
}

/// <summary>Counts for the /chats nudge (and the operator's "Sync now").</summary>
public sealed record EmailSyncReport(bool Configured, int Fetched, int Skipped, int Duplicates,
    int Mailboxes, bool Throttled, IReadOnlyList<string> Errors);

/// <summary>
/// <c>POST /api/messaging/email/sync</c> — pull UNSEEN mail from every
/// configured mailbox into the inbox (port of messaging/views.email_sync).
///
///  * Real IMAP traffic is throttled to one pass per 30 s no matter how many
///    dashboards poll (<c>force</c> skips it).
///  * KNOWN-SENDER scope: only a CRM contact's address, or an address we have
///    already corresponded with (a contact-linked email thread, or one we sent
///    to). Newsletters and platform notifications stay out — already \Seen, so
///    a skipped sender costs one look, ever.
///  * Each kept mail runs the normal inbound pipeline
///    (<see cref="InboundProcessor.ProcessInbound"/>: contact link, flows, team
///    routing, automations, notifications, the coverage-gated AI path), deduped
///    by Message-ID. A new email thread starts with the chatbot OFF (email has
///    no auto-reply) unless its inbox's AI default says on.
///  * The first attachment rides on the mail's own message; each extra one
///    becomes its own message (one media per crm_message).
///  * Never throws: a failing mailbox is reported in <c>errors</c>.
/// </summary>
public sealed class EmailSyncService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public const int SyncWindowDays = 7;
    public const int MaxFetchPerMailbox = 10;

    private static readonly object Gate = new();
    private static DateTime _lastRun = DateTime.MinValue;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly InboundProcessor _inbound;
    private readonly IMailboxReader _reader;
    private readonly EmailChannel _email;
    private readonly string _dataDir;

    public EmailSyncService(AppDbContext db, ScopingService scoping, ThreadStore threads,
        InboundProcessor inbound, IMailboxReader reader, EmailChannel email, VoiceAgentBridge bridge)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _inbound = inbound;
        _reader = reader;
        _email = email;
        _dataDir = bridge.DataDir;
    }

    /// <summary>Every mailbox that can be read: configured email accounts, then the global one (deduped).</summary>
    public List<EmailServerSettings> Mailboxes()
    {
        var outv = new List<EmailServerSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var a in _db.ChannelAccounts.AsNoTracking().Where(a => a.Channel == ChannelIds.Email)
                         .OrderBy(a => a.Id).ToList().Where(EmailConfig.AccountConfigured))
            {
                var s = EmailConfig.ForAccount(a, _scoping);
                if (s.ImapConfigured && seen.Add($"{s.ImapHost}|{s.ImapUsername}")) outv.Add(s);
            }
            var global = EmailConfig.GlobalSettings(_scoping);
            if (global.ImapConfigured && seen.Add($"{global.ImapHost}|{global.ImapUsername}")) outv.Add(global);
        }
        catch { /* unreadable config = nothing to sync */ }
        return outv;
    }

    public async Task<EmailSyncReport> SyncAsync(bool force = false, CancellationToken ct = default)
    {
        var mailboxes = Mailboxes();
        if (mailboxes.Count == 0)
            return new EmailSyncReport(false, 0, 0, 0, 0, false, Array.Empty<string>());

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastRun < MinInterval)
                return new EmailSyncReport(true, 0, 0, 0, mailboxes.Count, true, Array.Empty<string>());
            _lastRun = now;
        }

        int kept = 0, skipped = 0, dupes = 0;
        var errors = new List<string>();
        foreach (var mailbox in mailboxes)
        {
            IReadOnlyList<MimeMessage> mails;
            try
            {
                mails = await _reader.FetchUnseenAsync(mailbox, DateTime.UtcNow.AddDays(-SyncWindowDays),
                    MaxFetchPerMailbox, ct);
            }
            catch (Exception ex)
            {
                var detail = ex is MailboxException ? ex.Message
                    : $"IMAP sync failed: {MailKitEmailTransport.Scrub(ex.Message, mailbox)}";
                errors.Add($"{mailbox.Label}: {detail}");
                if (ex is MailboxException { AuthFailure: true } && mailbox.AccountId is int aid)
                    TryMarkDisconnected(aid, detail);
                continue;
            }

            foreach (var mime in mails)
            {
                try
                {
                    switch (Ingest(mime, mailbox))
                    {
                        case IngestOutcome.Kept: kept++; break;
                        case IngestOutcome.Duplicate: dupes++; break;
                        default: skipped++; break;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{mailbox.Label}: a message could not be stored ({ex.GetType().Name})");
                }
            }
        }
        return new EmailSyncReport(true, kept, skipped, dupes, mailboxes.Count, false, errors);
    }

    /// <summary>Test seam: forget the throttle clock.</summary>
    public static void ResetThrottle()
    {
        lock (Gate) _lastRun = DateTime.MinValue;
    }

    private enum IngestOutcome { Kept, Skipped, Duplicate }

    private IngestOutcome Ingest(MimeMessage mime, EmailServerSettings mailbox)
    {
        // RFC 3834 auto-replies (out-of-office, bounces from a known address) would
        // only feed flows and the AI a loop — they are not the customer talking
        var auto = mime.Headers[HeaderId.AutoSubmitted];
        if (!string.IsNullOrWhiteSpace(auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            return IngestOutcome.Skipped;

        var mail = EmailParser.Parse(mime);
        var key = mail.FromAddress;
        if (key.Length == 0 || !key.Contains('@')) return IngestOutcome.Skipped;
        if (string.Equals(key, mailbox.From, StringComparison.OrdinalIgnoreCase)) return IngestOutcome.Skipped;

        var existing = _db.Threads.FirstOrDefault(t => t.Channel == ChannelIds.Email && t.Key.ToLower() == key);
        if (!KnownSender(key, existing)) return IngestOutcome.Skipped;
        if (mail.MessageId.Length > 0 && _db.Messages.Any(m => m.ExternalId == mail.MessageId
                                                              && m.Thread!.Channel == ChannelIds.Email))
            return IngestOutcome.Duplicate;

        var threadKey = existing?.Key ?? key;
        var name = mail.FromName.Length > 0 ? mail.FromName : null;
        if (existing == null)
        {
            // email has no auto-reply by default (messaging store: ai_enabled = channel != "email");
            // the inbox's own AI default, applied by ProcessInbound, may still turn it on
            var fresh = _threads.GetOrCreateThread(ChannelIds.Email, threadKey, name, mailbox.AccountId);
            fresh.AiEnabled = false;
            _db.SaveChanges();
        }

        // a fresh subject is context; a "Re:" of our own subject is just noise
        var text = mail.Subject.Length > 0 && !mail.Subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
            ? $"{mail.Subject}\n\n{mail.Body}".Trim()
            : mail.Body;
        if (text.Length == 0)
            text = mail.Attachments.Count > 0 ? $"[attachment] {mail.Attachments[0].FileName}" : "[no text content]";

        var msg = _inbound.ProcessInbound(ChannelIds.Email, threadKey, text, name, mailbox.AccountId,
            mail.MessageId, mail.Date);
        var thread = _db.Threads.First(t => t.Id == msg.ThreadId);
        _email.RecordHeader(msg, thread, mail.MessageId, mail.Subject, mail.InReplyTo);

        for (var i = 0; i < mail.Attachments.Count; i++)
        {
            var att = mail.Attachments[i];
            try
            {
                var rel = MessagingMedia.Save(_dataDir, ChannelIds.Email, threadKey, att.FileName, att.Content);
                var type = MessagingMedia.TypeFor(att.FileName);
                if (i == 0)
                {
                    MediaStore.Stamp(msg, type, rel, MessagingMedia.ContentTypeFor(att.FileName), att.FileName,
                        att.Content.LongLength);
                    thread.UpdatedAt = DateTime.UtcNow; // the /chats stream refreshes on this
                    _db.SaveChanges();
                }
                else
                {
                    var extra = _threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                        $"[attachment] {att.FileName}", status: "received",
                        externalId: mail.MessageId.Length > 0 ? $"{mail.MessageId}-att{i}" : "",
                        accountId: mailbox.AccountId, providerTs: mail.Date);
                    MediaStore.Stamp(extra, type, rel, MessagingMedia.ContentTypeFor(att.FileName), att.FileName,
                        att.Content.LongLength);
                    _db.SaveChanges();
                }
            }
            catch { /* an attachment that cannot be stored must not lose the mail */ }
        }

        if (mailbox.AccountId is int accountId) HealInbound(accountId);
        return IngestOutcome.Kept;
    }

    /// <summary>
    /// messaging/views._known_sender: a CRM contact's email, or an address we
    /// have already corresponded with (contact-linked thread, or one we sent to).
    /// </summary>
    public bool KnownSender(string address, Domain.Thread? thread = null)
    {
        var a = address.Trim().ToLowerInvariant();
        if (_db.Contacts.Any(c => c.Email.ToLower() == a)) return true;
        thread ??= _db.Threads.FirstOrDefault(t => t.Channel == ChannelIds.Email && t.Key.ToLower() == a);
        if (thread == null) return false;
        var threadId = thread.Id;
        return thread.ContactId != null
               || _db.Messages.Any(m => m.ThreadId == threadId && m.Direction == MessageDirection.Out
                                        && !m.Private && m.Source != MessageSource.System);
    }

    private void HealInbound(int accountId)
    {
        try
        {
            var account = _db.ChannelAccounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return;
            account.LastInboundAt = DateTime.UtcNow;
            HealthRecorder.Ok(_db, account, "inbound mail received");
            _db.SaveChanges();
        }
        catch { }
    }

    private void TryMarkDisconnected(int accountId, string detail)
    {
        try
        {
            var account = _db.ChannelAccounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return;
            AccountHealth.SetStatus(_db, account, Health.Disconnected, detail);
            _db.SaveChanges();
        }
        catch { }
    }
}
