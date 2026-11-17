using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Zernio;

/// <summary>
/// The vocabulary of a Zernio-carried inbox (a managed connection): the profile
/// externalId an entity's Zernio profile carries, the config keys the mirror keeps on a
/// <see cref="ChannelAccount"/>, URL-safe key parts and platform labels.
///
/// ChatBranch uses Zernio ONLY as a connection method on its native messaging channels
/// (<see cref="ZernioNative"/>): WhatsApp, Messenger, Instagram DM, Telegram and SMS.
/// Social-media features (posts, public comments, reviews) are out of scope.
/// </summary>
public static class ZernioKeys
{
    /// <summary>The externalId a ChatBranch entity's Zernio profile carries.</summary>
    public static string ProfileExternalId(int entityId) => $"cb-entity-{entityId}";

    /// <summary>The entity a profile externalId names, or null.</summary>
    public static int? EntityFromExternalId(string? externalId)
    {
        var m = Regex.Match(externalId ?? "", @"^cb-entity-(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>The platforms the Connect buttons may name (GET /v1/connect/{platform}) — messaging only.</summary>
    public static readonly HashSet<string> ConnectPlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "whatsapp", "facebook", "instagram", "telegram" };

    public static string PlatformLabel(string platform) => platform.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "facebook" => "Messenger",
        "telegram" => "Telegram",
        "whatsapp" => "WhatsApp",
        "sms" => "SMS",
        _ => platform,
    };

    private static readonly Regex Safe = new(@"^[A-Za-z0-9_.\-:]{1,120}$", RegexOptions.Compiled);

    /// <summary>
    /// An id as it may appear in a thread key (and so in a URL path segment): kept when it is
    /// URL-safe, else a stable hash (a WhatsApp BSUID can carry characters a key must not).
    /// </summary>
    public static string KeyPart(string id)
    {
        var raw = (id ?? "").Trim();
        if (Safe.IsMatch(raw)) return raw;
        return "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    // ---- account config ----------------------------------------------------------------

    public const string PlatformKey = "platform";
    public const string UsernameKey = "username";
    public const string DisplayNameKey = "display_name";
    public const string AvatarKey = "avatar";
    public const string ProfileIdKey = "zernio_profile_id";
    /// <summary>"connected" | "disconnected" | "removed" (Zernio's view of the account).</summary>
    public const string StatusKey = "zernio_status";
    /// <summary>When the account was last moved to another profile from ChatBranch (ISO, UTC).</summary>
    public const string MovedAtKey = "zernio_moved_at";

    public static string Cfg(ChannelAccount a, string key) =>
        a.Config != null && a.Config.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";

    public static string Platform(ChannelAccount a) => Cfg(a, PlatformKey);
}
