using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Where a WAHA account keeps the three things the operator must tell us: the base URL of
/// THEIR WAHA, the session name inside it, and (for display only) which engine that WAHA
/// runs. The API key lives on <see cref="ChannelAccount.AccessToken"/> and the webhook HMAC
/// key on <see cref="ChannelAccount.AppSecret"/>.
/// </summary>
public static class WahaConfig
{
    public const string BaseUrlKey = "base_url";
    public const string SessionKey = "session";
    public const string EngineKey = "engine";

    /// <summary>WAHA's own default session name.</summary>
    public const string DefaultSession = "default";

    /// <summary>
    /// The engines WAHA ships (docs: "Engines"): WEBJS (its default, a real Chromium through
    /// Puppeteer), WPP (also a browser), NOWEB and GOWS (websocket, no browser). The engine is
    /// chosen on the WAHA side with <c>WHATSAPP_DEFAULT_ENGINE</c> — NOT per session, and not
    /// by us — so this value is the operator telling us what they run. ChatBranch calls only
    /// endpoints every engine supports, so nothing here depends on it.
    /// </summary>
    public static readonly string[] Engines = { "WEBJS", "WPP", "NOWEB", "GOWS" };

    public static string BaseUrl(ChannelAccount a) =>
        (WhatsAppProviders.Cfg(a, BaseUrlKey) ?? "").Trim().TrimEnd('/');

    public static string Session(ChannelAccount a) =>
        (WhatsAppProviders.Cfg(a, SessionKey) ?? "").Trim() is { Length: > 0 } s ? s : DefaultSession;

    public static string Engine(ChannelAccount a) =>
        (WhatsAppProviders.Cfg(a, EngineKey) ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// An absolute http(s) address with a host — nothing else. WAHA is usually on the
    /// operator's own network (<c>http://waha:3000</c>), so a private host is fine here;
    /// this is not a fetch of customer-supplied content.
    /// </summary>
    public static bool ValidBaseUrl(string? raw, out string clean)
    {
        clean = (raw ?? "").Trim().TrimEnd('/');
        return clean.Length > 0
               && Uri.TryCreate(clean, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
               && u.Host.Length > 0;
    }

    /// <summary>A session name WAHA will accept as a path segment (its own ids are plain words).</summary>
    public static bool ValidSession(string? raw, out string clean)
    {
        clean = (raw ?? "").Trim();
        if (clean.Length == 0) clean = DefaultSession;
        return clean.Length <= 64 && clean.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    public static bool ValidEngine(string? raw, out string clean)
    {
        clean = (raw ?? "").Trim().ToUpperInvariant();
        return clean.Length == 0 || Array.IndexOf(Engines, clean) >= 0;
    }
}

/// <summary>
/// WAHA's session statuses, exactly as its <c>WAHASessionStatus</c> enum spells them
/// (src/structures/enums.dto.ts).
/// </summary>
public static class WahaStatus
{
    public const string Stopped = "STOPPED";
    public const string Starting = "STARTING";
    public const string ScanQrCode = "SCAN_QR_CODE";
    public const string PasskeyRequired = "PASSKEY_REQUIRED";
    public const string PasskeyConfirmationRequired = "PASSKEY_CONFIRMATION_REQUIRED";
    public const string Working = "WORKING";
    public const string Failed = "FAILED";
    /// <summary>Not one of WAHA's: we could not read a status at all (unreachable, refused).</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>
    /// A status that is the pairing flow WORKING AS INTENDED, not a fault: the session is
    /// coming up, waiting for a scan, or waiting for the person to confirm a passkey on the
    /// phone. These must never be recorded as a health failure and must never be shown as a
    /// hard error in the pairing UI.
    /// </summary>
    public static bool Transient(string? status) => status is Starting or ScanQrCode
        or PasskeyRequired or PasskeyConfirmationRequired;
}

/// <summary>One read of a WAHA session: its status, what the person has to do, and who it is.</summary>
/// <param name="Status">A <see cref="WahaStatus"/> value.</param>
/// <param name="Qr">A <c>data:image/png;base64,…</c> QR to render, when one is waiting.</param>
/// <param name="PairingCode">The 8-character code WhatsApp issued for a phone-number pairing.</param>
/// <param name="PairingPhone">The number that code was issued for (digits).</param>
/// <param name="ConfirmCode">PASSKEY_CONFIRMATION_REQUIRED: the number to CONFIRM on the phone —
/// a different instruction from a pairing code, so it never shares that field.</param>
/// <param name="HookUrl">The webhook URL the live session is configured with, "" when WAHA
/// returned no config (so "is it ours?" is unknown rather than false).</param>
/// <param name="Me">The paired number's digits (WAHA's <c>me.id</c> without its JID suffix).</param>
/// <param name="MeName">Its WhatsApp profile name (<c>me.pushName</c>).</param>
/// <param name="Detail">Why, in one line, when something is wrong. Never a secret.</param>
/// <param name="HttpStatus">WAHA's HTTP status for the read; 0 = never answered.</param>
public sealed record WahaSession(string Status, string Qr = "", string PairingCode = "", string PairingPhone = "",
    string Me = "", string MeName = "", string Detail = "", int HttpStatus = 0, string ConfirmCode = "",
    string HookUrl = "")
{
    public bool Working => Status == WahaStatus.Working;
    public bool Reachable => HttpStatus > 0;
}

/// <summary>
/// WhatsApp through <b>WAHA</b> (<c>github.com/devlikeapro/waha</c>, docs
/// <c>waha.devlike.pro</c>) — a self-hosted WhatsApp HTTP API the OPERATOR runs and we are
/// merely pointed at. It drives WhatsApp Web, so it is <b>UNOFFICIAL</b>: the same category
/// as Baileys, never a Meta Business Solution Provider. No templates, no 24-hour window, no
/// reminder role — and using it may breach WhatsApp's Terms of Service, with a real risk of
/// the number being banned. Unlike Baileys it is NOT a ChatBranch sidecar: no container of
/// ours, no <c>docker compose</c> entry, just a base URL + session name + API key.
///
/// What this client calls (verified against WAHA's current docs and source, 2026-11):
/// <list type="bullet">
///   <item>auth — every request carries <c>X-Api-Key: &lt;WAHA_API_KEY&gt;</c> when the
///         operator set one; a WAHA started without that env var needs no header. Base path
///         is <c>/api/…</c>;</item>
///   <item>sessions — <c>GET /api/sessions/{s}</c> (status + <c>me</c>),
///         <c>POST /api/sessions</c> <c>{name, start, config}</c>, <c>PUT /api/sessions/{s}</c>
///         (config), <c>POST /api/sessions/{s}/start|stop|logout</c>. Statuses:
///         STOPPED · STARTING · SCAN_QR_CODE · PASSKEY_REQUIRED ·
///         PASSKEY_CONFIRMATION_REQUIRED · WORKING · FAILED;</item>
///   <item>pairing — <c>GET /api/{s}/auth/qr</c> (<c>Accept: application/json</c> →
///         <c>{mimetype, data}</c> base64 PNG; <c>?format=raw</c> → <c>{value}</c>) and
///         <c>POST /api/{s}/auth/request-code</c> <c>{phoneNumber}</c> → <c>{code}</c>.
///         A QR expires (60 s for the first, 20 s after, 6 in all), so the panel re-reads it;</item>
///   <item>sending — <c>POST /api/sendText</c> <c>{session, chatId, text, reply_to?}</c>,
///         <c>PUT /api/reaction</c> <c>{session, messageId, reaction}</c>, and
///         <c>sendImage|sendVideo|sendVoice|sendFile|sendSticker</c> with
///         <c>file: {mimetype, data|url, filename}</c>. <c>chatId</c> is
///         <c>&lt;digits&gt;@c.us</c>;</item>
///   <item>inbound — the <c>message</c> event (and <c>session.status</c>) posted to the
///         per-account webhook we configure on the session, signed
///         <c>X-Webhook-Hmac</c> = hex HMAC-SHA512 of the raw body
///         (<c>X-Webhook-Hmac-Algorithm: sha512</c>);</item>
///   <item>media — an inbound file is <c>payload.media.url</c> on WAHA's own host, fetched
///         with the API key only when the host is the configured one.</item>
/// </list>
///
/// <b>Tier.</b> WAHA Plus was folded into WAHA Core in 2026.6.1 ("Plus is now Core": one
/// <c>devlikeapro/waha</c> image, "unlimited sessions, multimedia messages, all storages,
/// built-in security"), so nothing ChatBranch uses sits behind a paywall. An operator still
/// running a pre-2026.6 image has media and multiple sessions on the paid build — which the
/// setup UI says, rather than letting a media send fail mysteriously.
///
/// Built from WAHA's published API and tested against fakes of it, never against a live
/// WAHA instance.
/// </summary>
public sealed class WahaWhatsAppClient : IWhatsAppCloudClient
{
    /// <summary>WAHA's API-key header (docs → Security): <c>X-Api-Key</c>.</summary>
    public const string KeyHeader = "X-Api-Key";
    /// <summary>The webhook signature header WAHA sends when <c>hmac.key</c> is set.</summary>
    public const string SignatureHeader = "X-Webhook-Hmac";
    /// <summary>WAHA names the algorithm beside the signature; it only ever sends sha512.</summary>
    public const string AlgorithmHeader = "X-Webhook-Hmac-Algorithm";
    public const string Algorithm = "sha512";

    /// <summary>
    /// How stale a signed event may be. The timestamp is read from the SIGNED BODY
    /// (<c>timestamp</c>, unix ms), not from the unsigned <c>X-Webhook-Timestamp</c> header,
    /// so a replay cannot re-date itself. Generous on purpose: WAHA retries a failed webhook
    /// up to 15 times with its own backoff, and a legitimate retry must still land.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);

    /// <summary>Base64 in a JSON body: keep an outbound file to what WAHA's own body limits tolerate.</summary>
    public const long MaxInlineBytes = 16 * 1024 * 1024;

    /// <summary>The events ChatBranch asks the session to push. Nothing else is subscribed.</summary>
    public static readonly string[] Events = { "message", "session.status" };

    private readonly IHttpClientFactory _http;

    public WahaWhatsAppClient(IHttpClientFactory http) => _http = http;

    public bool IsConfigured(ChannelAccount account) =>
        WhatsAppProviders.IsWaha(account) && WhatsAppProviders.Configured(account);

    // ---- plumbing --------------------------------------------------------------------

    private static string Url(ChannelAccount a, string path) => $"{WahaConfig.BaseUrl(a)}{path}";

    private static string Seg(string s) => Uri.EscapeDataString(s);

    private static HttpRequestMessage Req(HttpMethod method, string url, ChannelAccount a,
        HttpContent? content = null, string? accept = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        if (a.AccessToken.Length > 0) req.Headers.TryAddWithoutValidation(KeyHeader, a.AccessToken);
        if (accept != null) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return req;
    }

    private Task<HttpReply> SendAsync(HttpRequestMessage req, ChannelAccount a, CancellationToken ct) =>
        ProviderHttp.SendAsync(_http, req, ct, a.AccessToken);

    private static SendOutcome NotConfigured() =>
        new(false, "", "WAHA account not configured (base URL and session name)");

    /// <summary>WhatsApp's own chat id for a 1:1 conversation: the digits plus <c>@c.us</c>.</summary>
    public static string ChatId(string waId)
    {
        var raw = (waId ?? "").Trim();
        if (raw.Contains('@')) return raw;                       // already a JID (a group, a @lid)
        var digits = WhatsAppProviders.Digits(raw);
        return digits.Length == 0 ? "" : $"{digits}@c.us";
    }

    /// <summary>The digits behind a WAHA chat id / <c>me.id</c> (<c>60123…@c.us</c> → <c>60123…</c>).</summary>
    public static string Digits(string? jid)
    {
        var s = (jid ?? "").Trim();
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        var colon = s.IndexOf(':');                              // 60123:12@s.whatsapp.net (a device)
        if (colon >= 0) s = s[..colon];
        return WhatsAppProviders.Digits(s);
    }

    /// <summary>
    /// WAHA's error in one line, plus the Meta-equivalent code where one honestly exists so
    /// <see cref="MetaErrors"/> can classify it. A refused API key is account-level (190); a
    /// session that is not working is NOT — health for that comes from the session probe, so a
    /// customer's send never bans the number on its own.
    /// </summary>
    public static (int? Code, string Detail) Error(HttpReply r)
    {
        if (r.Status == 0) return (null, $"WAHA unreachable: {r.Error}");
        var why = Message(r.Json);
        int? code = r.Status switch
        {
            401 or 403 => 190,                                   // the API key was refused → disconnected
            429 => 130429,                                       // throttled → degraded
            _ => null,
        };
        return (code, $"WAHA {r.Status}: {(why.Length > 0 ? why : "no detail")}");
    }

    /// <summary>NestJS answers <c>{statusCode, message, error}</c> — <c>message</c> may be a list.</summary>
    private static string Message(JsonElement body)
    {
        var m = ProviderHttp.At(body, "message");
        if (m.ValueKind == JsonValueKind.Array)
            return string.Join("; ", m.EnumerateArray().Select(e => e.ToString()).Where(s => s.Length > 0));
        var one = ProviderHttp.Str(body, "message");
        return one.Length > 0 ? one : ProviderHttp.Str(body, "error");
    }

    private static SendOutcome Fail(HttpReply r)
    {
        var (code, detail) = Error(r);
        return new SendOutcome(false, "", detail, code);
    }

    // ---- sending ---------------------------------------------------------------------

    private async Task<SendOutcome> PostAsync(ChannelAccount a, HttpMethod method, string path,
        Dictionary<string, object?> payload, CancellationToken ct)
    {
        if (!IsConfigured(a)) return NotConfigured();
        payload["session"] = WahaConfig.Session(a);
        var r = await SendAsync(Req(method, Url(a, path), a, ProviderHttp.JsonBody(payload)), a, ct);
        if (!r.Is2xx) return Fail(r);
        // the answer is the stored message: its id is what statuses, quotes and reactions use
        var id = r.Str("id");
        if (id.Length == 0) id = r.Str("_data", "id", "_serialized");
        return new SendOutcome(true, id, "");
    }

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) =>
        SendReplyTextAsync(account, toWaId, text, "", ct);

    public Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text,
        string contextWamid, CancellationToken ct = default)
    {
        var chat = ChatId(toWaId);
        if (chat.Length == 0) return Task.FromResult(new SendOutcome(false, "", "no recipient number"));
        var payload = new Dictionary<string, object?>
        {
            ["chatId"] = chat,
            ["text"] = text,
            ["linkPreview"] = false,
        };
        if (!string.IsNullOrWhiteSpace(contextWamid)) payload["reply_to"] = contextWamid;
        return PostAsync(account, HttpMethod.Post, "/api/sendText", payload, ct);
    }

    /// <summary>WAHA reacts with <c>PUT /api/reaction</c>; an empty emoji removes ours.</summary>
    public Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wamid))
            return Task.FromResult(new SendOutcome(false, "", "no message to react to"));
        return PostAsync(account, HttpMethod.Put, "/api/reaction", new Dictionary<string, object?>
        {
            ["chatId"] = ChatId(toWaId),
            ["messageId"] = wamid,
            ["reaction"] = emoji ?? "",
        }, ct);
    }

    /// <summary>
    /// WAHA has no upload step: a file rides in the send call as base64 (or a URL it fetches).
    /// So "upload" just encodes — the same two-call seam every other provider uses, with the
    /// bytes carried as a data URI through <see cref="SendMediaAsync"/>'s <c>mediaId</c>.
    /// </summary>
    public Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return Task.FromResult(NotConfigured());
        if (bytes.LongLength > MaxInlineBytes)
            return Task.FromResult(new SendOutcome(false, "",
                $"file too large for WAHA ({bytes.LongLength / (1024 * 1024)} MB; the limit here is "
                + $"{MaxInlineBytes / (1024 * 1024)} MB)"));
        var type = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;
        return Task.FromResult(new SendOutcome(true, $"data:{type};base64,{Convert.ToBase64String(bytes)}", ""));
    }

    /// <summary>
    /// <paramref name="mediaId"/> is a data URI from <see cref="UploadMediaAsync"/> or a URL
    /// WAHA can fetch itself. Pre-2026.6 WAHA builds put media behind WAHA Plus, which reads
    /// as a 4xx naming the paid build — passed on as it is, never retried as text.
    /// </summary>
    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default)
    {
        var chat = ChatId(toWaId);
        if (chat.Length == 0) return Task.FromResult(new SendOutcome(false, "", "no recipient number"));
        var file = new Dictionary<string, object?>();
        if (mediaId.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = mediaId.IndexOf(',');
            if (comma <= 0) return Task.FromResult(new SendOutcome(false, "", "unreadable file reference"));
            var head = mediaId[5..comma];
            file["mimetype"] = head.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? head[..^7] : head;
            file["data"] = mediaId[(comma + 1)..];
        }
        else if (mediaId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || mediaId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            file["url"] = mediaId;
        }
        else
        {
            return Task.FromResult(new SendOutcome(false, "", "WAHA takes a file inline or by URL"));
        }
        if (!string.IsNullOrWhiteSpace(filename)) file["filename"] = filename;

        var payload = new Dictionary<string, object?> { ["chatId"] = chat, ["file"] = file };
        var path = type switch
        {
            "image" => "/api/sendImage",
            "video" => "/api/sendVideo",
            "audio" => "/api/sendVoice",
            "sticker" => "/api/sendSticker",
            _ => "/api/sendFile",
        };
        // a voice note is OPUS-in-OGG; WAHA converts anything else when asked
        if (path == "/api/sendVoice") payload["convert"] = true;
        else if (!string.IsNullOrWhiteSpace(caption)) payload["caption"] = caption;
        return PostAsync(account, HttpMethod.Post, path, payload, ct);
    }

    /// <summary>
    /// An inbound file: WAHA hands us <c>payload.media.url</c> on its OWN host, so the API key
    /// rides only to the configured base URL and nowhere else.
    /// </summary>
    public async Task<MediaDownload> DownloadMediaAsync(ChannelAccount account, string mediaId, long maxBytes,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(mediaId, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new MediaDownload(false, Array.Empty<byte>(), "", "WAHA gave no download link");
        uri = Rebase(uri, WahaConfig.BaseUrl(account));
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (Uri.TryCreate(WahaConfig.BaseUrl(account), UriKind.Absolute, out var baseUri)
            && uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && account.AccessToken.Length > 0)
            req.Headers.TryAddWithoutValidation(KeyHeader, account.AccessToken);
        var (bytes, detail) = await ProviderHttp.DownloadAsync(_http, req, maxBytes, ct, account.AccessToken);
        return bytes == null
            ? new MediaDownload(false, Array.Empty<byte>(), "", detail.Length > 0 ? detail : "download failed")
            : new MediaDownload(true, bytes, "", "");
    }

    /// <summary>
    /// A WAHA that was started without <c>WAHA_BASE_URL</c> advertises its media as
    /// <c>http://localhost:3000/api/files/…</c> — its own address, not one we can reach. Any
    /// <c>/api/…</c> link that does not point at the configured base URL is therefore rebased
    /// onto it, which also keeps the API key scoped to the one host we were told about.
    /// Anything else (an S3 link, a CDN) is left exactly as it came.
    /// </summary>
    public static Uri Rebase(Uri uri, string baseUrl)
    {
        if (!uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return uri;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var home)) return uri;
        if (uri.Authority.Equals(home.Authority, StringComparison.OrdinalIgnoreCase)) return uri;
        return Uri.TryCreate($"{baseUrl}{uri.PathAndQuery}", UriKind.Absolute, out var moved) ? moved : uri;
    }

    // ---- the session ------------------------------------------------------------------

    /// <summary>
    /// <c>GET /api/sessions/{session}</c> — the status and, once WORKING, who is paired. A QR
    /// is fetched too when one is waiting (<paramref name="withQr"/>), because WAHA's codes
    /// expire in seconds and the panel re-reads this on a timer.
    /// </summary>
    public async Task<WahaSession> StatusAsync(ChannelAccount a, bool withQr = false, CancellationToken ct = default)
    {
        if (!IsConfigured(a)) return new WahaSession(WahaStatus.Unknown, Detail: "WAHA is not configured");
        var session = WahaConfig.Session(a);
        var r = await SendAsync(Req(HttpMethod.Get, Url(a, $"/api/sessions/{Seg(session)}"), a), a, ct);
        if (!r.Is2xx)
        {
            var (_, detail) = Error(r);
            return new WahaSession(r.Status == 404 ? WahaStatus.Stopped : WahaStatus.Unknown,
                Detail: r.Status == 404 ? $"WAHA has no session named \"{session}\" yet" : detail,
                HttpStatus: r.Status);
        }
        var status = r.Str("status").Trim().ToUpperInvariant();
        if (status.Length == 0) status = WahaStatus.Unknown;
        var me = Digits(r.Str("me", "id"));
        var state = new WahaSession(status, Me: me, MeName: r.Str("me", "pushName"), HttpStatus: r.Status,
            HookUrl: HookUrl(r.Json));
        // PASSKEY_CONFIRMATION_REQUIRED carries {code} — the number to CONFIRM on the phone,
        // never typed under "Link with phone number": it is not a pairing code
        if (status == WahaStatus.PasskeyConfirmationRequired && r.Str("data", "code") is { Length: > 0 } confirm)
            state = state with { ConfirmCode = confirm };
        if (withQr && status == WahaStatus.ScanQrCode) state = state with { Qr = await QrAsync(a, ct) };
        return state;
    }

    /// <summary>
    /// The first webhook URL in a session's own config, or "" when WAHA returned no config
    /// (global <c>WHATSAPP_HOOK_URL</c> settings are deliberately absent from it). "" means
    /// "unknown", which is why a WORKING session is never rewritten on the strength of it.
    /// </summary>
    public static string HookUrl(JsonElement session)
    {
        var hooks = ProviderHttp.At(session, "config", "webhooks");
        if (hooks.ValueKind != JsonValueKind.Array) return "";
        foreach (var h in hooks.EnumerateArray())
            if (ProviderHttp.Str(h, "url") is { Length: > 0 } u) return u;
        return "";
    }

    /// <summary>A base64 PNG QR as a data URL the dashboard can render, or "" when none is waiting.</summary>
    public async Task<string> QrAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var session = WahaConfig.Session(a);
        var r = await SendAsync(Req(HttpMethod.Get, Url(a, $"/api/{Seg(session)}/auth/qr"), a,
            accept: "application/json"), a, ct);
        if (!r.Is2xx) return "";
        var data = r.Str("data");
        if (data.Length == 0) return "";
        var mime = r.Str("mimetype") is { Length: > 0 } m ? m : "image/png";
        return $"data:{mime};base64,{data}";
    }

    /// <summary>
    /// Make sure WAHA holds a session with OUR webhook on it and that it is running:
    /// <c>GET</c> it, then <c>PUT</c> the config (existing) or <c>POST /api/sessions</c>
    /// (new), then <c>POST …/start</c> unless it is already up. Idempotent — pressing Pair
    /// twice is safe.
    ///
    /// <b>A WORKING session is left strictly alone</b> unless its webhook is demonstrably
    /// not ours, because WAHA's update is destructive: <c>SessionService.updateSession</c>
    /// stops the session, upserts the config and starts it again. Opening the setup drawer
    /// on a paired number must not drop a live WhatsApp connection.
    /// </summary>
    public async Task<WahaSession> StartAsync(ChannelAccount a, string webhookUrl, string hmacKey,
        CancellationToken ct = default)
    {
        if (!IsConfigured(a)) return new WahaSession(WahaStatus.Unknown, Detail: "WAHA is not configured");
        var session = WahaConfig.Session(a);
        var config = Config(a, webhookUrl, hmacKey);

        var get = await SendAsync(Req(HttpMethod.Get, Url(a, $"/api/sessions/{Seg(session)}"), a), a, ct);
        if (get.Status is 401 or 403)
            return new WahaSession(WahaStatus.Unknown, Detail: Error(get).Detail, HttpStatus: get.Status);
        if (get.Status == 0)
            return new WahaSession(WahaStatus.Unknown, Detail: Error(get).Detail);

        if (get.Is2xx)
        {
            var current = get.Str("status").Trim().ToUpperInvariant();
            var hook = HookUrl(get.Json);
            // ours already (or WAHA told us nothing about it) and the number is up: touch nothing
            var ours = hook.Length == 0 || string.Equals(hook, webhookUrl, StringComparison.OrdinalIgnoreCase);
            if (current == WahaStatus.Working && ours) return await StatusAsync(a, withQr: false, ct);
            if (!ours || current != WahaStatus.Working)
            {
                var put = await SendAsync(Req(HttpMethod.Put, Url(a, $"/api/sessions/{Seg(session)}"), a,
                    ProviderHttp.JsonBody(new Dictionary<string, object?> { ["config"] = config })), a, ct);
                if (!put.Is2xx)
                    return new WahaSession(WahaStatus.Unknown, Detail: Error(put).Detail, HttpStatus: put.Status);
                // WAHA's update restarts a session that was running; a stopped one needs a start
                current = put.Str("status").Trim().ToUpperInvariant();
            }
            if (current is WahaStatus.Stopped or WahaStatus.Failed or "")
            {
                var start = await SendAsync(Req(HttpMethod.Post, Url(a, $"/api/sessions/{Seg(session)}/start"), a,
                    ProviderHttp.JsonBody(new Dictionary<string, object?>())), a, ct);
                if (!start.Is2xx)
                    return new WahaSession(WahaStatus.Unknown, Detail: Error(start).Detail, HttpStatus: start.Status);
            }
        }
        else
        {
            var create = await SendAsync(Req(HttpMethod.Post, Url(a, "/api/sessions"), a,
                ProviderHttp.JsonBody(new Dictionary<string, object?>
                {
                    ["name"] = session, ["start"] = true, ["config"] = config,
                })), a, ct);
            if (!create.Is2xx)
                return new WahaSession(WahaStatus.Unknown, Detail: Error(create).Detail, HttpStatus: create.Status);
        }
        return await StatusAsync(a, withQr: true, ct);
    }

    /// <summary>
    /// The session config we own: our per-account webhook, the two events we read, and the
    /// HMAC key that signs them. WAHA also takes a global <c>WHATSAPP_HOOK_URL</c>, but per
    /// session is what lets every account verify with its OWN secret.
    /// </summary>
    private static Dictionary<string, object?> Config(ChannelAccount a, string webhookUrl, string hmacKey)
    {
        var hook = new Dictionary<string, object?>
        {
            ["url"] = webhookUrl,
            ["events"] = Events,
        };
        if (hmacKey.Length > 0) hook["hmac"] = new Dictionary<string, object?> { ["key"] = hmacKey };
        return new Dictionary<string, object?>
        {
            ["webhooks"] = webhookUrl.Length > 0 ? new[] { hook } : Array.Empty<Dictionary<string, object?>>(),
            ["metadata"] = new Dictionary<string, object?> { ["chatbranch.account"] = a.Id.ToString() },
        };
    }

    /// <summary>
    /// Poll the session until it is out of STARTING — a freshly created one is not ready to
    /// issue anything yet. Returns the last state read; never throws, never waits longer than
    /// <paramref name="budget"/>.
    /// </summary>
    public async Task<WahaSession> SettleAsync(ChannelAccount a, TimeSpan budget, bool withQr = false,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + budget;
        var state = await StatusAsync(a, withQr, ct);
        while (state.Status == WahaStatus.Starting && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
            state = await StatusAsync(a, withQr, ct);
        }
        return state;
    }

    /// <summary>
    /// <c>POST /api/{session}/auth/request-code</c> — the 8-character code the person types
    /// under WhatsApp → Linked devices → "Link with phone number". WAHA's own docs keep the
    /// QR as the fallback, because a pairing code is not always available.
    /// </summary>
    public async Task<(string Code, string Detail)> RequestPairingCodeAsync(ChannelAccount a, string phoneDigits,
        CancellationToken ct = default)
    {
        if (!IsConfigured(a)) return ("", "WAHA is not configured");
        var session = WahaConfig.Session(a);
        var r = await SendAsync(Req(HttpMethod.Post, Url(a, $"/api/{Seg(session)}/auth/request-code"), a,
            ProviderHttp.JsonBody(new Dictionary<string, object?> { ["phoneNumber"] = phoneDigits })), a, ct);
        if (!r.Is2xx) return ("", Error(r).Detail);
        var code = r.Str("code");
        return code.Length > 0 ? (code, "") : ("", "WAHA returned no pairing code — scan the QR instead");
    }

    /// <summary>Log the number out of WAHA (and leave the session stopped) — the "unpair" button.</summary>
    public async Task<WahaSession> LogoutAsync(ChannelAccount a, CancellationToken ct = default)
    {
        if (!IsConfigured(a)) return new WahaSession(WahaStatus.Unknown, Detail: "WAHA is not configured");
        var session = WahaConfig.Session(a);
        var r = await SendAsync(Req(HttpMethod.Post, Url(a, $"/api/sessions/{Seg(session)}/logout"), a,
            ProviderHttp.JsonBody(new Dictionary<string, object?>())), a, ct);
        if (!r.Is2xx) return new WahaSession(WahaStatus.Unknown, Detail: Error(r).Detail, HttpStatus: r.Status);
        await SendAsync(Req(HttpMethod.Post, Url(a, $"/api/sessions/{Seg(session)}/stop"), a,
            ProviderHttp.JsonBody(new Dictionary<string, object?>())), a, ct);
        return new WahaSession(WahaStatus.Stopped, HttpStatus: r.Status);
    }

    // ---- health ----------------------------------------------------------------------

    /// <summary>
    /// WAHA's session status → an account-health verdict. The pairing states are PROGRESS,
    /// not faults: STARTING and the two PASSKEY ones are transient (health untouched), and
    /// only a session that is stopped, failed or waiting to be paired is a real "this inbox
    /// cannot receive". A banned number stays banned until WAHA says WORKING again, as with
    /// the Baileys gateway.
    /// </summary>
    public static ProbeVerdict Verdict(ChannelAccount a, WahaSession s)
    {
        if (s.Working)
            return new ProbeVerdict(Health.Healthy, s.MeName.Length > 0 ? $"Connected as {s.MeName}" : "Connected");
        if (!s.Reachable)
            return new ProbeVerdict(null, s.Detail.Length > 0 ? s.Detail : "WAHA did not answer", Transient: true);
        if (s.HttpStatus is 401 or 403)
            return new ProbeVerdict(Health.Disconnected, "WAHA refused the API key — check it under Channels");
        if (WahaStatus.Transient(s.Status) && s.Status != WahaStatus.ScanQrCode)
            return new ProbeVerdict(null, $"WAHA session {s.Status.ToLowerInvariant()}", Transient: true);
        if (a.HealthStatus == Health.Banned)
            return new ProbeVerdict(null, $"WAHA session {s.Status.ToLowerInvariant()}", Transient: true);
        return s.Status switch
        {
            WahaStatus.ScanQrCode => new ProbeVerdict(Health.Disconnected,
                "not paired — a QR code is waiting to be scanned (Channels → Pair)"),
            WahaStatus.Stopped => new ProbeVerdict(Health.Disconnected,
                "the WAHA session is stopped — open Channels and press Pair"),
            WahaStatus.Failed => new ProbeVerdict(Health.Disconnected,
                s.Detail.Length > 0 ? $"WAHA session failed: {s.Detail}" : "the WAHA session failed — pair it again"),
            _ => new ProbeVerdict(null, s.Detail.Length > 0 ? s.Detail : $"WAHA session {s.Status}", Transient: true),
        };
    }

    public async Task<BspProbe?> ProbeBspAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return null;
        var s = await StatusAsync(account, withQr: false, ct);
        return new BspProbe(Verdict(account, s), s.Me, s.MeName, s.Status);
    }

    /// <summary>
    /// The account Test's read of "who is this number": WAHA's session identity in the field
    /// names the dashboard already renders. A session that is not WORKING is an honest NO,
    /// with what the person has to do.
    /// </summary>
    public async Task<GraphRead> GetPhoneNumberAsync(ChannelAccount account, CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new GraphRead(false, default, "WAHA account not configured");
        var s = await StatusAsync(account, withQr: false, ct);
        if (!s.Working)
        {
            var v = Verdict(account, s);
            return new GraphRead(false, default,
                v.Detail.Length > 0 ? v.Detail : $"the WAHA session is {s.Status}");
        }
        return new GraphRead(true, JsonSerializer.SerializeToElement(new
        {
            display_phone_number = s.Me.Length > 0 ? $"+{s.Me}" : "",
            verified_name = s.MeName,
            quality_rating = "",
            status = s.Status,
        }), "");
    }

    // ---- webhook signature ------------------------------------------------------------

    /// <summary>
    /// WAHA's signature: <c>X-Webhook-Hmac</c> = lowercase hex HMAC-SHA512 of the RAW request
    /// body, keyed by the session's <c>hmac.key</c> (its sender does
    /// <c>createHmac('sha512', key).update(body).digest('hex')</c>).
    /// </summary>
    public static string Sign(string secret, byte[] body) =>
        Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>A valid signature from this account's key, compared in constant time.</summary>
    public static bool VerifySignature(string secret, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header)) return false;
        return ProviderHttp.SameText(Sign(secret, body), header.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Is the SIGNED body fresh? WAHA stamps <c>timestamp</c> (unix ms) inside the envelope,
    /// so unlike the <c>X-Webhook-Timestamp</c> header it cannot be re-dated by a replayer.
    /// An envelope without one is accepted — the dedup on the message id is the other half.
    /// </summary>
    public static bool Fresh(JsonElement root, DateTimeOffset now)
    {
        var ms = ProviderHttp.Long(root, "timestamp");
        if (ms <= 0) return true;
        return (now - DateTimeOffset.FromUnixTimeMilliseconds(ms)).Duration() <= Freshness;
    }
}
