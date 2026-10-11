using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>The server's explicit <see cref="EmailServerSettings.TlsMode"/> when it has one, else the port rule.</summary>
    public static SecureSocketOptions Security(EmailServerSettings server) =>
        (server.TlsMode ?? "").Trim().ToLowerInvariant() switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => Security(server.Port, server.UseTls),
        };

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
            await client.ConnectAsync(server.Host, server.Port, Security(server), cts.Token);
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
/// Builds the outgoing MIME message: a multipart/alternative body (the plain
/// text as typed + its safe HTML rendering, <see cref="EmailHtml"/>), wrapped in
/// multipart/mixed when there are attachments; From = the mailbox address, a
/// fresh Message-ID and — when the conversation has history — In-Reply-To /
/// References so the customer's mail client threads the reply. Pure (no I/O)
/// so it is unit-testable.
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

        // multipart/alternative: the text as typed + an HTML rendering of the SAME
        // text (escaped, never raw HTML); attachments wrap it in multipart/mixed
        var builder = new BodyBuilder { TextBody = body, HtmlBody = EmailHtml.Render(body) };
        foreach (var a in attachments ?? Array.Empty<EmailAttachment>())
            builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        msg.Body = builder.ToMessageBody();
        return msg;
    }
}

/// <summary>
/// The HTML alternative of an outgoing mail, rendered SAFELY from the plain
/// text the operator (or the AI) wrote — there is no HTML compose, so nothing
/// typed is ever passed through as markup:
/// <list type="bullet">
///   <item>every character is HTML-escaped (<c>&amp; &lt; &gt; " '</c>);</item>
///   <item>blank lines separate paragraphs, a single newline is a <c>&lt;br&gt;</c>,
///         leading indentation is kept;</item>
///   <item>http(s) URLs and email addresses become links whose href is built
///         from the escaped match (only <c>http://</c>, <c>https://</c> and
///         <c>mailto:</c> — a <c>javascript:</c> "link" stays text), with
///         trailing punctuation left outside the link;</item>
///   <item>a minimal inline-styled wrapper (mail clients drop &lt;style&gt;).</item>
/// </list>
/// Pure, so it is unit-tested directly.
/// </summary>
public static partial class EmailHtml
{
    private const string LinkStyle = "color:#c2410c;text-decoration:underline;";
    private const string ButtonStyle =
        "display:inline-block;padding:12px 22px;border-radius:8px;background-color:#1f2328;color:#ffffff;" +
        "font-weight:600;text-decoration:none;";

    [GeneratedRegex(
        @"(?<url>\bhttps?://[^\s<>""`]+)|(?<![A-Za-z0-9._%+\-])(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,})",
        RegexOptions.IgnoreCase)]
    private static partial Regex Linkable();

    [GeneratedRegex(@"\n(?:[ \t]*\n)+")]
    private static partial Regex BlankLines();

    public static string Render(string? text) => Render(text, null, null);

    /// <summary>
    /// <see cref="Render(string?)"/> with one call-to-action: a paragraph that is exactly
    /// <paramref name="buttonUrl"/> (an http(s) URL) becomes a button labelled
    /// <paramref name="buttonLabel"/>, with the address itself as a small link under it for
    /// mail clients that hide buttons. Everything stays escaped — used by the system's own
    /// sign-in emails (welcome email, password links), never for typed replies.
    /// </summary>
    public static string Render(string? text, string? buttonLabel, string? buttonUrl)
    {
        var button = !string.IsNullOrWhiteSpace(buttonLabel) && buttonUrl is { Length: > 0 } u
                     && (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                         || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ? u.Trim() : null;
        var body = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"></head>\n")
          .Append("<body style=\"margin:0;padding:0;background-color:#ffffff;\">\n")
          .Append("<div style=\"max-width:640px;padding:16px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',")
          .Append("Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.5;color:#1f2328;\">\n");
        foreach (var paragraph in BlankLines().Split(body))
        {
            // whitespace-only lines at a paragraph's edges (the text's own start/end) are not content
            var lines = paragraph.Split('\n').SkipWhile(l => l.Trim().Length == 0).Reverse()
                .SkipWhile(l => l.Trim().Length == 0).Reverse().ToList();
            if (lines.Count == 0) continue;
            if (button != null && lines.Count == 1 && lines[0].Trim() == button)
            {
                sb.Append("<p style=\"margin:0 0 1.25em 0;\"><a href=\"").Append(Escape(button))
                  .Append("\" style=\"").Append(ButtonStyle).Append("\">").Append(Escape(buttonLabel!.Trim()))
                  .Append("</a></p>\n<p style=\"margin:0 0 1em 0;font-size:12px;line-height:1.4;color:#57606a;word-break:break-all;\">")
                  .Append("<a href=\"").Append(Escape(button)).Append("\" style=\"").Append(LinkStyle).Append("\">")
                  .Append(Escape(button)).Append("</a></p>\n");
                continue;
            }
            sb.Append("<p style=\"margin:0 0 1em 0;\">")
              .Append(string.Join("<br>\n", lines.Select(Line)))
              .Append("</p>\n");
        }
        sb.Append("</div>\n</body></html>\n");
        return sb.ToString();
    }

    /// <summary>One line: leading indentation kept, everything escaped, URLs and addresses linked.</summary>
    public static string Line(string line)
    {
        var sb = new StringBuilder();
        var i = 0;
        for (; i < line.Length && line[i] is ' ' or '\t'; i++)
            sb.Append(line[i] == '\t' ? "&nbsp;&nbsp;&nbsp;&nbsp;" : "&nbsp;");
        var rest = line[i..].TrimEnd();
        var last = 0;
        foreach (Match m in Linkable().Matches(rest))
        {
            if (m.Index < last) continue;
            string href, shown;
            if (m.Groups["url"].Success)
            {
                shown = TrimUrl(m.Value);
                if (shown.Length <= shown.IndexOf("://", StringComparison.Ordinal) + 3) continue; // "https://" alone
                href = shown;
            }
            else
            {
                shown = m.Value;
                href = $"mailto:{shown}";
            }
            sb.Append(Escape(rest[last..m.Index]))
              .Append("<a href=\"").Append(Escape(href)).Append("\" style=\"").Append(LinkStyle).Append("\">")
              .Append(Escape(shown)).Append("</a>");
            last = m.Index + shown.Length;
        }
        sb.Append(Escape(rest[last..]));
        return sb.ToString();
    }

    /// <summary>Sentence punctuation after a URL is not part of it; neither is an unbalanced closing bracket.</summary>
    public static string TrimUrl(string url)
    {
        var s = url;
        while (s.Length > 0)
        {
            var c = s[^1];
            if (".,;:!?'*_~".Contains(c)) { s = s[..^1]; continue; }
            if ((c == ')' && s.Count(x => x == '(') < s.Count(x => x == ')'))
                || (c == ']' && s.Count(x => x == '[') < s.Count(x => x == ']'))
                || (c == '}' && s.Count(x => x == '{') < s.Count(x => x == '}')))
            {
                s = s[..^1];
                continue;
            }
            break;
        }
        return s;
    }

    /// <summary>HTML-escape text and attribute values alike.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
