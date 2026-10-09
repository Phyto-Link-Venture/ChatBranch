using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// LINE Messaging API (a LINE Official Account's bot):
/// <list type="bullet">
///   <item>out — the webhook's <c>replyToken</c> while it is fresh (free, single use,
///         ≈1 minute), else <c>POST /v2/bot/message/push</c>; text only (LINE takes
///         images by public https URL, which our auth-gated media are not);</item>
///   <item>in — <c>X-Line-Signature</c> = base64(HMAC-SHA256(channel secret, body));
///         text, image / video / audio / file (downloaded from api-data.line.me off
///         the ack), sticker / location as placeholders; follow / unfollow keep the
///         opt-out in step;</item>
///   <item>profile — <c>GET /v2/bot/profile/{userId}</c> names a new thread;</item>
///   <item>health — <c>GET /v2/bot/info</c> (+ whether the webhook endpoint is active);
///         "Register webhook" = <c>PUT /v2/bot/channel/webhook/endpoint</c>.</item>
/// </list>
/// No messaging window (push may cost quota on the account's plan).
/// </summary>
public sealed class LineTransport : IChannelTransport
{
    public const string ApiBase = "https://api.line.me";
    public const string DataBase = "https://api-data.line.me";
    public const string SignatureHeader = "X-Line-Signature";
    /// <summary>LINE documents reply tokens as short-lived (about a minute); stay under it.</summary>
    public static readonly TimeSpan ReplyTokenTtl = TimeSpan.FromSeconds(50);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (string Token, DateTime At)> _replyTokens = new();

    public LineTransport(IHttpClientFactory http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    public string Channel => ChannelIds.Line;
    public int MaxText => 5000;
    public bool SupportsMedia => false;

    // ---- reply tokens ------------------------------------------------------------

    public void RememberReplyToken(int accountId, string userId, string token, DateTime receivedUtc)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId)) return;
        _replyTokens[$"{accountId}:{userId}"] = (token, receivedUtc);
        if (_replyTokens.Count > 5000) // never grows without bound
            foreach (var (k, v) in _replyTokens)
                if (Now - v.At > ReplyTokenTtl) _replyTokens.TryRemove(k, out _);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A fresh, unused reply token for this user (consumed: LINE accepts each once).</summary>
    public string? TakeReplyToken(int accountId, string userId)
    {
        if (!_replyTokens.TryRemove($"{accountId}:{userId}", out var t)) return null;
        return Now - t.At <= ReplyTokenTtl ? t.Token : null;
    }

    // ---- out ---------------------------------------------------------------------

    private HttpRequestMessage Req(HttpMethod method, string url, ChannelAccount a, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.AccessToken);
        if (body != null) req.Content = ProviderHttp.JsonBody(body);
        return req;
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default)
    {
        var messages = new[] { new { type = "text", text } };
        if (TakeReplyToken(a.Id, to.Key) is { } token)
        {
            var reply = Result(await ProviderHttp.SendAsync(_http,
                Req(HttpMethod.Post, $"{ApiBase}/v2/bot/message/reply", a, new { replyToken = token, messages }),
                ct, a.AccessToken));
            // an expired / already-used token is a 400 — the push below still reaches them
            if (reply.Ok || reply.HttpStatus != 400) return reply;
        }
        return Result(await ProviderHttp.SendAsync(_http,
            Req(HttpMethod.Post, $"{ApiBase}/v2/bot/message/push", a, new { to = to.Key, messages }),
            ct, a.AccessToken));
    }

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "LINE only accepts images by public https URL — send a link instead"));

    /// <summary>LINE answers <c>{"sentMessages":[{"id":…}]}</c> or <c>{"message":…,"details":[…]}</c>.</summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Is2xx) return TransportResult.Success(r.Str("sentMessages", "0", "id"));
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"LINE API unreachable: {r.Error}");
        var message = r.Str("message");
        var detail = r.Str("details", "0", "message");
        var why = $"LINE API {r.Status}: {(message.Length > 0 ? message : "error")}{(detail.Length > 0 ? $" ({detail})" : "")}";
        var failure = r.Status switch
        {
            401 => TransportFailure.Auth,
            403 => TransportFailure.Auth,
            429 when message.Contains("monthly limit", StringComparison.OrdinalIgnoreCase) => TransportFailure.Billing,
            429 => TransportFailure.RateLimit,
            >= 500 => TransportFailure.Transient,
            _ => TransportFailure.Recipient,
        };
        return TransportResult.Fail(failure, why, r.Status);
    }

    // ---- account -----------------------------------------------------------------

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var info = await ProviderHttp.SendAsync(_http, Req(HttpMethod.Get, $"{ApiBase}/v2/bot/info", a), ct,
            a.AccessToken);
        if (info.Status == 401)
            return new(new ProbeVerdict(Health.Disconnected, "channel access token invalid or expired"));
        if (!info.Is2xx)
        {
            var r = Result(info);
            return new(r.Failure is TransportFailure.Auth
                ? new ProbeVerdict(Health.Disconnected, r.Detail)
                : new ProbeVerdict(null, r.Detail, Transient: true));
        }
        var name = info.Str("displayName");
        var id = info.Str("userId");
        var hook = await ProviderHttp.SendAsync(_http,
            Req(HttpMethod.Get, $"{ApiBase}/v2/bot/channel/webhook/endpoint", a), ct, a.AccessToken);
        if (hook.Is2xx && (hook.Str("endpoint").Length == 0 || hook.Str("active") == "false"))
            return new(new ProbeVerdict(Health.Degraded,
                "bot alive, but its webhook is not active — inbound messages won't arrive (Channels → Register webhook)"),
                name, id);
        return new(new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"), name, id);
    }

    /// <summary>"Register webhook": point the channel at <paramref name="url"/>.</summary>
    public async Task<TransportResult> SetWebhookAsync(ChannelAccount a, string url, CancellationToken ct = default) =>
        Result(await ProviderHttp.SendAsync(_http,
            Req(HttpMethod.Put, $"{ApiBase}/v2/bot/channel/webhook/endpoint", a, new { endpoint = url }),
            ct, a.AccessToken));

    /// <summary>The user's display name ("" when LINE won't say — e.g. they blocked the bot).</summary>
    public async Task<string> ProfileNameAsync(ChannelAccount a, string userId, CancellationToken ct = default)
    {
        var r = await ProviderHttp.SendAsync(_http,
            Req(HttpMethod.Get, $"{ApiBase}/v2/bot/profile/{Uri.EscapeDataString(userId)}", a), ct, a.AccessToken);
        return r.Is2xx ? r.Str("displayName") : "";
    }

    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default) =>
        ProviderHttp.DownloadAsync(_http,
            Req(HttpMethod.Get, $"{DataBase}/v2/bot/message/{Uri.EscapeDataString(media.Ref)}/content", a),
            MessagingMedia.MaxBytes, ct, a.AccessToken);

    // ---- in ----------------------------------------------------------------------

    /// <summary>X-Line-Signature = base64(HMAC-SHA256(channel secret, raw body)), compared in constant time.</summary>
    public static bool VerifySignature(string channelSecret, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(channelSecret) || string.IsNullOrWhiteSpace(header)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body);
        byte[] given;
        try { given = Convert.FromBase64String(header.Trim()); }
        catch { return false; }
        return ProviderHttp.SameBytes(expected, given);
    }

    public static string Sign(string channelSecret, byte[] body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), body));
}

/// <summary>One LINE webhook event the inbox cares about.</summary>
public sealed record LineEvent(string Type, string UserId, string ReplyToken, string MessageId, string Text,
    DateTime? Timestamp, InboundMediaRef? Media);

public static class LineInbound
{
    /// <summary>Parse the webhook body: the bot's own id (<c>destination</c>) + the events from users.</summary>
    public static (string Destination, List<LineEvent> Events) Parse(JsonElement root)
    {
        var events = new List<LineEvent>();
        var destination = ProviderHttp.Str(root, "destination");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("events", out var list)
            || list.ValueKind != JsonValueKind.Array) return (destination, events);
        foreach (var e in list.EnumerateArray())
        {
            var type = ProviderHttp.Str(e, "type");
            // one-to-one chats only: a group / room has no single customer to answer
            if (ProviderHttp.Str(e, "source", "type") != "user") continue;
            var userId = ProviderHttp.Str(e, "source", "userId");
            if (userId.Length == 0) continue;
            var ts = ProviderHttp.Long(e, "timestamp") is var ms and > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
            var replyToken = ProviderHttp.Str(e, "replyToken");
            if (type != "message")
            {
                if (type is "follow" or "unfollow")
                    events.Add(new LineEvent(type, userId, replyToken, "", "", ts, null));
                continue;
            }
            var m = ProviderHttp.At(e, "message");
            var id = ProviderHttp.Str(m, "id");
            var kind = ProviderHttp.Str(m, "type");
            InboundMediaRef? media = null;
            var text = kind switch
            {
                "text" => ProviderHttp.Str(m, "text"),
                "image" => "[image]",
                "video" => "[video]",
                "audio" => "[audio]",
                "file" => $"[file] {ProviderHttp.Str(m, "fileName")}".Trim(),
                "sticker" => "[sticker]",
                "location" => $"[location] {ProviderHttp.Str(m, "title")} {ProviderHttp.Str(m, "address")}".Trim(),
                _ => "[message]",
            };
            // LINE-hosted content only ("external" content lives at a URL we don't fetch)
            if (kind is "image" or "video" or "audio" or "file"
                && ProviderHttp.Str(m, "contentProvider", "type") is "" or "line")
            {
                var name = kind switch
                {
                    "image" => "image.jpg",
                    "video" => "video.mp4",
                    "audio" => "audio.m4a",
                    _ => MessagingMedia.SafeName(ProviderHttp.Str(m, "fileName"), "file.bin"),
                };
                media = new InboundMediaRef(id, kind == "file" ? MessagingMedia.TypeFor(name) : kind, name);
            }
            if (text.Length == 0) text = "[message]";
            events.Add(new LineEvent("message", userId, replyToken, id, text, ts, media));
        }
        return (destination, events);
    }
}
