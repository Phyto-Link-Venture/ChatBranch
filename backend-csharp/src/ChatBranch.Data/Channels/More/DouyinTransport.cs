using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Douyin (抖音开放平台) enterprise-account private messages (企业号私信) —
/// approval-gated; built to the documented shapes, NOT verified against the
/// live service:
/// <list type="bullet">
///   <item>in — the app's webhook, signed <c>X-Douyin-Signature</c> = sha1(client_secret + body)
///         (hex); <c>verify_webhook</c> is answered with its challenge;
///         <c>im_receive_msg</c> carries the user's open_id, the enterprise account's
///         open_id and a JSON <c>content</c> (conversation_short_id, server_message_id,
///         message_type, text, user_infos);</item>
///   <item>out — a reply to the user's latest message: <c>POST /im/send/msg/?open_id=…</c>
///         (header <c>access-token</c> = the enterprise account's token) quoting that
///         message's id and conversation, inside a 48-hour window
///         (<see cref="ChannelWindow"/>);</item>
///   <item>health — the client-credential token (<c>/oauth/client_token/</c>) proves
///         client_key + client_secret.</item>
/// </list>
/// An inbound message's external id is <c>conversation_short_id|server_message_id</c>,
/// which is exactly what a reply needs to quote.
/// </summary>
public sealed class DouyinTransport : IChannelTransport
{
    public const string ApiBase = "https://open.douyin.com";
    public const string SignatureHeader = "X-Douyin-Signature";

    private readonly IHttpClientFactory _http;

    public DouyinTransport(IHttpClientFactory http) => _http = http;

    public string Channel => ChannelIds.Douyin;
    public int MaxText => 500;
    public bool SupportsMedia => false;

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default)
    {
        var (conversation, msgId) = SplitId(to.LastInboundId);
        if (conversation.Length == 0 || msgId.Length == 0)
            return TransportResult.Fail(TransportFailure.Window,
                "Douyin only lets an enterprise account reply to a message the user sent — nothing to reply to yet");
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}/im/send/msg/?open_id={Uri.EscapeDataString(a.ExternalId)}")
        {
            Content = ProviderHttp.JsonBody(new
            {
                msg_id = msgId,
                conversation_id = conversation,
                to_user_id = to.Key,
                content = new { msg_type = 1, text = new { text } },
            }),
        };
        req.Headers.TryAddWithoutValidation("access-token", a.AccessToken);
        return Result(await ProviderHttp.SendAsync(_http, req, ct, a.AccessToken));
    }

    public static (string Conversation, string MessageId) SplitId(string? externalId)
    {
        var parts = (externalId ?? "").Split('|', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("", "");
    }

    public static string JoinId(string conversation, string messageId) => $"{conversation}|{messageId}";

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "Douyin replies from ChatBranch are text only — send a link instead"));

    /// <summary>Douyin answers either <c>{"err_no":…}</c> or <c>{"data":{"error_code":…,"description":…}}</c>.</summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"Douyin API unreachable: {r.Error}");
        if (!r.Is2xx)
            return TransportResult.Fail(r.Status >= 500 ? TransportFailure.Transient
                : r.Status == 401 ? TransportFailure.Auth : TransportFailure.Recipient, $"Douyin API HTTP {r.Status}", r.Status);
        var code = r.Long("err_no");
        if (code == 0) code = r.Long("data", "error_code");
        if (code == 0) code = r.Long("extra", "error_code");
        if (code == 0)
        {
            var id = r.Str("data", "msg_id");
            if (id.Length == 0) id = r.Str("data", "server_message_id");
            return TransportResult.Success(id);
        }
        var msg = r.Str("err_msg");
        if (msg.Length == 0) msg = r.Str("data", "description");
        if (msg.Length == 0) msg = r.Str("extra", "description");
        var failure = code switch
        {
            2190002 or 2190004 or 2190008 or 28001003 or 28001005 or 10008 or 28001008 => TransportFailure.Auth,
            2190015 or 28001014 => TransportFailure.RateLimit,
            _ => TransportFailure.Recipient,
        };
        return TransportResult.Fail(failure, $"Douyin {code}: {(msg.Length > 0 ? msg : "error")}", r.Status, code.ToString());
    }

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var key = MoreChannels.Cfg(a, "client_key");
        if (key.Length == 0 || string.IsNullOrEmpty(a.AppSecret))
            return new(new ProbeVerdict(Health.Degraded,
                "client_key / client_secret not set — Douyin webhooks can't be verified, so inbound messages are refused"));
        var r = await ProviderHttp.SendAsync(_http, new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/oauth/client_token/")
        {
            Content = ProviderHttp.JsonBody(new { client_key = key, client_secret = a.AppSecret, grant_type = "client_credential" }),
        }, ct, a.AppSecret);
        if (r.Status == 0) return new(new ProbeVerdict(null, $"Douyin API unreachable: {r.Error}", Transient: true));
        if (r.Is2xx && r.Str("data", "access_token").Length > 0)
            return new(new ProbeVerdict(Health.Healthy, "Connected — client credentials accepted"));
        var why = r.Str("data", "description");
        return new(new ProbeVerdict(Health.Disconnected,
            $"Douyin refused the client credentials{(why.Length > 0 ? $": {why}" : "")}"));
    }

    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string)>((null, "Douyin media are not downloaded"));

    /// <summary>X-Douyin-Signature = sha1(client_secret + raw body), hex, compared in constant time.</summary>
    public static string Sign(string clientSecret, byte[] body) =>
        ProviderHttp.Hex(SHA1.HashData(Encoding.UTF8.GetBytes(clientSecret).Concat(body).ToArray()));

    public static bool VerifySignature(string clientSecret, byte[] body, string? header) =>
        !string.IsNullOrEmpty(clientSecret) && !string.IsNullOrWhiteSpace(header)
        && ProviderHttp.SameText(Sign(clientSecret, body), header.Trim().ToLowerInvariant());
}

/// <summary>One Douyin webhook event the inbox cares about.</summary>
/// <param name="Kind">verify | message | ignore</param>
public sealed record DouyinEvent(string Kind, string Challenge, string FromUser, string ToUser, string ExternalId,
    string Name, string Text, DateTime? Timestamp);

public static class DouyinInbound
{
    public static DouyinEvent? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var ev = ProviderHttp.Str(root, "event");
        var content = ProviderHttp.At(root, "content");
        if (content.ValueKind == JsonValueKind.String)
        {
            try { content = JsonSerializer.Deserialize<JsonElement>(content.GetString() ?? "{}"); }
            catch { content = default; }
        }
        if (ev == "verify_webhook")
            return new DouyinEvent("verify", ProviderHttp.Str(content, "challenge"), "", "", "", "", "", null);
        if (ev != "im_receive_msg")
            return new DouyinEvent("ignore", "", "", "", "", "", "", null);
        var from = ProviderHttp.Str(root, "from_user_id");
        var to = ProviderHttp.Str(root, "to_user_id");
        var conversation = ProviderHttp.Str(content, "conversation_short_id");
        var msgId = ProviderHttp.Str(content, "server_message_id");
        var type = ProviderHttp.Str(content, "message_type").ToLowerInvariant();
        var text = ProviderHttp.Str(content, "text");
        if (text.Length == 0) text = type is "" or "text" ? "[message]" : $"[{type}]";
        var name = "";
        var infos = ProviderHttp.At(content, "user_infos");
        if (infos.ValueKind == JsonValueKind.Array)
            foreach (var u in infos.EnumerateArray())
                if (ProviderHttp.Str(u, "open_id") == from) name = ProviderHttp.Str(u, "nick_name");
        var ts = ProviderHttp.Long(content, "create_time");
        DateTime? when = ts <= 0 ? null
            : ts > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
            : DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        return new DouyinEvent("message", "", from, to,
            conversation.Length > 0 && msgId.Length > 0 ? DouyinTransport.JoinId(conversation, msgId) : msgId,
            name, text, when);
    }
}
