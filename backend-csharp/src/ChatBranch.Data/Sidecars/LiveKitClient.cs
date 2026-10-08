using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Data.Sidecars;

/// <summary>
/// Where the LiveKit control plane lives. Read per call (a .env edit applies
/// without a restart): the process environment first, then backend/.env — the
/// same file the voice agent worker and the Baileys gateway load.
/// </summary>
public sealed record LiveKitSettings(string Url, string ApiKey, string ApiSecret, string AgentName, string SipTrunkId)
{
    public const string DefaultAgentName = "chatbranch-agent";

    private static readonly string[] Keys =
        { "LIVEKIT_URL", "LIVEKIT_API_KEY", "LIVEKIT_API_SECRET", "AGENT_NAME", "SIP_OUTBOUND_TRUNK_ID" };

    public static LiveKitSettings FromEnvironment()
    {
        var file = ReadEnvFile(VoiceAgentBridge.BackendDir is string dir ? Path.Combine(dir, ".env") : null);
        string Get(string key)
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(v)) file.TryGetValue(key, out v);
            return (v ?? "").Trim();
        }
        var agent = Get("AGENT_NAME");
        return new LiveKitSettings(Get("LIVEKIT_URL"), Get("LIVEKIT_API_KEY"), Get("LIVEKIT_API_SECRET"),
            agent.Length > 0 ? agent : DefaultAgentName, Get("SIP_OUTBOUND_TRUNK_ID"));
    }

    /// <summary>KEY=value lines (dotenv subset: comments, `export`, quotes, trailing ` #` notes).</summary>
    private static Dictionary<string, string> ReadEnvFile(string? path)
    {
        var values = new Dictionary<string, string>();
        if (path == null || !File.Exists(path)) return values;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("export ")) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (line.StartsWith('#') || eq <= 0) continue;
                var key = line[..eq].Trim();
                if (!Keys.Contains(key)) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                    value = value[1..^1];
                else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
                    value = value[..hash].TrimEnd();
                values[key] = value;
            }
        }
        catch { /* unreadable file = not configured there */ }
        return values;
    }

    /// <summary>The Twirp base URL: LiveKit serves its API on the same host as the websocket.</summary>
    public string HttpUrl
    {
        get
        {
            var url = Url.TrimEnd('/');
            if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return "https://" + url[6..];
            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) return "http://" + url[5..];
            return url;
        }
    }

    /// <summary>Operator-facing reason the server API can't be used, or null when it can.</summary>
    public string? MissingForServer()
    {
        var missing = new[] { ("LIVEKIT_URL", Url), ("LIVEKIT_API_KEY", ApiKey), ("LIVEKIT_API_SECRET", ApiSecret) }
            .Where(p => p.Item2.Length == 0).Select(p => p.Item1).ToList();
        return missing.Count == 0
            ? null
            : $"AI calling is not configured: set {string.Join(", ", missing)} in backend/.env (or the API's "
              + "environment) and run the voice agent worker (npm run dev:agent).";
    }

    /// <summary>As <see cref="MissingForServer"/>, plus the outbound SIP trunk a phone call needs.</summary>
    public string? MissingForSip() =>
        MissingForServer() ?? (SipTrunkId.Length == 0
            ? "Phone calling is not configured: create the LiveKit outbound trunk once "
              + "(npm --prefix backend/voice_agent run setup-trunk) and set SIP_OUTBOUND_TRUNK_ID in backend/.env."
            : null);
}

/// <summary>One LiveKit server-API answer: never an exception, always a shaped result.</summary>
public sealed record LiveKitResult(bool Ok, int Status, string Detail);

/// <summary>
/// A small client for LiveKit's server API — the control plane the old Python
/// caller.py drove through the LiveKit SDK: agent dispatch, outbound SIP
/// participants, room deletion (hang-up) and participant join tokens. Requests
/// are Twirp JSON over HTTP (<c>POST {url}/twirp/livekit.{Service}/{Method}</c>)
/// authorised by short-lived HS256 tokens signed with LIVEKIT_API_SECRET.
/// Nothing here throws into a request path: unconfigured, unreachable and
/// rejected calls all come back as a <see cref="LiveKitResult"/>.
/// </summary>
public class LiveKitClient
{
    /// <summary>The identity the phone participant joins as (the agent's recorder and logs use it).</summary>
    public const string PhoneIdentity = "phone_contact";

    /// <summary>The identity the Baileys gateway joins a WhatsApp call room as.</summary>
    public const string WhatsAppIdentity = "whatsapp_borrower";

    private static readonly TimeSpan ServerTokenTtl = TimeSpan.FromMinutes(10);
    private readonly IHttpClientFactory _http;
    private readonly Func<LiveKitSettings> _settings;

    public LiveKitClient(IHttpClientFactory http) : this(http, LiveKitSettings.FromEnvironment) { }

    public LiveKitClient(IHttpClientFactory http, Func<LiveKitSettings> settings)
    {
        _http = http;
        _settings = settings;
    }

    public LiveKitSettings Settings => _settings();

    /// <summary>A full-entropy, unguessable room id — it doubles as the live-transcript lookup key.</summary>
    public static string NewRoomName() =>
        "call-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- tokens -----------------------------------------------------------

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// An HS256 LiveKit access token: <c>iss</c> = API key, <c>nbf</c>/<c>exp</c>
    /// window, optional <c>sub</c> (identity) and <c>name</c>, and the grants
    /// (<c>video</c>, <c>sip</c>) as LiveKit's camelCase claim objects.
    /// </summary>
    public static string MintToken(string apiKey, string apiSecret, TimeSpan ttl, JsonObject grants,
        string? identity = null, string? name = null, DateTimeOffset? now = null)
    {
        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = apiKey,
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + (long)ttl.TotalSeconds,
        };
        if (!string.IsNullOrEmpty(identity)) claims["sub"] = identity;
        if (!string.IsNullOrEmpty(name)) claims["name"] = name;
        foreach (var (key, value) in grants) claims[key] = value?.DeepClone();

        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = B64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.UTF8.GetBytes($"{header}.{payload}"));
        return $"{header}.{payload}.{B64Url(signature)}";
    }

    /// <summary>
    /// A room-join token for a participant the API sends into a call — the
    /// Baileys gateway bridging a WhatsApp call joins with one.
    /// </summary>
    public string ParticipantToken(string room, string identity, string name, TimeSpan ttl)
    {
        var s = Settings;
        return MintToken(s.ApiKey, s.ApiSecret, ttl, new JsonObject
        {
            ["video"] = new JsonObject
            {
                ["room"] = room,
                ["roomJoin"] = true,
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
                // the bridge publishes wa.callStatus (ringing → active) as attributes
                ["canUpdateOwnMetadata"] = true,
            },
        }, identity, name);
    }

    // ---- server API -------------------------------------------------------

    /// <summary>Dispatch the voice agent (by AGENT_NAME) into <paramref name="room"/> with its call metadata.</summary>
    public Task<LiveKitResult> CreateDispatchAsync(string room, string metadataJson, CancellationToken ct = default)
    {
        var s = Settings;
        return TwirpAsync("AgentDispatchService", "CreateDispatch",
            new { room, agentName = s.AgentName, metadata = metadataJson },
            new JsonObject { ["video"] = new JsonObject { ["roomAdmin"] = true, ["room"] = room } }, ct);
    }

    /// <summary>Dial <paramref name="phone"/> (E.164) over the outbound trunk into <paramref name="room"/>.</summary>
    public Task<LiveKitResult> CreateSipParticipantAsync(string room, string phone, string displayName,
        CancellationToken ct = default) =>
        TwirpAsync("SIPService", "CreateSIPParticipant",
            new
            {
                sipTrunkId = Settings.SipTrunkId,
                sipCallTo = phone,
                roomName = room,
                participantIdentity = PhoneIdentity,
                participantName = displayName,
                // return once the INVITE is out; the agent itself waits for
                // sip.callStatus=active before it speaks
                waitUntilAnswered = false,
            },
            new JsonObject { ["sip"] = new JsonObject { ["call"] = true } }, ct);

    /// <summary>Delete the room, which disconnects everyone in it (hangs the call up).</summary>
    public Task<LiveKitResult> DeleteRoomAsync(string room, CancellationToken ct = default) =>
        TwirpAsync("RoomService", "DeleteRoom", new { room },
            new JsonObject { ["video"] = new JsonObject { ["roomCreate"] = true } }, ct);

    /// <summary>
    /// The outbound phone call: dispatch the agent, then dial. A failed dial
    /// deletes the room again, so the dispatched agent isn't left waiting in it.
    /// </summary>
    public async Task<LiveKitResult> PlaceSipCallAsync(string room, string phone, string displayName,
        string metadataJson, CancellationToken ct = default)
    {
        if (Settings.MissingForSip() is string reason) return new LiveKitResult(false, 503, reason);
        var dispatch = await CreateDispatchAsync(room, metadataJson, ct);
        if (!dispatch.Ok) return dispatch;
        var dial = await CreateSipParticipantAsync(room, phone, displayName, ct);
        if (!dial.Ok) await DeleteRoomAsync(room, ct);
        return dial;
    }

    private async Task<LiveKitResult> TwirpAsync(string service, string method, object body, JsonObject grants,
        CancellationToken ct)
    {
        var s = Settings;
        if (s.MissingForServer() is string reason) return new LiveKitResult(false, 503, reason);
        try
        {
            var client = _http.CreateClient("livekit");
            client.Timeout = TimeSpan.FromSeconds(30);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{s.HttpUrl}/twirp/livekit.{service}/{method}")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.TryAddWithoutValidation("Authorization",
                "Bearer " + MintToken(s.ApiKey, s.ApiSecret, ServerTokenTtl, grants));
            using var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return new LiveKitResult(true, (int)resp.StatusCode, "");
            return new LiveKitResult(false, (int)resp.StatusCode,
                $"LiveKit {service}.{method} failed ({(int)resp.StatusCode}): {TwirpMessage(text)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new LiveKitResult(false, 502, $"LiveKit unreachable at {s.HttpUrl} ({e.Message}).");
        }
    }

    /// <summary>Twirp errors are <c>{"code","msg"}</c>; anything else is passed through, trimmed.</summary>
    private static string TwirpMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? "";
        }
        catch (JsonException) { }
        text = text.Trim();
        return text.Length > 300 ? text[..300] : text;
    }
}
