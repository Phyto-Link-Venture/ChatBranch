using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Short-lived, signed, PUBLIC links to one stored media file — for providers that fetch
/// an outbound attachment themselves instead of taking an upload (Twilio's <c>MediaUrl</c>;
/// YCloud's <c>link</c> as a fallback). The token is
/// <c>base64url({c: channel, p: path, e: expiry}).base64url(HMAC-SHA256)</c> under a key
/// generated once and kept in Setting <c>media_link_secret</c>; the anonymous route
/// <c>GET /api/media/public/{token}</c> serves the file while the link is valid. A link
/// names one file of one conversation's media root and nothing else — no listing, no
/// path walking (the media store resolves it under its own root).
/// </summary>
public sealed class MediaLinks
{
    public const string SecretKey = "media_link_secret";
    public const string RoutePrefix = "/api/media/public/";
    /// <summary>Long enough for a provider's fetch (and its retries), short enough to be useless later.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private readonly ScopingService _sc;

    public MediaLinks(ScopingService sc) => _sc = sc;

    private byte[] Secret()
    {
        var stored = _sc.SettingValue(SecretKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = B64(RandomNumberGenerator.GetBytes(32));
            _sc.SetSetting(SecretKey, stored);
        }
        return Encoding.UTF8.GetBytes(stored);
    }

    /// <summary>The public base (Setting public_base_url → PUBLIC_BASE_URL → FRONTEND_URL → localhost).</summary>
    public string Base => ChatBranch.Data.Documents.DocumentService.PublicBase(_sc);

    /// <summary>Can a provider on the internet reach <see cref="Base"/>? (https, not a loopback/local host)</summary>
    public static bool Reachable(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        return !(host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "0.0.0.0" || host.EndsWith(".local")
                 || host.EndsWith(".localhost"));
    }

    /// <summary>A link for one stored file, or null when the API has no publicly reachable address.</summary>
    public string? PublicUrl(string channel, string relPath, DateTime? nowUtc = null)
    {
        var b = Base;
        if (!Reachable(b) || string.IsNullOrWhiteSpace(relPath)) return null;
        return $"{b}{RoutePrefix}{Mint(channel, relPath, nowUtc)}";
    }

    public string Mint(string channel, string relPath, DateTime? nowUtc = null)
    {
        var exp = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).Add(Lifetime).ToUnixTimeSeconds();
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new { c = channel, p = relPath, e = exp }));
        return $"{body}.{B64(HMACSHA256.HashData(Secret(), Encoding.ASCII.GetBytes(body)))}";
    }

    /// <summary>The (channel, path) a valid, unexpired token names; null otherwise.</summary>
    public (string Channel, string RelPath)? Verify(string? token, DateTime? nowUtc = null)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[0].Length > 2048) return null;
        var expected = B64(HMACSHA256.HashData(Secret(), Encoding.ASCII.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1])))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(FromB64(parts[0]));
            var root = doc.RootElement;
            var exp = root.GetProperty("e").GetInt64();
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < new DateTimeOffset(nowUtc ?? DateTime.UtcNow)) return null;
            var channel = root.GetProperty("c").GetString() ?? "";
            var path = root.GetProperty("p").GetString() ?? "";
            return channel.Length == 0 || path.Length == 0 ? null : (channel, path);
        }
        catch { return null; }
    }

    private static string B64(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
