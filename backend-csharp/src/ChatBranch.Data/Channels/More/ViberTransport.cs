using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Viber REST Bot API (<c>https://chatapi.viber.com/pa/*</c>, header
/// <c>X-Viber-Auth-Token</c>):
/// <list type="bullet">
///   <item>out — <c>send_message</c> (text; Viber takes pictures by public URL only);</item>
///   <item>in — <c>X-Viber-Content-Signature</c> = hex HMAC-SHA256(auth token, body);
///         <c>message</c> (text + picture / video / file media from Viber's CDN),
///         <c>subscribed</c> / <c>unsubscribed</c> (opt-in / opt-out),
///         <c>conversation_started</c> (its <c>context</c> — an invite code — is kept
///         and rides on the first message as <c>[ref:…]</c>), <c>delivered</c> /
///         <c>seen</c> / <c>failed</c> (ticks on our message);</item>
///   <item>"Register webhook" = <c>set_webhook</c>; health = <c>get_account_info</c>.</item>
/// </list>
/// Viber ids are base64 (they can hold <c>/</c> and <c>+</c>), so the thread key is
/// the URL-safe form (<see cref="KeyFor"/> / <see cref="IdFor"/>).
/// </summary>
public sealed class ViberTransport : IChannelTransport
{
    public const string ApiBase = "https://chatapi.viber.com/pa";
    public const string SignatureHeader = "X-Viber-Content-Signature";
    public static readonly string[] EventTypes =
        { "delivered", "seen", "failed", "subscribed", "unsubscribed", "conversation_started" };

    private readonly IHttpClientFactory _http;

    public ViberTransport(IHttpClientFactory http) => _http = http;

    public string Channel => ChannelIds.Viber;
    public int MaxText => 7000;
    public bool SupportsMedia => false;

    /// <summary>A Viber user id as a thread key (no <c>/</c> in a URL path).</summary>
    public static string KeyFor(string viberId) => viberId.Replace('/', '_').Replace('+', '-');

    public static string IdFor(string key) => key.Replace('_', '/').Replace('-', '+');

    private HttpRequestMessage Req(string method, ChannelAccount a, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{method}") { Content = ProviderHttp.JsonBody(body) };
        req.Headers.TryAddWithoutValidation("X-Viber-Auth-Token", a.AccessToken);
        return req;
    }

    /// <summary>The bot's sender name (≤28 chars): config <c>sender_name</c>, else the inbox name.</summary>
    public static string SenderName(ChannelAccount a)
    {
        var name = MoreChannels.Cfg(a, "sender_name");
        if (name.Length == 0) name = string.IsNullOrWhiteSpace(a.Name) ? "Support" : a.Name.Trim();
        return name.Length > 28 ? name[..28] : name;
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default) =>
        Result(await ProviderHttp.SendAsync(_http, Req("send_message", a, new
        {
            receiver = IdFor(to.Key),
            min_api_version = 1,
            sender = new { name = SenderName(a) },
            type = "text",
            text,
        }), ct, a.AccessToken));

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "Viber only accepts pictures and files by public URL — send a link instead"));

    /// <summary>Viber's status codes → what they mean for the account.</summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"Viber API unreachable: {r.Error}");
        if (!r.Is2xx)
            return TransportResult.Fail(r.Status >= 500 ? TransportFailure.Transient : TransportFailure.Recipient,
                $"Viber API HTTP {r.Status}", r.Status);
        var status = r.Long("status");
        if (status == 0 && r.Json.ValueKind == JsonValueKind.Object)
            return TransportResult.Success(r.Str("message_token"));
        var message = r.Str("status_message");
        var failure = status switch
        {
            2 or 8 or 15 => TransportFailure.Auth,          // invalid auth token / account not found / not authorized
            7 or 9 => TransportFailure.Banned,              // account blocked / suspended
            10 => TransportFailure.Transient,               // webhook not set
            12 => TransportFailure.RateLimit,
            23 or 24 => TransportFailure.Billing,           // free messages exceeded / no balance
            _ => TransportFailure.Recipient,                // not subscribed, no suitable device, bad data…
        };
        return TransportResult.Fail(failure, $"Viber {status}: {(message.Length > 0 ? message : "error")}", r.Status,
            status.ToString());
    }

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var r = await ProviderHttp.SendAsync(_http, Req("get_account_info", a, new { }), ct, a.AccessToken);
        var result = Result(r);
        if (!result.Ok)
            return new(result.Failure switch
            {
                TransportFailure.Auth => new ProbeVerdict(Health.Disconnected, result.Detail),
                TransportFailure.Banned => new ProbeVerdict(Health.Banned, result.Detail),
                _ => new ProbeVerdict(null, result.Detail, Transient: true),
            });
        var name = r.Str("name");
        var id = r.Str("id");
        if (r.Str("webhook").Length == 0)
            return new(new ProbeVerdict(Health.Degraded,
                "bot alive, but no webhook set — inbound messages won't arrive (Channels → Register webhook)"), name, id);
        return new(new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"), name, id);
    }

    public async Task<TransportResult> SetWebhookAsync(ChannelAccount a, string url, CancellationToken ct = default) =>
        Result(await ProviderHttp.SendAsync(_http, Req("set_webhook", a, new
        {
            url,
            event_types = EventTypes,
            send_name = true,
            send_photo = false,
        }), ct, a.AccessToken));

    /// <summary>Viber media live on its CDN at a public, expiring URL — fetched without our token.</summary>
    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(media.Ref, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<(byte[]?, string)>((null, "not an https media URL"));
        return ProviderHttp.DownloadAsync(_http, new HttpRequestMessage(HttpMethod.Get, uri), MessagingMedia.MaxBytes, ct);
    }

    /// <summary>X-Viber-Content-Signature = hex HMAC-SHA256(auth token, raw body), compared in constant time.</summary>
    public static bool VerifySignature(string authToken, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(authToken) || string.IsNullOrWhiteSpace(header)) return false;
        return ProviderHttp.SameText(Sign(authToken, body), header.Trim().ToLowerInvariant());
    }

    public static string Sign(string authToken, byte[] body) => ProviderHttp.Hmac256Hex(authToken, body);
}

/// <summary>One Viber callback.</summary>
public sealed record ViberEvent(string Event, string UserKey, string Name, string MessageToken, string Text,
    DateTime? Timestamp, string Context, InboundMediaRef? Media, string Description);

public static class ViberInbound
{
    public static ViberEvent? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var ev = ProviderHttp.Str(root, "event");
        if (ev.Length == 0) return null;
        var ts = ProviderHttp.Long(root, "timestamp") is var ms and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
        var token = ProviderHttp.Str(root, "message_token");
        string user, name;
        switch (ev)
        {
            case "message":
                user = ProviderHttp.Str(root, "sender", "id");
                name = ProviderHttp.Str(root, "sender", "name");
                break;
            case "subscribed":
            case "conversation_started":
                user = ProviderHttp.Str(root, "user", "id");
                name = ProviderHttp.Str(root, "user", "name");
                break;
            default:
                user = ProviderHttp.Str(root, "user_id");
                name = "";
                break;
        }
        var key = user.Length > 0 ? ViberTransport.KeyFor(user) : "";
        var text = "";
        InboundMediaRef? media = null;
        if (ev == "message")
        {
            var m = ProviderHttp.At(root, "message");
            var type = ProviderHttp.Str(m, "type");
            var body = ProviderHttp.Str(m, "text");
            var url = ProviderHttp.Str(m, "media");
            text = type switch
            {
                "text" => body,
                "picture" => body.Length > 0 ? body : "[image]",
                "video" => "[video]",
                "file" => $"[file] {ProviderHttp.Str(m, "file_name")}".Trim(),
                "sticker" => "[sticker]",
                "location" => "[location]",
                "contact" => $"[contact] {ProviderHttp.Str(m, "contact", "name")} {ProviderHttp.Str(m, "contact", "phone_number")}".Trim(),
                "url" => body.Length > 0 ? body : url,
                _ => body.Length > 0 ? body : "[message]",
            };
            if (url.Length > 0 && type is "picture" or "video" or "file")
            {
                var fileName = type switch
                {
                    "picture" => "image.jpg",
                    "video" => "video.mp4",
                    _ => MessagingMedia.SafeName(ProviderHttp.Str(m, "file_name"), "file.bin"),
                };
                media = new InboundMediaRef(url, type == "picture" ? "image" : type == "video" ? "video"
                    : MessagingMedia.TypeFor(fileName), fileName);
            }
            if (text.Length == 0) text = "[message]";
        }
        return new ViberEvent(ev, key, name, token, text, ts, ProviderHttp.Str(root, "context"), media,
            ProviderHttp.Str(root, "desc"));
    }
}
