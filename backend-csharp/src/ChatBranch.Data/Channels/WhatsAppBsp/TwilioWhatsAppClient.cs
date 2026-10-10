using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// WhatsApp through Twilio (Programmable Messaging + the Content API), behind the same
/// seam as Meta's Cloud API (<see cref="IWhatsAppCloudClient"/>):
/// <list type="bullet">
///   <item>send — <c>POST /2010-04-01/Accounts/{sid}/Messages.json</c> with
///         <c>To=whatsapp:+…</c> and <c>From=whatsapp:+…</c> (or <c>MessagingServiceSid</c>),
///         <c>Body</c>; media by <c>MediaUrl</c> — Twilio FETCHES the file, so the caller hands a
///         public, signed, short-lived link (<see cref="MediaLinks"/>); templates by
///         <c>ContentSid</c> + <c>ContentVariables</c> (JSON). A per-message <c>StatusCallback</c>
///         is added when the account knows its public webhook URL. Auth: Basic with the
///         Account SID + Auth Token, or an API Key SID + secret;</item>
///   <item>not supported by Twilio's WhatsApp API: quoted replies (sent unquoted — never a
///         failure) and reactions (refused before this client is called, a 409);</item>
///   <item>templates — the Content API (<c>GET content.twilio.com/v1/ContentAndApprovals</c>),
///         listed as Meta-shaped items (id = the ContentSid HX…, name = the friendly name,
///         the body text with its <c>{{1}}</c> variables, WhatsApp approval status/category).
///         Read-only here: create / edit in the Twilio console;</item>
///   <item>health — <c>GET /Accounts/{sid}.json</c> (active / suspended / closed), like SMS.</item>
/// </list>
/// Twilio's error codes are translated to the Meta code that means the same thing
/// (63016 outside the window → 131047; 20003 auth → 190; throttles → 130429; an
/// unreachable recipient → 131026…) so <see cref="MetaErrors"/> moves account health
/// only for account-level problems. Built against Twilio's public docs; verified here
/// against fakes only.
/// </summary>
public sealed partial class TwilioWhatsAppClient : IWhatsAppCloudClient
{
    public const string ApiBase = TwilioSmsTransport.ApiBase;
    public const string ContentBase = "https://content.twilio.com/v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const int MaxContentPages = 10;

    private static readonly ConcurrentDictionary<string, (DateTime At, List<JsonElement> Items)> ContentCache = new();

    [GeneratedRegex("^HX[0-9a-fA-F]{32}$")]
    private static partial Regex ContentSidRe();

    private readonly IHttpClientFactory _http;

    public TwilioWhatsAppClient(IHttpClientFactory http) => _http = http;

    public bool IsConfigured(ChannelAccount account) =>
        WhatsAppProviders.Of(account) == Providers.Twilio && WhatsAppProviders.Configured(account);

    private static string Sid(ChannelAccount a) => WhatsAppProviders.Cfg(a, "account_sid");

    /// <summary>Basic auth: an API Key SID + secret when both are set, else Account SID + Auth Token.</summary>
    public static AuthenticationHeaderValue Auth(ChannelAccount a)
    {
        var keySid = WhatsAppProviders.Cfg(a, "api_key_sid");
        var keySecret = WhatsAppProviders.Cfg(a, "api_key_secret");
        return keySid.Length > 0 && keySecret.Length > 0
            ? ProviderHttp.Basic(keySid, keySecret)
            : ProviderHttp.Basic(Sid(a), a.AccessToken);
    }

    private static string[] Secrets(ChannelAccount a) =>
        new[] { a.AccessToken, WhatsAppProviders.Cfg(a, "api_key_secret") };

    /// <summary>"whatsapp:+60…" — Twilio's address form for the WhatsApp channel.</summary>
    public static string Address(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)) s = s[9..];
        var e164 = WhatsAppProviders.E164(s);
        return e164.Length == 0 ? "" : $"whatsapp:{e164}";
    }

    private Task<HttpReply> SendAsync(HttpRequestMessage req, ChannelAccount a, CancellationToken ct)
    {
        req.Headers.Authorization = Auth(a);
        return ProviderHttp.SendAsync(_http, req, ct, Secrets(a));
    }

    // ---- errors -----------------------------------------------------------------------

    /// <summary>Twilio's code → the Meta code with the same meaning (null = leave health alone).</summary>
    public static int? MetaEquivalent(int twilioCode, int httpStatus = 0) => twilioCode switch
    {
        63016 => MetaErrors.WindowClosed,                 // outside the 24h window — use a template
        20003 or 20404 or 20401 => 190,                   // credentials refused / unknown account
        20005 or 10001 or 30002 => 368,                   // account suspended / not active
        63007 or 21606 or 21212 => 33,                    // the From isn't ours / not a WhatsApp sender
        63018 or 20429 or 14107 or 30022 or 63038 => 130429, // throttled
        63003 or 63024 or 21211 or 21614 or 30003 or 30005 or 30006 => 131026, // recipient unreachable
        21610 or 30004 => 131050,                         // recipient blocked / unsubscribed
        63032 => 130472,                                  // recipient in an experiment
        63049 => 131049,                                  // Meta chose not to deliver
        63005 or 63013 => 131051,                         // unsupported content for the channel
        63020 or 63021 or 63022 or 63027 or 63028 => 132000, // template problems
        _ when httpStatus == 401 => 190,
        _ when httpStatus == 429 => 130429,
        _ => null,
    };

    public static (int? Code, string Detail) Error(HttpReply r)
    {
        if (r.Status == 0) return (null, $"Twilio unreachable: {r.Error}");
        var code = (int)r.Long("code");
        var message = r.Str("message");
        return (MetaEquivalent(code, r.Status),
            $"Twilio {(code > 0 ? code.ToString() : r.Status.ToString())}: {(message.Length > 0 ? message : "error")}");
    }

    // ---- messages ---------------------------------------------------------------------

    private async Task<SendOutcome> PostMessageAsync(ChannelAccount a, string to,
        List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        if (!IsConfigured(a)) return new SendOutcome(false, "", "Twilio account not configured");
        form.Insert(0, new("To", Address(to)));
        var service = WhatsAppProviders.Cfg(a, "messaging_service_sid");
        var from = WhatsAppProviders.Cfg(a, "from_number");
        if (from.Length > 0) form.Add(new("From", Address(from)));
        else form.Add(new("MessagingServiceSid", service));
        var callback = WhatsAppProviders.Cfg(a, "status_callback");
        if (callback.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) form.Add(new("StatusCallback", callback));
        var r = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}/Accounts/{Uri.EscapeDataString(Sid(a))}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form),
        }, a, ct);
        if (!r.Is2xx)
        {
            var (code, detail) = Error(r);
            return new SendOutcome(false, "", detail, code);
        }
        return new SendOutcome(true, r.Str("sid"), "");
    }

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) =>
        PostMessageAsync(account, toWaId, new() { new("Body", text) }, ct);

    /// <summary>Twilio's WhatsApp API has no quoted replies: the text goes out unquoted.</summary>
    public Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text,
        string contextWamid, CancellationToken ct = default) =>
        SendTextAsync(account, toWaId, text, ct);

    public Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "Reactions aren't supported by Twilio's WhatsApp API."));

    /// <summary>Twilio fetches media itself — there is no upload; the caller passes a public link.</summary>
    public Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default) =>
        Task.FromResult(new SendOutcome(false, "", "Twilio takes media by public URL only"));

    /// <summary><paramref name="mediaId"/> is a PUBLIC https URL Twilio fetches.</summary>
    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default)
    {
        if (!mediaId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new SendOutcome(false, "", "Twilio needs a public https media URL"));
        var form = new List<KeyValuePair<string, string>> { new("MediaUrl", mediaId) };
        if (!string.IsNullOrWhiteSpace(caption) && type != "audio") form.Add(new("Body", caption!));
        return PostMessageAsync(account, toWaId, form, ct);
    }

    /// <summary>
    /// A template (the catalog's Meta-shaped object): the ContentSid is the name itself when it
    /// IS one (HX…), else the Content template with that friendly name (+ language); body
    /// parameters become ContentVariables <c>{"1": …}</c> (a named parameter keeps its name).
    /// </summary>
    public async Task<SendOutcome> SendTemplateAsync(ChannelAccount account, string toWaId, object template,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new SendOutcome(false, "", "Twilio account not configured");
        var t = JsonSerializer.SerializeToElement(template);
        var name = ProviderHttp.Str(t, "name");
        var language = ProviderHttp.Str(t, "language", "code");
        var sid = ContentSidRe().IsMatch(name) ? name : await ResolveContentSidAsync(account, name, language, ct);
        if (sid == null)
            return new SendOutcome(false, "", $"No Twilio Content template named '{name}' — create and approve it in the Twilio console.",
                132001);
        var variables = new Dictionary<string, string>();
        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("components", out var comps)
            && comps.ValueKind == JsonValueKind.Array)
            foreach (var c in comps.EnumerateArray())
            {
                if (!ProviderHttp.Str(c, "type").Equals("body", StringComparison.OrdinalIgnoreCase)
                    || !c.TryGetProperty("parameters", out var ps) || ps.ValueKind != JsonValueKind.Array) continue;
                var i = 0;
                foreach (var p in ps.EnumerateArray())
                {
                    i++;
                    var key = ProviderHttp.Str(p, "parameter_name");
                    variables[key.Length > 0 ? key : i.ToString()] = ProviderHttp.Str(p, "text");
                }
            }
        var form = new List<KeyValuePair<string, string>> { new("ContentSid", sid) };
        if (variables.Count > 0) form.Add(new("ContentVariables", JsonSerializer.Serialize(variables, ProviderHttp.Json)));
        return await PostMessageAsync(account, toWaId, form, ct);
    }

    private async Task<string?> ResolveContentSidAsync(ChannelAccount a, string name, string language,
        CancellationToken ct)
    {
        var list = await ContentAsync(a, fresh: false, ct);
        if (list == null) return null;
        var named = list.Where(x => ProviderHttp.Str(x, "name") == name).ToList();
        if (named.Count == 0) return null;
        var hit = named.FirstOrDefault(x => ProviderHttp.Str(x, "language").Equals(language, StringComparison.OrdinalIgnoreCase));
        if (hit.ValueKind != JsonValueKind.Object) hit = named[0];
        return ProviderHttp.Str(hit, "id") is { Length: > 0 } sid ? sid : null;
    }

    /// <summary>Inbound media: Twilio-hosted URLs only (our credentials never go anywhere else).</summary>
    public async Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(mediaId, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "api.twilio.com" || uri.Host.EndsWith(".twilio.com", StringComparison.OrdinalIgnoreCase)))
            return new MediaDownload(false, Array.Empty<byte>(), "", "not a Twilio media URL");
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = Auth(account);
        try
        {
            using var resp = await ProviderHttp.Client(_http).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new MediaDownload(false, Array.Empty<byte>(), "", $"download failed (HTTP {(int)resp.StatusCode})");
            var bytes = await MetaGraph.ReadCappedAsync(resp.Content, maxBytes, ct);
            if (bytes == null) return new MediaDownload(false, Array.Empty<byte>(), "", "file too large");
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (mime.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new MediaDownload(false, Array.Empty<byte>(), "", "Twilio answered with an error instead of the file");
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

    // ---- templates (Content API, read-only) --------------------------------------------

    public async Task<GraphRead> ListTemplatesAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new GraphRead(false, default, "Twilio account not configured");
        var list = await ContentAsync(account, fresh: true, ct);
        return list == null
            ? new GraphRead(false, default, _lastContentError.TryGetValue(account.Id, out var why) ? why : "Twilio Content API listing failed")
            : new GraphRead(true, JsonSerializer.SerializeToElement(list), "");
    }

    private readonly ConcurrentDictionary<int, string> _lastContentError = new();

    private static string CacheKey(ChannelAccount a) =>
        $"{a.Id}|{Sid(a)}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(a.AccessToken + WhatsAppProviders.Cfg(a, "api_key_secret"))))[..16]}";

    /// <summary>The account's Content templates as Meta-shaped items (cached 5 minutes); null = listing failed.</summary>
    private async Task<List<JsonElement>?> ContentAsync(ChannelAccount a, bool fresh, CancellationToken ct)
    {
        var key = CacheKey(a);
        if (!fresh && ContentCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl) return hit.Items;
        var items = new List<JsonElement>();
        string? url = $"{ContentBase}/ContentAndApprovals?PageSize=100";
        for (var page = 0; url != null && page < MaxContentPages; page++)
        {
            var r = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), a, ct);
            if (!r.Is2xx)
            {
                _lastContentError[a.Id] = Error(r).Detail;
                return null;
            }
            var contents = ProviderHttp.At(r.Json, "contents");
            if (contents.ValueKind == JsonValueKind.Array)
                foreach (var c in contents.EnumerateArray()) items.Add(ToMeta(c));
            var next = r.Str("meta", "next_page_url");
            url = Uri.TryCreate(next, UriKind.Absolute, out var nu) && nu.Scheme == Uri.UriSchemeHttps
                  && nu.Host == "content.twilio.com" ? next : null;
        }
        ContentCache[key] = (DateTime.UtcNow, items);
        return items;
    }

    /// <summary>One Content API item → a Graph message_templates item (BODY + BUTTONS).</summary>
    public static JsonElement ToMeta(JsonElement c)
    {
        var types = ProviderHttp.At(c, "types");
        string body = "";
        var buttons = new JsonArray();
        string headerFormat = "";
        if (types.ValueKind == JsonValueKind.Object)
        {
            // the plain text first, then whichever rich type carries a body
            foreach (var preferred in new[] { "twilio/text", "twilio/media", "twilio/quick-reply", "twilio/call-to-action",
                         "twilio/card", "twilio/list-picker", "whatsapp/card", "whatsapp/authentication" })
                if (body.Length == 0 && types.TryGetProperty(preferred, out var tt))
                    body = ProviderHttp.Str(tt, "body") is { Length: > 0 } b ? b : ProviderHttp.Str(tt, "title");
            foreach (var p in types.EnumerateObject())
            {
                if (body.Length == 0) body = ProviderHttp.Str(p.Value, "body");
                if (p.Name == "twilio/media") headerFormat = "IMAGE";
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("actions", out var acts)
                    && acts.ValueKind == JsonValueKind.Array)
                    foreach (var act in acts.EnumerateArray())
                    {
                        var kind = ProviderHttp.Str(act, "type").ToUpperInvariant();
                        buttons.Add(new JsonObject
                        {
                            ["type"] = kind is "URL" ? "URL" : kind is "PHONE_NUMBER" ? "PHONE_NUMBER" : "QUICK_REPLY",
                            ["text"] = ProviderHttp.Str(act, "title"),
                            ["url"] = ProviderHttp.Str(act, "url"),
                            ["phone_number"] = ProviderHttp.Str(act, "phone"),
                        });
                    }
            }
        }
        var approval = ProviderHttp.At(c, "approval_requests");
        var status = ProviderHttp.Str(approval, "status");
        if (status.Length == 0) status = ProviderHttp.Str(approval, "whatsapp", "status");
        var category = ProviderHttp.Str(approval, "category");
        if (category.Length == 0) category = ProviderHttp.Str(approval, "whatsapp", "category");
        var components = new JsonArray();
        if (headerFormat.Length > 0) components.Add(new JsonObject { ["type"] = "HEADER", ["format"] = headerFormat });
        components.Add(new JsonObject { ["type"] = "BODY", ["text"] = body });
        if (buttons.Count > 0) components.Add(new JsonObject { ["type"] = "BUTTONS", ["buttons"] = buttons });
        var node = new JsonObject
        {
            ["id"] = ProviderHttp.Str(c, "sid"),
            ["name"] = ProviderHttp.Str(c, "friendly_name") is { Length: > 0 } fn ? fn : ProviderHttp.Str(c, "sid"),
            ["language"] = ProviderHttp.Str(c, "language") is { Length: > 0 } l ? l : "en",
            // not submitted to WhatsApp = usable inside the 24h window only; say so honestly
            ["status"] = status.Length > 0 ? status.ToUpperInvariant() : "UNSUBMITTED",
            ["category"] = category.ToUpperInvariant(),
            ["components"] = components,
        };
        return JsonSerializer.SerializeToElement(node);
    }

    // ---- the account / health -----------------------------------------------------------

    public async Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new GraphRead(false, default, "Twilio account not configured");
        var r = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/Accounts/{Uri.EscapeDataString(Sid(account))}.json"), account, ct);
        if (!r.Is2xx)
        {
            var (code, detail) = Error(r);
            return new GraphRead(false, default, detail, code);
        }
        var from = WhatsAppProviders.Cfg(account, "from_number");
        return new GraphRead(true, JsonSerializer.SerializeToElement(new
        {
            display_phone_number = from.Length > 0 ? WhatsAppProviders.E164(from)
                : WhatsAppProviders.Cfg(account, "messaging_service_sid"),
            verified_name = r.Str("friendly_name"),
            quality_rating = r.Str("status"),
            status = r.Str("status"),
        }), "");
    }

    public async Task<BspProbe?> ProbeBspAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return null;
        var r = await GetPhoneNumberAsync(account, ct);
        if (!r.Ok)
            return new BspProbe(r.ErrorCode == 190
                ? new ProbeVerdict(Health.Disconnected, r.Detail)
                : new ProbeVerdict(null, r.Detail, Transient: true));
        var name = MetaGraph.Str(r.Body, "verified_name");
        var number = MetaGraph.Str(r.Body, "display_phone_number");
        var status = MetaGraph.Str(r.Body, "status");
        var v = status switch
        {
            "suspended" => new ProbeVerdict(Health.Banned, "Twilio account suspended"),
            "closed" => new ProbeVerdict(Health.Disconnected, "Twilio account closed"),
            _ => new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"),
        };
        return new BspProbe(v, number, name, status);
    }
}
