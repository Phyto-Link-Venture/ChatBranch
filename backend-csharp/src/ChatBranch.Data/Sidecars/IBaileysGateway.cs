using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Sidecars;

public record BaileysResult(bool Ok, string Detail, string ExternalId = "")
{
    /// <summary>
    /// The gateway answered 409: the request can never succeed as asked (a message
    /// it no longer holds), as opposed to a provider failure worth retrying. Lets a
    /// caller turn it into a 409 with the reason instead of a 502.
    /// </summary>
    public bool Conflict { get; init; }
}

/// <summary>
/// Pairing state as the gateway reports it: {state, qr?, pairing_code?, pairing_phone?,
/// me?, error?}. A number is linked EITHER by scanning <see cref="Qr"/> or by typing
/// <see cref="PairingCode"/> on the phone that owns <see cref="PairingPhone"/> — state
/// "qr" or "pairing_code" says which one is waiting. The code is a credential for the
/// length of the pairing attempt: it is never audited or logged, and
/// <c>Tools/ToolResults</c> redacts it.
/// </summary>
public record BaileysStatus(string State, string? Qr, string? Me, string? Error,
    string? PairingCode = null, string? PairingPhone = null);

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
/// <see cref="Participant"/> = in a GROUP, the member who wrote it (their full jid, the
/// message's <c>SenderKey</c>) — WhatsApp needs it to address a quote or a reaction
/// there; "" for a 1:1 chat or one of our own messages.
/// </summary>
public record BaileysQuote(string Id, bool FromMe, string Text = "", string Participant = "");

/// <summary>
/// The oldest message ChatBranch already holds for one WhatsApp chat — the anchor a
/// gateway run asks WhatsApp from ("give me what came BEFORE this"). The gateway
/// keeps its own cursor per chat, but a number paired before on-demand fetching
/// existed has none, so the API seeds it from the rows the first import left behind.
/// A seed can only ever move a cursor further back, never forward.
/// </summary>
/// <param name="WaId">The customer's number, as the thread is keyed.</param>
/// <param name="MsgId">WhatsApp's own id of that oldest message.</param>
/// <param name="FromMe">The number sent it (an outbound reply), else the customer did.</param>
/// <param name="Seconds">Its WhatsApp timestamp, unix seconds.</param>
public record BaileysHistorySeed(string WaId, string MsgId, bool FromMe, long Seconds);

/// <summary>One chat as a history-fetch run touched it.</summary>
public record BaileysHistoryChat(string WaId, int Requests, int Received, int Imported, bool Done, string Reason);

/// <summary>
/// A history-fetch run as the gateway reports it — the honest version, which is the
/// point of the feature: how many requests were MADE, how many messages WhatsApp
/// actually served, how many landed in the inbox, and why it stopped. WhatsApp
/// serves what the paired phone still holds and no more, so a run ending with
/// fewer messages than asked for is the normal case, not a failure.
/// </summary>
public record BaileysHistoryRun
{
    public bool Running { get; init; }
    public bool Aborted { get; init; }
    /// <summary>Messages per chat this run was asked for.</summary>
    public int Depth { get; init; }
    public int Requests { get; init; }
    /// <summary>The hard cap on requests for one run.</summary>
    public int RequestsCap { get; init; }
    /// <summary>Messages WhatsApp served (importable or not).</summary>
    public int Received { get; init; }
    /// <summary>Messages the inbox took — the number that matters to an operator.</summary>
    public int Imported { get; init; }
    public int Timeouts { get; init; }
    public int Errors { get; init; }
    public int ChatsTotal { get; init; }
    public int ChatsTouched { get; init; }
    public int ChatsDone { get; init; }
    /// <summary>Chats the gateway holds an anchor for at all.</summary>
    public int ChatsKnown { get; init; }
    /// <summary>Chats WhatsApp itself declared exhausted.</summary>
    public int ChatsExhausted { get; init; }
    public string StopReason { get; init; } = "";
    /// <summary>Seconds until the next request, while one is being waited out.</summary>
    public int? NextGapS { get; init; }
    public long? StartedAt { get; init; }
    public long? FinishedAt { get; init; }
    public long? LastRunAt { get; init; }
    /// <summary>The jittered pacing between requests, seconds.</summary>
    public int MinGapS { get; init; }
    public int MaxGapS { get; init; }
    /// <summary>A run may not start again this soon after the last one ended.</summary>
    public int CooldownS { get; init; }
    /// <summary>Requests this number has spent in the rolling 24h, and the cap on them —
    /// the bound that matters across runs, not within one.</summary>
    public int DayUsed { get; init; }
    public int DayLeft { get; init; }
    public int DayCap { get; init; }
    public IReadOnlyList<BaileysHistoryChat> Chats { get; init; } = Array.Empty<BaileysHistoryChat>();
    /// <summary>"" when the gateway answered; the reason otherwise.</summary>
    public string Error { get; init; } = "";
    /// <summary>The gateway refused as asked (already running, cooling down, not linked).</summary>
    public bool Conflict { get; init; }
}

/// <summary>
/// Per-number settings of a QR (Baileys) WhatsApp number, kept in
/// <c>ChannelAccount.Config</c> and handed to the gateway (which stores its own copy
/// next to the session): <see cref="KeepPhoneNotifications"/> = stay offline between
/// sends so the phone keeps getting WhatsApp's notifications (backend/baileys_service/presence.js).
/// </summary>
public static class BaileysSettings
{
    public const string KeepPhoneNotifications = "keep_phone_notifications";
    public const string ImportHistoryDaysKey = "import_history_days";

    /// <summary>"Everything WhatsApp sends" — which is only ever what the phone still holds.</summary>
    public const int HistoryAll = -1;

    /// <summary>Off, a month (the default), a quarter, or everything — what the drawer offers.</summary>
    public static readonly int[] HistoryChoices = { 0, 30, 90, HistoryAll };

    /// <summary>
    /// How many days of the number's existing chats to import when it is linked: 0 = none,
    /// <see cref="HistoryAll"/> = everything WhatsApp serves, else that many days. A number
    /// with no setting imports the last 30 days, which is what the inbox is worth having on
    /// the first day; an explicit 0 turns it off.
    /// </summary>
    public static int ImportHistoryDays(ChatBranch.Domain.ChannelAccount? a)
    {
        if (a == null) return 0;
        if (!a.Config.TryGetValue(ImportHistoryDaysKey, out var raw) || raw == null) return 30;
        var text = raw switch
        {
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e =>
                e.GetRawText(),
            System.Text.Json.JsonElement e => e.GetString() ?? "",
            _ => raw.ToString() ?? "",
        };
        return int.TryParse(text.Trim(), out var days) && HistoryChoices.Contains(days) ? days : 30;
    }

    public static bool KeepPhone(ChatBranch.Domain.ChannelAccount a) =>
        a.Config.TryGetValue(KeepPhoneNotifications, out var v) && v switch
        {
            bool b => b,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e =>
                string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            string str => string.Equals(str, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
}

/// <summary>
/// Pairing a QR number by PHONE NUMBER instead of a scan. Mirrors
/// <c>backend/baileys_service/pairing.js</c> (<c>pairingPhone</c>) so the dashboard
/// refuses an impossible number before a pairing attempt is spent — WhatsApp
/// rate-limits them per number — and both sides agree on what Baileys is handed.
/// </summary>
public static class BaileysPairing
{
    /// <summary>E.164's own bounds: 15 digits at most, and nothing shorter than 7 is a real line.</summary>
    public const int MinDigits = 7;
    public const int MaxDigits = 15;

    /// <summary>
    /// The number as Baileys wants it: digits only, country code included, no "+".
    /// "" when it cannot be one — a leading 0 means a national trunk prefix was typed
    /// instead of a country code, which WhatsApp would reject.
    /// </summary>
    public static string Phone(string? raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.Length is < MinDigits or > MaxDigits) return "";
        return digits.StartsWith('0') ? "" : digits;
    }
}

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
    /// <summary>
    /// An Ogg/Opus file as a VOICE NOTE (the gateway's <c>send-media</c> with <c>ptt: true</c> →
    /// Baileys <c>audioMessage</c>, <c>ptt</c>, mimetype <c>audio/ogg; codecs=opus</c>) — the AI's
    /// voice replies. The default answers "not supported" so the reply falls back to text.
    /// </summary>
    Task<BaileysResult> SendVoiceNoteAsync(string accountExternalId, string toWaId, byte[] oggOpus,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway does not support voice notes"));
    /// <summary>
    /// WhatsApp's "delete for everyone" on a message WE sent. WhatsApp keeps its
    /// own window (about two days) and refuses past it — that refusal comes back
    /// as a failure, because the message is still on the customer's phone and the
    /// operator needs to know it is.
    /// </summary>
    Task<BaileysResult> RevokeMessageAsync(string accountExternalId, string toWaId, BaileysQuote target,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot delete a sent message"));

    /// <summary>
    /// Edit a TEXT message we sent (WhatsApp allows about 15 minutes). The edit
    /// arrives as its own message, so ExternalId is the EDIT's id — the original
    /// keeps its own, which is what the stored row is keyed on.
    /// </summary>
    Task<BaileysResult> EditMessageAsync(string accountExternalId, string toWaId, BaileysQuote target, string text,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot edit a sent message"));

    /// <summary>
    /// A true WhatsApp FORWARD of <paramref name="sourceMessageId"/> into this
    /// conversation, keeping WhatsApp's own "Forwarded" marking. It needs the whole
    /// original, so the gateway can only do it while it still holds it (the last
    /// ~2000 messages, lost on restart) — otherwise the result is a
    /// <see cref="BaileysResult.Conflict"/>, because re-sending the text or the
    /// file would arrive unmarked and be a different thing from a forward.
    /// </summary>
    Task<BaileysResult> ForwardMessageAsync(string accountExternalId, string toWaId, string sourceMessageId,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot forward a message"));

    /// <summary>A pinned location.</summary>
    Task<BaileysResult> SendLocationAsync(string accountExternalId, string toWaId, double latitude, double longitude,
        string name, string address, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot send a location"));

    /// <summary>A contact card (vCard). WhatsApp makes each number tappable from its <c>waid</c>.</summary>
    Task<BaileysResult> SendContactAsync(string accountExternalId, string toWaId, string name,
        IReadOnlyList<string> phones, string organization, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot send a contact card"));

    /// <summary>A poll: 2–12 options, <paramref name="selectableCount"/> 1 = a single choice.</summary>
    Task<BaileysResult> SendPollAsync(string accountExternalId, string toWaId, string question,
        IReadOnlyList<string> options, int selectableCount, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "this Baileys gateway cannot send a poll"));

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
    /// <summary>
    /// "Keep phone notifications" (<see cref="BaileysSettings"/>): the gateway keeps the
    /// number offline between sends so the phone still buzzes, and remembers it next to
    /// the session. True when the gateway took it; the default (a double without it) says no.
    /// </summary>
    Task<bool> SetPhoneNotificationsAsync(string accountExternalId, bool keep, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>
    /// Hand over every per-number setting the gateway needs BEFORE a session starts:
    /// "keep phone notifications" and how far back to import the number's existing
    /// chats (<see cref="BaileysSettings.ImportHistoryDays"/>) — the history window is
    /// a socket option, so it can only be chosen at pairing time. A gateway that knows
    /// only the older call still gets the notification setting.
    /// </summary>
    Task<bool> SetSettingsAsync(string accountExternalId, bool keep, int importHistoryDays,
        CancellationToken ct = default) => SetPhoneNotificationsAsync(accountExternalId, keep, ct);
    // ---- on-demand history (Channels → the number → "Fetch older messages") ----
    /// <summary>
    /// Where the number's history-fetch run is (or the last one that ran). Read-only
    /// and cheap: it talks to no one but the gateway's own bookkeeping.
    /// </summary>
    Task<BaileysHistoryRun> HistoryRunAsync(string accountExternalId, CancellationToken ct = default) =>
        Task.FromResult(new BaileysHistoryRun
        {
            Error = "this Baileys gateway cannot fetch older history", Conflict = true,
        });

    /// <summary>
    /// Ask WhatsApp for OLDER messages, chat by chat — the operator-triggered deepening
    /// of what the shallow replay on pair left behind. One run at a time per number;
    /// the gateway paces the requests, caps them, and stops when WhatsApp stops serving.
    /// <paramref name="depth"/> is messages per chat; <paramref name="waId"/> narrows it
    /// to one chat; <paramref name="seeds"/> are the anchors from our own rows.
    /// </summary>
    Task<BaileysHistoryRun> FetchHistoryAsync(string accountExternalId, int depth, string? waId,
        IReadOnlyList<BaileysHistorySeed> seeds, CancellationToken ct = default) =>
        HistoryRunAsync(accountExternalId, ct);

    /// <summary>Stop the run in progress (an operator pressing Stop). Idempotent.</summary>
    Task<BaileysHistoryRun> StopHistoryAsync(string accountExternalId, CancellationToken ct = default) =>
        HistoryRunAsync(accountExternalId, ct);

    // ---- pairing (Settings → Channels → WhatsApp → Pair) ----
    Task<BaileysStatus> StatusAsync(string accountExternalId, CancellationToken ct = default);
    Task<BaileysStatus> StartAsync(string accountExternalId, CancellationToken ct = default);
    /// <summary>
    /// Start pairing by PHONE NUMBER: WhatsApp hands out an 8-character code for
    /// <paramref name="pairPhone"/> (digits, country code, no "+") which the person types
    /// under Linked devices → "Link with phone number" instead of scanning. A gateway that
    /// cannot do it falls back to the QR start, so the dialog still has a way in.
    /// </summary>
    Task<BaileysStatus> StartAsync(string accountExternalId, string? pairPhone,
        CancellationToken ct = default) => StartAsync(accountExternalId, ct);
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

    /// <summary>
    /// Fetching older history is the gateway's own paced run — with no gateway attached
    /// there is nothing to run and nothing to report. A refusal with the reason, never
    /// an exception and never an invented run.
    /// </summary>
    public Task<BaileysHistoryRun> HistoryRunAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysHistoryRun
        {
            Error = "Baileys gateway not attached — fetching older history runs in the Node sidecar "
                    + "(backend/baileys_service).",
            Conflict = true,
        });
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
        return new BaileysStatus(Get("state") ?? "stopped", Get("qr"), Get("me"),
            Get("error") ?? (error.Length > 0 ? error : null), Get("pairing_code"), Get("pairing_phone"));
    }

    public async Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(a)}/status", null, ct);
        return ok ? ToStatus(body, "") : new BaileysStatus("error", null, null, err);
    }

    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) =>
        StartAsync(a, null, ct);

    public async Task<BaileysStatus> StartAsync(string a, string? pairPhone, CancellationToken ct = default)
    {
        // {} = the gateway emits a QR; {phone} = it asks WhatsApp for a pairing code.
        object payload = string.IsNullOrWhiteSpace(pairPhone) ? new { } : new { phone = pairPhone };
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/start", payload, ct);
        return ok ? ToStatus(body, "") : new BaileysStatus("error", null, null, err);
    }

    public async Task<bool> SetPhoneNotificationsAsync(string a, bool keep, CancellationToken ct = default)
    {
        var (ok, _, _) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/settings",
            new { keep_phone_notifications = keep }, ct, timeoutSeconds: 10);
        return ok;
    }

    public async Task<bool> SetSettingsAsync(string a, bool keep, int importHistoryDays,
        CancellationToken ct = default)
    {
        var (ok, _, _) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/settings",
            new { keep_phone_notifications = keep, import_history_days = importHistoryDays }, ct,
            timeoutSeconds: 10);
        return ok;
    }

    // ---- on-demand history --------------------------------------------------
    // The gateway owns the run (and the per-chat cursors next to its session files);
    // this end only triggers, reads and stops. A 409/429 refusal — already running,
    // cooling down, the number not linked — is a Conflict, not a provider failure,
    // so the endpoint can answer 409 with the reason instead of 502.

    private static int Int(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static int? IntOrNull(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? LongOrNull(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool Flag(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static BaileysHistoryRun ToRun(bool ok, JsonElement body, string error)
    {
        if (!ok || body.ValueKind != JsonValueKind.Object)
            return new BaileysHistoryRun { Error = error, Conflict = Refused(error) };
        var pacing = body.TryGetProperty("pacing", out var p) ? p : default;
        var day = body.TryGetProperty("day", out var d) ? d : default;
        var chats = new List<BaileysHistoryChat>();
        if (body.TryGetProperty("chats", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var c in list.EnumerateArray())
                chats.Add(new BaileysHistoryChat(Text(c, "wa_id"), Int(c, "requests"), Int(c, "received"),
                    Int(c, "imported"), Flag(c, "done"), Text(c, "reason")));
        return new BaileysHistoryRun
        {
            Running = Flag(body, "running"),
            Aborted = Flag(body, "aborted"),
            Depth = Int(body, "depth"),
            Requests = Int(body, "requests"),
            RequestsCap = Int(body, "requests_cap"),
            Received = Int(body, "received"),
            Imported = Int(body, "imported"),
            Timeouts = Int(body, "timeouts"),
            Errors = Int(body, "errors"),
            ChatsTotal = Int(body, "chats_total"),
            ChatsTouched = Int(body, "chats_touched"),
            ChatsDone = Int(body, "chats_done"),
            ChatsKnown = Int(body, "chats_known"),
            ChatsExhausted = Int(body, "chats_exhausted"),
            StopReason = Text(body, "stop_reason"),
            NextGapS = IntOrNull(body, "next_gap_s"),
            StartedAt = LongOrNull(body, "started_at"),
            FinishedAt = LongOrNull(body, "finished_at"),
            LastRunAt = LongOrNull(body, "last_run_at"),
            MinGapS = Int(pacing, "min_gap_s"),
            MaxGapS = Int(pacing, "max_gap_s"),
            CooldownS = Int(body, "cooldown_s"),
            DayUsed = Int(day, "used"),
            DayLeft = Int(day, "left"),
            DayCap = Int(day, "cap"),
            Chats = chats,
        };
    }

    /// <summary>A gateway 409/429: the request cannot succeed as asked, right now.</summary>
    private static bool Refused(string error) =>
        error.StartsWith("gateway 409", StringComparison.Ordinal)
        || error.StartsWith("gateway 429", StringComparison.Ordinal);

    public async Task<BaileysHistoryRun> HistoryRunAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Get,
            $"/accounts/{Uri.EscapeDataString(a)}/history-fetch", null, ct, timeoutSeconds: 10);
        return ToRun(ok, body, err);
    }

    public async Task<BaileysHistoryRun> FetchHistoryAsync(string a, int depth, string? waId,
        IReadOnlyList<BaileysHistorySeed> seeds, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["depth"] = depth,
            ["chats"] = (seeds ?? Array.Empty<BaileysHistorySeed>()).Select(s => new
            {
                wa_id = s.WaId, msg_id = s.MsgId, from_me = s.FromMe, ts = s.Seconds,
            }).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(waId)) payload["wa_id"] = waId.Trim();
        var (ok, body, err) = await CallAsync(HttpMethod.Post,
            $"/accounts/{Uri.EscapeDataString(a)}/history-fetch", payload, ct, timeoutSeconds: 30);
        return ToRun(ok, body, err);
    }

    public async Task<BaileysHistoryRun> StopHistoryAsync(string a, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post,
            $"/accounts/{Uri.EscapeDataString(a)}/history-fetch-stop", new { }, ct, timeoutSeconds: 10);
        return ToRun(ok, body, err);
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
        object payload;
        if (quote is { Id.Length: > 0 })
        {
            var p = new Dictionary<string, object?>
            {
                ["to"] = toWaId, ["text"] = text, ["quoted_id"] = quote.Id, ["quoted_from_me"] = quote.FromMe,
                ["quoted_text"] = quote.Text.Length > 500 ? quote.Text[..500] : quote.Text,
            };
            // a group member's message: WhatsApp addresses the quote by its author
            if (quote.Participant.Length > 0) p["quoted_participant"] = quote.Participant;
            payload = p;
        }
        else payload = new { to = toWaId, text };
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send",
            payload, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> ReactAsync(string a, string toWaId, BaileysQuote target, string emoji,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["to"] = toWaId, ["msg_id"] = target.Id, ["from_me"] = target.FromMe, ["emoji"] = emoji ?? "",
        };
        if (target.Participant.Length > 0) payload["participant"] = target.Participant;
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/react",
            payload, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> RevokeMessageAsync(string a, string toWaId, BaileysQuote target,
        CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/revoke",
            new { to = toWaId, msg_id = target.Id, from_me = target.FromMe }, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "deleted", id);
    }

    public async Task<BaileysResult> EditMessageAsync(string a, string toWaId, BaileysQuote target, string text,
        CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/edit",
            new { to = toWaId, msg_id = target.Id, from_me = target.FromMe, text }, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "edited", id);
    }

    public async Task<BaileysResult> ForwardMessageAsync(string a, string toWaId, string sourceMessageId,
        CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/forward",
            new { to = toWaId, msg_id = sourceMessageId }, ct, timeoutSeconds: 120);
        if (!ok)
            // the gateway says 409 when the original is no longer held
            return new BaileysResult(false, err) { Conflict = err.Contains("gateway 409", StringComparison.Ordinal) };
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "forwarded", id);
    }

    public async Task<BaileysResult> SendLocationAsync(string a, string toWaId, double latitude, double longitude,
        string name, string address, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send-location",
            new { to = toWaId, lat = latitude, lng = longitude, name = name ?? "", address = address ?? "" }, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> SendContactAsync(string a, string toWaId, string name,
        IReadOnlyList<string> phones, string organization, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send-contact",
            new { to = toWaId, name, phones, organization = organization ?? "" }, ct);
        if (!ok) return new BaileysResult(false, err);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i) ? i.ToString() : "";
        return new BaileysResult(true, "sent", id);
    }

    public async Task<BaileysResult> SendPollAsync(string a, string toWaId, string question,
        IReadOnlyList<string> options, int selectableCount, CancellationToken ct = default)
    {
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send-poll",
            new { to = toWaId, question, options, selectableCount }, ct);
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

    public async Task<BaileysResult> SendVoiceNoteAsync(string a, string toWaId, byte[] oggOpus,
        CancellationToken ct = default)
    {
        var refused = BaileysMedia.Refusal("audio", oggOpus);
        if (refused != null) return new BaileysResult(false, refused);
        var (ok, body, err) = await CallAsync(HttpMethod.Post, $"/accounts/{Uri.EscapeDataString(a)}/send-media",
            BaileysMedia.Payload(toWaId, "audio", oggOpus, "audio/ogg", "voice.ogg", null, ptt: true), ct,
            timeoutSeconds: 120);
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
    /// <summary>
    /// <c>sticker</c> is its own type, not an image: WhatsApp only renders one for a
    /// 512×512 WebP inside its own (far smaller) ceiling and shows no caption for it.
    /// </summary>
    public static readonly string[] Types = { "image", "video", "audio", "document", "sticker" };

    public const string Sticker = "sticker";

    /// <summary>Why this file cannot go out over Baileys (null = it can).</summary>
    public static string? Refusal(string mediaType, byte[] content)
    {
        if (!Types.Contains(mediaType)) return $"media type '{mediaType}' cannot be sent over WhatsApp";
        if (content.Length == 0) return "the file is empty";
        // a sticker has its own shape and ceiling, checked where it is made
        if (mediaType == Sticker) return ChatBranch.Data.Channels.StickerImage.Refusal(content);
        if (content.Length > MaxBytes) return "file too large for WhatsApp (max 16 MB)";
        return null;
    }

    /// <summary>The JSON body of POST /accounts/{id}/send-media.</summary>
    public static Dictionary<string, object?> Payload(string toWaId, string mediaType, byte[] content, string mime,
        string fileName, string? caption, bool ptt = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["to"] = toWaId,
            ["media_type"] = mediaType,
            ["file_b64"] = Convert.ToBase64String(content),
            ["mime"] = (mime ?? "").Split(';')[0].Trim(),
            ["filename"] = Path.GetFileName(fileName ?? ""),
        };
        // neither audio nor a sticker carries a caption on WhatsApp
        if (!string.IsNullOrWhiteSpace(caption) && mediaType != "audio" && mediaType != Sticker)
            body["caption"] = caption.Trim();
        // a voice note (push-to-talk): the gateway sends audio/ogg; codecs=opus with ptt=true
        if (ptt && mediaType == "audio") body["ptt"] = true;
        return body;
    }
}
