using System.Security.Claims;
using System.Text.Encodings.Web;
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
    private readonly AppDbContext _db;

    public TokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, AppDbContext db)
        : base(options, logger, encoder) => _db = db;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var profile = await ResolveProfileAsync();
        if (profile != null && profile.User is { IsActive: true })
            return AuthenticateResult.Success(Ticket(profile.User.Username, profile.Role,
                profile.Id, profile.TenantId, profile.ResourceId));

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

    private async Task<Profile?> ResolveProfileAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ")) return null;
        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return null;

        var session = await _db.AuthTokens
            .Include(t => t.Profile!).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(t => t.Token == token);
        if (session?.Profile != null) return session.Profile;

        return await _db.Profiles.Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Token == token);
    }

    private AuthenticationTicket Ticket(string username, string role, int? profileId, int? tenantId,
        int? resourceId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role),
        };
        if (profileId is int pid) claims.Add(new Claim("profile_id", pid.ToString()));
        if (tenantId is int tid) claims.Add(new Claim("tenant_id", tid.ToString()));
        // Provider login: the caller IS this Service Provider — data queries scope
        // to their own world (see ProviderScope).
        if (resourceId is int rid) claims.Add(new Claim("resource_id", rid.ToString()));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }
}
