using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// Google OAuth2 with PKCE and a signed, time-limited <c>state</c>, plus lazy
/// token refresh. Ported from <c>backend/integrations/oauth.py</c>; the C# API
/// previously answered every connect attempt with "not available in this
/// backend yet", so no Google account could be linked at all.
///
/// The rules this class exists to enforce:
/// <list type="bullet">
/// <item><b>state is signed and expires.</b> It carries the integration pk and
/// a nonce under an HMAC; the callback rejects a tampered, foreign or
/// &gt;10-minute-old state. That IS the CSRF control — the browser arrives from
/// accounts.google.com with no Authorization header, so the callback cannot be
/// bearer-authenticated.</item>
/// <item><b>PKCE (S256).</b> The verifier is generated at start, stored on the
/// row (never serialized out) and sent once at exchange; only the challenge
/// travels through the browser.</item>
/// <item><b>Lazy refresh.</b> <see cref="AccessTokenAsync"/> refreshes only when
/// the stored token is near expiry, and a FAILED refresh marks the row
/// disconnected and returns "" — it never throws into a flow or send path.</item>
/// <item><b>Nothing secret is logged or echoed.</b> Error messages quote a
/// status code and Google's error slug, never a token or a response body.</item>
/// </list>
/// </summary>
public class GoogleOAuth
{
    public const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const int StateMaxAgeSeconds = 600;
    private const int RefreshSkewSeconds = 60;
    private const string StateSecretKey = "oauth_state_secret";

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly IHttpClientFactory _http;

    public GoogleOAuth(AppDbContext db, ScopingService scoping, IHttpClientFactory http)
    {
        _db = db;
        _scoping = scoping;
        _http = http;
    }

    /// <summary>Operator-facing failure. Never carries a token or a raw body.</summary>
    public class OAuthException : Exception
    {
        public OAuthException(string message) : base(message) { }
    }

    // ---- PKCE ---------------------------------------------------------------

    private static string MakeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64Url(bytes)[..Math.Min(128, Base64Url(bytes).Length)];
    }

    private static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] raw) =>
        Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- signed state -------------------------------------------------------

    /// <summary>
    /// The HMAC key for state signing. Generated once and stored, so links stay
    /// valid across restarts (a per-process key would invalidate any connect
    /// round-trip that spans a deploy).
    /// </summary>
    private byte[] StateSecret()
    {
        var stored = _scoping.SettingValue(StateSecretKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = Base64Url(RandomNumberGenerator.GetBytes(32));
            _scoping.SetSetting(StateSecretKey, stored);
        }
        return Encoding.UTF8.GetBytes(stored);
    }

    private string SignState(int integrationId, string provider, string returnTo = "")
    {
        var payload = JsonSerializer.Serialize(new
        {
            i = integrationId,
            p = provider,
            n = Base64Url(RandomNumberGenerator.GetBytes(9)),
            t = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            r = returnTo,
        });
        var body = Base64Url(Encoding.UTF8.GetBytes(payload));
        var sig = Base64Url(HMACSHA256.HashData(StateSecret(), Encoding.ASCII.GetBytes(body)));
        return $"{body}.{sig}";
    }

    /// <summary><paramref name="ReturnTo"/> = which dashboard page the callback
    /// lands on (a key the caller allowlists, "" = the default).</summary>
    public record StateData(int IntegrationId, string Provider, string ReturnTo = "");

    /// <summary>Throws <see cref="OAuthException"/> on a tampered or expired state.</summary>
    public StateData VerifyState(string? state)
    {
        var parts = (state ?? "").Split('.');
        if (parts.Length != 2)
            throw new OAuthException("Invalid OAuth state — the request did not originate here.");
        var expected = Base64Url(HMACSHA256.HashData(StateSecret(), Encoding.ASCII.GetBytes(parts[0])));
        // constant-time: a length-independent compare avoids leaking the signature
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1])))
            throw new OAuthException("Invalid OAuth state — the request did not originate here.");
        try
        {
            var json = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var issued = root.GetProperty("t").GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued > StateMaxAgeSeconds)
                throw new OAuthException("This connect link expired — start the connection again.");
            return new StateData(root.GetProperty("i").GetInt32(),
                                 root.GetProperty("p").GetString() ?? "",
                                 root.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.String
                                     ? r.GetString() ?? "" : "");
        }
        catch (OAuthException) { throw; }
        catch { throw new OAuthException("Invalid OAuth state payload."); }
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    // ---- authorize ----------------------------------------------------------

    /// <summary>
    /// The Google consent URL for this row (PKCE + signed state). Throws when no
    /// Google client is registered — the sandbox case the caller turns into a
    /// 400 with an operator-facing reason, attempting no network call.
    /// </summary>
    public string AuthorizeUrl(Integration integration, string[] scopes, string redirectUri, string returnTo = "")
    {
        var clientId = IntegrationSettings.Value(_scoping, "google_client_id");
        var clientSecret = IntegrationSettings.Value(_scoping, "google_client_secret");
        if (clientId.Length == 0 || clientSecret.Length == 0)
            throw new OAuthException(
                "Google OAuth is not configured. Add a client id + secret in "
                + "Settings → App & Provider Keys before connecting a Google account.");

        var verifier = MakeVerifier();
        var creds = new Dictionary<string, object?>(integration.Credentials)
        {
            ["code_verifier"] = verifier,
        };
        integration.Credentials = creds;
        integration.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", scopes),
            ["access_type"] = "offline",          // we want a refresh_token
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",  // incremental authorization
            ["code_challenge"] = ChallengeFor(verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = SignState(integration.Id, integration.Provider, returnTo),
        };
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthUrl}?{qs}";
    }

    // ---- token exchange + refresh -------------------------------------------

    private async Task<Dictionary<string, JsonElement>> PostTokenAsync(
        Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            resp = await client.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        }
        catch (Exception e)
        {
            throw new OAuthException($"Could not reach Google's token endpoint: {e.GetType().Name}");
        }
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // status + Google's own error slug only — the body carries tokens
            var slug = "";
            try
            {
                using var err = JsonDocument.Parse(text);
                slug = err.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            }
            catch { /* non-JSON error body */ }
            throw new OAuthException($"Google rejected the token request (HTTP {(int)resp.StatusCode}"
                                     + (slug.Length > 0 ? $", {slug}" : "") + ").");
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) ?? new();
        }
        catch
        {
            throw new OAuthException("Google returned a token response we could not parse.");
        }
    }

    private void StoreTokens(Integration integration, Dictionary<string, JsonElement> data)
    {
        var creds = new Dictionary<string, object?>(integration.Credentials);
        creds.Remove("code_verifier");   // single-use
        if (data.TryGetValue("access_token", out var at) && at.ValueKind == JsonValueKind.String)
            creds["access_token"] = at.GetString();
        if (data.TryGetValue("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
            creds["refresh_token"] = rt.GetString();
        creds["token_type"] = data.TryGetValue("token_type", out var tt) && tt.ValueKind == JsonValueKind.String
            ? tt.GetString() : "Bearer";
        var expiresIn = data.TryGetValue("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt32() : 0;
        creds["expires_at"] = expiresIn > 0
            ? DateTime.UtcNow.AddSeconds(expiresIn).ToString("o")
            : "";
        integration.Credentials = creds;
        if (data.TryGetValue("scope", out var sc) && sc.ValueKind == JsonValueKind.String)
            integration.Scopes = (sc.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        integration.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    /// <summary>How long after a successful exchange a repeat of the callback still
    /// counts as "already connected" (the browser loading the callback twice).</summary>
    public static readonly TimeSpan DuplicateCallbackWindow = TimeSpan.FromMinutes(2);

    /// <summary>The row finished an exchange moments ago: healthy, holding a token,
    /// no verifier left, updated within <see cref="DuplicateCallbackWindow"/>.</summary>
    public static bool JustConnected(Integration i) =>
        i.Status == "healthy"
        && Cred(i, "access_token").Length > 0
        && Cred(i, "code_verifier").Length == 0
        && DateTime.UtcNow - i.UpdatedAt <= DuplicateCallbackWindow;

    /// <summary>
    /// Swap an authorization code for tokens and store them. Safe to run twice:
    /// browsers sometimes load the callback twice with the same URL (a reload,
    /// a prefetch). The first request uses the single-use verifier; a repeat that
    /// finds the row just connected — verifier already gone, or Google refusing
    /// the reused code because the first request won the race — returns false
    /// ("already connected") instead of an error. True = this call connected it.
    /// </summary>
    public async Task<bool> ExchangeCodeAsync(Integration integration, string code, string redirectUri,
        CancellationToken ct = default)
    {
        var verifier = Cred(integration, "code_verifier");
        if (verifier.Length == 0)
        {
            if (JustConnected(integration)) return false;
            throw new OAuthException("This connection was not started here (missing PKCE verifier).");
        }
        Dictionary<string, JsonElement> data;
        try
        {
            data = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = IntegrationSettings.Value(_scoping, "google_client_id"),
                ["client_secret"] = IntegrationSettings.Value(_scoping, "google_client_secret"),
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
            }, ct);
        }
        catch (OAuthException)
        {
            // the twin request may have exchanged this code a moment ago
            try { await _db.Entry(integration).ReloadAsync(ct); } catch { /* keep the original error */ }
            if (JustConnected(integration)) return false;
            throw;
        }
        if (!data.ContainsKey("access_token"))
            throw new OAuthException("Google's response carried no access token.");
        StoreTokens(integration, data);
        SetStatus(integration, "healthy", "");
        return true;
    }

    private bool Expired(Integration integration)
    {
        var raw = Cred(integration, "expires_at");
        if (raw.Length == 0) return false;  // no expiry recorded — a 401 will correct us
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
               && DateTime.UtcNow >= when.ToUniversalTime().AddSeconds(-RefreshSkewSeconds);
    }

    /// <summary>
    /// Trade the refresh token for a new access token. On failure the row goes
    /// "disconnected" and false comes back — callers treat that as "not
    /// connected" and skip rather than blowing up a flow run.
    /// </summary>
    public async Task<bool> RefreshAsync(Integration integration, CancellationToken ct = default)
    {
        var token = Cred(integration, "refresh_token");
        var clientId = IntegrationSettings.Value(_scoping, "google_client_id");
        var clientSecret = IntegrationSettings.Value(_scoping, "google_client_secret");
        if (token.Length == 0 || clientId.Length == 0 || clientSecret.Length == 0)
        {
            SetStatus(integration, "disconnected", "no refresh token stored — reconnect this account");
            return false;
        }
        Dictionary<string, JsonElement> data;
        try
        {
            data = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = token,
                ["grant_type"] = "refresh_token",
            }, ct);
        }
        catch (OAuthException e)
        {
            SetStatus(integration, "disconnected", Trim(e.Message, 200));
            return false;
        }
        if (!data.ContainsKey("access_token"))
        {
            SetStatus(integration, "disconnected", "refresh returned no access token — reconnect this account");
            return false;
        }
        StoreTokens(integration, data);
        return true;
    }

    /// <summary>
    /// The usable bearer token, refreshing lazily. Returns "" when the row has
    /// no credentials, the refresh failed, or anything unexpected happened —
    /// NEVER throws. "" always means "treat this as not connected".
    /// </summary>
    public async Task<string> AccessTokenAsync(Integration? integration, CancellationToken ct = default)
    {
        try
        {
            if (integration == null) return "";
            var token = Cred(integration, "access_token");
            if (token.Length == 0) return "";
            if (Expired(integration))
            {
                if (!await RefreshAsync(integration, ct)) return "";
                token = Cred(integration, "access_token");
            }
            return token;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Forget the stored grant (the operator disconnected). Google's revoke
    /// endpoint is deliberately NOT called — a shared client may hold other
    /// grants for the same user.
    /// </summary>
    public void RevokeLocal(Integration integration)
    {
        integration.Credentials = new Dictionary<string, object?>();
        integration.Scopes = new List<string>();
        integration.UpdatedAt = DateTime.UtcNow;
        SetStatus(integration, "revoked", "disconnected by an operator");
    }

    private void SetStatus(Integration integration, string status, string detail)
    {
        integration.Status = status;
        integration.StatusDetail = detail;
        integration.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    public static string Cred(Integration i, string key) =>
        i.Credentials.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
