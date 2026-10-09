using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// TikTok Business Messaging (TikTok API for Business — partner-gated; built to
/// the documented shapes, NOT verified against the live service):
/// <list type="bullet">
///   <item>out — <c>POST /open_api/v1.3/business/message/send/</c> (header
///         <c>Access-Token</c>) with the business id, the conversation as the
///         recipient and a TEXT message, inside the 48-hour reply window
///         (<see cref="ChannelWindow"/>);</item>
///   <item>in — <c>TikTok-Signature: t=&lt;unix&gt;,s=&lt;hex&gt;</c> where s =
///         HMAC-SHA256(app secret, "t.body"), with a 5-minute replay window;
///         message events carry a conversation id, a message id, the sender and
///         the text (<see cref="TikTokInbound"/> reads the common field names);</item>
///   <item>health — <c>GET /open_api/v1.3/business/get/</c>.</item>
/// </list>
/// Every endpoint path lives in this class so a correction is one edit here.
/// </summary>
public sealed class TikTokTransport : IChannelTransport
{
    public const string ApiBase = "https://business-api.tiktok.com/open_api/v1.3";
    public const string SignatureHeader = "TikTok-Signature";
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _http;

    public TikTokTransport(IHttpClientFactory http) => _http = http;

    public string Channel => ChannelIds.TikTok;
    public int MaxText => 1000;
    public bool SupportsMedia => false;

    private static HttpRequestMessage Req(HttpMethod method, string url, ChannelAccount a, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Access-Token", a.AccessToken);
        if (body != null) req.Content = ProviderHttp.JsonBody(body);
        return req;
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default) =>
        Result(await ProviderHttp.SendAsync(_http, Req(HttpMethod.Post, $"{ApiBase}/business/message/send/", a, new
        {
            business_id = a.ExternalId,
            recipient_type = "CONVERSATION",
            recipient = to.Key,
            message_type = "TEXT",
            text = new { body = text },
        }), ct, a.AccessToken));

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "TikTok messages from ChatBranch are text only — send a link instead"));

    /// <summary>The Business API envelope <c>{"code":0,"message":"OK","data":{…}}</c>.</summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"TikTok API unreachable: {r.Error}");
        var code = r.Long("code");
        if (r.Is2xx && code == 0 && r.Json.ValueKind == JsonValueKind.Object)
        {
            var id = r.Str("data", "message_id");
            if (id.Length == 0) id = r.Str("data", "message", "message_id");
            return TransportResult.Success(id);
        }
        var msg = r.Str("message");
        var failure = code switch
        {
            40100 or 40101 or 40102 or 40104 or 40105 or 40001 => TransportFailure.Auth,
            40016 or 40133 or 51021 => TransportFailure.RateLimit,
            >= 50000 => TransportFailure.Transient,
            _ when r.Status == 401 => TransportFailure.Auth,
            _ when r.Status == 429 => TransportFailure.RateLimit,
            _ when r.Status >= 500 => TransportFailure.Transient,
            _ => TransportFailure.Recipient,
        };
        return TransportResult.Fail(failure,
            $"TikTok {(code != 0 ? code.ToString() : $"HTTP {r.Status}")}: {(msg.Length > 0 ? msg : "error")}",
            r.Status, code.ToString());
    }

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var fields = Uri.EscapeDataString("[\"display_name\",\"username\"]");
        var r = await ProviderHttp.SendAsync(_http, Req(HttpMethod.Get,
            $"{ApiBase}/business/get/?business_id={Uri.EscapeDataString(a.ExternalId)}&fields={fields}", a), ct,
            a.AccessToken);
        var res = Result(r);
        if (!res.Ok)
            return new(res.Failure == TransportFailure.Auth
                ? new ProbeVerdict(Health.Disconnected, res.Detail)
                : new ProbeVerdict(null, res.Detail, Transient: true));
        var name = r.Str("data", "display_name");
        if (name.Length == 0) name = r.Str("data", "username");
        return new(new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"), name);
    }

    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string)>((null, "TikTok media are not downloaded"));

    // ---- webhook -------------------------------------------------------------------

    public static string Sign(string appSecret, string timestamp, byte[] body)
    {
        var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return ProviderHttp.Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), payload));
    }

    /// <summary>Check <c>t=…,s=…</c>: the HMAC and that t is within the replay window.</summary>
    public static bool VerifySignature(string appSecret, byte[] body, string? header, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrWhiteSpace(header)) return false;
        string t = "", s = "";
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t") t = kv[1].Trim();
            else if (kv[0].Trim() == "s") s = kv[1].Trim();
        }
        if (!long.TryParse(t, out var unix) || s.Length == 0) return false;
        var age = nowUtc - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        if (age.Duration() > SignatureTolerance) return false;
        return ProviderHttp.SameText(Sign(appSecret, t, body), s.ToLowerInvariant());
    }
}

/// <summary>One inbound TikTok direct message.</summary>
public sealed record TikTokMessage(string ConversationId, string MessageId, string SenderId, string Name,
    string Text, DateTime? Timestamp);

/// <summary>
/// Tolerant reader for TikTok's webhook envelope (<c>{event, create_time, content}</c>,
/// where content may be a JSON string or an object). Only RECEIVED message events
/// become inbox messages; anything else (and our own business's echoes) is ignored.
/// </summary>
public static class TikTokInbound
{
    public static TikTokMessage? Parse(JsonElement root, string businessId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var ev = ProviderHttp.Str(root, "event").ToLowerInvariant();
        if (!(ev.Contains("message") || ev.Contains("receive")) || ev.Contains("send") || ev.Contains("read")) return null;
        var content = ProviderHttp.At(root, "content");
        if (content.ValueKind == JsonValueKind.String)
        {
            try { content = JsonSerializer.Deserialize<JsonElement>(content.GetString() ?? "{}"); }
            catch { return null; }
        }
        if (content.ValueKind != JsonValueKind.Object) content = root;
        string First(params string[][] paths)
        {
            foreach (var p in paths)
                if (ProviderHttp.Str(content, p) is { Length: > 0 } v) return v;
            return "";
        }
        var sender = First(new[] { "from_user", "id" }, new[] { "sender", "id" }, new[] { "from", "id" },
            new[] { "from_user_id" }, new[] { "sender_id" });
        if (sender.Length == 0) sender = ProviderHttp.Str(root, "user_openid");
        if (sender.Length == 0 || sender == businessId) return null;
        var conversation = First(new[] { "conversation_id" }, new[] { "conversation", "id" });
        var text = First(new[] { "text", "body" }, new[] { "message", "text", "body" }, new[] { "message", "text" },
            new[] { "text" });
        var type = First(new[] { "message_type" }, new[] { "message", "type" }).ToLowerInvariant();
        if (text.Length == 0) text = type is "" or "text" ? "[message]" : $"[{type}]";
        var ts = ProviderHttp.Long(content, "timestamp") is var t and > 0 ? t
            : ProviderHttp.Long(root, "create_time");
        DateTime? when = ts <= 0 ? null
            : ts > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
            : DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        var name = First(new[] { "from_user", "display_name" }, new[] { "sender", "display_name" },
            new[] { "from_user", "nickname" }, new[] { "sender", "nickname" });
        return new TikTokMessage(conversation.Length > 0 ? conversation : sender,
            First(new[] { "message_id" }, new[] { "message", "message_id" }, new[] { "message", "id" }),
            sender, name, text, when);
    }
}
