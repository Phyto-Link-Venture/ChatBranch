namespace ChatBranch.Domain;

// Django stores these as short strings. We keep the exact wire values as
// constants (not C# enums) so the DB column round-trips byte-for-byte and the
// Next.js frontend contract is unchanged. Group them by the model they belong
// to for readability.

public static class Roles
{
    public const string SuperAdmin = "superadmin";
    public const string Admin = "admin";
    public const string Agent = "agent";
}

public static class Channels
{
    public const string WhatsApp = "whatsapp";
    public const string Facebook = "facebook";
    public const string Instagram = "instagram";
    public const string Telegram = "telegram";
    public const string Email = "email";
    /// <summary>Embeddable site widget — the visitor's browser is the transport.</summary>
    public const string Website = "website";
    /// <summary>LINE Messaging API (a LINE Official Account's bot).</summary>
    public const string Line = "line";
    /// <summary>Viber REST Bot API.</summary>
    public const string Viber = "viber";
    /// <summary>SMS / MMS through Twilio — phone-keyed like WhatsApp.</summary>
    public const string Sms = "sms";
    /// <summary>WeChat Official Account (公众号), Customer Service message API.</summary>
    public const string WeChat = "wechat";
    /// <summary>TikTok Business Messaging API (partner-gated).</summary>
    public const string TikTok = "tiktok";
    /// <summary>Douyin (抖音) enterprise account private messages (企业号私信).</summary>
    public const string Douyin = "douyin";
    /// <summary>Zalo Official Account.</summary>
    public const string Zalo = "zalo";

    // ---- marketplace seller channels (Channels/Commerce) --------------------
    // Buyer chat on a marketplace, carried by the e-comerce-seller-mcp sidecar,
    // which holds the marketplace grant. The shop is the ChannelAccount.

    /// <summary>Shopee buyer chat (seller chat), through the seller service.</summary>
    public const string Shopee = "shopee";
    /// <summary>Lazada buyer chat (IM), through the seller service.</summary>
    public const string Lazada = "lazada";
    /// <summary>
    /// TikTok Shop buyer chat (Seller Center), through the seller service — a
    /// different platform from <see cref="TikTok"/>, which is TikTok Business
    /// Messaging on a marketing account.
    /// </summary>
    public const string TikTokShop = "tiktok_shop";
    /// <summary>
    /// A Shopify store, through the seller service. It carries orders and
    /// products; Shopify has no public buyer-chat API, so it never carries a
    /// conversation.
    /// </summary>
    public const string Shopify = "shopify";

    // ---- social media through Zernio (Data/Social) ---------------------------
    /// <summary>
    /// Public comments, Google Business reviews and the DMs of platforms ChatBranch
    /// has no native channel for, carried by Zernio (one Zernio account = one
    /// <see cref="ChannelAccount"/>, provider <see cref="Providers.Zernio"/>). The
    /// thread key says what it is: <c>comment:&lt;account&gt;:&lt;root comment&gt;</c>,
    /// <c>review:&lt;account&gt;:&lt;review&gt;</c> or <c>dm:&lt;account&gt;:&lt;conversation&gt;</c>.
    /// </summary>
    public const string Social = "social";
}

public static class Providers
{
    public const string Cloud = "cloud";
    public const string Baileys = "baileys";
    /// <summary>YCloud — an official WhatsApp Business Solution Provider (Meta-shaped API).</summary>
    public const string YCloud = "ycloud";
    /// <summary>Twilio's WhatsApp sender (Programmable Messaging + the Content API).</summary>
    public const string Twilio = "twilio";
    /// <summary>Zernio — the social-media API behind the <see cref="Channels.Social"/> channel.</summary>
    public const string Zernio = "zernio";
}

public static class Health
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Disconnected = "disconnected";
    public const string Banned = "banned";
}

public static class ThreadStatus
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string Snoozed = "snoozed";
    public const string Resolved = "resolved";
}

public static class MessageDirection
{
    public const string In = "in";
    public const string Out = "out";
}

public static class MessageSource
{
    public const string Customer = "customer";
    public const string Agent = "agent";
    public const string Ai = "ai";
    public const string System = "system";
    /// <summary>
    /// A mail a person sent from their own mail app (Outlook, Gmail…), read back from
    /// the mailbox's Sent folder by the IMAP sync — stored as outbound, never sent by us.
    /// </summary>
    public const string ExternalClient = "external_client";

    /// <summary>A person wrote it (in ChatBranch or in their own mail app) — not the AI, not the system.</summary>
    public static bool IsPerson(string? source) => source is Agent or ExternalClient;
}

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class JobKind
{
    public const string Text = "text";
    public const string Template = "template";
    public const string AiReply = "ai_reply";
    public const string Call = "call";
}

public static class FlowRunStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
