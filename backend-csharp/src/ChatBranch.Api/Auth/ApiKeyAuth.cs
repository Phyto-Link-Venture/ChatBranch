using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Personal API keys — the credential behind the REST API for scripts and the
/// MCP server for AI apps. Format: <c>cbk_&lt;prefix&gt;_&lt;secret&gt;</c>, shown once.
/// Only the prefix (lookup) and a SHA-256 of the secret are stored, compared in
/// constant time.
///
/// A key IS its owning user: same role, capabilities and entity scope. Two
/// claims mark the difference — <c>auth_source=api</c> (the activity log's
/// "api" source) and, when the key was minted with scopes, <c>api_scopes</c>,
/// which narrows the capability set further (never widens it).
/// </summary>
public static class ApiKeyAuth
{
    public const string TokenPrefix = "cbk_";
    public const string SourceClaim = "auth_source";
    public const string KeyIdClaim = "api_key_id";
    public const string ScopesClaim = "api_scopes";
    public const string HeaderName = "X-Api-Key";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string SecretAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A fresh key row + the one-time token to hand to the user.</summary>
    public static (string Token, ApiKey Row) Mint(int userId, string name, IEnumerable<string>? scopes,
        DateTime? expiresAt)
    {
        var prefix = RandomNumberGenerator.GetString(Alphabet, 10);
        var secret = RandomNumberGenerator.GetString(SecretAlphabet, 40);
        var row = new ApiKey
        {
            UserId = userId,
            Name = name,
            Prefix = prefix,
            Hash = Sha256(secret),
            Scopes = (scopes ?? Array.Empty<string>()).Distinct().ToList(),
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        };
        return ($"{TokenPrefix}{prefix}_{secret}", row);
    }

    /// <summary>The API-key token on this request (X-Api-Key, or a Bearer cbk_… token), if any.</summary>
    public static string? Presented(HttpRequest request)
    {
        var header = request.Headers[HeaderName].ToString().Trim();
        if (header.StartsWith(TokenPrefix, StringComparison.Ordinal)) return header;
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return token;
        }
        return null;
    }

    /// <summary>
    /// The live key behind a token, with its user + profile loaded — or null when
    /// it is malformed, unknown, revoked, expired, or its user is inactive.
    /// </summary>
    public static async Task<ApiKey?> ResolveAsync(AppDbContext db, string token)
    {
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var rest = token[TokenPrefix.Length..];
        var sep = rest.IndexOf('_');
        if (sep <= 0 || sep == rest.Length - 1) return null;
        var prefix = rest[..sep];
        var secret = rest[(sep + 1)..];

        ApiKey? key;
        try
        {
            key = await db.ApiKeys.Include(k => k.User!).ThenInclude(u => u.Profile)
                .FirstOrDefaultAsync(k => k.Prefix == prefix);
        }
        catch { return null; }
        if (key == null || key.RevokedAt != null) return null;
        if (key.ExpiresAt is DateTime exp && exp <= DateTime.UtcNow) return null;
        if (key.User is not { IsActive: true } || key.User.Profile == null) return null;

        var expected = Encoding.ASCII.GetBytes(key.Hash);
        var actual = Encoding.ASCII.GetBytes(Sha256(secret));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        // Last-used is bookkeeping, not an audit trail: refresh at most once a
        // minute so a busy script does not turn every request into a write.
        if (key.LastUsedAt is not DateTime last || DateTime.UtcNow - last > TimeSpan.FromMinutes(1))
        {
            try
            {
                var now = DateTime.UtcNow;
                await db.ApiKeys.Where(k => k.Id == key.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
                key.LastUsedAt = now;
            }
            catch { /* never fail auth over bookkeeping */ }
        }
        return key;
    }

    /// <summary>Mark an authenticated principal as coming from this key.</summary>
    public static void Decorate(ClaimsPrincipal principal, ApiKey key)
    {
        if (principal.Identity is not ClaimsIdentity id) return;
        id.AddClaim(new Claim(SourceClaim, "api"));
        id.AddClaim(new Claim(KeyIdClaim, key.Id.ToString()));
        id.AddClaim(new Claim("user_id", key.UserId.ToString()));
        if (key.Scopes.Count > 0) id.AddClaim(new Claim(ScopesClaim, string.Join(",", key.Scopes)));
    }

    public static bool IsApiKey(ClaimsPrincipal me) => me.FindFirstValue(SourceClaim) == "api";

    /// <summary>The key's scope list, or null when the key is unscoped (or not a key).</summary>
    public static HashSet<string>? Scopes(ClaimsPrincipal me)
    {
        var raw = me.FindFirstValue(ScopesClaim);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
    }

    private static readonly AsyncLocal<HashSet<string>?> RequestScopes = new();

    /// <summary>
    /// The scopes of the key THIS request came in on (null = a session or an
    /// unscoped key), made ambient by <see cref="UseKeyScopes"/> so every
    /// capability check — the route filters AND the in-handler
    /// <c>AdminEndpoints.HasCapabilityAsync</c> calls — narrows to them, on REST
    /// as on MCP. Without it a scoped key passed any check its owner's ROLE passed.
    /// </summary>
    public static HashSet<string>? CurrentScopes => RequestScopes.Value;

    /// <summary>After UseAuthentication: publish the request's key scopes to the async flow.</summary>
    public static IApplicationBuilder UseKeyScopes(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            RequestScopes.Value = Scopes(ctx.User);
            await next(ctx);
        });

    /// <summary>A capability that changes something (as opposed to viewing).</summary>
    public static bool IsWriteCapability(string cap) =>
        cap.EndsWith(".manage", StringComparison.Ordinal) || cap.EndsWith(".send", StringComparison.Ordinal);

    /// <summary>A scoped key that holds no write capability at all — read-only on REST.</summary>
    public static bool IsReadOnlyKey(ClaimsPrincipal me) =>
        Scopes(me) is { } s && !s.Any(IsWriteCapability);

    /// <summary>
    /// What this caller may actually do: the role's capabilities, intersected
    /// with the key's scopes when the request came in on a scoped key.
    /// </summary>
    public static async Task<HashSet<string>> EffectiveCapabilitiesAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var caps = (await AdminEndpoints.CapabilitiesForAsync(db, role)).ToHashSet();
        if (Scopes(me) is { } scopes) caps.IntersectWith(scopes);
        return caps;
    }

    private static string Sha256(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
