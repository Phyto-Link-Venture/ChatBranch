using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatBranch.Api.Auth;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Short-lived SIGNED links to one message's attachment, so the inbox's
/// <c>&lt;audio&gt;</c> / <c>&lt;video&gt;</c> / download links can use a plain
/// <c>src</c> (with HTTP Range — seeking, streaming) instead of blob-downloading
/// every file with the bearer header first.
///
/// Token = <c>{message id}.{profile id}.{expiry unix}.{base64url HMAC-SHA256}</c>
/// over <c>"{message}.{profile}.{username}.{expiry}"</c> with a per-process random
/// key: nothing is stored, a restart voids every link, and renaming the user voids
/// theirs. The expiry is rounded UP to a 30-minute grid (60–90 minutes ahead), so
/// a thread re-rendered by the live stream keeps the SAME link for half an hour
/// and a playing voice note is not restarted by a fresh src.
///
/// The link is minted only for a signed-in SESSION (never an API key / MCP — they
/// have the authenticated route) while a request is being answered: the payload
/// builders read the caller from <see cref="Bind"/>'s async-flow slot. Opening it
/// (<c>GET /api/media/s/{token}</c>, anonymous at the door) re-checks that the
/// person is still active and may still see that conversation.
/// </summary>
public static class MediaStreamLinks
{
    public static readonly TimeSpan Grid = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(90);

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private sealed record Caller(int ProfileId, string Username);

    private static readonly AsyncLocal<Caller?> CallerSlot = new();
    private static readonly AsyncLocal<MediaStore?> StoreSlot = new();

    /// <summary>Middleware: publish the request's session user + the media store to the
    /// payload builders (static, sync) for the rest of the request.</summary>
    public static IApplicationBuilder UseMediaStreamLinks(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            StoreSlot.Value = ctx.RequestServices.GetService<MediaStore>();
            var user = ctx.User;
            CallerSlot.Value = user.Identity?.IsAuthenticated == true && !ApiKeyAuth.IsApiKey(user)
                && int.TryParse(user.FindFirstValue("profile_id"), out var pid)
                && user.Identity?.Name is { Length: > 0 } name
                ? new Caller(pid, name)
                : null;
            await next();
        });

    public static string Sign(int messageId, int profileId, string username, long expiresUnix)
    {
        var sig = B64(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(
            $"{messageId}.{profileId}.{username}.{expiresUnix}")));
        return $"{messageId}.{profileId}.{expiresUnix}.{sig}";
    }

    /// <summary>The expiry for a link minted at <paramref name="now"/>: the next 30-minute
    /// boundary plus an hour.</summary>
    public static long ExpiryFor(DateTimeOffset now)
    {
        var g = (long)Grid.TotalSeconds;
        return (now.ToUnixTimeSeconds() / g + 1) * g + 2 * g;
    }

    public enum Verdict { Ok, Malformed, Expired }

    /// <summary>Parse a token (signature checked later, against the profile's CURRENT username).</summary>
    public static Verdict Parse(string? token, DateTimeOffset now, out int messageId, out int profileId,
        out long expires, out string sig)
    {
        messageId = profileId = 0;
        expires = 0;
        sig = "";
        if (string.IsNullOrEmpty(token) || token.Length > 200) return Verdict.Malformed;
        var parts = token.Split('.');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out messageId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out profileId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out expires))
            return Verdict.Malformed;
        sig = parts[3];
        // never longer than the lifetime, whatever the clock said when it was signed
        if (expires > now.Add(MaxLifetime).AddSeconds(30).ToUnixTimeSeconds()) return Verdict.Malformed;
        return expires < now.ToUnixTimeSeconds() ? Verdict.Expired : Verdict.Ok;
    }

    /// <summary>True when <paramref name="sig"/> signs these claims for this username.</summary>
    public static bool SignatureOk(int messageId, int profileId, string username, long expires, string sig)
    {
        var expected = Sign(messageId, profileId, username, expires).Split('.')[3];
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(sig ?? ""));
    }

    /// <summary>A signed path for the current request's session user, or null (API key,
    /// background work, no user).</summary>
    public static string? PathFor(int messageId)
    {
        var c = CallerSlot.Value;
        if (c == null || messageId <= 0) return null;
        return $"/api/media/s/{Sign(messageId, c.ProfileId, c.Username, ExpiryFor(DateTimeOffset.UtcNow))}";
    }

    /// <summary>
    /// The <c>media</c> object of a message payload: <c>type</c>, the auth-gated <c>url</c>,
    /// <c>mime</c>, <c>name</c> (the original file name, else "{type}-{id}{ext}") and
    /// <c>size</c> (bytes; read from disk for rows older than the column), plus for a
    /// signed-in session <c>stream_url</c> (inline, Range), <c>download_url</c> (as an
    /// attachment) and — for audio browsers often cannot play (Ogg/Opus on Safari, AMR)
    /// when the server has ffmpeg — <c>alt_url</c> (an AAC/M4A rendering).
    /// </summary>
    public static Dictionary<string, object?> Payload(Message m, string channel)
    {
        var type = string.IsNullOrEmpty(m.MediaType) ? "document" : m.MediaType;
        var mime = m.MediaMime.Length > 0 ? m.MediaMime : MediaStore.MimeFor(m.MediaPath);
        var size = m.MediaSize;
        if (size <= 0 && StoreSlot.Value is { } store)
            size = MediaStore.SizeOf(store.Resolve(channel, m.MediaPath));
        var outv = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["url"] = $"/api/{(channel == Channels.WhatsApp ? "whatsapp" : "messaging")}/media/{m.Id}",
            ["mime"] = mime,
            ["name"] = DisplayName(m, type),
            ["size"] = size > 0 ? size : null,
        };
        if (PathFor(m.Id) is { } stream)
        {
            outv["stream_url"] = stream;
            outv["download_url"] = stream + "?download=1";
            if (AudioTranscoder.CanTranscode(mime) && AudioTranscoder.Available)
                outv["alt_url"] = stream + "?format=m4a";
        }
        return outv;
    }

    /// <summary>The name a person sees / saves: the stored original, else the stored file's
    /// own (email / Telegram keep it after a 10-hex prefix), else "{type}-{id}{ext}".</summary>
    public static string DisplayName(Message m, string? type = null)
    {
        if (m.MediaName.Length > 0) return m.MediaName;
        var file = Path.GetFileName(m.MediaPath ?? "");
        if (file.Length > 11 && file[10] == '-' && file[..10].All(Uri.IsHexDigit)) return file[11..];
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return $"{type ?? (m.MediaType.Length > 0 ? m.MediaType : "file")}-{m.Id}{ext}";
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
