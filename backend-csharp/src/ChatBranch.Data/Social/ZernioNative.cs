using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Social;

/// <summary>
/// Zernio as a CONNECTION METHOD on ChatBranch's native channels.
///
/// A Zernio account whose platform ChatBranch already has a channel for becomes a
/// <see cref="ChannelAccount"/> ON THAT CHANNEL with <c>Provider = "zernio"</c>:
/// whatsapp → <c>whatsapp</c>, facebook (Messenger) → <c>facebook</c>, instagram →
/// <c>instagram</c>, telegram → <c>telegram</c>, sms → <c>sms</c>. Its
/// <c>ExternalId</c> is Zernio's account id (also in <c>Config.zernio_account_id</c>),
/// so the inbox, routing, windows, templates, flows, the AI and automations treat it
/// like any other connection of that channel; threads keep the NATIVE key (WhatsApp /
/// SMS: the phone's digits, Messenger / Instagram: the PSID / IGSID, Telegram: the chat
/// id) and a reply addresses Zernio's conversation id from <see cref="ZernioConversation"/>.
///
/// One Zernio account → up to TWO rows sharing the Zernio id: the <c>social</c> row
/// (comments, reviews, posts — facebook, instagram, telegram) and the native row (DMs).
/// WhatsApp and SMS have no social features, so they only get the native row.
/// </summary>
public static class ZernioNative
{
    public const string AccountIdKey = "zernio_account_id";
    /// <summary>The social row's ChannelAccount id when the account has one (facebook, instagram, telegram).</summary>
    public const string SocialAccountKey = "zernio_social_account_id";
    /// <summary>What the customer sees: the WhatsApp / SMS number, the @username, the Page name.</summary>
    public const string AddressKey = "zernio_address";
    /// <summary>The user-facing name of the method (normal users never see the word "Zernio").</summary>
    public const string MethodLabel = "Managed connection";
    /// <summary>Set when someone renamed the inbox here: a sync then keeps that name.</summary>
    public const string CustomNameKey = "zernio_custom_name";

    /// <summary>Rename a managed-connection row (social or native) so later syncs keep the name.</summary>
    public static bool Rename(ChannelAccount a, string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.Contains("••")) return false;
        a.Name = n.Length > 120 ? n[..120] : n;
        a.Config = new Dictionary<string, object?>(a.Config ?? new()) { [CustomNameKey] = "1" };
        return true;
    }
    /// <summary>The machine key of the method on every listing (<c>method</c>), for the dashboard to translate.</summary>
    public const string Method = "managed";

    /// <summary>Zernio platform → native ChatBranch channel (only these become native inboxes).</summary>
    public static string? ChannelFor(string? platform) => (platform ?? "").ToLowerInvariant() switch
    {
        "whatsapp" => ChannelIds.WhatsApp,
        "facebook" => ChannelIds.Facebook,
        "instagram" => ChannelIds.Instagram,
        "telegram" => ChannelIds.Telegram,
        "sms" => ChannelIds.Sms,
        _ => null,
    };

    public static string PlatformFor(string channel) => channel switch
    {
        ChannelIds.Facebook => "facebook",
        _ => channel,
    };

    public static readonly string[] Platforms = { "whatsapp", "facebook", "instagram", "telegram", "sms" };

    /// <summary>Platforms whose Zernio account ALSO keeps a social row (comments / posts).</summary>
    public static bool HasSocialRow(string platform) => platform is not ("whatsapp" or "sms");

    /// <summary>A native-channel inbox carried by Zernio (never the social row).</summary>
    public static bool Is(ChannelAccount? a) =>
        a != null && a.Provider == Providers.Zernio && a.Channel != ChannelIds.Social;

    /// <summary>Configured = it names a Zernio account Zernio has not removed (the key itself is checked by the client).</summary>
    public static bool Configured(ChannelAccount a) =>
        Is(a) && a.ExternalId.Length > 0 && SocialChannels.Cfg(a, SocialChannels.StatusKey) != "removed";

    public static string Address(ChannelAccount a) => SocialChannels.Cfg(a, AddressKey);

    /// <summary>The method label a Connected card shows ("via Zernio").</summary>
    public static string Label(string channel) => channel switch
    {
        ChannelIds.WhatsApp => "WhatsApp (managed connection)",
        ChannelIds.Facebook => "Messenger (managed connection)",
        ChannelIds.Instagram => "Instagram (managed connection)",
        ChannelIds.Telegram => "Telegram (managed connection)",
        ChannelIds.Sms => "SMS (managed connection)",
        _ => MethodLabel,
    };

    /// <summary>The native-channel payload fields every account listing adds for a Zernio-carried inbox.</summary>
    public static Dictionary<string, object?> Payload(ChannelAccount a) => new()
    {
        ["provider"] = Providers.Zernio,
        ["method"] = Method,
        ["method_label"] = MethodLabel,
        ["provider_label"] = Label(a.Channel),
        ["zernio_account_id"] = a.ExternalId,
        ["zernio_platform"] = SocialChannels.Platform(a),
        ["zernio_status"] = SocialChannels.Cfg(a, SocialChannels.StatusKey),
        ["social_account_id"] = int.TryParse(SocialChannels.Cfg(a, SocialAccountKey), out var s) ? s : null,
        ["address"] = Address(a),
        ["username"] = SocialChannels.Cfg(a, SocialChannels.UsernameKey),
        ["display_name"] = SocialChannels.Cfg(a, SocialChannels.DisplayNameKey),
        ["avatar"] = SocialChannels.Cfg(a, SocialChannels.AvatarKey),
        ["credentials_held_by"] = "zernio",
        ["health_status"] = a.HealthStatus,
        ["health_detail"] = a.HealthDetail,
    };

    /// <summary>Why a Zernio-carried inbox cannot be credential-edited here.</summary>
    public const string NotEditable =
        "This inbox is a managed connection: its credentials are held by the connection service, not here. Reconnect it " +
        "from Channels → Add inbox (Managed connection); its team and AI default are still edited here.";

    // ---- thread keys ------------------------------------------------------------------

    /// <summary>
    /// The NATIVE thread key of a <c>message.received</c> (or its conversation): WhatsApp /
    /// SMS the phone's digits (a WhatsApp group keeps its <c>@g.us</c> jid), Telegram the
    /// chat id, Messenger / Instagram the PSID / IGSID. "" when the payload names nobody.
    /// </summary>
    public static string Key(string channel, JsonElement message, JsonElement conversation)
    {
        var sender = ZernioJson.At(message, "sender");
        var platformConv = ZernioJson.Str(conversation, "platformConversationId");
        var participant = ZernioJson.Str(conversation, "participantId");
        switch (channel)
        {
            case ChannelIds.WhatsApp:
            {
                if (platformConv.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                    return GroupChats.WhatsAppKey(platformConv);
                foreach (var raw in new[]
                         {
                             ZernioJson.Str(sender, "phoneNumber"), ZernioJson.Str(sender, "id"), participant,
                         })
                {
                    var d = WhatsAppProviders.Digits(raw);
                    // a BSUID is not a phone: only an all-digits id is
                    if (d.Length >= 6 && d.Length == raw.TrimStart('+').Length) return d;
                }
                // a user with a WhatsApp username and no phone: the BSUID keys the chat
                var bsuid = FirstNonEmpty(ZernioJson.Str(sender, "businessScopedUserId"), ZernioJson.Str(sender, "id"), participant);
                return bsuid.Length > 0 ? SocialChannels.KeyPart(bsuid) : "";
            }
            case ChannelIds.Sms:
            {
                var d = WhatsAppProviders.Digits(FirstNonEmpty(ZernioJson.Str(sender, "phoneNumber"), ZernioJson.Str(sender, "id"), participant));
                return d;
            }
            case ChannelIds.Telegram:
            {
                // the chat: a private chat's id is the user's, a group's is negative
                if (long.TryParse(platformConv, out _)) return platformConv;
                return FirstNonEmpty(ZernioJson.Str(sender, "id"), participant);
            }
            default:
                // Messenger / Instagram: platformConversationId IS the PSID / IGSID
                return FirstNonEmpty(platformConv, ZernioJson.Str(sender, "id"), participant);
        }
    }

    internal static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    // ---- the conversation map ---------------------------------------------------------

    public static ZernioConversation? Conversation(AppDbContext db, int accountId, string key) =>
        db.ZernioConversations.AsNoTracking().FirstOrDefault(c => c.AccountId == accountId && c.Key == key);

    /// <summary>Remember (or refresh) the Zernio conversation behind (inbox, key).</summary>
    public static void Remember(AppDbContext db, ChannelAccount account, string key, string conversationId,
        string participantId = "")
    {
        if (key.Length == 0 || conversationId.Length == 0) return;
        var row = db.ZernioConversations.FirstOrDefault(c => c.AccountId == account.Id && c.Key == key);
        if (row == null)
        {
            row = new ZernioConversation
            {
                AccountId = account.Id, ZernioAccountId = account.ExternalId, Channel = account.Channel, Key = key,
                CreatedAt = DateTime.UtcNow,
            };
            db.ZernioConversations.Add(row);
        }
        row.ConversationId = conversationId;
        if (participantId.Length > 0) row.ParticipantId = participantId;
        row.UpdatedAt = DateTime.UtcNow;
        try { db.SaveChanges(); }
        catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; /* a parallel insert won */ }
    }
}

/// <summary>What one Zernio send did, in both vocabularies (transport + Meta's SendOutcome).</summary>
public sealed record ZernioSend(bool Ok, string ExternalId, string Detail, int Status = 0, int? MetaCode = null,
    TransportFailure Failure = TransportFailure.None, string ConversationId = "")
{
    public static ZernioSend From(ZernioReply r, string externalId = "")
    {
        if (r.Ok) return new ZernioSend(true, externalId, "", r.Status);
        int? meta = ZernioJson.Int(r.Json, "platformError", "code") is var c and > 0 ? c : null;
        var t = SocialTransport.Fail(r);
        return new ZernioSend(false, "", t.Detail, r.Status, meta, t.Failure);
    }

    public static ZernioSend Refused(TransportFailure failure, string detail) => new(false, "", detail, 0, null, failure);

    public TransportResult ToTransport() =>
        Ok ? TransportResult.Success(ExternalId) : TransportResult.Fail(Failure, Detail, Status, MetaCode?.ToString() ?? "");

    /// <summary>Meta's codes where Zernio relays one; else what the HTTP status means for the account.</summary>
    public SendOutcome ToOutcome() => Ok
        ? new SendOutcome(true, ExternalId, "")
        : new SendOutcome(false, "", Detail, MetaCode ?? Failure switch
        {
            TransportFailure.Auth => 190,
            TransportFailure.RateLimit => 130429,
            TransportFailure.Billing => 131042,
            _ => null,
        });
}

/// <summary>
/// Sends on a native channel through a Zernio account: the conversation's messages
/// endpoint (text, an attachment by public URL, a quote, a Messenger tag, a WhatsApp
/// template), or — with no conversation yet — SMS's own send / WhatsApp's create-
/// conversation (a template opens it). Singleton; the conversation map is read through a
/// fresh scope. Also the <see cref="IChannelTransport"/> the Telegram and SMS send seams
/// use for a Zernio account (it is NOT registered as a channel transport itself).
/// </summary>
public sealed class ZernioNativeTransport : IChannelTransport
{
    private readonly IZernioClient _zernio;
    private readonly IServiceScopeFactory _scopes;

    public ZernioNativeTransport(IZernioClient zernio, IServiceScopeFactory scopes)
    {
        _zernio = zernio;
        _scopes = scopes;
    }

    public string Channel => "zernio";
    public int MaxText => 4096;
    public bool SupportsMedia => true;
    public bool IsConfigured => _zernio.IsConfigured;

    /// <summary>The account sends for real: a Zernio-carried inbox and a configured key.</summary>
    public bool Live(ChannelAccount? a) => a != null && ZernioNative.Configured(a) && _zernio.IsConfigured;

    private ZernioConversation? Find(int accountId, string key)
    {
        using var scope = _scopes.CreateScope();
        return ZernioNative.Conversation(scope.ServiceProvider.GetRequiredService<AppDbContext>(), accountId, key);
    }

    private void Remember(ChannelAccount a, string key, string conversationId, string participant = "")
    {
        if (conversationId.Length == 0) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.ChannelAccounts.AsNoTracking().FirstOrDefault(x => x.Id == a.Id) ?? a;
        ZernioNative.Remember(db, row, key, conversationId, participant);
    }

    /// <summary>The Zernio conversation id behind a thread ("" = none known yet).</summary>
    public string ConversationOf(ChannelAccount a, string key) => Find(a.Id, key)?.ConversationId ?? "";

    /// <summary>
    /// One message. <paramref name="tag"/> = Messenger / Instagram's HUMAN_AGENT tag;
    /// <paramref name="replyTo"/> = the platform message id it quotes;
    /// <paramref name="attachmentUrl"/> = a public file (type image | video | audio | file).
    /// </summary>
    public async Task<ZernioSend> SendAsync(ChannelAccount a, string key, string text, string? tag = null,
        string? replyTo = null, string? attachmentUrl = null, string attachmentType = "file", string? attachmentName = null,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return ZernioSend.Refused(TransportFailure.Transient, _zernio.NotConfiguredReason);
        if (!ZernioNative.Configured(a))
            return ZernioSend.Refused(TransportFailure.Auth, "this managed connection was removed — connect it again");
        var conv = Find(a.Id, key);
        if (conv == null)
        {
            if (a.Channel == ChannelIds.Sms)
            {
                var from = ZernioNative.Address(a);
                if (from.Length == 0)
                    return ZernioSend.Refused(TransportFailure.Auth, "this SMS inbox has no sending number yet");
                var r = await _zernio.SendSmsAsync(WhatsAppProviders.E164(from), WhatsAppProviders.E164(key), text,
                    attachmentUrl is { Length: > 0 } u ? new[] { u } : null, ct);
                if (r.Ok) Remember(a, key, r.Str("conversationId"), key);
                return ZernioSend.From(r, r.Str("id")) with { ConversationId = r.Str("conversationId") };
            }
            return ZernioSend.Refused(TransportFailure.Recipient, a.Channel == ChannelIds.WhatsApp
                ? "no conversation with this number yet — WhatsApp needs an approved template to open one"
                : $"{ZernioNative.Label(a.Channel)} can only answer someone who has written to it first");
        }
        var body = new Dictionary<string, object?>();
        if (text.Length > 0) body["message"] = text;
        if (!string.IsNullOrEmpty(attachmentUrl))
        {
            body["attachmentUrl"] = attachmentUrl;
            body["attachmentType"] = attachmentType;
            if (a.Channel == ChannelIds.WhatsApp && attachmentType == "file" && !string.IsNullOrEmpty(attachmentName))
                body["attachmentName"] = attachmentName;
        }
        if (!string.IsNullOrEmpty(replyTo) && a.Channel is ChannelIds.WhatsApp or ChannelIds.Telegram)
            body["replyTo"] = replyTo;
        if (!string.IsNullOrEmpty(tag))
        {
            body["messagingType"] = "MESSAGE_TAG";
            body["messageTag"] = tag;
        }
        var res = await _zernio.SendInboxMessageAsync(a.ExternalId, conv.ConversationId, body, ct);
        return ZernioSend.From(res, res.Str("data", "messageId")) with { ConversationId = conv.ConversationId };
    }

    /// <summary>A WhatsApp template (Meta's <c>template</c> object: name, language{code}, components).</summary>
    public async Task<ZernioSend> SendTemplateAsync(ChannelAccount a, string key, object template, CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return ZernioSend.Refused(TransportFailure.Transient, _zernio.NotConfiguredReason);
        var t = JsonSerializer.SerializeToElement(template);
        var name = ZernioJson.Str(t, "name");
        var language = ZernioJson.Str(t, "language", "code");
        if (language.Length == 0) language = ZernioJson.Str(t, "language");
        var components = ZernioJson.At(t, "components");
        var conv = Find(a.Id, key);
        if (conv != null)
        {
            var element = new Dictionary<string, object?> { ["name"] = name, ["language"] = language };
            if (components.ValueKind == JsonValueKind.Array) element["components"] = components;
            var r = await _zernio.SendInboxMessageAsync(a.ExternalId, conv.ConversationId,
                new Dictionary<string, object?> { ["template"] = new Dictionary<string, object?> { ["elements"] = new[] { element } } }, ct);
            return ZernioSend.From(r, r.Str("data", "messageId")) with { ConversationId = conv.ConversationId };
        }
        // no conversation yet: create it with the template (Zernio fills media headers itself)
        var body = new Dictionary<string, object?>
        {
            ["accountId"] = a.ExternalId,
            ["participantId"] = WhatsAppProviders.Digits(key),
            ["templateName"] = name,
            ["templateLanguage"] = language.Length > 0 ? language : null,
        };
        var flat = FlatParams(components);
        if (flat.Count > 0) body["templateParams"] = flat;
        var made = await _zernio.CreateConversationAsync(body, ct);
        var convId = made.Str("data", "conversationId");
        if (made.Ok) Remember(a, key, convId, WhatsAppProviders.Digits(key));
        return ZernioSend.From(made, made.Str("data", "messageId")) with { ConversationId = convId };
    }

    /// <summary>Meta components → Zernio's flat templateParams (header text, body, then URL-button values).</summary>
    public static List<string> FlatParams(JsonElement components)
    {
        var outv = new List<string>();
        if (components.ValueKind != JsonValueKind.Array) return outv;
        foreach (var kind in new[] { "header", "body", "button" })
            foreach (var c in components.EnumerateArray())
            {
                if (!string.Equals(ZernioJson.Str(c, "type"), kind, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var p in ZernioJson.Arr(c, "parameters"))
                {
                    var type = ZernioJson.Str(p, "type");
                    if (type == "text") outv.Add(ZernioJson.Str(p, "text"));
                    else if (kind == "button" && type is "payload" or "") outv.Add(ZernioJson.Str(p, "text"));
                }
            }
        return outv;
    }

    public async Task<ZernioSend> ReactAsync(ChannelAccount a, string key, string platformMessageId, string emoji,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return ZernioSend.Refused(TransportFailure.Transient, _zernio.NotConfiguredReason);
        var conv = Find(a.Id, key);
        if (conv == null) return ZernioSend.Refused(TransportFailure.Recipient, "no conversation for this chat yet");
        var r = await _zernio.ReactAsync(a.ExternalId, conv.ConversationId, platformMessageId, emoji, ct);
        return ZernioSend.From(r, platformMessageId);
    }

    /// <summary>A stored file → Zernio's presigned upload → its public URL (Zernio's send fetches it).</summary>
    public async Task<(string Url, string Error)> UploadAsync(byte[] bytes, string mime, string filename,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return ("", _zernio.NotConfiguredReason);
        var name = string.IsNullOrWhiteSpace(filename) ? "upload" : filename;
        var p = await _zernio.PresignMediaAsync(name, string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime,
            bytes.LongLength, ct);
        if (!p.Ok) return ("", p.Error);
        var up = await _zernio.UploadMediaAsync(p.Str("uploadUrl"), bytes, mime, ct);
        if (!up.Ok) return ("", up.Error);
        var url = p.Str("publicUrl");
        return url.Length > 0 ? (url, "") : ("", "the upload returned no public URL");
    }

    public static string AttachmentType(string mediaType) => mediaType switch
    {
        "image" or "sticker" => "image",
        "video" => "video",
        "audio" => "audio",
        _ => "file",
    };

    // ---- IChannelTransport (Telegram, SMS) --------------------------------------------

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default) =>
        (await SendAsync(a, to.Key, text, ct: ct)).ToTransport();

    public async Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media,
        string? caption, CancellationToken ct = default)
    {
        if (!File.Exists(media.AbsolutePath))
            return TransportResult.Fail(TransportFailure.Unsupported, "the file is missing");
        var bytes = await File.ReadAllBytesAsync(media.AbsolutePath, ct);
        var (url, err) = await UploadAsync(bytes, MessagingMedia.ContentTypeFor(media.FileName), media.FileName, ct);
        if (url.Length == 0) return TransportResult.Fail(TransportFailure.Transient, err);
        return (await SendAsync(a, to.Key, caption ?? "", attachmentUrl: url,
            attachmentType: AttachmentType(media.MediaType), attachmentName: media.FileName, ct: ct)).ToTransport();
    }

    /// <summary>Health = does Zernio still hold this account, active (the social transport's probe).</summary>
    public Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default) =>
        new SocialTransport(_zernio, _scopes).ProbeAsync(a, ct);

    public async Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default)
    {
        var (bytes, _, err) = await _zernio.DownloadAsync(media.Ref, MediaStore.MaxBytes, ct);
        return (bytes, err);
    }
}

/// <summary>
/// WhatsApp via Zernio behind the Cloud seam (<see cref="IWhatsAppCloudClient"/>, routed by
/// <see cref="WhatsAppClientRouter"/>), so every WhatsApp send path — text, quotes,
/// reactions, templates (the 24h window and <c>RouteNeed.Template</c> treat it as an
/// OFFICIAL number), media, the template catalog — works unchanged.
/// Media: an upload is Zernio's presigned upload; the "media id" is its public URL.
/// Templates: Zernio's <c>GET /v1/whatsapp/templates</c> (Meta-shaped); authoring stays in
/// Zernio / Meta (create / edit / delete answer "not supported").
/// </summary>
public sealed class ZernioWhatsAppClient : IWhatsAppCloudClient
{
    private readonly IZernioClient _zernio;
    private readonly ZernioNativeTransport _send;

    public ZernioWhatsAppClient(IZernioClient zernio, ZernioNativeTransport send)
    {
        _zernio = zernio;
        _send = send;
    }

    public bool IsConfigured(ChannelAccount account) =>
        account.Channel == ChannelIds.WhatsApp && ZernioNative.Configured(account) && _zernio.IsConfigured;

    public async Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) =>
        (await _send.SendAsync(account, toWaId, text, ct: ct)).ToOutcome();

    public async Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text,
        string contextWamid, CancellationToken ct = default) =>
        (await _send.SendAsync(account, toWaId, text, replyTo: contextWamid, ct: ct)).ToOutcome();

    public async Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default) =>
        (await _send.ReactAsync(account, toWaId, wamid, emoji, ct)).ToOutcome();

    public async Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default) =>
        (await _send.SendTemplateAsync(account, toWaId, template, ct)).ToOutcome();

    public async Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default)
    {
        var (url, err) = await _send.UploadAsync(bytes, mime, filename, ct);
        return url.Length > 0 ? new SendOutcome(true, url, "") : new SendOutcome(false, "", err);
    }

    public async Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default) =>
        (await _send.SendAsync(account, toWaId, type == "audio" ? "" : caption ?? "", attachmentUrl: mediaId,
            attachmentType: ZernioNativeTransport.AttachmentType(type == "document" ? "file" : type),
            attachmentName: filename, ct: ct)).ToOutcome();

    public async Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default)
    {
        var (bytes, mime, err) = await _zernio.DownloadAsync(mediaId, maxBytes, ct);
        return bytes != null ? new MediaDownload(true, bytes, mime, "") : new MediaDownload(false, Array.Empty<byte>(), "", err);
    }

    public async Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new GraphRead(false, default, "managed WhatsApp connection not configured");
        var r = await _zernio.ListWhatsAppTemplatesAsync(account.ExternalId, ct);
        if (!r.Ok) return new GraphRead(false, default, r.Error);
        var list = ZernioJson.At(r.Json, "templates");
        return new GraphRead(true, list.ValueKind == JsonValueKind.Array ? list.Clone()
            : JsonSerializer.SerializeToElement(Array.Empty<object>()), "");
    }

    public Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(true, JsonSerializer.SerializeToElement(new
        {
            display_phone_number = ZernioNative.Address(account),
            verified_name = SocialChannels.Cfg(account, SocialChannels.DisplayNameKey),
            quality_rating = "",
            status = SocialChannels.Cfg(account, SocialChannels.StatusKey),
        }), ""));
}
