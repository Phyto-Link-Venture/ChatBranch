using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Security;

/// <summary>
/// Two-step sign-in (TOTP). Enrollment (a secret shown once as a QR code + manual key,
/// confirmed with a code, then 10 one-time recovery codes), verification at sign-in
/// (a code or a recovery code, ±1 step, never a step twice), the short-lived signed
/// CHALLENGE that carries a password-checked sign-in to the code step, and the
/// organization policy <c>require_2fa</c> (off | admins | everyone).
///
/// Secrets are encrypted with ASP.NET Core Data Protection — keys under
/// <c>&lt;data dir&gt;/keys</c>, so they survive restarts and travel with the data
/// volume — and are never returned after enrollment. Recovery codes are kept only as
/// salted SHA-256 hashes (each ~60 random bits, so a hash can't be brute-forced).
/// </summary>
public sealed class TwoFactorService
{
    public const string PolicyKey = "require_2fa";
    public const string PolicyOff = "off";
    public const string PolicyAdmins = "admins";
    public const string PolicyEveryone = "everyone";
    public static readonly string[] Policies = { PolicyOff, PolicyAdmins, PolicyEveryone };

    public const int RecoveryCodeCount = 10;
    /// <summary>How long a password-checked sign-in waits for its code.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long a started (unconfirmed) setup stays valid.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);

    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly AppDbContext _db;
    private readonly IDataProtector _secrets;
    private readonly ITimeLimitedDataProtector _challenges;
    private readonly TimeProvider _clock;
    private readonly SecurityOptions _options;

    public TwoFactorService(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock, SecurityOptions options)
    {
        _db = db;
        _secrets = protection.CreateProtector("ChatBranch.TwoFactor.Secret.v1");
        _challenges = protection.CreateProtector("ChatBranch.TwoFactor.Challenge.v1").ToTimeLimitedDataProtector();
        _clock = clock;
        _options = options;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    // ---- the organization policy -------------------------------------------------------

    private static readonly ConcurrentDictionary<string, (string Value, DateTime At)> PolicyCache = new();
    private static readonly ConcurrentDictionary<string, (bool On, DateTime At)> EnrolledCache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    /// <summary>Forget the cached policy / enrollment answers (after a save, an enrollment, a reset).</summary>
    public static void Invalidate()
    {
        PolicyCache.Clear();
        EnrolledCache.Clear();
    }

    private static string DbKey(AppDbContext db)
    {
        try { return db.Database.GetConnectionString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>The organization's policy (Setting <c>require_2fa</c>, org-wide); "off" when unset or unreadable.</summary>
    public static string Policy(AppDbContext db)
    {
        var key = DbKey(db);
        if (PolicyCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Value;
        string value;
        try
        {
            value = db.Settings.AsNoTracking().Where(s => s.Key == PolicyKey && s.EntityId == null)
                .Select(s => s.Value).FirstOrDefault() ?? "";
        }
        catch { value = ""; }
        value = Policies.Contains(value.Trim().ToLowerInvariant()) ? value.Trim().ToLowerInvariant() : PolicyOff;
        PolicyCache[key] = (value, DateTime.UtcNow);
        return value;
    }

    /// <summary>Does <paramref name="policy"/> require two-step sign-in for someone in <paramref name="role"/>?</summary>
    public static bool RequiredFor(string policy, string? role) => policy switch
    {
        PolicyEveryone => true,
        PolicyAdmins => role is Roles.Admin or Roles.SuperAdmin,
        _ => false,
    };

    /// <summary>Has this user confirmed two-step sign-in? (cached briefly — the session gate asks per request)</summary>
    public static bool IsEnrolled(AppDbContext db, int userId)
    {
        var key = DbKey(db) + "|" + userId;
        if (EnrolledCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.On;
        bool on;
        try { on = db.UserTwoFactors.AsNoTracking().Any(t => t.UserId == userId && t.Enabled); }
        catch { on = false; }
        EnrolledCache[key] = (on, DateTime.UtcNow);
        return on;
    }

    // ---- rows ----------------------------------------------------------------------------

    public Task<UserTwoFactor?> RowAsync(int userId) =>
        _db.UserTwoFactors.FirstOrDefaultAsync(t => t.UserId == userId);

    private string Protect(byte[] secret) => _secrets.Protect(Convert.ToBase64String(secret));

    private byte[]? Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try { return Convert.FromBase64String(_secrets.Unprotect(protectedSecret)); }
        catch { return null; } // a lost key ring: the person needs an admin reset
    }

    // ---- enrollment ----------------------------------------------------------------------

    public sealed record Setup(string ManualKey, string Uri, string? QrSvg, DateTime ExpiresAt);

    /// <summary>Start (or restart) setup: a fresh secret kept as PENDING until a code confirms it.
    /// An enrolled person may set up a new device this way — the old secret stays until then.</summary>
    public async Task<Setup> BeginAsync(AppUser user)
    {
        var secret = Totp.NewSecret();
        var row = await RowAsync(user.Id);
        var now = Now.UtcDateTime;
        if (row == null)
        {
            row = new UserTwoFactor { UserId = user.Id, CreatedAt = now };
            _db.UserTwoFactors.Add(row);
        }
        row.PendingSecret = Protect(secret);
        row.PendingAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync();
        var uri = Totp.ProvisioningUri(_options.TotpIssuer, user.Username, secret);
        return new Setup(Base32.Encode(secret), uri, ChatBranch.Data.Contacts.QrSvg.Render(uri), now + PendingLifetime);
    }

    /// <summary>Confirm setup with a code from the app: the pending secret becomes THE secret and
    /// 10 new recovery codes are returned (shown once). Null = wrong / expired code.</summary>
    public async Task<List<string>?> ConfirmAsync(AppUser user, string? code)
    {
        var row = await RowAsync(user.Id);
        if (row == null || row.PendingSecret.Length == 0) return null;
        if (row.PendingAt is DateTime at && Now.UtcDateTime - at > PendingLifetime) return null;
        var secret = Unprotect(row.PendingSecret);
        if (secret == null) return null;
        var step = Totp.Match(secret, code, Now, lastUsedStep: 0);
        if (step == null) return null;
        var now = Now.UtcDateTime;
        row.Secret = row.PendingSecret;
        row.PendingSecret = "";
        row.PendingAt = null;
        row.Enabled = true;
        row.LastStep = step.Value;
        row.ConfirmedAt = now;
        row.UpdatedAt = now;
        var codes = NewRecoveryCodes(row);
        await _db.SaveChangesAsync();
        Invalidate();
        return codes;
    }

    /// <summary>Replace the recovery codes (the old ones stop working); the caller saves.</summary>
    public static List<string> NewRecoveryCodes(UserTwoFactor row)
    {
        row.RecoverySalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var plain = Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => RandomNumberGenerator.GetString(RecoveryAlphabet, 12))
            .Select(c => $"{c[..4]}-{c[4..8]}-{c[8..]}").ToList();
        row.RecoveryCodes = plain.Select(c => HashRecovery(row.RecoverySalt, c)).ToList();
        return plain;
    }

    private static string NormalizeRecovery(string code) =>
        new string((code ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string HashRecovery(string salt, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + NormalizeRecovery(code)))).ToLowerInvariant();

    /// <summary>Turn two-step sign-in off for this user (the row goes); the caller audits.</summary>
    public async Task RemoveAsync(int userId)
    {
        await _db.UserTwoFactors.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        Invalidate();
    }

    // ---- verification ----------------------------------------------------------------------

    public enum CodeKind { Totp, Recovery }

    /// <summary>
    /// Check a code from the app or a recovery code against an ENROLLED row. A matching code
    /// moves <see cref="UserTwoFactor.LastStep"/> on (no replay); a recovery code is used up.
    /// The caller saves. Null = no match.
    /// </summary>
    public CodeKind? Verify(UserTwoFactor row, string? code)
    {
        if (!row.Enabled) return null;
        var text = (code ?? "").Trim();
        if (text.Length == 0 || text.Length > 64) return null;
        var digits = text.Where(char.IsAsciiDigit).Count();
        if (digits == Totp.Digits && text.All(c => char.IsAsciiDigit(c) || c == ' ' || c == '-'))
        {
            var secret = Unprotect(row.Secret);
            if (secret == null) return null;
            var step = Totp.Match(secret, text, Now, row.LastStep);
            if (step == null) return null;
            row.LastStep = step.Value;
            row.UpdatedAt = Now.UtcDateTime;
            return CodeKind.Totp;
        }
        if (NormalizeRecovery(text).Length != 12 || row.RecoverySalt.Length == 0) return null;
        var hash = HashRecovery(row.RecoverySalt, text);
        var match = row.RecoveryCodes.FirstOrDefault(h =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (match == null) return null;
        row.RecoveryCodes = row.RecoveryCodes.Where(h => h != match).ToList();
        row.UpdatedAt = Now.UtcDateTime;
        return CodeKind.Recovery;
    }

    // ---- the sign-in challenge ------------------------------------------------------------

    /// <summary>What the password step vouches for, carried (encrypted + signed) to the code step.</summary>
    public sealed record Challenge(int UserId, string PasswordStamp, bool DefaultPassword, string Nonce);

    private static readonly ConcurrentDictionary<string, DateTime> UsedChallenges = new();

    /// <summary>A short fingerprint of the stored password hash — a password change voids open challenges.</summary>
    public static string PasswordStamp(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];

    public string IssueChallenge(AppUser user, bool defaultPassword)
    {
        var payload = JsonSerializer.Serialize(new Challenge(user.Id, PasswordStamp(user.Password), defaultPassword,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
        return _challenges.Protect(payload, ChallengeLifetime);
    }

    /// <summary>The challenge behind a token — null when tampered, expired or already used.</summary>
    public Challenge? ReadChallenge(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return null;
        try
        {
            var json = _challenges.Unprotect(token.Trim(), out var expires);
            if (expires < Now) return null;
            var c = JsonSerializer.Deserialize<Challenge>(json);
            if (c == null || UsedChallenges.ContainsKey(c.Nonce)) return null;
            return c;
        }
        catch { return null; }
    }

    /// <summary>A challenge that signed someone in is spent.</summary>
    public static void Spend(Challenge c)
    {
        var now = DateTime.UtcNow;
        UsedChallenges[c.Nonce] = now + ChallengeLifetime;
        if (UsedChallenges.Count > 1000)
            foreach (var (k, until) in UsedChallenges)
                if (until < now) UsedChallenges.TryRemove(k, out _);
    }
}
