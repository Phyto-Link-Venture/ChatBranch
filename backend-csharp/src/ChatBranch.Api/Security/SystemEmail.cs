using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using MimeKit;

namespace ChatBranch.Api.Security;

/// <summary>
/// The organization's "System email" — the mailbox sign-in emails go out from (the
/// welcome email with its set-password link, an admin's password reset link, the
/// settings page's test). ONE organization-wide Setting, <c>system_email</c>, a JSON
/// object <c>{from_name, from_email, host, port, security, username, password}</c>
/// (credentials are never entity-scoped). Edited at Settings → System email
/// (<c>app_keys.manage</c>); the password leaves the server only masked.
///
/// When it is empty, the default mail settings (Channels → default mail,
/// <see cref="EmailConfig.GlobalSettings"/>) are used if those are configured; with
/// neither, sign-in emails can't be sent and the dashboard says so. Sending reuses
/// the email channel's MailKit transport (<see cref="IEmailTransport"/>) and its safe
/// HTML rendering (<see cref="EmailHtml"/>).
/// </summary>
public static class SystemEmail
{
    public const string SettingKey = "system_email";
    public static readonly string[] SecurityModes = { "starttls", "ssl", "none" };

    public const string SourceSystem = "system";
    public const string SourceDefault = "default";
    public const string SourceNone = "none";

    /// <summary>The stored configuration ("" fields = not set).</summary>
    public sealed record Config
    {
        public string FromName { get; init; } = "";
        public string FromEmail { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; } = 587;
        public string Security { get; init; } = "starttls";
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";

        /// <summary>Usable once a server and a sender address are set.</summary>
        public bool Configured => Host.Length > 0 && FromEmail.Length > 0;
        public bool Empty => Host.Length == 0 && FromEmail.Length == 0 && Username.Length == 0
                             && Password.Length == 0 && FromName.Length == 0;

        public EmailServerSettings ToServer() => new()
        {
            Host = Host, Port = Port, Username = Username, Password = Password,
            UseTls = Security != "none", TlsMode = Security,
            From = FromEmail, FromName = FromName,
        };

        public string ToJson() => new JsonObject
        {
            ["from_name"] = FromName, ["from_email"] = FromEmail, ["host"] = Host, ["port"] = Port,
            ["security"] = Security, ["username"] = Username, ["password"] = Password,
        }.ToJsonString();
    }

    /// <summary>The organization-wide row (never an entity override).</summary>
    public static Config Load(ScopingService sc)
    {
        string raw;
        try { raw = sc.SettingValue(SettingKey); } catch { raw = ""; }
        if (string.IsNullOrWhiteSpace(raw)) return new Config();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return new Config();
            string S(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "") : "";
            var port = o.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
                       && p.TryGetInt32(out var n) && n is > 0 and < 65536 ? n : 587;
            var security = S("security").Trim().ToLowerInvariant();
            return new Config
            {
                FromName = S("from_name").Trim(), FromEmail = S("from_email").Trim(), Host = S("host").Trim(),
                Port = port, Security = SecurityModes.Contains(security) ? security : DefaultSecurity(port),
                Username = S("username").Trim(), Password = S("password"),
            };
        }
        catch (JsonException) { return new Config(); }
    }

    public static void Save(ScopingService sc, Config c) =>
        sc.SetSetting(SettingKey, c.Empty ? "" : c.ToJson(), null);

    /// <summary>465 = implicit TLS, 25 without a login is usually a plain relay, else STARTTLS.</summary>
    public static string DefaultSecurity(int port) => port == 465 ? "ssl" : "starttls";

    /// <summary>
    /// The server sign-in emails go through right now: the System email when it is
    /// configured, else the default mail settings when THOSE are, else none.
    /// </summary>
    public static (EmailServerSettings? Server, string Source) Resolve(ScopingService sc)
    {
        var own = Load(sc);
        if (own.Configured) return (own.ToServer(), SourceSystem);
        EmailServerSettings fallback;
        try { fallback = EmailConfig.GlobalSettings(sc); } catch { return (null, SourceNone); }
        return fallback.SmtpConfigured ? (fallback, SourceDefault) : (null, SourceNone);
    }

    // ---- the login address ----------------------------------------------------------

    /// <summary>Why the login address can't go into an email ("" = it can).</summary>
    public const string NoLoginAddress = "no_login_address";
    public const string LocalLoginAddress = "localhost_login_address";

    /// <summary>
    /// The dashboard's public address (<c>FRONTEND_URL</c>, else <c>PUBLIC_BASE_URL</c>)
    /// without a trailing slash, or a problem code: not set / not an http(s) URL, or — on a
    /// real install — pointing at this machine (a localhost link works for nobody who
    /// receives the email; a demo may use one for local testing).
    /// </summary>
    public static (string Base, string Problem) LoginBase(SecurityOptions options)
    {
        var raw = (options.LoginBaseUrl ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.Host.Length == 0)
            return ("", NoLoginAddress);
        if (options.RealInstall && IsLocal(uri.Host)) return ("", LocalLoginAddress);
        return (raw, "");
    }

    private static bool IsLocal(string host)
    {
        var h = host.Trim('[', ']').ToLowerInvariant();
        if (h is "localhost" or "0.0.0.0" or "::" || h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        return System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>The English reason for a problem code (API callers; the dashboard translates the code).</summary>
    public static string Explain(string code) => code switch
    {
        SourceNone or "email_not_configured" =>
            "Sign-in emails can't be sent: set up Settings → System email (or the default mail settings under Channels) first.",
        NoLoginAddress =>
            "The login address isn't configured: set FRONTEND_URL (or PUBLIC_BASE_URL) on the API to the address people open ChatBranch at.",
        LocalLoginAddress =>
            "FRONTEND_URL points to this machine (localhost), so a link in an email would work for nobody else: set it to the public address of the dashboard.",
        _ => code,
    };

    // ---- sending ----------------------------------------------------------------------

    /// <summary>
    /// One system email: plain text as written + its safe HTML rendering (with
    /// <paramref name="buttonLabel"/> on the paragraph that is exactly
    /// <paramref name="buttonUrl"/>). Marked <c>Auto-Submitted: auto-generated</c>.
    /// </summary>
    public static async Task<EmailResult> SendAsync(IEmailTransport transport, EmailServerSettings server,
        string toAddress, string? toName, string subject, string text, string? buttonLabel = null,
        string? buttonUrl = null, CancellationToken ct = default)
    {
        MimeMessage msg;
        try
        {
            msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(server.FromName.Length > 0 ? server.FromName : "ChatBranch", server.From));
            msg.To.Add(new MailboxAddress(OneLine(toName, 120), toAddress));
            msg.Subject = OneLine(subject, 200);
            msg.Date = DateTimeOffset.UtcNow;
            msg.Headers.Add("Auto-Submitted", "auto-generated");
            var at = server.From.LastIndexOf('@');
            msg.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(
                at >= 0 && at < server.From.Length - 1 ? server.From[(at + 1)..] : "chatbranch.local");
            msg.Body = new BodyBuilder
            {
                TextBody = text,
                HtmlBody = EmailHtml.Render(text, buttonLabel, buttonUrl),
            }.ToMessageBody();
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or FormatException)
        {
            return new EmailResult(false, "The sender or recipient address isn't a valid email address.", EmailFailure.Other);
        }
        try
        {
            return await transport.SendAsync(server, msg, ct);
        }
        catch (Exception ex)
        {
            return MailKitEmailTransport.Classify(ex, server);
        }
    }

    /// <summary>One line of display text: control characters out, capped.</summary>
    /// <summary>
    /// Clean up a pasted SMTP password. Always trims, and removes the spaces out
    /// of a Gmail App Password: Google shows it as four groups of four
    /// ("abcd efgh ijkl mnop"), and copying it from Google's own dialog brings
    /// those spaces along. Stored with them, Gmail answers
    /// "5.7.8 Username and Password not accepted" — indistinguishable from a
    /// wrong password, so it is a trap worth closing here.
    ///
    /// ONLY that exact 4x4 shape is de-spaced, so a password that legitimately
    /// contains a space is left untouched.
    /// </summary>
    public static string NormalizePassword(string? raw)
    {
        var s = (raw ?? "").Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z]{4}( [A-Za-z]{4}){3}$")
            ? s.Replace(" ", "")
            : s;
    }

    public static string OneLine(string? s, int max)
    {
        var chars = (s ?? "").Where(c => !char.IsControl(c)).ToArray();
        var t = new string(chars).Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    public static bool IsEmail(string s)
    {
        if (s.Length is 0 or > 254 || s.Contains(' ') || s.Count(c => c == '@') != 1) return false;
        var at = s.IndexOf('@');
        if (at < 1 || at == s.Length - 1 || !s[(at + 1)..].Contains('.') || s.EndsWith('.')) return false;
        return MailAddress.TryCreate(s, out var addr) && addr.Address == s;
    }

    /// <summary>a•••@example.com — for audit rows and answers that should not spell an address out.</summary>
    public static string MaskEmail(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.IndexOf('@');
        if (at <= 0) return e.Length == 0 ? "" : "••••";
        return e[0] + "•••" + e[at..];
    }
}
