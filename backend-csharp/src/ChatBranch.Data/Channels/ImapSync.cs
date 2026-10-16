using System.Net;
using System.Text.RegularExpressions;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using ChatBranch.Data.Contacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>One inbound mail, parsed into what the inbox stores.</summary>
public sealed record ParsedEmail(string FromAddress, string FromName, string Subject, string Body,
    string MessageId, string InReplyTo, DateTime? Date, IReadOnlyList<EmailAttachment> Attachments)
{
    /// <summary>The mail as a mail app shows it; null = text only.</summary>
    public EmailView? View { get; init; }
}

/// <summary>An email's cleaned HTML (inline images embedded; "" = plain text) and its address lines.</summary>
public sealed record EmailView(string Html, string From, string To, string Cc);

/// <summary>
/// MIME → inbox text. Prefers text/plain (HTML-only mail is flattened), strips
/// quoted reply history conservatively, and keeps named attachments (capped).
/// Pure, so it is unit-tested with raw RFC 822 bytes.
/// </summary>
public static partial class EmailParser
{
    public const int MaxBodyChars = 5000;
    public const int MaxAttachments = 20;
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;
    /// <summary>A body image (cid-referenced) smaller than this is decoration — a signature logo or icon.</summary>
    public const long MinInlineImageBytes = 100 * 1024;
    /// <summary>Inline (cid:) images embedded into the mail-app view, in total.</summary>
    public const long MaxEmbeddedBytes = 3L * 1024 * 1024;
    /// <summary>A view bigger than this is not kept (the text still is).</summary>
    public const int MaxViewChars = 6 * 1024 * 1024;

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
    [GeneratedRegex(@"^\s*fwd?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardSubject();
    [GeneratedRegex(@"[ \t]*<mailto:[^>\s]*>[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex MailtoNoise();
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
        // Outlook's plain text repeats every address as "<mailto:…>"
        var text = StripQuotes(DropMailto(body ?? ""), ForwardSubject().IsMatch(msg.Subject ?? ""));
        if (text.Length > MaxBodyChars) text = text[..MaxBodyChars];
        var (attachments, skipped) = AttachmentsAndSkipped(msg);
        // never a silent loss: the chat names every file that could not be kept
        if (skipped.Count > 0)
            text = $"{text}\n\n[Not kept: {string.Join(", ", skipped)}]".Trim();

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
            attachments) { View = View(msg) };
    }

    /// <summary>
    /// The mail-app view: the HTML part with every inline (cid:) image the body
    /// shows embedded as a data: URL (logos included — that is how the mail looks),
    /// cleaned in EMAIL mode (no scripts, handlers or forms; remote images held
    /// back until asked for). A text-only mail has no HTML — the inbox shows its text.
    /// </summary>
    public static EmailView View(MimeMessage msg)
    {
        static string Line(InternetAddressList list) => string.Join(", ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name.Trim()} <{m.Address}>"));
        var html = "";
        try
        {
            var raw = msg.HtmlBody ?? "";
            if (raw.Trim().Length > 0)
            {
                var budget = MaxEmbeddedBytes;
                foreach (var part in msg.BodyParts.OfType<MimePart>())
                {
                    if (string.IsNullOrWhiteSpace(part.ContentId) || part.Content == null
                        || !part.ContentType.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
                    var cid = "cid:" + part.ContentId;
                    if (raw.IndexOf(cid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using var ms = new MemoryStream();
                    part.Content.DecodeTo(ms);
                    if (ms.Length == 0 || ms.Length > budget) continue;
                    budget -= ms.Length;
                    raw = raw.Replace(cid, $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}",
                        StringComparison.OrdinalIgnoreCase);
                }
                html = Documents.HtmlSanitizer.Sanitize(raw, email: true);
                if (html.Length > MaxViewChars) html = "";
            }
        }
        catch { html = ""; /* the text is still there */ }
        return new EmailView(html, Line(msg.From), Line(msg.To), Line(msg.Cc));
    }

    /// <summary>"a@x.test &lt;mailto:a@x.test&gt; &gt;" → "a@x.test&gt;"; elsewhere one space.</summary>
    private static string DropMailto(string body) =>
        MailtoNoise().Replace(body, m =>
            m.Index + m.Length < body.Length && body[m.Index + m.Length] == '>' ? "" : " ");

    /// <summary>
    /// Drop quoted reply history so the thread shows what the contact actually
    /// typed. Conservative: cut only at an unmistakable marker ("On … wrote:",
    /// "-----Original Message-----", an Outlook From:/Sent: header block, or a
    /// TRAILING run of "&gt;" lines), and only when something is left above it —
    /// an inline reply interleaved with quotes is kept whole. A FORWARD is kept
    /// WHOLE (up to <see cref="MaxBodyChars"/>): the forwarded chain is the content,
    /// and a forward of a forward has it two or more header blocks down.
    /// </summary>
    public static string StripQuotes(string body, bool forwarded = false)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (forwarded) return string.Join("\n", lines).Trim();
        var cut = -1;
        for (var i = 0; i < lines.Length && cut < 0; i++) cut = MarkerAt(lines, i);
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

    /// <summary>Where quoted history starts when line <paramref name="i"/> opens it, else -1.</summary>
    private static int MarkerAt(string[] lines, int i)
    {
        var line = lines[i];
        if (WroteLine().IsMatch(line)) return i;
        if (i + 1 < lines.Length && WroteStart().IsMatch(line) && !line.TrimEnd().EndsWith("wrote:")
            && WroteEnd().IsMatch(lines[i + 1])) return i; // "On …\n… wrote:" wrapped
        if (OriginalMessage().IsMatch(line)) return i;
        if (OutlookFrom().IsMatch(line) && i + 1 < lines.Length
            && (OutlookSent().IsMatch(lines[i + 1])
                || (i + 2 < lines.Length && OutlookSent().IsMatch(lines[i + 2]))))
            return i > 0 && lines[i - 1].Trim().Trim('_').Length == 0 ? i - 1 : i;
        return -1;
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

    /// <summary>
    /// Named parts: real attachments first, then a named inline image that is
    /// content (a pasted photo of a receipt). An image the HTML body shows by
    /// <c>cid:</c> under <see cref="MinInlineImageBytes"/> is decoration — a
    /// signature logo or social icon — and is never kept, so it neither clutters
    /// the chat nor uses up the cap before the real attachments.
    /// </summary>
    public static IReadOnlyList<EmailAttachment> Attachments(MimeMessage msg) => AttachmentsAndSkipped(msg).Kept;

    /// <summary>The attachments kept, and "name (why)" for each real one that was not.</summary>
    public static (IReadOnlyList<EmailAttachment> Kept, IReadOnlyList<string> Skipped) AttachmentsAndSkipped(MimeMessage msg)
    {
        var outv = new List<EmailAttachment>();
        var skipped = new List<string>();
        try
        {
            var html = msg.HtmlBody ?? "";
            var parts = msg.BodyParts.OfType<MimePart>()
                .Where(p => !string.IsNullOrWhiteSpace(p.FileName) && p.Content != null)
                .Select(p => (Part: p, Inline: !string.IsNullOrWhiteSpace(p.ContentId)
                    && html.Contains("cid:" + p.ContentId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Inline) // stable: real attachments first, in mail order
                .ToList();
            foreach (var (part, inline) in parts)
            {
                var name = part.FileName.Trim();
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                if (inline && ms.Length < MinInlineImageBytes) continue; // decoration, not a file
                if (ms.Length == 0) continue;
                if (ms.Length > MaxAttachmentBytes) { skipped.Add($"{name} (over {MaxAttachmentBytes / 1024 / 1024} MB)"); continue; }
                if (outv.Count >= MaxAttachments) { skipped.Add($"{name} (over {MaxAttachments} files)"); continue; }
                outv.Add(new EmailAttachment(name, ms.ToArray(), part.ContentType.MimeType));
            }
        }
        catch { /* a malformed part must not lose the mail itself */ }
        return (outv, skipped);
    }
}

/// <summary>Counts for the /chats nudge (and the operator's "Sync now").</summary>
public sealed record EmailSyncReport(bool Configured, int Fetched, int Skipped, int Duplicates,
    int Mailboxes, bool Throttled, IReadOnlyList<string> Errors)
{
    /// <summary>Sent-folder mails stored as outbound (one per mail, however many recipients).</summary>
    public int SentImported { get; init; }
    /// <summary>Sent-folder mails ChatBranch already held — its own replies, or an earlier import.</summary>
    public int SentDuplicates { get; init; }
    /// <summary>Sent-folder mails with no one to file them under (only our own address) or auto-generated.</summary>
    public int SentSkipped { get; init; }
    /// <summary>Worth knowing, not a failure — e.g. a mailbox without a Sent folder.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// <c>POST /api/messaging/email/sync</c> — two-way sync with the mailbox, so a
/// person who also works in Outlook (or any mail app) sees the same conversation
/// in both places (port of messaging/views.email_sync, made two-way).
///
///  * Real IMAP traffic is throttled to one pass per 30 s no matter how many
///    dashboards poll (<c>force</c> skips it).
///  * The \Seen flag plays no part: folders are opened READ-ONLY, so ChatBranch
///    never marks a mail read, and mail someone already read elsewhere still
///    arrives. Each folder has a UID cursor (<c>crm_mailboxcursor</c>, per mailbox
///    and folder, with its UIDVALIDITY): the first pass — and the first after the
///    server rebuilt the folder (another UIDVALIDITY) — reads the last
///    <see cref="SyncWindowDays"/> days, seen or not; later passes read the UIDs
///    after the cursor. Oldest first, at most <see cref="MaxFetchPerMailbox"/> per
///    folder per pass; the cursor moves past every mail handled — kept, skipped,
///    duplicate or failed — so a backlog drains over passes and one broken mail
///    never stalls a folder. The Message-ID check stays as the safety net.
///  * INBOX: EVERY sender lands in the inbox, contact or not: mail from an address
///    that is not a contact opens (or continues) an UNLINKED conversation
///    (ContactId null) — listed under "Not in contacts", never answered by the AI
///    (the coverage gate wants a linked contact). Only three things are skipped:
///    RFC 3834 auto-replies (<c>Auto-Submitted</c>), mail from the mailbox's own
///    From address, and Message-ID duplicates. Each kept mail runs the normal
///    inbound pipeline (<see cref="InboundProcessor.ProcessInbound"/>: contact
///    link, flows, team routing, automations, notifications, the coverage-gated AI
///    path). A new email thread starts with the chatbot OFF (email has no
///    auto-reply) unless its inbox's AI default says on.
///  * SENT (found by <see cref="SentFolders"/>; none → a note, nothing else): what a
///    person sent from their own mail app becomes an OUTBOUND message
///    (<see cref="MessageSource.ExternalClient"/>, status sent, the mail's Date) on
///    the conversation of each distinct To address that is not the mailbox's own
///    (Cc / Bcc ignored) — a brand-new address opens an unlinked conversation with
///    the chatbot off; a known contact's address is linked as their own mail would
///    link it. A mail whose Message-ID ChatBranch already holds is skipped — that is
///    how its own replies (<see cref="EmailChannel"/> stores the Message-ID as the
///    message's external id, and <see cref="SentFolderCopier"/> files a copy in Sent)
///    never appear twice. It goes through <see cref="ThreadStore"/> only: no AI, no
///    flows, no automations, no notifications, no STOP handling, no handover.
///  * Both: the headers are recorded (<c>crm_emailheader</c>) so the next reply
///    threads under the mail; the first attachment rides on the mail's own message,
///    each extra one becomes its own message (one media per crm_message).
///  * Never throws: a failing mailbox or mail is reported in <c>errors</c>.
/// </summary>
public sealed class EmailSyncService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public const int SyncWindowDays = 7;
    /// <summary>Per FOLDER per pass; a longer backlog drains over the next passes.</summary>
    public const int MaxFetchPerMailbox = 10;

    private static readonly object Gate = new();
    // last run per DATABASE: one key in production; test hosts sharing the process never
    // throttle one another (a static single timestamp made parallel test classes race)
    private static readonly Dictionary<string, DateTime> LastRun = new();

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly InboundProcessor _inbound;
    private readonly IMailboxReader _reader;
    private readonly EmailChannel _email;
    private readonly string _dataDir;
    private readonly ILogger<EmailSyncService>? _log;

    public EmailSyncService(AppDbContext db, ScopingService scoping, ThreadStore threads,
        InboundProcessor inbound, IMailboxReader reader, EmailChannel email, VoiceAgentBridge bridge,
        ILogger<EmailSyncService>? log = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _inbound = inbound;
        _reader = reader;
        _email = email;
        _dataDir = bridge.DataDir;
        _log = log;
    }

    /// <summary>The cursor key of a mailbox: "imap_host|imap_username", trimmed and lower-cased.</summary>
    public static string MailboxKey(EmailServerSettings s) =>
        $"{s.ImapHost.Trim()}|{s.ImapUsername.Trim()}".ToLowerInvariant();

    /// <summary>Every mailbox that can be read: configured email accounts, then the global one (deduped).</summary>
    public List<EmailServerSettings> Mailboxes()
    {
        var outv = new List<EmailServerSettings>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var a in _db.ChannelAccounts.AsNoTracking().Where(a => a.Channel == ChannelIds.Email)
                         .OrderBy(a => a.Id).ToList().Where(EmailConfig.AccountConfigured))
            {
                var s = EmailConfig.ForAccount(a, _scoping);
                if (s.ImapConfigured && seen.Add(MailboxKey(s))) outv.Add(s);
            }
            var global = EmailConfig.GlobalSettings(_scoping);
            if (global.ImapConfigured && seen.Add(MailboxKey(global))) outv.Add(global);
        }
        catch { /* unreadable config = nothing to sync */ }
        return outv;
    }

    public async Task<EmailSyncReport> SyncAsync(bool force = false, CancellationToken ct = default)
    {
        var mailboxes = Mailboxes();
        if (mailboxes.Count == 0)
            return new EmailSyncReport(false, 0, 0, 0, 0, false, Array.Empty<string>());

        var key = ThrottleKey();
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (!force && LastRun.TryGetValue(key, out var last) && now - last < MinInterval)
                return new EmailSyncReport(true, 0, 0, 0, mailboxes.Count, true, Array.Empty<string>());
            LastRun[key] = now;
        }

        int kept = 0, skipped = 0, dupes = 0, sentKept = 0, sentDupes = 0, sentSkipped = 0;
        var errors = new List<string>();
        var notes = new List<string>();
        var since = DateTime.UtcNow.AddDays(-SyncWindowDays);
        foreach (var mailbox in mailboxes)
        {
            var mailboxKey = MailboxKey(mailbox);
            MailboxFetch fetched;
            try
            {
                fetched = await _reader.FetchAsync(mailbox, LoadCursors(mailboxKey), since, MaxFetchPerMailbox, ct);
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

            foreach (var folder in fetched.Folders)
            {
                foreach (var mail in folder.Mails)
                {
                    if (mail.Message is null)
                    {
                        Problem(errors, mailbox,
                            $"a message in {folder.Folder} could not be downloaded (UID {mail.Uid}) and was skipped");
                        continue;
                    }
                    try
                    {
                        if (folder.Role == MailFolderRole.Sent)
                            switch (IngestSent(mail.Message, mailbox))
                            {
                                case IngestOutcome.Kept: sentKept++; break;
                                case IngestOutcome.Duplicate: sentDupes++; break;
                                default: sentSkipped++; break;
                            }
                        else
                            switch (Ingest(mail.Message, mailbox))
                            {
                                case IngestOutcome.Kept: kept++; break;
                                case IngestOutcome.Duplicate: dupes++; break;
                                default: skipped++; break;
                            }
                    }
                    catch (Exception ex)
                    {
                        Forget();
                        Problem(errors, mailbox, $"a message could not be stored ({ex.GetType().Name})");
                    }
                }
                // past everything handled (kept, skipped, duplicate or failed), so a backlog
                // drains over passes and one bad mail never stalls the folder
                if (folder.AdvanceTo is uint to) SaveCursor(mailboxKey, folder.Folder, folder.UidValidity, to);
            }
            if (fetched.NoSentFolder)
                notes.Add($"{mailbox.Label}: no Sent folder found — mail sent from other mail apps is not imported.");
            foreach (var problem in fetched.Problems) Problem(errors, mailbox, problem);
        }
        return new EmailSyncReport(true, kept, skipped, dupes, mailboxes.Count, false, errors)
        {
            SentImported = sentKept, SentDuplicates = sentDupes, SentSkipped = sentSkipped, Notes = notes,
        };
    }

    /// <summary>Test seam: forget the throttle clock.</summary>
    public static void ResetThrottle()
    {
        lock (Gate) LastRun.Clear();
    }

    private string ThrottleKey()
    {
        try { return _db.Database.GetConnectionString() ?? ""; }
        catch { return ""; }
    }

    private void Problem(List<string> errors, EmailServerSettings mailbox, string text)
    {
        errors.Add($"{mailbox.Label}: {text}");
        _log?.LogWarning("Email sync: {Mailbox}: {Problem}", mailbox.Label, text);
    }

    /// <summary>After a failed write: drop whatever it left tracked so the next mail starts clean.</summary>
    private void Forget()
    {
        try { _db.ChangeTracker.Clear(); } catch { }
    }

    // ---- cursors -------------------------------------------------------------------

    private Dictionary<string, FolderCursor> LoadCursors(string mailboxKey)
    {
        try
        {
            return _db.MailboxCursors.AsNoTracking().Where(c => c.Mailbox == mailboxKey).ToList()
                .GroupBy(c => c.Folder, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new FolderCursor(U32(g.First().UidValidity), U32(g.First().LastUid)),
                    StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, FolderCursor>(); // read the window; Message-IDs keep it single
        }
    }

    private static uint U32(long v) => (uint)Math.Clamp(v, 0, uint.MaxValue);

    private void SaveCursor(string mailboxKey, string folder, uint uidValidity, uint lastUid)
    {
        try
        {
            var row = _db.MailboxCursors.FirstOrDefault(c => c.Mailbox == mailboxKey && c.Folder == folder);
            if (row == null)
            {
                row = new MailboxCursor { Mailbox = mailboxKey, Folder = folder };
                _db.MailboxCursors.Add(row);
            }
            // a pass running alongside may already have got further in the same folder
            else if (row.UidValidity == uidValidity && row.LastUid >= lastUid) return;
            row.UidValidity = uidValidity;
            row.LastUid = lastUid;
            row.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch (Exception ex)
        {
            Forget(); // the next pass re-reads from the old cursor; Message-IDs keep it single
            _log?.LogWarning("Email sync: the cursor of {Folder} could not be saved ({Error})", folder,
                ex.GetType().Name);
        }
    }

    // ---- one mail ------------------------------------------------------------------

    private enum IngestOutcome { Kept, Skipped, Duplicate }

    /// <summary>RFC 3834 auto-replies (out-of-office, bounces) are not a person talking.</summary>
    private static bool AutoSubmitted(MimeMessage mime)
    {
        var auto = mime.Headers[HeaderId.AutoSubmitted];
        return !string.IsNullOrWhiteSpace(auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A Message-ID an email message already carries — an inbound mail, our own send or an import.</summary>
    private bool KnownMessageId(string headerMessageId) =>
        _db.Messages.Any(m => m.ExternalId == headerMessageId && m.Thread!.Channel == ChannelIds.Email);

    /// <summary>A fresh subject is context; a "Re:" of the thread's subject is just noise.</summary>
    private static string MessageText(ParsedEmail mail)
    {
        var text = mail.Subject.Length > 0 && !mail.Subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
            ? $"{mail.Subject}\n\n{mail.Body}".Trim()
            : mail.Body;
        if (text.Length == 0)
            text = mail.Attachments.Count > 0 ? $"[attachment] {mail.Attachments[0].FileName}" : "[no text content]";
        return text;
    }

    private IngestOutcome Ingest(MimeMessage mime, EmailServerSettings mailbox)
    {
        // out-of-office replies would only feed flows and the AI a loop
        if (AutoSubmitted(mime)) return IngestOutcome.Skipped;

        var mail = EmailParser.Parse(mime);
        var key = mail.FromAddress;
        if (key.Length == 0 || !key.Contains('@')) return IngestOutcome.Skipped;
        // a copy of our own mail landing in the Inbox (what we send is read from Sent)
        if (string.Equals(key, mailbox.From, StringComparison.OrdinalIgnoreCase)) return IngestOutcome.Skipped;

        // a sender who is not a contact still gets a conversation (unlinked, AI off) —
        // the same rule as every other channel; there is no known-sender gate
        // THIS mailbox's company's conversation with the sender (one per company)
        var (existing, threadKey) = ExistingFor(key, mailbox.AccountId);
        if (mail.MessageId.Length > 0 && KnownMessageId(mail.MessageId)) return IngestOutcome.Duplicate;

        var name = mail.FromName.Length > 0 ? mail.FromName : null;
        if (existing == null)
        {
            // email has no auto-reply by default (messaging store: ai_enabled = channel != "email");
            // the inbox's own AI default, applied by ProcessInbound, may still turn it on
            var fresh = _threads.GetOrCreateThread(ChannelIds.Email, threadKey, name, mailbox.AccountId);
            fresh.AiEnabled = false;
            _db.SaveChanges();
        }

        var msg = _inbound.ProcessInbound(ChannelIds.Email, threadKey, MessageText(mail), name, mailbox.AccountId,
            mail.MessageId, mail.Date);
        var thread = _db.Threads.First(t => t.Id == msg.ThreadId);
        _email.RecordHeader(msg, thread, mail.MessageId, mail.Subject, mail.InReplyTo, mail.View);
        StoreAttachments(mail, thread, msg, mailbox, MessageDirection.In, MessageSource.Customer, "received");

        if (mailbox.AccountId is int accountId) HealInbound(accountId);
        return IngestOutcome.Kept;
    }

    /// <summary>
    /// A mail from the Sent folder: outbound on the conversation of each customer it
    /// was addressed to. Deliberately NOT <see cref="InboundProcessor.ProcessInbound"/> —
    /// a person already handled the conversation, so nothing automated reacts.
    /// </summary>
    private IngestOutcome IngestSent(MimeMessage mime, EmailServerSettings mailbox)
    {
        if (AutoSubmitted(mime)) return IngestOutcome.Skipped;
        var mail = EmailParser.Parse(mime);
        // ChatBranch's own replies (and anything imported before) already hold this Message-ID
        if (mail.MessageId.Length > 0 && KnownMessageId(mail.MessageId)) return IngestOutcome.Duplicate;

        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mailbox.From.Trim() };
        foreach (var login in new[] { mailbox.ImapUsername, mailbox.Username })
            if (login.Contains('@')) own.Add(login.Trim());
        var recipients = mime.To.Mailboxes
            .Select(a => (Address: (a.Address ?? "").Trim().ToLowerInvariant(), Name: (a.Name ?? "").Trim()))
            .Where(r => r.Address.Contains('@') && !own.Contains(r.Address))
            .DistinctBy(r => r.Address)
            .ToList();
        if (recipients.Count == 0) return IngestOutcome.Skipped;

        var text = MessageText(mail);
        foreach (var (address, name) in recipients)
        {
            var thread = SentThread(address, name, mailbox);
            var msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient, text,
                status: "sent", externalId: mail.MessageId, accountId: mailbox.AccountId, providerTs: mail.Date);
            _email.RecordHeader(msg, thread, mail.MessageId, mail.Subject, mail.InReplyTo, mail.View);
            StoreAttachments(mail, thread, msg, mailbox, MessageDirection.Out, MessageSource.ExternalClient, "sent");
        }
        return IngestOutcome.Kept;
    }

    /// <summary>
    /// The mailbox's company's conversation with an address (any casing of it), and the key
    /// to file under — a customer has one conversation per company, never another's.
    /// </summary>
    private (Thread? Existing, string Key) ExistingFor(string address, int? accountId)
    {
        var keys = _db.Threads.AsNoTracking().Where(t => t.Channel == ChannelIds.Email && t.Key.ToLower() == address)
            .Select(t => t.Key).Distinct().ToList();
        foreach (var k in keys)
            if (_threads.FindForAccount(ChannelIds.Email, k, accountId) is { } mine) return (mine, k);
        return (null, keys.FirstOrDefault() ?? address);
    }

    /// <summary>The conversation a sent mail is filed under — the one for that address, else a new one.</summary>
    private Thread SentThread(string address, string name, EmailServerSettings mailbox)
    {
        var (existing, threadKey) = ExistingFor(address, mailbox.AccountId);
        var display = name.Length > 0 && !string.Equals(name, address, StringComparison.OrdinalIgnoreCase) ? name : null;
        var thread = _threads.GetOrCreateThread(ChannelIds.Email, threadKey, display, mailbox.AccountId);
        if (existing == null)
        {
            thread.AiEnabled = false; // email has no auto-reply; a person started this one
            _db.SaveChanges();
        }
        if (thread.ContactId == null)
        {
            // a known contact is linked as their own mail would link it; a brand-new address stays unlinked
            var entity = mailbox.AccountId is int aid
                ? _db.ChannelAccounts.AsNoTracking().Where(a => a.Id == aid).Select(a => a.EntityId).FirstOrDefault()
                  ?? thread.EntityId
                : thread.EntityId;
            if (ContactCompanies.MatchEmail(_db, _scoping, address, entity) is { } contact
                && ContactCompanies.MayLink(_db, _scoping, contact.Id, _scoping.ThreadCompany(thread)))
            {
                thread.ContactId = contact.Id;
                _db.SaveChanges();
                try { _scoping.SyncThreadEntity(thread); _db.SaveChanges(); } catch { }
            }
        }
        return thread;
    }

    /// <summary>The first attachment rides on the mail's own message; each extra one is its own message.</summary>
    private void StoreAttachments(ParsedEmail mail, Thread thread, Message msg, EmailServerSettings mailbox,
        string direction, string source, string status)
    {
        for (var i = 0; i < mail.Attachments.Count; i++)
        {
            var att = mail.Attachments[i];
            try
            {
                var rel = MessagingMedia.Save(_dataDir, ChannelIds.Email, thread.Key, att.FileName, att.Content);
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
                    var extra = _threads.AppendMessage(thread, direction, source,
                        $"[attachment] {att.FileName}", status: status,
                        externalId: mail.MessageId.Length > 0 ? $"{mail.MessageId}-att{i}" : "",
                        accountId: mailbox.AccountId, providerTs: mail.Date);
                    MediaStore.Stamp(extra, type, rel, MessagingMedia.ContentTypeFor(att.FileName), att.FileName,
                        att.Content.LongLength);
                    _db.SaveChanges();
                    // every file is something a background AI task may need to read (the first
                    // rides on the mail's own message, queued by the inbound path)
                    if (direction == MessageDirection.In)
                        Ai.Tasks.AiTaskService.Enqueue(_db, _scoping, thread, extra, MessageText(mail));
                }
            }
            catch { /* an attachment that cannot be stored must not lose the mail */ }
        }
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
