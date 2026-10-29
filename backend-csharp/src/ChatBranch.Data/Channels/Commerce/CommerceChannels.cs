using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The marketplace seller channels — Shopee, Lazada, TikTok Shop and Shopify.
///
/// They are ordinary inbox channels (a <see cref="ChannelAccount"/> per SHOP, a
/// <see cref="Thread"/> per buyer conversation), so the inbox, contacts, teams,
/// automations, the AI and entity scoping all work with no special-casing. What
/// makes them different from the seven in <see cref="MoreChannels"/> is where
/// the credentials live: **not here**. A marketplace grant belongs to the
/// `e-comerce-seller-mcp` sidecar, which owns the OAuth flow, the token refresh
/// and the provider API — exactly as <c>backend/baileys_service</c> owns a
/// paired WhatsApp number. ChatBranch holds the conversation and the AI.
///
/// So an account here carries no token: its <see cref="ChannelAccount.ExternalId"/>
/// is the shop handle the sidecar knows it by (<c>shopee:123456</c>), there is no
/// per-account webhook (the sidecar verifies the marketplace's signature and
/// pushes to <c>/api/commerce/inbound</c>), and the account is created by that
/// push or by a sync rather than typed in. Everything else — who answers it, the
/// AI default, the team, labels, handover — is edited like any other inbox.
/// </summary>
public static class CommerceChannels
{
    public static readonly string[] All =
    {
        ChannelIds.Shopee, ChannelIds.Lazada, ChannelIds.TikTokShop, ChannelIds.Shopify,
    };

    public static bool Handles(string? channel) => channel != null && Array.IndexOf(All, channel) >= 0;

    public static string Label(string channel) => channel switch
    {
        ChannelIds.Shopee => "Shopee",
        ChannelIds.Lazada => "Lazada",
        ChannelIds.TikTokShop => "TikTok Shop",
        ChannelIds.Shopify => "Shopify",
        _ => channel,
    };

    /// <summary>
    /// The marketplace id the sidecar uses. Only TikTok Shop differs: the channel
    /// is <c>tiktok_shop</c> (distinct from the existing <c>tiktok</c> Business
    /// Messaging channel) while the sidecar calls the marketplace <c>tiktokshop</c>.
    /// </summary>
    public static string Marketplace(string channel) => channel switch
    {
        ChannelIds.TikTokShop => "tiktokshop",
        _ => channel,
    };

    /// <summary>The channel a sidecar marketplace id belongs to, or null.</summary>
    public static string? ChannelFor(string? marketplace) => (marketplace ?? "").Trim().ToLowerInvariant() switch
    {
        "shopee" => ChannelIds.Shopee,
        "lazada" => ChannelIds.Lazada,
        "tiktokshop" or "tiktok_shop" => ChannelIds.TikTokShop,
        "shopify" => ChannelIds.Shopify,
        _ => null,
    };

    /// <summary>
    /// A shop handle (<c>shopee:123456</c>) split into its marketplace and shop id.
    /// Shopify's shop id is its domain, which contains dots but no colon, so the
    /// FIRST colon is the separator.
    /// </summary>
    public static (string Marketplace, string ShopId) ParseAccountKey(string? externalId)
    {
        var raw = (externalId ?? "").Trim();
        var colon = raw.IndexOf(':');
        return colon <= 0 ? ("", raw) : (raw[..colon], raw[(colon + 1)..]);
    }

    /// <summary>The handle the sidecar knows a shop by.</summary>
    public static string AccountKey(string channel, string shopId) => $"{Marketplace(channel)}:{shopId}";

    /// <summary>
    /// Whether this account can carry a message. A commerce account holds no
    /// credential of its own, so "configured" means it names a shop the sidecar
    /// can act for; whether the sidecar itself is reachable is a health question,
    /// answered by the transport's probe rather than guessed at here.
    /// </summary>
    public static bool Configured(ChannelAccount a) =>
        Handles(a.Channel) && ParseAccountKey(a.ExternalId).Marketplace.Length > 0;

    /// <summary>
    /// Buyer chat exists on three of the four. Shopify Inbox has no public API, so
    /// a Shopify shop appears for its orders and products but can never carry a
    /// reply — and says so rather than failing at send time.
    /// </summary>
    public static bool CarriesChat(string? channel) => Handles(channel) && channel != ChannelIds.Shopify;

    /// <summary>The account payload's commerce part (there is no secret to mask).</summary>
    public static void AddPayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
        var (marketplace, shopId) = ParseAccountKey(a.ExternalId);
        outv["marketplace"] = marketplace;
        outv["shop_id"] = shopId;
        outv["shop"] = a.ExternalId;
        outv["configured"] = Configured(a);
        outv["carries_chat"] = CarriesChat(a.Channel);
        // the credentials are the sidecar's; nothing here is editable by hand
        outv["credentials_held_by"] = "seller_service";
        outv["webhook_path"] = "";
    }

    /// <summary>Why a commerce account cannot be created or edited by hand.</summary>
    public static string NotEditable(string channel) =>
        $"A {Label(channel)} shop is connected in the seller service, not here: sign in there and approve access " +
        "for the shop. It then appears as an inbox by itself. You can still choose who answers it, its team and " +
        "its AI default from this page.";
}
