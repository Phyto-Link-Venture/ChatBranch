using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Security;

/// <summary>
/// One-time set-password links (<see cref="PasswordToken"/>, <c>crm_passwordtoken</c>):
/// the welcome email of a new login and an admin's "password reset link". A token is
/// 256 random bits, URL-safe; only its SHA-256 is stored, so a database copy can't be
/// turned into working links. A link works ONCE, until it expires
/// (<see cref="SecurityOptions.InviteLinkHours"/>, 72 h); issuing a new one, deactivating
/// the user, changing their email or an admin setting their password revokes the older
/// ones. No email ever carries a password.
/// </summary>
public static class PasswordLinks
{
    /// <summary>A fresh token: 32 random bytes, base64url (43 characters).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");

    /// <summary>Lowercase hex SHA-256 — what the table stores.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();

    /// <summary>A token that can't be one of ours (wrong length / alphabet) is never looked up.</summary>
    public static bool Plausible(string? token) =>
        token is { Length: >= 40 and <= 64 } t && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// The live row behind a presented token with its (active) user, or null — unknown, used,
    /// revoked, expired and inactive all look the same to the caller. The row is found by the
    /// hash (an index lookup on a value an attacker can't steer) and the stored hash is then
    /// compared in constant time.
    /// </summary>
    public static async Task<(PasswordToken Row, AppUser User)?> FindLiveAsync(AppDbContext db, string? token)
    {
        if (!Plausible(token)) return null;
        var hash = Hash(token!);
        var row = await db.PasswordTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (row == null
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(hash))
            || row.UsedAt != null || row.RevokedAt != null || row.ExpiresAt <= DateTime.UtcNow)
            return null;
        var user = await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == row.UserId);
        return user is { IsActive: true } ? (row, user) : null;
    }

    /// <summary>Mark the link used — atomically, so two tabs submitting at once succeed once.</summary>
    public static async Task<bool> ClaimAsync(AppDbContext db, PasswordToken row)
    {
        var now = DateTime.UtcNow;
        var n = await db.PasswordTokens
            .Where(t => t.Id == row.Id && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now));
        return n == 1;
    }

    /// <summary>Revoke every live link of a user (a newer link, a deactivation, an email or password change).</summary>
    public static Task<int> RevokeAllAsync(AppDbContext db, int userId)
    {
        var now = DateTime.UtcNow;
        return db.PasswordTokens.Where(t => t.UserId == userId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    /// <summary>Links sent to this user within the last hour (the per-person send limit).</summary>
    public static Task<int> SentLastHourAsync(AppDbContext db, int userId)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        return db.PasswordTokens.CountAsync(t => t.UserId == userId && t.CreatedAt > since);
    }

    /// <summary>When the oldest of the last hour's links leaves the window (for Retry-After).</summary>
    public static async Task<int> RetryAfterSecondsAsync(AppDbContext db, int userId)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var oldest = await db.PasswordTokens.Where(t => t.UserId == userId && t.CreatedAt > since)
            .OrderBy(t => t.CreatedAt).Select(t => (DateTime?)t.CreatedAt).FirstOrDefaultAsync();
        return oldest is DateTime o ? Math.Max(1, (int)Math.Ceiling((o.AddHours(1) - DateTime.UtcNow).TotalSeconds)) : 60;
    }

    /// <summary>Revoke every live link of a user except <paramref name="keepId"/> (the one just sent).</summary>
    public static Task<int> RevokeOthersAsync(AppDbContext db, int userId, int keepId)
    {
        var now = DateTime.UtcNow;
        return db.PasswordTokens.Where(t => t.UserId == userId && t.Id != keepId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    /// <summary>
    /// A new link for <paramref name="userId"/>; rows that are long dead (expired more than 30
    /// days ago) are pruned. The row is added to the context, not saved — and the older live
    /// links are NOT revoked here: the sender revokes them once the new one has really gone
    /// out (<see cref="RevokeOthersAsync"/>), so a failed send never kills a working link.
    /// </summary>
    public static async Task<(string Token, PasswordToken Row)> IssueAsync(AppDbContext db, int userId,
        string purpose, string createdBy, TimeSpan lifetime)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await db.PasswordTokens.Where(t => t.UserId == userId && t.ExpiresAt < cutoff).ExecuteDeleteAsync();
        var token = NewToken();
        var now = DateTime.UtcNow;
        var row = new PasswordToken
        {
            UserId = userId, TokenHash = Hash(token), Purpose = purpose,
            CreatedBy = SystemEmail.OneLine(createdBy, 150), CreatedAt = now, ExpiresAt = now.Add(lifetime),
        };
        db.PasswordTokens.Add(row);
        return (token, row);
    }

    /// <summary>The newest link that is neither used nor revoked, per user (for the Users list).</summary>
    public static async Task<Dictionary<int, PasswordToken>> OpenLinksAsync(AppDbContext db, IReadOnlyCollection<int> userIds)
    {
        if (userIds.Count == 0) return new();
        var rows = await db.PasswordTokens.AsNoTracking()
            .Where(t => userIds.Contains(t.UserId) && t.UsedAt == null && t.RevokedAt == null)
            .ToListAsync();
        return rows.GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).First());
    }

    /// <summary>The Users list's view of an open link (null = none): pending or expired.</summary>
    public static object? LinkPayload(PasswordToken? t) => t == null ? null : new
    {
        status = t.ExpiresAt > DateTime.UtcNow ? "pending" : "expired",
        purpose = t.Purpose,
        sent_at = Utc(t.CreatedAt),
        expires_at = Utc(t.ExpiresAt),
        sent_by = t.CreatedBy,
    };

    public static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

/// <summary>
/// Composes and sends one welcome / reset email: checks the preconditions (an email
/// address, a way to send mail, a login address), issues the link, sends it, and audits
/// <c>user.invite_sent</c> (or <c>user.invite_failed</c>). A failed send revokes the link
/// it just made — nobody has it — and never undoes anything else.
/// </summary>
public static class InviteMailer
{
    public sealed record Outcome(bool Sent, string Code, string Error, DateTime? ExpiresAt, string Purpose, string To)
    {
        /// <summary>The <c>invite</c> object on the create / resend answers.</summary>
        public object Payload(bool requested = true) => new
        {
            requested,
            sent = Sent,
            purpose = Purpose,
            to = To.Length > 0 ? SystemEmail.MaskEmail(To) : null,
            expires_at = ExpiresAt is DateTime e ? PasswordLinks.Utc(e) : (DateTime?)null,
            code = Code.Length > 0 ? Code : null,
            error = Error.Length > 0 ? Error : null,
        };
    }

    /// <summary>What the dashboard needs before offering an invite: can mail go out, is there a login address.</summary>
    public sealed record Readiness(bool Ready, string Code, string Source, string LoginUrl)
    {
        public object Payload(int hours) => new
        {
            ready = Ready,
            problem = Code.Length > 0 ? Code : null,
            detail = Code.Length > 0 ? SystemEmail.Explain(Code) : null,
            email_source = Source,
            login_url = LoginUrl.Length > 0 ? LoginUrl : null,
            link_hours = hours,
        };
    }

    public static Readiness Check(ScopingService sc, SecurityOptions security)
    {
        var (server, source) = SystemEmail.Resolve(sc);
        var (loginBase, problem) = SystemEmail.LoginBase(security);
        var loginUrl = loginBase.Length > 0 ? loginBase + "/login" : "";
        if (server == null) return new Readiness(false, "email_not_configured", source, loginUrl);
        if (problem.Length > 0) return new Readiness(false, problem, source, loginUrl);
        return new Readiness(true, "", source, loginUrl);
    }

    /// <summary>
    /// Send a link to <paramref name="user"/>. The caller has already decided the actor may
    /// (capability + user tiers) and saves nothing for us: everything here is saved here.
    /// </summary>
    public static async Task<Outcome> SendAsync(AppDbContext db, ScopingService sc, IEmailTransport transport,
        SecurityOptions security, AppUser user, string purpose, string actorUsername, string actorName,
        CancellationToken ct = default)
    {
        var to = (user.Email ?? "").Trim();
        if (!user.IsActive) return Fail("inactive", "This user is deactivated — reactivate them first.", purpose, to);
        if (to.Length == 0) return Fail("no_email", "This user has no email address — add one first.", purpose, to);
        var (server, _) = SystemEmail.Resolve(sc);
        if (server == null) return Fail("email_not_configured", SystemEmail.Explain("email_not_configured"), purpose, to);
        var (loginBase, problem) = SystemEmail.LoginBase(security);
        if (problem.Length > 0) return Fail(problem, SystemEmail.Explain(problem), purpose, to);

        var hours = Math.Max(1, security.InviteLinkHours);
        var (token, row) = await PasswordLinks.IssueAsync(db, user.Id, purpose, actorUsername, TimeSpan.FromHours(hours));
        await db.SaveChangesAsync(ct);

        var profile = user.Profile ?? await db.Profiles.AsNoTracking().Include(p => p.Entities)
            .FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var message = WelcomeEmail.Build(new WelcomeEmail.Facts(
            Locale: LocaleOf(sc, profile?.Preferences),
            Purpose: purpose,
            Name: SystemEmail.OneLine(MyAccountEndpoints.DisplayName(user), 120),
            Username: user.Username,
            Inviter: SystemEmail.OneLine(actorName.Length > 0 ? actorName : actorUsername, 120),
            Organization: SystemEmail.OneLine(await OrganizationNameAsync(db, sc, profile), 120),
            LoginUrl: loginBase + "/login",
            LinkUrl: $"{loginBase}/set-password?token={token}",
            ExpiresAtUtc: row.ExpiresAt,
            Hours: hours,
            Zone: SafeZone(sc)));

        var result = await SystemEmail.SendAsync(transport, server, to, MyAccountEndpoints.DisplayName(user),
            message.Subject, message.Text, message.ButtonLabel, message.ButtonUrl, ct);
        var kind = purpose == PasswordTokenPurposes.Reset ? "password reset link" : "welcome email";
        if (!result.Ok)
        {
            // nobody received this link: drop it (older links keep working) and don't count it
            // against the hourly limit
            db.PasswordTokens.Remove(row);
            AuditTrail.Add(db, "user.invite_failed", user.Username,
                $"{kind} to {SystemEmail.MaskEmail(to)} not sent: {MailKitEmailTransport.Scrub(result.Detail, server)}",
                userId: user.Id);
            await db.SaveChangesAsync(ct);
            return new Outcome(false, "send_failed", MailKitEmailTransport.Scrub(result.Detail, server), null, purpose, to);
        }
        // the new link is out: every older one stops working
        await PasswordLinks.RevokeOthersAsync(db, user.Id, row.Id);
        AuditTrail.Add(db, "user.invite_sent", user.Username,
            $"{kind} to {SystemEmail.MaskEmail(to)}; the link works once, for {hours} h", userId: user.Id);
        await db.SaveChangesAsync(ct);
        return new Outcome(true, "", "", row.ExpiresAt, purpose, to);
    }

    private static Outcome Fail(string code, string error, string purpose, string to) =>
        new(false, code, error, null, purpose, to);

    /// <summary>The person's own dashboard language, else the organization default, else English.</summary>
    public static string LocaleOf(ScopingService sc, string? preferencesJson)
    {
        var doc = ChatBranch.Data.ProfilePreferences.Load(preferencesJson);
        if (doc["locale"] is JsonValue v && v.TryGetValue<string>(out var own)
            && SettingsEndpoints.SupportedLocales.Contains(own))
            return own;
        string org;
        try { org = (sc.SettingValue(SettingsEndpoints.LocaleSettingKey) ?? "").Trim().ToLowerInvariant(); }
        catch { org = ""; }
        return SettingsEndpoints.SupportedLocales.Contains(org) ? org : "en";
    }

    /// <summary>
    /// "Which organization" in the email: the top-level company of the person's first entity
    /// (a bound login), else the business name setting, else the tenant's name.
    /// </summary>
    public static async Task<string> OrganizationNameAsync(AppDbContext db, ScopingService sc, Profile? profile)
    {
        try
        {
            if (profile?.Entities.OrderBy(e => e.Id).FirstOrDefault() is Entity first)
            {
                var top = sc.EntityChain(first.Id).LastOrDefault();
                var name = await db.Entities.AsNoTracking().Where(e => e.Id == top).Select(e => e.Name).FirstOrDefaultAsync();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            var business = sc.SettingValue("business_name").Trim();
            if (business.Length > 0) return business;
            if (profile?.TenantId is int tid
                && await db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync()
                    is { Length: > 0 } tenant)
                return tenant;
        }
        catch { /* the name is cosmetic */ }
        return "";   // WelcomeEmail says "your organization" in the reader's language
    }

    private static TimeZoneInfo SafeZone(ScopingService sc)
    {
        try { return OrgClock.Zone(sc); } catch { return TimeZoneInfo.Utc; }
    }
}
