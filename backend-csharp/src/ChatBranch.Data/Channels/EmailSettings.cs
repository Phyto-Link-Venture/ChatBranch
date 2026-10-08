using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// One mailbox's server settings — SMTP for sending, IMAP for receiving — in a
/// single shape whether it came from an email <see cref="ChannelAccount"/> or
/// from the organization-wide fallback settings. Port of msg_config's
/// <c>email_settings()</c> / <c>email_account_settings()</c>.
/// </summary>
public sealed record EmailServerSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    /// <summary>STARTTLS on a non-465 port (465 is always implicit TLS).</summary>
    public bool UseTls { get; init; } = true;
    public string From { get; init; } = "";
    public string FromName { get; init; } = "";
    public string DefaultSubject { get; init; } = EmailConfig.DefaultSubject;

    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; } = 993;
    public string ImapUsername { get; init; } = "";
    public string ImapPassword { get; init; } = "";

    /// <summary>The email ChannelAccount these came from; null = the global fallback.</summary>
    public int? AccountId { get; init; }

    public bool SmtpConfigured => Host.Length > 0 && From.Length > 0;
    public bool ImapConfigured => ImapHost.Length > 0 && ImapUsername.Length > 0 && ImapPassword.Length > 0;

    /// <summary>Human label for errors and sync reports — never carries a secret.</summary>
    public string Label => AccountId is int id ? $"{From} (account #{id})" : $"{From} (default mail settings)";
}

/// <summary>
/// Where email configuration comes from. Organization-wide keys are Setting rows
/// (Channels → Advanced, <c>/api/messaging/settings</c>) over the process env;
/// a mailbox account carries its own SMTP (and optionally IMAP) settings in
/// <see cref="ChannelAccount.Config"/>: <c>host port username password use_tls
/// from_email</c> + <c>imap_host imap_port imap_username imap_password</c>.
/// IMAP falls back per field: host/port to the global <c>imap_host</c> /
/// <c>imap_port</c>, login to the account's own SMTP login (the same login works
/// for both on Gmail, Outlook and most hosts).
/// </summary>
public static class EmailConfig
{
    public const string DefaultSubject = "Regarding your account";

    /// <summary>Global key → env fallback (msg_config._EMAIL_ENV).</summary>
    public static readonly IReadOnlyDictionary<string, string> GlobalEnv = new Dictionary<string, string>
    {
        ["smtp_host"] = "SMTP_HOST",
        ["smtp_port"] = "SMTP_PORT",
        ["smtp_username"] = "SMTP_USERNAME",
        ["smtp_password"] = "SMTP_PASSWORD",
        ["smtp_from"] = "SMTP_FROM",
        ["smtp_tls"] = "SMTP_TLS",
        ["email_subject"] = "EMAIL_DEFAULT_SUBJECT",
        ["imap_host"] = "IMAP_HOST",
        ["imap_port"] = "IMAP_PORT",
    };

    /// <summary>Global Setting row (entity null) wins; else the env var; else "".</summary>
    public static string Global(ScopingService sc, string key)
    {
        string v;
        try { v = sc.SettingValue(key); } catch { v = ""; }
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return GlobalEnv.TryGetValue(key, out var env)
            ? (Environment.GetEnvironmentVariable(env) ?? "").Trim()
            : "";
    }

    public static string Subject(ScopingService sc) =>
        Global(sc, "email_subject") is { Length: > 0 } s ? s : DefaultSubject;

    private static bool Truthy(string raw, bool dflt) =>
        raw.Length == 0 ? dflt : raw.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off");

    private static int Port(string raw, int dflt) =>
        int.TryParse(raw.Trim(), out var n) && n is > 0 and < 65536 ? n : dflt;

    /// <summary>The organization-wide mailbox (SMTP + IMAP share one login, as in Django).</summary>
    public static EmailServerSettings GlobalSettings(ScopingService sc)
    {
        var user = Global(sc, "smtp_username");
        var pass = Global(sc, "smtp_password");
        return new EmailServerSettings
        {
            Host = Global(sc, "smtp_host"),
            Port = Port(Global(sc, "smtp_port"), 587),
            Username = user,
            Password = pass,
            UseTls = Truthy(Global(sc, "smtp_tls"), true),
            From = Global(sc, "smtp_from"),
            FromName = "",
            DefaultSubject = Subject(sc),
            ImapHost = Global(sc, "imap_host"),
            ImapPort = Port(Global(sc, "imap_port"), 993),
            ImapUsername = user,
            ImapPassword = pass,
        };
    }

    /// <summary>A mailbox account's settings (the default subject stays global).</summary>
    public static EmailServerSettings ForAccount(ChannelAccount a, ScopingService sc)
    {
        var cfg = a.Config ?? new Dictionary<string, object?>();
        var user = Cfg(cfg, "username");
        var pass = Cfg(cfg, "password", trim: false);
        var from = Cfg(cfg, "from_email") is { Length: > 0 } f ? f : a.ExternalId;
        var name = (a.Name ?? "").Trim();
        return new EmailServerSettings
        {
            AccountId = a.Id,
            Host = Cfg(cfg, "host"),
            Port = Port(Cfg(cfg, "port"), 587),
            Username = user,
            Password = pass,
            UseTls = Truthy(Cfg(cfg, "use_tls"), true),
            From = from,
            // a mailbox named after its own address needs no display name
            FromName = string.Equals(name, from, StringComparison.OrdinalIgnoreCase) ? "" : name,
            DefaultSubject = Subject(sc),
            ImapHost = Cfg(cfg, "imap_host") is { Length: > 0 } ih ? ih : Global(sc, "imap_host"),
            ImapPort = Port(Cfg(cfg, "imap_port") is { Length: > 0 } ip ? ip : Global(sc, "imap_port"), 993),
            ImapUsername = Cfg(cfg, "imap_username") is { Length: > 0 } iu ? iu : user,
            ImapPassword = Cfg(cfg, "imap_password", trim: false) is { Length: > 0 } iw ? iw : pass,
        };
    }

    /// <summary>
    /// An email account is usable once its SMTP host + login are set
    /// (msg_config.email_account_configured — the same rule the accounts API reports).
    /// </summary>
    public static bool AccountConfigured(ChannelAccount a)
    {
        var cfg = a.Config ?? new Dictionary<string, object?>();
        return a.Channel == ChannelIds.Email && Cfg(cfg, "host").Length > 0
               && Cfg(cfg, "username").Length > 0 && Cfg(cfg, "password").Length > 0;
    }

    /// <summary>A Config value as text (values come back from SQLite as JsonElement).</summary>
    public static string Cfg(Dictionary<string, object?> cfg, string key, bool trim = true)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return "";
        string s;
        if (v is JsonElement el)
            s = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        else if (v is bool b) s = b ? "true" : "false";
        else s = v.ToString() ?? "";
        return trim ? s.Trim() : s;
    }
}
