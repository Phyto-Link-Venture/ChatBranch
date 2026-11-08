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
///     and media (upload → send by id, <see cref="SendMedia"/>); the official BSPs
///     YCloud and Twilio ride the same seam (<see cref="WhatsAppClientRouter"/> picks
///     the provider's client: Twilio sends media by a signed public link and has no
///     quotes or reactions);
///   * WhatsApp Baileys — text and media through the Node gateway (templates
///     render as text);
///   * Messenger / Instagram — the Send API with the page token (text, URL
///     attachments, uploaded files), inside Meta's messaging window
///     (<see cref="SocialWindow"/>: 24h for anything, up to 7 days for a person
///     with the HUMAN_AGENT tag, nothing after that);
///   * Email (SMTP, <see cref="EmailChannel"/>) and Telegram (Bot API,
///     <see cref="TelegramChannel"/>) — they resolve their own mailbox / bot.
///
/// A provider failure is recorded honestly as a <c>failed</c> system message;
/// an ACCOUNT-level Meta error moves the account's health (per-recipient codes
/// never do), a success heals it, and 131047 closes the 24h window.
/// </summary>
/// <summary>
/// The provider's answer to one reaction: <see cref="Live"/> = it went to a real
/// provider (false = sandbox, stored only); <see cref="Conflict"/> = refused for a
/// reason the operator can't retry past (closed window, no provider id, channel
/// without reactions) — a 409 rather than a 502.
/// </summary>
public sealed record ReactionSend(bool Ok, bool Live, string Detail, bool Conflict = false);

/// <summary>
/// One provider action on a message already sent (delete for everyone, edit).
/// <see cref="Live"/> false = the conversation has no live number, so the
/// message never left this inbox and only the stored row changes.
/// <see cref="Conflict"/> = the caller asked for something this channel or this
/// moment cannot do (a 409), not a provider outage.
/// </summary>
public sealed record MessageActionSend(bool Ok, bool Live, string Detail, bool Conflict = false);

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
    /// <summary>LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo (<see cref="MoreChannels"/>).</summary>
    private readonly MoreChannelSender? _more;

    public OutboundSender(AppDbContext db, ThreadStore threads, IWhatsAppCloudClient whatsapp,
        ChatBranch.Data.Sidecars.IBaileysGateway baileys, IMetaMessagingClient? meta = null,
        MediaStore? media = null, EmailChannel? email = null, TelegramChannel? telegram = null,
        MoreChannelSender? more = null)
    {
        _more = more;
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

    /// <summary>LINE, Viber, SMS…: whether the channel's transport can carry an uploaded file.</summary>
    public bool ChannelCarriesFiles(string channel) => _more?.SupportsMedia(channel) ?? false;

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
        if (MoreChannels.Handles(thread.Channel))
            return _more?.WouldSendLive(thread, accountId ?? thread.AccountId) ?? false;
        var account = Account(accountId ?? thread.AccountId);
        return BaileysLive(thread, account) || CloudLive(thread, account) || SocialLive(thread, account);
    }

    /// <param name="subject">Email only: an explicit subject (else "Re: &lt;last subject&gt;" / the default).</param>
    /// <param name="media">Email / Telegram: a stored file to attach (messaging media root).</param>
    /// <param name="replyTo">The message this one quotes (same thread). WhatsApp Cloud sends
    /// <c>context.message_id</c>, Baileys a quoted message, Telegram <c>reply_parameters</c>;
    /// other channels send the text normally. A quote never fails a send.</param>
    public Message Send(Thread thread, string text, string source, int? accountId = null,
        string? subject = null, OutboundMedia? media = null, Message? replyTo = null)
    {
        if (replyTo != null && (replyTo.ThreadId != thread.Id || !MessageReactions.Supports(thread.Channel)))
            replyTo = null;
        if (thread.Channel == ChatBranch.Domain.Channels.Email && _email != null)
            return _email.Send(thread, text, source, accountId ?? thread.AccountId, subject, media);
        if (thread.Channel == ChatBranch.Domain.Channels.Telegram && _telegram != null)
            return LinkReply(_telegram.Send(thread, text, source, accountId ?? thread.AccountId, media,
                replyTo?.ExternalId), replyTo);
        if (MoreChannels.Handles(thread.Channel) && _more != null)
            return _more.Send(thread, text, source, accountId ?? thread.AccountId, media);

        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);

        // Baileys (personal number paired by QR): send through the Node gateway.
        // The account's ExternalId is the gateway's account id.
        if (BaileysLive(thread, account))
        {
            var quote = replyTo is { ExternalId.Length: > 0 }
                ? new ChatBranch.Data.Sidecars.BaileysQuote(replyTo.ExternalId,
                    replyTo.Direction == MessageDirection.Out, replyTo.Text)
                : null;
            var r = (quote == null
                    ? _baileys.SendTextAsync(account!.ExternalId, thread.Key, text)
                    : _baileys.SendTextAsync(account!.ExternalId, thread.Key, text, quote))
                .GetAwaiter().GetResult();
            if (r.Ok)
                return LinkReply(_threads.AppendMessage(thread, MessageDirection.Out, source, text,
                    status: "sent", externalId: r.ExternalId, accountId: account.Id), replyTo);
            return _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {r.Detail}] {text}", status: "failed", accountId: account.Id);
        }

        if (CloudLive(thread, account))
        {
            // an official number (Cloud API, YCloud, Twilio): Twilio has no quoted replies, so
            // its reply goes out — and is recorded — unquoted rather than failing
            var quote = replyTo is { ExternalId.Length: > 0 } && WhatsAppProviders.SupportsQuotes(account!) ? replyTo : null;
            return LinkReply(Record(thread, account!, source, text,
                (quote != null
                    ? _whatsapp.SendReplyTextAsync(account!, thread.Key, text, quote.ExternalId)
                    : _whatsapp.SendTextAsync(account!, thread.Key, text)).GetAwaiter().GetResult()), quote);
        }

        if (SocialLive(thread, account))
            return SocialSend(thread, account!, source, text, humanAgent: false,
                tag => _meta!.SendTextAsync(account!, thread.Key, text, tag).GetAwaiter().GetResult());

        // sandbox (no configured account)
        return LinkReply(_threads.AppendMessage(thread, MessageDirection.Out, source, text,
            status: "sandbox", accountId: acctId), replyTo);
    }

    /// <summary>Record which message a delivered (or sandbox) reply quoted — a failed send keeps none.</summary>
    private Message LinkReply(Message msg, Message? replyTo)
    {
        if (replyTo == null || msg.Status == "failed" || msg.ReplyToId != null) return msg;
        msg.ReplyToId = replyTo.Id;
        msg.ReplyToExternalId = replyTo.ExternalId;
        _db.SaveChanges();
        return msg;
    }

    /// <summary>
    /// Put OUR reaction (<paramref name="emoji"/>, "" = remove) on <paramref name="target"/>
    /// at the provider: WhatsApp Cloud <c>type: reaction</c> (inside the 24h window —
    /// Meta refuses it after), Baileys <c>react</c>, Telegram <c>setMessageReaction</c>.
    /// A conversation with no live account is sandbox (stored only). The caller
    /// stores the reaction when this answers Ok.
    /// </summary>
    public ReactionSend React(Thread thread, Message target, string emoji)
    {
        if (!MessageReactions.Supports(thread.Channel))
            return new ReactionSend(false, false, $"Reactions aren't supported on {thread.Channel}.", Conflict: true);
        if (thread.Channel == ChatBranch.Domain.Channels.Telegram)
        {
            if (_telegram == null) return new ReactionSend(true, false, "sandbox");
            var (live, ok, detail) = _telegram.React(thread, target.ExternalId, emoji);
            return new ReactionSend(ok, live, detail, Conflict: live && !ok && target.ExternalId.Length == 0);
        }
        var account = Account(thread.AccountId) ?? Account(target.AccountId);
        var baileys = BaileysLive(thread, account);
        var cloud = CloudLive(thread, account);
        if (!baileys && !cloud) return new ReactionSend(true, false, "sandbox");
        if (cloud && !WhatsAppProviders.SupportsReactions(account!))
            return new ReactionSend(false, true,
                $"Reactions aren't supported by {WhatsAppProviders.Label(account!.Provider)} — this provider's API can't send one.",
                Conflict: true);
        if (target.ExternalId.Length == 0 || target.ExternalId.StartsWith("db-", StringComparison.Ordinal))
            return new ReactionSend(false, true,
                "That message has no WhatsApp id (it never left this inbox), so it can't carry a reaction.",
                Conflict: true);
        if (baileys)
        {
            var r = _baileys.ReactAsync(account!.ExternalId, thread.Key,
                new ChatBranch.Data.Sidecars.BaileysQuote(target.ExternalId, target.Direction == MessageDirection.Out),
                emoji).GetAwaiter().GetResult();
            return new ReactionSend(r.Ok, true, r.Ok ? "sent" : r.Detail);
        }
        // Meta refuses a reaction outside the customer-service window — say so up front
        var lastIn = _db.Messages.Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);
        if (lastIn is not DateTime ts || DateTime.UtcNow >= ts.AddHours(WhatsAppWindow.Hours))
            return new ReactionSend(false, true,
                "WhatsApp 24h window closed — a reaction can only be sent within 24 hours of the customer's last message.",
                Conflict: true);
        var outcome = _whatsapp.SendReactionAsync(account!, thread.Key, target.ExternalId, emoji)
            .GetAwaiter().GetResult();
        if (outcome.Ok) MetaErrors.MarkHealthy(_db, account!, "reaction sent");
        else MetaErrors.ApplyToAccount(_db, account!, outcome.ErrorCode, outcome.ErrorSubcode, outcome.Detail);
        return new ReactionSend(outcome.Ok, true, outcome.Ok ? "sent" : outcome.Detail,
            Conflict: outcome.ErrorCode == MetaErrors.WindowClosed);
    }

    /// <summary>
    /// WhatsApp's "delete for everyone" on a message WE sent. QR (Baileys)
    /// numbers only: Meta's Cloud API — and every other channel here — exposes no
    /// delete at all, so this refuses rather than pretending, and the message
    /// stays on the customer's phone.
    /// </summary>
    public MessageActionSend Revoke(Thread thread, Message target)
    {
        if (target.Direction != MessageDirection.Out)
            return new MessageActionSend(false, false,
                "Only a message you sent can be deleted for everyone.", Conflict: true);
        if (target.Private || target.Source == MessageSource.System)
            return new MessageActionSend(false, false,
                "Notes and system messages only exist in this inbox — there is nothing to delete on WhatsApp.",
                Conflict: true);

        var account = Account(thread.AccountId) ?? Account(target.AccountId);
        if (!BaileysLive(thread, account))
        {
            // a Cloud / BSP number, another channel, or nothing live at all
            if (CloudLive(thread, account))
                return new MessageActionSend(false, true,
                    $"{WhatsAppProviders.Label(account!.Provider)} cannot delete a sent message — Meta's API has no "
                    + "endpoint for it. Only a QR-paired number can.", Conflict: true);
            if (account != null && account.Channel != ChatBranch.Domain.Channels.WhatsApp)
                return new MessageActionSend(false, true,
                    $"Deleting a sent message isn't supported on {account.Channel}.", Conflict: true);
            // sandbox: it never left, so removing it here is the whole truth
            return new MessageActionSend(true, false, "sandbox");
        }
        if (target.ExternalId.Length == 0 || target.ExternalId.StartsWith("db-", StringComparison.Ordinal))
            return new MessageActionSend(false, true,
                "That message has no WhatsApp id (it never left this inbox), so there is nothing to delete.",
                Conflict: true);

        var r = _baileys.RevokeMessageAsync(account!.ExternalId, thread.Key,
            new ChatBranch.Data.Sidecars.BaileysQuote(target.ExternalId, true)).GetAwaiter().GetResult();
        return new MessageActionSend(r.Ok, true, r.Ok ? "deleted" : r.Detail);
    }

    /// <summary>
    /// Edit the text of a message WE sent. QR (Baileys) numbers only, and only
    /// text — WhatsApp has no edit for media captions through this path, and no
    /// other channel here offers an edit at all.
    /// </summary>
    public MessageActionSend Edit(Thread thread, Message target, string text)
    {
        if (target.Direction != MessageDirection.Out)
            return new MessageActionSend(false, false, "Only a message you sent can be edited.", Conflict: true);
        if (target.Private || target.Source == MessageSource.System)
            return new MessageActionSend(false, false,
                "Notes and system messages aren't on WhatsApp, so there is nothing to edit.", Conflict: true);
        if (target.MediaPath.Length > 0)
            return new MessageActionSend(false, false,
                "WhatsApp can't edit a message that carries a file — delete it and send a new one.", Conflict: true);
        if (target.RecalledAt != null)
            return new MessageActionSend(false, false, "That message was already deleted.", Conflict: true);

        var account = Account(thread.AccountId) ?? Account(target.AccountId);
        if (!BaileysLive(thread, account))
        {
            if (CloudLive(thread, account))
                return new MessageActionSend(false, true,
                    $"{WhatsAppProviders.Label(account!.Provider)} cannot edit a sent message — Meta's API has no "
                    + "endpoint for it. Only a QR-paired number can.", Conflict: true);
            if (account != null && account.Channel != ChatBranch.Domain.Channels.WhatsApp)
                return new MessageActionSend(false, true,
                    $"Editing a sent message isn't supported on {account.Channel}.", Conflict: true);
            return new MessageActionSend(true, false, "sandbox");
        }
        if (target.ExternalId.Length == 0 || target.ExternalId.StartsWith("db-", StringComparison.Ordinal))
            return new MessageActionSend(false, true,
                "That message has no WhatsApp id (it never left this inbox), so there is nothing to edit.",
                Conflict: true);

        var r = _baileys.EditMessageAsync(account!.ExternalId, thread.Key,
            new ChatBranch.Data.Sidecars.BaileysQuote(target.ExternalId, true), text).GetAwaiter().GetResult();
        return new MessageActionSend(r.Ok, true, r.Ok ? "edited" : r.Detail);
    }

    /// <summary>
    /// A pinned location. QR (Baileys) numbers; an official number gets an
    /// honest refusal — Meta's Cloud API does carry locations, it is simply not
    /// wired here, and saying so beats implying the platform cannot.
    /// </summary>
    public (Message? Sent, string Error, bool Conflict) SendLocation(Thread thread, double latitude,
        double longitude, string name, string address, string source, int? accountId = null)
    {
        var label = string.Join(" ", new[] { $"{latitude},{longitude}", name }.Where(x => x.Length > 0));
        return Card(thread, $"[location] {label}".TrimEnd(), source, accountId, "a location",
            cloudCould: true,
            send: a => _baileys.SendLocationAsync(a.ExternalId, thread.Key, latitude, longitude, name, address));
    }

    /// <summary>A contact card (vCard). QR numbers, as above.</summary>
    public (Message? Sent, string Error, bool Conflict) SendContact(Thread thread, string name,
        IReadOnlyList<string> phones, string organization, string source, int? accountId = null)
    {
        var first = phones.Count > 0 ? phones[0] : "";
        return Card(thread, $"[contact] {name}{(first.Length > 0 ? $" · {first}" : "")}", source, accountId,
            "a contact card", cloudCould: true,
            send: a => _baileys.SendContactAsync(a.ExternalId, thread.Key, name, phones, organization));
    }

    /// <summary>
    /// A poll. QR numbers ONLY in the real sense: WhatsApp's Cloud API has no
    /// polls at all, so an official number is told that rather than "not wired".
    /// </summary>
    public (Message? Sent, string Error, bool Conflict) SendPoll(Thread thread, string question,
        IReadOnlyList<string> options, int selectableCount, string source, int? accountId = null)
    {
        var text = $"[poll] {question} — {string.Join(" / ", options)}";
        return Card(thread, text, source, accountId, "a poll", cloudCould: false,
            send: a => _baileys.SendPollAsync(a.ExternalId, thread.Key, question, options, selectableCount));
    }

    /// <summary>
    /// A true WhatsApp forward of another message into this conversation —
    /// WhatsApp's own "Forwarded" marking, which needs the whole original. The
    /// gateway only holds the recent ones, and says so (a conflict) rather than
    /// re-sending the content unmarked.
    /// </summary>
    public (Message? Sent, string Error, bool Conflict) Forward(Thread thread, string sourceExternalId, string text,
        string source, int? accountId = null)
    {
        if (sourceExternalId.Length == 0 || sourceExternalId.StartsWith("db-", StringComparison.Ordinal))
            return (null, "That message has no WhatsApp id (it never left this inbox), so it cannot be forwarded.",
                true);
        return Card(thread, text, source, accountId, "a forwarded message", cloudCould: false,
            send: a => _baileys.ForwardMessageAsync(a.ExternalId, thread.Key, sourceExternalId));
    }

    /// <summary>
    /// A sticker — a 512×512 WebP shaped by <see cref="StickerImage"/> and sent as
    /// WhatsApp's own <c>stickerMessage</c>, which is NOT an image message: it shows
    /// with no bubble and carries no caption.
    ///
    /// QR (Baileys) numbers only, and honestly so: Meta's Cloud API can send a sticker
    /// (by the media id of an uploaded WebP) but that is not wired here, while the
    /// BSPs publish no sticker send at all — each is told which of the two it is. A
    /// conversation with nothing live keeps a sandbox row with the sticker attached,
    /// so the chat still shows what was meant.
    ///
    /// The row is stored as "[sticker]" with the file attached as an IMAGE, which is
    /// exactly how an INBOUND sticker is recorded — so the inbox renders ours and
    /// theirs the same way, with no second code path in the bubble.
    /// </summary>
    public (Message? Sent, string Error, bool Conflict) SendSticker(Thread thread, byte[] webp, string relPath,
        string source, int? accountId = null)
    {
        var refused = StickerImage.Refusal(webp);
        if (refused != null) return (null, refused, false);

        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        const string text = "[sticker]";
        var name = Path.GetFileName(relPath);

        Message msg;
        if (BaileysLive(thread, account))
        {
            var r = _baileys.SendMediaAsync(account!.ExternalId, thread.Key,
                    ChatBranch.Data.Sidecars.BaileysMedia.Sticker, webp, StickerImage.Mime, name, null)
                .GetAwaiter().GetResult();
            if (!r.Ok) return (null, r.Detail, r.Conflict);
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: r.ExternalId, accountId: account.Id);
        }
        else if (CloudLive(thread, account))
        {
            // two different "no"s: Meta's API carries stickers and is simply not wired
            // here, while the BSPs publish no sticker send at all
            return (null, account!.Provider == Providers.Cloud
                ? "Sending a sticker from the WhatsApp Cloud API isn't wired up in ChatBranch yet — "
                  + "a QR-paired number can."
                : $"{WhatsAppProviders.Label(account.Provider)} cannot send stickers — its API has no "
                  + "sticker message.", true);
        }
        else if (account != null && account.Channel != ChatBranch.Domain.Channels.WhatsApp)
        {
            return (null, $"Sending a sticker isn't supported on {account.Channel}.", true);
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: acctId);
        }
        // "image", not "sticker": the same shape an inbound sticker is stored with
        AttachMedia(msg, "image", relPath, StickerImage.Mime, name,
            _media?.Resolve(thread.Channel, relPath));
        return (msg, "", false);
    }

    /// <summary>
    /// The shared path for the message kinds only a QR number carries: route,
    /// refuse honestly per provider, or store a sandbox row so the inbox still
    /// shows what was meant when nothing is attached.
    /// </summary>
    private (Message? Sent, string Error, bool Conflict) Card(Thread thread, string text, string source,
        int? accountId, string what, bool cloudCould, Func<ChannelAccount, Task<ChatBranch.Data.Sidecars.BaileysResult>> send)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        if (!BaileysLive(thread, account))
        {
            if (CloudLive(thread, account))
                return (null, cloudCould
                    ? $"Sending {what} from {WhatsAppProviders.Label(account!.Provider)} isn't wired up in "
                      + "ChatBranch yet — a QR-paired number can."
                    : $"{WhatsAppProviders.Label(account!.Provider)} cannot send {what} — WhatsApp's official API "
                      + "has no such message.", true);
            if (account != null && account.Channel != ChatBranch.Domain.Channels.WhatsApp)
                return (null, $"Sending {what} isn't supported on {account.Channel}.", true);
            // nothing live: keep the row so the chat shows what was meant
            return (_threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: acctId), "", false);
        }
        var r = send(account!).GetAwaiter().GetResult();
        if (!r.Ok) return (null, r.Detail, r.Conflict);
        return (_threads.AppendMessage(thread, MessageDirection.Out, source, text,
            status: "sent", externalId: r.ExternalId, accountId: account!.Id), "", false);
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
    /// <paramref name="relPath"/> under the channel's media root). Live paths:
    ///   * WhatsApp Cloud — upload to /{phone_number_id}/media, send by id;
    ///   * WhatsApp Baileys — the gateway's <c>send-media</c> (base64 body, ≤16 MB);
    ///   * Messenger / Instagram — the Send API's multipart upload, inside the
    ///     messaging window (<paramref name="humanAgent"/> = a person sent it, so
    ///     24h–7d goes out tagged HUMAN_AGENT). Messenger attachments carry no
    ///     caption, so a caption follows as its own text message.
    /// Anything else keeps the file locally and stores the bubble as sandbox.
    /// </summary>
    public Message SendMedia(Thread thread, string relPath, string mediaType, string mime, string? caption,
        string? filename, string source, int? accountId = null, bool humanAgent = false)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        var text = string.IsNullOrWhiteSpace(caption) ? $"[{mediaType}]" : caption!.Trim();
        if (MoreChannels.Handles(thread.Channel) && _more != null
            && _media?.Resolve(thread.Channel, relPath) is { } stored)
            return _more.Send(thread, text, source, acctId, new OutboundMedia(stored, relPath, mediaType,
                string.IsNullOrWhiteSpace(filename) ? Path.GetFileName(stored) : filename!), humanAgent);

        Message msg;
        var full = _media?.Resolve(thread.Channel, relPath);
        var name = string.IsNullOrWhiteSpace(filename) ? Path.GetFileName(full ?? relPath) : filename!;
        if (CloudLive(thread, account) && full != null)
        {
            msg = Record(thread, account!, source, text, OfficialMedia(thread, account!, full, relPath, mediaType, mime,
                caption, filename));
        }
        else if (BaileysLive(thread, account) && full != null)
        {
            // same recording as a Baileys text send: the WhatsApp id, or a failed row with the reason
            var bytes = File.ReadAllBytes(full);
            var r = _baileys.SendMediaAsync(account!.ExternalId, thread.Key, mediaType, bytes, mime, name, caption)
                .GetAwaiter().GetResult();
            msg = r.Ok
                ? _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                    status: "sent", externalId: r.ExternalId, accountId: account.Id)
                : _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                    $"[send failed: {r.Detail}] {text}", status: "failed", accountId: account.Id);
        }
        else if (SocialLive(thread, account) && full != null)
        {
            var bytes = File.ReadAllBytes(full);
            var withCaption = !string.IsNullOrWhiteSpace(caption);
            msg = SocialSend(thread, account!, source, withCaption ? $"[{mediaType}]" : text, humanAgent,
                tag => _meta!.SendFileAsync(account!, thread.Key, mediaType, bytes, mime, name, tag)
                    .GetAwaiter().GetResult());
            if (withCaption && msg.Status == "sent")
            {
                AttachMedia(msg, mediaType, relPath, mime, name, full);
                var said = caption!.Trim();
                return SocialSend(thread, account!, source, said, humanAgent,
                    tag => _meta!.SendTextAsync(account!, thread.Key, said, tag).GetAwaiter().GetResult());
            }
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: acctId);
        }
        AttachMedia(msg, mediaType, relPath, mime, name, full);
        return msg;
    }

    /// <summary>
    /// The AI's voice reply: ONE voice note (a stored Ogg/Opus or MP3 file, <paramref name="relPath"/>)
    /// whose message row keeps the reply's words (<paramref name="text"/>) with the audio attached.
    /// WhatsApp Baileys = the gateway's <c>send-media</c> with <c>ptt</c>; Cloud API / YCloud = upload
    /// then <c>type: audio</c> (Ogg/Opus shows as a voice note); Telegram = <c>sendVoice</c>;
    /// Messenger / Instagram = an audio attachment inside the messaging window. A conversation
    /// with no live account stores the voice note as sandbox. Returns (null, why) when it could
    /// not go out as a voice note — no failed row is left behind, the caller sends the TEXT.
    /// </summary>
    public (Message? Sent, string Error) SendVoiceNote(Thread thread, string relPath, string mime, string text,
        string source, int? accountId = null)
    {
        var acctId = accountId ?? thread.AccountId;
        var full = _media?.Resolve(thread.Channel, relPath);
        if (full == null) return (null, "the voice note file is missing");
        var name = thread.Channel is ChatBranch.Domain.Channels.WhatsApp or ChatBranch.Domain.Channels.Telegram
            ? "voice.ogg" : Path.GetFileName(full);
        try
        {
            if (thread.Channel == ChatBranch.Domain.Channels.Telegram)
            {
                if (_telegram == null) return (Sandbox(), "");
                var tg = _telegram.Send(thread, text, source, acctId, new OutboundMedia(full, relPath, "audio", name),
                    voiceNote: true);
                return Kept(tg);
            }
            var account = Account(acctId);
            if (thread.Channel == ChatBranch.Domain.Channels.WhatsApp)
            {
                if (BaileysLive(thread, account))
                {
                    var r = _baileys.SendVoiceNoteAsync(account!.ExternalId, thread.Key, File.ReadAllBytes(full))
                        .GetAwaiter().GetResult();
                    if (!r.Ok) return (null, r.Detail);
                    var m = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                        status: "sent", externalId: r.ExternalId, accountId: account.Id);
                    AttachMedia(m, "audio", relPath, mime, name, full);
                    return (m, "");
                }
                if (CloudLive(thread, account))
                {
                    if (WhatsAppProviders.Of(account!) is not (Providers.Cloud or Providers.YCloud))
                        return (null, $"{WhatsAppProviders.Label(account!.Provider)} can't send voice notes");
                    var up = _whatsapp.UploadMediaAsync(account!, File.ReadAllBytes(full), mime, name)
                        .GetAwaiter().GetResult();
                    var outcome = up.Ok
                        ? _whatsapp.SendMediaAsync(account!, thread.Key, "audio", up.ExternalId, null, null)
                            .GetAwaiter().GetResult()
                        : up;
                    if (!outcome.Ok) return (null, outcome.Detail);
                    var m = Record(thread, account!, source, text, outcome);
                    AttachMedia(m, "audio", relPath, mime, name, full);
                    return (m, "");
                }
                return (Sandbox(), "");
            }
            if (IsSocial(thread))
            {
                if (!SocialLive(thread, account)) return (Sandbox(), "");
                var bytes = File.ReadAllBytes(full);
                var m = SocialSend(thread, account!, source, text, humanAgent: false,
                    tag => _meta!.SendFileAsync(account!, thread.Key, "audio", bytes, mime, name, tag)
                        .GetAwaiter().GetResult());
                return Kept(m);
            }
            return (null, $"voice notes aren't sent on {thread.Channel}");
        }
        catch (Exception ex)
        {
            return (null, $"voice note send failed ({ex.GetType().Name})");
        }

        Message Sandbox()
        {
            var m = _threads.AppendMessage(thread, MessageDirection.Out, source, text, status: "sandbox", accountId: acctId);
            AttachMedia(m, "audio", relPath, mime, name, full);
            return m;
        }

        // a refused send left a failed row: drop it (the caller sends the words as text instead)
        (Message?, string) Kept(Message m)
        {
            if (m.Status != "failed")
            {
                if (m.MediaPath.Length == 0) AttachMedia(m, "audio", relPath, mime, name, full);
                return (m, "");
            }
            var why = m.Error.Length > 0 ? m.Error : m.Text;
            _db.Messages.Remove(m);
            _db.SaveChanges();
            return (null, why);
        }
    }

    /// <summary>
    /// A stored file on an official number. Cloud API: upload to /{phone_number_id}/media,
    /// send by id. YCloud: its media upload, send by id — falling back to a signed public
    /// link when the upload is refused. Twilio: no upload exists, Twilio FETCHES the file,
    /// so it gets a short-lived signed public link (<see cref="MediaLinks"/>) — and an honest
    /// failure when this API has no publicly reachable address to hand out.
    /// </summary>
    private SendOutcome OfficialMedia(Thread thread, ChannelAccount account, string full, string relPath,
        string mediaType, string mime, string? caption, string? filename)
    {
        var provider = WhatsAppProviders.Of(account);
        string? Link() => new MediaLinks(new ScopingService(_db)).PublicUrl(thread.Channel, relPath);
        if (provider == Providers.Twilio)
        {
            var link = Link();
            return link == null
                ? new SendOutcome(false, "", "Twilio fetches media from a public URL — set public_base_url "
                                              + "(Settings → App & provider keys) to this API's public https address")
                : _whatsapp.SendMediaAsync(account, thread.Key, mediaType, link, caption, filename).GetAwaiter().GetResult();
        }
        var bytes = File.ReadAllBytes(full);
        var up = _whatsapp.UploadMediaAsync(account, bytes, mime, filename ?? Path.GetFileName(full))
            .GetAwaiter().GetResult();
        if (up.Ok)
            return _whatsapp.SendMediaAsync(account, thread.Key, mediaType, up.ExternalId, caption, filename)
                .GetAwaiter().GetResult();
        if (provider == Providers.YCloud && up.ErrorCode is not (190 or 10) && Link() is { } fallback)
            return _whatsapp.SendMediaAsync(account, thread.Key, mediaType, fallback, caption, filename)
                .GetAwaiter().GetResult();
        return up;
    }

    private void AttachMedia(Message msg, string mediaType, string relPath, string? mime, string? name,
        string? full)
    {
        MediaStore.Stamp(msg, mediaType, relPath, mime, name, MediaStore.SizeOf(full));
        _db.SaveChanges();
    }

    /// <summary>
    /// Messenger / Instagram attachment by public URL (the Send API fetches it),
    /// inside the messaging window like every social send. Sandbox stores the
    /// bubble text only.
    /// </summary>
    public Message SendMediaUrl(Thread thread, string mediaType, string url, string source, int? accountId = null,
        bool humanAgent = false)
    {
        var acctId = accountId ?? thread.AccountId;
        var account = Account(acctId);
        var text = $"[{mediaType}] {url}";
        if (SocialLive(thread, account))
            return SocialSend(thread, account!, source, text, humanAgent,
                tag => _meta!.SendAttachmentAsync(account!, thread.Key, mediaType, url, tag).GetAwaiter().GetResult());
        return _threads.AppendMessage(thread, MessageDirection.Out, source, text, status: "sandbox", accountId: acctId);
    }

    // ---- Messenger / Instagram: the messaging window --------------------------------

    /// <summary>
    /// A text reply on Messenger / Instagram where the caller knows who wrote it:
    /// <paramref name="humanAgent"/> = typed by a signed-in person, so between 24h
    /// and 7 days it goes out tagged HUMAN_AGENT. Any other channel (or a sandbox
    /// thread) goes through <see cref="Send"/> unchanged.
    /// </summary>
    public Message SendSocialText(Thread thread, string text, string source, int? accountId, bool humanAgent)
    {
        var account = Account(accountId ?? thread.AccountId);
        if (!SocialLive(thread, account)) return Send(thread, text, source, accountId);
        return SocialSend(thread, account!, source, text, humanAgent,
            tag => _meta!.SendTextAsync(account!, thread.Key, text, tag).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Why a send on this thread may not go out right now because of Meta's
    /// Messenger / Instagram window — null when it may, or when the thread is not
    /// a LIVE social conversation (sandbox never dead-ends). The send endpoints
    /// (409), the AI gate and the queue (failed job) ask this BEFORE sending; the
    /// send path enforces it again.
    /// </summary>
    public string? SocialWindowBlock(Thread thread, int? accountId, bool humanAgent, DateTime? nowUtc = null)
    {
        // WeChat (48h), Zalo (7 days), TikTok / Douyin (48h): the ChannelWindow registry
        if (ChannelWindow.Applies(thread.Channel))
            return _more?.WindowBlock(thread, accountId ?? thread.AccountId, humanAgent, nowUtc);
        if (!SocialWindow.Applies(thread.Channel)) return null;
        var account = Account(accountId ?? thread.AccountId);
        if (!SocialLive(thread, account)) return null;
        var last = SocialWindow.LastInbound(_db, thread);
        var state = SocialWindow.StateAt(last, nowUtc ?? DateTime.UtcNow);
        return SocialWindow.Refusal(thread.Channel, state, humanAgent, everWrote: last != null);
    }

    /// <summary>
    /// One live social send: refused (a failed row; Meta is never called) when the
    /// window forbids it, else sent with the right messaging_type / tag and the
    /// round-trip recorded.
    /// </summary>
    private Message SocialSend(Thread thread, ChannelAccount account, string source, string text, bool humanAgent,
        Func<string?, SendOutcome> send)
    {
        var last = SocialWindow.LastInbound(_db, thread);
        var state = SocialWindow.StateAt(last, DateTime.UtcNow);
        var refusal = SocialWindow.Refusal(thread.Channel, state, humanAgent, everWrote: last != null);
        if (refusal != null) return Failed(thread, account, text, refusal, $"window_closed: {refusal}");

        var tag = SocialWindow.Tag(state, humanAgent);
        var outcome = send(tag);
        // A TAGGED reply refused on permissions (10 / 2xx) means the page works but
        // its Meta app lacks the Human Agent feature — not an account fault, so
        // health is left alone (a really revoked token fails the next plain send
        // and the prober anyway).
        if (tag != null && !outcome.Ok && outcome.ErrorCode is int code && (code == 10 || code is >= 200 and <= 299))
            return Failed(thread, account, text,
                $"{outcome.Detail} — {SocialWindow.HumanAgentTag} replies need Meta's Human Agent permission for this app",
                $"{code}: {outcome.Detail}");
        return Record(thread, account, source, text, outcome);
    }

    /// <summary>A refused social send: a failed system row with the reason (health untouched).</summary>
    private Message Failed(Thread thread, ChannelAccount account, string text, string reason, string error)
    {
        var msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
            $"[send failed: {reason}] {text}", status: "failed", accountId: account.Id);
        msg.Error = error.Length > 300 ? error[..300] : error;
        _db.SaveChanges();
        return msg;
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
