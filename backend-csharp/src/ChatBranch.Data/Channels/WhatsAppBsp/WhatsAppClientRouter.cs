using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The <see cref="IWhatsAppCloudClient"/> the app resolves: every call goes to the client of
/// the account's provider — Meta's Graph (<see cref="WhatsAppCloudClient"/>),
/// <see cref="YCloudWhatsAppClient"/>, <see cref="TwilioWhatsAppClient"/> or
/// <see cref="WahaWhatsAppClient"/>. So every send path, the template catalog, the media
/// fetcher and the account Test treat a number the same whoever carries it; Baileys never
/// reaches here (<see cref="IsConfigured"/> is false for it) because its gateway is a
/// different seam (<c>IBaileysGateway</c>).
///
/// WAHA rides this seam but is NOT official: it answers "not supported" to templates by
/// inheriting the interface's defaults, and <see cref="WhatsAppProviders.IsOfficial"/> keeps
/// it out of every template route, the 24h window and the reminder role.
/// </summary>
public sealed class WhatsAppClientRouter : IWhatsAppCloudClient
{
    private readonly IWhatsAppCloudClient _meta;
    private readonly IWhatsAppCloudClient _ycloud;
    private readonly IWhatsAppCloudClient _twilio;
    /// <summary>WhatsApp through the operator's own WAHA server (unofficial).</summary>
    private readonly IWhatsAppCloudClient _waha;
    /// <summary>WhatsApp via Zernio (<see cref="ChatBranch.Data.Zernio.ZernioWhatsAppClient"/>); null = not wired.</summary>
    private readonly IWhatsAppCloudClient? _zernio;

    public WhatsAppClientRouter(IWhatsAppCloudClient meta, YCloudWhatsAppClient ycloud, TwilioWhatsAppClient twilio,
        WahaWhatsAppClient waha, IWhatsAppCloudClient? zernio = null)
    {
        _meta = meta;
        _ycloud = ycloud;
        _twilio = twilio;
        _waha = waha;
        _zernio = zernio;
    }

    private IWhatsAppCloudClient For(ChannelAccount a) => WhatsAppProviders.Of(a) switch
    {
        Providers.YCloud => _ycloud,
        Providers.Twilio => _twilio,
        Providers.Waha => _waha,
        Providers.Zernio => _zernio ?? NoZernio.Instance,
        _ => _meta,
    };

    public bool IsConfigured(ChannelAccount account) =>
        WhatsAppProviders.Of(account) != Providers.Baileys && For(account).IsConfigured(account);

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text, CancellationToken ct = default) =>
        For(account).SendTextAsync(account, toWaId, text, ct);

    public Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text, string contextWamid,
        CancellationToken ct = default) =>
        For(account).SendReplyTextAsync(account, toWaId, text, contextWamid, ct);

    public Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default) =>
        For(account).SendReactionAsync(account, toWaId, wamid, emoji, ct);

    public Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default) =>
        For(account).SendTemplateAsync(account, toWaId, template, ct);

    public Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default) =>
        For(account).UploadMediaAsync(account, bytes, mime, filename, ct);

    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default) =>
        For(account).SendMediaAsync(account, toWaId, type, mediaId, caption, filename, ct);

    public Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default) =>
        For(account).DownloadMediaAsync(account, mediaId, maxBytes, ct);

    public Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default) =>
        For(account).ListTemplatesAsync(account, ct);

    public Task<GraphRead> CreateTemplateAsync(ChannelAccount account, object payload, CancellationToken ct = default) =>
        For(account).CreateTemplateAsync(account, payload, ct);

    public Task<GraphRead> EditTemplateAsync(ChannelAccount account, string templateId, object payload,
        CancellationToken ct = default) =>
        For(account).EditTemplateAsync(account, templateId, payload, ct);

    public Task<GraphRead> DeleteTemplateAsync(ChannelAccount account, string name, string? templateId,
        CancellationToken ct = default) =>
        For(account).DeleteTemplateAsync(account, name, templateId, ct);

    public Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default) =>
        For(account).GetPhoneNumberAsync(account, ct);

    /// <summary>The providers with their own authenticated liveness probe: the BSPs, and WAHA's session status.</summary>
    public Task<BspProbe?> ProbeBspAsync(ChannelAccount account, CancellationToken ct = default) =>
        WhatsAppProviders.HasOwnProbe(account)
            ? For(account).ProbeBspAsync(account, ct)
            : Task.FromResult<BspProbe?>(null);
}

/// <summary>A Zernio WhatsApp account on a host without the Zernio client: never configured.</summary>
internal sealed class NoZernio : IWhatsAppCloudClient
{
    public static readonly NoZernio Instance = new();
    public bool IsConfigured(ChannelAccount account) => false;
    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text, CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "Zernio is not wired on this host"));
}
