using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The one place an outbound message becomes real or sandbox. If the resolved
/// account is a configured provider account it hits the provider and the stored
/// message carries the real id + status; otherwise it stores locally with status
/// "sandbox" (the zero-credential default). The queue and the manual send
/// endpoints all go through here so behaviour is identical:
///
///   * WhatsApp Cloud — text, real TEMPLATE messages (<see cref="SendTemplate"/>)
///     and media (upload → send by id, <see cref="SendMedia"/>);
///   * WhatsApp Baileys — text through the Node gateway (templates render as text);
///   * Messenger / Instagram — the Send API with the page token;
///   * Email (SMTP, <see cref="EmailChannel"/>) and Telegram (Bot API,
///     <see cref="TelegramChannel"/>) — they resolve their own mailbox / bot.
///
/// A provider failure is recorded honestly as a <c>failed</c> system message;
/// an ACCOUNT-level Meta error moves the account's health (per-recipient codes
/// never do), a success heals it, and 131047 closes the 24h window.
/// </summary>
public class OutboundSender
{
    private readonly AppDbContext _db;
    private readonly ThreadStore _threads;
    private readonly IWhatsAppCloudClient _whatsapp;
    private readonly ChatBranch.Data.Sidecars.IBaileysGateway _baileys;
    private readonly IMetaMessagingClient? _meta;
    private readonly MediaStore? _media;
    private readonly EmailChannel? _email;
    private readonly TelegramChannel? _telegram;

    public OutboundSender(AppDbContext db, ThreadStore threads, IWhatsAppCloudClient whatsapp,
        ChatBranch.Data.Sidecars.IBaileysGateway baileys, IMetaMessagingClient? meta = null,
        MediaStore? media = null, EmailChannel? email = null, TelegramChannel? telegram = null)
    {
        _db = db;
        _threads = threads;
        _whatsapp = whatsapp;
        _baileys = baileys;
        _meta = meta;
        _media = media;
        _email = email;
        _telegram = telegram;
    }

    private static bool IsSocial(Thread thread) =>
        thread.Channel is ChatBranch.Domain.Channels.Facebook or ChatBranch.Domain.Channels.Instagram;

    private bool CloudLive(Thread thread, ChannelAccount? account) =>
        thread.Channel == ChatBranch.Domain.Channels.WhatsApp && account != null
        && account.Provider != Providers.Baileys && _whatsapp.IsConfigured(account);

    private bool BaileysLive(Thread thread, ChannelAccount? account) =>
        thread.Channel == ChatBranch.Domain.Channels.WhatsApp && account != null
        && account.Provider == Providers.Baileys && _baileys.IsConfigured && !string.IsNullOrEmpty(account.ExternalId);

    private bool SocialLive(Thread thread, ChannelAccount? account) =>
        IsSocial(thread) && account != null && account.Channel == thread.Channel
        && _meta != null && _meta.IsConfigured(account);

    private ChannelAccount? Account(int? id) => id is int pk ? _db.ChannelAccounts.Find(pk) : null;

    /// <summary>
    /// True when this thread would send for REAL (a configured Cloud, Baileys,
    /// Messenger or Instagram account carries it) rather than storing a sandbox
    /// message. Pacing and the queue only make sense for real sends — a sandbox
    /// demo must stay instant.
    /// </summary>
    public bool WouldSendLive(Thread thread, int? accountId = null)
    {
        // email + telegram resolve their own mailbox / bot (EmailChannel, TelegramChannel)
        if (thread.Channel == ChatBranch.Domain.Channels.Email)
            return _email?.WouldSendLive(thread, accountId ?? thread.AccountId) ?? false;
        if (thread.Channel == ChatBranch.Domain.Channels.Telegram)
            return _telegram?.WouldSendLive(thread, accountId ?? thread.AccountId) ?? false;
        var account = Account(accountId ?? thread.AccountId);
        return BaileysLive(thread, account) || CloudLive(thread, account) || SocialLive(thread, account);
    }

    /// <param name="subject">Email only: an explicit subject (else "Re: &lt;last subject&gt;" / the default).</param>
    /// <param name="media">Email / Telegram: a stored file to attach (messaging media root).</param>
    public Message Send(Thread thread, string text, string source, int? accountId = null,
        string? subject = null, OutboundMedia? media = null)
    {
        if (thread.Channel == ChatBranch.Domain.Channels.Email && _email != null)
            return _email.Send(thread, text, source, accountId ?? thread.AccountId, subject, media);
        if (thread.Channel == ChatBranch.Domain.Channels.Telegram && _telegram != null)
            return _telegram.Send(thread, text, source, accountId ?? thread.AccountId, media);

        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);

        // Baileys (personal number paired by QR): send through the Node gateway.
        // The account's ExternalId is the gateway's account id.
        if (BaileysLive(thread, account))
        {
            var r = _baileys.SendTextAsync(account!.ExternalId, thread.Key, text).GetAwaiter().GetResult();
            if (r.Ok)
                return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                    status: "sent", externalId: r.ExternalId, accountId: account.Id);
            return _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {r.Detail}] {text}", status: "failed", accountId: account.Id);
        }

        if (CloudLive(thread, account))
            return Record(thread, account!, source, text,
                _whatsapp.SendTextAsync(account!, thread.Key, text).GetAwaiter().GetResult());

        if (SocialLive(thread, account))
            return Record(thread, account!, source, text,
                _meta!.SendTextAsync(account!, thread.Key, text).GetAwaiter().GetResult());

        // sandbox (no configured account)
        return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
            status: "sandbox", accountId: acctId);
    }

    /// <summary>
    /// A WhatsApp template. A configured Cloud number sends a REAL
    /// <c>type: "template"</c> message (name, language, header/body/button
    /// parameters) — the only thing Meta accepts outside the 24h window — and the
    /// RENDERED body is what the inbox stores for the bubble. Everywhere else
    /// (Baileys, sandbox) the rendered body goes out / is stored as text.
    /// </summary>
    public Message SendTemplate(Thread thread, string name, string? language, TemplateArgs args, string source,
        int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        var catalog = new WhatsAppTemplateCatalog(new ScopingService(_db), _whatsapp);
        WaTemplateDef? def = null;
        try { def = catalog.FindAsync(account, name, language).GetAwaiter().GetResult(); }
        catch { /* registry unreachable: send by name, let Meta validate */ }
        var rendered = WhatsAppTemplateCatalog.Render(def, name, args);

        if (CloudLive(thread, account))
        {
            var template = WhatsAppTemplateCatalog.BuildMessage(def, name, language, args);
            var outcome = _whatsapp.SendTemplateAsync(account!, thread.Key, template).GetAwaiter().GetResult();
            return Record(thread, account!, source, rendered, outcome);
        }
        return Send(thread, rendered, source, acctId);
    }

    /// <summary>
    /// A media message whose file is already stored (<see cref="MediaStore"/>,
    /// <paramref name="relPath"/>). A configured Cloud number uploads it to
    /// /{phone_number_id}/media and sends it by id; anything else keeps the file
    /// locally and stores the bubble as sandbox (no media path on Baileys or the
    /// social channels in this build).
    /// </summary>
    public Message SendMedia(Thread thread, string relPath, string mediaType, string mime, string? caption,
        string? filename, string source, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        var text = string.IsNullOrWhiteSpace(caption) ? $"[{mediaType}]" : caption!.Trim();

        Message msg;
        var full = _media?.Resolve(thread.Channel, relPath);
        if (CloudLive(thread, account) && full != null)
        {
            var bytes = File.ReadAllBytes(full);
            var up = _whatsapp.UploadMediaAsync(account!, bytes, mime, filename ?? Path.GetFileName(full))
                .GetAwaiter().GetResult();
            var outcome = up.Ok
                ? _whatsapp.SendMediaAsync(account!, thread.Key, mediaType, up.ExternalId, caption, filename)
                    .GetAwaiter().GetResult()
                : up;
            msg = Record(thread, account!, source, text, outcome);
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: acctId);
        }
        msg.MediaType = mediaType;
        msg.MediaPath = relPath;
        _db.SaveChanges();
        return msg;
    }

    /// <summary>
    /// Messenger / Instagram attachment by public URL (the Send API fetches it).
    /// Sandbox stores the bubble text only.
    /// </summary>
    public Message SendMediaUrl(Thread thread, string mediaType, string url, string source, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        var text = $"[{mediaType}] {url}";
        if (SocialLive(thread, account))
            return Record(thread, account!, source, text,
                _meta!.SendAttachmentAsync(account!, thread.Key, mediaType, url).GetAwaiter().GetResult());
        return _threads.AppendMessage(thread, MessageDirection.Out, source, text, status: "sandbox", accountId: acctId);
    }

    /// <summary>
    /// Store a provider round-trip: success = the real id + "sent" (and the account
    /// is proven healthy); failure = a "failed" system message with Meta's reason,
    /// the account's health moved ONLY for an account-level code, and a 131047
    /// closing the conversation's window so the composer offers templates.
    /// </summary>
    private Message Record(Thread thread, ChannelAccount account, string source, string text, SendOutcome outcome)
    {
        if (outcome.Ok)
        {
            MetaErrors.MarkHealthy(_db, account, "send ok");
            return _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: outcome.ExternalId, accountId: account.Id);
        }
        MetaErrors.ApplyToAccount(_db, account, outcome.ErrorCode, outcome.ErrorSubcode, outcome.Detail);
        if (outcome.ErrorCode == MetaErrors.WindowClosed && thread.Channel == ChatBranch.Domain.Channels.WhatsApp)
            WhatsAppWindow.Close(_db, thread, exceptAccountId: null, save: false);
        var msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
            $"[send failed: {outcome.Detail}] {text}", status: "failed", accountId: account.Id);
        if (outcome.ErrorCode is int code)
        {
            msg.Error = $"{code}: {outcome.Detail}".Length > 300 ? $"{code}: {outcome.Detail}"[..300] : $"{code}: {outcome.Detail}";
            _db.SaveChanges();
        }
        return msg;
    }
}
