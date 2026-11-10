using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Social;

/// <summary>
/// The vocabulary of the <c>social</c> channel (Zernio): thread keys, what each
/// platform can do, account names and the AI's public-reply line.
///
/// A Zernio account is a <see cref="ChannelAccount"/> (channel <c>social</c>,
/// provider <c>zernio</c>, ExternalId = Zernio's account id, Config.platform), so
/// routing, inbox settings, health and entity scoping work like any other inbox.
/// A conversation is ONE of three kinds, and its key says which:
/// <list type="bullet">
///   <item><c>comment:&lt;account&gt;:&lt;root comment id&gt;</c> — one thread per top-level comment; replies join it;</item>
///   <item><c>review:&lt;account&gt;:&lt;review id&gt;</c> — a Google Business review;</item>
///   <item><c>dm:&lt;account&gt;:&lt;conversation id&gt;</c> — a DM on a platform ChatBranch has no native channel for.</item>
/// </list>
/// An id that is not URL-safe (a Google review's resource name has slashes, a Bluesky
/// comment is an at:// URI) is shortened in the key; the full id lives on
/// <see cref="SocialThread"/>, which is what a reply reads.
/// </summary>
public static class SocialChannels
{
    public const string Comment = "comment";
    public const string Review = "review";
    public const string Dm = "dm";
    public static readonly string[] Kinds = { Comment, Review, Dm };

    /// <summary>The externalId a ChatBranch entity's Zernio profile carries.</summary>
    public static string ProfileExternalId(int entityId) => $"cb-entity-{entityId}";

    /// <summary>The entity a profile externalId names, or null.</summary>
    public static int? EntityFromExternalId(string? externalId)
    {
        var m = Regex.Match(externalId ?? "", @"^cb-entity-(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    public static bool IsSocial(string? channel) => channel == ChannelIds.Social;
    public static bool IsSocial(Thread? t) => t != null && IsSocial(t.Channel);

    /// <summary>A PUBLIC conversation (a comment or a review): no contact, no identity ask, no flows.</summary>
    public static bool IsPublic(Thread? t) => IsSocial(t) && KindOf(t!.Key) is Comment or Review;

    // ---- platforms ---------------------------------------------------------------

    /// <summary>Platforms whose comments Zernio delivers (<c>comment.received</c>).</summary>
    public static readonly HashSet<string> CommentPlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "instagram", "facebook", "threads", "youtube", "linkedin", "bluesky", "reddit", "tiktok" };

    /// <summary>Platforms whose DMs Zernio delivers (<c>message.received</c>).</summary>
    public static readonly HashSet<string> DmPlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "instagram", "facebook", "telegram", "whatsapp", "sms", "rcs", "twitter", "bluesky", "reddit", "slack", "tiktok", "imessage" };

    /// <summary>Platforms whose reviews arrive by webhook (<c>review.new</c> / <c>review.updated</c>).</summary>
    public static readonly HashSet<string> ReviewPlatforms = new(StringComparer.OrdinalIgnoreCase) { "googlebusiness" };

    /// <summary>Platforms a post can target (PlatformTarget.platform + the platform-data variants).</summary>
    public static readonly HashSet<string> PublishPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "twitter", "threads", "instagram", "youtube", "facebook", "linkedin", "pinterest", "reddit", "tiktok",
        "bluesky", "googlebusiness", "telegram", "snapchat", "discord", "slack",
    };

    /// <summary>Hide / unhide a comment (Facebook, Instagram, Threads, X, TikTok).</summary>
    public static readonly HashSet<string> HidePlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "facebook", "instagram", "threads", "twitter", "tiktok" };

    /// <summary>A private reply (DM) to a commenter (Instagram, Facebook — one per comment, within 7 days).</summary>
    public static readonly HashSet<string> PrivateReplyPlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "instagram", "facebook" };

    /// <summary>The platforms the Connect buttons may name (GET /v1/connect/{platform}).</summary>
    public static readonly HashSet<string> ConnectPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "facebook", "instagram", "linkedin", "twitter", "tiktok", "youtube", "threads", "reddit", "pinterest",
        "bluesky", "googlebusiness", "telegram", "snapchat", "discord", "slack", "whatsapp",
    };

    /// <summary>
    /// The native ChatBranch channel that already carries a platform's DMs. A DM from
    /// Zernio is dropped when the entity has such an inbox, so a customer never has two
    /// inboxes for one Instagram conversation.
    /// </summary>
    public static string? NativeChannel(string platform) => platform.ToLowerInvariant() switch
    {
        "instagram" => ChannelIds.Instagram,
        "facebook" => ChannelIds.Facebook,
        "whatsapp" => ChannelIds.WhatsApp,
        "telegram" => ChannelIds.Telegram,
        "sms" => ChannelIds.Sms,
        "tiktok" => ChannelIds.TikTok,
        _ => null,
    };

    public static string PlatformLabel(string platform) => platform.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "facebook" => "Facebook",
        "threads" => "Threads",
        "youtube" => "YouTube",
        "linkedin" => "LinkedIn",
        "bluesky" => "Bluesky",
        "reddit" => "Reddit",
        "tiktok" => "TikTok",
        "twitter" => "X",
        "pinterest" => "Pinterest",
        "googlebusiness" => "Google Business",
        "telegram" => "Telegram",
        "whatsapp" => "WhatsApp",
        "snapchat" => "Snapchat",
        "discord" => "Discord",
        "slack" => "Slack",
        _ => platform,
    };

    // ---- keys ----------------------------------------------------------------------

    private static readonly Regex Safe = new(@"^[A-Za-z0-9_.\-:]{1,120}$", RegexOptions.Compiled);

    /// <summary>
    /// An id as it may appear in a thread key (and so in a URL path segment): kept
    /// when it is URL-safe; a Google review name keeps its last segment; anything else
    /// becomes a stable hash. The full id is stored on <see cref="SocialThread"/>.
    /// </summary>
    public static string KeyPart(string id)
    {
        var raw = (id ?? "").Trim();
        if (Safe.IsMatch(raw)) return raw;
        var slash = raw.LastIndexOf('/');
        if (slash >= 0 && slash < raw.Length - 1 && Safe.IsMatch(raw[(slash + 1)..]) && raw.Contains("/reviews/"))
            return raw[(slash + 1)..];
        return "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    public static string ThreadKey(string kind, string accountId, string id) => $"{kind}:{accountId}:{KeyPart(id)}";

    /// <summary>A key split into (kind, Zernio account id, id part) — kind "" when it is no social key.</summary>
    public static (string Kind, string AccountId, string Part) ParseKey(string? key)
    {
        var raw = key ?? "";
        var a = raw.IndexOf(':');
        if (a <= 0) return ("", "", raw);
        var b = raw.IndexOf(':', a + 1);
        if (b <= a + 1) return ("", "", raw);
        var kind = raw[..a];
        return Array.IndexOf(Kinds, kind) < 0 ? ("", "", raw) : (kind, raw[(a + 1)..b], raw[(b + 1)..]);
    }

    public static string KindOf(string? key) => ParseKey(key).Kind;

    // ---- post targets ----------------------------------------------------------------

    /// <summary>
    /// A post target: <c>"&lt;accountId&gt;"</c>, or <c>"&lt;accountId&gt;:&lt;pageId&gt;"</c> for one
    /// Facebook Page of a facebook account (Zernio account ids never contain ':').
    /// </summary>
    public static (string AccountId, string PageId) ParseTarget(string? entry)
    {
        var raw = (entry ?? "").Trim();
        var i = raw.IndexOf(':');
        return i < 0 ? (raw, "") : (raw[..i].Trim(), raw[(i + 1)..].Trim());
    }

    public static string Target(string accountId, string pageId) =>
        string.IsNullOrEmpty(pageId) ? accountId : $"{accountId}:{pageId}";

    // ---- accounts ------------------------------------------------------------------

    public const string PlatformKey = "platform";
    public const string UsernameKey = "username";
    public const string DisplayNameKey = "display_name";
    public const string AvatarKey = "avatar";
    public const string ProfileUrlKey = "profile_url";
    public const string ProfileIdKey = "zernio_profile_id";
    /// <summary>"connected" | "disconnected" | "removed" (Zernio's view of the account).</summary>
    public const string StatusKey = "zernio_status";
    /// <summary>When the account was last moved to another profile from ChatBranch (ISO, UTC).</summary>
    public const string MovedAtKey = "zernio_moved_at";

    public static string Cfg(ChannelAccount a, string key) =>
        a.Config != null && a.Config.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";

    public static string Platform(ChannelAccount a) => Cfg(a, PlatformKey);

    /// <summary>A Zernio account ChatBranch may send through (the API key itself is checked by the client).</summary>
    public static bool Configured(ChannelAccount a) =>
        IsSocial(a.Channel) && a.Provider == Providers.Zernio && a.ExternalId.Length > 0
        && Cfg(a, StatusKey) != "removed";

    /// <summary>"@username (Platform)".</summary>
    public static string AccountName(string platform, string username, string displayName)
    {
        var who = username.Trim();
        if (who.Length == 0) who = displayName.Trim();
        if (who.Length > 0 && !who.StartsWith('@') && platform != "googlebusiness") who = "@" + who;
        if (who.Length == 0) who = "account";
        var full = $"{who} ({PlatformLabel(platform)})";
        return full.Length > 120 ? full[..120] : full;
    }

    /// <summary>What a connected account can do in ChatBranch (the account payload's can_* flags).</summary>
    public static (bool Publish, bool Comments, bool Dm, bool Reviews) Capabilities(string platform) =>
        (PublishPlatforms.Contains(platform), CommentPlatforms.Contains(platform),
         DmPlatforms.Contains(platform), ReviewPlatforms.Contains(platform));

    /// <summary>Why a social account cannot be created or credential-edited from the accounts API.</summary>
    public const string NotEditable =
        "A social account is a managed connection, connected from Channels → Add inbox, not here. " +
        "It then appears as an inbox by itself; you can still choose who answers it, its team and its AI default here.";

    // ---- the AI ------------------------------------------------------------------------

    /// <summary>
    /// The system line for an AI reply in a social conversation. A comment or a review
    /// is answered IN PUBLIC: short, safe, nothing personal.
    /// </summary>
    public static string AiSystemLine(Thread thread, string platform)
    {
        var kind = KindOf(thread.Key);
        var where = PlatformLabel(platform.Length > 0 ? platform : "social media");
        return kind switch
        {
            Comment =>
                $"THIS IS A PUBLIC COMMENT on the business's {where} post. Your reply is posted publicly under it, " +
                "visible to everyone. Reply briefly (one to three short sentences), friendly and professional. Never " +
                "share personal details, order or account information, private prices, phone numbers or anything " +
                "about another customer; for anything private, invite them to send a direct message. Never argue.",
            Review =>
                $"THIS IS A PUBLIC {where.ToUpperInvariant()} REVIEW. Your reply is shown publicly under the review. " +
                "Thank the reviewer, keep it brief (two to four sentences) and professional, address a complaint " +
                "calmly and offer to continue privately. Never share personal or order details and never argue.",
            _ =>
                $"This is a direct message on {where}, received through a managed connection. Keep replies short and conversational.",
        };
    }
}
