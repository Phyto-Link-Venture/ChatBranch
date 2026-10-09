using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Contacts;

/// <summary>One shareable link (or code) for one app.</summary>
public record InviteLink(string Channel, string Label, string? Url, string Text, string Code,
    int? AccountId = null, string? AccountName = null, string? Note = null);

/// <summary>An invite marker found in a customer's first message.</summary>
public sealed class InviteMatch
{
    public required ContactInvite Invite { get; init; }
    public required Contact Contact { get; init; }
    public required string Original { get; init; }
    public required string Stripped { get; init; }
    /// <summary>The thread was linked to the invited contact by this match.</summary>
    public bool Linked { get; set; }
    /// <summary>The thread already belongs to a different contact (left alone).</summary>
    public int? ConflictContactId { get; set; }
}

/// <summary>
/// Invite links: a short code that binds the conversation a customer opens on
/// another app to their existing contact. The code travels in the platform's
/// own deep-link payload where one exists (Telegram <c>/start CODE</c>,
/// Messenger <c>m.me/…?ref=CODE</c>) and as text otherwise (WhatsApp prefill
/// <c>(ref:CODE)</c>, email subject <c>(ref CODE)</c>, Instagram "include this
/// code"); the channels that pass platform referrals as a <c>[ref:CODE] </c>
/// first-message prefix are covered by the same text binder.
///
/// The binder runs in the ONE inbound path BEFORE contact matching: a valid,
/// unexpired code links the thread (entity re-synced), the marker is stripped
/// from the stored text (the original kept in the note), and the use is
/// recorded + audited (<c>contact.invite_used</c>). Anything else — unknown,
/// expired, spent, wrong channel — is ignored silently.
/// </summary>
public class ContactInviteService
{
    public const int CodeLength = 8;
    public const int ExpiryDays = 30;
    /// <summary>No 0/O, 1/I/L — a code read out loud or typed from a QR caption survives.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Channels an invite link can be built for (the app's own deep link or a text code).</summary>
    public static readonly string[] LinkChannels =
    {
        ChatBranch.Domain.Channels.WhatsApp, ChatBranch.Domain.Channels.Telegram,
        ChatBranch.Domain.Channels.Facebook, ChatBranch.Domain.Channels.Instagram,
        ChatBranch.Domain.Channels.Email,
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["messenger"] = ChatBranch.Domain.Channels.Facebook,
        ["fb"] = ChatBranch.Domain.Channels.Facebook,
        ["ig"] = ChatBranch.Domain.Channels.Instagram,
        ["wa"] = ChatBranch.Domain.Channels.WhatsApp,
        ["mail"] = ChatBranch.Domain.Channels.Email,
        ["tg"] = ChatBranch.Domain.Channels.Telegram,
    };

    // "[ref:CODE]" (platform referral prefix) and "(ref:CODE)" (our WhatsApp prefill)
    private static readonly Regex Bracketed = new(@"[\[\(]\s*ref\s*[:#=]?\s*([A-Za-z0-9]{4,32})\s*[\]\)]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Telegram deep link: "/start CODE" (optionally /start@bot)
    private static readonly Regex TelegramStart = new(@"^\s*/start(?:@\w+)?\s+([A-Za-z0-9_-]{4,64})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // free text: "ref CODE", "ref: CODE", "REF#CODE"
    private static readonly Regex Loose = new(@"\bref\s*[:#]?\s*([A-Za-z0-9]{8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ScopingService _sc;
    private readonly TimeProvider _clock;

    public ContactInviteService(AppDbContext db, ScopingService sc, TimeProvider? clock = null)
    {
        _db = db;
        _sc = sc;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A known channel id (aliases accepted), "" for "any", null when unknown.</summary>
    public static string? NormalizeChannel(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s is "" or "any" or "all" or "*") return "";
        if (Aliases.TryGetValue(s, out var mapped)) return mapped;
        return s.Length <= 32 && s.All(c => char.IsLetterOrDigit(c) || c == '_') ? s : null;
    }

    public static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }

    /// <summary>Is this string shaped like one of our codes (after upper-casing)?</summary>
    public static bool LooksLikeCode(string? s) =>
        s != null && s.Length == CodeLength && s.ToUpperInvariant().All(c => Alphabet.Contains(c));

    // ---- create + links -----------------------------------------------------

    /// <summary>Mint a code for this contact (one per request; the caller saves nothing else).</summary>
    public ContactInvite Create(Contact contact, string channel, int? accountId, string createdBy)
    {
        var now = Now;
        var entityId = _db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
            .Select(l => (int?)l.EntityId).OrderBy(e => e).FirstOrDefault();
        string code;
        var guard = 0;
        do { code = NewCode(); } while (_db.ContactInvites.Any(i => i.Code == code) && ++guard < 10);
        var row = new ContactInvite
        {
            Code = code, ContactId = contact.Id, EntityId = entityId, Channel = channel, AccountId = accountId,
            CreatedBy = createdBy, CreatedAt = now, ExpiresAt = now.AddDays(ExpiryDays),
        };
        _db.ContactInvites.Add(row);
        Audit.AuditTrail.Add(_db, "contact.invite_created", $"contact:{contact.Id}",
            $"code {code} for {(channel.Length > 0 ? channel : "any channel")}", entityId: entityId);
        _db.SaveChanges();
        return row;
    }

    /// <summary>
    /// The links for <paramref name="channels"/> (empty = every channel an
    /// account exists for). <paramref name="telegramUsername"/> resolves a
    /// bot's @username when its config does not carry one (a live getMe).
    /// </summary>
    public async Task<List<InviteLink>> LinksAsync(ContactInvite invite, Contact contact,
        IReadOnlyCollection<string> channels, Func<ChannelAccount, Task<string?>>? telegramUsername = null)
    {
        var accounts = _db.ChannelAccounts.AsNoTracking().OrderBy(a => a.Id).ToList();
        var wanted = channels.Count > 0
            ? channels.ToList()
            : LinkChannels.Where(ch => accounts.Any(a => a.Channel == ch)).ToList();
        var entityId = invite.EntityId;
        var links = new List<InviteLink>();
        foreach (var ch in wanted.Distinct())
        {
            ChannelAccount? Pick(Func<ChannelAccount, bool> usable)
            {
                if (invite.AccountId is int pinned)
                {
                    var p = accounts.FirstOrDefault(a => a.Id == pinned && a.Channel == ch);
                    if (p != null) return p;
                }
                var pool = new Routing(_db, _sc).Pool(ch, entityId).Where(usable).ToList();
                if (pool.Count == 0) pool = accounts.Where(a => a.Channel == ch).Where(usable).ToList();
                // a conversation number first: the customer is about to CHAT
                return pool.OrderBy(a => Routing.IsDown(a) ? 1 : 0)
                    .ThenBy(a => ch == ChatBranch.Domain.Channels.WhatsApp
                                 && AccountRoles.Of(a) == AccountRoles.Conversation ? 0 : 1)
                    .ThenBy(a => a.Id).FirstOrDefault();
            }
            var code = invite.Code;
            switch (ch)
            {
                case ChatBranch.Domain.Channels.WhatsApp:
                {
                    var a = Pick(x => WhatsAppNumber(x) != null) ?? Pick(_ => true);
                    var number = a == null ? null : WhatsAppNumber(a);
                    links.Add(number == null
                        ? new InviteLink(ch, "WhatsApp", null, $"Message us on WhatsApp and include: (ref:{code})", code,
                            a?.Id, a?.Name, a == null
                                ? "No WhatsApp number is connected."
                                : $"{a.Name} has no phone number on file — set it on the account to get a wa.me link.")
                        : new InviteLink(ch, "WhatsApp", $"https://wa.me/{number}?text=Hi%20(ref%3A{code})",
                            $"Chat with us on WhatsApp: https://wa.me/{number}?text=Hi%20(ref%3A{code})", code,
                            a!.Id, a.Name));
                    break;
                }
                case ChatBranch.Domain.Channels.Telegram:
                {
                    var a = Pick(_ => true);
                    var user = a == null ? null : Cfg(a, "bot_username", "username").TrimStart('@');
                    if (a != null && string.IsNullOrEmpty(user) && telegramUsername != null)
                        user = (await telegramUsername(a))?.TrimStart('@');
                    links.Add(string.IsNullOrEmpty(user)
                        ? new InviteLink(ch, "Telegram", null, $"Message our Telegram bot: /start {code}", code,
                            a?.Id, a?.Name, a == null
                                ? "No Telegram bot is connected."
                                : "The bot's @username is unknown — run Test on the bot once, then make the link again.")
                        : new InviteLink(ch, "Telegram", $"https://t.me/{user}?start={code}",
                            $"Chat with us on Telegram: https://t.me/{user}?start={code}", code, a!.Id, a.Name));
                    break;
                }
                case ChatBranch.Domain.Channels.Facebook:
                {
                    var a = Pick(_ => true);
                    var page = a == null ? "" : Cfg(a, "page_username", "username") is { Length: > 0 } u ? u : a.ExternalId;
                    links.Add(string.IsNullOrEmpty(page)
                        ? new InviteLink(ch, "Messenger", null, $"Message our page on Messenger and include: ref {code}",
                            code, a?.Id, a?.Name, "No Messenger page is connected.")
                        : new InviteLink(ch, "Messenger", $"https://m.me/{Uri.EscapeDataString(page)}?ref={code}",
                            $"Chat with us on Messenger: https://m.me/{Uri.EscapeDataString(page)}?ref={code}", code,
                            a!.Id, a.Name));
                    break;
                }
                case ChatBranch.Domain.Channels.Instagram:
                {
                    // ig.me carries no referral payload — the code rides as text
                    var a = Pick(_ => true);
                    var user = a == null ? "" : Cfg(a, "username", "ig_username").TrimStart('@');
                    links.Add(new InviteLink(ch, "Instagram",
                        user.Length > 0 ? $"https://ig.me/m/{Uri.EscapeDataString(user)}" : null,
                        user.Length > 0
                            ? $"Message us on Instagram (https://ig.me/m/{Uri.EscapeDataString(user)}) and include this code: ref {code}"
                            : $"Message us on Instagram and include this code: ref {code}",
                        code, a?.Id, a?.Name,
                        a == null ? "No Instagram account is connected."
                            : user.Length == 0 ? "Set the Instagram @username on the account to get an ig.me link."
                            : "Instagram links carry no code — the customer pastes it into the first message."));
                    break;
                }
                case ChatBranch.Domain.Channels.Email:
                {
                    var a = Pick(Routing.IsConfigured) ?? Pick(_ => true);
                    var from = a == null ? EmailConfig.Global(_sc, "smtp_from")
                        : EmailConfig.ForAccount(a, _sc).From;
                    var subject = Uri.EscapeDataString($"Hello (ref {code})");
                    links.Add(string.IsNullOrWhiteSpace(from) || !from.Contains('@')
                        ? new InviteLink(ch, "Email", null, $"Email us with this in the subject: (ref {code})", code,
                            a?.Id, a?.Name, "No mailbox address is configured.")
                        : new InviteLink(ch, "Email", $"mailto:{from.Trim()}?subject={subject}",
                            $"Email us at {from.Trim()} with the subject \"Hello (ref {code})\"", code, a?.Id, a?.Name));
                    break;
                }
                default:
                {
                    // LINE / Viber / WeChat…: their referral payload reaches the
                    // first message as "[ref:CODE] " — the text binder handles it
                    var a = accounts.FirstOrDefault(x => x.Channel == ch);
                    links.Add(new InviteLink(ch, ch.Length > 0 ? char.ToUpperInvariant(ch[0]) + ch[1..] : ch, null,
                        $"Send us this code in your first message: [ref:{code}]", code, a?.Id, a?.Name,
                        "This app has no invite link — the customer sends the code as text."));
                    break;
                }
            }
        }
        return links;
    }

    /// <summary>The account's own WhatsApp number (digits): config keys, a numeric name, or a numeric Baileys id.</summary>
    public static string? WhatsAppNumber(ChannelAccount a)
    {
        if (a.Channel != ChatBranch.Domain.Channels.WhatsApp) return null;
        if (BusinessRedirect.PhoneNumberOf(a) is { } n) return n;
        foreach (var key in new[] { "me", "wa_number" })
            if (a.Config.TryGetValue(key, out var v) && PhoneMatch.Digits(v?.ToString()?.Split(':', '@')[0])
                    is { Length: >= 8 } d)
                return d;
        var ext = PhoneMatch.Digits(a.ExternalId);
        return a.Provider == Providers.Baileys && ext.Length >= 8 && ext == a.ExternalId ? ext : null;
    }

    private static string Cfg(ChannelAccount a, params string[] keys)
    {
        foreach (var k in keys)
            if (a.Config.TryGetValue(k, out var v) && v != null)
            {
                var s = v is JsonElement el && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : v.ToString() ?? "";
                if (s.Trim().Length > 0) return s.Trim();
            }
        return "";
    }

    // ---- binding --------------------------------------------------------------

    /// <summary>Every code-shaped token in a message, with the text minus its marker.</summary>
    public static List<(string Code, string Stripped)> Candidates(string? text)
    {
        var outv = new List<(string, string)>();
        var s = text ?? "";
        if (s.Length == 0 || s.Length > 4000) return outv;
        var tg = TelegramStart.Match(s);
        if (tg.Success)
            outv.Add((tg.Groups[1].Value, ("/start" + s[(tg.Index + tg.Length)..]).Trim()));
        foreach (Match m in Bracketed.Matches(s))
            outv.Add((m.Groups[1].Value, Tidy(s.Remove(m.Index, m.Length))));
        foreach (Match m in Loose.Matches(s))
            outv.Add((m.Groups[1].Value, Tidy(s.Remove(m.Index, m.Length))));
        return outv;
    }

    private static string Tidy(string s) =>
        Regex.Replace(Regex.Replace(s, @"[ \t]{2,}", " "), @"\s+([,.!?])", "$1").Trim();

    /// <summary>A valid invite for this message on this channel, or null.</summary>
    public InviteMatch? Find(string channel, string? text)
    {
        foreach (var (raw, stripped) in Candidates(text))
        {
            var code = raw.ToUpperInvariant();
            if (!LooksLikeCode(code)) continue;
            var invite = _db.ContactInvites.FirstOrDefault(i => i.Code == code);
            if (invite == null || !Usable(invite, channel)) continue;
            var contact = _db.Contacts.FirstOrDefault(c => c.Id == invite.ContactId);
            if (contact == null) continue;
            return new InviteMatch
            {
                Invite = invite, Contact = contact, Original = text ?? "",
                Stripped = stripped.Length > 0 ? stripped : "(opened an invite link)",
            };
        }
        return null;
    }

    /// <summary>Unexpired, for this channel (or any), not already spent on it.</summary>
    public bool Usable(ContactInvite invite, string channel)
    {
        if (invite.ExpiresAt <= Now) return false;
        if (invite.Channel.Length > 0 && invite.Channel != channel) return false;
        if (invite.Channel.Length > 0) return invite.UsedAt == null;
        return !UsedOn(invite).Contains(channel);
    }

    private static HashSet<string> UsedOn(ContactInvite i) =>
        i.UsedChannels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    /// <summary>
    /// The inbound step, BEFORE contact matching: find a code in the message and
    /// link the thread to its contact. Returns the match (its <see cref="InviteMatch.Stripped"/>
    /// text is what gets stored) or null. Never throws.
    /// </summary>
    public InviteMatch? TryBind(Thread thread, string? text)
    {
        try
        {
            var m = Find(thread.Channel, text);
            if (m == null) return null;
            Link(thread, m);
            return m;
        }
        catch { return null; }
    }

    /// <summary>A Messenger referral (<c>m.me/…?ref=CODE</c>) for this conversation. Never throws.</summary>
    public InviteMatch? TryBindReferral(Thread thread, string? refCode)
    {
        try
        {
            var code = (refCode ?? "").Trim().ToUpperInvariant();
            if (!LooksLikeCode(code)) return null;
            var invite = _db.ContactInvites.FirstOrDefault(i => i.Code == code);
            if (invite == null || !Usable(invite, thread.Channel)) return null;
            var contact = _db.Contacts.FirstOrDefault(c => c.Id == invite.ContactId);
            if (contact == null) return null;
            var m = new InviteMatch
            {
                Invite = invite, Contact = contact, Original = $"(referral {code})", Stripped = "",
            };
            Link(thread, m);
            return m;
        }
        catch { return null; }
    }

    private void Link(Thread thread, InviteMatch m)
    {
        if (thread.ContactId is int linked && linked != m.Contact.Id)
        {
            m.ConflictContactId = linked;
            return;
        }
        if (!SameTenant(thread, m.Contact)) { m.ConflictContactId = -1; return; }
        if (thread.ContactId == m.Contact.Id) return;   // already theirs — just record the use
        thread.ContactId = m.Contact.Id;
        thread.Contact = m.Contact;
        if (thread.AccountId is int aid && thread.Account == null)
            thread.Account = _db.ChannelAccounts.FirstOrDefault(a => a.Id == aid);
        _sc.SyncThreadEntity(thread, save: false);
        thread.UpdatedAt = Now;
        _db.SaveChanges();
        m.Linked = true;
    }

    /// <summary>A code never crosses tenants: the receiving inbox's tenant must be the contact's.</summary>
    private bool SameTenant(Thread thread, Contact contact)
    {
        if (contact.TenantId is not int tid || thread.AccountId is not int aid) return true;
        var entityId = _db.ChannelAccounts.AsNoTracking().Where(a => a.Id == aid).Select(a => a.EntityId).FirstOrDefault();
        if (entityId is not int eid) return true;
        var accountTenant = _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId)
            .FirstOrDefault();
        return accountTenant == null || accountTenant == tid;
    }

    /// <summary>
    /// After the message is stored: mark the code used, write the system note
    /// (with the original text) and the audit row. Never throws.
    /// </summary>
    public void Complete(InviteMatch? m, Thread thread, Message? stored)
    {
        if (m == null) return;
        try
        {
            var now = Now;
            if (m.ConflictContactId != null)
            {
                var other = m.ConflictContactId > 0
                    ? _db.Contacts.AsNoTracking().Where(c => c.Id == m.ConflictContactId).Select(c => c.Name).FirstOrDefault()
                    : null;
                Note(thread, other != null
                    ? $"Invite code {m.Invite.Code} for {m.Contact.Name} arrived, but this chat is already linked to {other} — relink it by hand if it is the same person. Original message: \"{Trim(m.Original, 300)}\""
                    : $"Invite code {m.Invite.Code} belongs to another organization — ignored.", now);
                _db.SaveChanges();
                return;
            }
            var used = UsedOn(m.Invite);
            used.Add(thread.Channel);
            m.Invite.UsedChannels = string.Join(",", used.OrderBy(x => x));
            m.Invite.UsedAt ??= now;
            m.Invite.UsedThreadId = thread.Id;
            Note(thread, $"Linked to {m.Contact.Name} via invite link (code {m.Invite.Code})."
                         + (m.Original.Length > 0 && !m.Original.StartsWith("(referral")
                             ? $" Original message: \"{Trim(m.Original, 300)}\"" : ""), now);
            Audit.AuditTrail.Add(_db, "contact.invite_used", $"{thread.Channel}:{thread.Key}",
                $"code {m.Invite.Code} → contact {m.Contact.Id} ({m.Contact.Name})", actor: "invite",
                source: AuditSources.Webhook, entityId: thread.EntityId);
            thread.UpdatedAt = now;
            _db.SaveChanges();
        }
        catch { /* binding bookkeeping never breaks an inbound */ }
    }

    private void Note(Thread thread, string text, DateTime now) =>
        _db.Messages.Add(new Message
        {
            ThreadId = thread.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
            Private = true, Text = text, Status = "note", AccountId = thread.AccountId, CreatedAt = now,
        });

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Wire shape of an invite row.</summary>
    public Dictionary<string, object?> Payload(ContactInvite i, List<InviteLink>? links = null) => new()
    {
        ["id"] = i.Id,
        ["code"] = i.Code,
        ["contact_id"] = i.ContactId,
        ["channel"] = i.Channel,
        ["account_id"] = i.AccountId,
        ["created_by"] = i.CreatedBy,
        ["created_at"] = Iso(i.CreatedAt),
        ["expires_at"] = Iso(i.ExpiresAt),
        ["used_at"] = i.UsedAt is DateTime u ? Iso(u) : null,
        ["used_channels"] = UsedOn(i).OrderBy(x => x).ToList(),
        ["expired"] = i.ExpiresAt <= Now,
        ["links"] = links?.Select(l => new Dictionary<string, object?>
        {
            ["channel"] = l.Channel, ["label"] = l.Label, ["url"] = l.Url, ["text"] = l.Text,
            ["code"] = l.Code, ["account_id"] = l.AccountId, ["account_name"] = l.AccountName,
            ["note"] = l.Note, ["qr_svg"] = l.Url == null ? null : QrSvg.Render(l.Url),
        }).ToList(),
    };

    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o");
}
