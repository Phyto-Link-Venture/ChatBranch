using System.Collections.Concurrent;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Prepaid AI credit ENFORCEMENT (phase 2): does a company's (or a login's)
/// balance stop the AI right now?
///
/// <para><b>Who is affected.</b> Only a scope that has an
/// <see cref="AiCreditAccount"/> row with <c>enabled = true</c>. A company with no
/// account row, or with credit switched off, is never blocked, never debited and
/// never warned — on deploy that is every company, because an account row only
/// exists once a billing manager granted or adjusted credit.</para>
///
/// <para><b>The rule.</b> Blocked when <c>balance &lt;= -overdraft_limit</c>
/// (<see cref="AiCreditStatus.Exhausted"/>; the default overdraft is 0 = stop at
/// zero). Balances are signed and never clamped: consumption is known only after
/// the model call and the sweep (<see cref="AiCreditSweep"/>) lags, so the last
/// replies before exhaustion overshoot and the overshoot carries into the next
/// top-up.</para>
///
/// <para><b>Where it is read.</b> Through the ONE eligibility funnel —
/// <see cref="AiControls.CreditBlock"/> → <see cref="AiPolicyInputs.CreditBlock"/>
/// → <see cref="AiEligibility"/> (rule <see cref="AiRules.Credit"/>, forced) — and,
/// for the interactive features, <c>QuotaGuard</c>. The handful of AI paths that
/// already call <c>AiQuota.EntityBlock</c> directly (voice, queued calls, AI tasks,
/// the operator's suggest/review buttons) ask <see cref="EntityReason"/> right
/// beside it.</para>
///
/// <para><b>Fail-open, loudly.</b> A failure reading credit lets the AI answer —
/// a billing bug must never take customer replies down — but every failure is
/// counted, logged and raises an admin notification (one collapsing row,
/// refreshed each time), so free AI during an outage cannot go unnoticed. When
/// even the notification cannot be written (the database is down) it is kept
/// pending and the next successful sweep delivers it.</para>
///
/// <para>The snapshot is cached ≤<see cref="CacheFor"/> per database; any ledger
/// write (<see cref="AiCredit.ApplyAsync"/>) invalidates it, so a grant or top-up
/// unblocks on the next message.</para>
/// </summary>
public static class AiCreditGuard
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private sealed class Cached
    {
        public DateTime BuiltAt { get; init; }
        /// <summary>Null when no account is enabled — nothing to enforce, nothing else read.</summary>
        public AiCredit.Snapshot? Snapshot { get; init; }
    }

    private static readonly ConcurrentDictionary<string, Cached> Cache = new();
    private static readonly ConcurrentDictionary<string, Health> HealthByDb = new();
    private static readonly ConcurrentDictionary<string, Exception> Faults = new();

    /// <summary>What went wrong around credit on one database (in-process counters).</summary>
    public sealed class Health
    {
        private long _failOpen;
        private long _failOpenNotified;
        private long _metering;
        private long _meteringNotified;
        public long FailOpen => Interlocked.Read(ref _failOpen);
        public long MeteringFailures => Interlocked.Read(ref _metering);
        public DateTime? LastFailOpenAt { get; private set; }
        public string LastFailOpenError { get; private set; } = "";
        public DateTime? LastMeteringFailureAt { get; private set; }
        public string LastMeteringError { get; private set; } = "";
        public string LastMeteringFeature { get; private set; } = "";
        /// <summary>A fail-open happened whose notification could not be written yet.</summary>
        public bool FailOpenUnnotified => Interlocked.Read(ref _failOpenNotified) < FailOpen;
        public bool MeteringUnnotified => Interlocked.Read(ref _meteringNotified) < MeteringFailures;

        internal long RecordFailOpen(Exception ex)
        {
            LastFailOpenAt = DateTime.UtcNow;
            LastFailOpenError = Short(ex);
            return Interlocked.Increment(ref _failOpen);
        }

        internal void FailOpenNotified(long upTo) => Raise(ref _failOpenNotified, upTo);

        internal long RecordMetering(string feature, Exception ex)
        {
            LastMeteringFailureAt = DateTime.UtcNow;
            LastMeteringError = Short(ex);
            LastMeteringFeature = feature;
            return Interlocked.Increment(ref _metering);
        }

        internal void MeteringNotified(long upTo) => Raise(ref _meteringNotified, upTo);

        private long _sweepFailures;
        private long _unpriced;
        /// <summary>Sweep passes that threw (a stuck batch stays pinned and nobody is debited).</summary>
        public long SweepFailures => Interlocked.Read(ref _sweepFailures);
        public DateTime? LastSweepFailureAt { get; private set; }
        public string LastSweepError { get; private set; } = "";
        /// <summary>Usage rows of a company on credit that priced at 0 because neither the provider
        /// nor the stored price table knew the model — debited as nothing.</summary>
        public long UnpricedRows => Interlocked.Read(ref _unpriced);

        internal long RecordSweepFailure(Exception ex)
        {
            LastSweepFailureAt = DateTime.UtcNow;
            LastSweepError = Short(ex);
            return Interlocked.Increment(ref _sweepFailures);
        }

        internal void RecordUnpriced(int n) => Interlocked.Add(ref _unpriced, n);

        private static void Raise(ref long field, long upTo)
        {
            long seen;
            do
            {
                seen = Interlocked.Read(ref field);
                if (seen >= upTo) return;
            } while (Interlocked.CompareExchange(ref field, upTo, seen) != seen);
        }

        private static string Short(Exception ex)
        {
            var m = $"{ex.GetType().Name}: {ex.Message}";
            return m.Length > 300 ? m[..300] : m;
        }
    }

    /// <summary>Forget every cached snapshot (a ledger write, an account change; tests).</summary>
    public static void Invalidate() => Cache.Clear();

    /// <summary>The counters for this database.</summary>
    public static Health HealthOf(AppDbContext db) => HealthByDb.GetOrAdd(Key(db), _ => new Health());

    /// <summary>TEST SEAM: make every credit read on this database throw <paramref name="fault"/> (null clears).</summary>
    public static void SetFaultForTests(AppDbContext db, Exception? fault)
    {
        var key = Key(db);
        if (fault == null) Faults.TryRemove(key, out _);
        else Faults[key] = fault;
        Invalidate();
    }

    // ---- reading ---------------------------------------------------------------------------

    private static AiCredit.Snapshot? Current(AppDbContext db, ScopingService sc)
    {
        var key = Key(db);
        if (Faults.TryGetValue(key, out var fault)) throw fault;
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(key, out var hit) && now >= hit.BuiltAt && now - hit.BuiltAt < CacheFor)
            return hit.Snapshot;
        // the deploy-safety fast path: no enabled account anywhere = nothing to enforce
        var any = db.AiCreditAccounts.AsNoTracking().Any(a => a.Enabled);
        var snap = any ? AiCredit.Load(db, sc) : null;
        Cache[key] = new Cached { BuiltAt = now, Snapshot = snap };
        return snap;
    }

    /// <summary>
    /// The exhausted COMPANY balance a conversation of this entity draws on, or
    /// null: no entity, no company, no account, credit switched off, money left —
    /// or the credit could not be read (fails OPEN, with an admin notification).
    /// </summary>
    public static AiCreditStatus? EntityBlock(AppDbContext db, ScopingService sc, int? entityId)
    {
        if (entityId == null) return null;
        try
        {
            var snap = Current(db, sc);
            return snap?.ForEntity(entityId) is { Exhausted: true } s ? s : null;
        }
        catch (Exception ex)
        {
            FailOpen(db, ex, $"entity #{entityId}");
            return null;
        }
    }

    /// <summary>The exhausted per-login balance of this user, or null (no account / off / money left / unreadable).</summary>
    public static AiCreditStatus? UserBlock(AppDbContext db, ScopingService sc, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            var snap = Current(db, sc);
            if (snap == null || !snap.ByUser.ContainsKey(AiCredit.NormalizeUser(username))) return null;
            return snap.User(username) is { Exhausted: true } s ? s : null;
        }
        catch (Exception ex)
        {
            FailOpen(db, ex, $"user {username}");
            return null;
        }
    }

    /// <summary>The <c>ai_reason</c> of a conversation whose company ran out of credit.</summary>
    public static string Reason(AiCreditStatus s) =>
        $"Off: AI credit used up for {s.Name} (balance {AiCredit.Money(s.Currency, s.Balance)}) — top up to resume";

    /// <summary><see cref="EntityBlock"/> as the sentence a direct caller shows, or null.</summary>
    public static string? EntityReason(AppDbContext db, ScopingService sc, int? entityId) =>
        EntityBlock(db, sc, entityId) is { } s ? Reason(s) : null;

    /// <summary>The refusal an interactive AI feature answers a login whose own credit is used up with.</summary>
    public static string UserRefusal(AiCreditStatus s) =>
        $"Your AI credit is used up (balance {AiCredit.Money(s.Currency, s.Balance)}). "
        + "Ask a billing manager to add credit.";

    /// <summary>The refusal when every company the caller works for is out of credit.</summary>
    public static string CompanyRefusal(AiCreditStatus s) =>
        $"{s.Name} is out of AI credit (balance {AiCredit.Money(s.Currency, s.Balance)}). "
        + "Top up on the billing page to resume.";

    // ---- the loud part of fail-open ----------------------------------------------------------

    /// <summary>
    /// A credit read failed and the AI was allowed to answer: count it, log it and
    /// tell the platform admins NOW (one collapsing notification, refreshed on every
    /// failure). Written on a FRESH context so nothing the caller has pending is
    /// flushed; if that fails too the alert stays pending for the next sweep.
    /// Never throws.
    /// </summary>
    private static void FailOpen(AppDbContext db, Exception ex, string what)
    {
        try
        {
            var health = HealthOf(db);
            var n = health.RecordFailOpen(ex);
            Console.Error.WriteLine($"[ai-credit] FAIL-OPEN #{n} reading credit for {what}: {ex.GetType().Name}: {ex.Message}");
            if (NotifyFailOpen(db, health)) health.FailOpenNotified(n);
        }
        catch { /* fail-open must never throw into a reply path */ }
    }

    /// <summary>Raise (or refresh) the fail-open alert; true when it was written.</summary>
    internal static bool NotifyFailOpen(AppDbContext db, Health health)
    {
        try
        {
            using var fresh = Fresh(db);
            var body = $"Prepaid AI credit could not be read {health.FailOpen} time(s) since the server started "
                       + $"(last: {health.LastFailOpenAt:yyyy-MM-dd HH:mm} UTC — {health.LastFailOpenError}). "
                       + "The AI kept answering without checking balances, so this AI use may not be stopped at zero. "
                       + "Check the database and the credit ledger.";
            return NotificationService.Notify(fresh, NotificationCategories.Admin, "system",
                "AI credit check failed — AI kept answering", body, "/billing",
                NotificationAudience.Admins(), new NotifyOptions { RefKey = "ai-credit:fail-open" }) > 0;   // 0 = not delivered (also: down)
        }
        catch { return false; }
    }

    /// <summary>
    /// A usage row (the billable record of one model call) could not be saved and
    /// the reply went on regardless: count it so the sweep can raise an alert —
    /// that usage is neither billed nor debited. Call it from the swallowing
    /// <c>catch</c>; never throws, never touches the failed context's state.
    /// </summary>
    public static void MeteringFailed(AppDbContext db, string feature, Exception ex)
    {
        try
        {
            var n = HealthOf(db).RecordMetering(feature, ex);
            Console.Error.WriteLine($"[ai-credit] usage row NOT recorded (#{n}, feature {feature}): {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
    }

    /// <summary>Raise (or refresh) the swallowed-metering alert; true when it was written.</summary>
    internal static bool NotifyMetering(AppDbContext db, Health health)
    {
        try
        {
            var body = $"{health.MeteringFailures} AI usage record(s) failed to save since the server started "
                       + $"(last: {health.LastMeteringFailureAt:yyyy-MM-dd HH:mm} UTC, feature {health.LastMeteringFeature} — "
                       + $"{health.LastMeteringError}). The AI replies went out, but that usage is missing from the AI bill "
                       + "and was not debited from any credit balance.";
            return NotificationService.Notify(db, NotificationCategories.Admin, "system",
                "AI usage was not recorded", body, "/billing",
                NotificationAudience.Admins(), new NotifyOptions { RefKey = "ai-credit:metering-failed" }) > 0;
        }
        catch { return false; }
    }

    // ---- plumbing ------------------------------------------------------------------------------

    /// <summary>A NEW context on the same database (the <c>RequestAuditMiddleware</c> pattern).</summary>
    internal static AppDbContext Fresh(AppDbContext db)
    {
        var options = db.GetService<IDbContextOptions>() as DbContextOptions<AppDbContext>
                      ?? throw new InvalidOperationException("No AppDbContext options to open a fresh context with.");
        return new AppDbContext(options);
    }

    internal static string Key(AppDbContext db)
    {
        try { return db.Database.GetConnectionString() ?? "default"; }
        catch { return "default"; }
    }
}
