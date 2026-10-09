using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// WeChat Official Account (公众号) through the official API:
/// <list type="bullet">
///   <item>access_token — <c>GET /cgi-bin/token</c> (AppID + AppSecret), cached for
///         ~7000 s per app and refetched once when WeChat says it expired;</item>
///   <item>out — Customer Service messages <c>POST /cgi-bin/message/custom/send</c>
///         (text; images uploaded as temporary media then sent by media_id), only
///         within 48 h of the follower's last interaction (<see cref="ChannelWindow"/>;
///         WeChat's own 45015 is mapped to the same refusal);</item>
///   <item>in — the server URL: <c>GET</c> = URL verification (sha1 of the sorted
///         token, timestamp, nonce; echo <c>echostr</c>), <c>POST</c> = XML in plaintext
///         or AES safe mode (<see cref="WeChatCrypto"/>); text, image, voice
///         (WeChat's recognition when on), video, location, link, and the subscribe /
///         SCAN / unsubscribe events — a parametric QR's scene (<c>qrscene_X</c> /
///         <c>X</c>) rides on the message as <c>[ref:X]</c>;</item>
///   <item>health — the token fetch (a wrong secret or an IP missing from the
///         whitelist disconnects).</item>
/// </list>
/// </summary>
public sealed class WeChatTransport : IChannelTransport
{
    public const string ApiBase = "https://api.weixin.qq.com/cgi-bin";
    /// <summary>WeChat hands out 7200 s tokens; refresh comfortably before that.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(7000);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> _tokens = new();

    public WeChatTransport(IHttpClientFactory http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    public string Channel => ChannelIds.WeChat;
    /// <summary>Kept well under WeChat's per-message byte limit for CJK text (3 bytes a character).</summary>
    public int MaxText => 600;
    public bool SupportsMedia => true;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private static string AppId(ChannelAccount a) => MoreChannels.Cfg(a, "app_id");
    private static string CacheKey(ChannelAccount a) =>
        $"{AppId(a)}:{ProviderHttp.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(a.AppSecret ?? "")))[..16]}";

    // ---- access token --------------------------------------------------------------

    /// <summary>A cached or freshly fetched access token; a failure says why (never the secret).</summary>
    public async Task<(string? Token, TransportResult Error)> TokenAsync(ChannelAccount a, bool refresh = false,
        CancellationToken ct = default)
    {
        var key = CacheKey(a);
        if (!refresh && _tokens.TryGetValue(key, out var cached) && cached.Expires > Now)
            return (cached.Token, TransportResult.Success(""));
        var url = $"{ApiBase}/token?grant_type=client_credential&appid={Uri.EscapeDataString(AppId(a))}" +
                  $"&secret={Uri.EscapeDataString(a.AppSecret ?? "")}";
        var r = await ProviderHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Get, url), ct, a.AppSecret);
        var token = r.Str("access_token");
        if (r.Is2xx && token.Length > 0)
        {
            var life = r.Long("expires_in") is var s and > 0
                ? TimeSpan.FromSeconds(Math.Clamp(s - 200, 60, TokenLifetime.TotalSeconds)) : TokenLifetime;
            _tokens[key] = (token, Now + life);
            return (token, TransportResult.Success(""));
        }
        _tokens.TryRemove(key, out _);
        return (null, Result(r, "token"));
    }

    // ---- out -----------------------------------------------------------------------

    public Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default) =>
        CustomSendAsync(a, new { touser = to.Key, msgtype = "text", text = new { content = text } }, ct);

    private async Task<TransportResult> CustomSendAsync(ChannelAccount a, object payload, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (token, err) = await TokenAsync(a, refresh: attempt > 0, ct);
            if (token == null) return err;
            var r = await ProviderHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/message/custom/send?access_token={Uri.EscapeDataString(token)}")
            {
                Content = ProviderHttp.JsonBody(payload),
            }, ct, token, a.AppSecret);
            if (attempt == 0 && TokenExpired(r)) continue; // refetch once
            var res = Result(r, "send");
            // WeChat returns no message id for customer-service messages
            return res.Ok ? TransportResult.Success("") : res;
        }
        return TransportResult.Fail(TransportFailure.Auth, "WeChat keeps refusing the access token");
    }

    public async Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media,
        string? caption, CancellationToken ct = default)
    {
        if (media.MediaType != "image")
            return TransportResult.Fail(TransportFailure.Unsupported,
                "WeChat customer-service messages carry text and images — send other files as a link");
        if (!File.Exists(media.AbsolutePath))
            return TransportResult.Fail(TransportFailure.Unsupported, "the file is missing");
        var bytes = await File.ReadAllBytesAsync(media.AbsolutePath, ct);
        if (bytes.Length > 10 * 1024 * 1024)
            return TransportResult.Fail(TransportFailure.Unsupported, "WeChat images must be 10 MB or smaller");
        string? mediaId = null;
        for (var attempt = 0; attempt < 2 && mediaId == null; attempt++)
        {
            var (token, err) = await TokenAsync(a, refresh: attempt > 0, ct);
            if (token == null) return err;
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(MessagingMedia.ContentTypeFor(media.FileName));
            form.Add(file, "media", media.FileName);
            var up = await ProviderHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/media/upload?access_token={Uri.EscapeDataString(token)}&type=image") { Content = form },
                ct, token, a.AppSecret);
            if (attempt == 0 && TokenExpired(up)) continue;
            if (up.Str("media_id") is { Length: > 0 } id) mediaId = id;
            else return Result(up, "upload");
        }
        if (mediaId == null) return TransportResult.Fail(TransportFailure.Auth, "WeChat keeps refusing the access token");
        var sent = await CustomSendAsync(a, new { touser = to.Key, msgtype = "image", image = new { media_id = mediaId } }, ct);
        if (sent.Ok && !string.IsNullOrWhiteSpace(caption))
            await SendTextAsync(a, to, caption!.Trim(), ct);
        return sent;
    }

    private static bool TokenExpired(HttpReply r) => r.Long("errcode") is 40001 or 40014 or 42001;

    /// <summary>WeChat's <c>{"errcode":…,"errmsg":…}</c> → what it means for the account.</summary>
    public static TransportResult Result(HttpReply r, string what)
    {
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"WeChat API unreachable: {r.Error}");
        if (!r.Is2xx)
            return TransportResult.Fail(r.Status >= 500 ? TransportFailure.Transient : TransportFailure.Recipient,
                $"WeChat API HTTP {r.Status}", r.Status);
        var code = r.Long("errcode");
        if (code == 0) return TransportResult.Success("");
        var msg = r.Str("errmsg");
        var why = code switch
        {
            40164 => "this server's IP is not on the account's IP whitelist (WeChat Official Account → Settings → Basic configuration)",
            45015 => "WeChat's 48-hour window has closed (or the user unfollowed) — nothing can be sent until they write again",
            45047 => "WeChat's reply limit for this conversation was reached — wait for the follower to write again",
            48001 => "the account isn't authorised for this API (customer-service messages need a verified Service Account)",
            _ => msg.Length > 0 ? msg : "error",
        };
        var failure = code switch
        {
            40001 or 40014 or 42001 or 40013 or 40125 or 41002 or 41004 or 40164 or 48001 or 50001 or 50002
                => TransportFailure.Auth,
            45015 or 45047 => TransportFailure.Window,
            45009 or 45011 => TransportFailure.RateLimit,
            -1 => TransportFailure.Transient,
            _ => TransportFailure.Recipient,
        };
        return TransportResult.Fail(failure, $"WeChat {code} ({what}): {why}", r.Status, code.ToString());
    }

    // ---- account -------------------------------------------------------------------

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var (token, err) = await TokenAsync(a, refresh: true, ct);
        if (token != null)
            return new(new ProbeVerdict(Health.Healthy, $"Connected — access token issued for {AppId(a)}"), AppId(a));
        return new(err.Failure switch
        {
            TransportFailure.Auth => new ProbeVerdict(Health.Disconnected, err.Detail),
            TransportFailure.RateLimit => new ProbeVerdict(Health.Degraded, err.Detail),
            _ => new ProbeVerdict(null, err.Detail, Transient: true),
        });
    }

    /// <summary>Temporary media (images, voice, video) by media_id, valid for 3 days.</summary>
    public async Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default)
    {
        var (token, err) = await TokenAsync(a, ct: ct);
        if (token == null) return (null, err.Detail);
        return await ProviderHttp.DownloadAsync(_http, new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}/media/get?access_token={Uri.EscapeDataString(token)}&media_id={Uri.EscapeDataString(media.Ref)}"),
            MessagingMedia.MaxBytes, ct, token, a.AppSecret);
    }
}

/// <summary>
/// WeChat's server-URL cryptography: the URL signature (plaintext mode) and the
/// AES "safe mode" envelope (msg_signature + AES-256-CBC, IV = the key's first 16
/// bytes, PKCS#7 padded to 32-byte blocks, body = 16 random bytes + 4-byte
/// big-endian length + XML + AppID).
/// </summary>
public static class WeChatCrypto
{
    /// <summary>sha1 of the sorted parts, concatenated, lowercase hex.</summary>
    public static string Signature(params string[] parts)
    {
        var sorted = parts.Select(p => p ?? "").OrderBy(p => p, StringComparer.Ordinal);
        return ProviderHttp.Hex(SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(sorted))));
    }

    public static bool Verify(string expected, params string[] parts) =>
        !string.IsNullOrEmpty(expected) && ProviderHttp.SameText(Signature(parts), expected.Trim().ToLowerInvariant());

    /// <summary>The 32-byte key from the 43-character EncodingAESKey (null when malformed).</summary>
    public static byte[]? Key(string encodingAesKey)
    {
        if (string.IsNullOrWhiteSpace(encodingAesKey) || encodingAesKey.Trim().Length != 43) return null;
        try
        {
            var key = Convert.FromBase64String(encodingAesKey.Trim() + "=");
            return key.Length == 32 ? key : null;
        }
        catch { return null; }
    }

    /// <summary>Decrypt an <c>Encrypt</c> element; null when it does not decrypt or the AppID differs.</summary>
    public static string? Decrypt(string encodingAesKey, string encrypted, string expectedAppId)
    {
        var key = Key(encodingAesKey);
        if (key == null || string.IsNullOrWhiteSpace(encrypted)) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(Convert.FromBase64String(encrypted.Trim()), key[..16], PaddingMode.None);
            var pad = plain[^1];
            if (pad is < 1 or > 32) pad = 0;
            var body = plain[..^pad];
            if (body.Length < 20) return null;
            var len = (body[16] << 24) | (body[17] << 16) | (body[18] << 8) | body[19];
            if (len < 0 || 20 + len > body.Length) return null;
            var xml = Encoding.UTF8.GetString(body, 20, len);
            var appId = Encoding.UTF8.GetString(body, 20 + len, body.Length - 20 - len);
            return ProviderHttp.SameText(appId, expectedAppId) ? xml : null;
        }
        catch { return null; }
    }

    /// <summary>The inverse of <see cref="Decrypt"/> (passive replies; and the tests' signer).</summary>
    public static string Encrypt(string encodingAesKey, string xml, string appId)
    {
        var key = Key(encodingAesKey) ?? throw new ArgumentException("bad EncodingAESKey");
        var msg = Encoding.UTF8.GetBytes(xml);
        var app = Encoding.UTF8.GetBytes(appId);
        var body = new List<byte>(RandomNumberGenerator.GetBytes(16));
        body.AddRange(new[] { (byte)(msg.Length >> 24), (byte)(msg.Length >> 16), (byte)(msg.Length >> 8), (byte)msg.Length });
        body.AddRange(msg);
        body.AddRange(app);
        var pad = 32 - body.Count % 32;
        for (var i = 0; i < pad; i++) body.Add((byte)pad);
        using var aes = Aes.Create();
        aes.Key = key;
        return Convert.ToBase64String(aes.EncryptCbc(body.ToArray(), key[..16], PaddingMode.None));
    }
}

/// <summary>One parsed WeChat push.</summary>
/// <param name="Kind">message | subscribe | scan | unsubscribe | ignore</param>
public sealed record WeChatPush(string Kind, string ToUser, string OpenId, string Text, string ExternalId,
    DateTime? Timestamp, string Ref, InboundMediaRef? Media);

public static class WeChatInbound
{
    /// <summary>XML with DTDs and external entities refused (XXE-safe), size-capped.</summary>
    public static XElement? Load(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2_000_000,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader).Root;
        }
        catch { return null; }
    }

    private static string V(XElement x, string name) => x.Element(name)?.Value?.Trim() ?? "";

    /// <summary>The safe-mode envelope's <c>Encrypt</c> ("" when plaintext).</summary>
    public static string EncryptedPart(XElement x) => V(x, "Encrypt");

    public static WeChatPush Parse(XElement x)
    {
        var to = V(x, "ToUserName");
        var openId = V(x, "FromUserName");
        DateTime? ts = long.TryParse(V(x, "CreateTime"), out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
        var type = V(x, "MsgType").ToLowerInvariant();
        var msgId = V(x, "MsgId");
        InboundMediaRef? media = null;
        if (type == "event")
        {
            var ev = V(x, "Event").ToLowerInvariant();
            var key = V(x, "EventKey");
            var evId = $"evt:{openId}:{V(x, "CreateTime")}:{ev}";
            return ev switch
            {
                "subscribe" => new WeChatPush("subscribe", to, openId, "[followed the account]", evId, ts,
                    key.StartsWith("qrscene_", StringComparison.Ordinal) ? key["qrscene_".Length..] : "", null),
                "scan" => new WeChatPush("scan", to, openId, "[scanned a QR code]", evId, ts, key, null),
                "unsubscribe" => new WeChatPush("unsubscribe", to, openId, "", evId, ts, "", null),
                _ => new WeChatPush("ignore", to, openId, "", evId, ts, "", null),
            };
        }
        string text;
        switch (type)
        {
            case "text":
                text = V(x, "Content");
                break;
            case "image":
                text = "[image]";
                media = new InboundMediaRef(V(x, "MediaId"), "image", "image.jpg");
                break;
            case "voice":
                var heard = V(x, "Recognition");
                text = heard.Length > 0 ? $"[voice] {heard}" : "[voice]";
                media = new InboundMediaRef(V(x, "MediaId"), "audio",
                    $"voice.{(V(x, "Format") is { Length: > 0 } f ? MessagingMedia.SafeName(f, "amr") : "amr")}");
                break;
            case "video":
            case "shortvideo":
                text = "[video]";
                media = new InboundMediaRef(V(x, "MediaId"), "video", "video.mp4");
                break;
            case "location":
                text = $"[location] {V(x, "Label")} ({V(x, "Location_X")}, {V(x, "Location_Y")})".Trim();
                break;
            case "link":
                text = $"[link] {V(x, "Title")} {V(x, "Url")}".Trim();
                break;
            default:
                text = "[message]";
                break;
        }
        if (media is { Ref.Length: 0 }) media = null;
        if (text.Length == 0) text = "[message]";
        var id = msgId.Length > 0 ? msgId : $"msg:{openId}:{V(x, "CreateTime")}";
        return new WeChatPush("message", to, openId, text, id, ts, "", media);
    }
}
