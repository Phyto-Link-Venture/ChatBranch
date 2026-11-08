using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ChatBranch.Data.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Channels;

/// <summary>A file the server fetched for a send: the bytes, and what to call them.</summary>
/// <param name="Kind">image | video | audio | document — the inbox's own vocabulary.</param>
public record RemoteFile(byte[] Content, string FileName, string Mime, string Kind);

/// <summary>Why a link could not be turned into a file. Each maps to words a person can act on.</summary>
public enum RemoteMediaError
{
    None,
    /// <summary>Not an https URL (http, a data: URI, a local path).</summary>
    NotHttps,
    /// <summary>A private or local address — refused before any request is made.</summary>
    Blocked,
    NotFound,
    /// <summary>The link needs permission we do not have (403, or a private Drive file).</summary>
    NoAccess,
    TooLarge,
    Timeout,
    /// <summary>Reached, but not a file we can send (an HTML page, an empty body).</summary>
    Unreadable,
}

public record RemoteMediaResult(RemoteFile? File, RemoteMediaError Error = RemoteMediaError.None, string Detail = "")
{
    public bool Ok => File != null;

    public static RemoteMediaResult Fail(RemoteMediaError error, string detail) => new(null, error, detail);
}

/// <summary>Turns a link into bytes the normal send path can carry.</summary>
public interface IRemoteMediaFetcher
{
    /// <summary>
    /// Fetch <paramref name="url"/>, at most <paramref name="maxBytes"/>.
    /// <paramref name="entityId"/> is the conversation's outlet, used to resolve a Google
    /// Drive connection for a private Drive link. Never throws.
    /// </summary>
    Task<RemoteMediaResult> FetchAsync(string url, long maxBytes, int? entityId, CancellationToken ct = default);
}

/// <summary>
/// The pure decisions behind fetching a link: is this address allowed, and is this a
/// Google Drive file? Separated from the HTTP so they can be tested without a network
/// (<c>RemoteMediaTests</c>) — these are the rules that keep <c>media_url</c> from
/// becoming a way to read the server's own network.
/// </summary>
public static partial class RemoteMediaRules
{
    /// <summary>How many hops a link may take. Each one is checked again.</summary>
    public const int MaxRedirects = 3;

    /// <summary>How long one fetch may take, start to finish.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Is this an address the server may fetch? Only https, and never one that points
    /// back inside: loopback, link-local (including the cloud metadata address
    /// 169.254.169.254), private ranges, unique-local IPv6, multicast, or a bare
    /// hostname with no dot (an internal service name). The host's resolved addresses
    /// are checked too, so <c>https://evil.test</c> pointing at 10.0.0.1 is refused.
    /// </summary>
    public static RemoteMediaError? Refuse(Uri url, IReadOnlyCollection<IPAddress> resolved)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return RemoteMediaError.NotHttps;
        var host = url.Host;
        if (host.Length == 0) return RemoteMediaError.Blocked;
        // "localhost", "intranet", "api" — a name with no dot is never a public site
        if (!host.Contains('.') && !IPAddress.TryParse(host, out _)) return RemoteMediaError.Blocked;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return RemoteMediaError.Blocked;
        if (resolved.Count == 0) return RemoteMediaError.NotFound;
        return resolved.Any(IsPrivate) ? RemoteMediaError.Blocked : null;
    }

    /// <summary>An address that is not on the public internet.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            // unique-local fc00::/7
            var v6 = ip.GetAddressBytes();
            if ((v6[0] & 0xfe) == 0xfc) return true;
            // an IPv4 address wearing an IPv6 coat (::ffff:10.0.0.1)
            if (ip.IsIPv4MappedToIPv6) return IsPrivate(ip.MapToIPv4());
            return false;
        }
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                   // 0.0.0.0/8
            10 => true,                                  // private
            127 => true,                                 // loopback
            169 when b[1] == 254 => true,                // link-local + cloud metadata
            172 when b[1] >= 16 && b[1] <= 31 => true,   // private
            192 when b[1] == 168 => true,                // private
            192 when b[1] == 0 && b[2] == 0 => true,     // IETF protocol assignments
            100 when b[1] >= 64 && b[1] <= 127 => true,  // carrier NAT
            >= 224 => true,                              // multicast + reserved
            _ => false,
        };
    }

    /// <summary>
    /// The Google Drive file id a share link names, or "" when it is not a Drive link.
    /// Covers the three shapes people paste: <c>/file/d/&lt;id&gt;/view</c>,
    /// <c>/open?id=</c> and <c>/uc?id=</c>.
    /// </summary>
    public static string DriveFileId(Uri url)
    {
        if (!url.Host.EndsWith("drive.google.com", StringComparison.OrdinalIgnoreCase)
            && !url.Host.EndsWith("docs.google.com", StringComparison.OrdinalIgnoreCase)) return "";
        if (DrivePath().Match(url.AbsolutePath) is { Success: true } m) return m.Groups[1].Value;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var id = (query["id"] ?? "").Trim();
        return IdLike().IsMatch(id) ? id : "";
    }

    /// <summary>A Drive share link as a direct download, for a file that IS public.</summary>
    public static string DriveDirectUrl(string fileId) =>
        $"https://drive.google.com/uc?export=download&id={fileId}";

    [GeneratedRegex(@"/file/d/([A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,}$")]
    private static partial Regex IdLike();

    /// <summary>
    /// The file name to store: the one the request gave, else Content-Disposition's,
    /// else the URL's last segment, else one built from the type. Always ends with an
    /// extension, because that is what decides the kind on every channel.
    /// </summary>
    public static string FileName(string? given, string? contentDisposition, Uri url, string mime)
    {
        var name = (given ?? "").Trim();
        if (name.Length == 0) name = FromDisposition(contentDisposition);
        if (name.Length == 0)
        {
            var last = url.Segments.Length > 0 ? url.Segments[^1].Trim('/') : "";
            name = Uri.UnescapeDataString(last);
        }
        name = MessagingMedia.SafeName(name, "");
        if (name.Length == 0 || !Path.HasExtension(name))
        {
            var ext = MediaStore.ExtFor(mime, name);
            var stem = name.Length > 0 ? Path.GetFileNameWithoutExtension(name) : "file";
            name = stem + ext;
        }
        return name;
    }

    private static string FromDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";
        try
        {
            var parsed = ContentDispositionHeaderValue.Parse(header);
            var name = (parsed.FileNameStar ?? parsed.FileName ?? "").Trim('"', ' ');
            return name;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Words for a refusal that say what to do about it.</summary>
    public static string Explain(RemoteMediaError error, string url, long maxBytes) => error switch
    {
        RemoteMediaError.NotHttps => "media_url must be an https:// link.",
        RemoteMediaError.Blocked =>
            "That link points to a private or local address, so it was not fetched. Use a public https link.",
        RemoteMediaError.NotFound => $"Nothing was found at that link ({Host(url)}).",
        RemoteMediaError.NoAccess =>
            "That link needs permission. Share the file with anyone who has the link, or connect the Google "
            + "account that owns it (Settings → Integrations → Google Drive).",
        RemoteMediaError.TooLarge => $"That file is larger than this channel allows ({maxBytes / (1024 * 1024)} MB).",
        RemoteMediaError.Timeout => "That link took longer than 30 seconds to answer.",
        RemoteMediaError.Unreadable =>
            "That link did not return a file — it looks like a web page. Use a direct link to the file itself.",
        _ => "That link could not be fetched.",
    };

    private static string Host(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }
}

/// <summary>
/// Fetches a link so the AI never has to spell a file out. Sending a 2 MB photo as
/// base64 inside a tool call means the model writing ~2.7 million characters: on
/// 8 Nov 2026 a photo had to be shrunk to 15 KB to be sendable at all, and the send
/// still took about thirteen minutes. A link is a few hundred characters, whatever the
/// file weighs, and the file arrives at full quality.
///
/// The rules (<see cref="RemoteMediaRules"/>): https only, at most
/// <see cref="RemoteMediaRules.MaxRedirects"/> hops with every hop re-checked, a 30
/// second budget, the channel's own size limit, and no private or local address — the
/// last one is what keeps this from becoming a way to read the server's own network.
///
/// Google Drive share links are understood: a Drive connection for the conversation's
/// outlet downloads the file through the API (so a PRIVATE file needs nothing made
/// public), and without one the public share link is rewritten to a direct download.
/// </summary>
public class HttpRemoteMediaFetcher : IRemoteMediaFetcher
{
    /// <summary>The named client: redirects are followed by hand, so each hop is checked.</summary>
    public const string ClientName = "media-fetch";

    private readonly IHttpClientFactory _http;
    private readonly IServiceProvider _services;

    public HttpRemoteMediaFetcher(IHttpClientFactory http, IServiceProvider services)
    {
        _http = http;
        _services = services;
    }

    public async Task<RemoteMediaResult> FetchAsync(string url, long maxBytes, int? entityId,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var start))
            return RemoteMediaResult.Fail(RemoteMediaError.NotHttps,
                RemoteMediaRules.Explain(RemoteMediaError.NotHttps, url ?? "", maxBytes));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RemoteMediaRules.Timeout);
        try
        {
            // A Drive link is not an ordinary URL: the public form answers with an HTML
            // confirm page for anything but the smallest files, and a private one answers
            // a sign-in page. Go through the connected account when there is one.
            if (RemoteMediaRules.DriveFileId(start) is { Length: > 0 } fileId)
            {
                var viaDrive = await DriveAsync(fileId, maxBytes, entityId, budget.Token);
                if (viaDrive != null) return viaDrive;
                start = new Uri(RemoteMediaRules.DriveDirectUrl(fileId));
            }
            return await FollowAsync(start, maxBytes, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RemoteMediaResult.Fail(RemoteMediaError.Timeout,
                RemoteMediaRules.Explain(RemoteMediaError.Timeout, url ?? "", maxBytes));
        }
        catch (OperationCanceledException)
        {
            throw;   // the caller gave up, not us
        }
        catch (Exception e)
        {
            return RemoteMediaResult.Fail(RemoteMediaError.NotFound,
                $"That link could not be fetched: {e.Message}");
        }
    }

    /// <summary>Walk the redirects by hand, checking the address at every hop.</summary>
    private async Task<RemoteMediaResult> FollowAsync(Uri url, long maxBytes, CancellationToken ct)
    {
        var client = _http.CreateClient(ClientName);
        for (var hop = 0; ; hop++)
        {
            if (await RefuseAsync(url, ct) is { } refused)
                return RemoteMediaResult.Fail(refused, RemoteMediaRules.Explain(refused, url.ToString(), maxBytes));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Drive's public download answers HTML to a bare client; asking for anything
            // keeps it honest, and some CDNs need a User-Agent at all
            req.Headers.TryAddWithoutValidation("User-Agent", "ChatBranch");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(res.StatusCode) && res.Headers.Location != null)
            {
                if (hop >= RemoteMediaRules.MaxRedirects)
                    return RemoteMediaResult.Fail(RemoteMediaError.NotFound,
                        "That link redirected too many times.");
                url = res.Headers.Location.IsAbsoluteUri
                    ? res.Headers.Location
                    : new Uri(url, res.Headers.Location);
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                var error = res.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.Gone => RemoteMediaError.NotFound,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RemoteMediaError.NoAccess,
                    HttpStatusCode.RequestEntityTooLarge => RemoteMediaError.TooLarge,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => RemoteMediaError.Timeout,
                    _ => RemoteMediaError.NotFound,
                };
                return RemoteMediaResult.Fail(error,
                    $"{RemoteMediaRules.Explain(error, url.ToString(), maxBytes)} (HTTP {(int)res.StatusCode})");
            }

            // refuse on the declared size before reading a byte, when it is declared
            if (res.Content.Headers.ContentLength is long declared && declared > maxBytes)
                return RemoteMediaResult.Fail(RemoteMediaError.TooLarge,
                    RemoteMediaRules.Explain(RemoteMediaError.TooLarge, url.ToString(), maxBytes));

            var mime = (res.Content.Headers.ContentType?.MediaType ?? "").Trim().ToLowerInvariant();
            var bytes = await ReadCappedAsync(res, maxBytes, ct);
            if (bytes == null)
                return RemoteMediaResult.Fail(RemoteMediaError.TooLarge,
                    RemoteMediaRules.Explain(RemoteMediaError.TooLarge, url.ToString(), maxBytes));
            if (bytes.Length == 0)
                return RemoteMediaResult.Fail(RemoteMediaError.Unreadable,
                    "That link returned an empty file.");
            // an HTML page is the usual answer to a share link that is not public
            if (mime.StartsWith("text/html"))
                return RemoteMediaResult.Fail(RemoteMediaError.NoAccess,
                    RemoteMediaRules.Explain(RemoteMediaError.NoAccess, url.ToString(), maxBytes));

            var name = RemoteMediaRules.FileName(null, res.Content.Headers.ContentDisposition?.ToString(), url, mime);
            return Built(bytes, name, mime);
        }
    }

    /// <summary>
    /// Download a Drive file through the outlet's connected Google account. Returns null
    /// when Drive is not connected at all — the caller then tries the public link, so a
    /// genuinely public file still works on a server with no Google connection.
    /// </summary>
    private async Task<RemoteMediaResult?> DriveAsync(string fileId, long maxBytes, int? entityId,
        CancellationToken ct)
    {
        var actions = _services.GetService<IntegrationActions>();
        if (actions == null) return null;
        var drive = await actions.DownloadDriveFileAsync(fileId, maxBytes, entityId, ct);
        if (drive.NotConnected) return null;
        if (!drive.Ok)
            return RemoteMediaResult.Fail(drive.Error,
                $"{RemoteMediaRules.Explain(drive.Error, "drive.google.com", maxBytes)} {drive.Detail}".Trim());
        var name = RemoteMediaRules.FileName(drive.FileName, null,
            new Uri($"https://drive.google.com/file/d/{fileId}"), drive.Mime);
        return Built(drive.Content!, name, drive.Mime);
    }

    private static RemoteMediaResult Built(byte[] bytes, string name, string mime)
    {
        // Content-Type first, the file name second — a server that says
        // application/octet-stream for a .jpg should not make it a document
        var effective = mime.Length > 0 && mime != "application/octet-stream"
            ? mime
            : MessagingMedia.ContentTypeFor(name);
        var kind = mime.Length > 0 && mime != "application/octet-stream"
            ? MediaStore.TypeForMime(mime)
            : MessagingMedia.TypeFor(name);
        return new RemoteMediaResult(new RemoteFile(bytes, name, effective, kind));
    }

    /// <summary>Read at most <paramref name="maxBytes"/>; null when the body is bigger.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage res, long maxBytes, CancellationToken ct)
    {
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The address check, including DNS. A name that resolves to a private address is
    /// refused here; the host is re-resolved by the socket when the request goes out, so
    /// a name that changes its answer in between could still slip past — the honest fix
    /// is a pinned-IP connection, which is why the server never puts a fetched file
    /// anywhere but the conversation it was asked for.
    /// </summary>
    private static async Task<RemoteMediaError?> RefuseAsync(Uri url, CancellationToken ct)
    {
        IReadOnlyCollection<IPAddress> resolved;
        if (IPAddress.TryParse(url.Host, out var literal))
            resolved = new[] { literal };
        else
        {
            try { resolved = await Dns.GetHostAddressesAsync(url.Host, ct); }
            catch (OperationCanceledException) { throw; }
            catch { resolved = Array.Empty<IPAddress>(); }
        }
        return RemoteMediaRules.Refuse(url, resolved);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
