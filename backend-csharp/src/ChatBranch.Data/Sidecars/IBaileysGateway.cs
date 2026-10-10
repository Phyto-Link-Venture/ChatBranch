using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Sidecars;

public record BaileysResult(bool Ok, string Detail, string ExternalId = "");

/// <summary>Pairing state as the gateway reports it: {state, qr?, me?, error?}.</summary>
public record BaileysStatus(string State, string? Qr, string? Me, string? Error);

/// <summary>
/// "Is this number on WhatsApp?" as the gateway answers it (POST
/// /accounts/{id}/check → {exists, jid}): Exists true / false, or null when the
/// lookup was inconclusive (gateway down, account not connected, WhatsApp did
/// not answer) — <see cref="Error"/> then says why. A lookup never messages the
/// number.
/// </summary>
public record BaileysCheck(bool? Exists, string? Jid, string Error = "");

/// <summary>
/// A message another one refers to (a quote or a reaction target): its WhatsApp
/// id, whether WE sent it, and its text — enough for the gateway to rebuild the
/// quoted message when its own key map no longer holds it (after a restart).
/// </summary>
public record BaileysQuote(string Id, bool FromMe, string Text = "");

/// <summary>
/// The Node Baileys gateway (unofficial WhatsApp Web, <c>backend/baileys_service</c>)
/// stays a sidecar — no C# port exists. This is the HTTP contract the backend
/// uses to drive it, mirroring Django's <c>whatsapp/client.py baileys_request</c>:
/// every request carries <c>X-Baileys-Secret</c>; the gateway pushes inbound
/// traffic back to <c>/api/whatsapp/baileys/*</c> with the same secret.
///
/// Config resolves Setting → env → default (Django's <c>BAILEYS_URL</c> /
/// <c>BAILEYS_SECRET</c>): <c>baileys_url</c> (http://127.0.0.1:3020) and
/// <c>baileys_secret</c> (baileys-local-dev). The sandbox default keeps the app
/// runnable with no gateway attached, exactly like the Django app.
/// </summary>
public interface IBaileysGateway
{
    bool IsConfigured { get; }
    /// <summary>Base URL actually in use (for operator-facing "unreachable at …" messages).</summary>
    string BaseUrl { get; }
    Task<BaileysResult> SendTextAsync(string accountExternalId, string toWaId, string text,
        CancellationToken ct = default);
    /// <summary>A text quoting an earlier message (the gateway's <c>quoted_id</c>; it keeps a
    /// small key map and falls back to <paramref name="quote"/>'s text/direction to build the
    /// quote). The default sends the plain text — a quote never fails a send.</summary>
    Task<BaileysResult> SendTextAsync(string accountExternalId, string toWaId, string text, BaileysQuote? quote,
        CancellationToken ct = default) =>
        SendTextAsync(accountExternalId, toWaId, text, ct);
    /// <summary>An emoji reaction (<c>POST /accounts/{id}/react</c>) on <paramref name="target"/>;
    /// an empty emoji removes ours.</summary>
    Task<BaileysResult> ReactAsync(string accountExternalId, string toWaId, BaileysQuote target, string emoji,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway does not support reactions"));
    /// <summary>
    /// An image / video / audio / document (POST /accounts/{id}/send-media: the
    /// file travels base64 in the body — never a path — capped at
    /// <see cref="BaileysMedia.MaxBytes"/>). ExternalId = the WhatsApp message id.
    /// The default answers "not supported" so a gateway double without media
    /// support still compiles and degrades to a clean failed send.
    /// </summary>
    Task<BaileysResult> SendMediaAsync(string accountExternalId, string toWaId, string mediaType, byte[] content,
        string mime, string fileName, string? caption, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway does not support sending media"));
    /// <summary>Place a WhatsApp voice call bridged into a LiveKit room; ExternalId = the gateway's call id.</summary>
    Task<BaileysResult> StartCallAsync(string accountExternalId, string toWaId,
        Dictionary<string, object?> dispatch, CancellationToken ct = default);
    /// <summary>Hang up the account's active call (a no-op when none is live).</summary>
    Task<BaileysResult> EndCallAsync(string accountExternalId, CancellationToken ct = default);
    /// <summary>
    /// The gateway's own view of the account's call: idle | connecting | ringing |
    /// in_call (GET /accounts/{id}/call-status). Null = unknown (no gateway, or it
    /// didn't answer) — the caller falls back to the thread rows. Authoritative
    /// because thread rows are not reliably attributed to the calling account.
    /// </summary>
    Task<string?> CallStatusAsync(string accountExternalId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
    /// <summary>
    /// Look a number up on WhatsApp through a connected QR (Baileys) account —
    /// the only transport that can: the Cloud API has no lookup. The default
    /// answers "inconclusive" so a gateway double without it still compiles.
    /// </summary>
    Task<BaileysCheck> CheckNumberAsync(string accountExternalId, string number, CancellationToken ct = default) =>
        Task.FromResult(new BaileysCheck(null, null, "this Baileys gateway does not support number lookups"));
    // ---- pairing (Settings → Channels → WhatsApp → Pair) ----
    Task<BaileysStatus> StatusAsync(string accountExternalId, CancellationToken ct = default);
    Task<BaileysStatus> StartAsync(string accountExternalId, CancellationToken ct = default);
    Task<BaileysStatus> LogoutAsync(string accountExternalId, CancellationToken ct = default);
}

/// <summary>Zero-config fallback: every call is a logged skip, run continues.</summary>
public class SandboxBaileysGateway : IBaileysGateway
{
    public bool IsConfigured => false;
    public string BaseUrl => "";

    private static readonly BaileysStatus NotAttached = new("stopped", null, null,
        "Baileys gateway not attached — pairing runs in the Node sidecar (backend/baileys_service).");

    public Task<BaileysResult> SendTextAsync(string accountExternalId, string toWaId, string text,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));

    public Task<BaileysResult> SendMediaAsync(string accountExternalId, string toWaId, string mediaType,
        byte[] content, string mime, string fileName, string? caption, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));

    public Task<BaileysResult> StartCallAsync(string accountExternalId, string toWaId,
        Dictionary<string, object?> dispatch, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));

    public Task<BaileysResult> EndCallAsync(string accountExternalId, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));

    public Task<BaileysCheck> CheckNumberAsync(string accountExternalId, string number,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysCheck(null, null, "baileys gateway not configured (sandbox)"));

    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(NotAttached);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(NotAttached);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(NotAttached);
}

/// <summary>
/// The real client. "Configured" = a URL is set (default is set) — reachability
/// is discovered per call and reported as a clean error, never an exception
/// into a send/webhook path. Config is read PER CALL so a Setting change
/// (baileys_url / baileys_secret via the dashboard) applies without a restart.
/// </summary>
public class HttpBaileysGateway : IBaileysGateway
{
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;

    public HttpBaileysGateway(IHttpClientFactory http, IServiceScopeFactory scopes)
    {
        _http = http;
        _scopes = scopes;
    }

    // Setting → env → Django default. Read through a fresh scope because this
    // is a singleton and ScopingService/AppDbContext are scoped.
    private (string Url, string Secret) Config()
    {
        using var scope = _scopes.CreateScope();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        string S(string key, string env, string dflt)
        {
            var v = sc.SettingValue(key);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            var e = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(e) ? dflt : e.Trim();
        }
        return (S("baileys_url", "BAILEYS_URL", "http://127.0.0.1:3020").TrimEnd('/'),
                S("baileys_secret", "BAILEYS_SECRET", "baileys-local-dev"));
    }

    public bool IsConfigured => !string.IsNullOrEmpty(Config().Url);
    public string BaseUrl => Config().Url;

    private async Task<(bool Ok, JsonElement Body, string Error)> CallAsync(HttpMethod method, string path,
        object? payload, CancellationToken ct, int timeoutSeconds = 30)
    {
        var (url, secret) = Config();
        try
        {
            var client = _http.CreateClient("baileys");
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            using var req = new HttpRequestMessage(method, url + path);
            req.Headers.TryAddWithoutValidation("X-Baileys-Secret", secret);
            if (payload != null) req.Content = JsonContent.Create(payload);
            using var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement body;
            try { body = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone(); }
            catch { body = default; }
            if (!resp.IsSuccessStatusCode)
            {
                // the gateway answers {detail}; older builds {error}
                var detail = body.ValueKind == JsonValueKind.Object
                             && (body.TryGetProperty("detail", out var e) || body.TryGetProperty("error", out e))
                             && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : text;
                return (false, body, $"gateway {(int)resp.StatusCode}: {detail}".Trim());
            }
            return (true, body, "");
        }
        catch (HttpRequestException)
        {
            return (false, default, $"Baileys gateway unreachable at {url} — start it with `npm start` in backend/baileys_service.");
        }
        catch (TaskCanceledException)
        {
            return (false, default, $"Baileys gateway timed out at {url}.");
        }
    }

    private static BaileysStatus ToStatus(JsonElement body, string error)
    {
        if (body.ValueKind != JsonValueKind.Object) return new BaileysStatus("error", null, null, error);
        string? Get(string k) => body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new BaileysStatus(Get("state") ?? "stopped", Get("qr"), Get("me"), Get("error") ?? (error.Length > 0 ? error : null));
    }

    public async Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(a)}/status", null, ct);
        return ok ? ToStatus(body, "") : new BaileysStatus("error", null, null, err);
    }

    public async Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/start", new { }, ct);
        return ok ? ToStatus(body, "") : new BaileysStatus("error", null, null, err);
    }

    public async Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/logout", new { }, ct);
        return ok ? ToStatus(body, "") with { State = "logged_out" } : new BaileysStatus("error", null, null, err);
    }

    public Task<BaileysResult> SendTextAsync(string a, string toWaId, string text, CancellationToken ct = default) =>
        SendTextAsync(a, toWaId, text, null, ct);

    public async Task<BaileysResult> SendTextAsync(string a, string toWaId, string text, BaileysQuote? quote,
        CancellationToken ct = default)
    {
        object payload = quote is { Id.Length: > 0 }
            ? new
            {
                to = toWaId, text, quoted_id = quote.Id, quoted_from_me = quote.FromMe,
                quoted_text = quote.Text.Length > 500 ? quote.Text[..500] : quote.Text,
            }
            : new { to = toWaId, text };
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send",
            payload, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> ReactAsync(string a, string toWaId, BaileysQuote target, string emoji,
        CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/react",
            new { to = toWaId, msg_id = target.Id, from_me = target.FromMe, emoji = emoji ?? "" }, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> SendMediaAsync(string a, string toWaId, string mediaType, byte[] content,
        string mime, string fileName, string? caption, CancellationToken ct = default)
    {
        var refused = BaileysMedia.Refusal(mediaType, content);
        if (refused != null) return new BaileysResult(false, refused);
        // the gateway uploads to WhatsApp's CDN before it answers — allow for a slow link
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send-media",
            BaileysMedia.Payload(toWaId, mediaType, content, mime, fileName, caption), ct, timeoutSeconds: 120);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> StartCallAsync(string a, string toWaId, Dictionary<string, object?> dispatch,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>(dispatch) { ["to"] = toWaId };
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/call", payload, ct);
        if (!ok) return new BaileysResult(false, err);
        var callId = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("call_id", out var id)
            ? id.ToString() : "";
        return new BaileysResult(true, "dialled", callId);
    }

    public async Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default)
    {
        var (ok, _, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/call-end", new { }, ct);
        return ok ? new BaileysResult(true, "ended") : new BaileysResult(false, err);
    }

    public async Task<BaileysCheck> CheckNumberAsync(string a, string number, CancellationToken ct = default)
    {
        var digits = new string((number ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return new BaileysCheck(null, null, "not a phone number");
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/check",
            new { to = digits }, ct, timeoutSeconds: 20);
        if (!ok) return new BaileysCheck(null, null, err);
        if (body.ValueKind != JsonValueKind.Object) return new BaileysCheck(null, null, "gateway answered no body");
        bool? exists = body.TryGetProperty("exists", out var ex)
            ? ex.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
        var jid = body.TryGetProperty("jid", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
        return new BaileysCheck(exists, jid, exists == null ? "WhatsApp did not answer the lookup" : "");
    }

    public async Task<string?> CallStatusAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, _) = await CallAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(a)}/call-status", null, ct);
        return ok && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("status", out var st)
               && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;
    }
}

/// <summary>
/// The send-media contract with the gateway (<c>backend/baileys_service/media.js</c>
/// validates the same rules on its side): the file is base64 IN the body, the
/// gateway never reads a path or fetches a URL, and WhatsApp's 16 MB ceiling is
/// checked here first so an oversized file fails without a round-trip.
/// </summary>
public static class BaileysMedia
{
    public const long MaxBytes = 16L * 1024 * 1024;
    public static readonly string[] Types = { "image", "video", "audio", "document" };

    /// <summary>Why this file cannot go out over Baileys (null = it can).</summary>
    public static string? Refusal(string mediaType, byte[] content)
    {
        if (!Types.Contains(mediaType)) return $"media type '{mediaType}' cannot be sent over WhatsApp";
        if (content.Length == 0) return "the file is empty";
        if (content.Length > MaxBytes) return "file too large for WhatsApp (max 16 MB)";
        return null;
    }

    /// <summary>The JSON body of POST /accounts/{id}/send-media.</summary>
    public static Dictionary<string, object?> Payload(string toWaId, string mediaType, byte[] content, string mime,
        string fileName, string? caption)
    {
        var body = new Dictionary<string, object?>
        {
            ["to"] = toWaId,
            ["media_type"] = mediaType,
            ["file_b64"] = Convert.ToBase64String(content),
            ["mime"] = (mime ?? "").Split(';')[0].Trim(),
            ["filename"] = Path.GetFileName(fileName ?? ""),
        };
        if (!string.IsNullOrWhiteSpace(caption) && mediaType != "audio") body["caption"] = caption.Trim();
        return body;
    }
}
