using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Connecting an AI app by SIGNING IN, instead of pasting a key.
///
/// A personal API key works, but it asks a lot of someone who is not a developer: open
/// the developers page, create a key, copy a secret out of a one-time dialog, find where
/// the AI app keeps its headers, paste it there without losing a character. Every step is
/// a place to give up — and a secret in a clipboard is a secret in a clipboard.
///
/// So ChatBranch also speaks OAuth 2.1, which is what MCP clients already use: the app
/// registers itself, opens a page, the person signs in the way they always do and presses
/// Allow. No secret is ever shown to anyone.
///
/// What the app gets ACTS AS THAT PERSON — the same role, capabilities and entity scope a
/// personal key would have, and the same narrowing when they chose read-only. It is
/// deliberately marked <c>auth_source=api</c>, exactly like a key, so every guard that
/// already separates "a person at the keyboard" from "a program acting for them" keeps
/// working with no changes: the account routes still refuse it, Messenger's human-agent
/// window still does not open for it, and a file it sends does not answer a handover.
/// </summary>
public static class McpOAuth
{
    /// <summary>Access tokens. Short-lived; the refresh token is how an app keeps going.</summary>
    public const string AccessPrefix = "cbo_";

    /// <summary>Refresh tokens, which are never sent to the MCP endpoint.</summary>
    public const string RefreshPrefix = "cbr_";

    public const string ClientClaim = "oauth_client";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(60);

    /// <summary>How long a person has to finish a sign-in once the app opens the page.</summary>
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long the code is worth anything after Allow (RFC 6749 says keep it short).</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>"Everything I can do" — the scope an app asks for by default.</summary>
    public const string ScopeFull = "chatbranch";

    /// <summary>"Read only" — every capability its owner holds that changes nothing.</summary>
    public const string ScopeRead = "chatbranch:read";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string SecretAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // ---- tokens -------------------------------------------------------------

    /// <summary>A fresh token pair for an approved authorization.</summary>
    public static (string Access, string Refresh, OAuthToken Row) Mint(string clientId, int userId,
        IEnumerable<string> scopes, string requested)
    {
        var (accessPrefix, accessSecret) = Pair();
        var (refreshPrefix, refreshSecret) = Pair();
        var now = DateTime.UtcNow;
        var row = new OAuthToken
        {
            ClientId = clientId,
            UserId = userId,
            Scopes = scopes.Distinct().ToList(),
            Requested = requested,
            AccessPrefix = accessPrefix,
            AccessHash = Sha256(accessSecret),
            RefreshPrefix = refreshPrefix,
            RefreshHash = Sha256(refreshSecret),
            ExpiresAt = now.Add(AccessLifetime),
            RefreshExpiresAt = now.Add(RefreshLifetime),
            CreatedAt = now,
        };
        return ($"{AccessPrefix}{accessPrefix}_{accessSecret}", $"{RefreshPrefix}{refreshPrefix}_{refreshSecret}", row);
    }

    /// <summary>
    /// Rotate: a refresh token is spent the moment it is used, and the row gets a new
    /// pair. A replayed refresh token therefore buys nothing, and the person's
    /// "Connected apps" entry stays the same one.
    /// </summary>
    public static (string Access, string Refresh) Rotate(OAuthToken row)
    {
        var (accessPrefix, accessSecret) = Pair();
        var (refreshPrefix, refreshSecret) = Pair();
        var now = DateTime.UtcNow;
        row.AccessPrefix = accessPrefix;
        row.AccessHash = Sha256(accessSecret);
        row.RefreshPrefix = refreshPrefix;
        row.RefreshHash = Sha256(refreshSecret);
        row.ExpiresAt = now.Add(AccessLifetime);
        row.RefreshExpiresAt = now.Add(RefreshLifetime);
        return ($"{AccessPrefix}{accessPrefix}_{accessSecret}", $"{RefreshPrefix}{refreshPrefix}_{refreshSecret}");
    }

    /// <summary>The OAuth access token on this request, if any (Bearer only).</summary>
    public static string? Presented(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        return token.StartsWith(AccessPrefix, StringComparison.Ordinal) ? token : null;
    }

    /// <summary>
    /// The live token behind an access token, with its user + profile loaded — or null
    /// when it is malformed, unknown, revoked, expired, or its user is inactive.
    /// </summary>
    public static Task<OAuthToken?> ResolveAccessAsync(AppDbContext db, string token) =>
        ResolveAsync(db, token, AccessPrefix,
            (q, prefix) => q.Where(t => t.AccessPrefix == prefix),
            t => t.AccessHash, t => t.ExpiresAt);

    /// <summary>The live token behind a REFRESH token (the token endpoint's only use for one).</summary>
    public static Task<OAuthToken?> ResolveRefreshAsync(AppDbContext db, string token) =>
        ResolveAsync(db, token, RefreshPrefix,
            (q, prefix) => q.Where(t => t.RefreshPrefix == prefix),
            t => t.RefreshHash, t => t.RefreshExpiresAt);

    private static async Task<OAuthToken?> ResolveAsync(AppDbContext db, string token, string kind,
        Func<IQueryable<OAuthToken>, string, IQueryable<OAuthToken>> byPrefix,
        Func<OAuthToken, string> hashOf, Func<OAuthToken, DateTime> expiryOf)
    {
        if (!token.StartsWith(kind, StringComparison.Ordinal)) return null;
        var rest = token[kind.Length..];
        var sep = rest.IndexOf('_');
        if (sep <= 0 || sep == rest.Length - 1) return null;
        var prefix = rest[..sep];
        var secret = rest[(sep + 1)..];

        OAuthToken? row;
        try
        {
            row = await byPrefix(db.OAuthTokens.Include(t => t.User!).ThenInclude(u => u.Profile), prefix)
                .FirstOrDefaultAsync();
        }
        catch { return null; }
        if (row == null || row.RevokedAt != null) return null;
        if (expiryOf(row) <= DateTime.UtcNow) return null;
        if (row.User is not { IsActive: true } || row.User.Profile == null) return null;

        var expected = Encoding.ASCII.GetBytes(hashOf(row));
        var actual = Encoding.ASCII.GetBytes(Sha256(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? row : null;
    }

    /// <summary>
    /// Mark an authenticated principal as coming from a connected app. The source is
    /// <c>api</c> ON PURPOSE: a connected app is a program acting for someone, so every
    /// existing guard that treats an API key that way applies to it unchanged. The client
    /// is named in its own claim, for the activity log and "Connected apps".
    /// </summary>
    public static void Decorate(ClaimsPrincipal principal, OAuthToken row)
    {
        if (principal.Identity is not ClaimsIdentity id) return;
        id.AddClaim(new Claim(ApiKeyAuth.SourceClaim, "api"));
        id.AddClaim(new Claim(ClientClaim, row.ClientId));
        id.AddClaim(new Claim("user_id", row.UserId.ToString()));
        if (row.Scopes.Count > 0)
            id.AddClaim(new Claim(ApiKeyAuth.ScopesClaim, string.Join(",", row.Scopes)));
    }

    /// <summary>Bookkeeping, at most once a minute — never a write per request.</summary>
    public static async Task TouchAsync(AppDbContext db, OAuthToken row)
    {
        if (row.LastUsedAt is DateTime last && DateTime.UtcNow - last <= TimeSpan.FromMinutes(1)) return;
        try
        {
            var now = DateTime.UtcNow;
            await db.OAuthTokens.Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));
            row.LastUsedAt = now;
        }
        catch { /* never fail auth over bookkeeping */ }
    }

    // ---- what a person is agreeing to ---------------------------------------

    /// <summary>
    /// The capability ids a token should carry for what the app asked for. Empty means
    /// "everything its owner can do", which is how an unscoped personal key behaves;
    /// read-only means every capability the person holds that changes nothing, so the
    /// app can never send a message or edit a setting even by accident.
    /// </summary>
    public static async Task<List<string>> ScopesForAsync(AppDbContext db, string role, bool readOnly)
    {
        if (!readOnly) return new List<string>();
        var caps = await AdminEndpoints.CapabilitiesForAsync(db, role);
        return caps.Where(c => !ApiKeyAuth.IsWriteCapability(c)).Distinct().OrderBy(c => c).ToList();
    }

    /// <summary>Did the app ask for read-only access?</summary>
    public static bool WantsReadOnly(string? requestedScope) =>
        (requestedScope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Any(s => string.Equals(s, ScopeRead, StringComparison.OrdinalIgnoreCase));

    // ---- PKCE ----------------------------------------------------------------

    /// <summary>
    /// Does <paramref name="verifier"/> prove the challenge the app sent? S256 only:
    /// <c>plain</c> is not accepted, because it proves nothing an interceptor could not
    /// also produce.
    /// </summary>
    public static bool VerifyPkce(string challenge, string method, string verifier)
    {
        if (challenge.Length == 0 || verifier.Length is < 43 or > 128) return false;
        if (!string.Equals(method, "S256", StringComparison.OrdinalIgnoreCase)) return false;
        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    // ---- small helpers -------------------------------------------------------

    public static string NewClientId() => "cbc_" + RandomNumberGenerator.GetString(Alphabet, 24);

    public static string NewRequestId() => RandomNumberGenerator.GetString(Alphabet, 32);

    /// <summary>A fresh authorization code and the hash to store for it.</summary>
    public static (string Code, string Hash) NewCode()
    {
        var code = RandomNumberGenerator.GetString(SecretAlphabet, 48);
        return (code, Sha256(code));
    }

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static (string Prefix, string Secret) Pair() =>
        (RandomNumberGenerator.GetString(Alphabet, 10), RandomNumberGenerator.GetString(SecretAlphabet, 48));

    private static string Base64Url(byte[] raw) =>
        Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
