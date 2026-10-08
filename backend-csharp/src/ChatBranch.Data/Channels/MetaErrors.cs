using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>Who a Meta (Graph / WhatsApp Cloud / Messenger) error is about.</summary>
public enum MetaErrorScope
{
    /// <summary>One recipient or one message (closed window, bad number, opted out of
    /// marketing, media the user's phone refused…). NEVER touches account health.</summary>
    Recipient,
    /// <summary>The template itself (missing, paused, wrong parameter count). Not health.</summary>
    Template,
    /// <summary>The sending account (token revoked, number deregistered, business locked).</summary>
    Account,
    /// <summary>Throughput limits on the account — degraded until a send succeeds again.</summary>
    RateLimit,
    /// <summary>Meta's own hiccup (service unavailable, unknown error). Not health.</summary>
    Transient,
    /// <summary>Anything this table does not know. Not health — an unknown code must
    /// never take a working number out of rotation.</summary>
    Unknown,
}

/// <param name="Health">The health status the account moves to, or null = leave it.</param>
public readonly record struct MetaErrorClass(MetaErrorScope Scope, string? Health);

/// <summary>
/// The Meta error-code classification (port of the <c>whatsapp/health.py</c>
/// docstring table). The rule the whole health model rests on: a PER-RECIPIENT
/// error (131047 window closed, 131026 undeliverable, 131050 stopped marketing,
/// Messenger 551 / the 2018278 window subcode…) NEVER damages an account's
/// health — only errors about the account itself (190 token, 131031 locked,
/// 368 policy block, 133010 not registered…) and throughput limits do, and only
/// through <see cref="AccountHealth.SetStatus"/>.
/// </summary>
public static class MetaErrors
{
    /// <summary>The re-engagement error: the 24h customer-service window is closed.</summary>
    public const int WindowClosed = 131047;

    private static readonly HashSet<int> RecipientCodes = new()
    {
        131021, // recipient cannot be the sender
        131026, // message undeliverable (no WhatsApp, old app, not accepted terms)
        131047, // re-engagement message (24h window closed)
        131049, // Meta chose not to deliver (ecosystem engagement)
        131050, // user stopped marketing messages
        131051, // unsupported message type
        131052, // media download error (on the user's side)
        131053, // media upload error
        131056, // pair rate limit — too many messages to THIS recipient
        130472, // user's number is part of an experiment
        130497, // business can't message users in this country
        131008, // required parameter missing (payload for this message)
        131009, // parameter value invalid
        135000, // generic user error
        551,    // Messenger: this person isn't available right now
    };

    private static readonly HashSet<int> TemplateCodes = new()
    {
        132000, 132001, 132005, 132007, 132012, 132015, 132016, 132068, 132069,
    };

    private static readonly Dictionary<int, string> AccountCodes = new()
    {
        [0] = Health.Disconnected,       // AuthException
        [190] = Health.Disconnected,     // access token expired / invalid
        [10] = Health.Disconnected,      // permission denied
        [33] = Health.Disconnected,      // the phone number id / object does not exist
        [131005] = Health.Disconnected,  // access denied
        [131045] = Health.Disconnected,  // incorrect certificate (number not registered)
        [133000] = Health.Disconnected,  // incomplete deregistration
        [133006] = Health.Disconnected,  // phone number needs re-verification
        [133010] = Health.Disconnected,  // phone number not registered
        [131031] = Health.Banned,        // business account locked
        [368] = Health.Banned,           // temporarily blocked for policy violations
        [131037] = Health.Degraded,      // display name not approved
        [131042] = Health.Degraded,      // business eligibility / payment issue
        [133005] = Health.Degraded,      // two-step PIN mismatch
        [133008] = Health.Degraded,
        [133009] = Health.Degraded,
        [133015] = Health.Degraded,
        [133016] = Health.Degraded,
    };

    private static readonly HashSet<int> RateLimitCodes = new() { 4, 17, 32, 613, 80007, 130429, 131048 };

    private static readonly HashSet<int> TransientCodes = new() { 1, 2, 131000, 131016, 131057, 133004 };

    /// <summary>Messenger subcodes that make an otherwise account-level code per-recipient.</summary>
    private static readonly HashSet<int> RecipientSubcodes = new()
    {
        2018278, // message sent outside the allowed window
        2018001, // no matching user found
        2018065, // user blocked messages from the page
        2018108, // user can't receive messages right now
        2018109,
        1545041, // user is not available
    };

    public static MetaErrorClass Classify(int? code, int? subcode = null)
    {
        if (subcode is int sub && RecipientSubcodes.Contains(sub))
            return new(MetaErrorScope.Recipient, null);
        if (code is not int c) return new(MetaErrorScope.Unknown, null);
        if (RecipientCodes.Contains(c)) return new(MetaErrorScope.Recipient, null);
        if (TemplateCodes.Contains(c)) return new(MetaErrorScope.Template, null);
        if (AccountCodes.TryGetValue(c, out var health)) return new(MetaErrorScope.Account, health);
        if (c is >= 200 and <= 299) return new(MetaErrorScope.Account, Health.Disconnected); // API permission
        if (RateLimitCodes.Contains(c)) return new(MetaErrorScope.RateLimit, Health.Degraded);
        if (TransientCodes.Contains(c)) return new(MetaErrorScope.Transient, null);
        return new(MetaErrorScope.Unknown, null);
    }

    /// <summary>
    /// Move the account's health for an account-level error (through the ONE writer,
    /// <see cref="AccountHealth.SetStatus"/>). Per-recipient/template/transient/unknown
    /// codes leave it alone. The caller saves. Returns true when the status changed.
    /// </summary>
    public static bool ApplyToAccount(AppDbContext db, ChannelAccount? account, int? code, int? subcode,
        string detail, string actor = "system")
    {
        if (account == null || account.Id == 0) return false; // env pseudo-account: no row to track
        var cls = Classify(code, subcode);
        if (cls.Health is not string next) return false;
        account.FailStreak += 1;
        var reason = code is int c ? $"Meta error {c}" : "Meta error";
        if (!string.IsNullOrWhiteSpace(detail)) reason += $": {Cap(detail, 160)}";
        return AccountHealth.SetStatus(db, account, next, reason, actor);
    }

    /// <summary>A successful provider round-trip proves the account works — heal it.</summary>
    public static void MarkHealthy(AppDbContext db, ChannelAccount? account, string why, string actor = "system")
    {
        if (account == null || account.Id == 0) return;
        // same status → only last_ok_at / fail_streak move (no audit row)
        AccountHealth.SetStatus(db, account, Health.Healthy,
            account.HealthStatus == Health.Healthy ? null : why, actor);
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
