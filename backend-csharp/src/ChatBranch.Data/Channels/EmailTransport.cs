using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace ChatBranch.Data.Channels;

/// <summary>Why an SMTP operation failed — drives the account-health verdict.</summary>
public enum EmailFailure
{
    None,
    /// <summary>The server refused the login — the stored credentials are wrong or revoked.</summary>
    Auth,
    /// <summary>Could not reach / talk to the server (DNS, refused, timeout, TLS handshake).</summary>
    Connect,
    /// <summary>The server refused THIS recipient — never the account's fault.</summary>
    Recipient,
    Other,
}

public sealed record EmailResult(bool Ok, string Detail = "", EmailFailure Failure = EmailFailure.None)
{
    public static readonly EmailResult Success = new(true);
}

/// <summary>
/// SMTP out (port of messaging/email_client.py). The interface is the seam the
/// tests fake; <see cref="MailKitEmailTransport"/> is the real thing.
/// </summary>
public interface IEmailTransport
{
    /// <summary>Connect (TLS per settings), log in when a username is set, send.</summary>
    Task<EmailResult> SendAsync(EmailServerSettings server, MimeMessage message, CancellationToken ct = default);

    /// <summary>Connect + log in, send nothing — the "Test connection" button and the health checker.</summary>
    Task<EmailResult> ProbeAsync(EmailServerSettings server, CancellationToken ct = default);
}

public sealed class MailKitEmailTransport : IEmailTransport
{
    private readonly int _timeoutMs;
    public MailKitEmailTransport(int timeoutMs = 20_000) => _timeoutMs = timeoutMs;

    /// <summary>465 = implicit TLS; otherwise STARTTLS when enabled (required, not opportunistic).</summary>
    public static SecureSocketOptions Security(int port, bool useTls) =>
        port == 465 ? SecureSocketOptions.SslOnConnect
        : useTls ? SecureSocketOptions.StartTls
        : SecureSocketOptions.None;

    public Task<EmailResult> SendAsync(EmailServerSettings server, MimeMessage message,
        CancellationToken ct = default) =>
        RunAsync(server, (client, token) => client.SendAsync(message, token), ct);

    public Task<EmailResult> ProbeAsync(EmailServerSettings server, CancellationToken ct = default) =>
        RunAsync(server, null, ct);

    private async Task<EmailResult> RunAsync(EmailServerSettings server,
        Func<SmtpClient, CancellationToken, Task>? work, CancellationToken ct)
    {
        if (server.Host.Length == 0)
            return new EmailResult(false, "SMTP host not configured.", EmailFailure.Other);
        using var client = new SmtpClient { Timeout = _timeoutMs };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs + 5_000);
        try
        {
            await client.ConnectAsync(server.Host, server.Port, Security(server.Port, server.UseTls), cts.Token);
            if (server.Username.Length > 0)
                await client.AuthenticateAsync(server.Username, server.Password, cts.Token);
            if (work != null) await work(client, cts.Token);
            try { await client.DisconnectAsync(true, cts.Token); } catch { /* the work is done */ }
            return EmailResult.Success;
        }
        catch (Exception ex)
        {
            return Classify(ex, server);
        }
    }

    /// <summary>Map a MailKit/socket failure onto a health-relevant category.</summary>
    public static EmailResult Classify(Exception ex, EmailServerSettings server)
    {
        var detail = Scrub(ex.Message, server);
        return ex switch
        {
            AuthenticationException => new(false, $"SMTP login refused: {detail}", EmailFailure.Auth),
            SmtpCommandException sc when sc.ErrorCode == SmtpErrorCode.RecipientNotAccepted =>
                new(false, $"recipient refused: {detail}", EmailFailure.Recipient),
            SmtpCommandException sc when (int)sc.StatusCode is 530 or 534 or 535 =>
                new(false, $"SMTP login refused: {detail}", EmailFailure.Auth),
            SmtpCommandException => new(false, $"SMTP refused the message: {detail}", EmailFailure.Other),
            NotSupportedException => new(false, $"SMTP: {detail}", EmailFailure.Other),
            SslHandshakeException => new(false, $"TLS handshake failed: {detail}", EmailFailure.Connect),
            SocketException or IOException or SmtpProtocolException or ServiceNotConnectedException
                or OperationCanceledException or TimeoutException =>
                new(false, $"SMTP server unreachable: {detail}", EmailFailure.Connect),
            _ => new(false, $"SMTP error: {detail}", EmailFailure.Other),
        };
    }

    /// <summary>Error text for the thread / audit: one line, capped, and never the password.</summary>
    public static string Scrub(string? text, EmailServerSettings server)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (server.Password.Length > 0) s = s.Replace(server.Password, "••••");
        if (server.ImapPassword.Length > 0) s = s.Replace(server.ImapPassword, "••••");
        return s.Length > 200 ? s[..200] : s;
    }
}

/// <summary>A file attached to an outgoing mail.</summary>
public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

/// <summary>
/// Builds the outgoing MIME message: plain-text body, From = the mailbox
/// address, a fresh Message-ID and — when the conversation has history —
/// In-Reply-To / References so the customer's mail client threads the reply.
/// Pure (no I/O) so it is unit-testable.
/// </summary>
public static class EmailComposer
{
    /// <summary>Most mail clients cap References; the newest ids are the ones that matter.</summary>
    public const int MaxReferences = 10;

    /// <summary>"Re: &lt;subject&gt;" without stacking prefixes; null when there is no subject.</summary>
    public static string? ReplySubject(string? lastSubject)
    {
        var s = (lastSubject ?? "").Trim();
        if (s.Length == 0) return null;
        return s.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? s : $"Re: {s}";
    }

    /// <summary>Strip the angle brackets MimeKit adds itself.</summary>
    public static string Bare(string id) => (id ?? "").Trim().Trim('<', '>').Trim();

    /// <summary>A header Message-ID as stored on the message ("&lt;id@host&gt;").</summary>
    public static bool LooksLikeMessageId(string? id) =>
        id is { Length: > 2 } s && s[0] == '<' && s[^1] == '>' && s.Contains('@');

    public static MimeMessage Build(EmailServerSettings server, string toAddress, string? toName,
        string subject, string body, IReadOnlyList<string> references, string? inReplyTo,
        IEnumerable<EmailAttachment>? attachments, out string messageId)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(server.FromName.Length > 0 ? server.FromName : "", server.From));
        msg.To.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(toName) || string.Equals(toName, toAddress, StringComparison.OrdinalIgnoreCase)
                ? "" : toName.Trim(),
            toAddress));
        msg.Subject = subject;
        msg.Date = DateTimeOffset.UtcNow;

        var at = server.From.LastIndexOf('@');
        var domain = at >= 0 && at < server.From.Length - 1 ? server.From[(at + 1)..] : "chatbranch.local";
        var id = MimeUtils.GenerateMessageId(domain);
        msg.MessageId = id;
        messageId = $"<{id}>";

        // a malformed id from some other mailer must not cost the reply itself
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            try { msg.InReplyTo = Bare(inReplyTo); } catch { }
        foreach (var r in references.Select(Bare).Where(r => r.Length > 0).Distinct().TakeLast(MaxReferences))
            try { msg.References.Add(r); } catch { }

        var builder = new BodyBuilder { TextBody = body };
        foreach (var a in attachments ?? Array.Empty<EmailAttachment>())
            builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        msg.Body = builder.ToMessageBody();
        return msg;
    }
}
