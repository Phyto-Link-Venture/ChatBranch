using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// WhatsApp through YCloud (<c>https://api.ycloud.com/v2</c>, header <c>X-API-Key</c>), a Meta
/// Business Solution Provider whose message API is Meta-shaped — so the Cloud seam
/// (<see cref="IWhatsAppCloudClient"/>) carries it unchanged:
/// <list type="bullet">
///   <item>send — <c>POST /whatsapp/messages/sendDirectly</c> with <c>from</c> (our number), <c>to</c>,
///         <c>type</c> text | image | document | video | audio | template | reaction and the
///         Meta object for that type; a quoted reply carries <c>context.message_id</c>. The answer
///         carries the <c>wamid</c> (falling back to YCloud's own id), which is what statuses,
///         quotes and reactions refer to;</item>
///   <item>media — <c>POST /whatsapp/media/{phone}/upload</c> (multipart <c>file</c>) → a media id,
///         sent as <c>{id}</c>; a public https link is sent as <c>{link}</c>; inbound media is
///         downloaded from the event's <c>link</c> (the API key only ever goes to *.ycloud.com);</item>
///   <item>templates — <c>GET /whatsapp/templates?filter.wabaId=…</c> (Meta's components), create
///         <c>POST /whatsapp/templates</c>, edit <c>PATCH /whatsapp/templates/{waba}/{name}/{language}</c>,
///         delete <c>DELETE /whatsapp/templates/{waba}/{name}[/{language}]</c>. A template's id here is
///         <c>name:language</c> (YCloud addresses templates by both);</item>
///   <item>health — <c>GET /whatsapp/phoneNumbers/{waba}/{phone}</c> when a WABA id is set (quality
///         rating, status), else <c>GET /balance</c> (a cheap authenticated call).</item>
/// </list>
/// Errors: YCloud's <c>error.whatsappApiError.code</c> is Meta's own code and is passed on as
/// such (so <see cref="MetaErrors"/> classifies it); a refused key reads as Meta's 190, a
/// throttle as 130429, an empty balance as 131042 — per-recipient problems never touch health.
/// Built against YCloud's public API reference; verified here against fakes only.
/// </summary>
public sealed class YCloudWhatsAppClient : IWhatsAppCloudClient
{
    public const string ApiBase = "https://api.ycloud.com/v2";
    public const string KeyHeader = "X-API-Key";
    public const string SignatureHeader = "YCloud-Signature";
    /// <summary>A webhook older (or newer) than this is refused — replay protection.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private const int MaxTemplatePages = 20;

    private readonly IHttpClientFactory _http;

    public YCloudWhatsAppClient(IHttpClientFactory http) => _http = http;

    public bool IsConfigured(ChannelAccount account) =>
        WhatsAppProviders.Of(account) == Providers.YCloud && WhatsAppProviders.Configured(account);

    private static HttpRequestMessage Req(HttpMethod method, string url, ChannelAccount a, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation(KeyHeader, a.AccessToken);
        return req;
    }

    private Task<HttpReply> SendAsync(HttpRequestMessage req, ChannelAccount a, CancellationToken ct) =>
        ProviderHttp.SendAsync(_http, req, ct, a.AccessToken);

    private static SendOutcome NotConfigured() => new(false, "", "YCloud account not configured");

    // ---- errors ---------------------------------------------------------------------

    /// <summary>
    /// The Meta-equivalent code of a YCloud answer: Meta's own code when YCloud relays one
    /// (<c>whatsappApiError.code</c>), else what the HTTP status means for the account.
    /// </summary>
    public static (int? Code, string Detail) Error(HttpReply r)
    {
        if (r.Status == 0) return (null, $"YCloud unreachable: {r.Error}");
        var meta = (int)r.Long("error", "whatsappApiError", "code");
        if (meta == 0) meta = (int)r.Long("whatsappApiError", "code");
        var message = First(r.Str("error", "whatsappApiError", "message"), r.Str("error", "message"),
            r.Str("message"), r.Str("errorMessage"));
        var named = First(r.Str("error", "code"), r.Str("code"));
        int? code = meta > 0 ? meta
            : r.Status == 401 ? 190                                    // key refused → disconnected
            : r.Status == 403 ? 10                                     // no permission for this number
            : r.Status == 429 ? 130429                                 // throttled → degraded
            : r.Status == 402 || named.Contains("BALANCE", StringComparison.OrdinalIgnoreCase) ? 131042
            : null;
        var why = message.Length > 0 ? message : named.Length > 0 ? named : $"HTTP {r.Status}";
        return (code, $"YCloud {(meta > 0 ? meta.ToString() : r.Status.ToString())}: {why}");
    }

    private static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    private static SendOutcome Fail(HttpReply r)
    {
        var (code, detail) = Error(r);
        return new SendOutcome(false, "", detail, code);
    }

    // ---- messages -------------------------------------------------------------------

    private async Task<SendOutcome> PostMessageAsync(ChannelAccount a, Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        if (!IsConfigured(a)) return NotConfigured();
        payload["from"] = WhatsAppProviders.E164(a.ExternalId);
        var r = await SendAsync(Req(HttpMethod.Post, $"{ApiBase}/whatsapp/messages/sendDirectly", a,
            ProviderHttp.JsonBody(payload)), a, ct);
        if (!r.Is2xx) return Fail(r);
        // sendDirectly answers the message as it stands: "failed" carries Meta's reason
        if (r.Str("status").Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            var meta = (int)r.Long("whatsappApiError", "code");
            if (meta == 0 && int.TryParse(r.Str("errorCode"), out var parsed)) meta = parsed;
            var why = First(r.Str("errorMessage"), r.Str("whatsappApiError", "message"), "message failed");
            return new SendOutcome(false, "", $"YCloud {(meta > 0 ? meta.ToString() : "failed")}: {why}",
                meta > 0 ? meta : null);
        }
        var id = First(r.Str("wamid"), r.Str("id"));
        return new SendOutcome(true, id, "");
    }

    private static Dictionary<string, object?> Message(string to, string type, object? body)
    {
        var d = new Dictionary<string, object?> { ["to"] = WhatsAppProviders.E164(to), ["type"] = type };
        if (body != null) d[type] = body;
        return d;
    }

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) =>
        PostMessageAsync(account, Message(toWaId, "text", new { body = text, preview_url = false }), ct);

    public Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text,
        string contextWamid, CancellationToken ct = default)
    {
        var payload = Message(toWaId, "text", new { body = text, preview_url = false });
        if (!string.IsNullOrWhiteSpace(contextWamid)) payload["context"] = new { message_id = contextWamid };
        return PostMessageAsync(account, payload, ct);
    }

    public Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default) =>
        PostMessageAsync(account, Message(toWaId, "reaction", new { message_id = wamid, emoji = emoji ?? "" }), ct);

    public Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default) =>
        PostMessageAsync(account, Message(toWaId, "template", template), ct);

    public async Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime,
        string filename, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return NotConfigured();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
        form.Add(file, "file", string.IsNullOrWhiteSpace(filename) ? "upload" : filename);
        var phone = Uri.EscapeDataString(WhatsAppProviders.E164(account.ExternalId));
        var r = await SendAsync(Req(HttpMethod.Post, $"{ApiBase}/whatsapp/media/{phone}/upload", account, form),
            account, ct);
        if (!r.Is2xx) return Fail(r);
        var id = r.Str("id");
        return id.Length > 0 ? new SendOutcome(true, id, "") : new SendOutcome(false, "", "YCloud returned no media id");
    }

    /// <summary><paramref name="mediaId"/> is an uploaded media id, or a public https link.</summary>
    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default)
    {
        var media = new Dictionary<string, object?>();
        if (mediaId.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) media["link"] = mediaId;
        else media["id"] = mediaId;
        if (type != "audio" && !string.IsNullOrWhiteSpace(caption)) media["caption"] = caption;
        if (type == "document" && !string.IsNullOrWhiteSpace(filename)) media["filename"] = filename;
        return PostMessageAsync(account, Message(toWaId, type, media), ct);
    }

    /// <summary>Inbound media: the event's download <c>link</c> (the key rides only to *.ycloud.com).</summary>
    public async Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(mediaId, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new MediaDownload(false, Array.Empty<byte>(), "", "YCloud gave no download link");
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host.Equals("ycloud.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".ycloud.com", StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation(KeyHeader, account.AccessToken);
        try
        {
            using var resp = await ProviderHttp.Client(_http).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new MediaDownload(false, Array.Empty<byte>(), "", $"download failed (HTTP {(int)resp.StatusCode})");
            var bytes = await MetaGraph.ReadCappedAsync(resp.Content, maxBytes, ct);
            if (bytes == null) return new MediaDownload(false, Array.Empty<byte>(), "", "file too large");
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (mime.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new MediaDownload(false, Array.Empty<byte>(), "", "YCloud answered with an error instead of the file");
            return new MediaDownload(true, bytes, mime, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new MediaDownload(false, Array.Empty<byte>(), "", "download failed");
        }
        finally
        {
            req.Dispose();
        }
    }

    // ---- templates ------------------------------------------------------------------

    /// <summary>Every template of the WABA, as Meta-shaped objects (id = <c>name:language</c>).</summary>
    public async Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account) || account.WabaId.Length == 0)
            return new GraphRead(false, default, "no WABA id set on this YCloud account");
        var all = new List<JsonElement>();
        for (var page = 1; page <= MaxTemplatePages; page++)
        {
            var url = $"{ApiBase}/whatsapp/templates?filter.wabaId={Uri.EscapeDataString(account.WabaId)}"
                      + $"&page={page}&limit=100&includeTotal=true";
            var r = await SendAsync(Req(HttpMethod.Get, url, account), account, ct);
            if (!r.Is2xx)
            {
                var (code, detail) = Error(r);
                return new GraphRead(false, default, detail, code);
            }
            var items = ProviderHttp.At(r.Json, "items");
            if (items.ValueKind != JsonValueKind.Array) break;
            foreach (var t in items.EnumerateArray()) all.Add(ToMeta(t));
            var total = r.Long("total");
            if (items.GetArrayLength() < 100 || (total > 0 && all.Count >= total)) break;
        }
        return new GraphRead(true, JsonSerializer.SerializeToElement(all), "");
    }

    /// <summary>A YCloud template → the Graph message_templates item shape the catalog parses.</summary>
    public static JsonElement ToMeta(JsonElement t)
    {
        var name = ProviderHttp.Str(t, "name");
        var language = ProviderHttp.Str(t, "language");
        var node = new JsonObject
        {
            ["id"] = $"{name}:{language}",
            ["name"] = name,
            ["language"] = language,
            ["category"] = ProviderHttp.Str(t, "category"),
            ["status"] = ProviderHttp.Str(t, "status"),
        };
        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("components", out var comps))
            node["components"] = JsonNode.Parse(comps.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    public Task<GraphRead> CreateTemplateAsync(ChannelAccount account, object payload, CancellationToken ct = default)
    {
        if (!IsConfigured(account) || account.WabaId.Length == 0)
            return Task.FromResult(new GraphRead(false, default, "no WABA id set on this YCloud account"));
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["wabaId"] = account.WabaId;
        return TemplateCallAsync(account, HttpMethod.Post, $"{ApiBase}/whatsapp/templates", node, ct);
    }

    public Task<GraphRead> EditTemplateAsync(ChannelAccount account, string templateId, object payload,
        CancellationToken ct = default)
    {
        var (name, language) = SplitId(templateId);
        if (!IsConfigured(account) || account.WabaId.Length == 0 || name.Length == 0 || language.Length == 0)
            return Task.FromResult(new GraphRead(false, default, "YCloud edits a template by name and language"));
        return TemplateCallAsync(account, HttpMethod.Patch,
            $"{ApiBase}/whatsapp/templates/{Uri.EscapeDataString(account.WabaId)}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(language)}",
            JsonSerializer.SerializeToNode(payload), ct);
    }

    public Task<GraphRead> DeleteTemplateAsync(ChannelAccount account, string name, string? templateId,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account) || account.WabaId.Length == 0)
            return Task.FromResult(new GraphRead(false, default, "no WABA id set on this YCloud account"));
        var (_, language) = SplitId(templateId);
        var url = $"{ApiBase}/whatsapp/templates/{Uri.EscapeDataString(account.WabaId)}/{Uri.EscapeDataString(name)}"
                  + (language.Length > 0 ? $"/{Uri.EscapeDataString(language)}" : "");
        return TemplateCallAsync(account, HttpMethod.Delete, url, null, ct);
    }

    private async Task<GraphRead> TemplateCallAsync(ChannelAccount a, HttpMethod method, string url, JsonNode? body,
        CancellationToken ct)
    {
        var content = body == null ? null
            : new StringContent(body.ToJsonString(ProviderHttp.Json), Encoding.UTF8, "application/json");
        var r = await SendAsync(Req(method, url, a, content), a, ct);
        if (r.Is2xx) return new GraphRead(true, r.Json.ValueKind == JsonValueKind.Undefined ? default : r.Json.Clone(), "");
        var (code, detail) = Error(r);
        return new GraphRead(false, default, detail, code);
    }

    private static (string Name, string Language) SplitId(string? id)
    {
        var s = id ?? "";
        var i = s.LastIndexOf(':');
        return i <= 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }

    // ---- the number / health ----------------------------------------------------------

    /// <summary>The number's profile in Graph's field names (display_phone_number, verified_name, quality_rating).</summary>
    public async Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new GraphRead(false, default, "YCloud account not configured");
        var phone = WhatsAppProviders.E164(account.ExternalId);
        var url = account.WabaId.Length > 0
            ? $"{ApiBase}/whatsapp/phoneNumbers/{Uri.EscapeDataString(account.WabaId)}/{Uri.EscapeDataString(phone)}"
            : $"{ApiBase}/balance";
        var r = await SendAsync(Req(HttpMethod.Get, url, account), account, ct);
        if (!r.Is2xx)
        {
            var (code, detail) = Error(r);
            return new GraphRead(false, default, detail, code);
        }
        return new GraphRead(true, JsonSerializer.SerializeToElement(new
        {
            display_phone_number = First(r.Str("displayPhoneNumber"), phone),
            verified_name = r.Str("verifiedName"),
            quality_rating = r.Str("qualityRating"),
            status = r.Str("status"),
        }), "");
    }

    public async Task<BspProbe?> ProbeBspAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return null;
        var r = await GetPhoneNumberAsync(account, ct);
        if (!r.Ok)
        {
            var verdict = r.ErrorCode is 190 or 10 ? new ProbeVerdict(Health.Disconnected, r.Detail)
                : r.ErrorCode == 131042 ? new ProbeVerdict(Health.Degraded, r.Detail)
                : new ProbeVerdict(null, r.Detail, Transient: true);
            return new BspProbe(verdict);
        }
        var number = MetaGraph.Str(r.Body, "display_phone_number");
        var name = MetaGraph.Str(r.Body, "verified_name");
        var quality = MetaGraph.Str(r.Body, "quality_rating");
        var status = MetaGraph.Str(r.Body, "status").ToUpperInvariant();
        var v = status switch
        {
            "BANNED" or "RESTRICTED" => new ProbeVerdict(Health.Banned, $"YCloud reports the number as {status.ToLowerInvariant()}"),
            "DISCONNECTED" or "DELETED" or "UNVERIFIED" => new ProbeVerdict(Health.Disconnected,
                $"YCloud reports the number as {status.ToLowerInvariant()}"),
            _ when quality.Equals("RED", StringComparison.OrdinalIgnoreCase) =>
                new ProbeVerdict(Health.Degraded, "quality rating RED — Meta may limit this number"),
            _ => new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"),
        };
        return new BspProbe(v, number, name, quality);
    }

    // ---- webhook signature ------------------------------------------------------------

    /// <summary><c>YCloud-Signature: t=&lt;unix&gt;,s=&lt;hex HMAC-SHA256(secret, "t.body")&gt;</c>.</summary>
    public static string Sign(string secret, long unix, byte[] body) => $"t={unix},s={Digest(secret, unix, body)}";

    private static string Digest(string secret, long unix, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{unix}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        return ProviderHttp.Hmac256Hex(secret, signed);
    }

    /// <summary>A valid, fresh signature from the endpoint's secret (constant-time compare).</summary>
    public static bool VerifySignature(string secret, byte[] body, string? header, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header)) return false;
        long t = 0;
        var sigs = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var k = kv[0].Trim();
            var v = kv[1].Trim();
            if (k == "t") long.TryParse(v, out t);
            else if (k == "s") sigs.Add(v);
        }
        if (t <= 0 || sigs.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(t)).Duration() > Freshness) return false;
        var expected = Digest(secret, t, body);
        var ok = false;
        foreach (var s in sigs) ok |= ProviderHttp.SameText(expected, s.ToLowerInvariant());
        return ok;
    }
}
