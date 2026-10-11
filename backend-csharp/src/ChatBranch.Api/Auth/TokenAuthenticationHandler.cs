using System.Security.Claims;
using System.Text.Encodings.Web;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Bearer-token auth ported from <c>crm/auth.py</c>:
///   * <c>Authorization: Bearer &lt;token&gt;</c> → an AuthToken session row
///     (one per login, concurrent sessions allowed), falling back to a
///     directly-set <c>Profile.Token</c> (scripts/tests).
///   * Bootstrap: while NO Profile rows exist yet (fresh DB before seed), the
///     legacy <c>X-Role</c> header is honoured so the app isn't bricked.
/// The resolved role is emitted as a <see cref="ClaimTypes.Role"/> claim so the
/// authorization policies (admin+, superadmin) work with <c>[Authorize]</c>.
/// </summary>
public class TokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";
    /// <summary>The session's <see cref="AuthToken.Restriction"/> ("" = none) — Security/SessionGate reads it.</summary>
    public const string RestrictionClaim = "session_restriction";
    private readonly AppDbContext _db;
    private readonly LoginThrottle? _throttle;

    public TokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, LoginThrottle? throttle = null)
        : base(options, logger, encoder)
    {
        _db = db;
        _throttle = throttle;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // API keys (cbk_…, Bearer or X-Api-Key) act as their owning user, marked
        // auth_source=api and optionally narrowed by the key's scopes (ApiKeyAuth).
        if (ApiKeyAuth.Presented(Request) is string apiKey)
        {
            var key = await ApiKeyAuth.ResolveAsync(_db, apiKey);
            if (key?.User?.Profile is not Profile kp)
            {
                // repeated bad keys from one IP lock that IP out of key sign-in (SessionGate.UseApiKeyGate → 429)
                if (_throttle?.ApiKeyFail(ClientIp.Of(Context)) is { } locked)
                    LoginThrottle.Report(_db, new[] { locked }, "API keys");
                return AuthenticateResult.Fail("Invalid API key.");
            }
            var keyTicket = Ticket(key.User.Username, kp.Role, kp.Id, kp.TenantId);
            ApiKeyAuth.Decorate(keyTicket.Principal, key);
            return AuthenticateResult.Success(keyTicket);
        }

        var (profile, restriction) = await ResolveProfileAsync();
        if (profile != null && profile.User is { IsActive: true })
        {
            var ticket = Ticket(profile.User.Username, profile.Role, profile.Id, profile.TenantId);
            if (ticket.Principal.Identity is ClaimsIdentity id)
            {
                id.AddClaim(new Claim("user_id", profile.UserId.ToString()));
                if (restriction.Length > 0) id.AddClaim(new Claim(RestrictionClaim, restriction));
            }
            return AuthenticateResult.Success(ticket);
        }

        // Pre-seed bootstrap: no Profile rows yet → honour X-Role.
        if (!await _db.Profiles.AnyAsync())
        {
            var raw = (Request.Headers["X-Role"].ToString() ?? "")
                .Replace("-", "").Replace(" ", "").ToLowerInvariant();
            var role = raw is Roles.SuperAdmin or Roles.Admin or Roles.Agent ? raw : Roles.Admin;
            return AuthenticateResult.Success(Ticket("admin", role, null, null));
        }

        return AuthenticateResult.NoResult(); // anonymous; policies decide 401/403
    }

    private async Task<(Profile? Profile, string Restriction)> ResolveProfileAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ")) return (null, "");
        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return (null, "");

        var session = await _db.AuthTokens
            .Include(t => t.Profile!).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(t => t.Token == token);
        if (session?.Profile != null) return (session.Profile, session.Restriction ?? "");

        return (await _db.Profiles.Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Token == token), "");
    }

    private AuthenticationTicket Ticket(string username, string role, int? profileId, int? tenantId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role),
        };
        if (profileId is int pid) claims.Add(new Claim("profile_id", pid.ToString()));
        if (tenantId is int tid) claims.Add(new Claim("tenant_id", tid.ToString()));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }
}
