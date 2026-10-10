using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// One provider round-trip. <see cref="ErrorCode"/>/<see cref="ErrorSubcode"/>
/// carry Meta's error so the caller can classify it (<see cref="MetaErrors"/>):
/// per-recipient codes must never damage account health.
/// </summary>
public record SendOutcome(bool Ok, string ExternalId, string Detail, int? ErrorCode = null,
    int? ErrorSubcode = null);

/// <summary>A downloaded attachment (bytes are capped by the caller's limit).</summary>
public record MediaDownload(bool Ok, byte[] Bytes, string Mime, string Detail, int? ErrorCode = null);

/// <summary>A Graph read: the JSON body on success, Meta's error otherwise.</summary>
public record GraphRead(bool Ok, JsonElement Body, string Detail, int? ErrorCode = null, int? ErrorSubcode = null);

/// <summary>
/// WhatsApp Cloud API client (port of <c>whatsapp/client.py</c>): text, real
/// TEMPLATE messages, media (upload → send by id; inbound download by media id),
/// the template registry of a WABA (list / create / edit / delete) and the
/// number profile used by the account test. Every call takes the account
/// (multi-account); an account with no access_token is "not configured" and the
/// caller sandbox-stores instead. Tokens ride in the Authorization header, never
/// in a URL, and nothing here logs a token or a provider body.
///
/// The members added after <see cref="SendTextAsync"/> have default bodies that
/// answer "not supported", so a hand-written test double only has to implement
/// what it exercises.
/// </summary>
public interface IWhatsAppCloudClient
{
    bool IsConfigured(ChannelAccount account);
    Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default);

    /// <summary>POST a <c>type: "template"</c> message; <paramref name="template"/> is the
    /// <c>template</c> object (name, language, components).</summary>
    Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "template sends are not supported by this client"));

    /// <summary>Upload bytes to /{phone_number_id}/media → the media id.</summary>
    Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "media upload is not supported by this client"));

    /// <summary>Send an uploaded media id as image | document | audio | video.</summary>
    Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "media sends are not supported by this client"));

    /// <summary>Inbound media: GET /{media-id} → its download URL → the bytes (bearer token).</summary>
    Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default) =>
        Task.FromResult(new MediaDownload(false, Array.Empty<byte>(), "", "media download is not supported"));

    /// <summary>Every template of the account's WABA (paged GET /{waba_id}/message_templates).</summary>
    Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(false, default, "template listing is not supported"));

    /// <summary>POST /{waba_id}/message_templates (submits for Meta review).</summary>
    Task<GraphRead> CreateTemplateAsync(ChannelAccount account, object payload, CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(false, default, "template creation is not supported"));

    /// <summary>POST /{template_id} — edit components/category of an existing template.</summary>
    Task<GraphRead> EditTemplateAsync(ChannelAccount account, string templateId, object payload,
        CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(false, default, "template editing is not supported"));

    /// <summary>DELETE /{waba_id}/message_templates?name=…[&amp;hsm_id=…].</summary>
    Task<GraphRead> DeleteTemplateAsync(ChannelAccount account, string name, string? templateId,
        CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(false, default, "template deletion is not supported"));

    /// <summary>GET /{phone_number_id} — display number, verified name, quality rating.</summary>
    Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default) =>
        Task.FromResult(new GraphRead(false, default, "number lookup is not supported"));
}

/// <summary>Shared Graph plumbing for the WhatsApp Cloud and Messenger/Instagram clients.</summary>
public static class MetaGraph
{
    public const string Host = "https://graph.facebook.com";
    public const string DefaultVersion = "v23.0";

    private static readonly Regex VersionRe = new(@"^v\d{1,2}\.\d{1,2}$", RegexOptions.Compiled);

    /// <summary>The account's Graph version, refusing anything that is not vNN.N.</summary>
    public static string Version(ChannelAccount a) =>
        a.ApiVersion is { } v && VersionRe.IsMatch(v) ? v : DefaultVersion;

    /// <summary>A Graph URL; every caller-controlled id is escaped as a path segment.</summary>
    public static string Url(ChannelAccount a, params string[] segments) =>
        $"{Host}/{Version(a)}/{string.Join('/', segments.Select(Uri.EscapeDataString))}";

    public static HttpRequestMessage Request(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Send and parse. Never throws: a transport failure comes back as a result.</summary>
    public static async Task<GraphRead> SendAsync(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var resp = await client.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            JsonElement body = default;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { body = JsonSerializer.Deserialize<JsonElement>(raw); }
                catch { /* non-JSON body: keep default */ }
            }
            if (resp.IsSuccessStatusCode) return new GraphRead(true, body, "");
            var (code, sub, msg) = Error(body);
            return new GraphRead(false, body, msg ?? $"HTTP {(int)resp.StatusCode}", code, sub);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GraphRead(false, default, "Meta did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            // the exception text can carry an internal proxy / host address — say
            // what happened (a network problem), never where it happened
            var why = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "the address could not be resolved",
                HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError => "the connection failed",
                HttpRequestError.SecureConnectionError => "the secure connection failed",
                _ => "a network error",
            };
            return new GraphRead(false, default, $"could not reach Meta ({why})");
        }
        finally
        {
            req.Dispose();
        }
    }

    /// <summary>Meta's <c>{"error": {code, error_subcode, message}}</c> envelope.</summary>
    public static (int? Code, int? Subcode, string? Message) Error(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("error", out var e)
            || e.ValueKind != JsonValueKind.Object) return (null, null, null);
        int? Int(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : null;
        var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null;
        // error_data.details is the human explanation on newer WhatsApp errors
        if (e.TryGetProperty("error_data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(det.GetString()))
            message = $"{message} — {det.GetString()}";
        return (Int("code"), Int("error_subcode"), message);
    }

    /// <summary>Read a capped body; null when it exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long len && len > maxBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > maxBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.ToString() : ""
            : "";
}

public class WhatsAppCloudClient : IWhatsAppCloudClient
{
    /// <summary>The named HttpClient (tests swap the whole client for a fake handler).</summary>
    public const string HttpClientName = "whatsapp";

    /// <summary>Safety stop for the paged template listing (100 per page).</summary>
    private const int MaxTemplatePages = 20;

    private readonly IHttpClientFactory _http;
    public WhatsAppCloudClient(IHttpClientFactory http) => _http = http;

    private HttpClient Client() => _http.CreateClient(HttpClientName);

    public bool IsConfigured(ChannelAccount account) =>
        account.Provider == Providers.Cloud
        && !string.IsNullOrEmpty(account.AccessToken)
        && !string.IsNullOrEmpty(account.ExternalId);

    private static SendOutcome NotConfigured() => new(false, "", "account not configured");

    /// <summary>POST /{phone_number_id}/messages and read back the wamid.</summary>
    private async Task<SendOutcome> PostMessageAsync(ChannelAccount account, object payload, CancellationToken ct)
    {
        if (!IsConfigured(account)) return NotConfigured();
        var url = MetaGraph.Url(account, account.ExternalId, "messages");
        var r = await MetaGraph.SendAsync(Client(),
            MetaGraph.Request(HttpMethod.Post, url, account.AccessToken, JsonContent.Create(payload)), ct);
        if (!r.Ok) return new SendOutcome(false, "", r.Detail, r.ErrorCode, r.ErrorSubcode);
        var wamid = r.Body.ValueKind == JsonValueKind.Object && r.Body.TryGetProperty("messages", out var msgs)
                    && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0
            ? MetaGraph.Str(msgs[0], "id") : "";
        return new SendOutcome(true, wamid, "");
    }

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) =>
        PostMessageAsync(account, new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = toWaId,
            type = "text",
            text = new { preview_url = false, body = text },
        }, ct);

    public Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default) =>
        PostMessageAsync(account, new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = toWaId,
            type = "template",
            template,
        }, ct);

    public async Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime,
        string filename, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return NotConfigured();
        var form = new MultipartFormDataContent
        {
            { new StringContent("whatsapp"), "messaging_product" },
            { new StringContent(mime), "type" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", string.IsNullOrWhiteSpace(filename) ? "upload" : filename);
        var url = MetaGraph.Url(account, account.ExternalId, "media");
        var r = await MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Post, url, account.AccessToken, form), ct);
        if (!r.Ok) return new SendOutcome(false, "", r.Detail, r.ErrorCode, r.ErrorSubcode);
        var id = MetaGraph.Str(r.Body, "id");
        return id.Length > 0 ? new SendOutcome(true, id, "") : new SendOutcome(false, "", "Meta returned no media id");
    }

    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default)
    {
        var media = new Dictionary<string, object?> { ["id"] = mediaId };
        // audio carries no caption; documents keep their file name
        if (type != "audio" && !string.IsNullOrWhiteSpace(caption)) media["caption"] = caption;
        if (type == "document" && !string.IsNullOrWhiteSpace(filename)) media["filename"] = filename;
        return PostMessageAsync(account, new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = toWaId,
            ["type"] = type,
            [type] = media,
        }, ct);
    }

    public async Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
            return new MediaDownload(false, Array.Empty<byte>(), "", "account not configured");
        var meta = await MetaGraph.SendAsync(Client(),
            MetaGraph.Request(HttpMethod.Get, MetaGraph.Url(account, mediaId), account.AccessToken), ct);
        if (!meta.Ok)
            return new MediaDownload(false, Array.Empty<byte>(), "", meta.Detail, meta.ErrorCode);
        var url = MetaGraph.Str(meta.Body, "url");
        var mime = MetaGraph.Str(meta.Body, "mime_type");
        if (long.TryParse(MetaGraph.Str(meta.Body, "file_size"), out var size) && size > maxBytes)
            return new MediaDownload(false, Array.Empty<byte>(), mime, $"file too large ({size} bytes)");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new MediaDownload(false, Array.Empty<byte>(), mime, "Meta returned no https download url");
        try
        {
            // the lookaside URL ALSO needs the bearer token (Meta's documented flow)
            using var req = MetaGraph.Request(HttpMethod.Get, uri.ToString(), account.AccessToken);
            using var resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new MediaDownload(false, Array.Empty<byte>(), mime, $"download failed (HTTP {(int)resp.StatusCode})");
            var bytes = await MetaGraph.ReadCappedAsync(resp.Content, maxBytes, ct);
            if (bytes == null) return new MediaDownload(false, Array.Empty<byte>(), mime, "file too large");
            if (mime.Length == 0) mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new MediaDownload(true, bytes, mime, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new MediaDownload(false, Array.Empty<byte>(), mime, "download failed");
        }
    }

    public async Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account) || string.IsNullOrEmpty(account.WabaId))
            return new GraphRead(false, default, "no WABA linked");
        var all = new List<JsonElement>();
        string? url = MetaGraph.Url(account, account.WabaId, "message_templates")
                      + "?fields=id,name,language,status,category,components,parameter_format&limit=100";
        for (var page = 0; url != null && page < MaxTemplatePages; page++)
        {
            var r = await MetaGraph.SendAsync(Client(),
                MetaGraph.Request(HttpMethod.Get, url, account.AccessToken), ct);
            if (!r.Ok) return r;
            if (r.Body.ValueKind == JsonValueKind.Object && r.Body.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
                all.AddRange(data.EnumerateArray().Select(x => x.Clone()));
            url = null;
            // follow paging.next only back to graph.facebook.com (the token rides along)
            if (r.Body.ValueKind == JsonValueKind.Object && r.Body.TryGetProperty("paging", out var paging)
                && MetaGraph.Str(paging, "next") is { Length: > 0 } next
                && Uri.TryCreate(next, UriKind.Absolute, out var nextUri)
                && nextUri.Scheme == Uri.UriSchemeHttps && nextUri.Host == "graph.facebook.com")
                url = next;
        }
        return new GraphRead(true, JsonSerializer.SerializeToElement(all), "");
    }

    public Task<GraphRead> CreateTemplateAsync(ChannelAccount account, object payload, CancellationToken ct = default)
    {
        if (!IsConfigured(account) || string.IsNullOrEmpty(account.WabaId))
            return Task.FromResult(new GraphRead(false, default, "no WABA linked"));
        return MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Post,
            MetaGraph.Url(account, account.WabaId, "message_templates"), account.AccessToken,
            JsonContent.Create(payload)), ct);
    }

    public Task<GraphRead> EditTemplateAsync(ChannelAccount account, string templateId, object payload,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return Task.FromResult(new GraphRead(false, default, "account not configured"));
        return MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Post,
            MetaGraph.Url(account, templateId), account.AccessToken, JsonContent.Create(payload)), ct);
    }

    public Task<GraphRead> DeleteTemplateAsync(ChannelAccount account, string name, string? templateId,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account) || string.IsNullOrEmpty(account.WabaId))
            return Task.FromResult(new GraphRead(false, default, "no WABA linked"));
        var url = MetaGraph.Url(account, account.WabaId, "message_templates")
                  + $"?name={Uri.EscapeDataString(name)}"
                  + (string.IsNullOrEmpty(templateId) ? "" : $"&hsm_id={Uri.EscapeDataString(templateId)}");
        return MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Delete, url, account.AccessToken), ct);
    }

    public Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return Task.FromResult(new GraphRead(false, default, "account not configured"));
        return MetaGraph.SendAsync(Client(), MetaGraph.Request(HttpMethod.Get,
            MetaGraph.Url(account, account.ExternalId)
            + "?fields=display_phone_number,verified_name,quality_rating,code_verification_status",
            account.AccessToken), ct);
    }
}
