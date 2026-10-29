using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The transport for one marketplace channel. Four instances of this class — one
/// per channel — are registered as <see cref="IChannelTransport"/>, so the whole
/// existing send path (<see cref="OutboundSender"/> → <see cref="MoreChannelSender"/>
/// → routing → health) carries marketplace chat with no further wiring.
///
/// Every send is one call to the seller service, which holds the marketplace
/// grant and does the provider round-trip. That is the point: this backend never
/// learns four marketplace APIs, and the sidecar never learns about threads.
///
/// Honest refusals matter here more than usual:
/// <list type="bullet">
///   <item>Shopify has no public buyer-chat API, so a send is refused as
///         <see cref="TransportFailure.Unsupported"/> — a failed row with the
///         reason, not a pretend success and not an account marked unhealthy;</item>
///   <item>a seller service that is not attached is <see cref="TransportFailure.Transient"/>:
///         the shop is fine, the sidecar is down, so health stays put;</item>
///   <item>a grant the marketplace has revoked comes back from the sidecar as an
///         auth failure, which DOES move health — the seller has to reconnect.</item>
/// </list>
/// </summary>
public sealed class CommerceTransport : IChannelTransport
{
    private readonly ICommerceGateway _gateway;

    public CommerceTransport(string channel, ICommerceGateway gateway)
    {
        Channel = channel;
        _gateway = gateway;
    }

    public string Channel { get; }

    /// <summary>
    /// Marketplace chat boxes are small; 4 000 characters is comfortably under
    /// every one of them, and <see cref="MoreChannelSender"/> splits longer text.
    /// </summary>
    public int MaxText => 4000;

    /// <summary>
    /// Sending a file is not carried yet: the marketplaces' chat media APIs each
    /// want their own upload step. Inbound media still arrives — the sidecar
    /// pushes the marketplace's own URL — so an operator sees what a buyer sent.
    /// </summary>
    public bool SupportsMedia => false;

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default)
    {
        if (!CommerceChannels.CarriesChat(Channel))
            return TransportResult.Fail(TransportFailure.Unsupported,
                $"{CommerceChannels.Label(Channel)} has no buyer-chat API, so a reply cannot be sent from here.");
        if (!_gateway.IsConfigured)
            return TransportResult.Fail(TransportFailure.Transient, _gateway.NotConfiguredReason);

        var result = await _gateway.SendTextAsync(a.ExternalId, to.Key, text, ct);
        if (result.Ok) return TransportResult.Success(result.ExternalId);

        return TransportResult.Fail(Classify(result.Detail), result.Detail);
    }

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            $"{CommerceChannels.Label(Channel)} chat cannot carry an uploaded file from ChatBranch — send a link instead."));

    /// <summary>
    /// Health is "does the seller service still hold a working grant for this
    /// shop". The sidecar already knows, so the probe asks it rather than calling
    /// the marketplace twice.
    /// </summary>
    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        if (!_gateway.IsConfigured)
            return new TransportProbe(new ProbeVerdict(null, _gateway.NotConfiguredReason, Transient: true));

        var (shops, error) = await _gateway.ShopsAsync(ct);
        if (error.Length > 0) return new TransportProbe(new ProbeVerdict(null, error, Transient: true));

        var mine = shops.FirstOrDefault(s => s.Account == a.ExternalId);
        if (mine == null)
            return new TransportProbe(new ProbeVerdict(Health.Disconnected,
                $"The seller service no longer holds a grant for {a.ExternalId} — reconnect the shop there."));

        var verdict = mine.Status switch
        {
            "connected" => new ProbeVerdict(Health.Healthy, ""),
            "revoked" => new ProbeVerdict(Health.Disconnected,
                mine.StatusDetail.Length > 0 ? mine.StatusDetail : "the marketplace revoked the grant — reconnect the shop"),
            "disconnected" => new ProbeVerdict(Health.Disconnected,
                mine.StatusDetail.Length > 0 ? mine.StatusDetail : "disconnected in the seller service"),
            _ => new ProbeVerdict(Health.Unknown, mine.StatusDetail),
        };
        return new TransportProbe(verdict, mine.ShopName);
    }

    /// <summary>
    /// Nothing to download: the sidecar hands over the marketplace's own media URL
    /// with the message, so there is no second fetch for this backend to make.
    /// </summary>
    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string)>((null, "marketplace media is linked, not downloaded"));

    /// <summary>
    /// What the sidecar's refusal means for the account's health. Its failure
    /// codes travel in the detail string, which is why they are matched here
    /// rather than guessed from an HTTP status.
    /// </summary>
    private static TransportFailure Classify(string detail)
    {
        var d = detail.ToLowerInvariant();
        if (d.Contains("not_connected") || d.Contains("reconnect") || d.Contains("auth"))
            return TransportFailure.Auth;
        if (d.Contains("unsupported")) return TransportFailure.Unsupported;
        if (d.Contains("rate_limited") || d.Contains("too many")) return TransportFailure.RateLimit;
        // an unreachable or timed-out sidecar says nothing about the shop
        if (d.Contains("unreachable") || d.Contains("timed out") || d.Contains("not attached"))
            return TransportFailure.Transient;
        if (d.Contains("invalid") || d.Contains("no such")) return TransportFailure.Recipient;
        return TransportFailure.Transient;
    }

}
