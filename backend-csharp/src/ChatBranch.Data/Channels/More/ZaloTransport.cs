using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Zalo Official Account (OpenAPI v2/v3, header <c>access_token</c>):
/// <list type="bullet">
///   <item>out — consultation messages <c>POST /v3.0/oa/message/cs</c> (text; images
///         uploaded with <c>/v2.0/oa/upload/image</c> and sent as a media template)
///         to users who interacted within 7 days (<see cref="ChannelWindow"/>; Zalo's
///         own -230 maps to the same refusal);</item>
///   <item>token — an expired / invalid access token is refreshed ONCE with the
///         refresh token (<c>oauth.zaloapp.com/v4/oa/access_token</c>, header
///         <c>secret_key</c> = the app secret); Zalo rotates the refresh token, so the
///         new pair is written onto the (tracked) account and the caller saves;</item>
///   <item>in — <c>X-ZEvent-Signature: mac=sha256(appId + body + timestamp + OA secret key)</c>;
///         user_send_text / image / file / sticker / link / location…, follow /
///         unfollow (opt-in / opt-out); <c>oa_send_*</c> echoes ignored;</item>
///   <item>health — <c>GET /v2.0/oa/getoa</c>.</item>
/// </list>
/// </summary>
public sealed class ZaloTransport : IChannelTransport
{
    public const string ApiBase = "https://openapi.zalo.me";
    public const string OAuthBase = "https://oauth.zaloapp.com";
    public const string SignatureHeader = "X-ZEvent-Signature";

    private readonly IHttpClientFactory _http;

    public ZaloTransport(IHttpClientFactory http) => _http = http;

    public string Channel => ChannelIds.Zalo;
    public int MaxText => 2000;
    public bool SupportsMedia => true;

    private static HttpRequestMessage Req(HttpMethod method, string url, ChannelAccount a, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body };
        req.Headers.TryAddWithoutValidation("access_token", a.AccessToken);
        return req;
    }

    private static bool TokenProblem(HttpReply r) => r.Long("error") is -216 or -124 or -155;

    /// <summary>Run <paramref name="call"/>; on an expired token refresh once (when the refresh pair is set) and retry.</summary>
    private async Task<HttpReply> WithRefresh(ChannelAccount a, Func<HttpRequestMessage> call, CancellationToken ct)
    {
        var r = await ProviderHttp.SendAsync(_http, call(), ct, a.AccessToken);
        if (!TokenProblem(r) || !CanRefresh(a)) return r;
        if (!(await RefreshAsync(a, ct)).Ok) return r;
        return await ProviderHttp.SendAsync(_http, call(), ct, a.AccessToken);
    }

    public static bool CanRefresh(ChannelAccount a) =>
        MoreChannels.Cfg(a, "refresh_token").Length > 0 && MoreChannels.Cfg(a, "app_id").Length > 0
        && !string.IsNullOrEmpty(a.AppSecret);

    /// <summary>Swap the refresh token for a new access + refresh pair, written onto <paramref name="a"/> (caller saves).</summary>
    public async Task<TransportResult> RefreshAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var refresh = MoreChannels.Cfg(a, "refresh_token");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{OAuthBase}/v4/oa/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = refresh,
                ["app_id"] = MoreChannels.Cfg(a, "app_id"),
                ["grant_type"] = "refresh_token",
            }),
        };
        req.Headers.TryAddWithoutValidation("secret_key", a.AppSecret);
        var r = await ProviderHttp.SendAsync(_http, req, ct, a.AppSecret, refresh);
        var access = r.Str("access_token");
        if (!r.Is2xx || access.Length == 0)
        {
            var why = r.Str("error_description") is { Length: > 0 } d ? d : r.Status == 0 ? r.Error : $"HTTP {r.Status}";
            return TransportResult.Fail(r.Status == 0 ? TransportFailure.Transient : TransportFailure.Auth,
                $"Zalo token refresh failed: {ProviderHttp.Scrub(why, a.AppSecret, refresh)}");
        }
        a.AccessToken = access;
        var cfg = new Dictionary<string, object?>(a.Config ?? new());
        if (r.Str("refresh_token") is { Length: > 0 } next) cfg["refresh_token"] = next;
        cfg["token_refreshed_at"] = DateTime.UtcNow.ToString("o");
        a.Config = cfg;
        return TransportResult.Success("");
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default) =>
        Result(await WithRefresh(a, () => Req(HttpMethod.Post, $"{ApiBase}/v3.0/oa/message/cs", a,
            ProviderHttp.JsonBody(new { recipient = new { user_id = to.Key }, message = new { text } })), ct));

    public async Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media,
        string? caption, CancellationToken ct = default)
    {
        if (media.MediaType != "image")
            return TransportResult.Fail(TransportFailure.Unsupported, "Zalo messages carry text and images — send other files as a link");
        if (!File.Exists(media.AbsolutePath))
            return TransportResult.Fail(TransportFailure.Unsupported, "the file is missing");
        var bytes = await File.ReadAllBytesAsync(media.AbsolutePath, ct);
        if (bytes.Length > 1024 * 1024)
            return TransportResult.Fail(TransportFailure.Unsupported, "Zalo images must be 1 MB or smaller");
        var up = await WithRefresh(a, () =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(MessagingMedia.ContentTypeFor(media.FileName));
            form.Add(file, "file", media.FileName);
            return Req(HttpMethod.Post, $"{ApiBase}/v2.0/oa/upload/image", a, form);
        }, ct);
        var attachment = up.Str("data", "attachment_id");
        if (attachment.Length == 0) return Result(up);
        var message = new Dictionary<string, object?>
        {
            ["attachment"] = new
            {
                type = "template",
                payload = new
                {
                    template_type = "media",
                    elements = new[] { new { media_type = "image", attachment_id = attachment } },
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(caption)) message["text"] = caption!.Trim();
        return Result(await WithRefresh(a, () => Req(HttpMethod.Post, $"{ApiBase}/v3.0/oa/message/cs", a,
            ProviderHttp.JsonBody(new { recipient = new { user_id = to.Key }, message })), ct));
    }

    /// <summary>Zalo's <c>{"error":…,"message":…}</c> → what it means for the account.</summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"Zalo API unreachable: {r.Error}");
        if (!r.Is2xx)
            return TransportResult.Fail(r.Status >= 500 ? TransportFailure.Transient : TransportFailure.Recipient,
                $"Zalo API HTTP {r.Status}", r.Status);
        var code = r.Long("error");
        if (code == 0) return TransportResult.Success(r.Str("data", "message_id"));
        var msg = r.Str("message");
        var failure = code switch
        {
            -216 or -124 or -155 or -204 => TransportFailure.Auth,   // token invalid / expired, OA not authorised
            -230 => TransportFailure.Window,                         // no interaction within 7 days
            -32 => TransportFailure.RateLimit,
            -224 or -227 => TransportFailure.Billing,                // quota exhausted
            _ => TransportFailure.Recipient,                         // -213 not following, -201 bad parameter…
        };
        var why = code == -230
            ? "Zalo's 7-day interaction window has closed — nothing can be sent until the user writes again"
            : msg.Length > 0 ? msg : "error";
        return TransportResult.Fail(failure, $"Zalo {code}: {why}", r.Status, code.ToString());
    }

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var r = await WithRefresh(a, () => Req(HttpMethod.Get, $"{ApiBase}/v2.0/oa/getoa", a), ct);
        var res = Result(r);
        if (!res.Ok)
            return new(res.Failure switch
            {
                TransportFailure.Auth => new ProbeVerdict(Health.Disconnected,
                    res.Detail + (CanRefresh(a) ? "" : " — add the refresh token, app id and app secret so it renews itself")),
                TransportFailure.Transient => new ProbeVerdict(null, res.Detail, Transient: true),
                _ => new ProbeVerdict(Health.Degraded, res.Detail),
            });
        var name = r.Str("data", "name");
        return new(new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"),
            name, r.Str("data", "oa_id"));
    }

    public async Task<string> UserNameAsync(ChannelAccount a, string userId, CancellationToken ct = default)
    {
        var data = Uri.EscapeDataString(JsonSerializer.Serialize(new { user_id = userId }));
        var r = await ProviderHttp.SendAsync(_http,
            Req(HttpMethod.Get, $"{ApiBase}/v3.0/oa/user/detail?data={data}", a), ct, a.AccessToken);
        return r.Long("error") == 0 ? r.Str("data", "display_name") : "";
    }

    /// <summary>Zalo attachments are public CDN URLs — fetched without our token.</summary>
    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(media.Ref, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<(byte[]?, string)>((null, "not an https media URL"));
        return ProviderHttp.DownloadAsync(_http, new HttpRequestMessage(HttpMethod.Get, uri), MessagingMedia.MaxBytes, ct);
    }

    // ---- webhook -------------------------------------------------------------------

    /// <summary>mac = sha256(appId + raw body + timestamp + OA secret key), hex.</summary>
    public static string Mac(string appId, string body, string timestamp, string oaSecretKey) =>
        ProviderHttp.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(appId + body + timestamp + oaSecretKey)));

    public static bool VerifySignature(string appId, string oaSecretKey, string body, string timestamp, string? header)
    {
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(oaSecretKey) || string.IsNullOrWhiteSpace(header))
            return false;
        var given = header.Trim();
        if (given.StartsWith("mac=", StringComparison.OrdinalIgnoreCase)) given = given[4..];
        return ProviderHttp.SameText(Mac(appId, body, timestamp, oaSecretKey), given.Trim().ToLowerInvariant());
    }
}

/// <summary>One Zalo webhook event.</summary>
/// <param name="Kind">message | follow | unfollow | ignore</param>
public sealed record ZaloEvent(string Kind, string UserId, string Text, string MessageId, DateTime? Timestamp,
    InboundMediaRef? Media, string AppId, string OaId);

public static class ZaloInbound
{
    public static ZaloEvent? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var ev = ProviderHttp.Str(root, "event_name");
        var ts = ProviderHttp.Long(root, "timestamp") is var ms and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
        var appId = ProviderHttp.Str(root, "app_id");
        if (ev is "follow" or "unfollow")
            return new ZaloEvent(ev, ProviderHttp.Str(root, "follower", "id"), "", "", ts, null, appId,
                ProviderHttp.Str(root, "oa_id"));
        if (!ev.StartsWith("user_send_", StringComparison.Ordinal))
            return new ZaloEvent("ignore", "", "", "", ts, null, appId, "");
        var user = ProviderHttp.Str(root, "sender", "id");
        var m = ProviderHttp.At(root, "message");
        var text = ProviderHttp.Str(m, "text");
        var att = ProviderHttp.At(m, "attachments", "0");
        var attType = ProviderHttp.Str(att, "type");
        var url = ProviderHttp.Str(att, "payload", "url");
        InboundMediaRef? media = null;
        if (url.Length > 0 && attType is "image" or "gif" or "audio" or "video" or "file")
        {
            var name = attType switch
            {
                "image" => "image.jpg",
                "gif" => "image.gif",
                "audio" => "audio.m4a",
                "video" => "video.mp4",
                _ => MessagingMedia.SafeName(ProviderHttp.Str(att, "payload", "name"), "file.bin"),
            };
            media = new InboundMediaRef(url, attType is "gif" ? "image" : attType == "file" ? MessagingMedia.TypeFor(name)
                : attType, name);
        }
        if (text.Length == 0)
            text = attType switch
            {
                "" => "[message]",
                "link" => $"[link] {url}".Trim(),
                "location" => "[location]",
                "gif" => "[image]",
                _ => $"[{attType}]",
            };
        return new ZaloEvent("message", user, text, ProviderHttp.Str(m, "msg_id"), ts, media, appId,
            ProviderHttp.Str(root, "recipient", "id"));
    }
}
