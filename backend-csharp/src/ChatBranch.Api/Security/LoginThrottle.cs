using System.Collections.Concurrent;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Security;

/// <summary>
/// Brute-force protection for every password and code check — sign-in, the
/// two-step code, My account's password change, turning two-step sign-in off — and
/// for API keys. Failed attempts are counted in a sliding window per USERNAME and per
/// CLIENT IP; reaching the limit locks that username (or IP) for the lockout time
/// (<c>LOGIN_MAX_FAILURES</c> 5 / <c>LOGIN_MAX_FAILURES_PER_IP</c> 30 /
/// <c>LOGIN_LOCKOUT_MINUTES</c> 15) — an IP lock spares people who signed in from that
/// address before (<see cref="LockedForAsync"/>). While locked the check is refused BEFORE the
/// password is looked at (429 + Retry-After), the same way for a username that
/// doesn't exist, so a lock never tells an attacker which usernames are real. A
/// successful sign-in clears its username's count. Bad API keys count per IP
/// (<c>API_KEY_MAX_FAILURES_PER_IP</c> 20).
///
/// In memory, per API process — the shipped deployment runs one API instance.
/// Entries expire on their own and the table is pruned, so a flood of made-up
/// usernames can't grow it without bound.
/// </summary>
public sealed class LoginThrottle
{
    private readonly SecurityOptions _o;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _calls;

    /// <summary>The buckets the table may hold before old ones are dropped.</summary>
    public const int MaxEntries = 100_000;

    public LoginThrottle(SecurityOptions options, TimeProvider clock)
    {
        _o = options;
        _clock = clock;
    }

    private sealed class Bucket
    {
        public readonly Queue<DateTime> Fails = new();
        public DateTime LockedUntil;
        public DateTime Touched;
    }

    /// <summary>A lock that is (or just became) active.</summary>
    public sealed record Lock(string Scope, string Subject, int RetryAfterSeconds, DateTime Until);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public static string UserKey(string? username) => "u:" + (username ?? "").Trim().ToLowerInvariant();
    public static string IpKey(string? ip) => "ip:" + (string.IsNullOrEmpty(ip) ? "unknown" : ip);
    public static string ApiKeyIpKey(string? ip) => "key:" + (string.IsNullOrEmpty(ip) ? "unknown" : ip);

    // A request with no client address (in-process calls, the test server) is never counted
    // per IP — Kestrel always knows the peer, so production traffic always is.

    /// <summary>The active lock on this username or this IP, if any (the IP's first).</summary>
    public Lock? Locked(string? username, string? ip)
    {
        var now = Now;
        if (!string.IsNullOrEmpty(ip) && ActiveLock(IpKey(ip), now) is int ipRetry)
            return new Lock("ip", ip, ipRetry, now.AddSeconds(ipRetry));
        if (username != null && ActiveLock(UserKey(username), now) is int userRetry)
            return new Lock("user", username, userRetry, now.AddSeconds(userRetry));
        return null;
    }

    /// <summary>How far back a successful sign-in from an address still counts as "known here".</summary>
    public static readonly TimeSpan KnownAddressWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// The lock that applies to a password / code check by a REAL user from this IP. Like
    /// <see cref="Locked"/>, except that an IP lock (someone at that address keeps failing)
    /// does not stop a person who signed in successfully from that same address in the last
    /// 30 days — an office colleague of whoever is guessing keeps working, while an
    /// attacker's own address has no such history for anyone it hasn't already broken into.
    /// The username's own lock always applies. A lookup failure keeps the lock (fails closed).
    /// </summary>
    public async Task<Lock?> LockedForAsync(AppDbContext db, int userId, string username, string? ip)
    {
        var locked = Locked(username, ip);
        if (locked is not { Scope: "ip" } || string.IsNullOrEmpty(ip)) return locked;
        if (Locked(username, null) is { } userLock) return userLock;
        try
        {
            var since = Now - KnownAddressWindow;
            var known = await db.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.UserId == userId && a.Action == "login" && a.Ip == ip && a.CreatedAt >= since);
            return known ? null : locked;
        }
        catch (Exception)
        {
            return locked;
        }
    }

    /// <summary>Is this IP locked out of API-key authentication?</summary>
    public Lock? ApiKeyLocked(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        var now = Now;
        return ActiveLock(ApiKeyIpKey(ip), now) is int retry ? new Lock("api_key", ip, retry, now.AddSeconds(retry)) : null;
    }

    /// <summary>
    /// Count one failure for the username and the IP. Returns the locks THIS failure
    /// started (empty when it didn't reach a limit) — the caller audits and alerts once.
    /// </summary>
    public List<Lock> Fail(string? username, string? ip)
    {
        var started = new List<Lock>();
        var now = Now;
        if (username != null && Count(UserKey(username), _o.MaxFailuresPerUser, now) is { } u)
            started.Add(new Lock("user", username, u, now.AddSeconds(u)));
        if (!string.IsNullOrEmpty(ip) && Count(IpKey(ip), _o.MaxFailuresPerIp, now) is { } i)
            started.Add(new Lock("ip", ip, i, now.AddSeconds(i)));
        return started;
    }

    /// <summary>One bad API key from this IP; the lock it started, if any.</summary>
    public Lock? ApiKeyFail(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        var now = Now;
        return Count(ApiKeyIpKey(ip), _o.MaxApiKeyFailuresPerIp, now) is { } r
            ? new Lock("api_key", ip, r, now.AddSeconds(r)) : null;
    }

    /// <summary>A correct password (or code): the username starts again from zero.</summary>
    public void Succeed(string? username)
    {
        if (username != null) _buckets.TryRemove(UserKey(username), out _);
    }

    /// <summary>Forget everything about a username (an admin unlocking it, a test).</summary>
    public void Reset(string key) => _buckets.TryRemove(key, out _);

    private int? ActiveLock(string key, DateTime now)
    {
        if (!_buckets.TryGetValue(key, out var b)) return null;
        lock (b)
        {
            if (b.LockedUntil <= now) return null;
            return Math.Max(1, (int)Math.Ceiling((b.LockedUntil - now).TotalSeconds));
        }
    }

    /// <summary>Add a failure; when it reaches <paramref name="limit"/> inside the window, lock
    /// the key and return the lock's length in seconds (null = no new lock).</summary>
    private int? Count(string key, int limit, DateTime now)
    {
        MaybePrune(now);
        var b = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (b)
        {
            b.Touched = now;
            if (b.LockedUntil > now) return null; // already locked — refused upstream
            var window = _o.LockoutWindow;
            while (b.Fails.Count > 0 && now - b.Fails.Peek() >= window) b.Fails.Dequeue();
            b.Fails.Enqueue(now);
            if (b.Fails.Count < Math.Max(1, limit)) return null;
            b.Fails.Clear();
            b.LockedUntil = now + window;
            return Math.Max(1, (int)Math.Ceiling(window.TotalSeconds));
        }
    }

    private void MaybePrune(DateTime now)
    {
        if (Interlocked.Increment(ref _calls) % 256 != 0 && _buckets.Count < MaxEntries) return;
        var window = _o.LockoutWindow;
        foreach (var (key, b) in _buckets)
        {
            bool stale;
            lock (b) stale = b.LockedUntil <= now && now - b.Touched >= window;
            if (stale) _buckets.TryRemove(key, out _);
        }
        // still too big (a flood of fresh usernames): drop the oldest unlocked entries
        if (_buckets.Count >= MaxEntries)
            foreach (var (key, _) in _buckets.Where(kv => kv.Value.LockedUntil <= now)
                         .OrderBy(kv => kv.Value.Touched).Take(_buckets.Count - MaxEntries / 2).ToList())
                _buckets.TryRemove(key, out _);
    }

    /// <summary>
    /// The 429 body every throttled check answers with — the dashboard translates it by
    /// <c>code</c> and shows the minutes from <c>retry_after</c>.
    /// </summary>
    public static IResult Refusal(HttpContext ctx, Lock l)
    {
        ctx.Response.Headers.RetryAfter = l.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var minutes = Math.Max(1, (l.RetryAfterSeconds + 59) / 60);
        return Results.Json(new
        {
            detail = $"Too many failed attempts. Try again in {minutes} min.",
            code = "too_many_attempts",
            retry_after = l.RetryAfterSeconds,
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Record the locks a failure started: an audit row each (<c>login.locked</c>) and
    /// ONE admin notification that collapses (RefKey <c>security:login_locked</c>) — a
    /// burst of lockouts refreshes the same unread alert instead of stacking them.
    /// Never throws.
    /// </summary>
    public static void Report(AppDbContext db, IEnumerable<Lock> locks, string what, int? tenantId = null)
    {
        foreach (var l in locks)
        {
            try
            {
                var minutes = Math.Max(1, (l.RetryAfterSeconds + 59) / 60);
                var subject = l.Scope switch
                {
                    "user" => $"username \"{AuditTrail.Cap(l.Subject, 80)}\"",
                    "api_key" => $"API-key sign-ins from {l.Subject}",
                    _ => $"sign-ins from {l.Subject}",
                };
                AuditTrail.Write(db, "login.locked", AuditTrail.Cap(l.Subject, 150),
                    $"{l.Scope} locked for {minutes} min after repeated failed {what}",
                    actor: "system", source: "system");
                NotificationService.Notify(db, NotificationCategories.Admin, "login_locked",
                    "Sign-ins locked after repeated failures",
                    $"{char.ToUpperInvariant(subject[0])}{subject[1..]} locked for {minutes} min after repeated failed {what}. "
                    + "If this wasn't a person who forgot their password, someone may be guessing — see the activity log.",
                    "/activity?action=login.locked",
                    NotificationAudience.Admins(tenantId),
                    new NotifyOptions { RefKey = "security:login_locked", CollapsedTitle = "Sign-ins locked after repeated failures" });
            }
            catch { /* an alert never breaks the sign-in answer */ }
        }
    }

    /// <summary>The tenant of a username (for the alert's audience), null when unknown.</summary>
    public static async Task<int?> TenantOfAsync(AppDbContext db, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            return await db.Profiles.AsNoTracking().Where(p => p.User!.Username == username)
                .Select(p => p.TenantId).FirstOrDefaultAsync();
        }
        catch { return null; }
    }
}
