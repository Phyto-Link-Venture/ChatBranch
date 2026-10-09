using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Messenger + Instagram over Meta's Send API (port of the messaging app's
/// Meta client): <c>POST /me/messages</c> with the PAGE access token for text,
/// URL attachments and uploaded files (multipart <c>filedata</c>), <c>GET /me</c>
/// for the account test, the sender's profile for a new conversation's name,
/// and inbound attachment downloads from Meta's CDN. Instagram messaging
/// through a Facebook page uses the same endpoint and the page's token. The
/// token rides in the Authorization header; nothing is logged. Unconfigured (no
/// token) = the caller stores a sandbox message.
///
/// <c>tag</c>: null sends <c>messaging_type: RESPONSE</c> (inside the 24h
/// window); a tag (only <see cref="SocialWindow.HumanAgentTag"/> is used) sends
/// <c>messaging_type: MESSAGE_TAG</c> + that tag. The window decision is the
/// caller's (<see cref="SocialWindow"/>, <see cref="OutboundSender"/>).
/// </summary>
public interface IMetaMessagingClient
{
    bool IsConfigured(ChannelAccount account);
    Task<SendOutcome> SendTextAsync(ChannelAccount account, string recipientId, string text,
        string? tag = null, CancellationToken ct = default);
    /// <summary>image | video | audio | file, by public URL.</summary>
    Task<SendOutcome> SendAttachmentAsync(ChannelAccount account, string recipientId, string type, string url,
        string? tag = null, CancellationToken ct = default);
    /// <summary>
    /// image | video | audio | file UPLOADED with the message (multipart
    /// <c>recipient</c> / <c>messaging_type</c> / <c>message</c> /
    /// <c>filedata</c>) — for a file we hold, not one at a public URL.
    /// </summary>
    Task<SendOutcome> SendFileAsync(ChannelAccount account, string recipientId, string type, byte[] content,
        string mime, string fileName, string? tag = null, CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "file upload is not supported by this client"));
    /// <summary>
    /// A sender action (<c>mark_seen</c> | <c>typing_on</c> | <c>typing_off</c>) —
    /// <c>POST /me/messages</c> with only <c>recipient</c> + <c>sender_action</c>.
    /// Meta allows these inside the standard 24h window only.
    /// </summary>
    Task<SendOutcome> SendActionAsync(ChannelAccount account, string recipientId, string action,
        CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "sender actions are not supported by this client"));
    /// <summary>GET /me — proves the token (and names the page / IG account).</summary>
    Task<GraphRead> GetMeAsync(ChannelAccount account, CancellationToken ct = default);
    /// <summary>Best-effort display name of a PSID / IGSID ("" when Meta won't say).</summary>
    Task<string> GetProfileNameAsync(ChannelAccount account, string userId, CancellationToken ct = default);
    /// <summary>An inbound attachment from Meta's CDN (https only, capped).</summary>
    Task<MediaDownload> DownloadAsync(string url, long maxBytes, CancellationToken ct = default);
}

public class MetaMessagingClient : IMetaMessagingClient
{
    public const string HttpClientName = "meta-messaging";

    private readonly IHttpClientFactory _http;
    public MetaMessagingClient(IHttpClientFactory http) => _http = http;

    private HttpClient Client() => _http.CreateClient(HttpClientName);

    public bool IsConfigured(ChannelAccount account) =>
        account.Channel is ChatBranch.Domain.Channels.Facebook or ChatBranch.Domain.Channels.Instagram
        && !string.IsNullOrEmpty(account.AccessToken);

    private async Task<SendOutcome> PostAsync(ChannelAccount account, object payload, CancellationToken ct)
    {
        if (!IsConfigured(account)) return new SendOutcome(false, "", "account not configured");
        var r = await MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Post,
            MetaGraph.Url(account, "me", "messages"), account.AccessToken, JsonContent.Create(payload)), ct);
        if (!r.Ok) return new SendOutcome(false, "", r.Detail, r.ErrorCode, r.ErrorSubcode);
        return new SendOutcome(true, MetaGraph.Str(r.Body, "message_id"), "");
    }

    /// <summary>The Send API's attachment type for our bubble type (document → file).</summary>
    public static string AttachmentType(string type) => type switch
    {
        "image" or "video" or "audio" => type,
        _ => "file",
    };

    /// <summary>recipient + messaging_type (+ tag) — the envelope of every send.</summary>
    private static Dictionary<string, object?> Envelope(string recipientId, string? tag)
    {
        var body = new Dictionary<string, object?> { ["recipient"] = new { id = recipientId } };
        if (string.IsNullOrEmpty(tag)) body["messaging_type"] = "RESPONSE";
        else
        {
            body["messaging_type"] = "MESSAGE_TAG";
            body["tag"] = tag;
        }
        return body;
    }

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string recipientId, string text,
        string? tag = null, CancellationToken ct = default)
    {
        var body = Envelope(recipientId, tag);
        body["message"] = new { text };
        return PostAsync(account, body, ct);
    }

    public Task<SendOutcome> SendActionAsync(ChannelAccount account, string recipientId, string action,
        CancellationToken ct = default) =>
        PostAsync(account, new Dictionary<string, object?>
        {
            ["recipient"] = new { id = recipientId },
            ["sender_action"] = action,
        }, ct);

    public Task<SendOutcome> SendAttachmentAsync(ChannelAccount account, string recipientId, string type,
        string url, string? tag = null, CancellationToken ct = default)
    {
        var body = Envelope(recipientId, tag);
        body["message"] = new
        {
            attachment = new { type = AttachmentType(type), payload = new { url, is_reusable = false } },
        };
        return PostAsync(account, body, ct);
    }

    public async Task<SendOutcome> SendFileAsync(ChannelAccount account, string recipientId, string type,
        byte[] content, string mime, string fileName, string? tag = null, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new SendOutcome(false, "", "account not configured");
        if (content.Length == 0) return new SendOutcome(false, "", "the file is empty");
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in Envelope(recipientId, tag))
            form.Add(new StringContent(value is string str ? str : JsonSerializer.Serialize(value)), key);
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            attachment = new { type = AttachmentType(type), payload = new { is_reusable = false } },
        })), "message");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime.Split(';')[0].Trim());
        form.Add(file, "filedata", string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName);
        var r = await MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Post,
            MetaGraph.Url(account, "me", "messages"), account.AccessToken, form), ct);
        if (!r.Ok) return new SendOutcome(false, "", r.Detail, r.ErrorCode, r.ErrorSubcode);
        return new SendOutcome(true, MetaGraph.Str(r.Body, "message_id"), "");
    }

    public Task<GraphRead> GetMeAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return Task.FromResult(new GraphRead(false, default, "account not configured"));
        return MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Get,
            MetaGraph.Url(account, "me") + "?fields=id,name", account.AccessToken), ct);
    }

    public async Task<string> GetProfileNameAsync(ChannelAccount account, string userId,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account) || string.IsNullOrEmpty(userId)) return "";
        var fields = account.Channel == ChatBranch.Domain.Channels.Instagram ? "name,username" : "first_name,last_name,name";
        var r = await MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Get,
            MetaGraph.Url(account, userId) + $"?fields={fields}", account.AccessToken), ct);
        if (!r.Ok) return "";
        var name = MetaGraph.Str(r.Body, "name");
        if (name.Length == 0)
            name = $"{MetaGraph.Str(r.Body, "first_name")} {MetaGraph.Str(r.Body, "last_name")}".Trim();
        if (name.Length == 0) name = MetaGraph.Str(r.Body, "username");
        return name;
    }

    public async Task<MediaDownload> DownloadAsync(string url, long maxBytes, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new MediaDownload(false, Array.Empty<byte>(), "", "not an https url");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new MediaDownload(false, Array.Empty<byte>(), "", $"download failed (HTTP {(int)resp.StatusCode})");
            var bytes = await MetaGraph.ReadCappedAsync(resp.Content, maxBytes, ct);
            if (bytes == null) return new MediaDownload(false, Array.Empty<byte>(), "", "file too large");
            return new MediaDownload(true, bytes,
                resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new MediaDownload(false, Array.Empty<byte>(), "", "download failed");
        }
    }
}
